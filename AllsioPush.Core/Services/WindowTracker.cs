namespace AllsioPush.Services;

public class WindowTracker
{
    private readonly List<IRemoteAckTarget> _targets = new();
    private readonly List<ISmsAckTarget> _smsTargets = new();
    private readonly Dictionary<string, List<IScreenPopTarget>> _screenPops = new(StringComparer.Ordinal);
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

    public void RegisterSms(ISmsAckTarget target)
    {
        lock (_lock) _smsTargets.Add(target);
    }

    public void UnregisterSms(ISmsAckTarget target)
    {
        lock (_lock) _smsTargets.Remove(target);
    }

    // Notify any open SMS slideout whose conversation matches that someone else
    // replied to / acknowledged it (so it can show a label and dismiss).
    public void BroadcastSmsAck(string conversationId, string acknowledgedBy, string? actionType)
    {
        if (string.IsNullOrWhiteSpace(conversationId)) return;

        ISmsAckTarget[] snapshot;
        lock (_lock) snapshot = _smsTargets.ToArray();

        foreach (var t in snapshot)
        {
            if (string.Equals(t.ConversationId, conversationId, StringComparison.Ordinal))
            {
                try { t.RemoteAcknowledged(acknowledgedBy, actionType); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[WindowTracker] SMS ack notify failed: {ex.Message}");
                }
            }
        }
    }

    public void RegisterScreenPop(string? callId, IScreenPopTarget window)
    {
        if (string.IsNullOrWhiteSpace(callId)) return;
        lock (_lock)
        {
            if (!_screenPops.TryGetValue(callId, out var list))
            {
                list = new List<IScreenPopTarget>();
                _screenPops[callId] = list;
            }
            list.Add(window);
        }
    }

    public void UnregisterScreenPop(IScreenPopTarget window)
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

        IScreenPopTarget[] snapshot;
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
