using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace CodexHud;

internal static class WindowPlacementService
{
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    internal static void Restore(Window window, HudSettings settings)
    {
        var h = new WindowInteropHelper(window).Handle;
        if (h == IntPtr.Zero || !GetWindowRect(h, out var rect)) return;
        var primary = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        var x = settings.X ?? primary.Left + (primary.Width - (rect.Right - rect.Left)) / 2;
        var y = settings.Y ?? primary.Top + 24;
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(x, y)).WorkingArea;
        x = Math.Clamp(x, screen.Left, Math.Max(screen.Left, screen.Right - (rect.Right - rect.Left)));
        y = Math.Clamp(y, screen.Top, Math.Max(screen.Top, screen.Bottom - (rect.Bottom - rect.Top)));
        SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, 0x0015); // NOSIZE | NOZORDER | NOACTIVATE
    }
    internal static void Remember(Window window, HudSettings settings)
    {
        if (GetWindowRect(new WindowInteropHelper(window).Handle, out var r)) { settings.X = r.Left; settings.Y = r.Top; }
    }
    internal static double WorkingHeightDip(Window window, HudSettings settings)
    {
        var primary = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(settings.X ?? primary.Left, settings.Y ?? primary.Top));
        return screen.WorkingArea.Height / System.Windows.Media.VisualTreeHelper.GetDpi(window).DpiScaleY;
    }
    internal static bool StartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return key?.GetValue("CodexHud") is string;
    }
    internal static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("CodexHud", "\"" + Environment.ProcessPath + "\"");
        else key.DeleteValue("CodexHud", false);
    }
}
