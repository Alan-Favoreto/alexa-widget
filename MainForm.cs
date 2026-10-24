using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

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
    Shortcuts? shortcuts;
    List<Shortcut> commandShortcuts = []; // quick-command/device shortcuts, id CommandIdBase + their index
    bool exiting;
    Rectangle? appBounds;
    readonly System.Windows.Forms.Timer clipboardWatch = new() { Interval = 1000 };
    readonly Dictionary<string, Func<JsonElement, Task<object?>>> handlers;
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
        handlers = CreateHandlers();

        Text = "Alexa";
        StartPosition = FormStartPosition.Manual;
        Icon = AppIcon.Instance;
        Theme.Follow(this, web, webBackground: true);
        Controls.Add(web);
        // Back from sleep: routines whose time passed meanwhile can still run, and the data is stale.
        PowerModeChangedEventHandler resumed = (_, e) =>
        {
            if (e.Mode == PowerModes.Resume && IsHandleCreated) BeginInvoke(() => Post(new { @event = "resume" }));
        };
        SystemEvents.PowerModeChanged += resumed;
        FormClosed += (_, _) => SystemEvents.PowerModeChanged -= resumed;

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
        shortcuts?.Dispose();
        shortcuts = new Shortcuts(Handle, id => BeginInvoke(() => FireShortcut(id)));
        try
        {
            if (ApplyShortcuts().Contains(GlobalHotkey.Id))
                prefs.HotkeyLabel = null; // taken by another program; the settings screen shows it as unset
        }
        catch (AlexaException)
        {
            prefs.HotkeyLabel = null;
        }
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
        else if (m.Msg == GlobalHotkey.WmHotkey) FireShortcut((int)m.WParam);
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

    void FireShortcut(int id)
    {
        if (id == GlobalHotkey.Id) TriggerVoice();
        else if (id >= GlobalHotkey.CommandIdBase) Post(new { @event = "command-hotkey", index = id - GlobalHotkey.CommandIdBase });
    }

    /// <summary>Registers the voice shortcut and the command ones together; returns the ids that failed.</summary>
    List<int> ApplyShortcuts()
    {
        var voiceShortcut = prefs.HotkeyLabel is null
            ? []
            : new[] { new Shortcut(GlobalHotkey.Id, prefs.HotkeyModifiers, prefs.HotkeyKey, prefs.HotkeyTaps) };
        return shortcuts!.Apply(voiceShortcut.Concat(commandShortcuts));
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
        shortcuts?.Dispose();
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

        var id = 0;
        try
        {
            // Everything inside the try: this is async void, so anything escaping would close the app.
            var message = JsonDocument.Parse(e.WebMessageAsJson).RootElement.Clone();
            id = message.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out var parsed) ? parsed : 0;
            var method = message.TryGetProperty("method", out var methodProp) && methodProp.ValueKind == JsonValueKind.String
                ? methodProp.GetString()!
                : "";
            var args = message.TryGetProperty("params", out var paramsProp) ? paramsProp : default;
            if (method is "listen" or "stopListening") voice.Trace?.Invoke($"ui: {method} received");
            if (!handlers.TryGetValue(method, out var handler))
                throw new InvalidOperationException("Ação desconhecida: " + method);

            // Leave the WebView2 callback before doing anything: a modal dialog (the login window) opened
            // inside it runs a nested message loop that hangs WebView2.
            await Task.Yield();
            Post(new { id, ok = true, result = await handler(args) });
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

    /// <summary>What the UI (wwwroot) can ask for: method name → handler taking the message's "params".</summary>
    Dictionary<string, Func<JsonElement, Task<object?>>> CreateHandlers()
    {
        var table = new Dictionary<string, Func<JsonElement, Task<object?>>>();
        void On(string method, Func<JsonElement, Task<object?>> handler) => table.Add(method, handler);
        void Do(string method, Func<JsonElement, Task> action) => table.Add(method, async a => { await action(a); return null; });
        void Get(string method, Func<JsonElement, object?> read) => table.Add(method, a => Task.FromResult(read(a)));

        // Account
        Get("state", _ => State());
        On("login", async _ => await LoginAsync());
        Get("loginBrowser", _ =>
        {
            Process.Start(new ProcessStartInfo(session.BeginBrowserLogin().Url) { UseShellExecute = true });
            lastClipboard = ReadClipboard(); // only react to what is copied from now on
            clipboardWatch.Start();
            return null;
        });
        On("loginPaste", async a => await CompleteBrowserLoginAsync(Arg(a, "url")));
        On("logout", async _ => new { loggedIn = false, deviceRemoved = await session.LogoutAsync() });

        // Echos, commands and smart home
        On("devices", async _ => await api.GetDevicesAsync());
        On("smarthome", async _ => await api.GetEndpointsAsync());
        Do("command", a => api.TextCommandAsync(Arg(a, "serial"), Arg(a, "deviceType"), OptArg(a, "customerId"), Arg(a, "text")));
        Do("speak", a => api.SpeakAsync(Arg(a, "serial"), Arg(a, "deviceType"), OptArg(a, "customerId"), Arg(a, "text")));
        Do("power", a => api.SetPowerAsync(Arg(a, "id"), BoolArg(a, "on")));
        Do("setFeature", a => api.SetFeatureAsync(Arg(a, "id"), Arg(a, "feature"), Arg(a, "operation"), OptArg(a, "instance"),
            a.TryGetProperty("payload", out var payload) ? JsonNode.Parse(payload.GetRawText()) : null));
        On("musicDefaults", async _ => await api.GetMusicDefaultsAsync());
        On("openStore", async a =>
        {
            var query = OptArg(a, "query");
            var url = $"https://{session.Region.WwwHost}/s?i=alexa-skills" + (string.IsNullOrWhiteSpace(query) ? "" : "&k=" + Uri.EscapeDataString(query));
            if (store is { IsDisposed: false })
            {
                store.Navigate(url);
                return null;
            }
            store = new StoreForm(Path.Combine(dataDir, "store"), session.Region.CookieDomain, await session.GetWebCookiesAsync(), url);
            store.Show();
            return null;
        });

        // Alarms, reminders and routines made in the Alexa app
        On("notifications", async a => await api.GetNotificationsAsync(fresh: a.ValueKind == JsonValueKind.Object
            && a.TryGetProperty("fresh", out var fresh) && fresh.ValueKind == JsonValueKind.True));
        On("notificationCreate", async a =>
        {
            var when = DateTime.ParseExact($"{Arg(a, "date")} {Arg(a, "time")}", "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            return await api.CreateNotificationAsync(Arg(a, "type"), Arg(a, "serial"), Arg(a, "deviceType"),
                when, OptArg(a, "label"), OptArg(a, "recurring") is { Length: > 0 } recurring ? recurring : null);
        });
        Do("notificationDelete", a => api.DeleteNotificationAsync(
            a.TryGetProperty("notification", out var n) && JsonNode.Parse(n.GetRawText()) is JsonObject notification
                ? notification
                : throw new ArgumentException("Parâmetro ausente: notification")));
        On("routines", async _ => await routines.ListAsync());
        Do("routineRun", a => routines.RunAsync(Arg(a, "id")));
        Do("routineEnable", a => routines.SetEnabledAsync(Arg(a, "id"), BoolArg(a, "enabled")));
        Do("routineDelete", a => routines.DeleteAsync(Arg(a, "id")));

        // Data kept by the UI (quick commands, widget routines, phrase fixes…)
        Get("loadData", a => LocalData.Load(dataDir, Arg(a, "key")));
        Get("saveData", a =>
        {
            LocalData.Save(dataDir, Arg(a, "key"), a.GetProperty("value"));
            return null;
        });

        // Echo as the PC's Bluetooth speaker
        On("btPaired", async _ => await BluetoothSpeaker.FindPairedAsync());
        Do("btUnpair", a => BluetoothSpeaker.UnpairAsync(Arg(a, "id")));
        Do("btPairingMode", a => api.EnablePairingModeAsync(Arg(a, "serial"), Arg(a, "deviceType"), Arg(a, "softwareVersion"), OptArg(a, "customerId")));
        Do("btCancelPairingMode", a => api.CancelPairingModeAsync(Arg(a, "serial"), Arg(a, "deviceType")));
        On("btDiscover", async _ => await BluetoothSpeaker.DiscoverAsync(TimeSpan.FromSeconds(30)));
        Do("btPair", a => BluetoothSpeaker.PairAsync(Arg(a, "id")));

        // Window
        Get("mode", a =>
        {
            ApplyMode(app: Arg(a, "mode") == "app");
            return State();
        });
        Get("hide", _ =>
        {
            Hide();
            return null;
        });
        Get("minimize", _ =>
        {
            MinimizeToTaskbar();
            return null;
        });
        Get("notify", a =>
        {
            tray.ShowBalloonTip(2500, "Alexa", Arg(a, "text"), ToolTipIcon.None);
            return null;
        });

        // Voice
        On("listen", async _ => await voice.ListenAsync((state, progress) => Post(new { @event = "voice", state, progress })));
        Get("stopListening", _ =>
        {
            voice.Stop();
            return null;
        });

        // Shortcuts and mouse button
        Get("prefs", _ => PrefsView());
        Get("setHotkey", a =>
        {
            var previous = (prefs.HotkeyModifiers, prefs.HotkeyKey, prefs.HotkeyLabel, prefs.HotkeyTaps);
            (prefs.HotkeyModifiers, prefs.HotkeyKey, prefs.HotkeyLabel, prefs.HotkeyTaps) =
                (a.GetProperty("modifiers").GetInt32(), a.GetProperty("key").GetInt32(), Arg(a, "label"), Taps(a));
            if (ApplyShortcuts().Contains(GlobalHotkey.Id))
            {
                // Put the previous shortcut back before reporting the conflict.
                (prefs.HotkeyModifiers, prefs.HotkeyKey, prefs.HotkeyLabel, prefs.HotkeyTaps) = previous;
                ApplyShortcuts();
                throw new AlexaException("Esse atalho já está em uso por outro programa. Escolha outra combinação.");
            }
            prefs.Save(dataDir);
            return PrefsView();
        });
        Get("clearHotkey", _ =>
        {
            prefs.HotkeyLabel = null;
            ApplyShortcuts();
            prefs.Save(dataDir);
            return PrefsView();
        });
        Get("setCommandHotkeys", a =>
        {
            // Replaces the whole set; items are in quick-command order, null where a command has no shortcut.
            commandShortcuts = a.GetProperty("items").EnumerateArray()
                .Select((item, index) => item.ValueKind == JsonValueKind.Object
                    ? new Shortcut(GlobalHotkey.CommandIdBase + index, item.GetProperty("modifiers").GetInt32(), item.GetProperty("key").GetInt32(), Taps(item))
                    : null)
                .OfType<Shortcut>()
                .ToList();
            var failed = ApplyShortcuts().Where(id => id >= GlobalHotkey.CommandIdBase).Select(id => id - GlobalHotkey.CommandIdBase).Order();
            return new { failed };
        });
        Get("captureKeys", a =>
        {
            // On while the settings screen records a shortcut: multimedia keys never reach the page otherwise.
            shortcuts!.Capture = a.GetProperty("on").GetBoolean()
                ? (modifiers, key) => BeginInvoke(() => Post(new { @event = "key-captured", modifiers, key }))
                : null;
            return null;
        });
        Get("setMouseButton", a =>
        {
            prefs.MouseButton = OptArg(a, "button") is { Length: > 0 } name ? name : null;
            ApplyMouseButton();
            prefs.Save(dataDir);
            return PrefsView();
        });
        return table;
    }

    object State() => new
    {
        loggedIn = session.IsLoggedIn,
        mode = appMode ? "app" : "compact",
        region = session.Region.AmazonDomain,
        locale = session.Region.Locale,
    };

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
        OptArg(args, name) ?? throw new ArgumentException("Parâmetro ausente: " + name);

    static string? OptArg(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    // Shortcuts saved before double presses existed have no "taps".
    static int Taps(JsonElement args) =>
        args.TryGetProperty("taps", out var v) && v.ValueKind == JsonValueKind.Number ? Math.Clamp(v.GetInt32(), 1, 3) : 1;

    static bool BoolArg(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : throw new ArgumentException("Parâmetro ausente: " + name);
}
