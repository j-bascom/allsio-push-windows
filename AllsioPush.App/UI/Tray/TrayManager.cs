using System.Drawing;
using System.Reflection;
using System.Windows.Input;
using AllsioPush.Config;
using H.NotifyIcon;
using Microsoft.UI.Xaml.Controls;

namespace AllsioPush.UI.Tray;

public class TrayManager : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly AppSettings _settings;
    private readonly Icon _onIcon;
    private readonly Icon _offIcon;

    private bool _connected;
    private bool _signedIn;
    private IReadOnlyList<string> _channels = Array.Empty<string>();

    private MenuFlyoutItem _statusItem = null!;
    private MenuFlyoutSubItem _channelsItem = null!;
    private MenuFlyoutItem _authItem = null!;

    public event EventHandler? OnOpenSettings;
    public event EventHandler? OnOpenHistory;
    public event EventHandler? OnSignIn;
    public event EventHandler? OnSignOut;
    public event EventHandler? OnExit;
    public event EventHandler? OnCheckForUpdates;

    public TrayManager(AppSettings settings)
    {
        _settings = settings;
        _onIcon = LoadIcon("icon-on-128.png", "icon-on.ico");
        _offIcon = LoadIcon("icon-off-128.png", "icon-off.ico");

        _icon = new TaskbarIcon
        {
            ToolTipText = "Allsio Push",
            Icon = _offIcon,
            ContextFlyout = BuildMenu(),
            LeftClickCommand = new DelegateCommand(() => OnOpenHistory?.Invoke(this, EventArgs.Empty)),
        };
        _icon.ForceCreate();

        RefreshStatusItem();
        RebuildChannelsSubmenu();
    }

    private MenuFlyout BuildMenu()
    {
        var menu = new MenuFlyout();

        // NOTE: For a code-created TaskbarIcon (not in a XAML tree), MenuFlyoutItem.Click
        // events do not fire reliably — use Command bindings instead.
        _statusItem = new MenuFlyoutItem { Text = "Not signed in", IsEnabled = false };
        _channelsItem = new MenuFlyoutSubItem { Text = "Channels" };
        _authItem = new MenuFlyoutItem
        {
            Text = "Sign Out",
            Command = new DelegateCommand(() =>
            {
                if (_signedIn) OnSignOut?.Invoke(this, EventArgs.Empty);
                else OnSignIn?.Invoke(this, EventArgs.Empty);
            }),
        };

        var history = new MenuFlyoutItem
        {
            Text = "Notification History",
            Command = new DelegateCommand(() => OnOpenHistory?.Invoke(this, EventArgs.Empty)),
        };
        var settings = new MenuFlyoutItem
        {
            Text = "Settings",
            Command = new DelegateCommand(() => OnOpenSettings?.Invoke(this, EventArgs.Empty)),
        };
        var updates = new MenuFlyoutItem
        {
            Text = "Check for Updates",
            Command = new DelegateCommand(() => OnCheckForUpdates?.Invoke(this, EventArgs.Empty)),
        };
        var exit = new MenuFlyoutItem
        {
            Text = "Exit",
            Command = new DelegateCommand(() => OnExit?.Invoke(this, EventArgs.Empty)),
        };

        menu.Items.Add(_statusItem);
        menu.Items.Add(_channelsItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(history);
        menu.Items.Add(settings);
        menu.Items.Add(updates);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(_authItem);
        menu.Items.Add(exit);
        return menu;
    }

    public void SetConnected(bool connected)
    {
        _connected = connected;
        var icon = connected ? _onIcon : _offIcon;
        _icon.Icon = icon;
        _icon.UpdateIcon(icon);
        _icon.ToolTipText = connected ? "Allsio Push — Connected" : "Allsio Push — Disconnected";
        RefreshStatusItem();
        RebuildChannelsSubmenu();
    }

    public void SetAuthState(bool signedIn)
    {
        _signedIn = signedIn;
        _authItem.Text = signedIn ? "Sign Out" : "Sign In";
        RefreshStatusItem();
    }

    public void UpdateChannels(IReadOnlyList<string> channels)
    {
        _channels = channels ?? Array.Empty<string>();
        RebuildChannelsSubmenu();
    }

    private void RefreshStatusItem()
    {
        if (!_signedIn)
            _statusItem.Text = "Not signed in";
        else
            _statusItem.Text = _connected ? "Connected" : "Disconnected";
    }

    private void RebuildChannelsSubmenu()
    {
        _channelsItem.Items.Clear();
        if (!_connected)
        {
            _channelsItem.Items.Add(new MenuFlyoutItem { Text = "Not connected", IsEnabled = false });
        }
        else if (_channels.Count == 0)
        {
            _channelsItem.Items.Add(new MenuFlyoutItem { Text = "Subscribing…", IsEnabled = false });
        }
        else
        {
            foreach (var raw in _channels)
                _channelsItem.Items.Add(new MenuFlyoutItem
                {
                    Text = string.IsNullOrWhiteSpace(raw) ? "(unknown)" : raw,
                    IsEnabled = false,
                });
        }
    }

    public void ShowBalloon(string title, string message, int timeoutMs = 3000)
    {
        try { _icon.ShowNotification(title: title, message: message); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Tray] balloon failed: {ex.Message}"); }
    }

    private static Icon LoadIcon(string embeddedPng, string icoFallback)
    {
        // Prefer the high-res embedded PNG rendered to a real HICON; the tray needs a
        // System.Drawing.Icon (synchronous HICON) — a BitmapImage decodes async and the
        // tray icon ends up blank or fails to swap on state changes.
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream(embeddedPng);
            if (stream != null)
            {
                using var bmp = new Bitmap(stream);
                return Icon.FromHandle(bmp.GetHicon());
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Tray] PNG icon load failed: {ex.Message}"); }

        var path = Path.Combine(AppContext.BaseDirectory, "Resources", icoFallback);
        return new Icon(path);
    }

    public void Dispose()
    {
        try { _icon.Dispose(); } catch { }
        try { _onIcon.Dispose(); } catch { }
        try { _offIcon.Dispose(); } catch { }
    }

    private sealed class DelegateCommand : ICommand
    {
        private readonly Action _action;
        public DelegateCommand(Action action) => _action = action;
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _action();
    }
}
