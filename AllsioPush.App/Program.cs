using AllsioPush.Config;
using AllsioPush.Models;
using AllsioPush.Services;
using AllsioPush.UI.Tray;
using AllsioPush.UI.Windows;

namespace AllsioPush;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        WindowsFormsSynchronizationContext.AutoInstall = false;
        var uiContext = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(uiContext);

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

        if (args.Length > 0 && args[0].StartsWith("allsio-push://", StringComparison.OrdinalIgnoreCase))
        {
            var captured = ExtractToken(args[0]);
            System.Diagnostics.Debug.WriteLine($"[App] URI-scheme launch captured token (len={captured?.Length ?? 0})");
        }

        using var tray = new TrayManager(settings);
        PusherService? pusher = null;
        LoginWindow? loginWindow = null;
        SettingsWindow? settingsWindow = null;
        HistoryWindow? historyWindow = null;

        var router = new NotificationRouter(settings, uiContext, toastService, ackService, windowTracker, historyService);

        // History entry persisted → push to open HistoryWindow live
        router.OnEntrySaved += entry =>
        {
            uiContext.Post(_ => historyWindow?.AddEntry(entry), null);
        };

        // AckService recorded an action → update HistoryWindow card
        ackService.OnActionRecorded += (id, action, by) =>
        {
            uiContext.Post(_ => historyWindow?.UpdateEntryAction(id, action, by), null);
        };

        // Toast activation handler: must be registered before Application.Run.
        // When DeferToToast is on, clicking the toast body re-routes the original notification
        // through the router (which will then open the appropriate window).
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
                // Persist remote ack to history too, so the card reflects it after restart.
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
            loginWindow.OnLoginSuccess += async (s) =>
            {
                CredentialManager.SaveSession(s);
                session = s;
                ackService.SetSession(s);
                loginWindow?.Close();
                loginWindow = null;
                await ConnectPusher(s);
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

        tray.OnOpenSettings += (s, e) => ShowSettings();
        tray.OnOpenHistory += (s, e) => ShowHistory();
        tray.OnSignOut += (s, e) => DoSignOut();

        tray.OnExit += (s, e) =>
        {
            pusher?.Dispose();
            Application.Exit();
        };

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

        // Prune history after a 10s grace period so it doesn't slow startup.
        var pruneTimer = new System.Threading.Timer(_ =>
        {
            _ = historyService.PruneOldEntries();
        }, null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);

        if (session != null)
        {
            _ = Task.Run(async () => await ConnectPusher(session));
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

    private static string? ExtractToken(string uri)
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
