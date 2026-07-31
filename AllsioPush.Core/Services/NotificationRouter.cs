using System.Globalization;
using AllsioPush.Config;
using AllsioPush.Models;

namespace AllsioPush.Services;

public class NotificationRouter
{
    private readonly AppSettings _settings;
    private readonly SynchronizationContext _uiContext;
    private readonly ToastService _toastService;
    private readonly AckService _ackService;
    private readonly WindowTracker _windowTracker;
    private readonly HistoryService _history;
    private readonly INotificationPresenter _presenter;
    private readonly ToneService _toneService;
    private AuthSession? _session;

    public event Action<HistoryEntry>? OnEntrySaved;

    // The router needs the live session to read SmsNotificationScope for SMS
    // scope filtering. Updated on connect and cleared on sign-out.
    public void SetSession(AuthSession? session) => _session = session;

    public NotificationRouter(
        AppSettings settings,
        SynchronizationContext uiContext,
        ToastService toastService,
        AckService ackService,
        WindowTracker windowTracker,
        HistoryService history,
        INotificationPresenter presenter,
        ToneService toneService)
    {
        _settings = settings;
        _uiContext = uiContext;
        _toastService = toastService;
        _ackService = ackService;
        _windowTracker = windowTracker;
        _history = history;
        _presenter = presenter;
        _toneService = toneService;
    }

