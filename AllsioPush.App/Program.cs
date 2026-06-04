using AllsioPush.Config;
using AllsioPush.Models;
using AllsioPush.Services;
using AllsioPush.UI.Tray;
using AllsioPush.UI.Windows;
using Velopack;

namespace AllsioPush;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Velopack must run before anything else — it handles install/update/
        // uninstall hooks and may exit the process for some of them.
        VelopackApp.Build()
            .OnFirstRun(v =>
            {
                // First run after install — nothing needed yet. The URI scheme
                // and startup key are handled by SettingsManager during normal startup.
            })
            .OnAfterInstallFastCallback(v =>
            {
                // After install complete — register URI scheme.
                SettingsManager.RegisterUriScheme();
            })
            .OnAfterUpdateFastCallback(v =>
            {
                // After update applied — re-register URI scheme in case the path changed.
                SettingsManager.RegisterUriScheme();
            })
            .OnBeforeUninstallFastCallback(v =>
            {
                // Clean up before uninstall.
                UninstallCleanup();
            })
            .Run();

        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        WindowsFormsSynchronizationContext.AutoInstall = false;
        var uiContext = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(uiContext);

        // Single-instance gate. Must run before any tray icon, window,
        // or HTTP service is constructed — second instances should exit cleanly.
        var singleInstance = new SingleInstanceService(uiContext);
        if (!singleInstance.IsFirstInstance)
        {
            SingleInstanceService.SendArgsToRunningInstance(args);
            singleInstance.Dispose();
            return;
        }

        try
        {
            singleInstance.StartPipeServer();
            RunFirstInstance(args, uiContext, singleInstance);
        }
        finally
        {
            singleInstance.Dispose();
        }
    }

    private static void RunFirstInstance(
        string[] args,
        WindowsFormsSynchronizationContext uiContext,
        SingleInstanceService singleInstance)
    {
        SettingsManager.RegisterUriScheme();
        var settings = SettingsManager.Load();
        SettingsManager.SetLaunchOnStartup(settings.LaunchOnStartup);

        AuthSession? session = CredentialManager.LoadSession();
        var authService = new AuthService(settings);
        var historyService = new HistoryService();

        var ackService = new AckService(settings, historyService);
        if (session != null) ackService.SetSession(session);

        var windowTracker = new WindowTracker();
        var toastService = new ToastService(settings, ackService, uiContext);

        // Capture any token passed via this process's own args (URI launch).
        var startupToken = ExtractToken(args);

        using var tray = new TrayManager(settings);
        PusherService? pusher = null;
        LoginWindow? loginWindow = null;
        SettingsWindow? settingsWindow = null;
        HistoryWindow? historyWindow = null;

        var router = new NotificationRouter(settings, uiContext, toastService, ackService, windowTracker, historyService);

        router.OnEntrySaved += entry =>
        {
            uiContext.Post(_ => historyWindow?.AddEntry(entry), null);
        };

        ackService.OnActionRecorded += (id, action, by) =>
        {
            uiContext.Post(_ => historyWindow?.UpdateEntryAction(id, action, by), null);
        };

        toastService.RegisterActivationHandler(notification =>
        {
            var copy = notification;
            if (copy.DisplayMode == "popup" || copy.TemplateType == "custom_html"
                || copy.TemplateType == "url_popup")
            {
                uiContext.Post(_ => new PopupWindow(copy, ackService, windowTracker).Show(), null);
            }
            else if (copy.TemplateType == "url_tab" && !string.IsNullOrWhiteSpace(copy.Url))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = copy.Url,
                        UseShellExecute = true,
                    });
                }
                catch { }
            }
            else
            {
                uiContext.Post(_ => new SlideoutWindow(copy, ackService, windowTracker).Show(), null);
            }
        });

        void PostToUi(Action action) => uiContext.Post(_ => action(), null);

        async Task ConnectPusher(AuthSession s)
        {
            pusher?.Dispose();
            pusher = new PusherService(s, settings);
            pusher.OnConnectionStateChanged += (connected) =>
            {
                PostToUi(() => tray.SetConnected(connected));
            };
            pusher.OnNotificationReceived += (notification) =>
            {
                uiContext.Post(_ => router.Route(notification), null);
            };
            pusher.OnAcknowledgementReceived += (notificationId, acknowledgedBy) =>
            {
                _ = historyService.RecordAction(notificationId, "acknowledged", acknowledgedBy);
                uiContext.Post(_ =>
                {
                    windowTracker.BroadcastRemoteAck(notificationId, acknowledgedBy);
                    toastService.UpdateRemoteAck(notificationId, acknowledgedBy);
                    historyWindow?.UpdateEntryAction(notificationId, "acknowledged", acknowledgedBy);
                }, null);
            };
            await pusher.ConnectAsync();
        }

        void ShowLogin()
        {
            if (loginWindow != null && !loginWindow.IsDisposed)
            {
                loginWindow.Activate();
                return;
            }

            loginWindow = new LoginWindow(settings, authService);
            loginWindow.OnLoginSuccess += (s) =>
            {
                CredentialManager.SaveSession(s);
                session = s;
                ackService.SetSession(s);
                loginWindow?.Close();
                loginWindow = null;
                _ = ConnectPusher(s);
            };
            loginWindow.FormClosed += (s, e) => loginWindow = null;
            loginWindow.Show();
        }

        void ShowSettings()
        {
            if (settingsWindow != null && !settingsWindow.IsDisposed)
            {
                settingsWindow.Activate();
                settingsWindow.BringToFront();
                return;
            }
            settingsWindow = new SettingsWindow(settings, session, () => DoSignOut());
            settingsWindow.FormClosed += (_, _) => settingsWindow = null;
            settingsWindow.Show();
        }

        void ShowHistory()
        {
            if (historyWindow != null && !historyWindow.IsDisposed)
            {
                historyWindow.Activate();
                historyWindow.BringToFront();
                return;
            }
            historyWindow = new HistoryWindow(historyService, router, settings, () => session, uiContext);
            historyWindow.FormClosed += (_, _) => historyWindow = null;
            historyWindow.Show();
        }

        async void DoSignOut()
        {
            if (session != null)
            {
                try { await authService.Logout(session.Token); } catch { }
            }
            pusher?.Dispose();
            pusher = null;
            CredentialManager.ClearSession();
            session = null;
            ackService.SetSession(null);
            tray.SetConnected(false);

            if (settingsWindow != null && !settingsWindow.IsDisposed) settingsWindow.Close();
            if (historyWindow != null && !historyWindow.IsDisposed) historyWindow.Close();

            ShowLogin();
        }

        void HandleIncomingToken(string token)
        {
            if (session != null)
            {
                System.Diagnostics.Debug.WriteLine("[App] Incoming token ignored — already signed in.");
                return;
            }

            _ = Task.Run(async () =>
            {
                var newSession = await authService.ExchangeToken(token);
                if (newSession == null)
                {
                    System.Diagnostics.Debug.WriteLine("[App] Incoming token exchange failed.");
                    return;
                }

                uiContext.Post(_ =>
                {
                    CredentialManager.SaveSession(newSession);
                    session = newSession;
                    ackService.SetSession(newSession);
                    if (loginWindow != null && !loginWindow.IsDisposed)
                    {
                        loginWindow.Close();
                    }
                    loginWindow = null;
                    _ = ConnectPusher(newSession);
                    tray.ShowBalloon("Allsio Push", "Signed in.");
                }, null);
            });
        }

        singleInstance.OnSecondInstanceArgs += incomingArgs =>
        {
            var token = ExtractToken(incomingArgs);
            if (!string.IsNullOrWhiteSpace(token))
            {
                HandleIncomingToken(token!);
            }
            // Also surface tray to user attention regardless.
            tray.ShowBalloon("Allsio Push", "Already running.");
        };

        tray.OnOpenSettings += (s, e) => ShowSettings();
        tray.OnOpenHistory += (s, e) => ShowHistory();
        tray.OnSignOut += (s, e) => DoSignOut();

        tray.OnExit += (s, e) =>
        {
            pusher?.Dispose();
            Application.Exit();
        };

        var updateService = new UpdateService();

        tray.OnCheckForUpdates += async (s, e) =>
        {
            if (!updateService.IsInstalled)
            {
                tray.ShowBalloon("Allsio Push",
                    "Update checks are only available in the installed version.");
                return;
            }
            tray.ShowBalloon("Allsio Push", "Checking for updates...");
            await updateService.CheckAndApplyUpdates(msg =>
                uiContext.Post(_ => tray.ShowBalloon("Allsio Push", msg), null));
        };

        // Background update loop: first check 30s after startup, then every 4 hours.
        // Skipped entirely when not installed (e.g. dotnet run in development).
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            while (true)
            {
                if (updateService.IsInstalled)
                    await updateService.CheckAndApplyUpdates();
                await Task.Delay(TimeSpan.FromHours(4));
            }
        });

        var heartbeatTimer = new System.Threading.Timer(async _ =>
        {
            var current = session;
            if (current == null) return;
            var ok = await authService.SendHeartbeat(current.Token);
            if (!ok)
            {
                PostToUi(() =>
                {
                    pusher?.Dispose();
                    pusher = null;
                    CredentialManager.ClearSession();
                    session = null;
                    ackService.SetSession(null);
                    tray.SetConnected(false);
                    if (settingsWindow != null && !settingsWindow.IsDisposed) settingsWindow.Close();
                    if (historyWindow != null && !historyWindow.IsDisposed) historyWindow.Close();
                    ShowLogin();
                });
            }
        }, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));

        var pruneTimer = new System.Threading.Timer(_ =>
        {
            _ = historyService.PruneOldEntries();
        }, null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);

        if (session != null)
        {
            _ = Task.Run(async () => await ConnectPusher(session));
        }
        else if (!string.IsNullOrWhiteSpace(startupToken))
        {
            HandleIncomingToken(startupToken!);
        }
        else
        {
            ShowLogin();
        }

        Application.Run();

        pruneTimer.Dispose();
        heartbeatTimer.Dispose();
        pusher?.Dispose();
    }

    private static void UninstallCleanup()
    {
        try
        {
            // Remove URI scheme registration
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(
                @"Software\Classes\allsio-push", throwOnMissingSubKey: false);

            // Remove startup registry entry
            using var runKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            runKey?.DeleteValue("AllsioPush", throwOnMissingValue: false);

            // Remove Windows Credential Manager entry
            CredentialManager.ClearSession();

            // No dialogs here — uninstall fast callback has no message loop.
            // %APPDATA% user data is intentionally left intact (standard convention).
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[Uninstall] Cleanup error: {ex.Message}");
        }
    }

    /// <summary>
    /// Extracts a token from process args. Supports two forms:
    ///  - allsio-push://auth?token=xxx  (URI scheme launch)
    ///  - --token=xxx                   (command-line form for testing)
    /// </summary>
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
