using System.Text.Json;
using AllsioPush.Config;
using AllsioPush.Models;
using AllsioPush.Services;

namespace AllsioPush.UI.Windows;

public class HistoryWindow : Form
{
    private static readonly Color BgColor = Color.FromArgb(24, 24, 24);
    private static readonly Color SurfaceColor = Color.FromArgb(36, 36, 36);
    private static readonly Color BorderColor = Color.FromArgb(55, 55, 55);
    private static readonly Color TextPrimary = Color.FromArgb(240, 240, 240);
    private static readonly Color TextMuted = Color.FromArgb(140, 140, 140);
    private static readonly Color AccentColor = Color.FromArgb(59, 130, 246);

    private readonly HistoryService _history;
    private readonly NotificationRouter _router;
    private readonly AppSettings _settings;
    private readonly Func<AuthSession?> _sessionGetter;
    private readonly SynchronizationContext _uiContext;

    private readonly ComboBox _filterCombo;
    private readonly Button _refreshButton;
    private readonly Button _clearButton;
    private readonly Button _loadServerButton;
    private readonly Label _countLabel;
    private readonly FlowLayoutPanel _cardList;
    private readonly Panel _scrollContainer;

    private readonly List<HistoryEntry> _entries = new();
    private readonly Dictionary<string, EntryCard> _cardsById = new();
    private string _filter = "All Types";

    public HistoryWindow(
        HistoryService history,
        NotificationRouter router,
        AppSettings settings,
        Func<AuthSession?> sessionGetter,
        SynchronizationContext uiContext)
    {
        _history = history;
        _router = router;
        _settings = settings;
        _sessionGetter = sessionGetter;
        _uiContext = uiContext;

        Text = "Allsio Push — Notification History";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(680, 720);
        MinimumSize = new Size(500, 400);
        BackColor = BgColor;
        ForeColor = TextPrimary;
        Font = new Font("Segoe UI", 9f);

        var header = BuildHeader(out _filterCombo, out _refreshButton, out _clearButton);
        var footer = BuildFooter(out _countLabel, out _loadServerButton);

        _cardList = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = false,
            AutoScroll = false,
            Dock = DockStyle.Top,
            Padding = new Padding(12),
            BackColor = BgColor,
        };

