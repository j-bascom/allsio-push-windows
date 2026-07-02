using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using AllsioPush.Config;
using AllsioPush.Models;
using Microsoft.Data.Sqlite;

namespace AllsioPush.Services;

public class HistoryService
{
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly HttpClient _http;
    private readonly object _initLock = new();
    private bool _initialized = false;

    public HistoryService(AppSettings settings)
    {
        Directory.CreateDirectory(SettingsManager.GetAppDataPath());
        _dbPath = Path.Combine(SettingsManager.GetAppDataPath(), "history.db");
        _connectionString = $"Data Source={_dbPath}";
        // Use the same pinned / dev-aware client the rest of the API uses — the
        // server rejects requests over an unpinned client in some environments,
        // and dev certs aren't pinned.
        _http = AuthService.CreatePinnedHttpClient(
            settings.Environment == ServerEnvironment.Development);
    }

    private void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_initLock)
        {
            if (_initialized) return;
            try
            {
                using var conn = new SqliteConnection(_connectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS notifications (
  id TEXT PRIMARY KEY,
  notification_id TEXT,
  conversation_id TEXT,
  template_type TEXT,
  title TEXT,
  content TEXT,
  channel_name TEXT,
  group_name TEXT,
  received_at TEXT,
  action_taken TEXT,
  action_taken_at TEXT,
  acknowledged_by TEXT,
  raw_json TEXT
);
CREATE INDEX IF NOT EXISTS idx_received_at ON notifications(received_at DESC);
";
                cmd.ExecuteNonQuery();

                // Migrate pre-existing DBs that predate the conversation_id column.
                try
                {
                    using var migrate = conn.CreateCommand();
                    migrate.CommandText = "ALTER TABLE notifications ADD COLUMN conversation_id TEXT;";
                    migrate.ExecuteNonQuery();
                }
                catch
                {
                    // Column already exists — expected on already-migrated DBs.
                }

                _initialized = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[History] Init failed: {ex.Message}");
            }
        }
    }

    public async Task<HistoryEntry?> SaveNotification(PushNotification n, DateTime receivedAt)
    {
        EnsureInitialized();
        try
        {
            var entry = new HistoryEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                NotificationId = n.NotificationId,
                ConversationId = n.ConversationId,
                TemplateType = n.TemplateType,
                Title = n.Title,
                Content = n.Content,
                ChannelName = n.ChannelName,
                GroupName = n.GroupName,
                ReceivedAt = receivedAt,
                RawJson = JsonSerializer.Serialize(n),
            };

            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO notifications
  (id, notification_id, conversation_id, template_type, title, content, channel_name, group_name,
   received_at, action_taken, action_taken_at, acknowledged_by, raw_json)
VALUES
  ($id, $nid, $conv, $tt, $title, $content, $channel, $group,
   $received, NULL, NULL, NULL, $raw);";
            cmd.Parameters.AddWithValue("$id", entry.Id);
            cmd.Parameters.AddWithValue("$nid", (object?)entry.NotificationId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$conv", (object?)entry.ConversationId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$tt", entry.TemplateType);
            cmd.Parameters.AddWithValue("$title", entry.Title);
            cmd.Parameters.AddWithValue("$content", entry.Content);
            cmd.Parameters.AddWithValue("$channel", (object?)entry.ChannelName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$group", (object?)entry.GroupName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$received", entry.ReceivedAt.ToUniversalTime().ToString("O"));
            cmd.Parameters.AddWithValue("$raw", entry.RawJson ?? string.Empty);
            await cmd.ExecuteNonQueryAsync();
            return entry;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[History] SaveNotification failed: {ex.Message}");
            return null;
        }
    }

    public async Task RecordAction(string? notificationId, string action, string? acknowledgedBy = null)
    {
        if (string.IsNullOrWhiteSpace(notificationId)) return;
        EnsureInitialized();
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
UPDATE notifications
   SET action_taken = $action,
       action_taken_at = $when,
       acknowledged_by = COALESCE($by, acknowledged_by)
 WHERE notification_id = $nid;";
            cmd.Parameters.AddWithValue("$action", action);
            cmd.Parameters.AddWithValue("$when", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$by", (object?)acknowledgedBy ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$nid", notificationId);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[History] RecordAction failed: {ex.Message}");
        }
    }

    // Records an action against every history row for an SMS conversation.
    // SMS is keyed by conversationId (not notificationId), so regular
    // RecordAction can't match it.
    public async Task RecordSmsAction(string? conversationId, string action, string? acknowledgedBy = null)
    {
        if (string.IsNullOrWhiteSpace(conversationId)) return;
        EnsureInitialized();
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
UPDATE notifications
   SET action_taken = $action,
       action_taken_at = $when,
       acknowledged_by = COALESCE($by, acknowledged_by)
 WHERE conversation_id = $cid;";
            cmd.Parameters.AddWithValue("$action", action);
            cmd.Parameters.AddWithValue("$when", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$by", (object?)acknowledgedBy ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$cid", conversationId);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[History] RecordSmsAction failed: {ex.Message}");
        }
    }

    public async Task<List<HistoryEntry>> GetLocalHistory(int limit = 100)
    {
        EnsureInitialized();
        var list = new List<HistoryEntry>();
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT id, notification_id, template_type, title, content, channel_name, group_name,
       received_at, action_taken, action_taken_at, acknowledged_by, raw_json, conversation_id
  FROM notifications
 ORDER BY received_at DESC
 LIMIT $limit;";
            cmd.Parameters.AddWithValue("$limit", limit);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(ReadEntry(reader));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[History] GetLocalHistory failed: {ex.Message}");
        }
        return list;
    }

    public async Task<List<HistoryEntry>> GetServerHistory(
        string token, string apiBase, int limit = 100, int sinceDays = 30)
    {
        var list = new List<HistoryEntry>();
        if (string.IsNullOrWhiteSpace(token)) return list;

        try
        {
            // Path is signed without the query string, matching the rest of the API.
            const string path = "/api/notifications/history";
            var request = new HttpRequestMessage(HttpMethod.Get,
                $"{apiBase}{path}?limit={limit}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            AuthService.AddSigningHeaders(request, token, "GET", path);

            var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                DebugLog.Write("History",
                    $"Server fetch failed {(int)response.StatusCode} {response.StatusCode}: {err}");
                return list;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Server shape is {notifications:[...]}. Also accept {items:[...]},
            // {history:[...]}, {data:[...]}, or a bare [...] for robustness.
            JsonElement arr = root;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("notifications", out var notifs)) arr = notifs;
                else if (root.TryGetProperty("items", out var items)) arr = items;
                else if (root.TryGetProperty("history", out var hist)) arr = hist;
                else if (root.TryGetProperty("data", out var data)) arr = data;
            }
            if (arr.ValueKind != JsonValueKind.Array) return list;

            // The server has no date filter (limit/offset only), so bound the
            // window client-side to the last N days.
            var cutoff = sinceDays > 0 ? DateTime.UtcNow.AddDays(-sinceDays) : DateTime.MinValue;
            var skippedOld = 0;
            foreach (var item in arr.EnumerateArray())
            {
                var entry = ParseServerEntry(item);
                if (entry.ReceivedAt.ToUniversalTime() < cutoff) { skippedOld++; continue; }
                list.Add(entry);
            }
            DebugLog.Write("History",
                $"Server fetch ok — {list.Count} entr{(list.Count == 1 ? "y" : "ies")}"
                + (skippedOld > 0 ? $" ({skippedOld} older than {sinceDays}d skipped)" : ""));
        }
        catch (Exception ex)
        {
            DebugLog.Write("History", $"GetServerHistory exception: {ex.Message}");
        }
        return list;
    }

    public async Task PruneOldEntries()
    {
        EnsureInitialized();
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-90).ToString("O");
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM notifications WHERE received_at < $cutoff;";
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
            var deleted = await cmd.ExecuteNonQueryAsync();
            if (deleted > 0)
                System.Diagnostics.Debug.WriteLine($"[History] Pruned {deleted} old entries");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[History] Prune failed: {ex.Message}");
        }
    }

    public async Task ClearAll()
    {
        EnsureInitialized();
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM notifications;";
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[History] ClearAll failed: {ex.Message}");
        }
    }

    // Pulls the signed-in user's history from the server and merges it into the
    // local store. Called on sign-in to repopulate after ClearAll() wiped the
    // previous session's data at sign-out, so each user only sees their own.
    public async Task SyncFromServer(string token, string apiBase, int limit = 200)
    {
        var rows = await GetServerHistory(token, apiBase, limit);
        await MergeServerEntries(rows);
    }

    // Inserts server-sourced rows not already present locally (matched by
    // notification id). ParseServerEntry reconstructs a replayable RawJson from
    // the server's stored payload, so synced rows can be re-opened as popups.
    public async Task MergeServerEntries(IReadOnlyList<HistoryEntry> entries)
    {
        if (entries == null || entries.Count == 0) return;
        EnsureInitialized();
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            var existing = new HashSet<string>(StringComparer.Ordinal);
            await using (var sel = conn.CreateCommand())
            {
                sel.CommandText = "SELECT notification_id FROM notifications WHERE notification_id IS NOT NULL;";
                await using var r = await sel.ExecuteReaderAsync();
                while (await r.ReadAsync()) existing.Add(r.GetString(0));
            }

            foreach (var e in entries)
            {
                // Row already stored locally (or a dupe within this batch): don't
                // re-insert, but backfill a replayable payload / remote-ack info
                // if the existing row is missing it (e.g. synced before RawJson
                // was captured, or acked on the server since). COALESCE preserves
                // anything already there — never clobbers a live row's payload.
                if (!string.IsNullOrEmpty(e.NotificationId) && !existing.Add(e.NotificationId))
                {
                    if (!string.IsNullOrEmpty(e.RawJson) || !string.IsNullOrEmpty(e.AcknowledgedBy))
                    {
                        await using var upd = conn.CreateCommand();
                        upd.CommandText = @"
UPDATE notifications
   SET raw_json        = COALESCE(raw_json, $raw),
       acknowledged_by = COALESCE(acknowledged_by, $by),
       action_taken    = COALESCE(action_taken, $action),
       action_taken_at = COALESCE(action_taken_at, $actionAt)
 WHERE notification_id = $nid;";
                        upd.Parameters.AddWithValue("$raw", (object?)e.RawJson ?? DBNull.Value);
                        upd.Parameters.AddWithValue("$by", (object?)e.AcknowledgedBy ?? DBNull.Value);
                        upd.Parameters.AddWithValue("$action", (object?)e.ActionTaken ?? DBNull.Value);
                        upd.Parameters.AddWithValue("$actionAt",
                            (object?)e.ActionTakenAt?.ToUniversalTime().ToString("O") ?? DBNull.Value);
                        upd.Parameters.AddWithValue("$nid", e.NotificationId);
                        await upd.ExecuteNonQueryAsync();
                    }
                    continue;
                }

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
INSERT OR IGNORE INTO notifications
  (id, notification_id, conversation_id, template_type, title, content, channel_name, group_name,
   received_at, action_taken, action_taken_at, acknowledged_by, raw_json)
VALUES
  ($id, $nid, $conv, $tt, $title, $content, $channel, $group,
   $received, $action, $actionAt, $by, $raw);";
                cmd.Parameters.AddWithValue("$id",
                    string.IsNullOrEmpty(e.Id) ? Guid.NewGuid().ToString("N") : e.Id);
                cmd.Parameters.AddWithValue("$nid", (object?)e.NotificationId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$conv", (object?)e.ConversationId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$tt", e.TemplateType ?? "plain_text");
                cmd.Parameters.AddWithValue("$title", e.Title ?? string.Empty);
                cmd.Parameters.AddWithValue("$content", e.Content ?? string.Empty);
                cmd.Parameters.AddWithValue("$channel", (object?)e.ChannelName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$group", (object?)e.GroupName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$received", e.ReceivedAt.ToUniversalTime().ToString("O"));
                cmd.Parameters.AddWithValue("$action", (object?)e.ActionTaken ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$actionAt",
                    (object?)e.ActionTakenAt?.ToUniversalTime().ToString("O") ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$by", (object?)e.AcknowledgedBy ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$raw", (object?)e.RawJson ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[History] MergeServerEntries failed: {ex.Message}");
        }
    }

    private static HistoryEntry ReadEntry(SqliteDataReader r)
    {
        var entry = new HistoryEntry
        {
            Id = r.GetString(0),
            NotificationId = r.IsDBNull(1) ? null : r.GetString(1),
            TemplateType = r.IsDBNull(2) ? "plain_text" : r.GetString(2),
            Title = r.IsDBNull(3) ? string.Empty : r.GetString(3),
            Content = r.IsDBNull(4) ? string.Empty : r.GetString(4),
            ChannelName = r.IsDBNull(5) ? null : r.GetString(5),
            GroupName = r.IsDBNull(6) ? null : r.GetString(6),
            ReceivedAt = ParseDate(r.IsDBNull(7) ? null : r.GetString(7)) ?? DateTime.UtcNow,
            ActionTaken = r.IsDBNull(8) ? null : r.GetString(8),
            ActionTakenAt = ParseDate(r.IsDBNull(9) ? null : r.GetString(9)),
            AcknowledgedBy = r.IsDBNull(10) ? null : r.GetString(10),
            RawJson = r.IsDBNull(11) ? null : r.GetString(11),
            ConversationId = r.IsDBNull(12) ? null : r.GetString(12),
        };
        entry.IsRemoteAck = !string.IsNullOrEmpty(entry.AcknowledgedBy);
        return entry;
    }

    private static HistoryEntry ParseServerEntry(JsonElement e)
    {
        // Reads a key from any object element, returning strings as-is and other
        // scalar kinds via their raw text.
        static string? Get(JsonElement el, params string[] keys)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            foreach (var k in keys)
            {
                if (el.TryGetProperty(k, out var v) && v.ValueKind != JsonValueKind.Null)
                {
                    return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
                }
            }
            return null;
        }

        // The notification content lives in a nested "payload" object (the
        // original fired payload). Defensively handle a JSON-encoded string too.
        JsonElement payload = default;
        bool hasPayload = false;
        if (e.ValueKind == JsonValueKind.Object
            && e.TryGetProperty("payload", out var pl) && pl.ValueKind != JsonValueKind.Null)
        {
            if (pl.ValueKind == JsonValueKind.Object)
            {
                payload = pl;
                hasPayload = true;
            }
            else if (pl.ValueKind == JsonValueKind.String)
            {
                try
                {
                    var s = pl.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        using var pdoc = JsonDocument.Parse(s);
                        payload = pdoc.RootElement.Clone();
                        hasPayload = payload.ValueKind == JsonValueKind.Object;
                    }
                }
                catch { /* leave payload empty; fall back to envelope fields */ }
            }
        }

        // Prefer the payload for content fields, falling back to the envelope so
        // a flatter server shape still parses.
        string? P(params string[] keys) => hasPayload ? Get(payload, keys) : null;

        var acknowledgedAt = Get(e, "acknowledgedAt", "acknowledged_at", "actionTakenAt", "action_taken_at");
        var acknowledgedBy = Get(e, "acknowledgedByName", "acknowledged_by_name", "acknowledgedBy", "acknowledged_by");

        var entry = new HistoryEntry
        {
            Id = Get(e, "id") ?? Guid.NewGuid().ToString("N"),
            // Acks are matched by the payload's notificationId; fall back to the
            // envelope id only if the payload lacks one.
            NotificationId = P("notificationId", "notification_id", "id")
                             ?? Get(e, "notificationId", "notification_id", "id"),
            ConversationId = P("conversationId", "conversation_id")
                             ?? Get(e, "conversationId", "conversation_id"),
            TemplateType = P("templateType", "template_type")
                           ?? Get(e, "templateType", "template_type") ?? "plain_text",
            Title = P("title") ?? Get(e, "title") ?? string.Empty,
            Content = P("content", "body", "message")
                      ?? Get(e, "content", "body", "message") ?? string.Empty,
            ChannelName = P("channelName", "channel_name") ?? Get(e, "channelName", "channel_name"),
            GroupName = P("groupName", "group_name") ?? Get(e, "groupName", "group_name"),
            ReceivedAt = ParseDate(Get(e, "sentAt", "sent_at", "receivedAt", "received_at",
                                       "createdAt", "created_at")) ?? DateTime.UtcNow,
            ActionTakenAt = ParseDate(acknowledgedAt),
            AcknowledgedBy = acknowledgedBy,
        };
        // An ack timestamp or an acked-by name means it was acknowledged;
        // otherwise fall back to any explicit action field.
        entry.ActionTaken = (!string.IsNullOrEmpty(acknowledgedAt) || !string.IsNullOrEmpty(acknowledgedBy))
            ? "acknowledged"
            : Get(e, "actionTaken", "action_taken", "action");
        entry.IsRemoteAck = !string.IsNullOrEmpty(entry.AcknowledgedBy);

        // Reconstruct a replayable payload from the server's stored wire-format
        // payload (same shape as a live Pusher event), so history rows synced
        // from the server can be re-opened as popups just like live ones.
        if (hasPayload)
        {
            try
            {
                var n = PusherService.ParseNotification(payload.GetRawText(), entry.ChannelName ?? string.Empty);
                if (n != null)
                {
                    // Preserve identity fields the envelope may know better than the payload.
                    n.NotificationId ??= entry.NotificationId;
                    n.ConversationId ??= entry.ConversationId;
                    n.GroupName ??= entry.GroupName;
                    entry.RawJson = JsonSerializer.Serialize(n);
                }
            }
            catch (Exception ex)
            {
                DebugLog.Write("History", $"Payload reconstruct failed: {ex.Message}");
            }
        }
        return entry;
    }

    private static DateTime? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            return dt;
        return null;
    }
}
