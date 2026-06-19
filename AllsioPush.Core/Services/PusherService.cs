using System.Net.Http.Headers;
using System.Security.Cryptography;
using AllsioPush.Config;
using AllsioPush.Models;
using Newtonsoft.Json.Linq;
using PusherClient;

namespace AllsioPush.Services;

public class PusherService : IDisposable
{
    private readonly AuthSession _session;
    private readonly AppSettings _settings;
    private readonly HttpClient _authHttp;
    private Pusher? _pusher;
    private System.Threading.Timer? _keepaliveTimer;
    private CancellationTokenSource? _reconnectCts;
    private int _reconnectAttempt = 0;
    private bool _intentionalDisconnect = false;
    private bool _disposed = false;

    private readonly object _channelsLock = new();
    private readonly List<string> _subscribedChannels = new();

    public event Action<PushNotification>? OnNotificationReceived;
    // (notificationId, acknowledgedBy, conversationId?, actionType?)
    // conversationId/actionType are populated only for SMS conversation acks.
    public event Action<string, string, string?, string?>? OnAcknowledgementReceived;
    public event Action<bool>? OnConnectionStateChanged;
    public event Action<IReadOnlyList<string>>? OnChannelsChanged;
    public event Action<List<PushGroup>>? OnChannelsUpdated;
    public event Action<SupervisedTransferRequest>? OnSupervisedTransfer;
    public event Action<string>? OnSupervisedTransferCancelled;

    public IReadOnlyList<string> SubscribedChannels
    {
        get { lock (_channelsLock) { return _subscribedChannels.ToArray(); } }
    }

    public PusherService(AuthSession session, AppSettings settings)
    {
        _session = session;
        _settings = settings;
        _authHttp = AuthService.CreatePinnedHttpClient(_settings.Environment == ServerEnvironment.Development);
    }

    public async Task ConnectAsync()
    {
        if (_disposed) return;

        DebugLog.Write("Pusher", $"ConnectAsync — user={_session.DisplayName} cluster={_session.PusherCluster} key={_session.PusherAppKey}");

        _intentionalDisconnect = false;
        _reconnectCts?.Cancel();
        _reconnectCts = new CancellationTokenSource();

        try
        {
            await EstablishConnection().ConfigureAwait(false);
            _reconnectAttempt = 0;
            StartKeepalive();
        }
        catch (Exception ex)
        {
            DebugLog.Write("Pusher", $"ConnectAsync failed: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[Pusher] Connect failed: {ex.Message}");
            ScheduleReconnect();
        }
    }

