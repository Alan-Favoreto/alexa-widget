using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AlexaWidget;

sealed class NotLoggedInException() : Exception("Entre na sua conta Amazon para continuar.");

class AlexaException(string message) : Exception(message);

sealed class RateLimitedException() : AlexaException("A Amazon pediu para esperar um pouco antes de atualizar de novo.");

sealed class AuthData
{
    public string DeviceSerial { get; set; } = "";
    public string? DeviceType { get; set; }
    public string? RefreshToken { get; set; }
    public string? CustomerId { get; set; }
    public Dictionary<string, string> Cookies { get; set; } = [];
    public DateTimeOffset CookiesIssued { get; set; }
}

sealed record LoginRequest(string Url, string Verifier, string ReturnUrlPrefix, string CookieUrl);

/// <summary>
/// Signs in the same way the Alexa mobile app does: an OAuth/PKCE login on the Amazon sign-in page registers
/// this PC as an app instance, the resulting refresh token is exchanged for web cookies, and those cookies
/// (plus the csrf cookie) authorize the alexa.amazon.* REST endpoints the app's React Native bundle calls.
/// </summary>
sealed class AlexaSession
{
    // Identity of the analyzed Android app: MAPDeviceType in its AndroidManifest, version from the XAPK.
    // (Registered as the iOS type A2IVLV5VM2W81, /api/notifications answered "Rate exceeded" even after long idle periods.)
    const string DeviceType = "A2TF17PFR55MTB";
    const string AppVersion = "2.2.720364.0";
    const string AppVersionCode = "965636405";
    const string OsVersion = "14";
    const string DeviceModel = "Pixel 7";
    const string UserAgent = "AmazonWebView/Amazon Alexa/" + AppVersion + "/Android/" + OsVersion + "/" + DeviceModel;
    static readonly TimeSpan CookieLifetime = TimeSpan.FromDays(5);
    static readonly string[] CsrfPaths =
        ["/api/language", "/spa/index.html", "/api/devices-v2/device?cached=false", "/templates/oobe/d-device-pick.handlebars", "/api/strings"];

