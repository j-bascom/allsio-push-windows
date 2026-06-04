using System.Globalization;
using AllsioPush.Config;
using AllsioPush.Models;
using AllsioPush.Services;

namespace AllsioPush.UI.Windows;

public class ScreenPopWindow : Form
{
    private static readonly Color Bg = Color.FromArgb(24, 24, 24);
    private static readonly Color Surface = Color.FromArgb(36, 36, 36);
    private static readonly Color BorderColor = Color.FromArgb(55, 55, 55);
    private static readonly Color TextPrimary = Color.FromArgb(240, 240, 240);
    private static readonly Color TextMuted = Color.FromArgb(140, 140, 140);
    private static readonly Color AccentBlue = Color.FromArgb(59, 130, 246);
    private static readonly Color Green = Color.FromArgb(34, 197, 94);
    private static readonly Color Amber = Color.FromArgb(251, 191, 36);
    private static readonly Color Red = Color.FromArgb(239, 68, 68);

    private const int FormWidth = 420;
    private const int ContentWidth = 372;

    private readonly ScreenPopData _data;
    private readonly AppSettings _settings;
    private readonly WindowTracker _tracker;

    private readonly Label _timerLabel;
    private System.Threading.Timer? _callTimer;
    private bool _closing;

    private bool _dragging;
    private Point _dragOffset;

    public string? CallId => _data.CallId;

    public ScreenPopWindow(ScreenPopData data, AppSettings settings, WindowTracker tracker)
    {
        _data = data;
        _settings = settings;
        _tracker = tracker;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = BorderColor;
        Padding = new Padding(1);
        ClientSize = new Size(FormWidth, 320);

        var scroll = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Bg,
        };

