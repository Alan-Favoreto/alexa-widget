using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AlexaWidget;

/// <summary>
/// One window, two modes: a frameless always-on-top widget docked to the corner of the screen, and a regular
/// resizable app window with a taskbar button. The UI is wwwroot/, talking to this class over postMessage.
/// </summary>
sealed class MainForm : Form
{
    const string UiHost = "widget.alexa.local";
    const int WsExToolWindow = 0x80;
    const int WsMinimizeBox = 0x20000;
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    readonly string dataDir;
    readonly AlexaSession session;
    readonly AlexaApi api;
    readonly RoutinesApi routines;
    readonly VoiceInput voice;
    readonly Prefs prefs;
    readonly WebView2 web = new() { Dock = DockStyle.Fill };
    readonly NotifyIcon tray;
    MouseButtonHook? mouseHook;
    StoreForm? store;
    bool appMode;
    bool inTaskbar; // the widget was minimized and temporarily has a taskbar button
    int commandHotkeys; // how many quick-command hotkey ids are currently registered
    bool exiting;
    Rectangle? appBounds;
    readonly System.Windows.Forms.Timer clipboardWatch = new() { Interval = 1000 };
    string? lastClipboard;

    public MainForm(string dataDir, Region region, bool startInAppMode)
    {
        this.dataDir = dataDir;
        session = new AlexaSession(region, dataDir);
        api = new AlexaApi(session);
        routines = new RoutinesApi(session);
        // voice.log (rewritten each launch) records the listen timeline for troubleshooting.
        var voiceLog = Path.Combine(dataDir, "voice.log");
        var clock = Stopwatch.StartNew();
        File.WriteAllText(voiceLog, $"{DateTime.Now:O}\n");
        voice = new VoiceInput(dataDir, region.Locale.Split('-')[0])
        {
            Trace = line =>
            {
                lock (voiceLog) File.AppendAllText(voiceLog, $"{clock.ElapsedMilliseconds,7} ms  {line}\n");
            },
        };
        prefs = Prefs.Load(dataDir);

        Text = "Alexa";
        StartPosition = FormStartPosition.Manual;
        Icon = AppIcon.Instance;
        BackColor = Theme.Background;
        web.DefaultBackgroundColor = Theme.Background;
        Controls.Add(web);

        tray = new NotifyIcon { Icon = AppIcon.Instance, Text = "Alexa Widget", Visible = true, ContextMenuStrip = BuildTrayMenu() };
        tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowWindow();
        };

        ApplyMode(app: false, initial: true);
        Load += async (_, _) =>
        {
            try
            {
                await InitWebAsync();
            }
            catch (Exception ex)
            {
                // Usually the WebView2 runtime is missing; say so instead of showing an empty window.
                MessageBox.Show(this, "Não foi possível iniciar a interface (Microsoft Edge WebView2).\n\n" + ex.Message +
                    "\n\nInstale o WebView2 Runtime: https://developer.microsoft.com/microsoft-edge/webview2/",
                    "Alexa Widget", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
        clipboardWatch.Tick += async (_, _) => await CheckClipboardForLoginAsync();
        if (session.HasPendingBrowserLogin)
        {
            lastClipboard = ReadClipboard();
            clipboardWatch.Start(); // a browser sign-in started before a restart can still finish
        }
        Load += (_, _) => Task.Run(voice.Preload);
        if (startInAppMode) Shown += (_, _) => ApplyMode(app: true);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            // A tool window has no taskbar button; toggled here instead of ShowInTaskbar, which would
            // recreate the window handle underneath WebView2.
            var cp = base.CreateParams;
            if (!appMode && !inTaskbar) cp.ExStyle |= WsExToolWindow;
            if (!appMode) cp.Style |= WsMinimizeBox; // lets the taskbar button restore/minimize the frameless widget
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyWindowFrame(Handle);
        if (prefs.HotkeyLabel is not null && !GlobalHotkey.Register(Handle, prefs.HotkeyModifiers, prefs.HotkeyKey))
            prefs.HotkeyLabel = null; // taken by another program; the settings screen shows it as unset
        try
        {
            ApplyMouseButton();
        }
        catch (AlexaException)
        {
            prefs.MouseButton = null;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == SingleInstance.ShowMessage)
        {
            var wantApp = m.WParam == 1 || appMode;
            BeginInvoke(() => ApplyMode(app: wantApp));
        }
        else if (m.Msg == GlobalHotkey.WmHotkey && (int)m.WParam == GlobalHotkey.Id) TriggerVoice();
        else if (m.Msg == GlobalHotkey.WmHotkey && (int)m.WParam >= GlobalHotkey.CommandIdBase)
            Post(new { @event = "command-hotkey", index = (int)m.WParam - GlobalHotkey.CommandIdBase });
        base.WndProc(ref m);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!inTaskbar || WindowState == FormWindowState.Minimized) return;
        // Restored from the taskbar: drop the temporary taskbar button again.
        inTaskbar = false;
        BeginInvoke(() =>
        {
            UpdateStyles();
            Hide();
            Show();
        });
    }

    void MinimizeToTaskbar()
    {
        if (!appMode && !inTaskbar)
        {
            inTaskbar = true;
            UpdateStyles();
            Hide(); // re-show so the shell picks up the new taskbar button
            Show();
        }
        WindowState = FormWindowState.Minimized;
    }

    /// <summary>Hotkey or mouse button: bring the widget up (without stealing focus from a hidden state) and listen.</summary>
    void TriggerVoice()
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Post(new { @event = "voice-trigger" });
    }

