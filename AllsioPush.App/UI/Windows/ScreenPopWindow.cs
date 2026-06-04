using System.Globalization;
using AllsioPush.Config;
using AllsioPush.Models;
using AllsioPush.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using WinUIEx;

namespace AllsioPush.UI.Windows;

public class ScreenPopWindow : WindowEx, IScreenPopTarget
{
    private static readonly SolidColorBrush Bg = new(ColorHelper.FromArgb(255, 24, 24, 24));
    private static readonly SolidColorBrush Surface = new(ColorHelper.FromArgb(255, 36, 36, 36));
    private static readonly SolidColorBrush BorderBrushColor = new(ColorHelper.FromArgb(255, 55, 55, 55));
    private static readonly SolidColorBrush TextPrimary = new(ColorHelper.FromArgb(255, 240, 240, 240));
    private static readonly SolidColorBrush TextMuted = new(ColorHelper.FromArgb(255, 140, 140, 140));
    private static readonly SolidColorBrush AccentBlue = new(ColorHelper.FromArgb(255, 59, 130, 246));
    private static readonly SolidColorBrush Green = new(ColorHelper.FromArgb(255, 34, 197, 94));
    private static readonly SolidColorBrush Amber = new(ColorHelper.FromArgb(255, 251, 191, 36));
    private static readonly SolidColorBrush Red = new(ColorHelper.FromArgb(255, 239, 68, 68));

    private const int WindowWidth = 420;

    private readonly ScreenPopData _data;
    private readonly AppSettings _settings;
    private readonly WindowTracker _tracker;

    private readonly TextBlock _timerLabel;
    private DispatcherQueueTimer? _callTimer;
    private bool _closing;

    public string? CallId => _data.CallId;

    public ScreenPopWindow(ScreenPopData data, AppSettings settings, WindowTracker tracker)
    {
        _data = data;
        _settings = settings;
        _tracker = tracker;

        Title = "Allsio Screen Pop";
        ConfigurePresenter();

        _timerLabel = new TextBlock
        {
            Text = "0:00",
            Foreground = Green,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var stack = new StackPanel { Spacing = 0 };
        stack.Children.Add(BuildHeader());
        if (!string.IsNullOrWhiteSpace(_data.Reason)) stack.Children.Add(BuildReason());
        if (_data.Phorest != null) stack.Children.Add(BuildPhorest(_data.Phorest));
        if (_data.Qbo != null) stack.Children.Add(BuildQbo(_data.Qbo));
        if (_data.PriorCalls.Count > 0) stack.Children.Add(BuildPriorCalls());
        stack.Children.Add(BuildButtons());
        stack.Children.Add(BuildFooter());

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(16),
            Content = stack,
        };

        var card = new Border
        {
            Background = Bg,
            BorderBrush = BorderBrushColor,
            BorderThickness = new Thickness(1),
            Child = scroll,
        };

        var root = new Grid { RequestedTheme = ElementTheme.Dark };
        root.Children.Add(card);
        Content = root;

        root.Loaded += (_, _) =>
        {
            _tracker.RegisterScreenPop(_data.CallId, this);
            SizeAndPosition(root);
            StartCallTimer();
        };
        Closed += (_, _) =>
        {
            _callTimer?.Stop();
            _tracker.UnregisterScreenPop(this);
        };
    }

