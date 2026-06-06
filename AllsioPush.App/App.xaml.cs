using AllsioPush.Config;
using AllsioPush.Models;
using AllsioPush.Services;
using AllsioPush.UI;
using AllsioPush.UI.Tray;
using AllsioPush.UI.Windows;
using Microsoft.UI.Xaml;

namespace AllsioPush;

public partial class App : Application
{
    private SynchronizationContext _uiContext = null!;
    private AppSettings _settings = null!;
    private AuthSession? _session;
    private AuthService _authService = null!;
    private HistoryService _historyService = null!;
    private AckService _ackService = null!;
    private WindowTracker _windowTracker = null!;
    private ToastService _toastService = null!;
    private NotificationRouter _router = null!;
    private UpdateService _updateService = null!;
    private SupervisedTransferService _transferService = null!;
    private TrayManager _tray = null!;
    private SingleInstanceService _singleInstance = null!;
    private PusherService? _pusher;

    private Window? _hostWindow;
    private LoginWindow? _loginWindow;
    private SettingsWindow? _settingsWindow;
    private HistoryWindow? _historyWindow;

    private System.Threading.Timer? _heartbeatTimer;
    private System.Threading.Timer? _pruneTimer;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            Log("UnhandledException", e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    internal static void Log(string context, Exception? ex)
    {
        try
        {
            var dir = SettingsManager.GetAppDataPath();
            Directory.CreateDirectory(dir);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}: {ex}\n";
            File.AppendAllText(Path.Combine(dir, "error.log"), line);
        }
        catch { }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _uiContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("No UI SynchronizationContext.");

        // WinUI terminates the process when the last window closes. This app lives
        // in the tray with no persistent visible window, so keep a hidden window
        // open for the whole lifetime to anchor the message loop.
        _hostWindow = new Window();
        _hostWindow.AppWindow.IsShownInSwitchers = false;
        _hostWindow.AppWindow.Hide();

        var cmdline = Environment.GetCommandLineArgs().Skip(1).ToArray();

        _singleInstance = new SingleInstanceService(_uiContext);
        if (!_singleInstance.IsFirstInstance)
        {
            SingleInstanceService.SendArgsToRunningInstance(cmdline);
            _singleInstance.Dispose();
            Exit();
            return;
        }
        _singleInstance.StartPipeServer();

        RunFirstInstance(cmdline);
    }

