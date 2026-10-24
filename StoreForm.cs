using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AlexaWidget;

/// <summary>
/// Amazon's own Alexa skills store in a window, signed in with the widget's session cookies.
/// Enabling a skill there is also how music services (Spotify, Deezer, Apple Music) get linked.
/// </summary>
sealed class StoreForm : Form
{
    readonly WebView2 web = new() { Dock = DockStyle.Fill };
    readonly string userDataDir;
    readonly string cookieDomain;
    readonly IReadOnlyDictionary<string, string> cookies;
    string url;

    public StoreForm(string userDataDir, string cookieDomain, IReadOnlyDictionary<string, string> cookies, string url)
    {
        this.userDataDir = userDataDir;
        this.cookieDomain = cookieDomain;
        this.cookies = cookies;
        this.url = url;
        Text = "Skills da Alexa";
        Icon = AppIcon.Instance;
        StartPosition = FormStartPosition.CenterScreen;
        Size = LogicalToDeviceUnits(new Size(1100, 800));
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
                MessageBox.Show(this, "Não foi possível abrir a loja de skills: " + ex.Message, "Alexa Widget",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        };
    }

    public void Navigate(string address)
    {
        url = address;
        if (web.CoreWebView2 is { } core) core.Navigate(address);
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

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
        foreach (var (name, value) in cookies)
        {
            try
            {
                var cookie = core.CookieManager.CreateCookie(name, value, cookieDomain, "/");
                cookie.IsSecure = true;
                core.CookieManager.AddOrUpdateCookie(cookie);
            }
            catch (ArgumentException)
            {
                // a value WebView2 rejects; the page will just ask to sign in for that part
            }
        }
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            core.Navigate(e.Uri);
        };
        core.DocumentTitleChanged += (_, _) => Text = $"Skills da Alexa — {core.DocumentTitle}";
        core.Navigate(url);
    }
}
