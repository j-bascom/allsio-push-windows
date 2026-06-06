using AllsioPush.Models;
using AllsioPush.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using WinUIEx;

namespace AllsioPush.UI.Windows;

public class SlideoutWindow : WindowEx, IRemoteAckTarget, IStackableToast
{
    private static readonly SolidColorBrush GreenBrush = new(ColorHelper.FromArgb(255, 34, 197, 94));

    private readonly PushNotification _notification;
    private readonly AckService _ackService;
    private readonly WindowTracker _tracker;

    private readonly int _width;
    private readonly StackPanel _actionsPanel;
    private readonly ProgressBar _ttlBar;
    private Button? _ackButton;
    private Button _copyButton = null!;

    private DispatcherQueueTimer? _ttlTimer;
    private DispatcherQueueTimer? _slideTimer;
    private int _ttlElapsedMs;
    private int _ttlTotalMs;
    private bool _ttlPaused;
    private bool _closing;
    private bool _positionedOnce;

    private const int SlideInMs = 250;
    private const int SlideOutMs = 180;

    private int _pixelWidth;
    private int _pixelHeight;

    int IStackableToast.PixelWidth => _pixelWidth;
    int IStackableToast.PixelHeight => _pixelHeight;
    bool IStackableToast.IsClosing => _closing;

    internal static readonly List<IStackableToast> Stack = new();
    internal static readonly object StackLock = new();

    public string? NotificationId => _notification.NotificationId;

