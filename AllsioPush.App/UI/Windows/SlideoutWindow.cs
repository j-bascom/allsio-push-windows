using AllsioPush.Models;
using AllsioPush.Services;

namespace AllsioPush.UI.Windows;

public class SlideoutWindow : Form, IRemoteAckTarget
{
    private static readonly Color BgColor = Color.FromArgb(30, 30, 30);
    private static readonly Color BorderColor = Color.FromArgb(60, 60, 60);
    private static readonly Color TextColor = Color.FromArgb(230, 230, 230);
    private static readonly Color SubtextColor = Color.FromArgb(170, 170, 170);
    private static readonly Color AccentBg = Color.FromArgb(45, 45, 48);
    private static readonly Color ButtonBg = Color.FromArgb(0, 120, 215);
    private static readonly Color ButtonDangerBg = Color.FromArgb(80, 80, 80);

    private readonly PushNotification _notification;
    private readonly AckService _ackService;
    private readonly WindowTracker _tracker;

    private readonly Panel _root;
    private readonly ProgressBar _ttlBar;
    private readonly Button _copyButton;
    private readonly FlowLayoutPanel _actionsPanel;
    private Button? _ackButton;

    private System.Windows.Forms.Timer? _ttlTimer;
    private int _ttlElapsedMs = 0;
    private int _ttlTotalMs = 0;
    private bool _ttlPaused = false;
    private bool _closing = false;

    private Rectangle _targetScreen;

    public string? NotificationId => _notification.NotificationId;

    public SlideoutWindow(PushNotification notification, AckService ackService, WindowTracker tracker)
    {
        _notification = notification;
        _ackService = ackService;
        _tracker = tracker;

        var width = Math.Max(280, notification.PopupWidth ?? 380);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = BorderColor;
        Padding = new Padding(1);
        ClientSize = new Size(width, 200);

        _root = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = BgColor,
            Padding = new Padding(12),
        };
        Controls.Add(_root);

        var header = BuildHeader();
        var content = BuildContent();

        _ttlBar = new ProgressBar
        {
            Dock = DockStyle.Bottom,
            Height = 3,
            Minimum = 0,
            Maximum = 1000,
            Value = 0,
            Style = ProgressBarStyle.Continuous,
            Visible = false,
        };

        _copyButton = new Button
        {
            Text = "Copy",
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            BackColor = AccentBg,
            ForeColor = TextColor,
            Margin = new Padding(0, 0, 8, 0),
        };
        _copyButton.FlatAppearance.BorderColor = BorderColor;
        _copyButton.Click += OnCopyClick;

