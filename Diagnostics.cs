using System.Text.Json;
using System.Text.Json.Nodes;

namespace AlexaWidget;

/// <summary>
/// `AlexaWidget.exe --dump-notifications out.json` writes the cached and fresh /api/notifications answers side by side,
/// keeping only the scheduling fields (no labels or device names).
/// </summary>
static class Diagnostics
{
    static readonly string[] Fields =
        ["type", "status", "alarmTime", "originalDate", "originalTime", "recurringPattern", "remainingTime", "createdDate", "lastUpdatedDate"];

    /// <summary>
    /// `--listen-test out.txt [stopAfterMs]`: runs the real listen flow on a WinForms UI thread (same as the app),
    /// optionally pressing "stop" after a delay, and logs each status change with timestamps.
    /// </summary>
    public static void ListenTest(string outPath, int? stopAfterMs)
    {
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlexaWidget");
        var log = new List<string>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void Log(string line) { lock (log) log.Add($"{clock.ElapsedMilliseconds,6} ms  {line}"); }

        using var form = new Form { ShowInTaskbar = false, Opacity = 0, Size = new Size(1, 1) };
        var voice = new VoiceInput(dataDir, "pt") { Trace = line => Log(line) };
        form.Shown += async (_, _) =>
        {
            if (stopAfterMs is { } ms)
            {
                var timer = new System.Windows.Forms.Timer { Interval = ms };
                timer.Tick += (_, _) => { timer.Stop(); Log("-> Stop()"); voice.Stop(); };
                timer.Start();
            }
            var watchdog = new System.Windows.Forms.Timer { Interval = 25000 };
            watchdog.Tick += (_, _) => { Log("WATCHDOG: listen did not finish in 25 s"); form.Close(); };
            watchdog.Start();
            try
            {
                var text = await voice.ListenAsync((s, p) => Log($"status {s} {p}"));
                Log($"result: \"{text}\"");
            }
            catch (Exception e)
            {
                Log("error: " + e);
            }
            form.Close();
        };
        Application.Run(form);
        File.WriteAllLines(outPath, log);
    }

    /// <summary>
    /// `--login-selftest out.txt`: checks the callback parser on fixed cases, then opens the real login window
    /// pointed straight at fake callback addresses (cancelled before leaving the PC) to check capture and closing.
    /// </summary>
    public static void LoginSelfTest(string outPath)
    {
        const string Callback = "https://www.amazon.com.br/ap/maplanding";
        var log = new List<string>();
        (string Name, string Url, bool IsCallback, string? Code)[] cases =
        [
            ("válido", Callback + "?openid.mode=id_res&openid.oa2.authorization_code=ANabc123", true, "ANabc123"),
            ("barra final", Callback + "/?openid.oa2.authorization_code=X1", true, "X1"),
            ("cancelado", Callback + "?openid.mode=cancel", true, null),
            ("erro", Callback + "?error=access_denied&error_description=Negado", true, null),
            ("sem código", Callback, true, null),
            ("prefixo falso", Callback + "-evil?openid.oa2.authorization_code=X", false, null),
            ("http", "http://www.amazon.com.br/ap/maplanding?openid.oa2.authorization_code=X", false, null),
            ("outro servidor", "https://www.amazon.com.br.evil.com/ap/maplanding?openid.oa2.authorization_code=X", false, null),
            ("página de login", "https://www.amazon.com.br/ap/signin?openid.return_to=" + Uri.EscapeDataString(Callback), false, null),
        ];
        foreach (var c in cases)
        {
            var hit = LoginCallback.TryRead(c.Url, Callback, out var code, out var error);
            var ok = hit == c.IsCallback && code == c.Code;
            log.Add($"{(ok ? "OK  " : "FALHA")} parser {c.Name}: callback={hit} code={code ?? "-"} erro={error ?? "-"}");
        }

        ApplicationConfiguration.Initialize();
        var dataDir = Path.Combine(Path.GetTempPath(), "alexawidget-login-selftest");
        foreach (var (name, url, expectCode) in new[]
                 {
                     ("janela/código", Callback + "?openid.oa2.authorization_code=TESTE123", "TESTE123"),
                     ("janela/cancelado", Callback + "?openid.mode=cancel", (string?)null),
                 })
        {
            using var form = new LoginForm(new LoginRequest(url, "verifier", Callback, "https://www.amazon.com.br"), dataDir);
            var watchdog = new System.Windows.Forms.Timer { Interval = 20000 };
            watchdog.Tick += (_, _) => { watchdog.Stop(); form.Close(); };
            watchdog.Start();
            var result = form.ShowDialog();
            watchdog.Stop();
            var ok = form.Code == expectCode && (expectCode is null ? form.Error is not null : result == DialogResult.OK);
            log.Add($"{(ok ? "OK  " : "FALHA")} {name}: resultado={result} code={form.Code ?? "-"} erro={form.Error ?? "-"}");
        }
        File.WriteAllLines(outPath, log);
    }

