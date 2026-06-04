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
    private bool _activationRegistered = false;

    public ToastService(AppSettings settings, AckService ackService, SynchronizationContext uiContext)
    {
        _settings = settings;
        _ackService = ackService;
        _uiContext = uiContext;
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

    public void Show(PushNotification n)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(n.NotificationId))
                _byId[n.NotificationId] = n;

            TrimCache();

            var builder = new ToastContentBuilder();

            switch (n.TemplateType)
            {
                case "caller_card":
                    BuildCallerCard(builder, n);
                    break;
                case "appointment_alert":
                    BuildAppointmentAlert(builder, n);
                    break;
                case "url_tab":
                case "url_popup":
                    BuildUrlToast(builder, n);
                    break;
                case "custom_html":
                    BuildCustomHtmlToast(builder, n);
                    break;
                case "plain_text":
                default:
                    BuildPlainText(builder, n);
                    break;
            }

            ApplyAudio(builder, n);
            ApplyButtons(builder, n);

            var tag = string.IsNullOrWhiteSpace(n.NotificationId) ? "allsio" : SanitizeTag(n.NotificationId);
            builder.Show(toast =>
            {
                toast.Tag = tag;
                toast.Group = "AllsioPush";
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Toast] Show failed: {ex.Message}");
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

    private static void BuildPlainText(ToastContentBuilder b, PushNotification n)
    {
        b.AddText(n.Title);
        if (!string.IsNullOrWhiteSpace(n.Content))
            b.AddText(Truncate(n.Content, 200));
        AddDefaultArgs(b, n, defaultAction: "openWindow");
    }

    private static void BuildCallerCard(ToastContentBuilder b, PushNotification n)
    {
        b.AddText(string.IsNullOrWhiteSpace(n.Title) ? "Incoming Call" : n.Title);
        var line1 = n.CallerName ?? n.CallerPhone ?? "Unknown";
        b.AddText(line1);
        if (!string.IsNullOrWhiteSpace(n.Reason))
            b.AddText(Truncate(n.Reason, 200));
        AddDefaultArgs(b, n, defaultAction: "openWindow");
    }

    private static void BuildAppointmentAlert(ToastContentBuilder b, PushNotification n)
    {
        b.AddText(string.IsNullOrWhiteSpace(n.Title) ? "Appointment Alert" : n.Title);
        if (!string.IsNullOrWhiteSpace(n.AppointmentDate))
            b.AddText(n.AppointmentDate);

        var line2Parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(n.Service)) line2Parts.Add(n.Service);
        if (!string.IsNullOrWhiteSpace(n.Stylist)) line2Parts.Add(n.Stylist);
        if (line2Parts.Count > 0)
            b.AddText(string.Join(" — ", line2Parts));

        AddDefaultArgs(b, n, defaultAction: "openWindow");
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

    private static void BuildCustomHtmlToast(ToastContentBuilder b, PushNotification n)
    {
        b.AddText(n.Title);
        if (!string.IsNullOrWhiteSpace(n.Content))
            b.AddText(Truncate(n.Content, 200));
        AddDefaultArgs(b, n, defaultAction: "openWindow");
    }

    private static void AddDefaultArgs(ToastContentBuilder b, PushNotification n, string defaultAction)
    {
        b.AddArgument("action", defaultAction);
        if (!string.IsNullOrWhiteSpace(n.NotificationId))
            b.AddArgument("id", n.NotificationId);
    }

    private static void ApplyButtons(ToastContentBuilder b, PushNotification n)
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
                    if (!string.IsNullOrWhiteSpace(n.NotificationId))
                        toastButton.AddArgument("id", n.NotificationId);
                    break;
                case "dismiss":
                    toastButton.AddArgument("action", "dismiss");
                    toastButton.AddArgument("label", label);
                    if (!string.IsNullOrWhiteSpace(n.NotificationId))
                        toastButton.AddArgument("id", n.NotificationId);
                    break;
                case "webhook":
                    toastButton.AddArgument("action", "webhook");
                    toastButton.AddArgument("buttonId", $"btn{i}");
                    if (!string.IsNullOrWhiteSpace(n.NotificationId))
                        toastButton.AddArgument("id", n.NotificationId);
                    if (!string.IsNullOrWhiteSpace(btn.WebhookUrl))
                        toastButton.AddArgument("url", btn.WebhookUrl);
                    break;
                default:
                    toastButton.AddArgument("action", btn.Action ?? "noop");
                    toastButton.AddArgument("label", label);
                    if (!string.IsNullOrWhiteSpace(n.NotificationId))
                        toastButton.AddArgument("id", n.NotificationId);
                    break;
            }

            b.AddButton(toastButton);
            hasAnyButton = true;
        }

        if (!hasAnyButton && !string.IsNullOrWhiteSpace(n.NotificationId))
        {
            var defaultAck = new ToastButton()
                .SetContent("Acknowledge")
                .SetBackgroundActivation()
                .AddArgument("action", "ack")
                .AddArgument("label", "Acknowledge")
                .AddArgument("id", n.NotificationId);
            b.AddButton(defaultAck);
        }
    }

    private void ApplyAudio(ToastContentBuilder b, PushNotification n)
    {
        if (!_settings.SoundEnabled
            || string.Equals(n.Sound, "false", StringComparison.OrdinalIgnoreCase)
            || string.Equals(n.Sound, "none", StringComparison.OrdinalIgnoreCase))
        {
            b.AddAudio(new ToastAudio { Silent = true });
            return;
        }

        var src = (n.Sound ?? string.Empty).ToLowerInvariant() switch
        {
            "alert" => "ms-winsoundevent:Notification.Looping.Alarm",
            "soft" => "ms-winsoundevent:Notification.IM",
            "escalate" => "ms-winsoundevent:Notification.Reminder",
            "chime" => "ms-winsoundevent:Notification.Default",
            _ => "ms-winsoundevent:Notification.Default",
        };

        b.AddAudio(new Uri(src), loop: false, silent: false);
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
        return s[..(max - 1)] + "…";
    }

    private static string SanitizeTag(string id)
    {
        if (id.Length <= 64) return id;
        return id[..64];
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
