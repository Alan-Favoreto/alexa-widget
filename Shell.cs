using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AlexaWidget;

static class Theme
{
    const int DwmUseImmersiveDarkMode = 20;
    const int DwmWindowCornerPreference = 33;
    const int DwmCornerRound = 2;

    public static bool IsDark
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is 0;
        }
    }

    // Must match --bg in wwwroot/app.css so there is no flash while the page loads.
    public static Color Background => IsDark ? Color.FromArgb(0x18, 0x19, 0x1c) : Color.FromArgb(0xf3, 0xf4, 0xf6);

    public static void ApplyWindowFrame(IntPtr hwnd)
    {
        var dark = IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref dark, sizeof(int));
        var corner = DwmCornerRound;
        DwmSetWindowAttribute(hwnd, DwmWindowCornerPreference, ref corner, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

static class AppIcon
{
    public static Icon Instance { get; } = Create();

    // A plain ring with a speech-bubble tail; drawn at runtime so the project ships no image assets.
    static Icon Create()
    {
        using var bmp = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            var accent = Color.FromArgb(0x1f, 0xa8, 0xe0);
            using var pen = new Pen(accent, 9);
            g.DrawEllipse(pen, 9, 7, 46, 46);
            using var brush = new SolidBrush(accent);
            g.FillPolygon(brush, [new Point(12, 60), new Point(18, 42), new Point(30, 50)]);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}

static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "AlexaWidget";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
