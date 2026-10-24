using System.Runtime.InteropServices;

namespace AlexaWidget;

/// <summary>A global shortcut: <paramref name="Taps"/> presses of the key in a row (1 = a normal shortcut).</summary>
sealed record Shortcut(int Id, int Modifiers, int Key, int Taps = 1);

/// <summary>
/// Owns every global shortcut. Plain ones go through RegisterHotKey (which reports combinations other programs hold);
/// multimedia keys (a keyboard knob's click is "Mute") and keys with a double/triple-press shortcut go through
/// <see cref="KeyTapHook"/>, which can see them and count presses.
/// </summary>
sealed class Shortcuts(IntPtr hwnd, Action<int> fire) : IDisposable
{
    readonly List<int> registered = [];
    List<Shortcut> tapped = [];
    KeyTapHook? hook;
    Action<int, int>? capture;

    /// <summary>Replaces the whole set; returns the ids that could not be registered (taken or repeated).</summary>
    public List<int> Apply(IEnumerable<Shortcut> all)
    {
        foreach (var id in registered) GlobalHotkey.Unregister(hwnd, id);
        registered.Clear();
        var (plain, viaHook, failed) = Split(all);
        foreach (var s in plain)
        {
            if (GlobalHotkey.Register(hwnd, s.Modifiers, s.Key, s.Id)) registered.Add(s.Id);
            else failed.Add(s.Id);
        }
        tapped = viaHook;
        UpdateHook();
        return failed;
    }

    /// <summary>
    /// While set, multimedia keys are swallowed and reported here (modifiers, key) instead of acting, so the settings
    /// screen can record them: the page itself never receives them.
    /// </summary>
    public Action<int, int>? Capture
    {
        get => capture;
        set
        {
            capture = value;
            UpdateHook();
        }
    }

    /// <summary>Which shortcuts RegisterHotKey can take and which need the hook; repeats of a shortcut fail.</summary>
    public static (List<Shortcut> Plain, List<Shortcut> ViaHook, List<int> Failed) Split(IEnumerable<Shortcut> all)
    {
        var plain = new List<Shortcut>();
        var viaHook = new List<Shortcut>();
        var failed = new List<int>();
        foreach (var group in all.GroupBy(s => (s.Modifiers, s.Key)))
        {
            var unique = new List<Shortcut>();
            foreach (var s in group)
                if (unique.Any(u => u.Taps == s.Taps)) failed.Add(s.Id);
                else unique.Add(s);
            // The hook counts the presses for every shortcut on that key, so RegisterHotKey must not take the single one.
            if (KeyTapHook.IsMediaKey(group.Key.Key) || unique.Any(s => s.Taps > 1)) viaHook.AddRange(unique);
            else plain.AddRange(unique);
        }
        return (plain, viaHook, failed);
    }

    void UpdateHook()
    {
        if (tapped.Count == 0 && capture is null)
        {
            hook?.Dispose();
            hook = null;
            return;
        }
        hook ??= new KeyTapHook(fire);
        hook.Shortcuts = tapped;
        hook.Capture = capture;
    }

    public void Dispose()
    {
        foreach (var id in registered) GlobalHotkey.Unregister(hwnd, id);
        registered.Clear();
        hook?.Dispose();
        hook = null;
    }
}

/// <summary>
/// Low-level keyboard hook for <see cref="Shortcuts"/>: swallows presses of the keys it watches and, once no further
/// press comes within <see cref="TapWindow"/>, runs the shortcut for that number of presses. With nothing bound to
/// that count, a key pressed without modifiers is sent on unchanged (a single click of the knob still mutes).
/// Must be created on a thread with a message loop (the UI thread).
/// </summary>
sealed class KeyTapHook : IDisposable
{
    const int WhKeyboardLl = 13;
    const int WmKeyDown = 0x0100, WmKeyUp = 0x0101, WmSysKeyDown = 0x0104, WmSysKeyUp = 0x0105;
    const uint LlkhfExtended = 0x01;
    const uint KeyeventfExtendedKey = 0x1, KeyeventfKeyUp = 0x2;
    static readonly IntPtr Replayed = 0x414C5857; // marks the keys this hook sends back, so it lets them through
    public static readonly TimeSpan TapWindow = TimeSpan.FromMilliseconds(400);

    readonly HookProc proc;
    readonly Action<int> fire;
    readonly System.Windows.Forms.Timer timer = new() { Interval = (int)TapWindow.TotalMilliseconds };
    readonly HashSet<uint> held = []; // swallowed keys not yet released: their auto-repeat and key-up are swallowed too
    IntPtr hook;
    KeyInfo? pending;
    int pendingModifiers, taps;

