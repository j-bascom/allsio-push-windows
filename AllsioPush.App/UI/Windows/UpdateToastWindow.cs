using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinUIEx;
using Microsoft.UI.Dispatching;

namespace AllsioPush.UI.Windows;

public class UpdateToastWindow : WindowEx, IStackableToast
{
    private const int ToastWidth = 360;

    private readonly TextBlock _messageText;
    private readonly ProgressBar _progressBar;
    private bool _closing;
    private bool _positionedOnce;
    private DispatcherQueueTimer? _slideTimer;
    private DispatcherQueueTimer? _slideYTimer;
    private bool _slideInComplete;
    private PointInt32 _slideTarget;
    private IntPtr _hwnd;
    private int _pixelWidth;
    private int _pixelHeight;

    private const int SlideInMs = 250;
    private const int SlideOutMs = 180;

    int IStackableToast.PixelWidth => _pixelWidth;
    int IStackableToast.PixelHeight => _pixelHeight;
    bool IStackableToast.IsClosing => _closing;

    public UpdateToastWindow()
    {
        Title = "Allsio Push";
        ConfigurePresenter();

        _messageText = new TextBlock
        {
            Text = "Checking for updates...",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Margin = new Thickness(0, 6, 0, 0),
        };

        _progressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            IsIndeterminate = true,
            Height = 6,
            Margin = new Thickness(0, 10, 0, 0),
        };

        var header = BuildHeader();

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(_messageText);
        stack.Children.Add(_progressBar);

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
        Content = root;

        root.Loaded += (_, _) => SizeAndPosition(root);
        Closed += (_, _) =>
        {
            bool removed;
            lock (SlideoutWindow.StackLock) removed = SlideoutWindow.Stack.Remove(this);
            if (removed) SlideoutWindow.LayoutStack();
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

    private FrameworkElement BuildHeader()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = "Allsio Push",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 14,
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

    private void SizeAndPosition(FrameworkElement root)
    {
        root.Measure(new global::Windows.Foundation.Size(ToastWidth, double.PositiveInfinity));
        var h = Math.Clamp(root.DesiredSize.Height, 80, 200);
        var dpi = this.GetDpiForWindow() / 96.0;
        _pixelWidth = (int)Math.Ceiling(ToastWidth * dpi);
        _pixelHeight = (int)Math.Ceiling(h * dpi);
        AppWindow.Resize(new SizeInt32(_pixelWidth, _pixelHeight));
        lock (SlideoutWindow.StackLock)
        {
            if (!SlideoutWindow.Stack.Contains(this)) SlideoutWindow.Stack.Add(this);
        }
        SlideoutWindow.LayoutStack();
    }

    public void SetMessage(string message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closing) return;
            _messageText.Text = message;
        });
    }

    public void SetProgress(int? percent)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closing) return;
            if (percent == null)
            {
                _progressBar.IsIndeterminate = true;
            }
            else
            {
                _progressBar.IsIndeterminate = false;
                _progressBar.Value = percent.Value;
            }
        });
    }

    public void HideProgress()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closing) return;
            _progressBar.Visibility = Visibility.Collapsed;
        });
    }

    public void AutoClose(int delayMs = 4000)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(delayMs);
            DispatcherQueue.TryEnqueue(BeginHide);
        });
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

    private void BeginHide()
    {
        if (_closing) return;
        _closing = true;
        _slideTimer?.Stop();
        _slideYTimer?.Stop();
        EnsureOpaque();
        StartSlideOut(() => Close());
    }
}