    public async Task DisconnectAsync()
    {
        _intentionalDisconnect = true;
        _reconnectCts?.Cancel();
        StopKeepalive();

        if (_pusher != null)
        {
            try
            {
                await _pusher.DisconnectAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Pusher] Disconnect error: {ex.Message}");
            }
        }
    }

    private async Task EstablishConnection()
    {
        DebugLog.Write("Pusher", $"EstablishConnection — personal={_session.PersonalChannel} groups=[{string.Join(", ", _session.PushGroups.Select(g => g.PusherChannel))}]");
        // Reset the confirmed-channel list so a (re)connect rebuilds it from scratch.
        lock (_channelsLock) { _subscribedChannels.Clear(); }
        OnChannelsChanged?.Invoke(SubscribedChannels);

        var authorizer = new SignedPusherAuthorizer(
            authEndpoint: $"{_settings.ApiBase}/api/pusher/auth",
            getToken: () => _session.Token,
            pinnedHttpClient: _authHttp);

        var options = new PusherOptions
        {
            Cluster = string.IsNullOrWhiteSpace(_session.PusherCluster) ? "mt1" : _session.PusherCluster,
            Encrypted = true,
            Authorizer = authorizer,
        };

        var pusher = new Pusher(_session.PusherAppKey, options);

        pusher.ConnectionStateChanged += HandleConnectionStateChanged;
        pusher.Error += HandleError;

        _pusher = pusher;

        await pusher.ConnectAsync().ConfigureAwait(false);
        await SubscribeAll(pusher).ConfigureAwait(false);
    }

    private async Task SubscribeAll(Pusher pusher)
    {
        if (!string.IsNullOrWhiteSpace(_session.PersonalChannel))
        {
            await SubscribeChannel(pusher, _session.PersonalChannel, isGroup: false).ConfigureAwait(false);
        }

        foreach (var group in _session.PushGroups)
        {
            if (string.IsNullOrWhiteSpace(group.PusherChannel)) continue;
            await SubscribeToGroupChannel(group).ConfigureAwait(false);
        }
    }

    private async Task SubscribeChannel(Pusher pusher, string channelName, bool isGroup)
    {
        DebugLog.Write("Pusher", $"Subscribing to {channelName} (isGroup={isGroup})");

        // Guard against concurrent subscribe calls for the same channel (e.g. two rapid
        // channels_updated events during a rename). Add optimistically before SubscribeAsync
        // so any concurrent call sees the channel as already tracked and returns early —
        // preventing duplicate Bind calls that would fire every notification handler twice.
        bool added;
        lock (_channelsLock)
        {
            added = !_subscribedChannels.Contains(channelName);
            if (added) _subscribedChannels.Add(channelName);
        }
        if (!added)
        {
            DebugLog.Write("Pusher", $"Already subscribed (or subscribe in-flight): {channelName}");
            return;
        }

        try
        {
            var channel = await pusher.SubscribeAsync(channelName).ConfigureAwait(false);

            DebugLog.Write("Pusher", $"Subscribed OK: {channelName}");
            OnChannelsChanged?.Invoke(SubscribedChannels);

            var friendlyName = channelName == _session.PersonalChannel
                ? (!string.IsNullOrWhiteSpace(_session.DisplayName) ? _session.DisplayName : "Personal")
                : (_session.PushGroups.FirstOrDefault(g => g.PusherChannel == channelName)?.Name ?? channelName);

            channel.Bind("notification", (PusherEvent ev) =>
            {
                DebugLog.Write("Event:notification", $"channel={channelName} data={ev.Data}");
                try
                {
                    string? jsonToParse = ev.Data;
                    if (!string.IsNullOrWhiteSpace(ev.Data))
                    {
                        var rawObj = JObject.Parse(ev.Data);
                        if (rawObj["encrypted"]?.Value<bool>() == true)
                        {
                            jsonToParse = DecryptPayload(rawObj);
                            if (jsonToParse == null)
                            {
                                DebugLog.Write("Pusher", "Dropping encrypted notification — decryption failed");
                                System.Diagnostics.Debug.WriteLine("[Pusher] Dropping encrypted notification — decryption failed");
                                return;
                            }
                        }
                    }
                    var notification = ParseNotification(jsonToParse, friendlyName);
                    if (notification != null)
                        OnNotificationReceived?.Invoke(notification);
                }
                catch (Exception ex)
                {
                    DebugLog.Write("Pusher", $"notification parse failed: {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"[Pusher] notification parse failed: {ex.Message}");
                }
            });

            if (isGroup)
            {
                channel.Bind("notification_acknowledged", (PusherEvent ev) =>
                {
                    DebugLog.Write("Event:notification_acknowledged", $"channel={channelName} data={ev.Data}");
                    try
                    {
                        var obj = JObject.Parse(ev.Data ?? "{}");
                        var id = (string?)obj["notificationId"] ?? (string?)obj["notification_id"] ?? "";
                        var by = (string?)obj["acknowledgedBy"] ?? (string?)obj["acknowledged_by"] ?? "";
                        var convId = (string?)obj["conversationId"] ?? (string?)obj["conversation_id"];
                        var actionType = (string?)obj["actionType"] ?? (string?)obj["action_type"];
                        OnAcknowledgementReceived?.Invoke(id, by, convId, actionType);
                    }
                    catch (Exception ex)
                    {
                        DebugLog.Write("Pusher", $"ack parse failed: {ex.Message}");
                        System.Diagnostics.Debug.WriteLine($"[Pusher] ack parse failed: {ex.Message}");
                    }
                });
            }
            else
            {
                channel.Bind("channels_updated", (PusherEvent ev) =>
                {
                    DebugLog.Write("Event:channels_updated", $"channel={channelName} data={ev.Data}");
                    try
                    {
                        var payload = Newtonsoft.Json.JsonConvert
                            .DeserializeObject<ChannelsUpdatedPayload>(ev.Data ?? "{}");
                        if (payload?.PushGroups == null)
                        {
                            DebugLog.Write("Event:channels_updated", "WARNING — PushGroups was null after deserialize (check JSON field name)");
                            return;
                        }
                        DebugLog.Write("Event:channels_updated", $"Parsed {payload.PushGroups.Count} groups: [{string.Join(", ", payload.PushGroups.Select(g => g.PusherChannel))}]");
                        HandleChannelsUpdated(payload.PushGroups);
                    }
                    catch (Exception ex)
                    {
                        DebugLog.Write("Pusher", $"channels_updated parse error: {ex.Message}");
                        System.Diagnostics.Debug.WriteLine(
                            $"[Pusher] channels_updated parse error: {ex.Message}");
                    }
                });

                channel.Bind("supervised_transfer", HandleSupervisedTransferEvent);
                channel.Bind("supervised_transfer_cancelled", HandleSupervisedTransferCancelledEvent);
            }
        }
        catch (Exception ex)
        {
            // Remove from tracking so the channel can be retried on reconnect.
            lock (_channelsLock) { _subscribedChannels.Remove(channelName); }
            DebugLog.Write("Pusher", $"Subscribe '{channelName}' FAILED: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[Pusher] subscribe '{channelName}' failed: {ex.Message}");
        }
    }

    private async Task SubscribeToGroupChannel(PushGroup group)
    {
        if (_pusher == null) return;
        await SubscribeChannel(_pusher, group.PusherChannel, isGroup: true).ConfigureAwait(false);
    }

    private void HandleChannelsUpdated(List<PushGroup> newGroups)
    {
        HashSet<string> currentChannels;
        lock (_channelsLock) { currentChannels = _subscribedChannels.ToHashSet(); }

        DebugLog.Write("Pusher", $"HandleChannelsUpdated — current=[{string.Join(", ", currentChannels)}] new=[{string.Join(", ", newGroups.Select(g => g.PusherChannel))}]");

        var newChannelNames = newGroups
            .Select(g => g.PusherChannel)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .ToHashSet();

        // Subscribe to channels that are new
        foreach (var group in newGroups)
        {
            if (string.IsNullOrWhiteSpace(group.PusherChannel)) continue;
            if (!currentChannels.Contains(group.PusherChannel))
            {
                DebugLog.Write("Pusher", $"Subscribing to new group: {group.PusherChannel} ({group.Name})");
                System.Diagnostics.Debug.WriteLine(
                    $"[Pusher] Subscribing to new group: {group.PusherChannel}");
                _ = SubscribeToGroupChannel(group);
            }
        }

        // Unsubscribe from channels that were removed (never touch the personal channel)
        foreach (var channel in currentChannels)
        {
            if (channel == _session.PersonalChannel) continue;
            if (!newChannelNames.Contains(channel))
            {
                DebugLog.Write("Pusher", $"Unsubscribing from removed group: {channel}");
                System.Diagnostics.Debug.WriteLine(
                    $"[Pusher] Unsubscribing from removed group: {channel}");
                if (_pusher != null) _ = _pusher.UnsubscribeAsync(channel);
                lock (_channelsLock) { _subscribedChannels.Remove(channel); }
            }
        }

        _session.PushGroups = newGroups;
        OnChannelsChanged?.Invoke(SubscribedChannels);
        OnChannelsUpdated?.Invoke(newGroups);
    }

    private sealed class ChannelsUpdatedPayload
    {
        [Newtonsoft.Json.JsonProperty("pushGroups")]
        public List<PushGroup>? PushGroups { get; set; }
    }

    // Decrypts an AES-256-GCM encrypted Pusher payload.
    // Payload shape: { encrypted:true, iv:"<b64>", tag:"<b64>", data:"<b64>", v:1 }
    // Returns the plaintext JSON string, or null if decryption fails.
    private string? DecryptPayload(JObject raw)
    {
        try
        {
            var iv = Convert.FromBase64String(raw["iv"]!.Value<string>()!);
            var tag = Convert.FromBase64String(raw["tag"]!.Value<string>()!);
            var data = Convert.FromBase64String(raw["data"]!.Value<string>()!);

            if (_session.EncryptionKey == null) return null;
            var keyBytes = Convert.FromHexString(_session.EncryptionKey);

            using var aes = new AesGcm(keyBytes, 16);
            var plaintext = new byte[data.Length];
            aes.Decrypt(iv, data, tag, plaintext);
            return System.Text.Encoding.UTF8.GetString(plaintext);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Pusher] Decryption failed: {ex.Message}");
            return null;
        }
    }

    private static PushNotification? ParseNotification(string? data, string channelName)
    {
        if (string.IsNullOrWhiteSpace(data)) return null;
        var obj = JObject.Parse(data);

        string? Get(params string[] keys)
        {
            foreach (var k in keys)
            {
                var v = obj[k];
                if (v != null && v.Type != JTokenType.Null) return (string?)v;
            }
            return null;
        }

        int? GetInt(params string[] keys)
        {
            foreach (var k in keys)
            {
                var v = obj[k];
                if (v != null && v.Type != JTokenType.Null)
                {
                    if (v.Type == JTokenType.Integer) return (int)v;
                    if (int.TryParse((string?)v, out var i)) return i;
                }
            }
            return null;
        }

        var n = new PushNotification
        {
            NotificationId = Get("notificationId", "notification_id", "id"),
            TemplateType = Get("templateType", "template_type") ?? "plain_text",
            DisplayMode = Get("displayMode", "display_mode") ?? "slideout",
            Title = Get("title") ?? "Notification",
            Content = Get("content", "body", "message") ?? "",
            Url = Get("url"),
            CustomHtml = Get("customHtml", "custom_html", "html"),
            HeaderColor = Get("headerColor", "header_color"),
            Sound = Get("sound"),
            Ttl = GetInt("ttl"),
            PopupWidth = GetInt("popupWidth", "popup_width", "width"),
            PopupHeight = GetInt("popupHeight", "popup_height", "height"),
            ChannelName = channelName,
            GroupName = Get("groupName", "group_name"),
            CallerName = Get("callerName", "caller_name"),
            CallerPhone = Get("callerPhone", "caller_phone"),
            Reason = Get("reason"),
            AppointmentDate = Get("appointmentDate", "appointment_date"),
            Service = Get("service"),
            Stylist = Get("stylist"),
            SenderName = Get("senderName", "sender_name"),
            SenderPhone = Get("senderPhone", "sender_phone"),
            ConversationId = Get("conversationId", "conversation_id"),
            AssignmentType = Get("assignmentType", "assignment_type"),
        };

        // Dynamic label/value rows — try "rows" then "fields"
        var rowsArr = (obj["rows"] ?? obj["fields"]) as JArray;
        if (rowsArr != null)
        {
            foreach (var r in rowsArr)
            {
                var label = (string?)(r["label"] ?? r["key"] ?? r["name"]) ?? "";
                var value = (string?)(r["value"] ?? r["text"]) ?? "";
                if (!string.IsNullOrWhiteSpace(label) || !string.IsNullOrWhiteSpace(value))
                    n.Rows.Add(new NotificationRow { Label = label, Value = value });
            }
        }

        // templateFields: [{ key, label, required, description }] with values as flat payload fields
        var templateFields = obj["templateFields"];
        if (templateFields != null &&
            templateFields.Type == JTokenType.Array &&
            n.Rows.Count == 0)
        {
            foreach (var field in templateFields)
            {
                var key = field["key"]?.ToString();
                var label = field["label"]?.ToString();
                if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(label))
                    continue;

                string? value = null;
                if (n.Extras.TryGetValue(key, out var extrasVal))
                    value = extrasVal;
                else
                {
                    var token = obj[key];
                    if (token != null &&
                        token.Type != JTokenType.Null &&
                        token.Type != JTokenType.Object &&
                        token.Type != JTokenType.Array)
                        value = token.ToString();
                }

                if (!string.IsNullOrWhiteSpace(value))
                    n.Rows.Add(new NotificationRow { Label = label, Value = value });
            }
        }

        if (obj["buttons"] is JArray buttonsArr)
        {
            foreach (var b in buttonsArr)
            {
                n.Buttons.Add(new NotificationButton
                {
                    Label      = (string?)b["label"] ?? "",
                    Action     = (string?)b["action"] ?? "",
                    Style      = (string?)b["style"] ?? "default",
                    WebhookUrl = (string?)b["webhookUrl"] ?? (string?)b["webhook_url"],
                    Url        = (string?)b["url"],
                });
            }
        }

        // Capture every scalar field so downstream consumers (e.g. the screen
        // pop's CRM extraction) can read fields not mapped to a property.
        foreach (var prop in obj.Properties())
        {
            var t = prop.Value.Type;
            if (t == JTokenType.Object || t == JTokenType.Array || t == JTokenType.Null)
                continue;
            n.Extras[prop.Name] = (string?)prop.Value;
        }

        if ((obj["priorCalls"] ?? obj["prior_calls"]) is JArray priorArr)
        {
            foreach (var c in priorArr)
            {
                DateTime.TryParse((string?)(c["callDate"] ?? c["call_date"]), out var when);
                n.PriorCalls.Add(new PriorCall
                {
                    CallDate = when,
                    Duration = (string?)c["duration"],
                    Reason = (string?)c["reason"],
                    Outcome = (string?)c["outcome"],
                    AgentName = (string?)(c["agentName"] ?? c["agent_name"]),
                });
            }
        }

        return n;
    }

    private void HandleConnectionStateChanged(object sender, ConnectionState state)
    {
        DebugLog.Write("Pusher", $"State → {state}");
        System.Diagnostics.Debug.WriteLine($"[Pusher] state -> {state}");

        if (state == ConnectionState.Connected)
        {
            _reconnectAttempt = 0;
            OnConnectionStateChanged?.Invoke(true);
        }
        else if (state == ConnectionState.Disconnected)
        {
            OnConnectionStateChanged?.Invoke(false);
            if (!_intentionalDisconnect && !_disposed)
            {
                ScheduleReconnect();
            }
        }
    }

    private void HandleError(object sender, PusherException error)
    {
        DebugLog.Write("Pusher", $"Error: {error.Message}");
        System.Diagnostics.Debug.WriteLine($"[Pusher] error: {error.Message}");
    }

    private void HandleSupervisedTransferEvent(PusherEvent ev)
    {
        DebugLog.Write("Event:supervised_transfer", $"data={ev.Data}");
        try
        {
            var req = Newtonsoft.Json.JsonConvert
                .DeserializeObject<SupervisedTransferRequest>(ev.Data ?? "{}");
            if (req == null) return;
            System.Diagnostics.Debug.WriteLine(
                $"[Pusher] Supervised transfer request: {req.ConnectionId} from {req.CallerName}");
            OnSupervisedTransfer?.Invoke(req);
        }
        catch (Exception ex)
        {
            DebugLog.Write("Pusher", $"supervised_transfer parse failed: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[Pusher] supervised_transfer parse failed: {ex.Message}");
        }
    }

    private void HandleSupervisedTransferCancelledEvent(PusherEvent ev)
    {
        DebugLog.Write("Event:supervised_transfer_cancelled", $"data={ev.Data}");
        try
        {
            var obj = Newtonsoft.Json.Linq.JObject.Parse(ev.Data ?? "{}");
            var connectionId = (string?)obj["connectionId"] ?? (string?)obj["connection_id"] ?? "";
            System.Diagnostics.Debug.WriteLine($"[Pusher] Supervised transfer cancelled: {connectionId}");
            OnSupervisedTransferCancelled?.Invoke(connectionId);
        }
        catch (Exception ex)
        {
            DebugLog.Write("Pusher", $"supervised_transfer_cancelled parse failed: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[Pusher] supervised_transfer_cancelled parse failed: {ex.Message}");
        }
    }

    private void ScheduleReconnect()
    {
        if (_disposed || _intentionalDisconnect) return;

        _reconnectAttempt++;
        var delaySeconds = Math.Min(30, 2 * Math.Pow(2, _reconnectAttempt - 1));
        var delay = TimeSpan.FromSeconds(delaySeconds);
        DebugLog.Write("Pusher", $"Reconnect scheduled in {delaySeconds}s (attempt {_reconnectAttempt})");
        System.Diagnostics.Debug.WriteLine($"[Pusher] reconnect in {delaySeconds}s (attempt {_reconnectAttempt})");

        // Cancel any existing reconnect task so only one is ever active at a time.
        // Without this, multiple Disconnected events (keepalive + state change) pile
        // up separate tasks that race and tear each other down once one succeeds.
        var oldCts = _reconnectCts;
        _reconnectCts = new CancellationTokenSource();
        oldCts?.Cancel();
        var token = _reconnectCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
                if (token.IsCancellationRequested || _disposed || _intentionalDisconnect) return;

                // Unsubscribe from the old pusher BEFORE disconnecting.
                // DisconnectAsync fires the Disconnected event asynchronously after
                // returning; if we only relied on _intentionalDisconnect the flag
                // would already be reset by then, causing a spurious ScheduleReconnect
                // that produces the 2-second flap loop after wake-from-sleep.
                var oldPusher = _pusher;
                if (oldPusher != null)
                {
                    oldPusher.ConnectionStateChanged -= HandleConnectionStateChanged;
                    oldPusher.Error -= HandleError;
                    try { await oldPusher.DisconnectAsync().ConfigureAwait(false); }
                    catch { }
                }

                if (token.IsCancellationRequested || _disposed) return;
                await EstablishConnection().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                DebugLog.Write("Pusher", $"reconnect failed: {ex.Message}");
                ScheduleReconnect();
            }
        }, token);
    }

    private void StartKeepalive()
    {
        StopKeepalive();
        _keepaliveTimer = new System.Threading.Timer(KeepaliveTick, null,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    private void StopKeepalive()
    {
        _keepaliveTimer?.Dispose();
        _keepaliveTimer = null;
    }

    private void KeepaliveTick(object? state)
    {
        if (_disposed || _pusher == null) return;
        var pusherState = _pusher.State;
        System.Diagnostics.Debug.WriteLine($"[Pusher] keepalive tick — state={pusherState}");

        if (pusherState == ConnectionState.Disconnected && !_intentionalDisconnect)
        {
            ScheduleReconnect();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _intentionalDisconnect = true;
        _reconnectCts?.Cancel();
        StopKeepalive();

        if (_pusher != null)
        {
            try { _pusher.DisconnectAsync().GetAwaiter().GetResult(); }
            catch { }
            _pusher = null;
        }

        _reconnectCts?.Dispose();
        _authHttp.Dispose();
    }
}
