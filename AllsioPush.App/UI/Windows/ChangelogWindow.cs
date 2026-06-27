using AllsioPush.Models;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinUIEx;

namespace AllsioPush.UI.Windows;

// Customer-facing "What's New" list, opened from Settings. Renders the curated
// Changelog (Core) — newest release first, each item tagged NEW/IMPROVED/FIXED.
public class ChangelogWindow : WindowEx
{
    private static readonly SolidColorBrush TextMuted = new(ColorHelper.FromArgb(255, 140, 140, 140));
    private static readonly SolidColorBrush FeatureColor = new(ColorHelper.FromArgb(255, 34, 197, 94));  // green
    private static readonly SolidColorBrush ImproveColor = new(ColorHelper.FromArgb(255, 59, 130, 246)); // blue
    private static readonly SolidColorBrush FixColor = new(ColorHelper.FromArgb(255, 217, 119, 6));      // amber

    public ChangelogWindow()
    {
        Title = "Allsio Push — What's New";
        this.SetWindowSize(460, 600);
        this.CenterOnScreen();
        WindowIcon.Apply(this);
        SystemBackdrop = new MicaBackdrop();

        var content = new StackPanel { Spacing = 0, Padding = new Thickness(24, 20, 24, 24) };

        content.Children.Add(new TextBlock
        {
            Text = "What's New",
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 4),
        });
        content.Children.Add(new TextBlock
        {
            Text = "Recent features and fixes in Allsio Push.",
            Foreground = TextMuted,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 16),
        });

        foreach (var release in Changelog.Releases)
            content.Children.Add(BuildRelease(release));

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = content,
        };

        var root = new Grid { RequestedTheme = ElementTheme.Dark };
        root.Children.Add(scroll);
        Content = root;
    }

    private FrameworkElement BuildRelease(ChangelogRelease release)
    {
        var panel = new StackPanel { Spacing = 0, Margin = new Thickness(0, 0, 0, 18) };

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 0, 0, 8),
        };
        header.Children.Add(new TextBlock
        {
            Text = "Version " + release.Version,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (!string.IsNullOrWhiteSpace(release.Date))
            header.Children.Add(new TextBlock
            {
                Text = release.Date,
                Foreground = TextMuted,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            });
        panel.Children.Add(header);

        foreach (var item in release.Items)
            panel.Children.Add(BuildItem(item));

        return panel;
    }

    private FrameworkElement BuildItem(ChangeItem item)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var (label, color) = TagFor(item.Kind);
        var tag = new Border
        {
            Background = color,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 2, 7, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 10, 0),
            Width = 70,
            Child = new TextBlock
            {
                Text = label,
                Foreground = new SolidColorBrush(Colors.White),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
            },
        };
        Grid.SetColumn(tag, 0);

        var text = new TextBlock
        {
            Text = item.Text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(text, 1);

        grid.Children.Add(tag);
        grid.Children.Add(text);
        return grid;
    }

    private static (string label, SolidColorBrush color) TagFor(ChangeKind kind) => kind switch
    {
        ChangeKind.Feature => ("NEW", FeatureColor),
        ChangeKind.Improvement => ("IMPROVED", ImproveColor),
        ChangeKind.Fix => ("FIXED", FixColor),
        _ => ("UPDATE", TextMuted),
    };
}
