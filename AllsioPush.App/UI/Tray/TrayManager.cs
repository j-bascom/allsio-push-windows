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
    private IntPtr _iconHandle = IntPtr.Zero;

    public event EventHandler? OnOpenSettings;
    public event EventHandler? OnOpenHistory;
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

        var statusItem = new ToolStripMenuItem("● Disconnected") { Enabled = false };
        statusItem.Name = "statusItem";

        menu.Items.Add(statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Notification History", null, (s, e) => OnOpenHistory?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("Settings", null, (s, e) => OnOpenSettings?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("Check for Updates", null, (s, e) => OnCheckForUpdates?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sign Out", null, (s, e) => OnSignOut?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("Exit", null, (s, e) => OnExit?.Invoke(this, EventArgs.Empty));

        _notifyIcon.ContextMenuStrip = menu;
    }

    public void SetConnected(bool connected)
    {
        _connected = connected;
        _notifyIcon.Icon = CreateTrayIcon(connected);
        _notifyIcon.Text = connected ? "Allsio Push — Connected" : "Allsio Push — Disconnected";

        var menu = _notifyIcon.ContextMenuStrip;
        if (menu?.Items["statusItem"] is ToolStripMenuItem statusItem)
        {
            statusItem.Text = connected ? "● Connected" : "● Disconnected";
            statusItem.ForeColor = connected ? Color.Green : Color.Gray;
        }
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
