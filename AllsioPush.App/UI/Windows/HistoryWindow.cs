using System.Runtime.InteropServices;
using System.Text.Json;
using AllsioPush.Config;
using AllsioPush.Models;
using AllsioPush.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinUIEx;

namespace AllsioPush.UI.Windows;

public class HistoryWindow : WindowEx
{
    private static readonly SolidColorBrush SurfaceColor = new(ColorHelper.FromArgb(255, 36, 36, 36));
    private static readonly SolidColorBrush SurfaceHover = new(ColorHelper.FromArgb(255, 46, 46, 46));
    private static readonly SolidColorBrush BorderColorBrush = new(ColorHelper.FromArgb(255, 55, 55, 55));
    private static readonly SolidColorBrush TextPrimary = new(ColorHelper.FromArgb(255, 240, 240, 240));
    private static readonly SolidColorBrush TextMuted = new(ColorHelper.FromArgb(255, 140, 140, 140));
    private static readonly SolidColorBrush AccentColor = new(ColorHelper.FromArgb(255, 59, 130, 246));
    private static readonly SolidColorBrush GreenColor = new(ColorHelper.FromArgb(255, 34, 197, 94));

    private readonly HistoryService _history;
    private readonly NotificationRouter _router;
    private readonly AppSettings _settings;
    private readonly Func<AuthSession?> _sessionGetter;
    private readonly SynchronizationContext _uiContext;

    private readonly ComboBox _filterCombo;
    private readonly ComboBox _sortCombo;
    private readonly Button _loadServerButton;
    private readonly TextBlock _countLabel;
    private readonly StackPanel _cardList;

    private readonly List<HistoryEntry> _entries = new();
    private readonly Dictionary<string, EntryCard> _cardsById = new();
    private readonly Dictionary<string, EntryCard> _cardsByConversation = new();
    private string _filter = "All Types";
    private string _sort = "Newest First";

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

        Title = "Allsio Push — Notification History";
        this.SetWindowSize(680, 720);
        PositionBottomRight();
        WindowIcon.Apply(this);
        SystemBackdrop = new MicaBackdrop();

        _filterCombo = new ComboBox
        {
            Width = 150,
            VerticalAlignment = VerticalAlignment.Center,
        };
        foreach (var f in new[] { "All Types", "Plain Text", "Caller Card", "Appointment", "URL", "Custom HTML", "SMS" })
            _filterCombo.Items.Add(f);
        _filterCombo.SelectedIndex = 0;

        _sortCombo = new ComboBox
        {
            Width = 130,
            VerticalAlignment = VerticalAlignment.Center,
        };
        foreach (var s in new[] { "Newest First", "By Channel" })
            _sortCombo.Items.Add(s);
        _sortCombo.SelectedIndex = 0;

        var refreshButton = new Button { Content = "Refresh" };
        var clearButton = new Button { Content = "Clear" };
        _loadServerButton = new Button { Content = "Load from server" };
        _countLabel = new TextBlock { Text = "0 notifications", Foreground = TextMuted, VerticalAlignment = VerticalAlignment.Center };

        var header = BuildHeader(refreshButton, clearButton);
        var footer = BuildFooter();

