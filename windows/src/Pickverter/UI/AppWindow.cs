using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Pickverter.Interop;

namespace Pickverter.UI;

/// A regular window (Settings, Welcome, Software Update, editors): theme colours, dark title bar in dark mode,
/// a fresh build whenever the language or the theme changes, and never bigger than the screen it opens on.
internal abstract class AppWindow : Window
{
    protected AppWindow()
    {
        FontFamily = Theme.TextFont;
        Background = Theme.Window;
        Foreground = Theme.TextPrimary;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        try
        {
            using var stream = typeof(AppWindow).Assembly.GetManifestResourceStream("Pickverter.ico");
            if (stream != null) Icon = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        }
        catch (Exception)
        {
        }
        SourceInitialized += (_, _) =>
        {
            Windowing.DarkTitleBar(this);
            FitToScreen();
        };
        Loaded += (_, _) => KeepOnScreen();
        Theme.Changed += OnTheme;
        Loc.Changed += OnLanguage;
        Closed += (_, _) =>
        {
            Theme.Changed -= OnTheme;
            Loc.Changed -= OnLanguage;
        };
    }

    private void OnTheme()
    {
        Background = Theme.Window;
        Foreground = Theme.TextPrimary;
        Windowing.DarkTitleBar(this);
        Rebuild();
    }

    private void OnLanguage()
    {
        FontFamily = Theme.TextFont;
        Rebuild();
    }

    protected abstract void Rebuild();

    /// A 1080p laptop at 150 % leaves about 690 px of height for windows: shrink to the work area of the monitor the
    /// window opens on (CenterScreen picks the one under the pointer) and let the content scroll, so the buttons along
    /// the bottom stay reachable.
    private void FitToScreen()
    {
        Native.GetCursorPos(out var cursor);
        var (_, work, scale) = Native.MonitorAt(cursor.X, cursor.Y);
        var width = work.Width / scale - 16;
        var height = work.Height / scale - 16;
        MinWidth = Math.Min(MinWidth, width);
        MinHeight = Math.Min(MinHeight, height);
        if (!double.IsNaN(Width) && Width > width) Width = width;
        if (!double.IsNaN(Height) && Height > height) Height = height;
    }

    /// Belt and braces once shown: pull the window fully into its monitor's work area (it never sits under the taskbar).
    private void KeepOnScreen()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out var rect)) return;
        var (_, work, _) = Native.MonitorAt((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
        if (rect.Left >= work.Left && rect.Top >= work.Top && rect.Right <= work.Right && rect.Bottom <= work.Bottom) return;
        var width = Math.Min(rect.Width, work.Width);
        var height = Math.Min(rect.Height, work.Height);
        var x = Math.Clamp(rect.Left, work.Left, work.Right - width);
        var y = Math.Clamp(rect.Top, work.Top, work.Bottom - height);
        Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }
}
