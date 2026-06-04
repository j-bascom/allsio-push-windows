using System.Reflection;
using AllsioPush.Config;
using AllsioPush.Models;

namespace AllsioPush.UI.Windows;

public class SettingsWindow : Form
{
    private static readonly Color BgColor = Color.FromArgb(24, 24, 24);
    private static readonly Color SurfaceColor = Color.FromArgb(36, 36, 36);
    private static readonly Color BorderColor = Color.FromArgb(55, 55, 55);
    private static readonly Color TextPrimary = Color.FromArgb(240, 240, 240);
    private static readonly Color TextMuted = Color.FromArgb(140, 140, 140);
    private static readonly Color AccentColor = Color.FromArgb(59, 130, 246);
    private static readonly Color DangerColor = Color.FromArgb(220, 38, 38);
    private static readonly Color AmberColor = Color.FromArgb(217, 119, 6);
    private static readonly Color SegmentSelectedBg = Color.FromArgb(50, 50, 50);
    private static readonly Color SegmentUnselectedBg = Color.FromArgb(28, 28, 28);

    private readonly AppSettings _settings;
    private readonly AuthSession? _session;
    private readonly Action _onSignOut;

    private Button? _prodButton;
    private Button? _devButton;
    private Label? _envWarning;
    private Label? _envRestartNotice;
    private Button? _showImmediateButton;
    private Button? _deferToastButton;
    private Label? _displayModeDesc;

