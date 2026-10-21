using System.Runtime.InteropServices;
using System.Text.Json;

namespace AlexaWidget;

/// <summary>User preferences kept in %LOCALAPPDATA%\AlexaWidget\prefs.json.</summary>
sealed class Prefs
{
    public const int ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4;

    public int HotkeyModifiers { get; set; } = ModControl | ModAlt;
    public int HotkeyKey { get; set; } = 'A';
    public string? HotkeyLabel { get; set; } = "Ctrl+Alt+A";

    /// <summary>"XButton1" (side back), "XButton2" (side forward), "Middle", or null.</summary>
    public string? MouseButton { get; set; }

    public static Prefs Load(string dataDir)
    {
        try
        {
            var path = Path.Combine(dataDir, "prefs.json");
            if (File.Exists(path)) return JsonSerializer.Deserialize<Prefs>(File.ReadAllText(path)) ?? new Prefs();
        }
        catch (JsonException) { }
        return new Prefs();
    }

    public void Save(string dataDir) =>
        File.WriteAllText(Path.Combine(dataDir, "prefs.json"), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}

/// <summary>
/// Small JSON documents the UI keeps (quick commands, widget routines, phrase fixes), one file per key in
/// %LOCALAPPDATA%\AlexaWidget\data. Kept outside the WebView profile so clearing it loses nothing.
/// </summary>
static class LocalData
{
    static string PathFor(string dataDir, string key)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(key, "^[a-z][a-z0-9-]{0,40}$"))
            throw new ArgumentException("Chave inválida: " + key);
        var dir = Path.Combine(dataDir, "data");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, key + ".json");
    }

    public static JsonElement? Load(string dataDir, string key)
    {
        var path = PathFor(dataDir, key);
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static void Save(string dataDir, string key, JsonElement value)
    {
        var path = PathFor(dataDir, key);
        File.WriteAllText(path + ".tmp", value.GetRawText());
        File.Move(path + ".tmp", path, overwrite: true);
    }
}

/// <summary>Lets a second launch bring the running widget forward (it may be hidden in the tray).</summary>
static class SingleInstance
{
    public static readonly int ShowMessage = RegisterWindowMessage("AlexaWidget.Show");
    const int HwndBroadcast = 0xffff;

    /// <param name="appMode">wParam 1 asks for the app window, 0 for the widget.</param>
    public static void SignalShow(bool appMode) => PostMessage(HwndBroadcast, ShowMessage, appMode ? 1 : 0, 0);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    static extern bool PostMessage(nint hwnd, int msg, nint wParam, nint lParam);
}

/// <summary>System-wide keyboard shortcut via RegisterHotKey; WM_HOTKEY arrives in the owner window's WndProc.</summary>
static class GlobalHotkey
{
    public const int WmHotkey = 0x0312;
    public const int Id = 1;               // voice
    public const int CommandIdBase = 100;  // quick command i → CommandIdBase + i
    const int ModNoRepeat = 0x4000;

    /// <summary>Fails when any program (this one included) already owns the combination.</summary>
    public static bool Register(IntPtr hwnd, int modifiers, int key, int id = Id)
    {
        UnregisterHotKey(hwnd, id);
        return RegisterHotKey(hwnd, id, modifiers | ModNoRepeat, key);
    }

    public static void Unregister(IntPtr hwnd, int id = Id) => UnregisterHotKey(hwnd, id);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

/// <summary>
/// Low-level mouse hook that turns one extra mouse button into the voice trigger. The chosen button is swallowed,
/// so it stops doing its usual job (e.g. browser back) while the widget runs.
/// </summary>
sealed class MouseButtonHook : IDisposable
{
    const int WhMouseLl = 14;
    const int WmMButtonDown = 0x0207, WmMButtonUp = 0x0208, WmXButtonDown = 0x020B, WmXButtonUp = 0x020C;

    readonly HookProc proc;
    readonly string button;
    readonly Action pressed;
    IntPtr hook;

    public MouseButtonHook(string button, Action pressed)
    {
        this.button = button;
        this.pressed = pressed;
        proc = Callback; // keep the delegate alive for as long as the hook is installed
        hook = SetWindowsHookEx(WhMouseLl, proc, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) throw new AlexaException("O Windows não permitiu monitorar o botão do mouse.");
    }

    IntPtr Callback(int code, IntPtr message, IntPtr info)
    {
        if (code >= 0)
        {
            var msg = (int)message;
            string? which = msg switch
            {
                WmXButtonDown or WmXButtonUp => (Marshal.PtrToStructure<MouseInfo>(info).MouseData >> 16) == 1 ? "XButton1" : "XButton2",
                WmMButtonDown or WmMButtonUp => "Middle",
                _ => null,
            };
            if (which == button)
            {
                if (msg is WmXButtonDown or WmMButtonDown) pressed();
                return 1;
            }
        }
        return CallNextHookEx(hook, code, message, info);
    }

    public void Dispose()
    {
        if (hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(hook);
        hook = IntPtr.Zero;
    }

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct MouseInfo
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint threadId);

    [DllImport("user32.dll")]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetModuleHandle(string? name);
}
