using System.Reflection;
using AllsioPush.Config;
using AllsioPush.Models;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinUIEx;

namespace AllsioPush.UI.Windows;

public class SettingsWindow : WindowEx
{
    private static readonly SolidColorBrush TextMuted = new(ColorHelper.FromArgb(255, 140, 140, 140));
    private static readonly SolidColorBrush AmberColor = new(ColorHelper.FromArgb(255, 217, 119, 6));
    private static readonly SolidColorBrush GreenColor = new(ColorHelper.FromArgb(255, 34, 197, 94));
    private static readonly SolidColorBrush DangerColor = new(ColorHelper.FromArgb(255, 220, 38, 38));

    private readonly AppSettings _settings;
    private readonly AuthSession? _session;
    private readonly Action _onSignOut;
    private readonly Action _onOpenDebugLog;

    private TextBlock? _envWarning;
    private TextBlock? _envRestartNotice;
    private TextBlock? _displayModeDesc;

    public SettingsWindow(AppSettings settings, AuthSession? session, Action onSignOut, Action onOpenDebugLog)
    {
        _settings = settings;
        _session = session;
        _onSignOut = onSignOut;
        _onOpenDebugLog = onOpenDebugLog;

        Title = "Allsio Push — Settings";
        this.SetWindowSize(480, 600);
        this.CenterOnScreen();
        WindowIcon.Apply(this);
        SystemBackdrop = new MicaBackdrop();

        var content = new StackPanel { Spacing = 0, Padding = new Thickness(24, 20, 24, 20) };
        content.Children.Add(BuildAccountSection());
        content.Children.Add(Divider());
        content.Children.Add(BuildConnectionSection());
        content.Children.Add(Divider());
        content.Children.Add(BuildNotificationsSection());
        content.Children.Add(Divider());
        content.Children.Add(BuildStartupSection());
        content.Children.Add(Divider());
        content.Children.Add(BuildAppSection());

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = content,
        };

