using AllsioPush.Config;
using AllsioPush.Models;
using AllsioPush.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Dispatching;
using Windows.Graphics;
using Windows.System;
using WinUIEx;

namespace AllsioPush.UI.Windows;

/// Specialized slideout for "sms" notifications. Mirrors SlideoutWindow's
/// positioning / theming / slide animation, but adds an expandable inline
/// reply panel and never auto-dismisses (SMS stays until actioned).
public class SmsSlideoutWindow : WindowEx, IRemoteAckTarget, IStackableToast
{
    private static readonly SolidColorBrush GreenBrush = new(ColorHelper.FromArgb(255, 34, 197, 94));
    private static readonly SolidColorBrush RedBrush = new(ColorHelper.FromArgb(255, 239, 68, 68));

    private const int Margin = 12;
    private const int SlideInMs = 250;
    private const int SlideOutMs = 180;
    private const int ExpandMs = 200;

    private readonly PushNotification _notification;
    private readonly AppSettings _settings;
    private readonly AckService _ackService;
    private readonly WindowTracker _tracker;

    private readonly int _width = 380;
    private bool _startExpanded;

    private Grid _root = null!;
    private StackPanel _replyPanel = null!;
    private TextBox _replyBox = null!;
    private TextBlock _statusText = null!;
    private Button _replyButton = null!;
    private Button _sendButton = null!;

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _slideTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _slideYTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _expandTimer;
    private bool _slideInComplete;
    private bool _positionedOnce;
    private PointInt32 _slideTarget;
    private IntPtr _hwnd;
    private bool _closing;
    private bool _expanded;
    private bool _sending;

    private int _pixelWidth;
    private int _pixelHeight;

    int IStackableToast.PixelWidth => _pixelWidth;
    int IStackableToast.PixelHeight => _pixelHeight;
    bool IStackableToast.IsClosing => _closing;

    public string? NotificationId => _notification.NotificationId;

    public SmsSlideoutWindow(
        PushNotification notification,
        AppSettings settings,
        AckService ackService,
        WindowTracker tracker,
        bool startExpanded = false)
    {
        _notification = notification;
        _settings = settings;
        _ackService = ackService;
        _tracker = tracker;
        _startExpanded = startExpanded;

        Title = "Allsio Push";
        ConfigurePresenter();

        _root = BuildRoot();
        Content = _root;

        _root.Loaded += (_, _) =>
        {
            _tracker.Register(this);
            SizeAndPosition(_root);
            if (_startExpanded) DispatcherQueue.TryEnqueue(() => SetExpanded(true));
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
        InitLayeredAlpha();
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

    // ---- Layout -----------------------------------------------------------

    private Grid BuildRoot()
    {
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(BuildHeader());
        stack.Children.Add(BuildPreview());
        _replyPanel = BuildReplyPanel();
        stack.Children.Add(_replyPanel);
        stack.Children.Add(BuildActions());

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
        Grid.SetRow(card, 0);
        root.Children.Add(card);
        return root;
    }

    private FrameworkElement BuildHeader()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FontIcon
        {
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            Glyph = "", // Message
            FontSize = 16,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(icon, 0);

        var who = !string.IsNullOrWhiteSpace(_notification.SenderName)
            ? _notification.SenderName
            : (!string.IsNullOrWhiteSpace(_notification.SenderPhone) ? _notification.SenderPhone : "Unknown");
        var title = new TextBlock
        {
            Text = $"SMS — {who}",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 14,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(title, 1);

        var close = new Button
        {
            Content = "✕",
            Padding = new Thickness(6, 0, 6, 0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        close.Click += (_, _) => OnCloseRequested();
        Grid.SetColumn(close, 2);

        grid.Children.Add(icon);
        grid.Children.Add(title);
        grid.Children.Add(close);
        return grid;
    }

    private FrameworkElement BuildPreview()
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock
        {
            Text = _notification.Content,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 2,
            FontSize = 13,
        });

        // Show the phone as a muted caption only when a name is the headline.
        if (!string.IsNullOrWhiteSpace(_notification.SenderName)
            && !string.IsNullOrWhiteSpace(_notification.SenderPhone))
        {
            panel.Children.Add(new TextBlock
            {
                Text = _notification.SenderPhone,
                Opacity = 0.7,
                FontSize = 12,
            });
        }
        return panel;
    }

    private StackPanel BuildReplyPanel()
    {
        var panel = new StackPanel { Spacing = 6, Visibility = Visibility.Collapsed };

        panel.Children.Add(new TextBlock
        {
            Text = "Last message:",
            Opacity = 0.7,
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0),
        });
        panel.Children.Add(new TextBlock
        {
            Text = _notification.Content,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
        });

        _replyBox = new TextBox
        {
            PlaceholderText = "Type your reply...",
            AcceptsReturn = false,
            MaxLength = 1600,
            TextWrapping = TextWrapping.NoWrap,
        };
        _replyBox.KeyDown += OnReplyKeyDown;
        panel.Children.Add(_replyBox);

        _statusText = new TextBlock
        {
            FontSize = 12,
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap,
        };
        _statusText.Tapped += (_, _) => { if (!_sending) _ = DoSend(); };
        panel.Children.Add(_statusText);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        _sendButton = new Button { Content = "Send" };
        ApplyAccentStyle(_sendButton);
        _sendButton.Click += (_, _) => { if (!_sending) _ = DoSend(); };
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => SetExpanded(false);
        buttons.Children.Add(_sendButton);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        return panel;
    }

    private FrameworkElement BuildActions()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 4, 0, 0),
        };

