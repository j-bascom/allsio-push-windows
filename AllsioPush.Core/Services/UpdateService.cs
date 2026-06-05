using Velopack;
using Velopack.Sources;

namespace AllsioPush.Services;

public class UpdateService
{
    private readonly UpdateManager _manager;
    private const string RepoUrl =
        "https://github.com/j-bascom/allsio-push-windows";

    public UpdateService()
    {
        _manager = new UpdateManager(
            new GithubSource(RepoUrl, null, false));
    }

    // Call this on startup after a 30s delay
    // and then every 4 hours
    public async Task CheckAndApplyUpdates(Action<string>? onStatus = null)
    {
        try
        {
            onStatus?.Invoke("Checking for updates...");
            var updateInfo = await _manager.CheckForUpdatesAsync();

            if (updateInfo == null)
            {
                System.Diagnostics.Debug.WriteLine("[Update] No updates available");
                onStatus?.Invoke("Allsio Push is up to date.");
                return;
            }

            onStatus?.Invoke($"Downloading update {updateInfo.TargetFullRelease.Version}...");
            System.Diagnostics.Debug.WriteLine(
                $"[Update] Downloading {updateInfo.TargetFullRelease.Version}");

            await _manager.DownloadUpdatesAsync(updateInfo);

            System.Diagnostics.Debug.WriteLine("[Update] Update downloaded — will apply on next restart");
            onStatus?.Invoke("Update ready — will install on next restart");

            // Apply the downloaded release and relaunch the app.
            _manager.ApplyUpdatesAndRestart(updateInfo.TargetFullRelease);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Update] Check failed: {ex.Message}");
        }
    }

    public bool IsInstalled => _manager.IsInstalled;
}
