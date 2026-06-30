using Velopack;
using Velopack.Sources;

namespace AllsioPush.Services;

public enum UpdateOutcome
{
    UpToDate,
    Failed,
    RestartRequired,
}

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

    // Call this on startup after a 30s delay and then every 4 hours.
    // onUpdateFound fires only when an update is actually available (after the
    // check, before download) so callers can lazily show progress UI and stay
    // silent when nothing is pending. On RestartRequired the caller MUST exit
    // the app promptly: the Velopack updater is now waiting on this process to
    // exit (60s timeout) before it swaps files and relaunches.
    public async Task<UpdateOutcome> CheckAndApplyUpdates(
        Action<string>? onStatus = null,
        Action<int?>? onProgress = null,
        Action? onUpdateFound = null)
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
                return UpdateOutcome.UpToDate;
            }

            onUpdateFound?.Invoke();
            onStatus?.Invoke($"Installing update {updateInfo.TargetFullRelease.Version}...");
            onProgress?.Invoke(0);
            System.Diagnostics.Debug.WriteLine(
                $"[Update] Downloading {updateInfo.TargetFullRelease.Version}");

            await _manager.DownloadUpdatesAsync(updateInfo, pct => onProgress?.Invoke(pct));

            onStatus?.Invoke("Restarting to finish update...");
            onProgress?.Invoke(100);

            // silent: true suppresses Velopack's own native progress window so we
            // can show our in-app toast instead. Unlike ApplyUpdatesAndRestart,
            // this does NOT exit the app — the caller must do that next.
            _manager.WaitExitThenApplyUpdates(
                updateInfo.TargetFullRelease, silent: true, restart: true);
            return UpdateOutcome.RestartRequired;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Update] Check failed: {ex.Message}");
            onStatus?.Invoke("Update check failed.");
            return UpdateOutcome.Failed;
        }
    }

    public bool IsInstalled => _manager.IsInstalled;
}
