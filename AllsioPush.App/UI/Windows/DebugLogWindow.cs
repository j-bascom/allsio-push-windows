using AllsioPush.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using WinUIEx;

namespace AllsioPush.UI.Windows;

public class DebugLogWindow : WindowEx
{
    private readonly SynchronizationContext _uiContext;
    private readonly StackPanel _logPanel;
    private readonly ScrollViewer _scroll;
    private bool _autoScroll = true;

    private static readonly SolidColorBrush BgColor    = new(ColorHelper.FromArgb(255, 18,  18,  24));
    private static readonly SolidColorBrush TextColor  = new(ColorHelper.FromArgb(255, 200, 200, 200));
    private static readonly SolidColorBrush MutedColor = new(ColorHelper.FromArgb(255, 100, 100, 120));
    private static readonly SolidColorBrush AccentColor= new(ColorHelper.FromArgb(255, 80,  140, 255));
    private static readonly SolidColorBrush WarnColor  = new(ColorHelper.FromArgb(255, 240, 180, 40));

    public DebugLogWindow(SynchronizationContext uiContext)
    {
        _uiContext = uiContext;

        Title = "Allsio Push — Debug Log";
        this.SetWindowSize(800, 540);
        this.CenterOnScreen();
        WindowIcon.Apply(this);

        _logPanel = new StackPanel { Spacing = 1, Padding = new Thickness(8, 4, 8, 4) };

        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _logPanel,
            Background = BgColor,
        };

        _scroll.ViewChanged += (_, _) =>
        {
            var atBottom = _scroll.VerticalOffset >= _scroll.ScrollableHeight - 20;
            _autoScroll = atBottom;
        };

        var toolbar = BuildToolbar();

        var root = new Grid { RequestedTheme = ElementTheme.Dark, Background = BgColor };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(toolbar, 0);
        Grid.SetRow(_scroll, 1);
        root.Children.Add(toolbar);
        root.Children.Add(_scroll);
        Content = root;

        // Load existing entries
        foreach (var entry in DebugLog.GetEntries())
            AppendRow(entry);

        ScrollToBottom();

        // Subscribe to live updates
        DebugLog.OnEntry += OnNewEntry;
        Closed += (_, _) => DebugLog.OnEntry -= OnNewEntry;
    }

    private Grid BuildToolbar()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Padding = new Thickness(8, 6, 8, 6),
            Background = new SolidColorBrush(ColorHelper.FromArgb(255, 26, 26, 36)),
        };

        var clearBtn = new Button { Content = "Clear", Padding = new Thickness(10, 4, 10, 4) };
        clearBtn.Click += (_, _) =>
        {
            DebugLog.Clear();
            _logPanel.Children.Clear();
        };

        var copyBtn = new Button { Content = "Copy All", Padding = new Thickness(10, 4, 10, 4) };
        copyBtn.Click += (_, _) =>
        {
            var entries = DebugLog.GetEntries();
            var text = string.Join("\n", entries.Select(e => e.ToString()));
            var pkg = new DataPackage();
            pkg.SetText(text);
            Clipboard.SetContent(pkg);
        };

        var autoScrollLabel = new TextBlock
        {
            Text = "Auto-scroll",
            Foreground = MutedColor,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
        };
        var autoScrollToggle = new ToggleSwitch
        {
            IsOn = true,
            Margin = new Thickness(0, 0, 0, 0),
            MinWidth = 0,
        };
        autoScrollToggle.Toggled += (_, _) => _autoScroll = autoScrollToggle.IsOn;

        panel.Children.Add(clearBtn);
        panel.Children.Add(copyBtn);
        panel.Children.Add(autoScrollLabel);
        panel.Children.Add(autoScrollToggle);

        var grid = new Grid();
        grid.Children.Add(panel);
        return grid;
    }

    private void OnNewEntry(DebugLogEntry entry)
    {
        _uiContext.Post(_ =>
        {
            AppendRow(entry);
            if (_autoScroll) ScrollToBottom();
        }, null);
    }

    private void AppendRow(DebugLogEntry entry)
    {
        var isWarning = entry.Message.Contains("WARNING") || entry.Message.Contains("FAILED") || entry.Message.Contains("failed") || entry.Message.Contains("error");
        var isEvent   = entry.Category.StartsWith("Event:");

        var row = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var ts = new TextBlock
        {
            Text = entry.Timestamp.ToString("HH:mm:ss.fff"),
            Foreground = MutedColor,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
            FontSize = 11,
            Padding = new Thickness(2, 1, 6, 1),
        };
        var cat = new TextBlock
        {
            Text = entry.Category,
            Foreground = isEvent ? AccentColor : MutedColor,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
            FontSize = 11,
            Padding = new Thickness(0, 1, 8, 1),
        };
        var msg = new TextBlock
        {
            Text = entry.Message,
            Foreground = isWarning ? WarnColor : TextColor,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Padding = new Thickness(0, 1, 0, 1),
        };

        Grid.SetColumn(ts,  0);
        Grid.SetColumn(cat, 1);
        Grid.SetColumn(msg, 2);
        row.Children.Add(ts);
        row.Children.Add(cat);
        row.Children.Add(msg);

        _logPanel.Children.Add(row);
    }

    private void ScrollToBottom()
    {
        _scroll.UpdateLayout();
        _scroll.ChangeView(null, _scroll.ScrollableHeight, null, disableAnimation: true);
    }
}