    /// <summary>`--dump-features out.txt`: each smart-home endpoint's category, feature names and operations (read-only).</summary>
    public static async Task DumpFeaturesAsync(string outPath)
    {
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlexaWidget");
        var session = new AlexaSession(Region.Load(dataDir), dataDir);
        const string Query = """
            query { listEndpoints(listEndpointsInput: {}) { endpoints {
              friendlyNameObject { value { text } }
              displayCategories { primary { value } }
              features { name operations { name } properties { __typename } }
            } } }
            """;
        var lines = new List<string>();
        try
        {
            var answer = await session.SendAsync(HttpMethod.Post, "/nexus/v1/graphql", new JsonObject { ["query"] = Query });
            foreach (var ep in answer?["data"]?["listEndpoints"]?["endpoints"]?.AsArray() ?? [])
            {
                var features = (ep?["features"]?.AsArray() ?? []).Select(f =>
                    $"{(string?)f?["name"]}[{string.Join(",", (f?["operations"]?.AsArray() ?? []).Select(o => (string?)o?["name"]))}]");
                lines.Add($"{(string?)ep?["displayCategories"]?["primary"]?["value"]} | {(string?)ep?["friendlyNameObject"]?["value"]?["text"]} | {string.Join(" ", features)}");
            }
            if (answer?["errors"] is JsonArray errors) lines.Add("errors: " + errors.ToJsonString()[..Math.Min(600, errors.ToJsonString().Length)]);
        }
        catch (Exception e)
        {
            lines.Add("erro: " + e.Message);
        }
        await File.WriteAllLinesAsync(outPath, lines);
    }

    /// <summary>`--probe-send out.txt METHOD /path json [METHOD /path json …]`: raw write requests, logged (status + body).</summary>
    public static async Task ProbeSendAsync(string outPath, string[] triples)
    {
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlexaWidget");
        var session = new AlexaSession(Region.Load(dataDir), dataDir);
        var lines = new List<string>();
        for (var i = 0; i + 2 < triples.Length; i += 3)
        {
            var (method, path, json) = (triples[i], triples[i + 1], triples[i + 2]);
            if (json.StartsWith('@')) json = await File.ReadAllTextAsync(json[1..]); // body from a file
            try
            {
                var answer = await session.SendAsync(new HttpMethod(method), path, JsonNode.Parse(json));
                var shown = answer?.ToJsonString() ?? "(vazio)";
                lines.Add($"OK {method} {path} {json}\n   {shown[..Math.Min(20000, shown.Length)]}");
            }
            catch (Exception e)
            {
                lines.Add($"ERR {method} {path} {json}\n   {e.Message}");
            }
        }
        await File.WriteAllLinesAsync(outPath, lines);
    }

