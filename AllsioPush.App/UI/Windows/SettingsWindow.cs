using System.Reflection;
using AllsioPush.Config;
using AllsioPush.Models;
using AllsioPush.Services;
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
    private static readonly SolidColorBrush DangerColor = new(ColorHelper.FromArgb(255, 220, 38, 38));

    private readonly AppSettings _settings;
    private readonly AuthSession? _session;
    private readonly Action _onSignOut;
    private readonly Action _onSignIn;
    private readonly Action _onOpenDebugLog;

    private Border? _devBadge;
    private TextBlock? _envNotice;
    private TextBlock? _displayModeDesc;

    // Hidden environment switch: tap the version number 10 times in a row.
    // There is no visible Production/Development control any more — the app is
    // a production app, and pointing it at dev is a support/debug gesture.
    private const int EnvTapsRequired = 10;
    private static readonly TimeSpan EnvTapWindow = TimeSpan.FromSeconds(2);
    private int _envTapCount;
    private DateTime _lastEnvTapUtc = DateTime.MinValue;

    public SettingsWindow(AppSettings settings, AuthSession? session, Action onSignOut, Action onSignIn, Action onOpenDebugLog)
    {
        _settings = settings;
        _session = session;
        _onSignOut = onSignOut;
        _onSignIn = onSignIn;
        _onOpenDebugLog = onOpenDebugLog;

        Title = "Allsio Push — Settings";
        this.SetWindowSize(480, 600);
        this.CenterOnScreen();
        WindowIcon.Apply(this);
        SystemBackdrop = new MicaBackdrop();

        var content = new StackPanel { Spacing = 0, Padding = new Thickness(24, 20, 24, 20) };
        content.Children.Add(BuildAccountSection());
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

        // Only Development is badged. Production is the normal state and needs
        // no ornament; a badge there just added noise to every user's settings.
        _devBadge = new Border
        {
            Background = AmberColor,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 3, 8, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 10),
            Visibility = _settings.Environment == ServerEnvironment.Development
                ? Visibility.Visible : Visibility.Collapsed,
            Child = new TextBlock
            {
                Text = "Development",
                Foreground = new SolidColorBrush(Colors.White),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
            },
        };
        section.Children.Add(_devBadge);

        // Signed out, this window is reachable from the tray, so the button has
        // to offer the way back IN rather than a dead "Sign Out". Only the
        // destructive action wears the danger styling.
        var signedIn = _session != null;
        var authButton = new Button
        {
            Content = signedIn ? "Sign Out" : "Sign In",
            Margin = new Thickness(0, 6, 0, 0),
        };
        if (signedIn)
        {
            authButton.Foreground = DangerColor;
            authButton.BorderBrush = DangerColor;
            authButton.BorderThickness = new Thickness(1);
        }
        authButton.Click += (_, _) =>
        {
            Close();
            if (signedIn) _onSignOut();
            else _onSignIn();
        };
        section.Children.Add(authButton);
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

        section.Children.Add(new TextBlock { Text = "Animation", Margin = new Thickness(0, 12, 0, 6) });
        // Items follow NotificationAnimation declaration order so the selected
        // index maps straight to the enum value, same as Location above.
        var animCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 180,
        };
        animCombo.Items.Add("Slide in");
        animCombo.Items.Add("Fade in");
        animCombo.SelectedIndex = (int)_settings.NotificationAnimation;
        animCombo.SelectionChanged += (_, _) =>
        {
            if (animCombo.SelectedIndex < 0) return;
            SetNotificationAnimation((NotificationAnimation)animCombo.SelectedIndex);
        };
        section.Children.Add(animCombo);

        section.Children.Add(new TextBlock { Text = "Text message timeout", Margin = new Thickness(0, 12, 0, 6) });
        var smsTtlCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 180,
        };
        foreach (var (label, _) in SmsTtlChoices) smsTtlCombo.Items.Add(label);
        // An unrecognised stored value (hand-edited settings.json) falls back to
        // "Stay until dismissed" rather than silently snapping to some duration.
        var ttlIndex = Array.FindIndex(SmsTtlChoices, c => c.Seconds == _settings.SmsTtlSeconds);
        smsTtlCombo.SelectedIndex = ttlIndex >= 0 ? ttlIndex : 0;
        smsTtlCombo.SelectionChanged += (_, _) =>
        {
            if (smsTtlCombo.SelectedIndex < 0) return;
            SetSmsTtl(SmsTtlChoices[smsTtlCombo.SelectedIndex].Seconds);
        };
        section.Children.Add(smsTtlCombo);
        section.Children.Add(new TextBlock
        {
            Text = "How long a text message stays on screen. The countdown pauses "
                 + "while you're hovering over it or writing a reply.",
            Foreground = TextMuted,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });

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
        // Wrapped in a Border with an explicit Transparent background: a bare
        // TextBlock only hit-tests on its glyphs, so taps landing between digits
        // would silently not count. Transparent (not null) still hit-tests.
        // Deliberately undecorated — this reads as plain text, which is the point.
        var versionTap = new Border
        {
            Background = new SolidColorBrush(Colors.Transparent),
            Padding = new Thickness(2, 2, 8, 2),
            Child = new TextBlock { Text = AppVersionString(), Foreground = TextMuted },
        };
        versionTap.Tapped += (_, _) => OnVersionTapped();
        versionRow.Children.Add(versionTap);
        section.Children.Add(versionRow);

        _envNotice = new TextBlock
        {
            Foreground = AmberColor,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
            Visibility = Visibility.Collapsed,
        };
        section.Children.Add(_envNotice);

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

    /// <summary>
    /// Counts taps on the version number. Ten in a row — each within
    /// <see cref="EnvTapWindow"/> of the last, so ordinary stray clicks never
    /// accumulate — toggles the server environment. It toggles rather than only
    /// switching to Development so there is a way back without hand-editing
    /// settings.json.
    /// </summary>
    private void OnVersionTapped()
    {
        var now = DateTime.UtcNow;
        if (now - _lastEnvTapUtc > EnvTapWindow) _envTapCount = 0;
        _lastEnvTapUtc = now;
        _envTapCount++;
        if (_envTapCount < EnvTapsRequired) return;

        _envTapCount = 0;
        ToggleEnvironment();
    }

    private void ToggleEnvironment()
    {
        var target = _settings.Environment == ServerEnvironment.Production
            ? ServerEnvironment.Development
            : ServerEnvironment.Production;

        _settings.Environment = target;
        SettingsManager.Save(_settings);
        DebugLog.Write("Settings", $"Environment switched to {target} via the version tap gesture.");

        var isDev = target == ServerEnvironment.Development;
        if (_devBadge != null)
            _devBadge.Visibility = isDev ? Visibility.Visible : Visibility.Collapsed;
        if (_envNotice != null)
        {
            _envNotice.Text = (isDev
                ? "Switched to the Development server — for testing only."
                : "Switched back to the Production server.")
                + " Sign out and back in for this to take effect.";
            _envNotice.Visibility = Visibility.Visible;
        }
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

    // 0 = stay until dismissed, matching AppSettings.SmsTtlSeconds's default.
    private static readonly (string Label, int Seconds)[] SmsTtlChoices =
    {
        ("Stay until dismissed", 0),
        ("5 seconds", 5),
        ("10 seconds", 10),
        ("30 seconds", 30),
        ("1 minute", 60),
        ("2 minutes", 120),
        ("5 minutes", 300),
        ("10 minutes", 600),
        ("30 minutes", 1800),
    };

    private void SetSmsTtl(int seconds)
    {
        if (_settings.SmsTtlSeconds == seconds) return;
        _settings.SmsTtlSeconds = seconds;
        SettingsManager.Save(_settings);
        DebugLog.Write("Settings", $"SMS timeout set to {seconds}s.");
        // Applies to the next SMS that arrives; open slideouts keep the timing
        // they started with rather than having a bar appear or vanish on them.
    }

    private void SetNotificationAnimation(NotificationAnimation animation)
    {
        if (_settings.NotificationAnimation == animation) return;
        _settings.NotificationAnimation = animation;
        SettingsManager.Save(_settings);
        // Live for the next notification, like the anchor above.
        ToastLayout.Animation = animation;
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
