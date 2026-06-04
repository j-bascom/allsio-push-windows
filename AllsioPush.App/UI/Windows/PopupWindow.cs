using System.Text;
using AllsioPush.Config;
using AllsioPush.Models;
using AllsioPush.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AllsioPush.UI.Windows;

public class PopupWindow : Form, IRemoteAckTarget
{
    private readonly PushNotification _notification;
    private readonly AckService _ackService;
    private readonly WindowTracker _tracker;
    private readonly WebView2 _webView;
    private readonly FlowLayoutPanel _actionsPanel;
    private readonly Label _loadingLabel;
    private Button? _ackButton;

    public string? NotificationId => _notification.NotificationId;

    public PopupWindow(PushNotification notification, AckService ackService, WindowTracker tracker)
    {
        _notification = notification;
        _ackService = ackService;
        _tracker = tracker;

        var width = Math.Max(320, notification.PopupWidth ?? 600);
        var height = Math.Max(240, notification.PopupHeight ?? 480);

        Text = string.IsNullOrWhiteSpace(notification.Title) ? "Allsio Push" : notification.Title;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(width, height);
        var cursorPos = Cursor.Position;
        var screen = (Screen.FromPoint(cursorPos) ?? Screen.PrimaryScreen!).WorkingArea;
        Location = new Point(
            screen.X + (screen.Width - Width) / 2,
            screen.Y + (screen.Height - Height) / 2);
        TopMost = true;
        MinimizeBox = false;
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;

        _loadingLabel = new Label
        {
            Text = "Loading…",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.Gray,
        };

        _actionsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 48,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8),
            BackColor = SystemColors.Control,
        };

        _webView = new WebView2 { Dock = DockStyle.Fill, Visible = false };

        Controls.Add(_webView);
        Controls.Add(_actionsPanel);
        Controls.Add(_loadingLabel);

        BuildActionButtons();

        Load += async (_, _) =>
        {
            _tracker.Register(this);
            await InitializeWebViewAsync();
        };
        FormClosed += (_, _) => _tracker.Unregister(this);

        if ((notification.Ttl ?? 0) > 0)
        {
            var timer = new System.Windows.Forms.Timer { Interval = notification.Ttl!.Value * 1000 };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (!IsDisposed) Close();
            };
            timer.Start();
        }
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var userDataFolder = Path.Combine(SettingsManager.GetAppDataPath(), "WebView2");
            Directory.CreateDirectory(userDataFolder);
            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await _webView.EnsureCoreWebView2Async(env);

            _webView.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
            _webView.CoreWebView2.NavigationStarting += OnNavigationStarting;

            _loadingLabel.Visible = false;
            _webView.Visible = true;

            if (_notification.TemplateType == "url_popup" && !string.IsNullOrWhiteSpace(_notification.Url))
            {
                _webView.CoreWebView2.Navigate(_notification.Url);
            }
            else if (_notification.TemplateType == "custom_html")
            {
                var html = RenderCustomHtml();
                _webView.CoreWebView2.NavigateToString(html);
            }
            else if (!string.IsNullOrWhiteSpace(_notification.Url))
            {
                _webView.CoreWebView2.Navigate(_notification.Url);
            }
            else
            {
                _webView.CoreWebView2.NavigateToString(PlainContentHtml());
            }
        }
        catch (Exception ex)
        {
            _loadingLabel.Text =
                "Could not load content.\nWebView2 runtime may be missing.\n" + ex.Message;
            _loadingLabel.Visible = true;
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_notification.TemplateType == "custom_html")
        {
            if (!e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                && !e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
            }
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
        {
            sb.Replace("{{" + kv.Key + "}}", HtmlEscape(kv.Value ?? string.Empty));
        }
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
        input.Replace("&", "&amp;")
             .Replace("<", "&lt;")
             .Replace(">", "&gt;")
             .Replace("\"", "&quot;");

    private void BuildActionButtons()
    {
        var any = false;
        foreach (var btn in _notification.Buttons)
        {
            var label = string.IsNullOrWhiteSpace(btn.Label) ? "Button" : btn.Label;
            var button = new Button { Text = label, AutoSize = true, Margin = new Padding(4, 4, 0, 4) };
            switch (btn.Action)
            {
                case "ack":
                    button.Click += async (_, _) =>
                    {
                        button.Enabled = false;
                        await _ackService.Acknowledge(_notification.NotificationId, label, "ack");
                        button.Text = "Acknowledged";
                        await Task.Delay(1200);
                        if (!IsDisposed) Close();
                    };
                    _ackButton ??= button;
                    break;
                case "dismiss":
                    button.Click += async (_, _) =>
                    {
                        button.Enabled = false;
                        await _ackService.Dismiss(_notification.NotificationId, label);
                        if (!IsDisposed) Close();
                    };
                    break;
                case "webhook":
                    {
                        var captured = btn;
                        button.Click += async (_, _) =>
                        {
                            button.Enabled = false;
                            if (!string.IsNullOrWhiteSpace(captured.WebhookUrl))
                                await _ackService.FireWebhook(captured.WebhookUrl!, _notification, label);
                            button.Text = "Sent";
                            await Task.Delay(1200);
                            if (!IsDisposed) Close();
                        };
                        break;
                    }
                default:
                    button.Click += async (_, _) =>
                    {
                        button.Enabled = false;
                        await _ackService.Acknowledge(_notification.NotificationId, label, btn.Action ?? "noop");
                        await Task.Delay(1200);
                        if (!IsDisposed) Close();
                    };
                    break;
            }
            _actionsPanel.Controls.Add(button);
            any = true;
        }

        if (!any && !string.IsNullOrWhiteSpace(_notification.NotificationId))
        {
            var ack = new Button { Text = "Acknowledge", AutoSize = true, Margin = new Padding(4, 4, 0, 4) };
            ack.Click += async (_, _) =>
            {
                ack.Enabled = false;
                await _ackService.Acknowledge(_notification.NotificationId, "Acknowledge", "ack");
                ack.Text = "Acknowledged";
                await Task.Delay(1200);
                if (!IsDisposed) Close();
            };
            _actionsPanel.Controls.Add(ack);
            _ackButton = ack;
        }
    }

    public void RemoteAcknowledged(string acknowledgedBy)
    {
        void Apply()
        {
            if (_ackButton != null)
            {
                _ackButton.Enabled = false;
                var who = string.IsNullOrWhiteSpace(acknowledgedBy) ? "someone" : acknowledgedBy;
                _ackButton.Text = $"Acked by {who}";
            }
            var timer = new System.Windows.Forms.Timer { Interval = 2000 };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (!IsDisposed) Close();
            };
            timer.Start();
        }

        if (InvokeRequired) BeginInvoke((Action)Apply);
        else Apply();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _webView?.Dispose();
        base.Dispose(disposing);
    }
}