    public SlideoutWindow(PushNotification notification, AckService ackService, WindowTracker tracker)
    {
        _notification = notification;
        _ackService = ackService;
        _tracker = tracker;
        _width = Math.Max(280, notification.PopupWidth ?? 380);

        Title = "Allsio Push";
        ConfigurePresenter();

        _ttlBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 1000,
            Value = 1000,
            Height = 3,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 8, 0, 0),
        };

        _actionsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 10, 0, 0),
        };
        BuildActionButtons();

        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(BuildHeader());
        stack.Children.Add(BuildContent());
        stack.Children.Add(_actionsPanel);
        stack.Children.Add(_ttlBar);

        var card = new Border
        {
            Background = new SolidColorBrush(ColorHelper.FromArgb(255, 30, 30, 30)),
            BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 60, 60, 60)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
            Child = stack,
        };

        var root = new Grid { RequestedTheme = ElementTheme.Dark };
        root.Children.Add(card);
        root.PointerEntered += (_, _) => _ttlPaused = true;
        root.PointerExited += (_, _) => _ttlPaused = false;
        Content = root;

        root.Loaded += (_, _) =>
        {
            _tracker.Register(this);
            // Start the countdown first so the TTL bar is visible and its height is
            // included when we measure — otherwise it overflows and clips the buttons.
            if ((notification.Ttl ?? 0) > 0) StartTtlCountdown(notification.Ttl!.Value);
            SizeAndPosition(root);
        };
        Closed += (_, _) =>
        {
            _tracker.Unregister(this);
            RemoveFromStack();
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
        // Park off-screen so the window doesn't flash at center before LayoutStack moves it.
        var work = DisplayArea.Primary.WorkArea;
        AppWindow.Move(new PointInt32(work.X + work.Width, work.Y + work.Height));
    }

    private void SizeAndPosition(FrameworkElement root)
    {
        root.Measure(new global::Windows.Foundation.Size(_width, double.PositiveInfinity));
        var h = Math.Clamp(root.DesiredSize.Height, 120, 480);
        var dpi = this.GetDpiForWindow() / 96.0;
        _pixelWidth = (int)Math.Ceiling(_width * dpi);
        _pixelHeight = (int)Math.Ceiling(h * dpi);
        AppWindow.Resize(new SizeInt32(_pixelWidth, _pixelHeight));
        AddToStack();
    }

    private void AddToStack()
    {
        lock (StackLock)
        {
            if (!Stack.Contains(this)) Stack.Add(this);
        }
        LayoutStack();
    }

    private void RemoveFromStack()
    {
        bool removed;
        lock (StackLock) removed = Stack.Remove(this);
        if (removed) LayoutStack();
    }

    // Stack open toasts bottom-up along the right edge: newest sits at the
    // bottom, older ones step up by their own height plus a gap. Each window
    // moves itself on its own dispatcher so cross-thread moves stay safe.
    internal static void LayoutStack()
    {
        const int margin = 12;
        const int gap = 8;
        IStackableToast[] snapshot;
        lock (StackLock) snapshot = Stack.ToArray();

        var area = DisplayArea.Primary.WorkArea;
        var y = area.Y + area.Height - margin;
        for (var i = snapshot.Length - 1; i >= 0; i--)
        {
            var win = snapshot[i];
            var wy = y - win.PixelHeight;
            var wx = area.X + area.Width - win.PixelWidth - margin;
            y = wy - gap;
            var pos = new PointInt32(wx, wy);
            win.DispatcherQueue.TryEnqueue(() =>
            {
                if (win.IsClosing) return;
                try { win.MoveTo(pos); } catch { }
            });
        }
    }

    void IStackableToast.MoveTo(PointInt32 target)
    {
        if (!_positionedOnce)
        {
            _positionedOnce = true;
            StartSlideIn(target);
        }
        else
        {
            AppWindow.Move(target);
        }
    }

    private void StartSlideIn(PointInt32 target)
    {
        var work = DisplayArea.Primary.WorkArea;
        var startX = work.X + work.Width;
        var elapsed = 0;
        AppWindow.Move(new PointInt32(startX, target.Y));

        _slideTimer?.Stop();
        _slideTimer = DispatcherQueue.CreateTimer();
        _slideTimer.Interval = TimeSpan.FromMilliseconds(16);
        _slideTimer.IsRepeating = true;
        _slideTimer.Tick += (_, _) =>
        {
            elapsed += 16;
            var t = Math.Min(1.0, (double)elapsed / SlideInMs);
            var eased = 1.0 - (1.0 - t) * (1.0 - t);
            var x = (int)(startX + (target.X - startX) * eased);
            try { AppWindow.Move(new PointInt32(x, target.Y)); } catch { }
            if (t >= 1.0) _slideTimer?.Stop();
        };
        _slideTimer.Start();
    }

    private void StartSlideOut(Action onComplete)
    {
        var startX = AppWindow.Position.X;
        var startY = AppWindow.Position.Y;
        var endX = DisplayArea.Primary.WorkArea.X + DisplayArea.Primary.WorkArea.Width;
        var elapsed = 0;

        _slideTimer?.Stop();
        _slideTimer = DispatcherQueue.CreateTimer();
        _slideTimer.Interval = TimeSpan.FromMilliseconds(16);
        _slideTimer.IsRepeating = true;
        _slideTimer.Tick += (_, _) =>
        {
            elapsed += 16;
            var t = Math.Min(1.0, (double)elapsed / SlideOutMs);
            var eased = t * t;
            var x = (int)(startX + (endX - startX) * eased);
            try { AppWindow.Move(new PointInt32(x, startY)); } catch { }
            if (t >= 1.0)
            {
                _slideTimer?.Stop();
                onComplete();
            }
        };
        _slideTimer.Start();
    }

    private FrameworkElement BuildHeader()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(_notification.Title) ? "Notification" : _notification.Title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 14,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(title, 0);

        var close = new Button
        {
            Content = "✕",
            Padding = new Thickness(6, 0, 6, 0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        close.Click += (_, _) => BeginHide();
        Grid.SetColumn(close, 1);

        grid.Children.Add(title);
        grid.Children.Add(close);
        return grid;
    }

    private FrameworkElement BuildContent() => _notification.TemplateType switch
    {
        "caller_card" => BuildCallerCardContent(),
        "appointment_alert" => BuildAppointmentContent(),
        _ => BuildPlainContent(),
    };

    private FrameworkElement BuildPlainContent() => new TextBlock
    {
        Text = _notification.Content,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 13,
    };

    private FrameworkElement BuildCallerCardContent()
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock
        {
            Text = _notification.CallerName ?? _notification.CallerPhone ?? "Unknown",
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            FontSize = 16,
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrWhiteSpace(_notification.CallerPhone) && !string.IsNullOrWhiteSpace(_notification.CallerName))
            panel.Children.Add(new TextBlock { Text = _notification.CallerPhone, Opacity = 0.7, FontSize = 12 });
        if (!string.IsNullOrWhiteSpace(_notification.Reason))
        {
            panel.Children.Add(new Border
            {
                Background = new SolidColorBrush(ColorHelper.FromArgb(255, 45, 45, 48)),
                Padding = new Thickness(8),
                Margin = new Thickness(0, 6, 0, 0),
                Child = new TextBlock { Text = _notification.Reason, TextWrapping = TextWrapping.Wrap, FontSize = 12 },
            });
        }
        return panel;
    }

    private FrameworkElement BuildAppointmentContent()
    {
        var panel = new StackPanel { Spacing = 4 };
        void AddRow(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var inner = new StackPanel();
            inner.Children.Add(new TextBlock { Text = label, Opacity = 0.7, FontSize = 11 });
            inner.Children.Add(new TextBlock { Text = value, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 13 });
            panel.Children.Add(new Border
            {
                Background = new SolidColorBrush(ColorHelper.FromArgb(255, 45, 45, 48)),
                Padding = new Thickness(8),
                Margin = new Thickness(0, 2, 0, 2),
                Child = inner,
            });
        }
        AddRow("WHEN", _notification.AppointmentDate);
        AddRow("SERVICE", _notification.Service);
        AddRow("STYLIST", _notification.Stylist);
        return panel;
    }

    private void BuildActionButtons()
    {
        _copyButton = new Button { Content = "Copy" };
        _copyButton.Click += OnCopyClick;
        _actionsPanel.Children.Add(_copyButton);

        foreach (var btn in _notification.Buttons)
        {
            var label = string.IsNullOrWhiteSpace(btn.Label) ? "Button" : btn.Label;
            var button = new Button { Content = label };
            switch (btn.Action)
            {
                case "ack":
                    button.Click += async (_, _) =>
                    {
                        button.IsEnabled = false;
                        await _ackService.Acknowledge(_notification.NotificationId, label, "ack");
                        button.Foreground = GreenBrush;
                        CloseAfter(1200);
                    };
                    _ackButton ??= button;
                    break;
                case "dismiss":
                    button.Click += async (_, _) =>
                    {
                        button.IsEnabled = false;
                        await _ackService.Dismiss(_notification.NotificationId, label);
                        BeginHide();
                    };
                    break;
                case "webhook":
                {
                    var captured = btn;
                    button.Click += async (_, _) =>
                    {
                        button.IsEnabled = false;
                        if (!string.IsNullOrWhiteSpace(captured.WebhookUrl))
                            await _ackService.FireWebhook(captured.WebhookUrl!, _notification, label);
                        button.Foreground = GreenBrush;
                        CloseAfter(1200);
                    };
                    break;
                }
                default:
                    button.Click += async (_, _) =>
                    {
                        button.IsEnabled = false;
                        await _ackService.Acknowledge(_notification.NotificationId, label, btn.Action ?? "noop");
                        CloseAfter(1200);
                    };
                    break;
            }
            _actionsPanel.Children.Add(button);
        }

        if (_actionsPanel.Children.Count == 1 && !string.IsNullOrWhiteSpace(_notification.NotificationId))
        {
            var ack = new Button { Content = "Acknowledge" };
            ack.Click += async (_, _) =>
            {
                ack.IsEnabled = false;
                await _ackService.Acknowledge(_notification.NotificationId, "Acknowledge", "ack");
                ack.Foreground = GreenBrush;
                CloseAfter(1200);
            };
            _actionsPanel.Children.Add(ack);
            _ackButton = ack;
        }
    }

    private async void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrEmpty(_notification.Content))
            {
                var dp = new DataPackage();
                dp.SetText(_notification.Content);
                Clipboard.SetContent(dp);
            }
        }
        catch { }
        _copyButton.Content = "Copied!";
        await Task.Delay(2000);
        if (!_closing) _copyButton.Content = "Copy";
    }

    private void StartTtlCountdown(int seconds)
    {
        _ttlTotalMs = seconds * 1000;
        _ttlBar.Visibility = Visibility.Visible;
        _ttlElapsedMs = 0;
        _ttlTimer = DispatcherQueue.CreateTimer();
        _ttlTimer.Interval = TimeSpan.FromMilliseconds(50);
        _ttlTimer.Tick += (_, _) =>
        {
            if (_closing || _ttlPaused) return;
            _ttlElapsedMs += 50;
            var remaining = Math.Max(0, _ttlTotalMs - _ttlElapsedMs);
            _ttlBar.Value = (double)remaining / _ttlTotalMs * 1000;
            if (remaining <= 0)
            {
                _ttlTimer!.Stop();
                BeginHide();
            }
        };
        _ttlTimer.Start();
    }

    private async void CloseAfter(int ms)
    {
        await Task.Delay(ms);
        BeginHide();
    }

    private void BeginHide()
    {
        if (_closing) return;
        _closing = true;
        _ttlTimer?.Stop();
        _slideTimer?.Stop();
        StartSlideOut(() => Close());
    }

    public void RemoteAcknowledged(string acknowledgedBy)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_ackButton != null)
            {
                _ackButton.IsEnabled = false;
                var who = string.IsNullOrWhiteSpace(acknowledgedBy) ? "someone" : acknowledgedBy;
                _ackButton.Content = $"Acked by {who}";
            }
            CloseAfter(2000);
        });
    }
}
