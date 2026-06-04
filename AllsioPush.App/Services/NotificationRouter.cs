using AllsioPush.Config;
using AllsioPush.Models;
using AllsioPush.UI.Windows;

namespace AllsioPush.Services;

public class NotificationRouter
{
    private readonly AppSettings _settings;
    private readonly SynchronizationContext _uiContext;
    private readonly ToastService _toastService;
    private readonly AckService _ackService;
    private readonly WindowTracker _windowTracker;
    private readonly HistoryService _history;

    public event Action<HistoryEntry>? OnEntrySaved;

    public NotificationRouter(
        AppSettings settings,
        SynchronizationContext uiContext,
        ToastService toastService,
        AckService ackService,
        WindowTracker windowTracker,
        HistoryService history)
    {
        _settings = settings;
        _uiContext = uiContext;
        _toastService = toastService;
        _ackService = ackService;
        _windowTracker = windowTracker;
        _history = history;
    }

    public void Route(PushNotification notification)
    {
        var receivedAt = DateTime.UtcNow;

        // Persist to local history in the background; surface the saved entry
        // to subscribers (e.g. open HistoryWindow) without blocking routing.
        _ = Task.Run(async () =>
        {
            var entry = await _history.SaveNotification(notification, receivedAt);
            if (entry != null)
            {
                try { OnEntrySaved?.Invoke(entry); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Router] OnEntrySaved handler threw: {ex.Message}");
                }
            }
        });

        if (notification.TemplateType == "url_tab")
        {
            if (!string.IsNullOrWhiteSpace(notification.Url))
                OpenUrl(notification.Url!);
            return;
        }

        if (notification.TemplateType == "url_popup")
        {
            _uiContext.Post(_ => OpenPopupWindow(notification), null);
            return;
        }

        if (_settings.DeferToToast)
        {
            _toastService.Show(notification);
            return;
        }

        if (notification.DisplayMode == "popup" || notification.TemplateType == "custom_html")
        {
            _uiContext.Post(_ => OpenPopupWindow(notification), null);
            return;
        }

        _uiContext.Post(_ => OpenSlideoutWindow(notification), null);
    }

    private void OpenUrl(string url)
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
            System.Diagnostics.Debug.WriteLine($"[Router] OpenUrl failed: {ex.Message}");
        }
    }

    private void OpenPopupWindow(PushNotification notification)
    {
        try
        {
            var win = new PopupWindow(notification, _ackService, _windowTracker);
            win.Show();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Router] OpenPopupWindow failed: {ex.Message}");
        }
    }

    private void OpenSlideoutWindow(PushNotification notification)
    {
        try
        {
            var win = new SlideoutWindow(notification, _ackService, _windowTracker);
            win.Show();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Router] OpenSlideoutWindow failed: {ex.Message}");
        }
    }
}