        _replyButton = new Button { Content = "Reply" };
        ApplyAccentStyle(_replyButton);
        _replyButton.Click += (_, _) => SetExpanded(!_expanded);

        var open = new Button { Content = "Open" };
        open.Click += (_, _) => OnOpenClicked();

        panel.Children.Add(_replyButton);
        panel.Children.Add(open);
        return panel;
    }

    private static void ApplyAccentStyle(Button b)
    {
        try
        {
            if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var s) && s is Style style)
                b.Style = style;
        }
        catch { }
    }

    // ---- Sizing / positioning (mirrors SlideoutWindow) --------------------

    private void SizeAndPosition(FrameworkElement root)
    {
        _pixelWidth = ToPixels(_width);
        _pixelHeight = MeasureHeightPixels(root);
        AppWindow.Resize(new SizeInt32(_pixelWidth, _pixelHeight));
        AddToStack();
    }

    private int ToPixels(double dip)
    {
        var dpi = this.GetDpiForWindow() / 96.0;
        return (int)Math.Ceiling(dip * dpi);
    }

    private int MeasureHeightPixels(FrameworkElement root)
    {
        root.Measure(new global::Windows.Foundation.Size(_width, double.PositiveInfinity));
        var h = Math.Clamp(root.DesiredSize.Height, 120, 600);
        var dpi = this.GetDpiForWindow() / 96.0;
        return (int)Math.Ceiling(h * dpi) + 2;
    }

    private void AddToStack()
    {
        lock (SlideoutWindow.StackLock)
        {
            if (!SlideoutWindow.Stack.Contains(this)) SlideoutWindow.Stack.Add(this);
        }
        SlideoutWindow.LayoutStack();
    }

    private void RemoveFromStack()
    {
        bool removed;
        lock (SlideoutWindow.StackLock) removed = SlideoutWindow.Stack.Remove(this);
        if (removed) SlideoutWindow.LayoutStack();
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
            SetWindowOpacity((byte)(t * t * t * 255));
            try { AppWindow.Move(new PointInt32(x, _slideTarget.Y)); } catch { }
            if (t >= 1.0)
            {
                _slideInComplete = true;
                _slideTimer?.Stop();
                EnsureOpaque();
            }
        };
        _slideTimer.Start();
    }

    private void StartRepositionAnim(PointInt32 target)
    {
        var startY = AppWindow.Position.Y;
        if (startY == target.Y) return;
        var elapsed = 0;
        _slideYTimer?.Stop();
        _slideYTimer = DispatcherQueue.CreateTimer();
        _slideYTimer.Interval = TimeSpan.FromMilliseconds(16);
        _slideYTimer.IsRepeating = true;
        _slideYTimer.Tick += (_, _) =>
        {
            elapsed += 16;
            var t = Math.Min(1.0, (double)elapsed / ExpandMs);
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

    // ---- Expand / collapse ------------------------------------------------

    private void SetExpanded(bool expanded)
    {
        if (_closing || _expanded == expanded) return;
        _expanded = expanded;
        _replyPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        _replyButton.Content = expanded ? "Cancel" : "Reply";

        if (!expanded)
        {
            _statusText.Visibility = Visibility.Collapsed;
        }

        var targetHeight = MeasureHeightPixels(_root);
        AnimateToHeight(targetHeight, () =>
        {
            if (expanded)
            {
                _replyBox.Focus(FocusState.Programmatic);
            }
        });
    }

    // Grows/shrinks the window while keeping it anchored to the bottom-right of
    // the work area (12px margins), then re-settles the toast stack.
    private void AnimateToHeight(int targetPixelHeight, Action onComplete)
    {
        var area = DisplayArea.Primary.WorkArea;
        var startH = _pixelHeight;
        var startY = AppWindow.Position.Y;
        var x = area.X + area.Width - _pixelWidth - Margin;
        var endY = area.Y + area.Height - Margin - targetPixelHeight;
        _pixelHeight = targetPixelHeight;

        var elapsed = 0;
        _expandTimer?.Stop();
        _expandTimer = DispatcherQueue.CreateTimer();
        _expandTimer.Interval = TimeSpan.FromMilliseconds(16);
        _expandTimer.IsRepeating = true;
        _expandTimer.Tick += (_, _) =>
        {
            elapsed += 16;
            var t = Math.Min(1.0, (double)elapsed / ExpandMs);
            var eased = 1.0 - (1.0 - t) * (1.0 - t);
            var h = (int)(startH + (targetPixelHeight - startH) * eased);
            var y = (int)(startY + (endY - startY) * eased);
            try { AppWindow.MoveAndResize(new RectInt32(x, y, _pixelWidth, h)); } catch { }
            if (t >= 1.0)
            {
                _expandTimer?.Stop();
                try { AppWindow.MoveAndResize(new RectInt32(x, endY, _pixelWidth, targetPixelHeight)); } catch { }
                SlideoutWindow.LayoutStack();
                onComplete();
            }
        };
        _expandTimer.Start();
    }

    // ---- Actions ----------------------------------------------------------

    private void OnReplyKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            if (!_sending) _ = DoSend();
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            SetExpanded(false);
        }
    }

    private async Task DoSend()
    {
        var text = _replyBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;

        _sending = true;
        _sendButton.IsEnabled = false;
        _sendButton.Content = "Sending...";
        _statusText.Visibility = Visibility.Collapsed;

        var ok = await _ackService.SendSmsReply(_notification.ConversationId ?? "", text!);

        if (ok)
        {
            _statusText.Text = "Sent ✓";
            _statusText.Foreground = GreenBrush;
            _statusText.Visibility = Visibility.Visible;
            CloseAfter(1000);
        }
        else
        {
            _sending = false;
            _sendButton.IsEnabled = true;
            _sendButton.Content = "Send";
            _statusText.Text = "Failed to send — tap to retry";
            _statusText.Foreground = RedBrush;
            _statusText.Visibility = Visibility.Visible;
            _replyBox.Focus(FocusState.Programmatic);
        }
    }

    private void OnOpenClicked()
    {
        var convId = _notification.ConversationId ?? "";
        var url = $"{_settings.AdminBase}/dashboard/sms/inbox?conversation={Uri.EscapeDataString(convId)}";
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SMS] Open failed: {ex.Message}");
        }
        BeginHide();
    }

    private async void OnCloseRequested()
    {
        if (_expanded && !string.IsNullOrWhiteSpace(_replyBox.Text))
        {
            var discard = await ConfirmDiscard();
            if (!discard) return;
        }
        BeginHide();
    }

    private async Task<bool> ConfirmDiscard()
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = "Discard reply?",
                Content = "Your unsent reply will be lost.",
                PrimaryButtonText = "Discard",
                CloseButtonText = "Keep",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = _root.XamlRoot,
                RequestedTheme = ElementTheme.Dark,
            };
            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary;
        }
        catch
        {
            // If the dialog can't show, fall back to closing.
            return true;
        }
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
        _slideTimer?.Stop();
        _slideYTimer?.Stop();
        _expandTimer?.Stop();
        EnsureOpaque();
        StartSlideOut(() => Close());
    }

    public void RemoteAcknowledged(string acknowledgedBy)
    {
        // SMS notifications aren't remote-acked, but if the same id is acked
        // elsewhere just close this slideout to stay consistent.
        DispatcherQueue.TryEnqueue(() => BeginHide());
    }
}
