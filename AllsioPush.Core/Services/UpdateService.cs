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
    public async Task CheckAndApplyUpdates(
        Action<string>? onStatus = null,
        Action<int?>? onProgress = null)
    {
        try
        {
            onStatus?.Invoke("Checking for updates...");
            onProgress?.Invoke(null);
            var updateInfo = await _manager.CheckForUpdatesAsync();

            if (updateInfo == null)
            {
                System.Diagnostics.Debug.WriteLine("[Update] No updates available");
                onStatus?.Invoke("Software is up to date.");
                return;
            }

            onStatus?.Invoke($"Installing update {updateInfo.TargetFullRelease.Version}...");
            onProgress?.Invoke(0);
            System.Diagnostics.Debug.WriteLine(
                $"[Update] Downloading {updateInfo.TargetFullRelease.Version}");

            await _manager.DownloadUpdatesAsync(updateInfo, pct => onProgress?.Invoke(pct));

            _manager.ApplyUpdatesAndRestart(updateInfo.TargetFullRelease);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Update] Check failed: {ex.Message}");
        }
    }

    public bool IsInstalled => _manager.IsInstalled;
}