    readonly string authPath;
    readonly HttpClient http = new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false });
    readonly SemaphoreSlim refreshLock = new(1, 1);
    AuthData data;
    string? accessToken;
    DateTimeOffset accessTokenExpires;

    public AlexaSession(Region region, string dataDir)
    {
        Region = region;
        authPath = Path.Combine(dataDir, "auth.bin");
        data = Load();
        if (data.DeviceSerial.Length == 0)
        {
            data.DeviceSerial = NewSerial();
            Save();
        }
    }

    public Region Region { get; }
    // A token from an older registration (different device type) needs a new sign-in.
    public bool IsLoggedIn => data.RefreshToken is not null && data.DeviceType == DeviceType;
    public string? CustomerId => data.CustomerId;

    public LoginRequest CreateLoginRequest()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var returnTo = $"https://{Region.WwwHost}/ap/maplanding";
        var query = new Dictionary<string, string>
        {
            ["openid.return_to"] = returnTo,
            ["openid.assoc_handle"] = "amzn_dp_project_dee_android" + Region.PageHandle,
            ["openid.identity"] = "http://specs.openid.net/auth/2.0/identifier_select",
            ["pageId"] = "amzn_dp_project_dee_android" + Region.PageHandle,
            ["accountStatusPolicy"] = "P1",
            ["openid.claimed_id"] = "http://specs.openid.net/auth/2.0/identifier_select",
            ["openid.mode"] = "checkid_setup",
            ["openid.ns.oa2"] = "http://www.amazon.com/ap/ext/oauth/2",
            ["openid.oa2.client_id"] = "device:" + ClientId,
            ["openid.ns.pape"] = "http://specs.openid.net/extensions/pape/1.0",
            ["openid.oa2.response_type"] = "code",
            ["openid.ns"] = "http://specs.openid.net/auth/2.0",
            ["openid.pape.max_auth_age"] = "0",
            ["openid.oa2.scope"] = "device_auth_access",
            ["openid.oa2.code_challenge_method"] = "S256",
            ["openid.oa2.code_challenge"] = challenge,
            ["language"] = Region.Locale.Replace('-', '_'),
        };
        var url = $"https://{Region.WwwHost}/ap/signin?" +
                  string.Join("&", query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return new LoginRequest(url, verifier, returnTo, $"https://{Region.WwwHost}");
    }

    public async Task CompleteLoginAsync(string authorizationCode, string verifier, IReadOnlyDictionary<string, string> loginCookies)
    {
        var websiteCookies = new JsonArray(loginCookies
            .Select(c => (JsonNode)new JsonObject { ["Value"] = c.Value, ["Name"] = c.Key })
            .ToArray());
        var body = new JsonObject
        {
            ["requested_extensions"] = new JsonArray("device_info", "customer_info"),
            ["cookies"] = new JsonObject { ["website_cookies"] = websiteCookies, ["domain"] = Region.CookieDomain },
            ["registration_data"] = new JsonObject
            {
                ["domain"] = "Device",
                ["app_version"] = AppVersion,
                ["device_type"] = DeviceType,
                ["device_name"] = "%FIRST_NAME%%FIRST_NAME_POSSESSIVE_STRING%%DUPE_STRATEGY_1ST%Alexa Widget (PC)",
                ["os_version"] = OsVersion,
                ["device_serial"] = data.DeviceSerial,
                ["device_model"] = DeviceModel,
                ["app_name"] = "Amazon Alexa",
                ["software_version"] = AppVersionCode,
            },
            ["auth_data"] = new JsonObject
            {
                ["client_id"] = ClientId,
                ["authorization_code"] = authorizationCode,
                ["code_verifier"] = verifier,
                ["code_algorithm"] = "SHA-256",
                ["client_domain"] = "DeviceLegacy",
            },
            ["user_context_map"] = new JsonObject { ["frc"] = loginCookies.GetValueOrDefault("frc", "") },
            ["requested_token_type"] = new JsonArray("bearer", "mac_dms", "website_cookies"),
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://{Region.ApiHost}/auth/register")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        AddIdentityHeaders(req);
        using var resp = await http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        var success = TryParse(text)?["response"]?["success"]
            ?? throw new AlexaException($"A Amazon recusou o registro ({(int)resp.StatusCode}): {Shorten(text)}");

        data.RefreshToken = (string?)success["tokens"]?["bearer"]?["refresh_token"]
            ?? throw new AlexaException("A Amazon não devolveu um token de acesso.");
        data.DeviceType = DeviceType;
        data.CustomerId = (string?)success["extensions"]?["customer_info"]?["user_id"];
        data.Cookies.Clear();
        Save();
        await EnsureCookiesAsync(force: true);
    }

    public void Logout()
    {
        data = new AuthData { DeviceSerial = NewSerial() };
        Save();
    }

    /// <summary>Calls an alexa.amazon.* endpoint; re-mints the cookies once if they were rejected.</summary>
    public async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body = null)
    {
        await EnsureCookiesAsync(force: false);
        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, $"https://{Region.AlexaHost}{path}");
            AddAlexaHeaders(req);
            if (body is not null)
                req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

            using var resp = await http.SendAsync(req);
            CaptureCookies(resp);
            var text = await resp.Content.ReadAsStringAsync();
            var rejected = resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                           || (int)resp.StatusCode is >= 300 and < 400;
            if (rejected && attempt == 0)
            {
                await EnsureCookiesAsync(force: true);
                continue;
            }
            // Throttling comes back as 429, or as 400 with {"message":"Rate exceeded"}.
            if (resp.StatusCode == HttpStatusCode.TooManyRequests
                || (!resp.IsSuccessStatusCode && text.Contains("Rate exceeded", StringComparison.OrdinalIgnoreCase)))
                throw new RateLimitedException();
            if (!resp.IsSuccessStatusCode)
                throw new AlexaException($"A Alexa respondeu {(int)resp.StatusCode} em {path.Split('?')[0]}: {Shorten(text)}");
            return string.IsNullOrWhiteSpace(text) ? null : TryParse(text);
        }
    }

    /// <summary>The amazon.* web session cookies, so an embedded Amazon page (skills store) opens already signed in.</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetWebCookiesAsync()
    {
        await EnsureCookiesAsync(force: false);
        return new Dictionary<string, string>(data.Cookies);
    }

    /// <summary>Short-lived OAuth access token minted from the refresh token (used by the mobile API hosts).</summary>
    public async Task<string> GetAccessTokenAsync()
    {
        if (data.RefreshToken is null) throw new NotLoggedInException();
        if (accessToken is not null && DateTimeOffset.UtcNow < accessTokenExpires) return accessToken;

        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://{Region.ApiHost}/auth/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["app_name"] = "Amazon Alexa",
                ["app_version"] = AppVersion,
                ["di.sdk.version"] = "6.12.4",
                ["di.os.name"] = "Android",
                ["di.os.version"] = OsVersion,
                ["di.hw.version"] = DeviceModel,
                ["package_name"] = "com.amazon.dee.app",
                ["platform"] = "Android",
                ["requested_token_type"] = "access_token",
                ["source_token_type"] = "refresh_token",
                ["source_token"] = data.RefreshToken,
            }),
        };
        AddIdentityHeaders(req);
        using var resp = await http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        var json = TryParse(text);
        accessToken = (string?)json?["access_token"]
            ?? throw new AlexaException($"Falha ao obter token de acesso ({(int)resp.StatusCode}): {Shorten(text)}");
        var lifetime = (int?)json?["expires_in"] ?? 3600;
        accessTokenExpires = DateTimeOffset.UtcNow.AddSeconds(lifetime - 120);
        return accessToken;
    }

    /// <summary>Raw request for diagnostics: any host, with the cookie session and/or the access token.</summary>
    public async Task<(int Status, string Body)> ProbeAsync(string url, bool withCookies, bool withToken, bool bearer = false)
    {
        if (withCookies) await EnsureCookiesAsync(force: false);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (withCookies) AddAlexaHeaders(req);
        else
        {
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
        }
        if (withToken) req.Headers.TryAddWithoutValidation("x-amz-access-token", await GetAccessTokenAsync());
        if (bearer) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + await GetAccessTokenAsync());
        using var resp = await http.SendAsync(req);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }

    async Task EnsureCookiesAsync(bool force)
    {
        if (data.RefreshToken is null) throw new NotLoggedInException();
        if (!force && CookiesFresh) return;

        await refreshLock.WaitAsync();
        try
        {
            if (!force && CookiesFresh) return;
            await ExchangeCookiesAsync();
            await FetchCsrfAsync();
            Save();
        }
        finally
        {
            refreshLock.Release();
        }
    }

    bool CookiesFresh => data.Cookies.ContainsKey("csrf") && DateTimeOffset.UtcNow - data.CookiesIssued < CookieLifetime;

    async Task ExchangeCookiesAsync()
    {
        var cookieSeed = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{{\"cookies\":{{\"{Region.CookieDomain}\":[]}}}}"));
        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://{Region.WwwHost}/ap/exchangetoken/cookies")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["app_name"] = "Amazon Alexa",
                ["app_version"] = AppVersion,
                ["di.os.name"] = "Android",
                ["di.os.version"] = OsVersion,
                ["di.hw.version"] = DeviceModel,
                ["di.sdk.version"] = "6.12.4",
                ["requested_token_type"] = "auth_cookies",
                ["source_token_type"] = "refresh_token",
                ["source_token"] = data.RefreshToken!,
                ["domain"] = Region.CookieDomain,
                ["cookies"] = cookieSeed,
            }),
        };
        AddIdentityHeaders(req);
        using var resp = await http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        if (resp.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            // The refresh token was revoked (e.g. the device was removed in "Your devices"): start over.
            Logout();
            throw new NotLoggedInException();
        }

        var cookiesByDomain = TryParse(text)?["response"]?["tokens"]?["cookies"] as JsonObject
            ?? throw new AlexaException($"Falha ao renovar a sessão ({(int)resp.StatusCode}): {Shorten(text)}");
        data.Cookies.Clear();
        foreach (var (_, list) in cookiesByDomain)
        {
            foreach (var cookie in list?.AsArray() ?? [])
            {
                if ((string?)cookie?["Name"] is { } name)
                    data.Cookies[name] = (string?)cookie["Value"] ?? "";
            }
        }
        data.CookiesIssued = DateTimeOffset.UtcNow;
    }

    async Task FetchCsrfAsync()
    {
        foreach (var path in CsrfPaths)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://{Region.AlexaHost}{path}");
            AddAlexaHeaders(req);
            using var resp = await http.SendAsync(req);
            CaptureCookies(resp);
            if (data.Cookies.ContainsKey("csrf")) return;
        }
        throw new AlexaException("A Alexa não entregou o token CSRF. Tente sair e entrar novamente.");
    }

    void AddIdentityHeaders(HttpRequestMessage req)
    {
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Headers.TryAddWithoutValidation("Accept-Language", Region.Locale);
        req.Headers.TryAddWithoutValidation("x-amzn-identity-auth-domain", Region.ApiHost);
    }

    void AddAlexaHeaders(HttpRequestMessage req)
    {
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("Accept", "application/json; charset=utf-8");
        req.Headers.TryAddWithoutValidation("Accept-Language", Region.Locale);
        req.Headers.TryAddWithoutValidation("Referer", $"https://{Region.AlexaHost}/spa/index.html");
        req.Headers.TryAddWithoutValidation("Origin", $"https://{Region.AlexaHost}");
        req.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", data.Cookies.Select(c => $"{c.Key}={c.Value}")));
        if (data.Cookies.TryGetValue("csrf", out var csrf))
            req.Headers.TryAddWithoutValidation("csrf", csrf);
    }

    void CaptureCookies(HttpResponseMessage resp)
    {
        if (!resp.Headers.TryGetValues("Set-Cookie", out var values)) return;
        foreach (var header in values)
        {
            var pair = header.Split(';', 2)[0];
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var value = pair[(eq + 1)..].Trim();
            if (value.Length > 0 && value != "deleted")
                data.Cookies[pair[..eq].Trim()] = value;
        }
    }

    string ClientId => Convert.ToHexString(Encoding.UTF8.GetBytes($"{data.DeviceSerial}#{DeviceType}")).ToLowerInvariant();

    AuthData Load()
    {
        try
        {
            if (File.Exists(authPath))
            {
                var json = ProtectedData.Unprotect(File.ReadAllBytes(authPath), null, DataProtectionScope.CurrentUser);
                return JsonSerializer.Deserialize<AuthData>(json) ?? new AuthData();
            }
        }
        catch (Exception e) when (e is CryptographicException or JsonException) { }
        return new AuthData();
    }

    void Save()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(data);
        File.WriteAllBytes(authPath, ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser));
    }

    static string NewSerial() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static JsonNode? TryParse(string text)
    {
        try { return JsonNode.Parse(text); }
        catch (JsonException) { return null; }
    }

    static string Shorten(string text) => text.Length <= 240 ? text : text[..240] + "…";
}
