using System.Web;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AlexaWidget;

/// <summary>Shows Amazon's own sign-in page and captures the authorization code from the maplanding redirect.</summary>
sealed class LoginForm : Form
{
    const string MobileUserAgent =
        "Mozilla/5.0 (Linux; Android 14; Pixel 7 Build/UQ1A.240205.004; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/120.0.6099.230 Mobile Safari/537.36";

    readonly LoginRequest request;
    readonly string userDataDir;
    readonly WebView2 web = new() { Dock = DockStyle.Fill };

    public LoginForm(LoginRequest request, string userDataDir)
    {
        this.request = request;
        this.userDataDir = userDataDir;
        Text = "Entrar na Amazon";
        Icon = AppIcon.Instance;
        StartPosition = FormStartPosition.CenterScreen;
        Size = LogicalToDeviceUnits(new Size(500, 760));
        BackColor = Theme.Background;
        web.DefaultBackgroundColor = Color.White;
        Controls.Add(web);
        Load += async (_, _) => await StartAsync();
    }

    public string? Code { get; private set; }
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
        core.NavigationStarting += async (_, e) =>
        {
            if (!e.Uri.StartsWith(request.ReturnUrlPrefix, StringComparison.OrdinalIgnoreCase)) return;
            e.Cancel = true;
            Code = HttpUtility.ParseQueryString(new Uri(e.Uri).Query)["openid.oa2.authorization_code"];
            foreach (var cookie in await core.CookieManager.GetCookiesAsync(request.CookieUrl))
                Cookies[cookie.Name] = cookie.Value;
            core.CookieManager.DeleteAllCookies();
            DialogResult = Code is null ? DialogResult.Cancel : DialogResult.OK;
        };
        core.Navigate(request.Url);
    }
}
