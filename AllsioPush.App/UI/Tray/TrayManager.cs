using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using AllsioPush.Config;

namespace AllsioPush.UI.Tray;

public class TrayManager : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    private static readonly Bitmap? OnImage = LoadEmbeddedImage("icon-on-128.png");
    private static readonly Bitmap? OffImage = LoadEmbeddedImage("icon-off-128.png");

    private readonly NotifyIcon _notifyIcon;
    private readonly AppSettings _settings;
    private bool _connected = false;
    private bool _signedIn = false;
    private IReadOnlyList<string> _channels = Array.Empty<string>();
    private IntPtr _iconHandle = IntPtr.Zero;

    private ToolStripMenuItem? _statusItem;
    private ToolStripMenuItem? _channelsItem;
    private ToolStripMenuItem? _authItem;

    public event EventHandler? OnOpenSettings;
    public event EventHandler? OnOpenHistory;
    public event EventHandler? OnSignIn;
    public event EventHandler? OnSignOut;
    public event EventHandler? OnExit;
    public event EventHandler? OnCheckForUpdates;

    public TrayManager(AppSettings settings)
    {
        _settings = settings;
        _notifyIcon = new NotifyIcon
        {
            Text = "Allsio Push",
            Visible = true,
            Icon = CreateTrayIcon(false),
        };

        BuildContextMenu();
        _notifyIcon.MouseClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Left)
                OnOpenHistory?.Invoke(this, EventArgs.Empty);
        };
    }

    private void BuildContextMenu()
    {
        var menu = new ContextMenuStrip();

        _statusItem = new ToolStripMenuItem("● Not signed in") { Enabled = false, Name = "statusItem" };
        _channelsItem = new ToolStripMenuItem("Channels") { Name = "channelsItem" };

        _authItem = new ToolStripMenuItem("Sign Out") { Name = "authItem" };
        _authItem.Click += (s, e) =>
        {
            if (_signedIn) OnSignOut?.Invoke(this, EventArgs.Empty);
            else OnSignIn?.Invoke(this, EventArgs.Empty);
        };

        menu.Items.Add(_statusItem);
        menu.Items.Add(_channelsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Notification History", null, (s, e) => OnOpenHistory?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("Settings", null, (s, e) => OnOpenSettings?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("Check for Updates", null, (s, e) => OnCheckForUpdates?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_authItem);
        menu.Items.Add("Exit", null, (s, e) => OnExit?.Invoke(this, EventArgs.Empty));

        _notifyIcon.ContextMenuStrip = menu;

        RefreshStatusItem();
        RebuildChannelsSubmenu();
    }

    public void SetConnected(bool connected)
    {
        _connected = connected;
        _notifyIcon.Icon = CreateTrayIcon(connected);
        _notifyIcon.Text = connected ? "Allsio Push — Connected" : "Allsio Push — Disconnected";

        RefreshStatusItem();
        RebuildChannelsSubmenu();
    }

    public void SetAuthState(bool signedIn)
    {
        _signedIn = signedIn;
        if (_authItem != null) _authItem.Text = signedIn ? "Sign Out" : "Sign In";
        RefreshStatusItem();
    }

    // Update the status line. When not signed in it overrides the Pusher
    // connection state and always reads "Not signed in".
    private void RefreshStatusItem()
    {
        if (_statusItem == null) return;
        if (!_signedIn)
        {
            _statusItem.Text = "● Not signed in";
            _statusItem.ForeColor = Color.Gray;
        }
        else
        {
            _statusItem.Text = _connected ? "● Connected" : "● Disconnected";
            _statusItem.ForeColor = _connected ? Color.Green : Color.Gray;
        }
    }

    public void UpdateChannels(IReadOnlyList<string> channels)
    {
        _channels = channels ?? Array.Empty<string>();

        var menu = _notifyIcon.ContextMenuStrip;
        if (menu != null && menu.IsHandleCreated && menu.InvokeRequired)
        {
            menu.BeginInvoke(RebuildChannelsSubmenu);
            return;
        }
        RebuildChannelsSubmenu();
    }

    private void RebuildChannelsSubmenu()
    {
        if (_channelsItem == null) return;

        _channelsItem.DropDownItems.Clear();

        if (!_connected)
        {
            _channelsItem.DropDownItems.Add(new ToolStripMenuItem("Not connected") { Enabled = false });
        }
        else if (_channels.Count == 0)
        {
            _channelsItem.DropDownItems.Add(new ToolStripMenuItem("Subscribing…") { Enabled = false });
        }
        else
        {
            foreach (var raw in _channels)
            {
                _channelsItem.DropDownItems.Add(new ToolStripMenuItem(FormatChannelName(raw)) { Enabled = false });
            }
        }
    }

    private static string FormatChannelName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "(unknown)";

        const string groupMarker = "-group-";
        const string userMarker = "-user-";

        int gi = raw.IndexOf(groupMarker, StringComparison.OrdinalIgnoreCase);
        if (gi >= 0)
        {
            var name = raw[(gi + groupMarker.Length)..];
            return $"Group: {name}";
        }

        if (raw.Contains(userMarker, StringComparison.OrdinalIgnoreCase))
            return "Personal Channel";

        return raw.Length > 40 ? raw[..40] : raw;
    }

    private static Bitmap? LoadEmbeddedImage(string logicalName)
    {
        try
        {
            using var stream = typeof(TrayManager).Assembly.GetManifestResourceStream(logicalName);
            return stream == null ? null : new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    private Icon CreateTrayIcon(bool connected)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            var src = connected ? OnImage : OffImage;
            if (src != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, size, size));
            }
            else
            {
                var color = connected ? Color.FromArgb(34, 197, 94) : Color.FromArgb(156, 163, 175);
                using var brush = new SolidBrush(color);
                g.FillEllipse(brush, 4, 4, size - 8, size - 8);
            }
        }

        // Release the previous native icon handle before creating a new one.
        if (_iconHandle != IntPtr.Zero)
        {
            DestroyIcon(_iconHandle);
            _iconHandle = IntPtr.Zero;
        }
        _iconHandle = bmp.GetHicon();
        return Icon.FromHandle(_iconHandle);
    }

    public void ShowBalloon(string title, string message, int timeoutMs = 3000)
    {
        _notifyIcon.ShowBalloonTip(timeoutMs, title, message, ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        if (_iconHandle != IntPtr.Zero)
        {
            DestroyIcon(_iconHandle);
            _iconHandle = IntPtr.Zero;
        }
    }
}