    public void Route(PushNotification notification, bool persistHistory = true)
    {
        // call_ended is a control event, not a notification: it carries no UI
        // and is never persisted or routed to a toast/slideout. It simply closes
        // the matching screen pop. Normalize casing/underscores so the server
        // may send "call_ended", "callEnded", or "call_end".
        var normalized = notification.TemplateType?.ToLowerInvariant().Replace("_", "");
        if (normalized == "callended" || normalized == "callend")
        {
            var endedCallId = GetCallId(notification);
            if (!string.IsNullOrWhiteSpace(endedCallId))
                _uiContext.Post(_ => _windowTracker.HandleCallEnded(endedCallId!), null);
            return;
        }

        // SMS plays its own sound only after passing scope filtering (below), so a
        // scope-suppressed message stays silent. Everything else sounds now.
        var isSms = string.Equals(notification.TemplateType, "sms", StringComparison.OrdinalIgnoreCase);
        if (!isSms)
            _toneService.PlayNotificationSound(notification.Sound);

        var receivedAt = DateTime.UtcNow;

        // Persist to local history in the background; surface the saved entry
        // to subscribers (e.g. open HistoryWindow) without blocking routing.
        // Skipped when replaying an existing entry (e.g. clicking a history card)
        // so re-opening a notification doesn't create a duplicate record.
        if (persistHistory)
        {
            _ = Task.Run(async () =>
            {
                var entry = await _history.SaveNotification(notification, receivedAt);
                if (entry != null)
                {
                    try { OnEntrySaved?.Invoke(entry); }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Router] OnEntrySaved handler threw: {ex.Message}");
                    }
                }
            });
        }

        // url_tab opens a new tab in the user's default browser; url_popup opens a
        // dedicated new browser window. Both use the real default browser (with the
        // user's session/cookies) rather than the embedded WebView2 that url_popup
        // previously rendered into.
        if (notification.TemplateType == "url_tab")
        {
            if (!string.IsNullOrWhiteSpace(notification.Url))
                OpenUrl(notification.Url!);
            return;
        }

        if (notification.TemplateType == "url_popup")
        {
            if (!string.IsNullOrWhiteSpace(notification.Url))
                OpenUrlInNewWindow(notification.Url!);
            return;
        }

        // caller_card surfaces the standard notification (toast or slideout) AND,
        // when CRM/context data is present, a rich screen pop in the top-right.
        // The screen pop shows regardless of the DeferToToast setting.
        if (string.Equals(notification.TemplateType, "caller_card", StringComparison.OrdinalIgnoreCase))
        {
            if (_settings.DeferToToast)
            {
                if (!_toastService.TryShow(notification))
                    _uiContext.Post(_ => OpenSlideoutWindow(notification), null);
            }
            else
            {
                _uiContext.Post(_ => OpenSlideoutWindow(notification), null);
            }

            var popData = ExtractScreenPopData(notification);
            if (popData != null
                && (popData.Phorest != null
                    || popData.Qbo != null
                    || popData.PriorCalls.Count > 0))
            {
                _uiContext.Post(_ => OpenScreenPopWindow(popData), null);
            }
            return;
        }

        // SMS always opens as a native slideout, regardless of the DeferToToast
        // setting — the inline reply panel requires the native window. History is
        // already persisted above; the sound is played here only when the message
        // passes scope filtering, so suppressed messages stay silent.
        if (isSms)
        {
            // Filter by the user's SMS notification scope before surfacing.
            var scope = _session?.SmsNotificationScope ?? "mine";
            var assignmentType = notification.AssignmentType ?? "unassigned";
            bool shouldShow = scope switch
            {
                "personal_only" => assignmentType == "personal",
                "mine" => assignmentType == "personal" || assignmentType == "department",
                _ => true, // "all" — show everything, including unassigned
            };
            if (!shouldShow)
            {
                System.Diagnostics.Debug.WriteLine($"[SMS] Suppressed by scope {scope}: {assignmentType} (no sound)");
                return;
            }

            _toneService.PlayNotificationSound(notification.Sound);
            _uiContext.Post(_ => OpenSmsSlideout(notification, startExpanded: false), null);
            return;
        }

        if (_settings.DeferToToast)
        {
            if (!_toastService.TryShow(notification))
                _uiContext.Post(_ => OpenSlideoutWindow(notification), null);
            return;
        }

        if (notification.DisplayMode == "popup")
        {
            _uiContext.Post(_ => OpenPopupWindow(notification), null);
            return;
        }

        _uiContext.Post(_ => OpenSlideoutWindow(notification), null);
    }

    private static string? GetCallId(PushNotification n)
    {
        if (n.Extras.TryGetValue("callId", out var a) && !string.IsNullOrWhiteSpace(a)) return a;
        if (n.Extras.TryGetValue("call_id", out var b) && !string.IsNullOrWhiteSpace(b)) return b;
        return null;
    }

    private static ScreenPopData? ExtractScreenPopData(PushNotification n)
    {
        if (!string.Equals(n.TemplateType, "caller_card", StringComparison.OrdinalIgnoreCase))
            return null;

        string? Get(params string[] keys)
        {
            foreach (var k in keys)
                if (n.Extras.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v))
                    return v;
            return null;
        }

        int? GetInt(params string[] keys) =>
            int.TryParse(Get(keys), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;

        decimal? GetDecimal(params string[] keys) =>
            decimal.TryParse(Get(keys), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null;

        var data = new ScreenPopData
        {
            CallerName = n.CallerName,
            CallerPhone = n.CallerPhone,
            Reason = n.Reason,
            AgentName = Get("agentName", "agent_name"),
            CallId = GetCallId(n),
            CallStarted = DateTime.Now,
            SourceNotification = n,
            PriorCalls = n.PriorCalls,
        };

        var phorest = new PhorestRecord
        {
            ClientId = Get("phorest_client_id"),
            FirstName = Get("phorest_first_name"),
            LastName = Get("phorest_last_name"),
            Email = Get("phorest_email"),
            Phone = Get("phorest_phone"),
            LastVisitDate = Get("phorest_last_visit"),
            LastService = Get("phorest_last_service"),
            PreferredStylist = Get("phorest_preferred_stylist"),
            Notes = Get("phorest_notes"),
            TotalVisits = GetInt("phorest_total_visits"),
            LifetimeValue = GetDecimal("phorest_lifetime_value"),
        };
        if (HasAnyValue(phorest.ClientId, phorest.FirstName, phorest.LastName, phorest.Email,
                phorest.Phone, phorest.LastVisitDate, phorest.LastService, phorest.PreferredStylist,
                phorest.Notes) || phorest.TotalVisits.HasValue || phorest.LifetimeValue.HasValue)
        {
            data.Phorest = phorest;
        }

        var qbo = new QboRecord
        {
            CustomerId = Get("qbo_customer_id"),
            DisplayName = Get("qbo_display_name"),
            CompanyName = Get("qbo_company_name"),
            Email = Get("qbo_email"),
            Phone = Get("qbo_phone"),
            BillingAddress = Get("qbo_billing_address"),
            Balance = GetDecimal("qbo_balance"),
        };
        if (HasAnyValue(qbo.CustomerId, qbo.DisplayName, qbo.CompanyName, qbo.Email,
                qbo.Phone, qbo.BillingAddress) || qbo.Balance.HasValue)
        {
            data.Qbo = qbo;
        }

        return data;
    }

    private static bool HasAnyValue(params string?[] values) =>
        values.Any(v => !string.IsNullOrWhiteSpace(v));

    private void OpenUrl(string url)
    {
        try { _presenter.OpenUrl(url); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Router] OpenUrl failed: {ex.Message}");
        }
    }

    private void OpenUrlInNewWindow(string url)
    {
        try { _presenter.OpenUrlInNewWindow(url); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Router] OpenUrlInNewWindow failed: {ex.Message}");
        }
    }

    private void OpenPopupWindow(PushNotification notification)
    {
        try { _presenter.ShowPopup(notification); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Router] OpenPopupWindow failed: {ex.Message}");
        }
    }

    private void OpenSlideoutWindow(PushNotification notification)
    {
        try { _presenter.ShowSlideout(notification); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Router] OpenSlideoutWindow failed: {ex.Message}");
        }
    }

    private void OpenScreenPopWindow(ScreenPopData data)
    {
        try { _presenter.ShowScreenPop(data); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Router] OpenScreenPopWindow failed: {ex.Message}");
        }
    }

    private void OpenSmsSlideout(PushNotification notification, bool startExpanded)
    {
        try { _presenter.ShowSmsSlideout(notification, startExpanded); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Router] OpenSmsSlideout failed: {ex.Message}");
        }
    }

}
