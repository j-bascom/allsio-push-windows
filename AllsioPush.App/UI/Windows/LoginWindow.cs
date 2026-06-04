using AllsioPush.Config;
using AllsioPush.Models;
using AllsioPush.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AllsioPush.UI.Windows;

public class LoginWindow : Form
{
    private readonly AppSettings _settings;
    private readonly AuthService _authService;
    private readonly WebView2 _webView;
    private readonly Label _loadingLabel;
    private readonly Label _errorLabel;
    private readonly Button _retryButton;
    private bool _exchanging = false;

    public event Action<AuthSession>? OnLoginSuccess;

    public LoginWindow(AppSettings settings, AuthService authService)
    {
        _settings = settings;
        _authService = authService;

        Text = "Allsio Push — Sign In";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        ClientSize = new Size(480, 640);

        _loadingLabel = new Label
        {
            Text = "Loading sign-in…",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(SystemFonts.DefaultFont.FontFamily, 11f),
            ForeColor = Color.Gray,
        };

        _errorLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 80,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(SystemFonts.DefaultFont.FontFamily, 10f),
            ForeColor = Color.Firebrick,
            Visible = false,
            Padding = new Padding(16),
        };

        _retryButton = new Button
        {
            Text = "Retry",
            Dock = DockStyle.Bottom,
            Height = 40,
            Visible = false,
        };
        _retryButton.Click += async (s, e) => await RetryAsync();

        _webView = new WebView2 { Dock = DockStyle.Fill, Visible = false };

        Controls.Add(_webView);
        Controls.Add(_loadingLabel);
        Controls.Add(_errorLabel);
        Controls.Add(_retryButton);

        Load += async (s, e) => await InitializeWebView();
    }

    private async Task InitializeWebView()
    {
        try
        {
            var userDataFolder = Path.Combine(SettingsManager.GetAppDataPath(), "WebView2");
            Directory.CreateDirectory(userDataFolder);
            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await _webView.EnsureCoreWebView2Async(env);

            _webView.CoreWebView2.NavigationStarting += OnNavigationStarting;

            _webView.CoreWebView2.NewWindowRequested += (s, e) =>
            {
                if (e.Uri.StartsWith("allsio-push://", StringComparison.OrdinalIgnoreCase))
                {
                    e.Handled = true;
                    HandleAllsioPushUri(e.Uri);
                }
            };

            _loadingLabel.Visible = false;
            _webView.Visible = true;

            Navigate();
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

    private void Navigate()
    {
        _webView.CoreWebView2.Navigate(BuildLoginUrl());
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Uri)) return;

        if (e.Uri.StartsWith("allsio-push://", StringComparison.OrdinalIgnoreCase))
        {
            System.Diagnostics.Debug.WriteLine("[LoginWindow] Intercepted allsio-push:// auth redirect");
            e.Cancel = true;
            HandleAllsioPushUri(e.Uri);
        }
    }

    private void HandleAllsioPushUri(string uri)
    {
        _ = HandleAuthRedirect(uri);
    }

    private async Task HandleAuthRedirect(string uri)
    {
        if (_exchanging) return;
        _exchanging = true;

        try
        {
            var parsed = new Uri(uri);
            var token = ParseQueryParam(parsed.Query, "token");

            if (string.IsNullOrWhiteSpace(token))
            {
                ShowError("Sign-in did not return a token. Please try again.");
                return;
            }

            BeginInvoke(() =>
            {
                _webView.Visible = false;
                _loadingLabel.Text = "Signing in…";
                _loadingLabel.Visible = true;
            });

            var session = await _authService.ExchangeToken(token);

            if (session == null)
            {
                ShowError("Sign-in failed. Please try again.");
                return;
            }

            BeginInvoke(() => OnLoginSuccess?.Invoke(session));
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
        void Apply()
        {
            _webView.Visible = false;
            _loadingLabel.Visible = false;
            _errorLabel.Text = message;
            _errorLabel.Visible = true;
            _retryButton.Visible = true;
        }

        if (InvokeRequired) BeginInvoke(Apply);
        else Apply();
    }

    private async Task RetryAsync()
    {
        _errorLabel.Visible = false;
        _retryButton.Visible = false;

        if (_webView.CoreWebView2 != null)
        {
            _webView.Visible = true;
            Navigate();
        }
        else
        {
            _loadingLabel.Visible = true;
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
            var k = Uri.UnescapeDataString(pair[..idx]);
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(pair[(idx + 1)..]);
        }
        return null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _webView?.Dispose();
        }
        base.Dispose(disposing);
    }
}
