using System.Text;
using AllsioPush.Config;
using AllsioPush.Models;
using AllsioPush.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using WinUIEx;

namespace AllsioPush.UI.Windows;

public class PopupWindow : WindowEx, IRemoteAckTarget
{
    private static readonly SolidColorBrush GreenBrush = new(Microsoft.UI.ColorHelper.FromArgb(255, 34, 197, 94));

    private readonly PushNotification _notification;
    private readonly AckService _ackService;
    private readonly WindowTracker _tracker;
    private readonly WebView2 _webView;
    private readonly StackPanel _actionsPanel;
    private readonly TextBlock _loadingLabel;
    private Button? _ackButton;
    private bool _initialLoadComplete = false;

    public string? NotificationId => _notification.NotificationId;

    public PopupWindow(PushNotification notification, AckService ackService, WindowTracker tracker)
    {
        _notification = notification;
        _ackService = ackService;
        _tracker = tracker;

        var width = Math.Max(320, notification.PopupWidth ?? 600);
        var height = Math.Max(240, notification.PopupHeight ?? 480);

        Title = string.IsNullOrWhiteSpace(notification.Title) ? "Allsio Push" : notification.Title;
        this.SetWindowSize(width, height);
        this.CenterOnScreen();
        WindowIcon.Apply(this);
        IsAlwaysOnTop = true;
        SystemBackdrop = new MicaBackdrop();

        _loadingLabel = new TextBlock
        {
            Text = "Loading…",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
        };
        _webView = new WebView2 { Visibility = Visibility.Collapsed };

        _actionsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Padding = new Thickness(12),
        };

        var root = new Grid { RequestedTheme = ElementTheme.Dark };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_webView, 0);
        Grid.SetRow(_loadingLabel, 0);
        Grid.SetRow(_actionsPanel, 1);
        root.Children.Add(_webView);
        root.Children.Add(_loadingLabel);
        root.Children.Add(_actionsPanel);
        Content = root;

        BuildActionButtons();

        root.Loaded += async (_, _) =>
        {
            _tracker.Register(this);
            await InitializeWebViewAsync();
        };
        Closed += (_, _) => _tracker.Unregister(this);

        if ((notification.Ttl ?? 0) > 0)
        {
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(notification.Ttl!.Value);
            timer.IsRepeating = false;
            timer.Tick += (_, _) => Close();
            timer.Start();
        }
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var userDataFolder = Path.Combine(SettingsManager.GetAppDataPath(), "WebView2");
            Directory.CreateDirectory(userDataFolder);
            var env = await CoreWebView2Environment.CreateWithOptionsAsync(
                null, userDataFolder, new CoreWebView2EnvironmentOptions());
            await _webView.EnsureCoreWebView2Async(env);

            if (_notification.TemplateType == "custom_html")
            {
                _webView.NavigationCompleted += (_, e) =>
                {
                    if (!_initialLoadComplete && e.IsSuccess)
                        _initialLoadComplete = true;
                };
                _webView.CoreWebView2.NewWindowRequested += (_, e) =>
                {
                    e.Handled = true;
                    if (!string.IsNullOrEmpty(e.Uri) &&
                        (e.Uri.StartsWith("https://") || e.Uri.StartsWith("http://") ||
                         e.Uri.StartsWith("tel:")))
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = e.Uri,
                            UseShellExecute = true,
                        });
                    }
                };
                _webView.CoreWebView2.NavigationStarting += (_, e) =>
                {
                    if (!_initialLoadComplete) return;
                    if (e.Uri.StartsWith("https://") || e.Uri.StartsWith("http://") ||
                        e.Uri.StartsWith("tel:"))
                    {
                        e.Cancel = true;
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = e.Uri,
                            UseShellExecute = true,
                        });
                    }
                };
            }
            else
            {
                _webView.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
            }

            _loadingLabel.Visibility = Visibility.Collapsed;
            _webView.Visibility = Visibility.Visible;

            if (_notification.TemplateType == "url_popup" && !string.IsNullOrWhiteSpace(_notification.Url))
                _webView.CoreWebView2.Navigate(_notification.Url);
            else if (_notification.TemplateType == "custom_html")
                _webView.CoreWebView2.NavigateToString(RenderCustomHtml());
            else if (!string.IsNullOrWhiteSpace(_notification.Url))
                _webView.CoreWebView2.Navigate(_notification.Url);
            else
                _webView.CoreWebView2.NavigateToString(PlainContentHtml());
        }
        catch (Exception ex)
        {
            _loadingLabel.Text = "Could not load content.\nWebView2 runtime may be missing.\n" + ex.Message;
            _loadingLabel.Visibility = Visibility.Visible;
        }
    }

    private string RenderCustomHtml()
    {
        var template = _notification.CustomHtml ?? _notification.Content ?? string.Empty;
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
        var sb = new StringBuilder(template);
        foreach (var kv in tokens)
            sb.Replace("{{" + kv.Key + "}}", HtmlEscape(kv.Value ?? string.Empty));
        return sb.ToString();
    }

    private string PlainContentHtml()
    {
        var headerColor = string.IsNullOrWhiteSpace(_notification.HeaderColor) ? "#0078d7" : _notification.HeaderColor;
        var safeColor = HtmlEscape(headerColor);
        var safeTitle = HtmlEscape(_notification.Title);
        var safeContent = HtmlEscape(_notification.Content);
        return
            "<!doctype html><html><head><meta charset='utf-8'><style>" +
            "body{font-family:Segoe UI,sans-serif;margin:0;background:#fff;color:#222;}" +
            "header{background:" + safeColor + ";color:#fff;padding:14px 18px;font-size:16px;font-weight:600;}" +
            "main{padding:18px;font-size:14px;line-height:1.45;}" +
            "</style></head><body>" +
            "<header>" + safeTitle + "</header>" +
            "<main>" + safeContent + "</main>" +
            "</body></html>";
    }

    private static string HtmlEscape(string input) =>
        input.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private void BuildActionButtons()
    {
        var any = false;
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
                        await Task.Delay(1200);
                        Close();
                    };
                    _ackButton ??= button;
                    break;
                case "dismiss":
                    button.Click += async (_, _) =>
                    {
                        button.IsEnabled = false;
                        await _ackService.Dismiss(_notification.NotificationId, label);
                        Close();
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
                        await Task.Delay(1200);
                        Close();
                    };
                    break;
                }
                default:
                    button.Click += async (_, _) =>
                    {
                        button.IsEnabled = false;
                        await _ackService.Acknowledge(_notification.NotificationId, label, btn.Action ?? "noop");
                        await Task.Delay(1200);
                        Close();
                    };
                    break;
            }
            _actionsPanel.Children.Add(button);
            any = true;
        }

        if (!any && !string.IsNullOrWhiteSpace(_notification.NotificationId))
        {
            var ack = new Button { Content = "Acknowledge" };
            ack.Click += async (_, _) =>
            {
                ack.IsEnabled = false;
                await _ackService.Acknowledge(_notification.NotificationId, "Acknowledge", "ack");
                ack.Foreground = GreenBrush;
                await Task.Delay(1200);
                Close();
            };
            _actionsPanel.Children.Add(ack);
            _ackButton = ack;
        }
    }

    public void RemoteAcknowledged(string acknowledgedBy)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (_ackButton != null)
            {
                _ackButton.IsEnabled = false;
                var who = string.IsNullOrWhiteSpace(acknowledgedBy) ? "someone" : acknowledgedBy;
                _ackButton.Content = $"Acked by {who}";
            }
            await Task.Delay(2000);
            Close();
        });
    }
}
