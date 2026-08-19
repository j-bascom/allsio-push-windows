using AllsioPush.Config;
using AllsioPush.Models;
using AllsioPush.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using WinUIEx;

namespace AllsioPush.UI.Windows;

/// <summary>
/// Sign-in window. The ceremony runs in the user's DEFAULT BROWSER, not in the
/// embedded WebView2: WebView2 exposes the WebAuthn API, so the page offers
/// passkeys and Windows even surfaces the matching credential — but the ceremony
/// dies at the host/OS boundary, which is why passkey sign-in always failed with
/// an error carrying no code. A real browser has none of that problem.
///
/// The browser hands the token back through the already-registered
/// `allsio-push://` scheme: Windows launches a second instance,
/// SingleInstanceService pipes the argv over, and App.HandleIncomingToken does the
/// exchange and closes this window. Nothing to intercept here.
///
/// The embedded WebView2 stays as an explicit opt-in fallback for when there is no
/// browser association (Process.Start throws). Passwords work in it; passkeys do not.
/// </summary>
public class LoginWindow : WindowEx
{
    private readonly AppSettings _settings;
    private readonly AuthService _authService;

    private readonly Grid _root;
    private readonly StackPanel _browserPanel;
    private readonly StackPanel _statusPanel;
    private readonly TextBlock _statusText;
    private readonly StackPanel _errorPanel;
    private readonly TextBlock _errorText;
    private readonly Button _errorFallbackButton;

    // Fallback only — built on demand, so the normal path never touches the
    // WebView2 runtime and the window opens instantly.
    private WebView2? _webView;
    private bool _exchanging;

    public event Action<AuthSession>? OnLoginSuccess;

    public LoginWindow(AppSettings settings, AuthService authService)
    {
        _settings = settings;
        _authService = authService;

        Title = "Allsio Push — Sign In";
        // Shorter than the old 800: this window no longer hosts a login form,
        // just the "we opened your browser" panel.
        this.SetWindowSize(480, 560);
        this.CenterOnScreen();
        WindowIcon.Apply(this);
        SystemBackdrop = new MicaBackdrop();

        // ── Default panel: sign-in is happening in the browser ─────────
        var heading = new TextBlock
        {
            Text = "Continue in your browser",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };
        var blurb = new TextBlock
        {
            Text = "We opened your default browser to finish signing in.\n\n" +
                   "Passkeys and password managers only work in a real browser " +
                   "window, so sign-in happens there. Come back here when it is done.",
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Opacity = 0.8,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var waiting = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 10,
            Margin = new Thickness(0, 28, 0, 0),
            Children =
            {
                new ProgressRing { IsActive = true, Width = 20, Height = 20 },
                new TextBlock { Text = "Waiting for you to finish…", Opacity = 0.7 },
            },
        };
        var reopen = new Button
        {
            Content = "Open browser again",
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 28, 0, 0),
        };
        reopen.Click += (_, _) => LaunchBrowserSignIn();

        var fallback = MakeFallbackButton();

