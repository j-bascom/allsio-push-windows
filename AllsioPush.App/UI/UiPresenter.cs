using AllsioPush.Config;
using AllsioPush.Models;
using AllsioPush.Services;
using AllsioPush.UI.Windows;

namespace AllsioPush.UI;

/// App-layer implementation of the Core presenter abstraction. Creates the
/// concrete WinUI windows. All methods are invoked on the UI thread (the
/// router marshals via the SynchronizationContext before calling).
public class UiPresenter : INotificationPresenter
{
    private readonly AckService _ackService;
    private readonly WindowTracker _windowTracker;
    private readonly AppSettings _settings;
    private readonly SynchronizationContext _uiContext;

    public UiPresenter(AckService ackService, WindowTracker windowTracker, AppSettings settings, SynchronizationContext uiContext)
    {
        _ackService = ackService;
        _windowTracker = windowTracker;
        _settings = settings;
        _uiContext = uiContext;
    }

    public void ShowPopup(PushNotification notification)
    {
        var win = new PopupWindow(notification, _ackService, _windowTracker);
        win.Activate();
    }

    public void ShowSlideout(PushNotification notification)
    {
        var win = new SlideoutWindow(notification, _ackService, _windowTracker);
        win.Activate();
    }

    public void ShowScreenPop(ScreenPopData data)
    {
        var win = new ScreenPopWindow(data, _settings, _windowTracker);
        win.Activate();
    }

    public void OpenUrl(string url)
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
            System.Diagnostics.Debug.WriteLine($"[UiPresenter] OpenUrl failed: {ex.Message}");
        }
    }
}