    public IReadOnlyList<Shortcut> Shortcuts { get; set; } = [];
    public Action<int, int>? Capture { get; set; }

    public KeyTapHook(Action<int> fire)
    {
        this.fire = fire;
        timer.Tick += (_, _) => Resolve();
        proc = Callback; // keep the delegate alive for as long as the hook is installed
        hook = SetWindowsHookEx(WhKeyboardLl, proc, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) throw new AlexaException("O Windows não permitiu monitorar o teclado.");
    }

    /// <summary>Browser, volume and media keys (VK 0xA6–0xB7), usable on their own as shortcuts.</summary>
    public static bool IsMediaKey(int key) => key is >= 0xA6 and <= 0xB7;

    IntPtr Callback(int code, IntPtr message, IntPtr infoPtr)
    {
        if (code < 0) return CallNextHookEx(hook, code, message, infoPtr);
        var info = Marshal.PtrToStructure<KeyInfo>(infoPtr);
        var msg = (int)message;
        if (info.ExtraInfo == Replayed) return CallNextHookEx(hook, code, message, infoPtr);
        if (msg is WmKeyUp or WmSysKeyUp)
            return held.Remove(info.VkCode) ? 1 : CallNextHookEx(hook, code, message, infoPtr);
        if (msg is not (WmKeyDown or WmSysKeyDown)) return CallNextHookEx(hook, code, message, infoPtr);
        if (held.Contains(info.VkCode)) return 1;

        var key = (int)info.VkCode;
        var modifiers = CurrentModifiers();
        if (Capture is { } capture)
        {
            // Recording a shortcut: ordinary keys go on to the page, which records them itself.
            if (!IsMediaKey(key)) return CallNextHookEx(hook, code, message, infoPtr);
            held.Add(info.VkCode);
            capture(modifiers, key);
            return 1;
        }
        var most = Shortcuts.Where(s => s.Key == key && s.Modifiers == modifiers).Select(s => s.Taps).DefaultIfEmpty(0).Max();
        if (most == 0) return CallNextHookEx(hook, code, message, infoPtr);

        held.Add(info.VkCode);
        if (pending is { } previous && (previous.VkCode != info.VkCode || pendingModifiers != modifiers)) Resolve();
        pending = info;
        pendingModifiers = modifiers;
        taps++;
        timer.Stop();
        if (taps >= most) Resolve(); // nothing longer is bound: no need to wait for another press
        else timer.Start();
        return 1;
    }

    void Resolve()
    {
        timer.Stop();
        if (pending is not { } key) return;
        var count = taps;
        pending = null;
        taps = 0;
        var match = Shortcuts.FirstOrDefault(s => s.Key == key.VkCode && s.Modifiers == pendingModifiers && s.Taps == count);
        if (match is not null) fire(match.Id);
        else if (pendingModifiers == 0)
            for (var i = 0; i < count; i++) Replay(key);
    }

    static void Replay(KeyInfo key)
    {
        var flags = (key.Flags & LlkhfExtended) != 0 ? KeyeventfExtendedKey : 0;
        Input[] inputs =
        [
            Input.Keyboard((ushort)key.VkCode, (ushort)key.ScanCode, flags),
            Input.Keyboard((ushort)key.VkCode, (ushort)key.ScanCode, flags | KeyeventfKeyUp),
        ];
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    static int CurrentModifiers() =>
        (GetAsyncKeyState(0x12) < 0 ? Prefs.ModAlt : 0) |
        (GetAsyncKeyState(0x11) < 0 ? Prefs.ModControl : 0) |
        (GetAsyncKeyState(0x10) < 0 ? Prefs.ModShift : 0);

    public void Dispose()
    {
        timer.Dispose();
        if (hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(hook);
        hook = IntPtr.Zero;
    }

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct KeyInfo
    {
        public uint VkCode, ScanCode, Flags, Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Input
    {
        public int Type;
        public InputUnion Data;

        public static Input Keyboard(ushort vk, ushort scan, uint flags) => new()
        {
            Type = 1,
            Data = new InputUnion { Keyboard = new KeyboardInput { Vk = vk, Scan = scan, Flags = flags, ExtraInfo = Replayed } },
        };
    }

    // Sized by its largest member (the mouse one): SendInput rejects a wrong cbSize.
    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KeyboardInput
    {
        public ushort Vk, Scan;
        public uint Flags, Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MouseInput
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint threadId);

    [DllImport("user32.dll")]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetModuleHandle(string? name);
}
