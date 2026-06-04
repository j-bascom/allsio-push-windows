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

    public NotificationRouter(
        AppSettings settings,
        SynchronizationContext uiContext,
        ToastService toastService,
        AckService ackService,
        WindowTracker windowTracker)
    {
        _settings = settings;
        _uiContext = uiContext;
        _toastService = toastService;
        _ackService = ackService;
        _windowTracker = windowTracker;
    }

    public void Route(PushNotification notification)
    {
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
