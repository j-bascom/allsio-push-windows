using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AllsioPush.Config;
using AllsioPush.Models;

namespace AllsioPush.Services;

public class AckService
{
    private readonly HttpClient _http;        // pinned — own API only
    private readonly HttpClient _webhookHttp; // unpinned — external webhook URLs
    private readonly AppSettings _settings;
    private readonly HistoryService _history;
    private AuthSession? _session;

    public event Action<string, string, string?>? OnActionRecorded;

    public AckService(AppSettings settings, HistoryService history)
    {
        _settings = settings;
        _history = history;
        _http = AuthService.CreatePinnedHttpClient();
        _webhookHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public void SetSession(AuthSession? session) => _session = session;

    public async Task Acknowledge(string? notificationId, string buttonLabel, string buttonAction)
    {
        if (string.IsNullOrWhiteSpace(notificationId))
        {
            System.Diagnostics.Debug.WriteLine("[Ack] Skipping ack — no notificationId");
            return;
        }

        var token = _session?.Token;
        if (!string.IsNullOrWhiteSpace(token))
        {
            try
            {
                var path = $"/api/notifications/{Uri.EscapeDataString(notificationId)}/ack";
                var request = new HttpRequestMessage(HttpMethod.Post,
                    $"{_settings.ApiBase}{path}");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Content = new StringContent(
                    JsonSerializer.Serialize(new { buttonLabel, buttonAction }),
                    Encoding.UTF8, "application/json");
                AuthService.AddSigningHeaders(request, token, "POST", path);

                var response = await _http.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    var err = await response.Content.ReadAsStringAsync();
                    System.Diagnostics.Debug.WriteLine($"[Ack] Ack failed {response.StatusCode}: {err}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Ack] Ack exception: {ex.Message}");
            }
        }

        await _history.RecordAction(notificationId, buttonAction);
        RaiseActionRecorded(notificationId, buttonAction, null);
    }

    public async Task Dismiss(string? notificationId, string buttonLabel)
    {
        await Acknowledge(notificationId, buttonLabel, "dismiss");
    }

    // FireWebhook posts to an external caller-supplied URL — not our API.
    // Uses an unpinned client and is intentionally NOT signed.
    public async Task FireWebhook(string webhookUrl, PushNotification payload, string buttonId)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            System.Diagnostics.Debug.WriteLine("[Ack] Skipping webhook — no URL");
            return;
        }

        try
        {
            var body = new
            {
                notificationId = payload.NotificationId,
                templateType = payload.TemplateType,
                title = payload.Title,
                content = payload.Content,
                channelName = payload.ChannelName,
                groupName = payload.GroupName,
                callerName = payload.CallerName,
                callerPhone = payload.CallerPhone,
                reason = payload.Reason,
                appointmentDate = payload.AppointmentDate,
                service = payload.Service,
                stylist = payload.Stylist,
                senderName = payload.SenderName,
                senderPhone = payload.SenderPhone,
                conversationId = payload.ConversationId,
                _meta = new
                {
                    buttonId,
                    firedAt = DateTimeOffset.UtcNow.ToString("O"),
                    appVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "1.0.0",
                },
            };

            var request = new HttpRequestMessage(HttpMethod.Post, webhookUrl);
            request.Content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            var response = await _webhookHttp.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                System.Diagnostics.Debug.WriteLine($"[Ack] Webhook failed {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Ack] Webhook exception: {ex.Message}");
        }

        if (!string.IsNullOrWhiteSpace(payload.NotificationId))
        {
            await _history.RecordAction(payload.NotificationId, "webhook");
            RaiseActionRecorded(payload.NotificationId!, "webhook", null);
        }
    }

    public async Task PostTransferAction(string url)
    {
        var token = _session?.Token;
        if (string.IsNullOrWhiteSpace(token)) return;
        try
        {
            var path = new Uri(url).AbsolutePath;
            var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            AuthService.AddSigningHeaders(request, token, "POST", path);
            var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                System.Diagnostics.Debug.WriteLine($"[Ack] PostTransferAction failed {response.StatusCode}: {err}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Ack] PostTransferAction exception: {ex.Message}");
        }
    }

    private void RaiseActionRecorded(string notificationId, string action, string? by)
    {
        try { OnActionRecorded?.Invoke(notificationId, action, by); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Ack] OnActionRecorded handler threw: {ex.Message}");
        }
    }
}
