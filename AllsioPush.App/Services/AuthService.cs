using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AllsioPush.Config;
using AllsioPush.Models;

namespace AllsioPush.Services;

public class AuthService
{
    private readonly HttpClient _http;
    private readonly AppSettings _settings;

    public AuthService(AppSettings settings)
    {
        _settings = settings;
        _http = new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    public async Task<AuthSession?> ExchangeToken(string token)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post,
                $"{_settings.ApiBase}/api/extension/exchange");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(
                JsonSerializer.Serialize(new { deviceType = "windows_app" }),
                Encoding.UTF8, "application/json");

            var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                System.Diagnostics.Debug.WriteLine($"[Auth] Exchange failed {response.StatusCode}: {err}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var data = JsonSerializer.Deserialize<JsonElement>(json);

            return new AuthSession
            {
                Token = token,
                UserId = data.GetProperty("userId").GetString() ?? "",
                TenantId = data.GetProperty("tenantId").GetString() ?? "",
                DisplayName = data.GetProperty("displayName").GetString() ?? "",
                Email = data.GetProperty("email").GetString() ?? "",
                PusherAppKey = data.GetProperty("pusherAppKey").GetString() ?? "",
                PusherCluster = data.GetProperty("pusherCluster").GetString() ?? "",
                SessionId = data.TryGetProperty("sessionId", out var sid) ? sid.GetString() ?? "" : "",
                PersonalChannel = data.TryGetProperty("personalChannel", out var pc) ? pc.GetString() ?? "" : "",
                PushGroups = ParsePushGroups(data),
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Auth] Exchange exception: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> SendHeartbeat(string token)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post,
                $"{_settings.ApiBase}/api/extension/heartbeat");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(
                JsonSerializer.Serialize(new { deviceType = "windows_app" }),
                Encoding.UTF8, "application/json");

            var response = await _http.SendAsync(request);
            return response.StatusCode != System.Net.HttpStatusCode.Unauthorized;
        }
        catch
        {
            return true;
        }
    }

    public async Task Logout(string token)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post,
                $"{_settings.ApiBase}/api/extension/logout");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            await _http.SendAsync(request);
        }
        catch { }
    }

    private static List<PushGroup> ParsePushGroups(JsonElement data)
    {
        var groups = new List<PushGroup>();
        if (data.TryGetProperty("pushGroups", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                groups.Add(new PushGroup
                {
                    Name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    PusherChannel = item.TryGetProperty("pusherChannel", out var c) ? c.GetString() ?? "" : "",
                });
            }
        }
        return groups;
    }
}
