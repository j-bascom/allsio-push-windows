namespace AllsioPush.Services;

public sealed class DebugLogEntry
{
    public DateTime Timestamp { get; init; }
    public string Category { get; init; } = "";
    public string Message { get; init; } = "";

    public override string ToString() =>
        $"[{Timestamp:HH:mm:ss.fff}] [{Category}] {Message}";
}

public static class DebugLog
{
    private static readonly object _lock = new();
    private static readonly List<DebugLogEntry> _entries = new();
    private const int MaxEntries = 1000;

    public static event Action<DebugLogEntry>? OnEntry;

    public static void Write(string category, string message)
    {
        var entry = new DebugLogEntry
        {
            Timestamp = DateTime.Now,
            Category = category,
            Message = message,
        };
        lock (_lock)
        {
            _entries.Add(entry);
            if (_entries.Count > MaxEntries)
                _entries.RemoveAt(0);
        }
        try { OnEntry?.Invoke(entry); } catch { }
    }

    public static List<DebugLogEntry> GetEntries()
    {
        lock (_lock) { return new List<DebugLogEntry>(_entries); }
    }

    public static void Clear()
    {
        lock (_lock) { _entries.Clear(); }
    }
}
