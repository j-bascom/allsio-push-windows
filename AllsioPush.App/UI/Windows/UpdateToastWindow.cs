using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinUIEx;

namespace AllsioPush.UI.Windows;

public class UpdateToastWindow : WindowEx, IStackableToast
{
    private const int ToastWidth = 360;

    private readonly TextBlock _messageText;
    private readonly ProgressBar _progressBar;
    private bool _closing;
    private int _pixelWidth;
    private int _pixelHeight;

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

    private void BeginHide()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }
}