    private void RunFirstInstance(string[] cmdline)
    {
        SettingsManager.RegisterUriScheme();
        _settings = SettingsManager.Load();
        SettingsManager.SetLaunchOnStartup(_settings.LaunchOnStartup);

        _session = CredentialManager.LoadSession();
        _authService = new AuthService(_settings);
        _historyService = new HistoryService();
        _ackService = new AckService(_settings, _historyService);
        if (_session != null) _ackService.SetSession(_session);

        _windowTracker = new WindowTracker();
        _toastService = new ToastService(_settings, _ackService, _uiContext);
        _transferService = new SupervisedTransferService(_settings, _ackService);

        var startupToken = ExtractToken(cmdline);

        _tray = new TrayManager(_settings);

        var presenter = new UiPresenter(_ackService, _windowTracker, _settings, _uiContext);
        _router = new NotificationRouter(_settings, _uiContext, _toastService, _ackService, _windowTracker, _historyService, presenter);

        _router.OnEntrySaved += entry =>
            _uiContext.Post(_ => _historyWindow?.AddEntry(entry), null);

        _ackService.OnActionRecorded += (id, action, by) =>
            _uiContext.Post(_ => _historyWindow?.UpdateEntryAction(id, action, by), null);

        _toastService.RegisterActivationHandler(notification =>
        {
            var copy = notification;
            if (copy.DisplayMode == "popup" || copy.TemplateType == "custom_html"
                || copy.TemplateType == "url_popup")
            {
                _uiContext.Post(_ => presenter.ShowPopup(copy), null);
            }
            else if (copy.TemplateType == "url_tab" && !string.IsNullOrWhiteSpace(copy.Url))
            {
                _uiContext.Post(_ => presenter.OpenUrl(copy.Url!), null);
            }
            else
            {
                _uiContext.Post(_ => presenter.ShowSlideout(copy), null);
            }
        });

        _toastService.OnTransferAction += (action, connectionId) =>
        {
            if (action == "transfer_accept")
                _transferService.AcceptTransfer(connectionId);
            else
                _transferService.DeclineTransfer(connectionId);
        };

        _singleInstance.OnSecondInstanceArgs += incomingArgs =>
        {
            var token = ExtractToken(incomingArgs);
            if (!string.IsNullOrWhiteSpace(token)) HandleIncomingToken(token!);
            _tray.ShowBalloon("Allsio Push", "Already running.");
        };

        _tray.OnOpenSettings += (_, _) => ShowSettings();
        _tray.OnOpenHistory += (_, _) => ShowHistory();
        _tray.OnSignIn += (_, _) => ShowLogin();
        _tray.OnSignOut += (_, _) => DoSignOut();
        _tray.OnExit += (_, _) => DoExit();

        _updateService = new UpdateService();
        _tray.OnCheckForUpdates += async (_, _) =>
        {
            if (!_updateService.IsInstalled)
            {
                _tray.ShowBalloon("Allsio Push",
                    "Update checks are only available in the installed version.");
                return;
            }
            var toast = new UpdateToastWindow();
            toast.Activate();
            try
            {
                await _updateService.CheckAndApplyUpdates(
                    onStatus: toast.SetMessage,
                    onProgress: toast.SetProgress);
            }
            finally
            {
                // Reached only when no update was found (restart case kills the process).
                toast.HideProgress();
                toast.AutoClose(4000);
            }
        };

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            while (true)
            {
                if (_updateService.IsInstalled)
                    await _updateService.CheckAndApplyUpdates();
                await Task.Delay(TimeSpan.FromHours(4));
            }
        });

        _heartbeatTimer = new System.Threading.Timer(async _ =>
        {
            var current = _session;
            if (current == null) return;
            var ok = await _authService.SendHeartbeat(current.Token);
            if (!ok) _uiContext.Post(_ => ForceSignOut(), null);
        }, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));

        _pruneTimer = new System.Threading.Timer(_ =>
            _ = _historyService.PruneOldEntries(),
            null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);

        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _tray.SetAuthState(_session != null);

        if (_session != null)
        {
            _ = Task.Run(async () => await ConnectPusher(_session));
        }
        else if (!string.IsNullOrWhiteSpace(startupToken))
        {
            HandleIncomingToken(startupToken!);
        }
        else
        {
            ShowLogin();
        }
    }

    private async Task ConnectPusher(AuthSession s)
    {
        _pusher?.Dispose();
        _pusher = new PusherService(s, _settings);
        _pusher.OnConnectionStateChanged += connected =>
            _uiContext.Post(_ => _tray.SetConnected(connected), null);
        _pusher.OnChannelsChanged += channels =>
            _uiContext.Post(_ =>
            {
                var session = _session;
                var groups = session?.PushGroups ?? [];
                var personal = session?.PersonalChannel ?? string.Empty;
                var displayNames = channels
                    .Select(c =>
                    {
                        if (c == personal)
                            return string.IsNullOrWhiteSpace(session?.DisplayName)
                                ? "Personal" : session.DisplayName;
                        return groups.FirstOrDefault(g => g.PusherChannel == c)?.Name ?? c;
                    })
                    .ToArray();
                _tray.UpdateChannels(displayNames);
            }, null);
        _pusher.OnChannelsUpdated += _ =>
        {
            if (_session != null) CredentialManager.SaveSession(_session);
        };
        _pusher.OnNotificationReceived += notification =>
            _uiContext.Post(_ => _router.Route(notification), null);
        _pusher.OnAcknowledgementReceived += (notificationId, acknowledgedBy) =>
        {
            _ = _historyService.RecordAction(notificationId, "acknowledged", acknowledgedBy);
            _uiContext.Post(_ =>
            {
                _windowTracker.BroadcastRemoteAck(notificationId, acknowledgedBy);
                _toastService.UpdateRemoteAck(notificationId, acknowledgedBy);
                _historyWindow?.UpdateEntryAction(notificationId, "acknowledged", acknowledgedBy);
            }, null);
        };
        _pusher.OnSupervisedTransfer += req => _transferService.HandleTransferRequest(req);
        _pusher.OnSupervisedTransferCancelled += id => _transferService.HandleCancellation(id);
        await _pusher.ConnectAsync();
    }

    private void ShowLogin()
    {
        if (_loginWindow != null)
        {
            _loginWindow.Activate();
            return;
        }
        try
        {
        _loginWindow = new LoginWindow(_settings, _authService);
        _loginWindow.OnLoginSuccess += s =>
        {
            CredentialManager.SaveSession(s);
            _session = s;
            _ackService.SetSession(s);
            _loginWindow?.Close();
            _loginWindow = null;
            _tray.SetAuthState(true);
            _ = ConnectPusher(s);
        };
        _loginWindow.Closed += (_, _) => _loginWindow = null;
        _loginWindow.Activate();
        }
        catch (Exception ex) { Log("ShowLogin", ex); }
    }

    private void ShowSettings()
    {
        try
        {
            if (_settingsWindow != null)
            {
                _settingsWindow.Activate();
                return;
            }
            _settingsWindow = new SettingsWindow(_settings, _session, DoSignOut);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Activate();
        }
        catch (Exception ex) { Log("ShowSettings", ex); }
    }

    private void ShowHistory()
    {
        try
        {
            if (_historyWindow != null)
            {
                _historyWindow.PositionBottomRight();
                _historyWindow.Activate();
                return;
            }
            _historyWindow = new HistoryWindow(_historyService, _router, _settings, () => _session, _uiContext);
            _historyWindow.Closed += (_, _) => _historyWindow = null;
            _historyWindow.Activate();
        }
        catch (Exception ex) { Log("ShowHistory", ex); }
    }

    private async void DoSignOut()
    {
        if (_session != null)
        {
            try { await _authService.Logout(_session.Token); } catch { }
        }
        ForceSignOut();
    }

    private void ForceSignOut()
    {
        _pusher?.Dispose();
        _pusher = null;
        CredentialManager.ClearSession();
        _session = null;
        _ackService.SetSession(null);
        _tray.SetAuthState(false);
        _tray.SetConnected(false);
        _tray.UpdateChannels(Array.Empty<string>());
        _settingsWindow?.Close();
        _historyWindow?.Close();
        ShowLogin();
    }

    private void HandleIncomingToken(string token)
    {
        if (_session != null)
        {
            System.Diagnostics.Debug.WriteLine("[App] Incoming token ignored — already signed in.");
            return;
        }
        _ = Task.Run(async () =>
        {
            var newSession = await _authService.ExchangeToken(token);
            if (newSession == null)
            {
                System.Diagnostics.Debug.WriteLine("[App] Incoming token exchange failed.");
                return;
            }
            _uiContext.Post(_ =>
            {
                CredentialManager.SaveSession(newSession);
                _session = newSession;
                _ackService.SetSession(newSession);
                _loginWindow?.Close();
                _loginWindow = null;
                _tray.SetAuthState(true);
                _ = ConnectPusher(newSession);
                _tray.ShowBalloon("Allsio Push", "Signed in.");
            }, null);
        });
    }

    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;
        System.Diagnostics.Debug.WriteLine("[App] System resumed from sleep — scheduling Pusher reconnect");
        _ = Task.Run(async () =>
        {
            // Give the network stack a few seconds to come back up after wake.
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var session = _session;
            if (session == null) return;
            _uiContext.Post(_ => _ = ConnectPusher(session), null);
        });
    }

    private void DoExit()
    {
        try { Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }
        try { _heartbeatTimer?.Dispose(); } catch { }
        try { _pruneTimer?.Dispose(); } catch { }
        try { _pusher?.Dispose(); } catch { }
        try { _tray?.Dispose(); } catch { }
        try { _singleInstance?.Dispose(); } catch { }
        try { _hostWindow?.Close(); } catch { }
        Exit();
    }

    private static string? ExtractToken(string[] args)
    {
        if (args == null) return null;
        foreach (var arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg)) continue;
            if (arg.StartsWith("allsio-push://", StringComparison.OrdinalIgnoreCase))
            {
                var t = TokenFromUri(arg);
                if (!string.IsNullOrWhiteSpace(t)) return t;
            }
            else if (arg.StartsWith("--token=", StringComparison.OrdinalIgnoreCase))
            {
                var v = arg.Substring("--token=".Length);
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
        }
        return null;
    }

    private static string? TokenFromUri(string uri)
    {
        try
        {
            var u = new Uri(uri);
            var q = u.Query.StartsWith('?') ? u.Query[1..] : u.Query;
            foreach (var pair in q.Split('&'))
            {
                var idx = pair.IndexOf('=');
                if (idx < 0) continue;
                if (string.Equals(Uri.UnescapeDataString(pair[..idx]), "token", StringComparison.OrdinalIgnoreCase))
                    return Uri.UnescapeDataString(pair[(idx + 1)..]);
            }
        }
        catch { }
        return null;
    }
}