        var stack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Bg,
            Padding = new Padding(16),
            Location = new Point(0, 0),
        };

        _timerLabel = new Label
        {
            Text = "0:00",
            AutoSize = true,
            ForeColor = Green,
            Font = new Font(FontFamily.GenericMonospace, 14f, FontStyle.Bold),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            TextAlign = ContentAlignment.TopRight,
        };

        stack.Controls.Add(BuildHeader());
        if (!string.IsNullOrWhiteSpace(_data.Reason)) stack.Controls.Add(BuildReason());
        if (_data.Phorest != null) stack.Controls.Add(BuildPhorest(_data.Phorest));
        if (_data.Qbo != null) stack.Controls.Add(BuildQbo(_data.Qbo));
        if (_data.PriorCalls.Count > 0) stack.Controls.Add(BuildPriorCalls());
        stack.Controls.Add(BuildButtons());
        stack.Controls.Add(BuildFooter());

        scroll.Controls.Add(stack);
        Controls.Add(scroll);

        SizeToContent(stack);
        PositionTopRight();

        Load += (_, _) =>
        {
            _tracker.RegisterScreenPop(_data.CallId, this);
            StartCallTimer();
        };
        FormClosed += (_, _) =>
        {
            _callTimer?.Dispose();
            _tracker.UnregisterScreenPop(this);
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOOLWINDOW = 0x00000080;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    private Control BuildHeader()
    {
        var header = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            Width = ContentWidth,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Bg,
            Margin = new Padding(0, 0, 0, 12),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var info = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Bg,
            Margin = new Padding(0),
        };

        var name = string.IsNullOrWhiteSpace(_data.CallerName) ? "Unknown Caller" : _data.CallerName!;
        info.Controls.Add(new Label
        {
            Text = "☎  " + name,
            AutoSize = true,
            MaximumSize = new Size(ContentWidth - 70, 0),
            ForeColor = TextPrimary,
            Font = new Font(SystemFonts.DefaultFont.FontFamily, 15f, FontStyle.Bold),
            Margin = new Padding(0),
        });

        if (!string.IsNullOrWhiteSpace(_data.CallerPhone))
        {
            info.Controls.Add(new Label
            {
                Text = _data.CallerPhone,
                AutoSize = true,
                ForeColor = TextMuted,
                Font = new Font(SystemFonts.DefaultFont.FontFamily, 9.5f),
                Margin = new Padding(0, 2, 0, 0),
            });
        }

        if (!string.IsNullOrWhiteSpace(_data.AgentName))
        {
            info.Controls.Add(new Label
            {
                Text = $"Being handled by {_data.AgentName}",
                AutoSize = true,
                ForeColor = TextMuted,
                Font = new Font(SystemFonts.DefaultFont.FontFamily, 8f),
                Margin = new Padding(0, 2, 0, 0),
            });
        }

        header.Controls.Add(info, 0, 0);
        header.Controls.Add(_timerLabel, 1, 0);

        AttachDrag(header);
        return header;
    }

    private Control BuildReason()
    {
        var card = MakeCard();
        var col = CardColumn(card);
        col.Controls.Add(SectionCaption("REASON"));
        col.Controls.Add(new Label
        {
            Text = _data.Reason,
            AutoSize = true,
            MaximumSize = new Size(ContentWidth - 24, 0),
            ForeColor = TextPrimary,
            Font = new Font(SystemFonts.DefaultFont.FontFamily, 9.5f),
            Margin = new Padding(0, 4, 0, 0),
        });
        return card;
    }

    private Control BuildPhorest(PhorestRecord p)
    {
        var container = SectionContainer("\U0001F4CB Phorest Record", "MATCHED", Color.FromArgb(13, 148, 136));
        var card = (Panel)container.Controls[1];
        var col = CardColumn(card);

        var fullName = string.Join(" ", new[] { p.FirstName, p.LastName }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (!string.IsNullOrWhiteSpace(fullName)) col.Controls.Add(Row("Name", fullName));
        if (!string.IsNullOrWhiteSpace(p.Email)) col.Controls.Add(Row("Email", p.Email));
        if (!string.IsNullOrWhiteSpace(p.LastVisitDate))
        {
            var visit = string.IsNullOrWhiteSpace(p.LastService)
                ? p.LastVisitDate
                : $"{p.LastVisitDate} — {p.LastService}";
            col.Controls.Add(Row("Last visit", visit));
        }
        if (!string.IsNullOrWhiteSpace(p.PreferredStylist)) col.Controls.Add(Row("Preferred stylist", p.PreferredStylist));
        if (p.TotalVisits.HasValue) col.Controls.Add(Row("Total visits", p.TotalVisits.Value.ToString()));
        if (p.LifetimeValue.HasValue) col.Controls.Add(Row("Lifetime value", "$" + p.LifetimeValue.Value.ToString("F2", CultureInfo.InvariantCulture)));
        if (!string.IsNullOrWhiteSpace(p.Notes))
        {
            col.Controls.Add(new Label
            {
                Text = p.Notes,
                AutoSize = true,
                MaximumSize = new Size(ContentWidth - 24, 0),
                ForeColor = Amber,
                Font = new Font(SystemFonts.DefaultFont.FontFamily, 9f, FontStyle.Italic),
                Margin = new Padding(0, 4, 0, 0),
            });
        }

        return container;
    }

    private Control BuildQbo(QboRecord q)
    {
        var container = SectionContainer("\U0001F4BC QuickBooks Record", "MATCHED", Green);
        var card = (Panel)container.Controls[1];
        var col = CardColumn(card);

        var primary = !string.IsNullOrWhiteSpace(q.DisplayName) ? q.DisplayName
            : !string.IsNullOrWhiteSpace(q.CompanyName) ? q.CompanyName : null;
        if (!string.IsNullOrWhiteSpace(primary)) col.Controls.Add(Row("Name", primary));
        if (!string.IsNullOrWhiteSpace(q.CompanyName) && !string.Equals(q.CompanyName, primary, StringComparison.Ordinal))
            col.Controls.Add(Row("Company", q.CompanyName));
        if (!string.IsNullOrWhiteSpace(q.Email)) col.Controls.Add(Row("Email", q.Email));
        if (!string.IsNullOrWhiteSpace(q.Phone)) col.Controls.Add(Row("Phone", q.Phone));
        if (!string.IsNullOrWhiteSpace(q.BillingAddress)) col.Controls.Add(Row("Billing", q.BillingAddress));
        if (q.Balance.HasValue)
        {
            var balance = "$" + q.Balance.Value.ToString("F2", CultureInfo.InvariantCulture);
            var row = Row("Balance", balance);
            if (q.Balance.Value > 0 && row is Control c && c.Controls.Count > 1)
                c.Controls[1].ForeColor = Red;
            col.Controls.Add(row);
        }

        return container;
    }

    private Control BuildPriorCalls()
    {
        var container = SectionContainer("\U0001F4DE Prior Calls", null, Green);
        var card = (Panel)container.Controls[1];
        var col = CardColumn(card);

        foreach (var pc in _data.PriorCalls.Take(5))
        {
            var date = pc.CallDate == default ? "" : pc.CallDate.ToString("MMM d, h:mm tt", CultureInfo.InvariantCulture);
            var reason = pc.Reason ?? "";
            if (reason.Length > 40) reason = reason.Substring(0, 40) + "…";

            var parts = new[] { date, pc.Duration, reason }.Where(s => !string.IsNullOrWhiteSpace(s));
            col.Controls.Add(new Label
            {
                Text = string.Join("  |  ", parts),
                AutoSize = true,
                MaximumSize = new Size(ContentWidth - 24, 0),
                ForeColor = TextPrimary,
                Font = new Font(SystemFonts.DefaultFont.FontFamily, 9f),
                Margin = new Padding(0, 4, 0, 0),
            });

            if (!string.IsNullOrWhiteSpace(pc.Outcome))
            {
                col.Controls.Add(new Label
                {
                    Text = pc.Outcome,
                    AutoSize = true,
                    MaximumSize = new Size(ContentWidth - 24, 0),
                    ForeColor = TextMuted,
                    Font = new Font(SystemFonts.DefaultFont.FontFamily, 8f),
                    Margin = new Padding(0, 0, 0, 2),
                });
            }
        }

        return container;
    }

    private Control BuildButtons()
    {
        var panel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Bg,
            Margin = new Padding(0, 4, 0, 0),
        };

        var openBtn = MakeButton("\U0001F310  Open in Browser", AccentBlue);
        openBtn.Click += (_, _) => OpenInBrowser();
        panel.Controls.Add(openBtn);

        if (!string.IsNullOrWhiteSpace(_data.CallerPhone))
        {
            var copyBtn = MakeButton("\U0001F4CB  Copy Number", Surface);
            copyBtn.Click += async (_, _) =>
            {
                try { Clipboard.SetText(_data.CallerPhone!); } catch { }
                var original = copyBtn.Text;
                copyBtn.Text = "Copied!";
                await Task.Delay(2000);
                if (!IsDisposed) copyBtn.Text = original;
            };
            panel.Controls.Add(copyBtn);
        }

        var closeBtn = MakeButton("✕  Close", Surface);
        closeBtn.Click += (_, _) => BeginFadeOut();
        panel.Controls.Add(closeBtn);

        return panel;
    }

    private Control BuildFooter()
    {
        var panel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Bg,
            Margin = new Padding(0, 8, 0, 0),
        };
        panel.Controls.Add(new Panel
        {
            Width = ContentWidth,
            Height = 1,
            BackColor = BorderColor,
            Margin = new Padding(0, 0, 0, 6),
        });
        panel.Controls.Add(new Label
        {
            Text = "Call started " + _data.CallStarted.ToString("h:mm tt", CultureInfo.InvariantCulture),
            AutoSize = true,
            ForeColor = TextMuted,
            Font = new Font(SystemFonts.DefaultFont.FontFamily, 8f),
            Margin = new Padding(0),
        });
        return panel;
    }

    private Panel SectionContainer(string title, string? badge, Color badgeColor)
    {
        var wrap = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Bg,
            Margin = new Padding(0, 0, 0, 12),
        };

        var headerRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Bg,
            Margin = new Padding(0, 0, 0, 4),
        };
        headerRow.Controls.Add(new Label
        {
            Text = title,
            AutoSize = true,
            ForeColor = TextPrimary,
            Font = new Font(SystemFonts.DefaultFont.FontFamily, 9.5f, FontStyle.Bold),
            Margin = new Padding(0, 2, 0, 0),
        });
        if (!string.IsNullOrWhiteSpace(badge))
        {
            headerRow.Controls.Add(new Label
            {
                Text = badge,
                AutoSize = true,
                BackColor = badgeColor,
                ForeColor = Color.White,
                Font = new Font(SystemFonts.DefaultFont.FontFamily, 7f, FontStyle.Bold),
                Padding = new Padding(4, 2, 4, 2),
                Margin = new Padding(8, 2, 0, 0),
            });
        }

        wrap.Controls.Add(headerRow);
        wrap.Controls.Add(MakeCard());
        return wrap;
    }

    private static Panel MakeCard() => new Panel
    {
        Width = ContentWidth,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        BackColor = Surface,
        Padding = new Padding(12),
        Margin = new Padding(0, 0, 0, 12),
    };

    private static FlowLayoutPanel CardColumn(Panel card)
    {
        var col = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Surface,
            Margin = new Padding(0),
            Dock = DockStyle.Top,
        };
        card.Controls.Add(col);
        return col;
    }

    private static Label SectionCaption(string text) => new Label
    {
        Text = text,
        AutoSize = true,
        ForeColor = TextMuted,
        Font = new Font(SystemFonts.DefaultFont.FontFamily, 7.5f, FontStyle.Bold),
        Margin = new Padding(0),
    };

    private Control Row(string label, string? value)
    {
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Surface,
            Margin = new Padding(0, 1, 0, 1),
        };
        row.Controls.Add(new Label
        {
            Text = label + ":",
            AutoSize = true,
            ForeColor = TextMuted,
            Font = new Font(SystemFonts.DefaultFont.FontFamily, 9f),
            Margin = new Padding(0, 0, 6, 0),
        });
        row.Controls.Add(new Label
        {
            Text = value ?? "",
            AutoSize = true,
            MaximumSize = new Size(ContentWidth - 110, 0),
            ForeColor = TextPrimary,
            Font = new Font(SystemFonts.DefaultFont.FontFamily, 9f, FontStyle.Bold),
            Margin = new Padding(0),
        });
        return row;
    }

    private static Button MakeButton(string text, Color back)
    {
        var btn = new Button
        {
            Text = text,
            Width = ContentWidth,
            Height = 38,
            FlatStyle = FlatStyle.Flat,
            BackColor = back,
            ForeColor = TextPrimary,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(SystemFonts.DefaultFont.FontFamily, 9.5f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 8),
            TabStop = false,
        };
        btn.FlatAppearance.BorderColor = BorderColor;
        btn.FlatAppearance.BorderSize = 1;
        return btn;
    }

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

    private void SizeToContent(Control stack)
    {
        var preferred = stack.PreferredSize.Height;
        var h = Math.Max(320, Math.Min(700, preferred));
        ClientSize = new Size(FormWidth, h);
    }

    private void PositionTopRight()
    {
        var screen = Screen.FromPoint(Cursor.Position) ?? Screen.PrimaryScreen!;
        var wa = screen.WorkingArea;
        Location = new Point(wa.Right - Width - 12, wa.Top + 12);
    }

    private void AttachDrag(Control control)
    {
        control.MouseDown += OnDragMouseDown;
        control.MouseMove += OnDragMouseMove;
        control.MouseUp += OnDragMouseUp;
        foreach (Control child in control.Controls)
            AttachDrag(child);
    }

    private void OnDragMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _dragging = true;
        var screenPoint = ((Control)sender!).PointToScreen(e.Location);
        _dragOffset = new Point(screenPoint.X - Location.X, screenPoint.Y - Location.Y);
    }

    private void OnDragMouseMove(object? sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var screenPoint = ((Control)sender!).PointToScreen(e.Location);
        Location = new Point(screenPoint.X - _dragOffset.X, screenPoint.Y - _dragOffset.Y);
    }

    private void OnDragMouseUp(object? sender, MouseEventArgs e) => _dragging = false;

    private void StartCallTimer()
    {
        _callTimer = new System.Threading.Timer(
            OnCallTimerTick, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
    }

    private void OnCallTimerTick(object? state)
    {
        if (IsDisposed || _closing) return;
        try
        {
            if (InvokeRequired) BeginInvoke((Action)UpdateTimerLabel);
            else UpdateTimerLabel();
        }
        catch { }
    }

    private void UpdateTimerLabel()
    {
        if (IsDisposed) return;
        var elapsed = DateTime.Now - _data.CallStarted;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        var minutes = (int)elapsed.TotalMinutes;
        _timerLabel.Text = $"{minutes}:{elapsed.Seconds:D2}";
    }

    public void CallEnded()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke((Action)CallEnded); return; }
        BeginFadeOut();
    }

    private void BeginFadeOut()
    {
        if (_closing) return;
        _closing = true;
        _callTimer?.Dispose();
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
}