    private void ConfigurePresenter()
    {
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(false, false);
            p.IsAlwaysOnTop = true;
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
        }
        AppWindow.IsShownInSwitchers = false;
    }

    private void SizeAndPosition(FrameworkElement root)
    {
        root.Measure(new global::Windows.Foundation.Size(WindowWidth, double.PositiveInfinity));
        var h = (int)Math.Clamp(root.DesiredSize.Height, 320, 700);
        var dpi = this.GetDpiForWindow() / 96.0;
        var pw = (int)(WindowWidth * dpi);
        var ph = (int)(h * dpi);
        AppWindow.Resize(new SizeInt32(pw, ph));
        var area = DisplayArea.Primary.WorkArea;
        AppWindow.Move(new PointInt32(area.X + area.Width - pw - 12, area.Y + 12));
    }

    private FrameworkElement BuildHeader()
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel { Spacing = 2 };
        var name = string.IsNullOrWhiteSpace(_data.CallerName) ? "Unknown Caller" : _data.CallerName!;
        info.Children.Add(new TextBlock
        {
            Text = "☎  " + name,
            Foreground = TextPrimary,
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrWhiteSpace(_data.CallerPhone))
            info.Children.Add(new TextBlock { Text = _data.CallerPhone, Foreground = TextMuted, FontSize = 12 });
        if (!string.IsNullOrWhiteSpace(_data.AgentName))
            info.Children.Add(new TextBlock { Text = $"Being handled by {_data.AgentName}", Foreground = TextMuted, FontSize = 10 });

        Grid.SetColumn(info, 0);
        Grid.SetColumn(_timerLabel, 1);
        grid.Children.Add(info);
        grid.Children.Add(_timerLabel);
        return grid;
    }

    private FrameworkElement BuildReason()
    {
        var col = new StackPanel { Spacing = 4 };
        col.Children.Add(SectionCaption("REASON"));
        col.Children.Add(new TextBlock
        {
            Text = _data.Reason,
            Foreground = TextPrimary,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
        });
        return MakeCard(col);
    }

    private FrameworkElement BuildPhorest(PhorestRecord p)
    {
        var col = new StackPanel { Spacing = 2 };
        var fullName = string.Join(" ", new[] { p.FirstName, p.LastName }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (!string.IsNullOrWhiteSpace(fullName)) col.Children.Add(Row("Name", fullName));
        if (!string.IsNullOrWhiteSpace(p.Email)) col.Children.Add(Row("Email", p.Email));
        if (!string.IsNullOrWhiteSpace(p.LastVisitDate))
        {
            var visit = string.IsNullOrWhiteSpace(p.LastService) ? p.LastVisitDate : $"{p.LastVisitDate} — {p.LastService}";
            col.Children.Add(Row("Last visit", visit));
        }
        if (!string.IsNullOrWhiteSpace(p.PreferredStylist)) col.Children.Add(Row("Preferred stylist", p.PreferredStylist));
        if (p.TotalVisits.HasValue) col.Children.Add(Row("Total visits", p.TotalVisits.Value.ToString()));
        if (p.LifetimeValue.HasValue) col.Children.Add(Row("Lifetime value", "$" + p.LifetimeValue.Value.ToString("F2", CultureInfo.InvariantCulture)));
        if (!string.IsNullOrWhiteSpace(p.Notes))
            col.Children.Add(new TextBlock { Text = p.Notes, Foreground = Amber, FontSize = 12, FontStyle = global::Windows.UI.Text.FontStyle.Italic, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });

        return SectionContainer("\U0001F4CB Phorest Record", "MATCHED", ColorHelper.FromArgb(255, 13, 148, 136), col);
    }

    private FrameworkElement BuildQbo(QboRecord q)
    {
        var col = new StackPanel { Spacing = 2 };
        var primary = !string.IsNullOrWhiteSpace(q.DisplayName) ? q.DisplayName
            : !string.IsNullOrWhiteSpace(q.CompanyName) ? q.CompanyName : null;
        if (!string.IsNullOrWhiteSpace(primary)) col.Children.Add(Row("Name", primary));
        if (!string.IsNullOrWhiteSpace(q.CompanyName) && !string.Equals(q.CompanyName, primary, StringComparison.Ordinal))
            col.Children.Add(Row("Company", q.CompanyName));
        if (!string.IsNullOrWhiteSpace(q.Email)) col.Children.Add(Row("Email", q.Email));
        if (!string.IsNullOrWhiteSpace(q.Phone)) col.Children.Add(Row("Phone", q.Phone));
        if (!string.IsNullOrWhiteSpace(q.BillingAddress)) col.Children.Add(Row("Billing", q.BillingAddress));
        if (q.Balance.HasValue)
        {
            var balance = "$" + q.Balance.Value.ToString("F2", CultureInfo.InvariantCulture);
            col.Children.Add(Row("Balance", balance, q.Balance.Value > 0 ? Red : null));
        }

        return SectionContainer("\U0001F4BC QuickBooks Record", "MATCHED", ColorHelper.FromArgb(255, 34, 197, 94), col);
    }

    private FrameworkElement BuildPriorCalls()
    {
        var col = new StackPanel { Spacing = 2 };
        foreach (var pc in _data.PriorCalls.Take(5))
        {
            var date = pc.CallDate == default ? "" : pc.CallDate.ToString("MMM d, h:mm tt", CultureInfo.InvariantCulture);
            var reason = pc.Reason ?? "";
            if (reason.Length > 40) reason = reason.Substring(0, 40) + "…";
            var parts = new[] { date, pc.Duration, reason }.Where(s => !string.IsNullOrWhiteSpace(s));
            col.Children.Add(new TextBlock
            {
                Text = string.Join("  |  ", parts),
                Foreground = TextPrimary,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });
            if (!string.IsNullOrWhiteSpace(pc.Outcome))
                col.Children.Add(new TextBlock { Text = pc.Outcome, Foreground = TextMuted, FontSize = 10, TextWrapping = TextWrapping.Wrap });
        }
        return SectionContainer("\U0001F4DE Prior Calls", null, default, col);
    }

    private FrameworkElement BuildButtons()
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };

        var openBtn = MakeButton("\U0001F310  Open in Browser", AccentBlue);
        openBtn.Click += (_, _) => OpenInBrowser();
        panel.Children.Add(openBtn);

        if (!string.IsNullOrWhiteSpace(_data.CallerPhone))
        {
            var copyBtn = MakeButton("\U0001F4CB  Copy Number", Surface);
            copyBtn.Click += async (_, _) =>
            {
                try
                {
                    var dp = new DataPackage();
                    dp.SetText(_data.CallerPhone!);
                    Clipboard.SetContent(dp);
                }
                catch { }
                copyBtn.Content = "Copied!";
                await Task.Delay(2000);
                if (!_closing) copyBtn.Content = "\U0001F4CB  Copy Number";
            };
            panel.Children.Add(copyBtn);
        }

        var closeBtn = MakeButton("✕  Close", Surface);
        closeBtn.Click += (_, _) => BeginHide();
        panel.Children.Add(closeBtn);

        return panel;
    }

    private FrameworkElement BuildFooter()
    {
        var panel = new StackPanel { Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(new Border { Height = 1, Background = BorderBrushColor });
        panel.Children.Add(new TextBlock
        {
            Text = "Call started " + _data.CallStarted.ToString("h:mm tt", CultureInfo.InvariantCulture),
            Foreground = TextMuted,
            FontSize = 10,
        });
        return panel;
    }

    private FrameworkElement SectionContainer(string title, string? badge, global::Windows.UI.Color badgeColor, UIElement body)
    {
        var wrap = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 12) };

        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        headerRow.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = TextPrimary,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (!string.IsNullOrWhiteSpace(badge))
        {
            headerRow.Children.Add(new Border
            {
                Background = new SolidColorBrush(badgeColor),
                Padding = new Thickness(4, 2, 4, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = badge, Foreground = new SolidColorBrush(Colors.White), FontSize = 9, FontWeight = FontWeights.Bold },
            });
        }

        wrap.Children.Add(headerRow);
        wrap.Children.Add(MakeCard(body));
        return wrap;
    }

    private static Border MakeCard(UIElement child) => new()
    {
        Background = Surface,
        Padding = new Thickness(12),
        CornerRadius = new CornerRadius(4),
        Child = child,
    };

    private static TextBlock SectionCaption(string text) => new()
    {
        Text = text,
        Foreground = TextMuted,
        FontSize = 11,
        FontWeight = FontWeights.Bold,
    };

    private FrameworkElement Row(string label, string? value, SolidColorBrush? valueColor = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new TextBlock { Text = label + ":", Foreground = TextMuted, FontSize = 12 });
        row.Children.Add(new TextBlock
        {
            Text = value ?? "",
            Foreground = valueColor ?? TextPrimary,
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap,
        });
        return row;
    }

    private static Button MakeButton(string text, SolidColorBrush back) => new()
    {
        Content = text,
        Background = back,
        Foreground = TextPrimary,
        BorderBrush = BorderBrushColor,
        BorderThickness = new Thickness(1),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        Height = 38,
        FontWeight = FontWeights.SemiBold,
    };

    private void OpenInBrowser()
    {
        try
        {
            var url = _settings.AdminBase + "/dashboard";
            if (!string.IsNullOrWhiteSpace(_data.CallId))
                url += "?callId=" + Uri.EscapeDataString(_data.CallId!);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ScreenPop] OpenInBrowser failed: {ex.Message}");
        }
    }

    private void StartCallTimer()
    {
        _callTimer = DispatcherQueue.CreateTimer();
        _callTimer.Interval = TimeSpan.FromSeconds(1);
        _callTimer.Tick += (_, _) => UpdateTimerLabel();
        _callTimer.Start();
        UpdateTimerLabel();
    }

    private void UpdateTimerLabel()
    {
        if (_closing) return;
        var elapsed = DateTime.Now - _data.CallStarted;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        var minutes = (int)elapsed.TotalMinutes;
        _timerLabel.Text = $"{minutes}:{elapsed.Seconds:D2}";
    }

    public void CallEnded()
    {
        DispatcherQueue.TryEnqueue(BeginHide);
    }

    private void BeginHide()
    {
        if (_closing) return;
        _closing = true;
        _callTimer?.Stop();
        Close();
    }
}