        _scrollContainer = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = BgColor,
        };
        _scrollContainer.Controls.Add(_cardList);
        _cardList.Width = _scrollContainer.ClientSize.Width;
        _scrollContainer.Resize += (_, _) =>
        {
            _cardList.Width = _scrollContainer.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2;
            foreach (Control c in _cardList.Controls) ResizeCard(c);
        };

        Controls.Add(_scrollContainer);
        Controls.Add(footer);
        Controls.Add(header);

        _filterCombo.SelectedIndexChanged += OnFilterChanged;
        _refreshButton.Click += async (_, _) => await ReloadLocalAsync();
        _clearButton.Click += async (_, _) => await ClearAsync();
        _loadServerButton.Click += async (_, _) => await LoadFromServerAsync();

        Load += async (_, _) => await ReloadLocalAsync();
    }

    private Panel BuildHeader(out ComboBox filter, out Button refresh, out Button clear)
    {
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 48,
            BackColor = SurfaceColor,
            Padding = new Padding(12, 8, 12, 8),
        };

        var title = new Label
        {
            Text = "Notification History",
            ForeColor = TextPrimary,
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(12, 14),
        };

        filter = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = BgColor,
            ForeColor = TextPrimary,
            FlatStyle = FlatStyle.Flat,
            Width = 140,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        filter.Items.AddRange(new object[]
        {
            "All Types", "Plain Text", "Caller Card", "Appointment",
            "URL", "Custom HTML", "SMS",
        });
        filter.SelectedIndex = 0;

        refresh = StyledButton("Refresh", 72);
        clear = StyledButton("Clear", 60);

        var rightPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Dock = DockStyle.Right,
            BackColor = SurfaceColor,
            Padding = new Padding(0, 4, 0, 0),
            WrapContents = false,
        };
        rightPanel.Controls.Add(filter);
        rightPanel.Controls.Add(refresh);
        rightPanel.Controls.Add(clear);

        header.Controls.Add(rightPanel);
        header.Controls.Add(title);

        var border = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = BorderColor };
        header.Controls.Add(border);

        return header;
    }

    private Panel BuildFooter(out Label countLabel, out Button loadServer)
    {
        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 36,
            BackColor = SurfaceColor,
            Padding = new Padding(12, 6, 12, 6),
        };

        countLabel = new Label
        {
            Text = "0 notifications",
            ForeColor = TextMuted,
            AutoSize = true,
            Location = new Point(12, 10),
        };

        loadServer = StyledButton("Load from server", 130);
        var rightPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Dock = DockStyle.Right,
            BackColor = SurfaceColor,
            Padding = new Padding(0, 2, 0, 0),
            WrapContents = false,
        };
        rightPanel.Controls.Add(loadServer);

        footer.Controls.Add(rightPanel);
        footer.Controls.Add(countLabel);

        var border = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = BorderColor };
        footer.Controls.Add(border);

        return footer;
    }

    private static Button StyledButton(string text, int width)
    {
        var b = new Button
        {
            Text = text,
            Width = width,
            Height = 28,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(50, 50, 50),
            ForeColor = TextPrimary,
            Margin = new Padding(6, 2, 0, 2),
        };
        b.FlatAppearance.BorderColor = BorderColor;
        return b;
    }

    private async Task ReloadLocalAsync()
    {
        try
        {
            var rows = await _history.GetLocalHistory(200);
            _entries.Clear();
            _entries.AddRange(rows);
            Render();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HistoryWindow] Reload failed: {ex.Message}");
        }
    }

    private async Task LoadFromServerAsync()
    {
        var session = _sessionGetter();
        if (session == null || string.IsNullOrWhiteSpace(session.Token))
        {
            MessageBox.Show(this, "Not signed in. Cannot load server history.", "Allsio Push",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _loadServerButton.Enabled = false;
        var original = _loadServerButton.Text;
        _loadServerButton.Text = "Loading…";
        try
        {
            var serverRows = await _history.GetServerHistory(session.Token, _settings.ApiBase, 200);

            var byId = new Dictionary<string, HistoryEntry>(StringComparer.Ordinal);
            foreach (var e in _entries)
            {
                var key = e.NotificationId ?? e.Id;
                byId[key] = e;
            }
            foreach (var s in serverRows)
            {
                var key = s.NotificationId ?? s.Id;
                if (!byId.ContainsKey(key)) byId[key] = s;
            }
            _entries.Clear();
            _entries.AddRange(byId.Values.OrderByDescending(e => e.ReceivedAt));
            Render();
        }
        finally
        {
            _loadServerButton.Text = original;
            _loadServerButton.Enabled = true;
        }
    }

    private async Task ClearAsync()
    {
        var confirm = MessageBox.Show(this, "Clear all local history?", "Allsio Push",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.Yes) return;

        await _history.ClearAll();
        _entries.Clear();
        Render();
    }

    private void OnFilterChanged(object? sender, EventArgs e)
    {
        _filter = (_filterCombo.SelectedItem as string) ?? "All Types";
        Render();
    }

    private void Render()
    {
        _cardList.SuspendLayout();
        _cardList.Controls.Clear();
        _cardsById.Clear();

        var filtered = ApplyFilter(_entries);
        foreach (var entry in filtered)
        {
            var card = new EntryCard(entry, _cardList.Width - _cardList.Padding.Horizontal, OnCardClicked);
            _cardList.Controls.Add(card);
            if (!string.IsNullOrEmpty(entry.NotificationId))
                _cardsById[entry.NotificationId!] = card;
        }
        _cardList.ResumeLayout();
        _cardList.Height = filtered.Sum(_ => 96) + 12;
        _countLabel.Text = $"{filtered.Count} notification{(filtered.Count == 1 ? "" : "s")}";
    }

    private void ResizeCard(Control c)
    {
        c.Width = _cardList.Width - _cardList.Padding.Horizontal;
    }

    private List<HistoryEntry> ApplyFilter(List<HistoryEntry> source)
    {
        if (_filter == "All Types") return source;
        bool Match(HistoryEntry e) => _filter switch
        {
            "Plain Text" => e.TemplateType == "plain_text",
            "Caller Card" => e.TemplateType == "caller_card",
            "Appointment" => e.TemplateType == "appointment_alert",
            "URL" => e.TemplateType == "url_tab" || e.TemplateType == "url_popup",
            "Custom HTML" => e.TemplateType == "custom_html",
            "SMS" => e.TemplateType == "sms",
            _ => true,
        };
        return source.Where(Match).ToList();
    }

    private void OnCardClicked(HistoryEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.RawJson)) return;
        try
        {
            var n = JsonSerializer.Deserialize<PushNotification>(entry.RawJson);
            if (n == null) return;
            _router.Route(n, persistHistory: false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HistoryWindow] Replay failed: {ex.Message}");
        }
    }

    public void AddEntry(HistoryEntry entry)
    {
        void Apply()
        {
            _entries.Insert(0, entry);
            if (_entries.Count > 500) _entries.RemoveAt(_entries.Count - 1);
            Render();
        }
        if (InvokeRequired) BeginInvoke((Action)Apply);
        else Apply();
    }

    public void UpdateEntryAction(string notificationId, string action, string? by)
    {
        void Apply()
        {
            var entry = _entries.FirstOrDefault(e =>
                string.Equals(e.NotificationId, notificationId, StringComparison.Ordinal));
            if (entry == null) return;
            entry.ActionTaken = action;
            entry.ActionTakenAt = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(by))
            {
                entry.AcknowledgedBy = by;
                entry.IsRemoteAck = true;
            }
            if (_cardsById.TryGetValue(notificationId, out var card))
            {
                card.UpdateAction(entry);
            }
        }
        if (InvokeRequired) BeginInvoke((Action)Apply);
        else Apply();
    }

    private sealed class EntryCard : Panel
    {
        private readonly HistoryEntry _entry;
        private readonly Label _actionLabel;
        private readonly Action<HistoryEntry> _onClick;

        public EntryCard(HistoryEntry entry, int width, Action<HistoryEntry> onClick)
        {
            _entry = entry;
            _onClick = onClick;

            Width = width;
            Height = 90;
            BackColor = SurfaceColor;
            Margin = new Padding(0, 0, 0, 8);
            Padding = new Padding(12, 10, 12, 10);
            Cursor = Cursors.Hand;

            var badge = new Label
            {
                Text = BadgeText(entry.TemplateType),
                BackColor = BadgeColor(entry.TemplateType),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 7.5f, FontStyle.Bold),
                AutoSize = false,
                Width = 86,
                Height = 18,
                TextAlign = ContentAlignment.MiddleCenter,
                Location = new Point(12, 12),
            };

            var title = new Label
            {
                Text = string.IsNullOrWhiteSpace(entry.Title) ? "(no title)" : entry.Title,
                ForeColor = TextPrimary,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                AutoSize = false,
                Location = new Point(108, 11),
                Width = width - 108 - 90,
                Height = 20,
                AutoEllipsis = true,
            };

            var time = new Label
            {
                Text = FormatTime(entry.ReceivedAt),
                ForeColor = TextMuted,
                Font = new Font("Segoe UI", 8.5f),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleRight,
                Location = new Point(width - 90, 12),
                Width = 78,
                Height = 18,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
            };

            var content = new Label
            {
                Text = TruncateContent(entry.Content),
                ForeColor = TextMuted,
                Font = new Font("Segoe UI", 9f),
                AutoSize = false,
                Location = new Point(12, 36),
                Width = width - 24,
                Height = 32,
                AutoEllipsis = true,
            };

            _actionLabel = new Label
            {
                Text = FormatAction(entry),
                ForeColor = ActionColor(entry.ActionTaken),
                Font = new Font("Segoe UI", 8.5f),
                AutoSize = false,
                Location = new Point(12, 68),
                Width = width - 24,
                Height = 16,
            };

            Controls.Add(badge);
            Controls.Add(title);
            Controls.Add(time);
            Controls.Add(content);
            Controls.Add(_actionLabel);

            Click += OnClickSelf;
            badge.Click += OnClickSelf;
            title.Click += OnClickSelf;
            time.Click += OnClickSelf;
            content.Click += OnClickSelf;
            _actionLabel.Click += OnClickSelf;

            MouseEnter += OnHover;
            MouseLeave += OnUnhover;
        }

        public void UpdateAction(HistoryEntry entry)
        {
            _actionLabel.Text = FormatAction(entry);
            _actionLabel.ForeColor = ActionColor(entry.ActionTaken);
        }

        private void OnClickSelf(object? s, EventArgs e) => _onClick(_entry);
        private void OnHover(object? s, EventArgs e) => BackColor = Color.FromArgb(46, 46, 46);
        private void OnUnhover(object? s, EventArgs e) => BackColor = SurfaceColor;

        private static string BadgeText(string t) => t switch
        {
            "caller_card" => "CALLER",
            "appointment_alert" => "APPOINTMENT",
            "url_tab" => "URL",
            "url_popup" => "URL POPUP",
            "custom_html" => "CUSTOM HTML",
            "sms" => "SMS",
            _ => "PLAIN",
        };

        private static Color BadgeColor(string t) => t switch
        {
            "caller_card" => Color.FromArgb(59, 130, 246),
            "appointment_alert" => Color.FromArgb(217, 119, 6),
            "url_tab" or "url_popup" => Color.FromArgb(139, 92, 246),
            "custom_html" => Color.FromArgb(20, 184, 166),
            "sms" => Color.FromArgb(34, 197, 94),
            _ => Color.FromArgb(107, 114, 128),
        };

        private static string FormatTime(DateTime received)
        {
            var local = received.ToLocalTime();
            if (local.Date == DateTime.Now.Date) return local.ToString("h:mm tt");
            return local.ToString("MMM d h:mm tt");
        }

        private static string TruncateContent(string content)
        {
            if (string.IsNullOrEmpty(content)) return string.Empty;
            var oneLine = content.Replace('\r', ' ').Replace('\n', ' ');
            if (oneLine.Length > 200) oneLine = oneLine[..199] + "…";
            return oneLine;
        }

        private static string FormatAction(HistoryEntry e)
        {
            if (string.IsNullOrEmpty(e.ActionTaken)) return string.Empty;
            var when = e.ActionTakenAt.HasValue ? FormatAgo(e.ActionTakenAt.Value) : null;
            var dot = when != null ? "  •  " : "";

            return e.ActionTaken switch
            {
                "ack" or "acknowledge" or "acknowledged" =>
                    !string.IsNullOrEmpty(e.AcknowledgedBy)
                        ? $"✓ Acknowledged by {e.AcknowledgedBy}{dot}{when}"
                        : $"✓ Acknowledged{dot}{when}",
                "dismiss" or "dismissed" => $"× Dismissed{dot}{when}",
                "webhook" => $"↑ Sent{dot}{when}",
                "timed_out" => $"⧗ No response{dot}{when}",
                _ => $"● {e.ActionTaken}{dot}{when}",
            };
        }

        private static Color ActionColor(string? action) => action switch
        {
            "ack" or "acknowledge" or "acknowledged" => Color.FromArgb(34, 197, 94),
            "dismiss" or "dismissed" => TextMuted,
            "webhook" => AccentColor,
            "timed_out" => TextMuted,
            _ => TextMuted,
        };

        private static string FormatAgo(DateTime when)
        {
            var span = DateTime.UtcNow - when.ToUniversalTime();
            if (span.TotalSeconds < 60) return "just now";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min ago";
            if (span.TotalHours < 24) return $"{(int)span.TotalHours} hr ago";
            return when.ToLocalTime().ToString("MMM d");
        }
    }
}