        _cardList = new StackPanel { Spacing = 8, Padding = new Thickness(12) };
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _cardList,
        };

        var root = new Grid { RequestedTheme = ElementTheme.Dark };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(header, 0);
        Grid.SetRow(scroll, 1);
        Grid.SetRow(footer, 2);
        root.Children.Add(header);
        root.Children.Add(scroll);
        root.Children.Add(footer);
        Content = root;

        _filterCombo.SelectionChanged += OnFilterChanged;
        _sortCombo.SelectionChanged += OnSortChanged;
        refreshButton.Click += async (_, _) => await ReloadLocalAsync();
        clearButton.Click += async (_, _) => await ClearAsync();
        _loadServerButton.Click += async (_, _) => await LoadFromServerAsync();

        root.Loaded += async (_, _) => await ReloadLocalAsync();
    }

    // Anchor to the bottom-right of the work area on whichever monitor the cursor is on,
    // 12px above the taskbar. Public so re-opening (single-instance) can snap it back.
    public void PositionBottomRight()
    {
        var cursor = GetCursorPos(out var p) ? new PointInt32(p.X, p.Y) : new PointInt32(0, 0);
        var area = (DisplayArea.GetFromPoint(cursor, DisplayAreaFallback.Nearest)
                    ?? DisplayArea.Primary).WorkArea;
        var size = AppWindow.Size;
        var x = area.X + area.Width - size.Width - 12;
        var y = area.Y + area.Height - size.Height - 12;
        AppWindow.Move(new PointInt32(x, y));
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private FrameworkElement BuildHeader(Button refresh, Button clear)
    {
        var grid = new Grid
        {
            Background = SurfaceColor,
            Padding = new Thickness(12, 8, 12, 8),
            BorderBrush = BorderColorBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = "Notification History",
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(title, 0);

        var rightPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        rightPanel.Children.Add(_sortCombo);
        rightPanel.Children.Add(_filterCombo);
        rightPanel.Children.Add(refresh);
        rightPanel.Children.Add(clear);
        Grid.SetColumn(rightPanel, 1);

        grid.Children.Add(title);
        grid.Children.Add(rightPanel);
        return grid;
    }

    private FrameworkElement BuildFooter()
    {
        var grid = new Grid
        {
            Background = SurfaceColor,
            Padding = new Thickness(12, 6, 12, 6),
            BorderBrush = BorderColorBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_countLabel, 0);
        Grid.SetColumn(_loadServerButton, 1);
        grid.Children.Add(_countLabel);
        grid.Children.Add(_loadServerButton);
        return grid;
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
            await ShowMessage("Not signed in. Cannot load server history.");
            return;
        }

        _loadServerButton.IsEnabled = false;
        var original = _loadServerButton.Content;
        _loadServerButton.Content = "Loading…";
        try
        {
            var serverRows = await _history.GetServerHistory(session.Token, _settings.ApiBase, 200);

            // Persist to the local DB so the rows survive a Refresh / re-open —
            // GetServerHistory alone is display-only.
            await _history.MergeServerEntries(serverRows);

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
            _loadServerButton.Content = original;
            _loadServerButton.IsEnabled = true;
        }
    }

    private async Task ClearAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "Allsio Push",
            Content = "Clear all local history?",
            PrimaryButtonText = "Clear",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = ((FrameworkElement)Content).XamlRoot,
        };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        await _history.ClearAll();
        _entries.Clear();
        Render();

        // Clearing resets the local cache to the authoritative server copy —
        // otherwise the DB stays permanently empty until the next sign-in, since
        // SyncFromServer only fires on sign-in. Re-pull now so history comes back.
        var session = _sessionGetter();
        if (session != null && !string.IsNullOrWhiteSpace(session.Token))
        {
            await _history.SyncFromServer(session.Token, _settings.ApiBase);
            await ReloadLocalAsync();
        }
    }

    private async Task ShowMessage(string message)
    {
        var dialog = new ContentDialog
        {
            Title = "Allsio Push",
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = ((FrameworkElement)Content).XamlRoot,
        };
        await dialog.ShowAsync();
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        _filter = (_filterCombo.SelectedItem as string) ?? "All Types";
        Render();
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        _sort = (_sortCombo.SelectedItem as string) ?? "Newest First";
        Render();
    }

    private void Render()
    {
        _cardList.Children.Clear();
        _cardsById.Clear();
        _cardsByConversation.Clear();

        var filtered = ApplyFilter(_entries);
        if (_sort == "By Channel")
            RenderGrouped(filtered);
        else
            RenderFlat(filtered);
        _countLabel.Text = $"{filtered.Count} notification{(filtered.Count == 1 ? "" : "s")}";
    }

    private void RenderFlat(List<HistoryEntry> entries)
    {
        foreach (var entry in entries)
        {
            var card = new EntryCard(entry, OnCardClicked);
            _cardList.Children.Add(card.Root);
            if (!string.IsNullOrEmpty(entry.NotificationId))
                _cardsById[entry.NotificationId!] = card;
            if (!string.IsNullOrEmpty(entry.ConversationId))
                _cardsByConversation[entry.ConversationId!] = card;
        }
    }

    private void RenderGrouped(List<HistoryEntry> entries)
    {
        var groups = entries
            .GroupBy(e => string.IsNullOrWhiteSpace(e.ChannelName) ? null : e.ChannelName)
            .OrderBy(g => g.Key == null ? 1 : 0)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            _cardList.Children.Add(BuildChannelHeader(group.Key ?? "(no channel)", group.Count()));
            foreach (var entry in group.OrderByDescending(e => e.ReceivedAt))
            {
                var card = new EntryCard(entry, OnCardClicked);
                _cardList.Children.Add(card.Root);
                if (!string.IsNullOrEmpty(entry.NotificationId))
                    _cardsById[entry.NotificationId!] = card;
                if (!string.IsNullOrEmpty(entry.ConversationId))
                    _cardsByConversation[entry.ConversationId!] = card;
            }
        }
    }

    private static FrameworkElement BuildChannelHeader(string name, int count)
    {
        var grid = new Grid { Margin = new Thickness(2, 8, 2, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = name,
            Foreground = AccentColor,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        Grid.SetColumn(label, 0);

        var line = new Border
        {
            BorderBrush = BorderColorBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(line, 1);

        var countLabel = new TextBlock
        {
            Text = count.ToString(),
            Foreground = TextMuted,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        Grid.SetColumn(countLabel, 2);

        grid.Children.Add(label);
        grid.Children.Add(line);
        grid.Children.Add(countLabel);
        return grid;
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
        _uiContext.Post(_ =>
        {
            _entries.Insert(0, entry);
            if (_entries.Count > 500) _entries.RemoveAt(_entries.Count - 1);
            Render();
        }, null);
    }

    public void UpdateEntryAction(string notificationId, string action, string? by)
    {
        _uiContext.Post(_ =>
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
                card.UpdateAction(entry);
        }, null);
    }

    // SMS conversations are matched by conversationId, not notificationId.
    public void UpdateEntrySmsAction(string conversationId, string action, string? by)
    {
        _uiContext.Post(_ =>
        {
            var entry = _entries.FirstOrDefault(e =>
                string.Equals(e.ConversationId, conversationId, StringComparison.Ordinal));
            if (entry == null) return;
            entry.ActionTaken = action;
            entry.ActionTakenAt = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(by))
            {
                entry.AcknowledgedBy = by;
                entry.IsRemoteAck = true;
            }
            if (_cardsByConversation.TryGetValue(conversationId, out var card))
                card.UpdateAction(entry);
        }, null);
    }

    private sealed class EntryCard
    {
        public Border Root { get; }

        private readonly HistoryEntry _entry;
        private readonly TextBlock _actionLabel;
        private readonly Action<HistoryEntry> _onClick;

        public EntryCard(HistoryEntry entry, Action<HistoryEntry> onClick)
        {
            _entry = entry;
            _onClick = onClick;

            Root = new Border
            {
                Background = SurfaceColor,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(12, 10, 12, 10),
            };

            var stack = new StackPanel { Spacing = 4 };

            var topRow = new Grid();
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var badge = new Border
            {
                Background = BadgeColor(entry.TemplateType),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 2, 6, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = BadgeText(entry.TemplateType),
                    Foreground = new SolidColorBrush(Colors.White),
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                },
            };

            var badgesPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center,
            };
            badgesPanel.Children.Add(badge);
            if (!string.IsNullOrWhiteSpace(entry.ChannelName))
            {
                var ch = entry.ChannelName.Length > 16 ? entry.ChannelName[..15] + "…" : entry.ChannelName;
                badgesPanel.Children.Add(new Border
                {
                    Background = new SolidColorBrush(ColorHelper.FromArgb(255, 40, 40, 58)),
                    BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 75, 75, 110)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(5, 2, 5, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = "#" + ch,
                        Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 150, 150, 210)),
                        FontSize = 10,
                    },
                });
            }
            Grid.SetColumn(badgesPanel, 0);

            var title = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(entry.Title) ? "(no title)" : entry.Title,
                Foreground = TextPrimary,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 10, 0),
            };
            Grid.SetColumn(title, 1);

            var time = new TextBlock
            {
                Text = FormatTime(entry.ReceivedAt),
                Foreground = TextMuted,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(time, 2);

            topRow.Children.Add(badgesPanel);
            topRow.Children.Add(title);
            topRow.Children.Add(time);
            stack.Children.Add(topRow);

            stack.Children.Add(new TextBlock
            {
                Text = TruncateContent(entry.Content),
                Foreground = TextMuted,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            _actionLabel = new TextBlock
            {
                Text = FormatAction(entry),
                Foreground = ActionColor(entry.ActionTaken),
                FontSize = 11,
            };
            stack.Children.Add(_actionLabel);

            Root.Child = stack;

            Root.PointerEntered += (_, _) => Root.Background = SurfaceHover;
            Root.PointerExited += (_, _) => Root.Background = SurfaceColor;
            Root.PointerPressed += (_, _) => _onClick(_entry);
        }

        public void UpdateAction(HistoryEntry entry)
        {
            _actionLabel.Text = FormatAction(entry);
            _actionLabel.Foreground = ActionColor(entry.ActionTaken);
        }

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

        private static SolidColorBrush BadgeColor(string t) => t switch
        {
            "caller_card" => new SolidColorBrush(ColorHelper.FromArgb(255, 59, 130, 246)),
            "appointment_alert" => new SolidColorBrush(ColorHelper.FromArgb(255, 217, 119, 6)),
            "url_tab" or "url_popup" => new SolidColorBrush(ColorHelper.FromArgb(255, 139, 92, 246)),
            "custom_html" => new SolidColorBrush(ColorHelper.FromArgb(255, 20, 184, 166)),
            "sms" => new SolidColorBrush(ColorHelper.FromArgb(255, 34, 197, 94)),
            _ => new SolidColorBrush(ColorHelper.FromArgb(255, 107, 114, 128)),
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
                "replied" or "reply" =>
                    !string.IsNullOrEmpty(e.AcknowledgedBy)
                        ? $"↩ Replied by {e.AcknowledgedBy}{dot}{when}"
                        : $"↩ Replied{dot}{when}",
                "dismiss" or "dismissed" => $"× Dismissed{dot}{when}",
                "webhook" => $"↑ Sent{dot}{when}",
                "timed_out" => $"⧗ No response{dot}{when}",
                _ => $"● {e.ActionTaken}{dot}{when}",
            };
        }

        private static SolidColorBrush ActionColor(string? action) => action switch
        {
            "ack" or "acknowledge" or "acknowledged" => GreenColor,
            "replied" or "reply" => GreenColor,
            "webhook" => AccentColor,
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
