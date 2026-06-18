using System.Collections.Concurrent;
using AllsioPush.Config;
using AllsioPush.Models;
using Microsoft.Toolkit.Uwp.Notifications;

namespace AllsioPush.Services;

public class ToastService
{
    private readonly AppSettings _settings;
    private readonly AckService _ackService;
    private readonly SynchronizationContext _uiContext;
    private readonly ConcurrentDictionary<string, PushNotification> _byId = new();
    private Action<PushNotification>? _openWindowCallback;
    private Action<PushNotification>? _openSmsReplyCallback;
    private bool _activationRegistered = false;

    public event Action<string, string>? OnTransferAction;

    public ToastService(AppSettings settings, AckService ackService, SynchronizationContext uiContext)
    {
        _settings = settings;
        _ackService = ackService;
        _uiContext = uiContext;
    }

    // Lets the App open the SMS slideout (in expanded reply state) when a
    // toast's "sms_reply" action is activated.
    public void RegisterSmsReplyHandler(Action<PushNotification> openSmsReplyExpanded)
    {
        _openSmsReplyCallback = openSmsReplyExpanded;
    }

    public void RegisterActivationHandler(Action<PushNotification> openWindowCallback)
    {
        _openWindowCallback = openWindowCallback;
        if (_activationRegistered) return;
        _activationRegistered = true;

        ToastNotificationManagerCompat.OnActivated += toastArgs =>
        {
            try
            {
                var args = ToastArguments.Parse(toastArgs.Argument);
                HandleActivation(args);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Toast] Activation handler failed: {ex.Message}");
            }
        };
    }

    private void HandleActivation(ToastArguments args)
    {
        var action = args.Get("action");
        var id = args.Get("id");

        switch (action)
        {
            case "ack":
                {
                    var label = args.Get("label") ?? "Acknowledge";
                    _ = _ackService.Acknowledge(id, label, "ack");
                    break;
                }
            case "dismiss":
                {
                    var label = args.Get("label") ?? "Dismiss";
                    _ = _ackService.Dismiss(id, label);
                    break;
                }
            case "webhook":
                {
                    var url = args.Get("url");
                    var buttonId = args.Get("buttonId") ?? "webhook";
                    if (!string.IsNullOrWhiteSpace(url)
                        && !string.IsNullOrWhiteSpace(id)
                        && _byId.TryGetValue(id, out var payload))
                    {
                        _ = _ackService.FireWebhook(url, payload, buttonId);
                    }
                    break;
                }
            case "openUrl":
                {
                    var url = args.Get("url");
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        _uiContext.Post(_ => OpenInBrowser(url), null);
                    }
                    break;
                }
            case "openWindow":
                {
                    if (!string.IsNullOrWhiteSpace(id)
                        && _byId.TryGetValue(id, out var payload)
                        && _openWindowCallback != null)
                    {
                        var cb = _openWindowCallback;
                        _uiContext.Post(_ => cb(payload), null);
                    }
                    break;
                }
            case "sms_open":
                {
                    var convId = args.Get("conversationId");
                    if (!string.IsNullOrWhiteSpace(convId))
                    {
                        var url = $"{_settings.AdminBase}/dashboard/sms/inbox?conversation={Uri.EscapeDataString(convId)}";
                        _uiContext.Post(_ => OpenInBrowser(url), null);
                    }
                    break;
                }
            case "sms_reply":
                {
                    // Re-open the stored SMS payload as a slideout in expanded reply state.
                    if (!string.IsNullOrWhiteSpace(id)
                        && _byId.TryGetValue(id, out var payload)
                        && _openSmsReplyCallback != null)
                    {
                        var cb = _openSmsReplyCallback;
                        _uiContext.Post(_ => cb(payload), null);
                    }
                    break;
                }
            case "transfer_accept":
            case "transfer_decline":
                {
                    var connectionId = args.Get("connectionId") ?? "";
                    OnTransferAction?.Invoke(action, connectionId);
                    break;
                }
        }
    }

    private static void OpenInBrowser(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Toast] OpenInBrowser failed: {ex.Message}");
        }
    }

    // Returns false when Windows notifications are disabled for the app,
    // so callers can fall back to the slideout path.
    public bool TryShow(PushNotification n)
    {
        try
        {
            var setting = ToastNotificationManagerCompat.CreateToastNotifier().Setting;
            if (setting != Windows.UI.Notifications.NotificationSetting.Enabled)
            {
                System.Diagnostics.Debug.WriteLine($"[Toast] Notifications disabled: {setting}");
                return false;
            }
        }
        catch { /* unable to check — proceed */ }

        Show(n);
        return true;
    }

    public void Show(PushNotification n)
    {
        try
        {
            // Always store with a stable key so clicking the toast can retrieve
            // the payload even when the server sends no NotificationId.
            var key = string.IsNullOrWhiteSpace(n.NotificationId)
                ? Guid.NewGuid().ToString("N")[..16]
                : n.NotificationId;
            _byId[key] = n;

            TrimCache();

            var builder = new ToastContentBuilder();

            switch (n.TemplateType)
            {
                case "caller_card":
                    BuildCallerCard(builder, n, key);
                    break;
                case "appointment_alert":
                    BuildAppointmentAlert(builder, n, key);
                    break;
                case "url_tab":
                case "url_popup":
                    BuildUrlToast(builder, n);
                    break;
                case "custom_html":
                    BuildCustomHtmlToast(builder, n, key);
                    break;
                case "plain_text":
                default:
                    BuildPlainText(builder, n, key);
                    break;
            }

            ApplyAudio(builder, n);
            ApplyButtons(builder, n, key);

            builder.Show(toast =>
            {
                toast.Tag = SanitizeTag(key);
                toast.Group = "AllsioPush";
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Toast] Show failed: {ex.Message}");
            try
            {
                var dir = Config.SettingsManager.GetAppDataPath();
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "error.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Toast.Show: {ex}\n");
            }
            catch { }
        }
    }

    public void ShowBalloon(string title, string body)
    {
        try
        {
            new ToastContentBuilder()
                .AddText(title)
                .AddText(body)
                .Show(toast =>
                {
                    toast.Tag = "balloon";
                    toast.Group = "AllsioPush";
                });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Toast] ShowBalloon failed: {ex.Message}");
        }
    }

    public void UpdateRemoteAck(string notificationId, string acknowledgedBy)
    {
        try
        {
            var displayName = string.IsNullOrWhiteSpace(acknowledgedBy) ? "Someone" : acknowledgedBy;
            if (!string.IsNullOrWhiteSpace(notificationId))
            {
                try
                {
                    ToastNotificationManagerCompat.History.Remove(SanitizeTag(notificationId), "AllsioPush");
                }
                catch { }
            }

            new ToastContentBuilder()
                .AddText("Notification acknowledged")
                .AddText($"{displayName} acknowledged this notification")
                .Show(toast =>
                {
                    toast.Tag = "ack-" + (notificationId ?? Guid.NewGuid().ToString("N"));
                    toast.Group = "AllsioPush";
                });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Toast] UpdateRemoteAck failed: {ex.Message}");
        }
    }

    private static void BuildPlainText(ToastContentBuilder b, PushNotification n, string key)
    {
        b.AddText(n.Title);
        if (!string.IsNullOrWhiteSpace(n.Content))
            b.AddText(Truncate(n.Content, 200));
        AddDefaultArgs(b, key, defaultAction: "openWindow");
    }

    private static void BuildCallerCard(ToastContentBuilder b, PushNotification n, string key)
    {
        b.AddText(string.IsNullOrWhiteSpace(n.Title) ? "Incoming Call" : n.Title);
        var line1 = n.CallerName ?? n.CallerPhone ?? "Unknown";
        b.AddText(line1);
        if (!string.IsNullOrWhiteSpace(n.Reason))
            b.AddText(Truncate(n.Reason, 200));
        AddDefaultArgs(b, key, defaultAction: "openWindow");
    }

    private static void BuildAppointmentAlert(ToastContentBuilder b, PushNotification n, string key)
    {
        b.AddText(string.IsNullOrWhiteSpace(n.Title) ? "Appointment Alert" : n.Title);

        if (n.Rows.Count > 0)
        {
            // Dynamic rows — toast supports 2 body lines; pack first 4 rows into 2 lines
            var rows = n.Rows.Take(4).ToList();
            if (rows.Count >= 1)
                b.AddText(string.Join(" · ", rows.Take(2).Select(r => $"{r.Label}: {r.Value}")));
            if (rows.Count >= 3)
                b.AddText(string.Join(" · ", rows.Skip(2).Select(r => $"{r.Label}: {r.Value}")));
        }
        else
        {
            // Fallback for older flat payloads
            var line1Parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(n.AppointmentDate)) line1Parts.Add($"Date: {n.AppointmentDate}");
            if (!string.IsNullOrWhiteSpace(n.Service)) line1Parts.Add($"Service: {n.Service}");
            if (line1Parts.Count > 0) b.AddText(string.Join(" · ", line1Parts));
            if (!string.IsNullOrWhiteSpace(n.Stylist)) b.AddText($"Staff Member: {n.Stylist}");
        }

        AddDefaultArgs(b, key, defaultAction: "openWindow");
    }

    private static void BuildUrlToast(ToastContentBuilder b, PushNotification n)
    {
        b.AddText(n.Title);
        if (!string.IsNullOrWhiteSpace(n.Content))
            b.AddText(Truncate(n.Content, 200));

        b.AddArgument("action", "openUrl");
        if (!string.IsNullOrWhiteSpace(n.Url)) b.AddArgument("url", n.Url);
        if (!string.IsNullOrWhiteSpace(n.NotificationId)) b.AddArgument("id", n.NotificationId);
    }

    private static void BuildCustomHtmlToast(ToastContentBuilder b, PushNotification n, string key)
    {
        b.AddText(n.Title);
        if (!string.IsNullOrWhiteSpace(n.Content))
            b.AddText(Truncate(n.Content, 200));
        AddDefaultArgs(b, key, defaultAction: "openWindow");
    }

    private static void AddDefaultArgs(ToastContentBuilder b, string key, string defaultAction)
    {
        b.AddArgument("action", defaultAction);
        b.AddArgument("id", key);
    }

    private static void ApplyButtons(ToastContentBuilder b, PushNotification n, string key)
    {
        if (n.TemplateType == "url_tab" || n.TemplateType == "url_popup")
            return;

        var hasAnyButton = false;
        for (int i = 0; i < n.Buttons.Count; i++)
        {
            var btn = n.Buttons[i];
            var label = string.IsNullOrWhiteSpace(btn.Label) ? "Button" : btn.Label;

            var toastButton = new ToastButton()
                .SetContent(label)
                .SetBackgroundActivation();

            switch (btn.Action)
            {
                case "ack":
                    toastButton.AddArgument("action", "ack");
                    toastButton.AddArgument("label", label);
                    toastButton.AddArgument("id", key);
                    break;
                case "dismiss":
                    toastButton.AddArgument("action", "dismiss");
                    toastButton.AddArgument("label", label);
                    toastButton.AddArgument("id", key);
                    break;
                case "webhook":
                    toastButton.AddArgument("action", "webhook");
                    toastButton.AddArgument("buttonId", $"btn{i}");
                    toastButton.AddArgument("id", key);
                    if (!string.IsNullOrWhiteSpace(btn.WebhookUrl))
                        toastButton.AddArgument("url", btn.WebhookUrl);
                    break;
                default:
                    toastButton.AddArgument("action", btn.Action ?? "noop");
                    toastButton.AddArgument("label", label);
                    toastButton.AddArgument("id", key);
                    break;
            }

            b.AddButton(toastButton);
            hasAnyButton = true;
        }

        if (!hasAnyButton)
        {
            var defaultAck = new ToastButton()
                .SetContent("Acknowledge")
                .SetBackgroundActivation()
                .AddArgument("action", "ack")
                .AddArgument("label", "Acknowledge")
                .AddArgument("id", key);
            b.AddButton(defaultAck);
        }
    }

    private void ApplyAudio(ToastContentBuilder b, PushNotification n)
    {
        // ToneService is the sole sound source — always suppress WinRT toast audio
        // so notifications don't double-play. The IncomingCall OS sound for
        // supervised transfers is handled outside ToastService and is unaffected.
        b.AddAudio(new ToastAudio { Silent = true });
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
        return s[..(max - 1)] + "…";
    }

    private static string SanitizeTag(string id)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in id)
            sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '.' ? c : '_');
        var s = sb.ToString();
        return s.Length <= 64 ? s : s[..64];
    }

    private void TrimCache()
    {
        const int max = 200;
        if (_byId.Count <= max) return;
        var excess = _byId.Count - max;
        foreach (var key in _byId.Keys.Take(excess))
        {
            _byId.TryRemove(key, out _);
        }
    }
}
