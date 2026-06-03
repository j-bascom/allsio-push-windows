using AllsioPush.Config;

namespace AllsioPush.UI.Tray;

public class TrayManager : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly AppSettings _settings;
    private bool _connected = false;

    public event EventHandler? OnOpenSettings;
    public event EventHandler? OnOpenHistory;
    public event EventHandler? OnSignOut;
    public event EventHandler? OnExit;

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

    private static Icon CreateTrayIcon(bool connected)
    {
        var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        var color = connected ? Color.FromArgb(34, 197, 94) : Color.FromArgb(156, 163, 175);
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, 2, 2, 12, 12);
        var handle = bmp.GetHicon();
        return Icon.FromHandle(handle);
    }

    public void ShowBalloon(string title, string message, int timeoutMs = 3000)
    {
        _notifyIcon.ShowBalloonTip(timeoutMs, title, message, ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
