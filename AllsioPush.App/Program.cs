using System.Runtime.InteropServices;
using AllsioPush.Config;
using AllsioPush.Services;
using Microsoft.UI.Dispatching;
using Velopack;

namespace AllsioPush;

public static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Velopack must run before anything else — it handles install/update/
        // uninstall hooks and may exit the process for some of them.
        // Match the AUMID Velopack stamps on shortcuts so the running process
        // groups under the pinned taskbar button and inherits its icon.
        SetCurrentProcessExplicitAppUserModelID("velopack.AllsioPush");

        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => SettingsManager.RegisterUriScheme())
            .OnAfterUpdateFastCallback(_ => SettingsManager.RegisterUriScheme())
            .OnBeforeUninstallFastCallback(_ => UninstallCleanup())
            .Run();

        WinRT.ComWrappersSupport.InitializeComWrappers();

        Microsoft.UI.Xaml.Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    internal static void UninstallCleanup()
    {
        try
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(
                @"Software\Classes\allsio-push", throwOnMissingSubKey: false);

            using var runKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            runKey?.DeleteValue("AllsioPush", throwOnMissingValue: false);

            CredentialManager.ClearSession();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Uninstall] Cleanup error: {ex.Message}");
        }
    }
}
