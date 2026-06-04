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
    private readonly HttpClient _http;
    private readonly AppSettings _settings;
    private AuthSession? _session;

    public AckService(AppSettings settings)
    {
        _settings = settings;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
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
        if (string.IsNullOrWhiteSpace(token))
        {
            System.Diagnostics.Debug.WriteLine("[Ack] Skipping ack — no session token");
            return;
        }

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post,
                $"{_settings.ApiBase}/api/notifications/{Uri.EscapeDataString(notificationId)}/ack");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(
                JsonSerializer.Serialize(new { buttonLabel, buttonAction }),
                Encoding.UTF8, "application/json");

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

    public async Task Dismiss(string? notificationId, string buttonLabel)
    {
        await Acknowledge(notificationId, buttonLabel, "dismiss");
    }

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

            var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                System.Diagnostics.Debug.WriteLine($"[Ack] Webhook failed {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Ack] Webhook exception: {ex.Message}");
        }
    }
}
