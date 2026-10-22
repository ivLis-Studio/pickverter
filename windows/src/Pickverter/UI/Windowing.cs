using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Pickverter.Interop;

namespace Pickverter.UI;

/// Windows only lets the app that received the last input take the foreground. The wheel is driven through
/// a keyboard hook while File Explorer stays active, so dialogs and editors need a little help to come forward.
internal static class Windowing
{
    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool on);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hwnd);

    public static void Activate(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        Bring(hwnd);
        window.Activate();
    }

    public static void Bring(IntPtr hwnd)
    {
        var foreground = Native.GetForegroundWindow();
        if (foreground == hwnd) return;
        var theirs = Native.GetWindowThreadProcessId(foreground, out _);
        var ours = Native.GetCurrentThreadId();
        if (theirs != 0 && theirs != ours)
        {
            AttachThreadInput(ours, theirs, true);
            Native.SetForegroundWindow(hwnd);
            BringWindowToTop(hwnd);
            AttachThreadInput(ours, theirs, false);
        }
        else
        {
            Native.SetForegroundWindow(hwnd);
        }
    }

    /// An invisible window that owns a system dialog or the share sheet, already in front.
    public static Window Owner(ScreenPoint? at = null)
    {
        Native.GetCursorPos(out var cursor);
        var x = at is { IsZero: false } point ? point.X : cursor.X;
        var y = at is { IsZero: false } p2 ? p2.Y : cursor.Y;
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = true,
            Topmost = true,
            Width = 1,
            Height = 1,
            Title = "Pickverter",
        };
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        Native.SetWindowPos(hwnd, IntPtr.Zero, (int)x, (int)y, 1, 1, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        window.Show();
        Activate(window);
        return window;
    }

    /// Tool windows stay out of Alt+Tab and the taskbar; no-activate windows never steal the keyboard.
    public static void MakeOverlay(Window window, bool noActivate = true, bool transparentToMouse = false)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        var add = Native.WS_EX_TOOLWINDOW | (noActivate ? Native.WS_EX_NOACTIVATE : 0) | (transparentToMouse ? Native.WS_EX_TRANSPARENT : 0);
        Native.AddExStyle(hwnd, add, Native.WS_EX_APPWINDOW);
    }

    /// Windows 11 rounded corners and a theme-matched border (ignored on Windows 10).
    public static void Round(Window window, bool small = false)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        Native.SetDwm(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, small ? Native.DWMWCP_ROUNDSMALL : Native.DWMWCP_ROUND);
        Native.SetDwm(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, Theme.IsDark ? 1 : 0);
    }

    public static void DarkTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        Native.SetDwm(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, Theme.IsDark ? 1 : 0);
    }
}
