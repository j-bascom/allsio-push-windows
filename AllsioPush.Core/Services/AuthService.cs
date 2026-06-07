using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
        _http = CreatePinnedHttpClient();
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
            AddSigningHeaders(request, token, "POST", "/api/extension/exchange");

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
                Token = data.TryGetProperty("token", out var rt) && !string.IsNullOrEmpty(rt.GetString())
                    ? rt.GetString()!
                    : token,
                UserId = GetStringFlexible(data, "userId"),
                TenantId = data.GetProperty("tenantId").GetString() ?? "",
                DisplayName = data.GetProperty("displayName").GetString() ?? "",
                Email = data.GetProperty("email").GetString() ?? "",
                PusherAppKey = data.GetProperty("pusherAppKey").GetString() ?? "",
                PusherCluster = data.GetProperty("pusherCluster").GetString() ?? "",
                SessionId = data.TryGetProperty("sessionId", out var sid) ? sid.GetString() ?? "" : "",
                PersonalChannel = data.TryGetProperty("personalChannel", out var pc) ? pc.GetString() ?? "" : "",
                PushGroups = ParsePushGroups(data),
                EncryptionKey = data.TryGetProperty("encryptionKey", out var ek) ? ek.GetString() : null,
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Auth] Exchange exception: {ex.Message}");
            return null;
        }
    }

    // The API returns some fields (e.g. userId) as JSON numbers; read them as
    // strings regardless of whether the value is encoded as a string or number.
    private static string GetStringFlexible(JsonElement data, string key)
    {
        if (!data.TryGetProperty(key, out var el)) return "";
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString() ?? "",
            JsonValueKind.Number => el.GetRawText(),
            JsonValueKind.Null => "",
            _ => el.GetRawText(),
        };
    }

    public async Task<(bool valid, string? newToken)> SendHeartbeat(string token)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post,
                $"{_settings.ApiBase}/api/extension/heartbeat");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(
                JsonSerializer.Serialize(new { deviceType = "windows_app" }),
                Encoding.UTF8, "application/json");
            AddSigningHeaders(request, token, "POST", "/api/extension/heartbeat");

            var response = await _http.SendAsync(request);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return (false, null);

            try
            {
                var json = await response.Content.ReadAsStringAsync();
                var data = JsonSerializer.Deserialize<JsonElement>(json);
                var newToken = data.TryGetProperty("newToken", out var nt) ? nt.GetString() : null;
                return (true, newToken);
            }
            catch
            {
                return (true, null);
            }
        }
        catch
        {
            return (true, null);
        }
    }

    public async Task Logout(string token)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post,
                $"{_settings.ApiBase}/api/extension/logout");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            AddSigningHeaders(request, token, "POST", "/api/extension/logout");
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

    // HMAC-SHA256 signing: message = "{timestamp}.{METHOD}.{path}"
    internal static void AddSigningHeaders(HttpRequestMessage request, string token, string method, string path)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var message = $"{timestamp}.{method.ToUpper()}.{path}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(token));
        var sig = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
        var sigHex = Convert.ToHexString(sig).ToLower();
        request.Headers.TryAddWithoutValidation("X-Allsio-Timestamp", timestamp);
        request.Headers.TryAddWithoutValidation("X-Allsio-Signature", sigHex);
    }

    // Pinned HttpClient — validates that the TLS chain contains ISRG Root X1.
    // DEBUG builds skip pinning so dev servers with different certs still work.
    internal static HttpClient CreatePinnedHttpClient()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = ValidateServerCertificate,
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    private static bool ValidateServerCertificate(
        HttpRequestMessage request,
        X509Certificate2? cert,
        X509Chain? chain,
        SslPolicyErrors errors)
    {
        DebugLog.Write("TLS", $"Validating cert for: {request.RequestUri?.Host}");
        DebugLog.Write("TLS", $"SslPolicyErrors: {errors}");

        if (errors != SslPolicyErrors.None)
        {
            DebugLog.Write("TLS", "Policy errors — rejecting");
            return false;
        }

        if (chain == null)
        {
            DebugLog.Write("TLS", "No chain — rejecting");
            return false;
        }

        DebugLog.Write("TLS", $"Chain has {chain.ChainElements.Count} elements:");
        foreach (var element in chain.ChainElements)
        {
            var thumbprint = element.Certificate
                .GetCertHashString(HashAlgorithmName.SHA256).ToUpper();
            DebugLog.Write("TLS", $"  {element.Certificate.Subject} → {thumbprint}");
        }

#if DEBUG
        DebugLog.Write("TLS", "DEBUG build — skipping pin check");
        return errors == SslPolicyErrors.None;
#else
        const string isrgThumbprint =
            "96BCEC06264976F37460779ACF28C5A7" +
            "CFE8A3C0AAE11A8FFCEE05C0BDDF08C6";

        foreach (var element in chain.ChainElements)
        {
            var thumbprint = element.Certificate
                .GetCertHashString(HashAlgorithmName.SHA256).ToUpper();
            if (thumbprint == isrgThumbprint)
            {
                DebugLog.Write("TLS", "ISRG Root X1 found — accepting");
                return true;
            }
        }

        DebugLog.Write("TLS", "ISRG Root X1 NOT found — rejecting");
        return false;
#endif
    }
}
