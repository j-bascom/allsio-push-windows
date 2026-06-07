using System.Net.Http;
using System.Net.Http.Headers;
using PusherClient;

namespace AllsioPush.Services;

// Custom Pusher channel authorizer that adds HMAC signing headers to every
// /api/pusher/auth request. Implements both IAuthorizer (required by PusherOptions)
// and IAuthorizerAsync (preferred by PusherClient when available — avoids blocking).
public class SignedPusherAuthorizer : IAuthorizer, IAuthorizerAsync
{
    public TimeSpan? Timeout { get; set; } = TimeSpan.FromSeconds(15);

    private readonly string _authEndpoint;
    private readonly Func<string> _getToken;
    private readonly HttpClient _http;
    private readonly string _path;

    public SignedPusherAuthorizer(string authEndpoint, Func<string> getToken, HttpClient pinnedHttpClient)
    {
        _authEndpoint = authEndpoint;
        _getToken = getToken;
        _http = pinnedHttpClient;
        _path = new Uri(authEndpoint).AbsolutePath;
    }

    // Sync bridge — called only if PusherClient doesn't detect IAuthorizerAsync.
    public string Authorize(string channelName, string socketId)
        => AuthorizeAsync(channelName, socketId).GetAwaiter().GetResult();

    public async Task<string> AuthorizeAsync(string channelName, string socketId)
    {
        var token = _getToken();
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("No token available for Pusher auth");

        var request = new HttpRequestMessage(HttpMethod.Post, _authEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        AuthService.AddSigningHeaders(request, token, "POST", _path);
        request.Content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("socket_id", socketId),
            new KeyValuePair<string, string>("channel_name", channelName),
        });

        var response = await _http.SendAsync(request).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
    }
}