    public SettingsWindow(AppSettings settings, AuthSession? session, Action onSignOut)
    {
        _settings = settings;
        _session = session;
        _onSignOut = onSignOut;

        Text = "Allsio Push — Settings";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(480, 560);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        BackColor = BgColor;
        ForeColor = TextPrimary;
        Font = new Font("Segoe UI", 9f);

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = BgColor };
        var content = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Padding = new Padding(20, 16, 20, 16),
            BackColor = BgColor,
        };

        content.Controls.Add(BuildAccountSection());
        content.Controls.Add(Divider());
        content.Controls.Add(BuildConnectionSection());
        content.Controls.Add(Divider());
        content.Controls.Add(BuildNotificationsSection());
        content.Controls.Add(Divider());
        content.Controls.Add(BuildStartupSection());
        content.Controls.Add(Divider());
        content.Controls.Add(BuildAppSection());

        scroll.Controls.Add(content);
        Controls.Add(scroll);
    }

    private Control BuildAccountSection()
    {
        var section = NewSection("ACCOUNT");

        var name = new Label
        {
            Text = string.IsNullOrWhiteSpace(_session?.DisplayName) ? "Not signed in" : _session!.DisplayName,
            ForeColor = TextPrimary,
            Font = new Font("Segoe UI", 13f, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 2),
        };

        var email = new Label
        {
            Text = _session?.Email ?? "",
            ForeColor = TextMuted,
            Font = new Font("Segoe UI", 9f),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
        };

        var envBadge = new Label
        {
            Text = _settings.Environment == ServerEnvironment.Production ? "Production" : "Development",
            ForeColor = Color.White,
            BackColor = _settings.Environment == ServerEnvironment.Production
                ? Color.FromArgb(34, 197, 94)
                : AmberColor,
            Font = new Font("Segoe UI", 8f, FontStyle.Bold),
            AutoSize = false,
            Width = 90,
            Height = 20,
            TextAlign = ContentAlignment.MiddleCenter,
            Margin = new Padding(0, 0, 0, 10),
        };

        var signOut = new Button
        {
            Text = "Sign Out",
            Width = 100,
            Height = 30,
            FlatStyle = FlatStyle.Flat,
            BackColor = BgColor,
            ForeColor = DangerColor,
            Margin = new Padding(0, 4, 0, 0),
        };
        signOut.FlatAppearance.BorderColor = DangerColor;
        signOut.Click += (_, _) =>
        {
            Close();
            _onSignOut();
        };

        section.Controls.Add(name);
        section.Controls.Add(email);
        section.Controls.Add(envBadge);
        section.Controls.Add(signOut);
        return section;
    }

    private Control BuildConnectionSection()
    {
        var section = NewSection("CONNECTION");

        var label = new Label
        {
            Text = "Server Environment",
            ForeColor = TextPrimary,
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 6),
        };

        var segPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
            WrapContents = false,
        };
        _prodButton = SegmentButton("Production");
        _devButton = SegmentButton("Development");
        segPanel.Controls.Add(_prodButton);
        segPanel.Controls.Add(_devButton);

        _envWarning = new Label
        {
            Text = "Development server — for testing only",
            ForeColor = AmberColor,
            Font = new Font("Segoe UI", 8.5f),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 4),
            Visible = _settings.Environment == ServerEnvironment.Development,
        };
        _envRestartNotice = new Label
        {
            Text = "Server change will take effect after signing out and back in.",
            ForeColor = TextMuted,
            Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 0),
            Visible = false,
        };

        UpdateEnvSegment();

        _prodButton.Click += (_, _) => SetEnvironment(ServerEnvironment.Production);
        _devButton.Click += (_, _) => SetEnvironment(ServerEnvironment.Development);

        section.Controls.Add(label);
        section.Controls.Add(segPanel);
        section.Controls.Add(_envWarning);
        section.Controls.Add(_envRestartNotice);
        return section;
    }

    private Control BuildNotificationsSection()
    {
        var section = NewSection("NOTIFICATIONS");

        var soundLabel = new Label
        {
            Text = "Sound",
            ForeColor = TextPrimary,
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 4),
        };
        var soundToggle = ToggleSwitch(_settings.SoundEnabled, v =>
        {
            _settings.SoundEnabled = v;
            SettingsManager.Save(_settings);
        });

        var displayLabel = new Label
        {
            Text = "Display mode",
            ForeColor = TextPrimary,
            AutoSize = true,
            Margin = new Padding(0, 12, 0, 4),
        };
        var displayPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 4),
            WrapContents = false,
        };
        _showImmediateButton = SegmentButton("Show immediately");
        _deferToastButton = SegmentButton("Defer to toast");
        displayPanel.Controls.Add(_showImmediateButton);
        displayPanel.Controls.Add(_deferToastButton);

        _displayModeDesc = new Label
        {
            ForeColor = TextMuted,
            Font = new Font("Segoe UI", 8.5f),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 4),
        };

        UpdateDisplayModeSegment();

        _showImmediateButton.Click += (_, _) => SetDisplayMode(deferToToast: false);
        _deferToastButton.Click += (_, _) => SetDisplayMode(deferToToast: true);

        section.Controls.Add(soundLabel);
        section.Controls.Add(soundToggle);
        section.Controls.Add(displayLabel);
        section.Controls.Add(displayPanel);
        section.Controls.Add(_displayModeDesc);
        return section;
    }

    private Control BuildStartupSection()
    {
        var section = NewSection("STARTUP");

        var label = new Label
        {
            Text = "Launch on Windows startup",
            ForeColor = TextPrimary,
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 4),
        };
        var toggle = ToggleSwitch(_settings.LaunchOnStartup, v =>
        {
            _settings.LaunchOnStartup = v;
            SettingsManager.Save(_settings);
            SettingsManager.SetLaunchOnStartup(v);
        });

        section.Controls.Add(label);
        section.Controls.Add(toggle);
        return section;
    }

    private Control BuildAppSection()
    {
        var section = NewSection("APP");

        var versionRow = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            ColumnStyles =
            {
                new ColumnStyle(SizeType.Absolute, 120),
                new ColumnStyle(SizeType.AutoSize),
            },
            BackColor = BgColor,
            Margin = new Padding(0, 8, 0, 4),
        };
        versionRow.Controls.Add(new Label
        {
            Text = "Version",
            ForeColor = TextPrimary,
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 0),
        });
        versionRow.Controls.Add(new Label
        {
            Text = AppVersionString(),
            ForeColor = TextMuted,
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 0),
        });

        var folderLabel = new Label
        {
            Text = "App data folder",
            ForeColor = TextPrimary,
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 2),
        };
        var folderPath = new Label
        {
            Text = SettingsManager.GetAppDataPath(),
            ForeColor = TextMuted,
            Font = new Font("Segoe UI", 8f),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 6),
        };
        var openFolder = new Button
        {
            Text = "Open app data folder",
            Width = 180,
            Height = 28,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(50, 50, 50),
            ForeColor = TextPrimary,
            Margin = new Padding(0, 4, 0, 4),
        };
        openFolder.FlatAppearance.BorderColor = BorderColor;
        openFolder.Click += (_, _) => OpenAppDataFolder();

        section.Controls.Add(versionRow);
        section.Controls.Add(folderLabel);
        section.Controls.Add(folderPath);
        section.Controls.Add(openFolder);
        return section;
    }

    private static string AppVersionString()
    {
        var v = Assembly.GetEntryAssembly()?.GetName().Version;
        return v?.ToString() ?? "1.0.0";
    }

    private static void OpenAppDataFolder()
    {
        try
        {
            var path = SettingsManager.GetAppDataPath();
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Settings] OpenAppDataFolder failed: {ex.Message}");
        }
    }

    private FlowLayoutPanel NewSection(string title)
    {
        var section = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            WrapContents = false,
            BackColor = BgColor,
            Margin = new Padding(0, 4, 0, 4),
            Width = ClientSize.Width - 40,
        };
        var header = new Label
        {
            Text = title,
            ForeColor = TextMuted,
            Font = new Font("Segoe UI", 8f, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 4),
        };
        section.Controls.Add(header);
        return section;
    }

    private static Panel Divider()
    {
        var d = new Panel
        {
            Height = 1,
            BackColor = BorderColor,
            Width = 420,
            Margin = new Padding(0, 12, 0, 12),
        };
        return d;
    }

    private static Button SegmentButton(string text)
    {
        var b = new Button
        {
            Text = text,
            Width = 140,
            Height = 30,
            FlatStyle = FlatStyle.Flat,
            BackColor = SegmentUnselectedBg,
            ForeColor = TextPrimary,
            Margin = new Padding(0, 0, 0, 0),
        };
        b.FlatAppearance.BorderColor = BorderColor;
        return b;
    }

    private void UpdateEnvSegment()
    {
        if (_prodButton == null || _devButton == null) return;
        var prod = _settings.Environment == ServerEnvironment.Production;
        _prodButton.BackColor = prod ? SegmentSelectedBg : SegmentUnselectedBg;
        _devButton.BackColor = prod ? SegmentUnselectedBg : SegmentSelectedBg;
        _prodButton.FlatAppearance.BorderColor = prod ? AccentColor : BorderColor;
        _devButton.FlatAppearance.BorderColor = prod ? BorderColor : AmberColor;
    }

    private void SetEnvironment(ServerEnvironment env)
    {
        if (_settings.Environment == env) return;
        _settings.Environment = env;
        SettingsManager.Save(_settings);
        UpdateEnvSegment();
        if (_envWarning != null) _envWarning.Visible = env == ServerEnvironment.Development;
        if (_envRestartNotice != null) _envRestartNotice.Visible = true;
    }

    private void UpdateDisplayModeSegment()
    {
        if (_showImmediateButton == null || _deferToastButton == null || _displayModeDesc == null) return;
        var defer = _settings.DeferToToast;
        _showImmediateButton.BackColor = defer ? SegmentUnselectedBg : SegmentSelectedBg;
        _deferToastButton.BackColor = defer ? SegmentSelectedBg : SegmentUnselectedBg;
        _showImmediateButton.FlatAppearance.BorderColor = defer ? BorderColor : AccentColor;
        _deferToastButton.FlatAppearance.BorderColor = defer ? AccentColor : BorderColor;
        _displayModeDesc.Text = defer
            ? "Defer to toast: Toast appears first — click to open full notification."
            : "Show immediately: Notification windows open automatically.";
    }

    private void SetDisplayMode(bool deferToToast)
    {
        if (_settings.DeferToToast == deferToToast) return;
        _settings.DeferToToast = deferToToast;
        SettingsManager.Save(_settings);
        UpdateDisplayModeSegment();
    }

    private Control ToggleSwitch(bool initial, Action<bool> onChange)
    {
        var cb = new CheckBox
        {
            Appearance = Appearance.Button,
            Checked = initial,
            TextAlign = ContentAlignment.MiddleCenter,
            FlatStyle = FlatStyle.Flat,
            Width = 70,
            Height = 28,
            AutoSize = false,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 4),
        };
        cb.FlatAppearance.BorderColor = BorderColor;
        void Apply()
        {
            cb.Text = cb.Checked ? "ON" : "OFF";
            cb.BackColor = cb.Checked ? AccentColor : Color.FromArgb(40, 40, 40);
            cb.ForeColor = cb.Checked ? Color.White : TextMuted;
        }
        Apply();
        cb.CheckedChanged += (_, _) =>
        {
            Apply();
            try { onChange(cb.Checked); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Settings] toggle handler threw: {ex.Message}");
            }
        };
        return cb;
    }
}
