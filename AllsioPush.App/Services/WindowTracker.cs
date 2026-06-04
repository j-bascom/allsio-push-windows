namespace AllsioPush.Services;

public interface IRemoteAckTarget
{
    string? NotificationId { get; }
    void RemoteAcknowledged(string acknowledgedBy);
}

public class WindowTracker
{
    private readonly List<IRemoteAckTarget> _targets = new();
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
}
