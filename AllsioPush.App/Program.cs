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
        AuthSession? session = CredentialManager.LoadSession();
        var authService = new AuthService(settings);

        // If launched via allsio-push://auth?token=xxx from a fresh process, capture it.
        // The login window owns the WebView2 flow; this branch is reserved for a future phase
        // where the token can be exchanged directly without opening WebView2.
        if (args.Length > 0 && args[0].StartsWith("allsio-push://", StringComparison.OrdinalIgnoreCase))
        {
            var captured = ExtractToken(args[0]);
            System.Diagnostics.Debug.WriteLine($"[App] URI-scheme launch captured token (len={captured?.Length ?? 0})");
        }

        using var tray = new TrayManager(settings);
        PusherService? pusher = null;
        LoginWindow? loginWindow = null;

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
                PostToUi(() => tray.ShowBalloon(notification.Title, notification.Content));
            };
            pusher.OnAcknowledgementReceived += (notificationId, acknowledgedBy) =>
            {
                System.Diagnostics.Debug.WriteLine($"[App] Remote ack: {notificationId} by {acknowledgedBy}");
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
                loginWindow?.Close();
                loginWindow = null;
                await ConnectPusher(s);
            };
            loginWindow.FormClosed += (s, e) => loginWindow = null;
            loginWindow.Show();
        }

        tray.OnOpenSettings += (s, e) =>
            MessageBox.Show("Settings coming soon", "Allsio Push");

        tray.OnOpenHistory += (s, e) =>
            MessageBox.Show("History coming soon", "Allsio Push");

        tray.OnSignOut += async (s, e) =>
        {
            if (session != null)
                await authService.Logout(session.Token);
            pusher?.Dispose();
            pusher = null;
            CredentialManager.ClearSession();
            session = null;
            tray.SetConnected(false);
            ShowLogin();
        };

        tray.OnExit += (s, e) =>
        {
            pusher?.Dispose();
            Application.Exit();
        };

        // Heartbeat timer — every 60 seconds. Clears session on 401.
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
                    tray.SetConnected(false);
                    ShowLogin();
                });
            }
        }, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));

        if (session != null)
        {
            _ = Task.Run(async () => await ConnectPusher(session));
        }
        else
        {
            ShowLogin();
        }

        Application.Run();

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