        _actionsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 8, 0, 0),
            WrapContents = false,
        };
        _actionsPanel.Controls.Add(_copyButton);
        BuildActionButtons();

        _root.Controls.Add(content);
        _root.Controls.Add(_actionsPanel);
        _root.Controls.Add(_ttlBar);
        _root.Controls.Add(header);

        AutoSizeToContent();
        PositionBottomRight();

        if ((notification.Ttl ?? 0) > 0)
        {
            StartTtlCountdown(notification.Ttl!.Value);
        }

        Load += (_, _) => _tracker.Register(this);
        FormClosed += (_, _) => _tracker.Unregister(this);
        MouseEnter += (_, _) => _ttlPaused = true;
        MouseLeave += (_, _) => _ttlPaused = false;
        AttachHoverPause(_root);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOOLWINDOW = 0x00000080;
            const int WS_EX_NOACTIVATE = 0x08000000;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    private void AttachHoverPause(Control parent)
    {
        parent.MouseEnter += (_, _) => _ttlPaused = true;
        parent.MouseLeave += (_, _) => _ttlPaused = false;
        foreach (Control child in parent.Controls)
            AttachHoverPause(child);
    }

    private Panel BuildHeader()
    {
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 28,
            BackColor = BgColor,
        };

        var icon = new Label
        {
            Text = IconForTemplate(_notification.TemplateType),
            Font = new Font(SystemFonts.DefaultFont.FontFamily, 12, FontStyle.Bold),
            ForeColor = TextColor,
            AutoSize = true,
            Location = new Point(0, 4),
        };

        var title = new Label
        {
            Text = string.IsNullOrWhiteSpace(_notification.Title) ? "Notification" : _notification.Title,
            ForeColor = TextColor,
            Font = new Font(SystemFonts.DefaultFont.FontFamily, 10f, FontStyle.Bold),
            Location = new Point(28, 6),
            AutoSize = true,
            MaximumSize = new Size(ClientSize.Width - 80, 0),
        };

        var close = new Button
        {
            Text = "×",
            FlatStyle = FlatStyle.Flat,
            BackColor = BgColor,
            ForeColor = SubtextColor,
            Size = new Size(24, 24),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Location = new Point(ClientSize.Width - 24 - 24, 2),
            TabStop = false,
        };
        close.FlatAppearance.BorderSize = 0;
        close.Click += (_, _) => BeginHide();

        header.Controls.Add(icon);
        header.Controls.Add(title);
        header.Controls.Add(close);

        if (!string.IsNullOrWhiteSpace(_notification.HeaderColor))
        {
            try
            {
                var c = ColorTranslator.FromHtml(_notification.HeaderColor);
                header.BackColor = c;
            }
            catch { }
        }

        return header;
    }

    private static string IconForTemplate(string template) => template switch
    {
        "caller_card" => "☎",
        "appointment_alert" => "📅",
        "url_tab" or "url_popup" => "🔗",
        "custom_html" => "▦",
        _ => "●",
    };

    private Control BuildContent()
    {
        var container = new Panel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            BackColor = BgColor,
            Padding = new Padding(0, 8, 0, 8),
        };

        switch (_notification.TemplateType)
        {
            case "caller_card":
                container.Controls.Add(BuildCallerCardContent());
                break;
            case "appointment_alert":
                container.Controls.Add(BuildAppointmentContent());
                break;
            default:
                container.Controls.Add(BuildPlainContent());
                break;
        }

        return container;
    }

    private Control BuildPlainContent()
    {
        var lbl = new Label
        {
            Text = _notification.Content,
            Dock = DockStyle.Top,
            AutoSize = true,
            MaximumSize = new Size(ClientSize.Width - 24, 0),
            ForeColor = TextColor,
            Font = new Font(SystemFonts.DefaultFont.FontFamily, 9.5f),
        };
        return lbl;
    }

    private Control BuildCallerCardContent()
    {
        var panel = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            Dock = DockStyle.Top,
            BackColor = BgColor,
        };

        var caller = new Label
        {
            Text = _notification.CallerName ?? _notification.CallerPhone ?? "Unknown",
            ForeColor = TextColor,
            Font = new Font(SystemFonts.DefaultFont.FontFamily, 13f, FontStyle.Bold),
            AutoSize = true,
            MaximumSize = new Size(ClientSize.Width - 24, 0),
        };
        panel.Controls.Add(caller);

        if (!string.IsNullOrWhiteSpace(_notification.CallerPhone) && !string.IsNullOrWhiteSpace(_notification.CallerName))
        {
            var phone = new Label
            {
                Text = _notification.CallerPhone,
                ForeColor = SubtextColor,
                Font = new Font(SystemFonts.DefaultFont.FontFamily, 9f),
                AutoSize = true,
            };
            panel.Controls.Add(phone);
        }

        if (!string.IsNullOrWhiteSpace(_notification.Reason))
        {
            var reasonBox = new Panel
            {
                BackColor = AccentBg,
                Padding = new Padding(8),
                AutoSize = true,
                Margin = new Padding(0, 6, 0, 0),
                Dock = DockStyle.Top,
            };
            var reason = new Label
            {
                Text = _notification.Reason,
                ForeColor = TextColor,
                AutoSize = true,
                MaximumSize = new Size(ClientSize.Width - 48, 0),
                Font = new Font(SystemFonts.DefaultFont.FontFamily, 9f),
            };
            reasonBox.Controls.Add(reason);
            panel.Controls.Add(reasonBox);
        }

        return panel;
    }

    private Control BuildAppointmentContent()
    {
        var panel = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            Dock = DockStyle.Top,
            BackColor = BgColor,
        };

        void AddRow(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var row = new Panel { AutoSize = true, BackColor = AccentBg, Padding = new Padding(8), Margin = new Padding(0, 2, 0, 2) };
            var inner = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, BackColor = AccentBg };
            inner.Controls.Add(new Label { Text = label, ForeColor = SubtextColor, AutoSize = true, Font = new Font(SystemFonts.DefaultFont.FontFamily, 8f) });
            inner.Controls.Add(new Label { Text = value, ForeColor = TextColor, AutoSize = true, Font = new Font(SystemFonts.DefaultFont.FontFamily, 10f, FontStyle.Bold) });
            row.Controls.Add(inner);
            panel.Controls.Add(row);
        }

        AddRow("WHEN", _notification.AppointmentDate);
        AddRow("SERVICE", _notification.Service);
        AddRow("STYLIST", _notification.Stylist);

        return panel;
    }

    private void BuildActionButtons()
    {
        foreach (var btn in _notification.Buttons)
        {
            var label = string.IsNullOrWhiteSpace(btn.Label) ? "Button" : btn.Label;
            var button = new Button
            {
                Text = label,
                AutoSize = true,
                FlatStyle = FlatStyle.Flat,
                BackColor = btn.Style == "danger" ? ButtonDangerBg : ButtonBg,
                ForeColor = Color.White,
                Margin = new Padding(0, 0, 8, 0),
            };
            button.FlatAppearance.BorderSize = 0;

            switch (btn.Action)
            {
                case "ack":
                    button.Click += async (_, _) =>
                    {
                        button.Enabled = false;
                        await _ackService.Acknowledge(_notification.NotificationId, label, "ack");
                        button.Text = "Acknowledged";
                        CloseAfter(1200);
                    };
                    if (_ackButton == null) _ackButton = button;
                    break;
                case "dismiss":
                    button.Click += async (_, _) =>
                    {
                        button.Enabled = false;
                        await _ackService.Dismiss(_notification.NotificationId, label);
                        BeginHide();
                    };
                    break;
                case "webhook":
                    {
                        var capturedBtn = btn;
                        var capturedLabel = label;
                        button.Click += async (_, _) =>
                        {
                            button.Enabled = false;
                            if (!string.IsNullOrWhiteSpace(capturedBtn.WebhookUrl))
                                await _ackService.FireWebhook(capturedBtn.WebhookUrl!, _notification, capturedLabel);
                            button.Text = "Sent";
                            CloseAfter(1200);
                        };
                        break;
                    }
                default:
                    button.Click += async (_, _) =>
                    {
                        button.Enabled = false;
                        await _ackService.Acknowledge(_notification.NotificationId, label, btn.Action ?? "noop");
                        CloseAfter(1200);
                    };
                    break;
            }

            _actionsPanel.Controls.Add(button);
        }

        if (_actionsPanel.Controls.Count == 1 && !string.IsNullOrWhiteSpace(_notification.NotificationId))
        {
            var ack = new Button
            {
                Text = "Acknowledge",
                AutoSize = true,
                FlatStyle = FlatStyle.Flat,
                BackColor = ButtonBg,
                ForeColor = Color.White,
                Margin = new Padding(0, 0, 8, 0),
            };
            ack.FlatAppearance.BorderSize = 0;
            ack.Click += async (_, _) =>
            {
                ack.Enabled = false;
                await _ackService.Acknowledge(_notification.NotificationId, "Acknowledge", "ack");
                ack.Text = "Acknowledged";
                CloseAfter(1200);
            };
            _actionsPanel.Controls.Add(ack);
            _ackButton = ack;
        }
    }

    private async void OnCopyClick(object? sender, EventArgs e)
    {
        try
        {
            if (!string.IsNullOrEmpty(_notification.Content))
                Clipboard.SetText(_notification.Content);
        }
        catch { }

        var original = _copyButton.Text;
        _copyButton.Text = "Copied!";
        await Task.Delay(2000);
        if (!IsDisposed) _copyButton.Text = original;
    }

    private void AutoSizeToContent()
    {
        const int minHeight = 120;
        const int maxHeight = 480;

        var content = _root.PreferredSize.Height + _root.Padding.Vertical;
        var h = Math.Max(minHeight, Math.Min(maxHeight, content + 16));
        ClientSize = new Size(ClientSize.Width, h);
    }

    private void PositionBottomRight()
    {
        var cursorPos = Cursor.Position;
        var screen = Screen.FromPoint(cursorPos) ?? Screen.PrimaryScreen!;
        _targetScreen = screen.WorkingArea;
        Location = new Point(_targetScreen.Right - Width - 12, _targetScreen.Bottom - Height - 12);
    }

    private void StartTtlCountdown(int seconds)
    {
        _ttlTotalMs = seconds * 1000;
        _ttlBar.Visible = true;
        _ttlBar.Maximum = _ttlTotalMs;
        _ttlBar.Value = _ttlTotalMs;
        _ttlElapsedMs = 0;

        _ttlTimer = new System.Windows.Forms.Timer { Interval = 50 };
        _ttlTimer.Tick += (_, _) =>
        {
            if (_closing) return;
            if (_ttlPaused) return;

            _ttlElapsedMs += _ttlTimer!.Interval;
            var remaining = Math.Max(0, _ttlTotalMs - _ttlElapsedMs);
            _ttlBar.Value = remaining;
            if (remaining <= 0)
            {
                _ttlTimer.Stop();
                BeginHide();
            }
        };
        _ttlTimer.Start();
    }

    private async void CloseAfter(int ms)
    {
        await Task.Delay(ms);
        if (!IsDisposed) BeginHide();
    }

    private void BeginHide()
    {
        if (_closing) return;
        _closing = true;
        _ttlTimer?.Stop();
        var fade = new System.Windows.Forms.Timer { Interval = 15 };
        fade.Tick += OnFadeTick;
        fade.Start();
    }

    private void OnFadeTick(object? sender, EventArgs e)
    {
        var timer = (System.Windows.Forms.Timer)sender!;
        if (Opacity <= 0.05)
        {
            timer.Stop();
            timer.Dispose();
            Close();
        }
        else
        {
            Opacity -= 0.1;
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
            CloseAfter(2000);
        }

        if (InvokeRequired) BeginInvoke((Action)Apply);
        else Apply();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        SlideUp();
    }

    private async void SlideUp()
    {
        var screen = _targetScreen.IsEmpty
            ? (Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080))
            : _targetScreen;
        var finalY = screen.Bottom - Height - 12;
        var startY = screen.Bottom;
        var steps = 12;
        for (int i = 1; i <= steps; i++)
        {
            if (IsDisposed) return;
            Location = new Point(Location.X, startY - ((startY - finalY) * i / steps));
            await Task.Delay(16);
        }
    }
}