        _browserPanel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(32),
            Children = { heading, blurb, waiting, reopen, fallback },
        };

        // ── Spinner panel (token exchange, WebView2 boot) ──────────────
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
            Visibility = Visibility.Collapsed,
            Children = { new ProgressRing { IsActive = true, Width = 40, Height = 40 }, _statusText },
        };

        // ── Error panel ────────────────────────────────────────────────
        _errorText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed),
        };
        var retry = new Button
        {
            Content = "Try again",
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 0),
        };
        retry.Click += (_, _) => RetryBrowserSignIn();
        // The error panel gets its own copy rather than reparenting the browser
        // panel's — moving it meant the browser panel silently lost the affordance
        // for the rest of the window's life once any error had been shown.
        _errorFallbackButton = MakeFallbackButton();
        _errorFallbackButton.Visibility = Visibility.Collapsed;
        _errorPanel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24),
            Visibility = Visibility.Collapsed,
            Children = { _errorText, retry, _errorFallbackButton },
        };

        _root = new Grid { RequestedTheme = ElementTheme.Dark };
        _root.Children.Add(_browserPanel);
        _root.Children.Add(_statusPanel);
        _root.Children.Add(_errorPanel);
        Content = _root;

        Closed += (_, _) => DisposeWebView();

        _root.Loaded += (_, _) => LaunchBrowserSignIn();
    }

    private string BuildLoginUrl()
        => $"{_settings.AdminBase}/extension-login?redirect=allsio-push://auth";

    // ── Browser path (default) ─────────────────────────────────────────

    /// <summary>
    /// Hands the sign-in URL to the default browser. The token comes back through
    /// the `allsio-push://` protocol handler, so there is nothing to do here but wait.
    /// </summary>
    private void LaunchBrowserSignIn()
    {
        var url = BuildLoginUrl();
        try
        {
            DebugLog.Write("Login", $"Opening sign-in in the default browser: {url}");
            // UseShellExecute is required to hand a URL to the shell — without it
            // .NET tries to exec the string as a program and throws.
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            ShowBrowserPanel();
        }
        catch (Exception ex)
        {
            DebugLog.Write("Login", $"Could not open the default browser: {ex.Message}");
            ShowError(
                "Could not open your browser.\n\n" +
                "Sign in at this address, or use the in-app sign-in below " +
                "(passwords only — passkeys need a real browser).\n\n" +
                url,
                showFallback: true);
        }
    }

    private void RetryBrowserSignIn()
    {
        _errorPanel.Visibility = Visibility.Collapsed;
        LaunchBrowserSignIn();
    }

    private void ShowBrowserPanel()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_webView != null) _webView.Visibility = Visibility.Collapsed;
            _statusPanel.Visibility = Visibility.Collapsed;
            _errorPanel.Visibility = Visibility.Collapsed;
            _browserPanel.Visibility = Visibility.Visible;
        });
    }

    /// <summary>
    /// Surfaces a failure that happened on the protocol-handler path, where the
    /// token is exchanged by App rather than by this window. Without it the window
    /// would sit on "Waiting for you to finish…" forever after a failed exchange.
    /// </summary>
    public void ShowSignInError(string message) => ShowError(message, showFallback: true);

    // ── WebView2 fallback (opt-in) ─────────────────────────────────────

    private async Task StartWebViewFallbackAsync()
    {
        DebugLog.Write("Login", "Falling back to in-app WebView2 sign-in (passkeys unavailable there).");
        _browserPanel.Visibility = Visibility.Collapsed;
        _errorPanel.Visibility = Visibility.Collapsed;
        _statusText.Text = "Loading sign-in…";
        _statusPanel.Visibility = Visibility.Visible;

        try
        {
            if (_webView == null)
            {
                _webView = new WebView2 { Visibility = Visibility.Collapsed };
                // Behind the other panels so they keep painting over it.
                _root.Children.Insert(0, _webView);

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
            }

            _statusPanel.Visibility = Visibility.Collapsed;
            _webView.Visibility = Visibility.Visible;
            await NavigateToLoginAsync();
        }
        catch (Exception ex)
        {
            ShowError(
                "Could not start the in-app sign-in window.\n\n" +
                "The Microsoft Edge WebView2 runtime may not be installed.\n" +
                "Install it from https://go.microsoft.com/fwlink/p/?LinkId=2124703 and try again.\n\n" +
                $"Details: {ex.Message}");
        }
    }

    private async Task NavigateToLoginAsync()
    {
        if (_webView?.CoreWebView2 == null) return;
        await ClearWebSessionAsync();
        var url = BuildLoginUrl();
        DebugLog.Write("Login", $"Navigating in-app sign-in to: {url}");
        _webView.CoreWebView2.Navigate(url);
    }

    private async Task ClearWebSessionAsync()
    {
        if (_webView?.CoreWebView2 == null) return;
        try
        {
            // Wipe cookies/site data AND any saved passwords / autofill on every
            // sign-in. The password/autofill clear purges stale credentials a
            // prior build (2026.6.30, which had autosave on) may have saved —
            // those could otherwise ghost-fill the form and cause lockouts.
            // Only applies to the fallback: the browser path uses the user's own
            // browser profile and deliberately leaves it alone.
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
        var url = _webView?.CoreWebView2?.Source;
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
            if (_webView != null) _webView.Visibility = Visibility.Collapsed;
            _browserPanel.Visibility = Visibility.Collapsed;
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

    // ── Shared ─────────────────────────────────────────────────────────

    private void ShowError(string message, bool showFallback = false)
    {
        DebugLog.Write("Login", $"Showing error: {message.Replace('\n', ' ')}");
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_webView != null) _webView.Visibility = Visibility.Collapsed;
            _browserPanel.Visibility = Visibility.Collapsed;
            _statusPanel.Visibility = Visibility.Collapsed;
            _errorText.Text = message;
            _errorPanel.Visibility = Visibility.Visible;
            if (showFallback) _errorFallbackButton.Visibility = Visibility.Visible;
        });
    }

    private Button MakeFallbackButton()
    {
        var b = new Button
        {
            Content = "Having trouble? Sign in inside the app",
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Opacity = 0.7,
        };
        b.Click += async (_, _) => await StartWebViewFallbackAsync();
        return b;
    }

    private void DisposeWebView()
    {
        try { _webView?.Close(); } catch { }
        _webView = null;
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
