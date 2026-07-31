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

    public void ShowSmsSlideout(PushNotification notification, bool startExpanded)
    {
        var win = new SmsSlideoutWindow(notification, _settings, _ackService, _windowTracker, startExpanded);
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

    public void OpenUrlInNewWindow(string url)
    {
        try
        {
            var exe = GetDefaultBrowserExe();
            if (!string.IsNullOrWhiteSpace(exe) && System.IO.File.Exists(exe))
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                // Chromium- and Firefox-family browsers accept a new-window flag;
                // for anything else we can't guarantee a window, so fall through.
                var flag = name switch
                {
                    "firefox" or "waterfox" or "librewolf" or "palemoon" => "-new-window",
                    "chrome" or "msedge" or "brave" or "vivaldi" or "opera" or "chromium" => "--new-window",
                    _ => null,
                };
                if (flag != null)
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = exe,
                        UseShellExecute = false,
                    };
                    psi.ArgumentList.Add(flag);
                    psi.ArgumentList.Add(url);
                    System.Diagnostics.Process.Start(psi);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[UiPresenter] OpenUrlInNewWindow failed: {ex.Message}");
        }

        // Unknown/undetectable browser — open normally (typically a new tab).
        OpenUrl(url);
    }

    /// Resolves the executable path of the user's default https handler by reading
    /// the UserChoice ProgId, then that ProgId's registered shell open command.
    private static string? GetDefaultBrowserExe()
    {
        try
        {
            string? progId;
            using (var choice = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice"))
            {
                progId = choice?.GetValue("ProgId") as string;
            }
            if (string.IsNullOrWhiteSpace(progId)) return null;

            using var cmdKey = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
            var command = (cmdKey?.GetValue(null) as string)?.Trim();
            if (string.IsNullOrWhiteSpace(command)) return null;

            // The command is like: "C:\...\chrome.exe" -- "%1"  — pull out the exe path.
            if (command.StartsWith("\""))
            {
                var end = command.IndexOf('"', 1);
                return end > 1 ? command.Substring(1, end - 1) : null;
            }
            var space = command.IndexOf(' ');
            return space > 0 ? command.Substring(0, space) : command;
        }
        catch
        {
            return null;
        }
    }
}