        var root = new Grid { RequestedTheme = ElementTheme.Dark };
        root.Children.Add(scroll);
        Content = root;
    }

    private FrameworkElement BuildAccountSection()
    {
        var section = NewSection("ACCOUNT");
        section.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(_session?.DisplayName) ? "Not signed in" : _session!.DisplayName,
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 8, 0, 2),
        });
        if (!string.IsNullOrWhiteSpace(_session?.Email))
            section.Children.Add(new TextBlock { Text = _session!.Email, Foreground = TextMuted, FontSize = 12 });

        var isProd = _settings.Environment == ServerEnvironment.Production;
        section.Children.Add(new Border
        {
            Background = isProd ? GreenColor : AmberColor,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 3, 8, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 10),
            Child = new TextBlock
            {
                Text = isProd ? "Production" : "Development",
                Foreground = new SolidColorBrush(Colors.White),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
            },
        });

        var signOut = new Button
        {
            Content = "Sign Out",
            Foreground = DangerColor,
            BorderBrush = DangerColor,
            BorderThickness = new Thickness(1),
        };
        signOut.Click += (_, _) =>
        {
            Close();
            _onSignOut();
        };
        section.Children.Add(signOut);
        return section;
    }

    private FrameworkElement BuildConnectionSection()
    {
        var section = NewSection("CONNECTION");
        section.Children.Add(new TextBlock { Text = "Server Environment", Margin = new Thickness(0, 8, 0, 6) });

        var prodRadio = new RadioButton { Content = "Production", GroupName = "env", IsChecked = _settings.Environment == ServerEnvironment.Production };
        var devRadio = new RadioButton { Content = "Development", GroupName = "env", IsChecked = _settings.Environment == ServerEnvironment.Development };
        var segPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        segPanel.Children.Add(prodRadio);
        segPanel.Children.Add(devRadio);
        section.Children.Add(segPanel);

        _envWarning = new TextBlock
        {
            Text = "Development server — for testing only",
            Foreground = AmberColor,
            FontSize = 12,
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = _settings.Environment == ServerEnvironment.Development ? Visibility.Visible : Visibility.Collapsed,
        };
        _envRestartNotice = new TextBlock
        {
            Text = "Server change will take effect after signing out and back in.",
            Foreground = TextMuted,
            FontStyle = global::Windows.UI.Text.FontStyle.Italic,
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        section.Children.Add(_envWarning);
        section.Children.Add(_envRestartNotice);

        prodRadio.Checked += (_, _) => SetEnvironment(ServerEnvironment.Production);
        devRadio.Checked += (_, _) => SetEnvironment(ServerEnvironment.Development);
        return section;
    }

    private FrameworkElement BuildNotificationsSection()
    {
        var section = NewSection("NOTIFICATIONS");

        var soundToggle = new ToggleSwitch
        {
            Header = "Sound",
            IsOn = _settings.SoundEnabled,
            Margin = new Thickness(0, 8, 0, 4),
        };
        soundToggle.Toggled += (_, _) =>
        {
            _settings.SoundEnabled = soundToggle.IsOn;
            SettingsManager.Save(_settings);
        };
        section.Children.Add(soundToggle);

        section.Children.Add(new TextBlock { Text = "Display mode", Margin = new Thickness(0, 12, 0, 6) });
        var immediateRadio = new RadioButton { Content = "Show immediately", GroupName = "display", IsChecked = !_settings.DeferToToast };
        var deferRadio = new RadioButton { Content = "Defer to toast", GroupName = "display", IsChecked = _settings.DeferToToast };
        var displayPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        displayPanel.Children.Add(immediateRadio);
        displayPanel.Children.Add(deferRadio);
        section.Children.Add(displayPanel);

        _displayModeDesc = new TextBlock { Foreground = TextMuted, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        section.Children.Add(_displayModeDesc);
        UpdateDisplayModeDesc();

        immediateRadio.Checked += (_, _) => SetDisplayMode(false);
        deferRadio.Checked += (_, _) => SetDisplayMode(true);

        section.Children.Add(new TextBlock { Text = "Location", Margin = new Thickness(0, 12, 0, 6) });
        // Items are added in NotificationAnchor declaration order so the
        // selected index maps directly to the enum value.
        var locationCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 180,
        };
        locationCombo.Items.Add("Bottom right");
        locationCombo.Items.Add("Bottom left");
        locationCombo.Items.Add("Top right");
        locationCombo.Items.Add("Top left");
        locationCombo.Items.Add("Middle right");
        locationCombo.Items.Add("Middle left");
        locationCombo.SelectedIndex = (int)_settings.NotificationLocation;
        locationCombo.SelectionChanged += (_, _) =>
        {
            if (locationCombo.SelectedIndex < 0) return;
            SetNotificationLocation((NotificationAnchor)locationCombo.SelectedIndex);
        };
        section.Children.Add(locationCombo);

        return section;
    }

    private FrameworkElement BuildStartupSection()
    {
        var section = NewSection("STARTUP");
        var toggle = new ToggleSwitch
        {
            Header = "Launch on Windows startup",
            IsOn = _settings.LaunchOnStartup,
            Margin = new Thickness(0, 8, 0, 4),
        };
        toggle.Toggled += (_, _) =>
        {
            _settings.LaunchOnStartup = toggle.IsOn;
            SettingsManager.Save(_settings);
            SettingsManager.SetLaunchOnStartup(toggle.IsOn);
        };
        section.Children.Add(toggle);
        return section;
    }

    private FrameworkElement BuildAppSection()
    {
        var section = NewSection("APP");

        var versionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 8, 0, 4) };
        versionRow.Children.Add(new TextBlock { Text = "Version", Width = 110 });
        versionRow.Children.Add(new TextBlock { Text = AppVersionString(), Foreground = TextMuted });
        section.Children.Add(versionRow);

        section.Children.Add(new TextBlock { Text = "App data folder", Margin = new Thickness(0, 8, 0, 2) });
        section.Children.Add(new TextBlock
        {
            Text = SettingsManager.GetAppDataPath(),
            Foreground = TextMuted,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        });

        var whatsNew = new Button { Content = "What's New", Margin = new Thickness(0, 4, 0, 4) };
        whatsNew.Click += (_, _) =>
        {
            try { new ChangelogWindow().Activate(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Settings] Open changelog failed: {ex.Message}"); }
        };
        section.Children.Add(whatsNew);

        var openFolder = new Button { Content = "Open app data folder", Margin = new Thickness(0, 4, 0, 4) };
        openFolder.Click += (_, _) => OpenAppDataFolder();
        section.Children.Add(openFolder);

        var debugBtn = new Button
        {
            Content = "Debug Log",
            Margin = new Thickness(0, 16, 0, 0),
            Padding = new Thickness(8, 3, 8, 3),
            Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 60, 60, 80)),
            BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 50, 50, 70)),
            BorderThickness = new Thickness(1),
            FontSize = 11,
        };
        debugBtn.Click += (_, _) => _onOpenDebugLog();
        section.Children.Add(debugBtn);

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

    private static StackPanel NewSection(string title)
    {
        var section = new StackPanel { Spacing = 0, Margin = new Thickness(0, 4, 0, 4) };
        section.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = TextMuted,
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 4),
        });
        return section;
    }

    private static Border Divider() => new()
    {
        Height = 1,
        Background = new SolidColorBrush(ColorHelper.FromArgb(255, 55, 55, 55)),
        Margin = new Thickness(0, 12, 0, 12),
    };

    private void SetEnvironment(ServerEnvironment env)
    {
        if (_settings.Environment == env) return;
        _settings.Environment = env;
        SettingsManager.Save(_settings);
        if (_envWarning != null) _envWarning.Visibility = env == ServerEnvironment.Development ? Visibility.Visible : Visibility.Collapsed;
        if (_envRestartNotice != null) _envRestartNotice.Visibility = Visibility.Visible;
    }

    private void UpdateDisplayModeDesc()
    {
        if (_displayModeDesc == null) return;
        _displayModeDesc.Text = _settings.DeferToToast
            ? "Defer to toast: Toast appears first — click to open full notification."
            : "Show immediately: Notification windows open automatically.";
    }

    private void SetDisplayMode(bool deferToToast)
    {
        if (_settings.DeferToToast == deferToToast) return;
        _settings.DeferToToast = deferToToast;
        SettingsManager.Save(_settings);
        UpdateDisplayModeDesc();
    }

    private void SetNotificationLocation(NotificationAnchor anchor)
    {
        if (_settings.NotificationLocation == anchor) return;
        _settings.NotificationLocation = anchor;
        SettingsManager.Save(_settings);
        // Apply to the live process and re-settle any open toasts immediately.
        ToastLayout.Anchor = anchor;
        SlideoutWindow.LayoutStack();
    }
}
