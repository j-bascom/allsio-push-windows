using AllsioPush.Config;
using AllsioPush.Services;
using AllsioPush.UI.Tray;

namespace AllsioPush;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        var settings = SettingsManager.Load();
        var session = CredentialManager.LoadSession();

        using var tray = new TrayManager(settings);

        tray.OnOpenSettings += (s, e) =>
            MessageBox.Show("Settings coming soon", "Allsio Push");

        tray.OnOpenHistory += (s, e) =>
            MessageBox.Show("History coming soon", "Allsio Push");

        tray.OnSignOut += (s, e) =>
        {
            CredentialManager.ClearSession();
            MessageBox.Show("Signed out", "Allsio Push");
        };

        tray.OnExit += (s, e) =>
        {
            Application.Exit();
        };

        tray.SetConnected(session != null);

        if (session == null)
            tray.ShowBalloon("Allsio Push", "Not signed in. Open Settings to connect.");

        Application.Run();
    }
}