    void ApplyMouseButton()
    {
        mouseHook?.Dispose();
        mouseHook = null;
        if (prefs.MouseButton is { } button)
            mouseHook = new MouseButtonHook(button, () => BeginInvoke(TriggerVoice));
    }

    object PrefsView() => new { hotkeyLabel = prefs.HotkeyLabel, mouseButton = prefs.MouseButton };

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        tray.Visible = false;
        GlobalHotkey.Unregister(Handle);
        for (var i = 0; i < commandHotkeys; i++) GlobalHotkey.Unregister(Handle, GlobalHotkey.CommandIdBase + i);
        mouseHook?.Dispose();
        base.OnFormClosing(e);
    }

    async Task InitWebAsync()
    {
        var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(dataDir, "webview"));
        await web.EnsureCoreWebView2Async(env);
        var core = web.CoreWebView2;
        core.Settings.IsNonClientRegionSupportEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
#if !DEBUG
        core.Settings.AreDevToolsEnabled = false;
#endif
        core.SetVirtualHostNameToFolderMapping(UiHost, Path.Combine(AppContext.BaseDirectory, "wwwroot"),
            CoreWebView2HostResourceAccessKind.DenyCors);
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.WebMessageReceived += OnWebMessage;
        core.Navigate($"https://{UiHost}/index.html");
    }

    async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!e.Source.StartsWith($"https://{UiHost}/", StringComparison.Ordinal)) return;

        JsonElement message;
        try
        {
            message = JsonDocument.Parse(e.WebMessageAsJson).RootElement.Clone();
        }
        catch (JsonException)
        {
            return;
        }
        var id = message.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : 0;
        var method = message.TryGetProperty("method", out var methodProp) ? methodProp.GetString() ?? "" : "";
        var args = message.TryGetProperty("params", out var paramsProp) ? paramsProp : default;
        if (method is "listen" or "stopListening") voice.Trace?.Invoke($"ui: {method} received");

        // Leave the WebView2 callback before doing anything: a modal dialog (the login window) opened
        // inside it runs a nested message loop that hangs WebView2.
        await Task.Yield();
        try
        {
            Post(new { id, ok = true, result = await DispatchAsync(method, args) });
        }
        catch (NotLoggedInException ex)
        {
            Post(new { id, ok = false, code = "login", error = ex.Message });
        }
        catch (RateLimitedException ex)
        {
            Post(new { id, ok = false, code = "rate", error = ex.Message });
        }
        catch (Exception ex)
        {
            Post(new { id, ok = false, code = "error", error = ex.Message });
        }
    }

    async Task<object?> DispatchAsync(string method, JsonElement args)
    {
        switch (method)
        {
            case "state":
                return State();
            case "login":
                return await LoginAsync();
            case "loginBrowser":
                Process.Start(new ProcessStartInfo(session.BeginBrowserLogin().Url) { UseShellExecute = true });
                lastClipboard = ReadClipboard(); // only react to what is copied from now on
                clipboardWatch.Start();
                return null;
            case "loginPaste":
                return await CompleteBrowserLoginAsync(Arg(args, "url"));
            case "logout":
            {
                var removed = await session.LogoutAsync();
                return new { loggedIn = false, deviceRemoved = removed };
            }
            case "devices":
                return await api.GetDevicesAsync();
            case "notifications":
                return await api.GetNotificationsAsync(fresh: args.ValueKind == JsonValueKind.Object
                    && args.TryGetProperty("fresh", out var fresh) && fresh.GetBoolean());
            case "smarthome":
                return await api.GetEndpointsAsync();
            case "command":
                await api.TextCommandAsync(Arg(args, "serial"), Arg(args, "deviceType"),
                    args.TryGetProperty("customerId", out var c) ? c.GetString() : null, Arg(args, "text"));
                return null;
            case "setFeature":
                await api.SetFeatureAsync(Arg(args, "id"), Arg(args, "feature"), Arg(args, "operation"),
                    args.TryGetProperty("instance", out var inst) ? inst.GetString() : null,
                    args.TryGetProperty("payload", out var payload) ? JsonNode.Parse(payload.GetRawText()) : null);
                return null;
            case "musicDefaults":
                return await api.GetMusicDefaultsAsync();
            case "openStore":
            {
                var query = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("query", out var q) ? q.GetString() : null;
                var url = $"https://{session.Region.WwwHost}/s?i=alexa-skills" + (string.IsNullOrWhiteSpace(query) ? "" : "&k=" + Uri.EscapeDataString(query));
                if (store is { IsDisposed: false })
                {
                    store.Navigate(url);
                    return null;
                }
                store = new StoreForm(Path.Combine(dataDir, "store"), session.Region.CookieDomain, await session.GetWebCookiesAsync(), url);
                store.Show();
                return null;
            }
            case "speak":
                await api.SpeakAsync(Arg(args, "serial"), Arg(args, "deviceType"),
                    args.TryGetProperty("customerId", out var speakOwner) ? speakOwner.GetString() : null, Arg(args, "text"));
                return null;
            case "routines":
                return await routines.ListAsync();
            case "routineRun":
                await routines.RunAsync(Arg(args, "id"));
                return null;
            case "routineEnable":
                await routines.SetEnabledAsync(Arg(args, "id"), args.GetProperty("enabled").GetBoolean());
                return null;
            case "routineDelete":
                await routines.DeleteAsync(Arg(args, "id"));
                return null;
            case "notificationCreate":
            {
                var when = DateTime.ParseExact($"{Arg(args, "date")} {Arg(args, "time")}", "yyyy-MM-dd HH:mm", null);
                var label = args.TryGetProperty("label", out var l) ? l.GetString() : null;
                var recurring = args.TryGetProperty("recurring", out var r) ? r.GetString() : null;
                return await api.CreateNotificationAsync(Arg(args, "type"), Arg(args, "serial"), Arg(args, "deviceType"),
                    when, label, string.IsNullOrEmpty(recurring) ? null : recurring);
            }
            case "notificationDelete":
                await api.DeleteNotificationAsync(JsonNode.Parse(args.GetProperty("notification").GetRawText()) as JsonObject
                    ?? throw new ArgumentException("Parâmetro ausente: notification"));
                return null;
            case "loadData":
                return LocalData.Load(dataDir, Arg(args, "key"));
            case "saveData":
                LocalData.Save(dataDir, Arg(args, "key"), args.GetProperty("value"));
                return null;
            case "power":
                await api.SetPowerAsync(Arg(args, "id"), args.GetProperty("on").GetBoolean());
                return null;
            case "btPaired":
                return await BluetoothSpeaker.FindPairedAsync();
            case "btUnpair":
                await BluetoothSpeaker.UnpairAsync(Arg(args, "id"));
                return null;
            case "btPairingMode":
                await api.EnablePairingModeAsync(Arg(args, "serial"), Arg(args, "deviceType"), Arg(args, "softwareVersion"),
                    args.TryGetProperty("customerId", out var owner) ? owner.GetString() : null);
                return null;
            case "btCancelPairingMode":
                await api.CancelPairingModeAsync(Arg(args, "serial"), Arg(args, "deviceType"));
                return null;
            case "btDiscover":
                return await BluetoothSpeaker.DiscoverAsync(TimeSpan.FromSeconds(30));
            case "btPair":
                await BluetoothSpeaker.PairAsync(Arg(args, "id"));
                return null;
            case "mode":
                ApplyMode(app: Arg(args, "mode") == "app");
                return State();
            case "hide":
                Hide();
                return null;
            case "minimize":
                MinimizeToTaskbar();
                return null;
            case "listen":
                return await voice.ListenAsync((state, progress) => Post(new { @event = "voice", state, progress }));
            case "stopListening":
                voice.Stop();
                return null;
            case "prefs":
                return PrefsView();
            case "setHotkey":
            {
                var modifiers = args.GetProperty("modifiers").GetInt32();
                var key = args.GetProperty("key").GetInt32();
                if (!GlobalHotkey.Register(Handle, modifiers, key))
                {
                    // Put the previous shortcut back before reporting the conflict.
                    if (prefs.HotkeyLabel is not null) GlobalHotkey.Register(Handle, prefs.HotkeyModifiers, prefs.HotkeyKey);
                    throw new AlexaException("Esse atalho já está em uso por outro programa. Escolha outra combinação.");
                }
                (prefs.HotkeyModifiers, prefs.HotkeyKey, prefs.HotkeyLabel) = (modifiers, key, Arg(args, "label"));
                prefs.Save(dataDir);
                return PrefsView();
            }
            case "setCommandHotkeys":
            {
                // Replaces the whole set; items are in quick-command order, null where a command has no shortcut.
                for (var i = 0; i < commandHotkeys; i++) GlobalHotkey.Unregister(Handle, GlobalHotkey.CommandIdBase + i);
                var failed = new List<int>();
                var index = 0;
                foreach (var item in args.GetProperty("items").EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && !GlobalHotkey.Register(Handle,
                            item.GetProperty("modifiers").GetInt32(), item.GetProperty("key").GetInt32(), GlobalHotkey.CommandIdBase + index))
                        failed.Add(index);
                    index++;
                }
                commandHotkeys = index;
                return new { failed };
            }
            case "notify":
                tray.ShowBalloonTip(2500, "Alexa", Arg(args, "text"), ToolTipIcon.None);
                return null;
            case "clearHotkey":
                GlobalHotkey.Unregister(Handle);
                prefs.HotkeyLabel = null;
                prefs.Save(dataDir);
                return PrefsView();
            case "setMouseButton":
                prefs.MouseButton = args.TryGetProperty("button", out var b) && b.GetString() is { Length: > 0 } name ? name : null;
                ApplyMouseButton();
                prefs.Save(dataDir);
                return PrefsView();
            default:
                throw new InvalidOperationException("Ação desconhecida: " + method);
        }
    }

    object State() => new { loggedIn = session.IsLoggedIn, mode = appMode ? "app" : "compact", region = session.Region.AmazonDomain };

    async Task<object> LoginAsync()
    {
        var request = session.CreateLoginRequest();
        using var dialog = new LoginForm(request, Path.Combine(dataDir, "login"));
        var result = dialog.ShowDialog(this);
        if (dialog.Error is { } error) throw new AlexaException(error);
        if (result == DialogResult.OK && dialog.Code is { } code)
            await session.CompleteLoginAsync(code, request.Verifier, dialog.Cookies);
        return State(); // window closed by the user: still signed out, nothing to report
    }

    /// <summary>
    /// Finishes a sign-in done in the user's own browser: the page it ends on (…/ap/maplanding?…) carries the
    /// authorization code, which only this app can redeem because it holds the PKCE verifier.
    /// </summary>
    async Task<object> CompleteBrowserLoginAsync(string pasted)
    {
        pasted = pasted.Trim();
        string? code;
        if (LoginCallback.TryRead(pasted, session.CallbackUrl, out var fromUrl, out var error))
            code = fromUrl ?? throw new AlexaException(error!);
        else if (Uri.TryCreate(pasted, UriKind.Absolute, out _) || pasted.Length == 0 || pasted.Contains(' '))
            throw new AlexaException("Esse não é o endereço do final do login. Copie a barra de endereço inteira da página que abriu depois de entrar (começa com " + session.CallbackUrl + ").");
        else
            code = pasted; // just the code itself

        clipboardWatch.Stop();
        await session.CompleteBrowserLoginAsync(code);
        return State();
    }

    // While a browser sign-in is pending, copying the final address is enough: no need to paste it.
    async Task CheckClipboardForLoginAsync()
    {
        if (!session.HasPendingBrowserLogin)
        {
            clipboardWatch.Stop();
            return;
        }
        var text = ReadClipboard();
        if (text is null || text == lastClipboard) return;
        lastClipboard = text;
        if (!LoginCallback.TryRead(text.Trim(), session.CallbackUrl, out _, out _)) return;

        clipboardWatch.Stop();
        try
        {
            await CompleteBrowserLoginAsync(text);
            ShowWindow();
            Post(new { @event = "login", ok = true });
        }
        catch (Exception ex)
        {
            Post(new { @event = "login", ok = false, error = ex.Message });
        }
    }

    static string? ReadClipboard()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText() : null;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return null; // another program is holding the clipboard; try again on the next tick
        }
    }

    void ApplyMode(bool app, bool initial = false)
    {
        if (!initial && app == appMode)
        {
            ShowWindow();
            return;
        }
        if (!initial && appMode) appBounds = Bounds;

        appMode = app;
        inTaskbar = false;
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        var wasVisible = Visible;
        if (wasVisible) Hide();

        FormBorderStyle = app ? FormBorderStyle.Sizable : FormBorderStyle.None;
        TopMost = !app;
        UpdateStyles();

        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        if (app)
        {
            MinimumSize = LogicalToDeviceUnits(new Size(760, 540));
            var size = LogicalToDeviceUnits(new Size(1080, 740));
            Bounds = appBounds ?? new Rectangle(area.Left + (area.Width - size.Width) / 2, area.Top + (area.Height - size.Height) / 2,
                size.Width, size.Height);
        }
        else
        {
            MinimumSize = Size.Empty;
            var size = LogicalToDeviceUnits(new Size(380, 600));
            var margin = LogicalToDeviceUnits(16);
            Bounds = new Rectangle(area.Right - size.Width - margin, area.Bottom - size.Height - margin, size.Width, size.Height);
        }

        if (initial) return;
        ShowWindow();
        Post(new { @event = "mode", mode = appMode ? "app" : "compact" });
    }

    void ShowWindow()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Widget", null, (_, _) => ApplyMode(app: false));
        menu.Items.Add("Janela do app", null, (_, _) => ApplyMode(app: true));
        menu.Items.Add(new ToolStripSeparator());
        var startup = new ToolStripMenuItem("Iniciar com o Windows") { Checked = Startup.IsEnabled, CheckOnClick = true };
        startup.CheckedChanged += (_, _) => Startup.Set(startup.Checked);
        menu.Items.Add(startup);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) =>
        {
            exiting = true;
            Close();
        });
        return menu;
    }

    void Post(object message)
    {
        if (web.CoreWebView2 is { } core)
            core.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions));
    }

    static string Arg(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.GetString() is { } s
            ? s
            : throw new ArgumentException("Parâmetro ausente: " + name);
}