    /// <summary>`--probe-get out.txt /path1 /path2 …`: status, size and the start of each GET answer (read-only).</summary>
    public static async Task ProbeGetAsync(string outPath, IEnumerable<string> paths)
    {
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlexaWidget");
        var region = Region.Load(dataDir);
        var session = new AlexaSession(region, dataDir);
        var lines = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                // "https://host/path" probes another host with cookies + bearer token; "/path" uses the Alexa web host.
                var (status, body) = path.StartsWith("https://")
                    ? await session.ProbeAsync(path, withCookies: true, withToken: false, bearer: true)
                    : await session.ProbeAsync($"https://{region.AlexaHost}{path}", withCookies: true, withToken: false);
                lines.Add($"{status} {path} ({body.Length} chars)\n{(body.Length > 30000 ? body[..30000] + "…" : body)}\n");
            }
            catch (Exception e)
            {
                lines.Add($"ERR {path}: {e.Message}\n");
            }
        }
        await File.WriteAllLinesAsync(outPath, lines);
    }

    /// <summary>
    /// `--notification-selftest out.txt`: creates a reminder two days ahead on the first online Echo, finds it in a
    /// fresh /api/notifications list and deletes it again, logging each server answer.
    /// </summary>
    public static async Task NotificationSelfTestAsync(string outPath)
    {
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlexaWidget");
        var api = new AlexaApi(new AlexaSession(Region.Load(dataDir), dataDir));
        var log = new List<string>();
        const string Label = "teste do alexa widget";

        var echo = (await api.GetDevicesAsync())?["devices"]?.AsArray()
            .FirstOrDefault(d => (string?)d?["deviceFamily"] is "ECHO" or "KNIGHT" or "ROOK" && (bool?)d?["online"] == true);
        if (echo is null)
        {
            await File.WriteAllTextAsync(outPath, "nenhum Echo online");
            return;
        }
        var when = DateTime.Today.AddDays(2).AddHours(3).AddMinutes(33);
        JsonObject? created = null;
        if (!File.Exists(outPath + ".skip-create"))
        {
            try
            {
                created = await api.CreateNotificationAsync("Reminder", (string)echo["serialNumber"]!, (string)echo["deviceType"]!, when, Label, null) as JsonObject;
                log.Add($"criar: OK status {(string?)created?["status"]} data {(string?)created?["originalDate"]} hora {(string?)created?["originalTime"]}");
            }
            catch (Exception e)
            {
                log.Add("criar: " + e.Message);
            }
        }

        await Task.Delay(4000);
        var all = ((await api.GetNotificationsAsync(fresh: true))?["notifications"]?.AsArray() ?? []).OfType<JsonObject>().ToList();
        var mine = all.Where(n => ((string?)n["reminderLabel"])?.Contains("alexa widget", StringComparison.OrdinalIgnoreCase) == true).ToList();
        log.Add($"lista: {all.Count} itens, de teste: {mine.Count}");
        if (mine.Count == 0 && created is not null) mine.Add(created);
        foreach (var n in mine)
        {
            log.Add($"lido: status {(string?)n["status"]} data {(string?)n["originalDate"]} hora {(string?)n["originalTime"]}");
            try
            {
                await api.DeleteNotificationAsync((JsonObject)n.DeepClone());
                log.Add("excluir: OK");
            }
            catch (Exception e)
            {
                log.Add("excluir: " + e.Message);
            }
        }
        var left = ((await api.GetNotificationsAsync(fresh: true))?["notifications"]?.AsArray() ?? [])
            .Count(n => (string?)n?["reminderLabel"] == Label && (string?)n?["status"] != "OFF");
        log.Add($"sobrou ativo: {left}");
        await File.WriteAllLinesAsync(outPath, log);
    }

    /// <summary>`--dump-routines out.json`: raw /api/behaviors/automations answer (read-only).</summary>
    public static async Task DumpRoutinesAsync(string outPath)
    {
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlexaWidget");
        var session = new AlexaSession(Region.Load(dataDir), dataDir);
        var report = new JsonObject();
        foreach (var path in new[] { "/api/behaviors/v2/automations?limit=200", "/api/behaviors/v2/automation?limit=200", "/api/behaviors/automations?limit=200" })
        {
            try
            {
                report[path] = (await session.SendAsync(HttpMethod.Get, path))?.DeepClone();
                break;
            }
            catch (Exception e)
            {
                report[path] = "erro: " + e.Message;
            }
        }
        await File.WriteAllTextAsync(outPath, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>`--probe-notifications out.json`: tries the web and mobile API hosts with each kind of credential.</summary>
    public static async Task ProbeNotificationsAsync(string outPath)
    {
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlexaWidget");
        var region = Region.Load(dataDir);
        var session = new AlexaSession(region, dataDir);
        var report = new JsonObject { ["at"] = DateTimeOffset.Now.ToString("o") };
        (string Name, string Host, string Path, bool Cookies, bool Token, bool Bearer)[] variants =
        [
            ("devices web+cookies", region.AlexaHost, "/api/devices-v2/device?cached=true", true, false, false),
            ("notifications web+cookies", region.AlexaHost, "/api/notifications?cached=false", true, false, false),
            ("notifications mobile+bearer", "na-api-alexa.amazon.com", "/api/notifications?cached=false", false, false, true),
            ("notifications web+bearer", region.AlexaHost, "/api/notifications?cached=false", false, false, true),
        ];
        foreach (var v in variants)
        {
            try
            {
                var (status, body) = await session.ProbeAsync($"https://{v.Host}{v.Path}", v.Cookies, v.Token, v.Bearer);
                if (v.Path.StartsWith("/api/devices"))
                {
                    report[v.Name] = new JsonObject { ["status"] = status };
                    await Task.Delay(1500);
                    continue;
                }
                var entry = new JsonObject { ["status"] = status };
                if (status == 200 && JsonNode.Parse(body)?["notifications"] is JsonArray list)
                    entry["items"] = Summarize(list);
                else
                    entry["body"] = body.Length > 200 ? body[..200] : body;
                report[v.Name] = entry;
            }
            catch (Exception e)
            {
                report[v.Name] = "erro: " + e.Message;
            }
            await Task.Delay(1500);
        }
        await File.WriteAllTextAsync(outPath, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    static JsonArray Summarize(JsonArray list)
    {
        var items = new JsonArray();
        foreach (var n in list)
        {
            var item = new JsonObject();
            foreach (var f in Fields)
                item[f] = n?[f]?.DeepClone();
            items.Add(item);
        }
        return items;
    }

    public static async Task DumpNotificationsAsync(string outPath)
    {
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlexaWidget");
        var api = new AlexaApi(new AlexaSession(Region.Load(dataDir), dataDir));
        var report = new JsonObject { ["at"] = DateTimeOffset.Now.ToString("o") };
        foreach (var fresh in new[] { false, true })
        {
            try
            {
                var response = await api.GetNotificationsAsync(fresh);
                report[fresh ? "fresh" : "cached"] = Summarize(response?["notifications"]?.AsArray() ?? []);
            }
            catch (Exception e)
            {
                report[fresh ? "fresh" : "cached"] = "erro: " + e.Message;
            }
        }
        await File.WriteAllTextAsync(outPath, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
