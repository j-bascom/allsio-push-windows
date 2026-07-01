using AllsioPush.Config;
using AllsioPush.Models;
using AllsioPush.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using WinUIEx;

namespace AllsioPush.UI.Windows;

public class LoginWindow : WindowEx
{
    private readonly AppSettings _settings;
    private readonly AuthService _authService;
    private readonly WebView2 _webView;
    private readonly StackPanel _statusPanel;
    private readonly TextBlock _statusText;
    private readonly StackPanel _errorPanel;
    private readonly TextBlock _errorText;
    private bool _exchanging;

    public event Action<AuthSession>? OnLoginSuccess;

    public LoginWindow(AppSettings settings, AuthService authService)
    {
        _settings = settings;
        _authService = authService;

        Title = "Allsio Push — Sign In";
        this.SetWindowSize(480, 800);
        this.CenterOnScreen();
        WindowIcon.Apply(this);
        SystemBackdrop = new MicaBackdrop();

        _webView = new WebView2 { Visibility = Visibility.Collapsed };

        _statusText = new TextBlock
        {
            Text = "Loading sign-in…",
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0),
        };
        _statusPanel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { new ProgressRing { IsActive = true, Width = 40, Height = 40 }, _statusText },
        };

        _errorText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed),
        };
        var retry = new Button
        {
            Content = "Retry",
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 0),
        };
        retry.Click += async (_, _) => await RetryAsync();
        _errorPanel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24),
            Visibility = Visibility.Collapsed,
            Children = { _errorText, retry },
        };

        var root = new Grid { RequestedTheme = ElementTheme.Dark };
        root.Children.Add(_webView);
        root.Children.Add(_statusPanel);
        root.Children.Add(_errorPanel);
        Content = root;

        root.Loaded += async (_, _) => await InitializeWebView();
    }

    private async Task InitializeWebView()
    {
        try
        {
            var userDataFolder = Path.Combine(SettingsManager.GetAppDataPath(), "WebView2");
            Directory.CreateDirectory(userDataFolder);
            var env = await CoreWebView2Environment.CreateWithOptionsAsync(
                null, userDataFolder, new CoreWebView2EnvironmentOptions());
            await _webView.EnsureCoreWebView2Async(env);

            _webView.CoreWebView2.NavigationStarting += OnNavigationStarting;
            _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            _webView.CoreWebView2.NewWindowRequested += (s, e) =>
            {
                if (e.Uri.StartsWith("allsio-push://", StringComparison.OrdinalIgnoreCase))
                {
                    e.Handled = true;
                    _ = HandleAuthRedirect(e.Uri);
                }
            };

            _statusPanel.Visibility = Visibility.Collapsed;
            _webView.Visibility = Visibility.Visible;
            await NavigateToLoginAsync();
        }
        catch (Exception ex)
        {
            ShowError(
                "Could not start the sign-in window.\n\n" +
                "The Microsoft Edge WebView2 runtime may not be installed.\n" +
                "Install it from https://go.microsoft.com/fwlink/p/?LinkId=2124703 and try again.\n\n" +
                $"Details: {ex.Message}");
        }
    }

    private string BuildLoginUrl()
        => $"{_settings.AdminBase}/extension-login?redirect=allsio-push://auth";

    private async Task NavigateToLoginAsync()
    {
        await ClearWebSessionAsync();
        var url = BuildLoginUrl();
        DebugLog.Write("Login", $"Navigating to sign-in page: {url}");
        _webView.CoreWebView2.Navigate(url);
    }

    private async Task ClearWebSessionAsync()
    {
        if (_webView.CoreWebView2 == null) return;
        try
        {
            // Wipe cookies/site data AND any saved passwords / autofill on every
            // sign-in. The password/autofill clear purges stale credentials a
            // prior build (2026.6.30, which had autosave on) may have saved —
            // those could otherwise ghost-fill the form and cause lockouts.
            await _webView.CoreWebView2.Profile.ClearBrowsingDataAsync(
                CoreWebView2BrowsingDataKinds.Cookies
                | CoreWebView2BrowsingDataKinds.AllSite
                | CoreWebView2BrowsingDataKinds.PasswordAutosave
                | CoreWebView2BrowsingDataKinds.GeneralAutofill);
        }
        catch
        {
            _webView.CoreWebView2.CookieManager.DeleteAllCookies();
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Uri)) return;
        if (e.Uri.StartsWith("allsio-push://", StringComparison.OrdinalIgnoreCase))
        {
            DebugLog.Write("Login", "Auth redirect intercepted (navigation)");
            e.Cancel = true;
            _ = HandleAuthRedirect(e.Uri);
            return;
        }
        DebugLog.Write("Login", $"Navigation starting: {e.Uri}");
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        // Logs the sign-in page's real HTTP status — a 403 served by the
        // extension-login page still "completes" (IsSuccess=true), so the
        // status code is the only signal that the page itself was forbidden.
        var url = _webView.CoreWebView2?.Source;
        DebugLog.Write("Login",
            $"Navigation completed: url={url} http={e.HttpStatusCode} success={e.IsSuccess} webError={e.WebErrorStatus}");
    }

    private async Task HandleAuthRedirect(string uri)
    {
        if (_exchanging) return;
        _exchanging = true;
        try
        {
            var token = ParseQueryParam(new Uri(uri).Query, "token");
            if (string.IsNullOrWhiteSpace(token))
            {
                DebugLog.Write("Login", "Auth redirect contained no token");
                ShowError("Sign-in did not return a token. Please try again.");
                return;
            }

            DebugLog.Write("Login", $"Token received (len={token.Length}) — exchanging…");
            _webView.Visibility = Visibility.Collapsed;
            _statusText.Text = "Signing in…";
            _statusPanel.Visibility = Visibility.Visible;

            var session = await _authService.ExchangeToken(token);
            if (session == null)
            {
                DebugLog.Write("Login", "Token exchange returned no session — see [Auth] log above for status/body");
                ShowError("Sign-in failed. Please try again.");
                return;
            }
            DebugLog.Write("Login", $"Sign-in succeeded — user={session.DisplayName}");
            OnLoginSuccess?.Invoke(session);
        }
        catch (Exception ex)
        {
            ShowError($"Sign-in error: {ex.Message}");
        }
        finally
        {
            _exchanging = false;
        }
    }

    private void ShowError(string message)
    {
        DebugLog.Write("Login", $"Showing error: {message.Replace('\n', ' ')}");
        DispatcherQueue.TryEnqueue(() =>
        {
            _webView.Visibility = Visibility.Collapsed;
            _statusPanel.Visibility = Visibility.Collapsed;
            _errorText.Text = message;
            _errorPanel.Visibility = Visibility.Visible;
        });
    }

    private async Task RetryAsync()
    {
        _errorPanel.Visibility = Visibility.Collapsed;
        if (_webView.CoreWebView2 != null)
        {
            _webView.Visibility = Visibility.Visible;
            await NavigateToLoginAsync();
        }
        else
        {
            _statusPanel.Visibility = Visibility.Visible;
            await InitializeWebView();
        }
    }

    private static string? ParseQueryParam(string query, string key)
    {
        if (string.IsNullOrEmpty(query)) return null;
        var q = query.StartsWith('?') ? query[1..] : query;
        foreach (var pair in q.Split('&'))
        {
            var idx = pair.IndexOf('=');
            if (idx < 0) continue;
            if (string.Equals(Uri.UnescapeDataString(pair[..idx]), key, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(pair[(idx + 1)..]);
        }
        return null;
    }
}
