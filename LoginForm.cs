using System.Web;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AlexaWidget;

/// <summary>Recognizes Amazon's sign-in callback (…/ap/maplanding?…) and reads its result.</summary>
static class LoginCallback
{
    /// <summary>
    /// True when <paramref name="address"/> is exactly the callback page (scheme, host and path, not a prefix).
    /// Then either <paramref name="code"/> holds the authorization code or <paramref name="error"/> says why not.
    /// </summary>
    public static bool TryRead(string address, string callbackUrl, out string? code, out string? error)
    {
        code = error = null;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || !Uri.TryCreate(callbackUrl, UriKind.Absolute, out var expected))
            return false;
        if (uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals(expected.Host, StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.TrimEnd('/').Equals(expected.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            return false;

        var query = HttpUtility.ParseQueryString(uri.Query);
        code = query["openid.oa2.authorization_code"] is { Length: > 0 } c ? c : null;
        if (code is not null) return true;

        var reason = query["error_description"] ?? query["error"];
        error = query["openid.mode"] == "cancel" ? "O login foi cancelado."
            : reason is not null ? $"A Amazon recusou o login: {reason}"
            : "A Amazon não devolveu o código de login. Tente entrar de novo.";
        return true;
    }
}

/// <summary>Shows Amazon's own sign-in page and captures the authorization code from the maplanding redirect.</summary>
sealed class LoginForm : Form
{
    const string MobileUserAgent =
        "Mozilla/5.0 (Linux; Android 14; Pixel 7 Build/UQ1A.240205.004; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/120.0.6099.230 Mobile Safari/537.36";

    readonly LoginRequest request;
    readonly string userDataDir;
    readonly WebView2 web = new() { Dock = DockStyle.Fill };
    bool finished;

    public LoginForm(LoginRequest request, string userDataDir)
    {
        this.request = request;
        this.userDataDir = userDataDir;
        Text = "Entrar na Amazon";
        Icon = AppIcon.Instance;
        StartPosition = FormStartPosition.CenterScreen;
        Size = LogicalToDeviceUnits(new Size(500, 760));
        Theme.Follow(this, web, webBackground: false);
        web.DefaultBackgroundColor = Color.White;
        Controls.Add(web);
        Load += async (_, _) =>
        {
            // An event handler is async void: anything thrown here would close the whole app.
            try
            {
                await StartAsync();
            }
            catch (Exception ex)
            {
                Finish(DialogResult.Abort, "Não foi possível abrir a página de login: " + ex.Message);
            }
        };
    }

    public string? Code { get; private set; }

    /// <summary>Why the sign-in did not produce a code (null when it worked or the window was just closed).</summary>
    public string? Error { get; private set; }

    public Dictionary<string, string> Cookies { get; } = [];

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyWindowFrame(Handle);
    }

    async Task StartAsync()
    {
        var env = await CoreWebView2Environment.CreateAsync(null, userDataDir);
        await web.EnsureCoreWebView2Async(env);
        var core = web.CoreWebView2;
        core.CookieManager.DeleteAllCookies();
        core.Settings.UserAgent = MobileUserAgent;
        core.Settings.IsStatusBarEnabled = false;
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            core.Navigate(e.Uri);
        };
        // The callback can arrive as a top-level redirect or inside a frame.
        core.NavigationStarting += (_, e) => OnNavigation(e.Uri, () => e.Cancel = true);
        core.FrameNavigationStarting += (_, e) => OnNavigation(e.Uri, () => e.Cancel = true);
        // Last resort: an address set by script (history.pushState) changes the source without a navigation.
        core.SourceChanged += (_, _) => OnNavigation(core.Source, () => core.Stop());
        core.Navigate(request.Url);
    }

    void OnNavigation(string address, Action cancel)
    {
        if (finished || !LoginCallback.TryRead(address, request.CallbackUrl, out var code, out var error)) return;
        finished = true;
        cancel();
        Code = code;
        Error = error;

        // Leave the WebView2 event before awaiting, and never let a failure here escape.
        BeginInvoke(async () =>
        {
            try
            {
                if (code is not null && web.CoreWebView2 is { } core)
                {
                    foreach (var cookie in await core.CookieManager.GetCookiesAsync(request.CookieUrl))
                        Cookies[cookie.Name] = cookie.Value;
                    core.CookieManager.DeleteAllCookies();
                }
            }
            catch (Exception)
            {
                // The sign-in cookies only add context to the registration; it works without them.
            }
            Finish(code is null ? DialogResult.Abort : DialogResult.OK, error);
        });
    }

    void Finish(DialogResult result, string? error)
    {
        finished = true;
        Error ??= error;
        if (!IsDisposed) DialogResult = result;
    }
}
