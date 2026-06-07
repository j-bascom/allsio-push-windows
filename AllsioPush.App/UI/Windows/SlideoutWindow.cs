using System.Text;
using AllsioPush.Config;
using AllsioPush.Models;
using AllsioPush.Services;
using Microsoft.UI;
using Microsoft.Web.WebView2.Core;
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
    private readonly bool _isHtmlContent;
    private WebView2? _webView;
    private readonly StackPanel _actionsPanel;
    private readonly Grid _ttlBar;
    private Border _ttlFill = null!;
    private Button? _ackButton;
    private Button _copyButton = null!;

    private DispatcherQueueTimer? _ttlTimer;
    private DispatcherQueueTimer? _slideTimer;
    private DispatcherQueueTimer? _slideYTimer;
    private bool _slideInComplete;
    private PointInt32 _slideTarget;
    private IntPtr _hwnd;
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
        _isHtmlContent = notification.TemplateType == "custom_html";

        Title = "Allsio Push";
        ConfigurePresenter();

        _ttlFill = new Border { Background = GreenBrush };
        _ttlBar = new Grid
        {
            Height = 10,
            Margin = new Thickness(0, 2, 0, 0),
            Background = new SolidColorBrush(ColorHelper.FromArgb(255, 55, 55, 55)),
            Visibility = (notification.Ttl ?? 0) > 0 ? Visibility.Visible : Visibility.Collapsed,
        };
        // Col 0 = green fill (shrinks), Col 1 = dark track remainder (grows)
        _ttlBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _ttlBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) });
        Grid.SetColumn(_ttlFill, 0);
        _ttlBar.Children.Add(_ttlFill);

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

        var card = new Border
        {
            Background = new SolidColorBrush(ColorHelper.FromArgb(255, 30, 30, 30)),
            BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 60, 60, 60)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
            Child = stack,
        };

        var root = new Grid { RequestedTheme = ElementTheme.Dark };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(card, 0);
        Grid.SetRow(_ttlBar, 1);
        root.Children.Add(card);
        root.Children.Add(_ttlBar);
        root.PointerEntered += (_, _) => _ttlPaused = true;
        root.PointerExited += (_, _) => _ttlPaused = false;
        Content = root;

        root.Loaded += async (_, _) =>
        {
            _tracker.Register(this);
            DebugLog.Write("Slideout", $"Loaded ttl={notification.Ttl} ttlBarVisibility={_ttlBar.Visibility}");
            if ((notification.Ttl ?? 0) > 0) StartTtlCountdown(notification.Ttl!.Value);
            SizeAndPosition(root);
            if (_webView != null) await InitializeWebViewAsync();
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
        if (!_isHtmlContent) InitLayeredAlpha();
    }

    private void InitLayeredAlpha()
    {
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var ex = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, ex | (nint)NativeMethods.WS_EX_LAYERED);
        NativeMethods.SetLayeredWindowAttributes(_hwnd, 0, 0, NativeMethods.LWA_ALPHA);
    }

    private void SetWindowOpacity(byte alpha) =>
        NativeMethods.SetLayeredWindowAttributes(_hwnd, 0, alpha, NativeMethods.LWA_ALPHA);

    private void EnsureOpaque()
    {
        if (_hwnd == IntPtr.Zero) return;
        NativeMethods.SetLayeredWindowAttributes(_hwnd, 0, 255, NativeMethods.LWA_ALPHA);
        var ex = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, ex & ~(nint)NativeMethods.WS_EX_LAYERED);
    }

    private void SizeAndPosition(FrameworkElement root)
    {
        double h;
        if (_isHtmlContent)
        {
            // WebView2 is a child HWND — it doesn't participate in WinUI Measure.
            // Use the payload's popupHeight or a sensible default.
            h = _notification.PopupHeight ?? 300;
        }
        else
        {
            root.Measure(new global::Windows.Foundation.Size(_width, double.PositiveInfinity));
            h = Math.Clamp(root.DesiredSize.Height, 120, 480);
        }
        var dpi = this.GetDpiForWindow() / 96.0;
        _pixelWidth = (int)Math.Ceiling(_width * dpi);
        _pixelHeight = (int)Math.Ceiling(h * dpi) + 2;
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
        else if (!_slideInComplete)
        {
            _slideTarget = new PointInt32(_slideTarget.X, target.Y);
        }
        else
        {
            StartRepositionAnim(target);
        }
    }

    private void StartSlideIn(PointInt32 target)
    {
        _slideTarget = target;
        var work = DisplayArea.Primary.WorkArea;
        var startX = work.X + work.Width;
        var elapsed = 0;
        AppWindow.Move(new PointInt32(startX, _slideTarget.Y));

        _slideTimer?.Stop();
        _slideTimer = DispatcherQueue.CreateTimer();
        _slideTimer.Interval = TimeSpan.FromMilliseconds(16);
        _slideTimer.IsRepeating = true;
        _slideTimer.Tick += (_, _) =>
        {
            elapsed += 16;
            var t = Math.Min(1.0, (double)elapsed / SlideInMs);
            var eased = 1.0 - (1.0 - t) * (1.0 - t);
            var x = (int)(startX + (_slideTarget.X - startX) * eased);
            if (!_isHtmlContent) SetWindowOpacity((byte)(t * t * t * 255));
            try { AppWindow.Move(new PointInt32(x, _slideTarget.Y)); } catch { }
            if (t >= 1.0)
            {
                _slideInComplete = true;
                _slideTimer?.Stop();
                if (!_isHtmlContent) EnsureOpaque();
            }
        };
        _slideTimer.Start();
    }

    private void StartRepositionAnim(PointInt32 target)
    {
        var startY = AppWindow.Position.Y;
        if (startY == target.Y) return;
        var elapsed = 0;
        const int RepositionMs = 200;
        _slideYTimer?.Stop();
        _slideYTimer = DispatcherQueue.CreateTimer();
        _slideYTimer.Interval = TimeSpan.FromMilliseconds(16);
        _slideYTimer.IsRepeating = true;
        _slideYTimer.Tick += (_, _) =>
        {
            elapsed += 16;
            var t = Math.Min(1.0, (double)elapsed / RepositionMs);
            var eased = 1.0 - (1.0 - t) * (1.0 - t);
            var y = (int)(startY + (target.Y - startY) * eased);
            try { AppWindow.Move(new PointInt32(target.X, y)); } catch { }
            if (t >= 1.0) _slideYTimer?.Stop();
        };
        _slideYTimer.Start();
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
        "custom_html" => BuildHtmlContent(),
        _ => BuildPlainContent(),
    };

    private FrameworkElement BuildPlainContent() => new TextBlock
    {
        Text = _notification.Content,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 13,
    };

    private FrameworkElement BuildHtmlContent()
    {
        _webView = new WebView2
        {
            MinHeight = 120,
            Height = _notification.PopupHeight ?? 300,
        };
        return _webView;
    }

    private async Task InitializeWebViewAsync()
    {
        if (_webView == null) return;
        try
        {
            var userDataFolder = Path.Combine(SettingsManager.GetAppDataPath(), "WebView2");
            Directory.CreateDirectory(userDataFolder);
            var env = await CoreWebView2Environment.CreateWithOptionsAsync(
                null, userDataFolder, new CoreWebView2EnvironmentOptions());
            await _webView.EnsureCoreWebView2Async(env);

            _webView.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (!string.IsNullOrEmpty(e.Uri) &&
                    (e.Uri.StartsWith("https://") || e.Uri.StartsWith("http://") || e.Uri.StartsWith("tel:")))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = e.Uri,
                        UseShellExecute = true,
                    });
                }
            };

            _webView.CoreWebView2.NavigateToString(RenderCustomHtml());
        }
        catch (Exception ex)
        {
            DebugLog.Write("Slideout", $"WebView2 init failed: {ex.Message}");
        }
    }

    private string RenderCustomHtml()
    {
        var template = _notification.CustomHtml ?? _notification.Content ?? string.Empty;
        var sb = new StringBuilder(template);
        var tokens = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["title"] = _notification.Title,
            ["content"] = _notification.Content,
            ["channel"] = _notification.ChannelName,
            ["channelName"] = _notification.ChannelName,
            ["group"] = _notification.GroupName,
            ["groupName"] = _notification.GroupName,
            ["timestamp"] = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["callerName"] = _notification.CallerName,
            ["callerPhone"] = _notification.CallerPhone,
            ["reason"] = _notification.Reason,
            ["appointmentDate"] = _notification.AppointmentDate,
            ["service"] = _notification.Service,
            ["stylist"] = _notification.Stylist,
            ["senderName"] = _notification.SenderName,
            ["senderPhone"] = _notification.SenderPhone,
            ["url"] = _notification.Url,
        };
        foreach (var kv in tokens)
            sb.Replace("{{" + kv.Key + "}}", kv.Value ?? string.Empty);
        return sb.ToString();
    }

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
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var lbl = new TextBlock { Text = label, Opacity = 0.7, FontSize = 12, VerticalAlignment = VerticalAlignment.Top };
            var val = new TextBlock { Text = value, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 13, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(lbl, 0);
            Grid.SetColumn(val, 1);
            row.Children.Add(lbl);
            row.Children.Add(val);
            panel.Children.Add(row);
        }

        if (_notification.Rows.Count > 0)
        {
            // Server sent dynamic rows — render them as-is
            foreach (var r in _notification.Rows)
                AddRow(r.Label, r.Value);
        }
        else
        {
            // Fallback for older payloads that use flat scalar fields
            AddRow("Date", _notification.AppointmentDate);
            AddRow("Service", _notification.Service);
            AddRow("Staff Member", _notification.Stylist);
        }

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
        _ttlElapsedMs = 0;
        DebugLog.Write("Slideout", $"StartTtlCountdown seconds={seconds}");
        _ttlTimer = DispatcherQueue.CreateTimer();
        _ttlTimer.Interval = TimeSpan.FromMilliseconds(50);
        _ttlTimer.Tick += (_, _) =>
        {
            if (_closing || _ttlPaused) return;
            _ttlElapsedMs += 50;
            var remaining = Math.Max(0, _ttlTotalMs - _ttlElapsedMs);
            var progress = (double)remaining / _ttlTotalMs;
            _ttlBar.ColumnDefinitions[0].Width = new GridLength(progress, GridUnitType.Star);
            _ttlBar.ColumnDefinitions[1].Width = new GridLength(1.0 - progress, GridUnitType.Star);
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
        _slideYTimer?.Stop();
        EnsureOpaque();
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
