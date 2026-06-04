using AllsioPush.UI.Windows;

namespace AllsioPush.Services;

public interface IRemoteAckTarget
{
    string? NotificationId { get; }
    void RemoteAcknowledged(string acknowledgedBy);
}

public class WindowTracker
{
    private readonly List<IRemoteAckTarget> _targets = new();
    private readonly Dictionary<string, List<ScreenPopWindow>> _screenPops = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public void Register(IRemoteAckTarget target)
    {
        lock (_lock) _targets.Add(target);
    }

    public void Unregister(IRemoteAckTarget target)
    {
        lock (_lock) _targets.Remove(target);
    }

    public void BroadcastRemoteAck(string notificationId, string acknowledgedBy)
    {
        if (string.IsNullOrWhiteSpace(notificationId)) return;

        IRemoteAckTarget[] snapshot;
        lock (_lock) snapshot = _targets.ToArray();

        foreach (var t in snapshot)
        {
            if (string.Equals(t.NotificationId, notificationId, StringComparison.Ordinal))
            {
                try { t.RemoteAcknowledged(acknowledgedBy); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[WindowTracker] Notify failed: {ex.Message}");
                }
            }
        }
    }

    public void RegisterScreenPop(string? callId, ScreenPopWindow window)
    {
        if (string.IsNullOrWhiteSpace(callId)) return;
        lock (_lock)
        {
            if (!_screenPops.TryGetValue(callId, out var list))
            {
                list = new List<ScreenPopWindow>();
                _screenPops[callId] = list;
            }
            list.Add(window);
        }
    }

    public void UnregisterScreenPop(ScreenPopWindow window)
    {
        lock (_lock)
        {
            foreach (var key in _screenPops.Keys.ToList())
            {
                var list = _screenPops[key];
                list.Remove(window);
                if (list.Count == 0) _screenPops.Remove(key);
            }
        }
    }

    public void HandleCallEnded(string callId)
    {
        if (string.IsNullOrWhiteSpace(callId)) return;

        ScreenPopWindow[] snapshot;
        lock (_lock)
        {
            if (!_screenPops.TryGetValue(callId, out var list)) return;
            snapshot = list.ToArray();
        }

        foreach (var w in snapshot)
        {
            try { w.CallEnded(); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WindowTracker] CallEnded failed: {ex.Message}");
            }
        }
    }
}
