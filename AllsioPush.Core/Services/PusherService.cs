using System.Net.Http.Headers;
using AllsioPush.Config;
using AllsioPush.Models;
using Newtonsoft.Json.Linq;
using PusherClient;

namespace AllsioPush.Services;

public class PusherService : IDisposable
{
    private readonly AuthSession _session;
    private readonly AppSettings _settings;
    private Pusher? _pusher;
    private System.Threading.Timer? _keepaliveTimer;
    private CancellationTokenSource? _reconnectCts;
    private int _reconnectAttempt = 0;
    private bool _intentionalDisconnect = false;
    private bool _disposed = false;

    private readonly object _channelsLock = new();
    private readonly List<string> _subscribedChannels = new();

    public event Action<PushNotification>? OnNotificationReceived;
    public event Action<string, string>? OnAcknowledgementReceived;
    public event Action<bool>? OnConnectionStateChanged;
    public event Action<IReadOnlyList<string>>? OnChannelsChanged;
    public event Action<List<PushGroup>>? OnChannelsUpdated;

    public IReadOnlyList<string> SubscribedChannels
    {
        get { lock (_channelsLock) { return _subscribedChannels.ToArray(); } }
    }

    public PusherService(AuthSession session, AppSettings settings)
    {
        _session = session;
        _settings = settings;
    }

    public async Task ConnectAsync()
    {
        if (_disposed) return;

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
        // Reset the confirmed-channel list so a (re)connect rebuilds it from scratch.
        lock (_channelsLock) { _subscribedChannels.Clear(); }
        OnChannelsChanged?.Invoke(SubscribedChannels);

        var authorizer = new HttpAuthorizer($"{_settings.ApiBase}/api/pusher/auth")
        {
            AuthenticationHeader = new AuthenticationHeaderValue("Bearer", _session.Token),
            Timeout = TimeSpan.FromSeconds(15),
        };

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
        try
        {
            var channel = await pusher.SubscribeAsync(channelName).ConfigureAwait(false);

            // SubscribeAsync awaits subscription_succeeded for private channels, so
            // the channel is confirmed by the time it returns here.
            bool added;
            lock (_channelsLock)
            {
                added = !_subscribedChannels.Contains(channelName);
                if (added) _subscribedChannels.Add(channelName);
            }
            if (added) OnChannelsChanged?.Invoke(SubscribedChannels);

            channel.Bind("notification", (PusherEvent ev) =>
            {
                try
                {
                    var notification = ParseNotification(ev.Data, channelName);
                    if (notification != null)
                        OnNotificationReceived?.Invoke(notification);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Pusher] notification parse failed: {ex.Message}");
                }
            });

            if (isGroup)
            {
                channel.Bind("notification_acknowledged", (PusherEvent ev) =>
                {
                    try
                    {
                        var obj = JObject.Parse(ev.Data ?? "{}");
                        var id = (string?)obj["notificationId"] ?? (string?)obj["notification_id"] ?? "";
                        var by = (string?)obj["acknowledgedBy"] ?? (string?)obj["acknowledged_by"] ?? "";
                        OnAcknowledgementReceived?.Invoke(id, by);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Pusher] ack parse failed: {ex.Message}");
                    }
                });
            }
            else
            {
                channel.Bind("channels_updated", (dynamic data) =>
                {
                    try
                    {
                        var json = Newtonsoft.Json.JsonConvert.SerializeObject(data);
                        var payload = Newtonsoft.Json.JsonConvert
                            .DeserializeObject<ChannelsUpdatedPayload>(json);
                        if (payload?.PushGroups == null) return;
                        HandleChannelsUpdated(payload.PushGroups);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[Pusher] channels_updated parse error: {ex.Message}");
                    }
                });
            }
        }
        catch (Exception ex)
        {
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
        };

        if (obj["buttons"] is JArray buttonsArr)
        {
            foreach (var b in buttonsArr)
            {
                n.Buttons.Add(new NotificationButton
                {
                    Label = (string?)b["label"] ?? "",
                    Action = (string?)b["action"] ?? "",
                    Style = (string?)b["style"] ?? "default",
                    WebhookUrl = (string?)b["webhookUrl"] ?? (string?)b["webhook_url"],
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
        System.Diagnostics.Debug.WriteLine($"[Pusher] error: {error.Message}");
    }

    private void ScheduleReconnect()
    {
        if (_disposed || _intentionalDisconnect) return;

        _reconnectAttempt++;
        var delaySeconds = Math.Min(30, 2 * Math.Pow(2, _reconnectAttempt - 1));
        var delay = TimeSpan.FromSeconds(delaySeconds);
        System.Diagnostics.Debug.WriteLine($"[Pusher] reconnect in {delay.TotalSeconds}s (attempt {_reconnectAttempt})");

        var token = _reconnectCts?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
                if (token.IsCancellationRequested || _disposed || _intentionalDisconnect) return;

                try
                {
                    if (_pusher != null)
                    {
                        await _pusher.DisconnectAsync().ConfigureAwait(false);
                    }
                }
                catch { }

                await EstablishConnection().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Pusher] reconnect failed: {ex.Message}");
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
    }
}
