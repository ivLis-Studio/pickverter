using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pickverter.Interop;

namespace Pickverter.Input;

/// What File Explorer (and the desktop) shows and has selected.
internal static class Explorer
{
    private static readonly HashSet<string> FrameClasses = new(StringComparer.Ordinal) { "CabinetWClass", "ExploreWClass" };
    private static readonly HashSet<string> DesktopClasses = new(StringComparer.Ordinal) { "Progman", "WorkerW" };

    /// Windows whose keyboard focus means "the file list or the folder tree", not a text box.
    private static readonly HashSet<string> BrowsingFocus = new(StringComparer.Ordinal)
    {
        "DirectUIHWND", "SysListView32", "SysTreeView32", "SHELLDLL_DefView", "CabinetWClass", "ShellTabWindowClass",
        "Progman", "WorkerW", "DUIViewWndClassName",
    };

    public static bool IsExplorerFrame(IntPtr root) => FrameClasses.Contains(Native.ClassName(root));

    public static bool IsDesktop(IntPtr root) => DesktopClasses.Contains(Native.ClassName(root)) && HasDesktopView(root);

    private static bool HasDesktopView(IntPtr root) =>
        Native.FindWindowEx(root, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero;

    /// The foreground window when it is a File Explorer window or the desktop.
    public static IntPtr ForegroundExplorer()
    {
        var foreground = Native.GetForegroundWindow();
        if (foreground == IntPtr.Zero) return IntPtr.Zero;
        var root = Native.GetAncestor(foreground, Native.GA_ROOT);
        if (root == IntPtr.Zero) root = foreground;
        return IsExplorerFrame(root) || IsDesktop(root) ? root : IntPtr.Zero;
    }

    /// Renaming a file, typing in the address bar or the search box: the key belongs to the text.
    public static bool IsEditingText(IntPtr window)
    {
        var thread = Native.GetWindowThreadProcessId(window, out _);
        var info = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf<Native.GUITHREADINFO>() };
        if (!Native.GetGUIThreadInfo(thread, ref info)) return false;
        if ((info.flags & (Native.GUI_INMENUMODE | Native.GUI_POPUPMENUMODE)) != 0) return true;
        if (info.hwndFocus == IntPtr.Zero) return false;
        return !BrowsingFocus.Contains(Native.ClassName(info.hwndFocus));
    }

    /// The window under a screen point is a file list (Explorer's item view, the desktop or the folder tree).
    public static IntPtr FileViewAt(int x, int y)
    {
        var hwnd = Native.WindowFromPoint(new Native.POINT(x, y));
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;
        var cls = Native.ClassName(hwnd);
        if (cls is not ("DirectUIHWND" or "SysListView32" or "SysTreeView32")) return IntPtr.Zero;
        var root = Native.GetAncestor(hwnd, Native.GA_ROOT);
        return IsExplorerFrame(root) || IsDesktop(root) ? root : IntPtr.Zero;
    }

    /// Selected items of a File Explorer window (its active tab) or the desktop, in view order.
    public static Task<IReadOnlyList<string>> SelectionAsync(IntPtr root) =>
        StaWorker.Run<IReadOnlyList<string>>("explorer", () => Selection(root), TimeSpan.FromSeconds(1.5), []);

    private static IReadOnlyList<string> Selection(IntPtr root)
    {
        var type = Type.GetTypeFromCLSID(ShellIds.CLSID_ShellWindows);
        if (type == null || Activator.CreateInstance(type) is not IShellWindows windows) return [];
        try
        {
            if (IsDesktop(root)) return DesktopSelection(windows);
            var activeTab = Native.FindWindowEx(root, IntPtr.Zero, "ShellTabWindowClass", null);
            var count = windows.Count;
            for (var index = 0; index < count; index++)
            {
                object? item = null;
                try
                {
                    item = windows.Item(index);
                    if (BrowserOf(item) is not { } browser) continue;
                    if (browser.GetWindow(out var hwnd) != 0) continue;
                    var top = Native.GetAncestor(hwnd, Native.GA_ROOT);
                    if (top != root) continue;
                    // Windows 11 tabs: every tab is its own entry; the first ShellTabWindowClass child is the visible one.
                    if (hwnd != root && activeTab != IntPtr.Zero && hwnd != activeTab) continue;
                    return ItemsOf(browser);
                }
                catch (COMException)
                {
                }
                finally
                {
                    if (item != null && Marshal.IsComObject(item)) Marshal.ReleaseComObject(item);
                }
            }
            return [];
        }
        finally
        {
            Marshal.ReleaseComObject(windows);
        }
    }

    private static IReadOnlyList<string> DesktopSelection(IShellWindows windows)
    {
        object location = 0;
        object root = null!;
        var dispatch = windows.FindWindowSW(ref location, ref root, ShellIds.SWC_DESKTOP, out _, ShellIds.SWFO_NEEDDISPATCH);
        try
        {
            return BrowserOf(dispatch) is { } browser ? ItemsOf(browser) : [];
        }
        finally
        {
            if (dispatch != null && Marshal.IsComObject(dispatch)) Marshal.ReleaseComObject(dispatch);
        }
    }

    /// What each step of reading the selection sees, for "cli inspect" and the end-to-end check.
    public static Task<string> DiagnoseAsync(IntPtr root) =>
        StaWorker.Run("explorer diagnostics", () => Diagnose(root), TimeSpan.FromSeconds(10), "timed out");

    private static string Diagnose(IntPtr root)
    {
        var activeTab = Native.FindWindowEx(root, IntPtr.Zero, "ShellTabWindowClass", null);
        var lines = new List<string> { $"window {root:X} {Native.ClassName(root)} · desktop {IsDesktop(root)} · first tab {activeTab:X}" };
        var type = Type.GetTypeFromCLSID(ShellIds.CLSID_ShellWindows);
        if (type == null || Activator.CreateInstance(type) is not IShellWindows windows) return lines[0] + "\nno ShellWindows";
        try
        {
            var count = windows.Count;
            lines.Add($"shell windows: {count}");
            for (var index = 0; index < count; index++)
            {
                object? item = null;
                try
                {
                    item = windows.Item(index);
                    if (BrowserOf(item) is not { } browser)
                    {
                        lines.Add($"  {index}: no IShellBrowser");
                        continue;
                    }
                    var line = $"  {index}:";
                    var result = browser.GetWindow(out var hwnd);
                    line += $" window {hwnd:X} {Native.ClassName(hwnd)} (0x{result:X8}) root {Native.GetAncestor(hwnd, Native.GA_ROOT):X}";
                    result = browser.QueryActiveShellView(out var view);
                    line += $" · view 0x{result:X8}";
                    if (view is IFolderView folder)
                    {
                        var counted = folder.ItemCount(ShellIds.SVGIO_SELECTION, out var selected);
                        line += $" · selected {selected} (0x{counted:X8})";
                        var iid = ShellIds.IID_IShellItemArray;
                        result = folder.Items(ShellIds.SVGIO_SELECTION | ShellIds.SVGIO_FLAG_VIEWORDER, ref iid, out var items);
                        line += $" · items 0x{result:X8} {(items is IShellItemArray ? "IShellItemArray" : items?.GetType().Name ?? "null")}";
                        line += " · " + string.Join(", ", ItemsOf(browser).Select(Path.GetFileName));
                    }
                    else
                    {
                        line += " · no IFolderView";
                    }
                    lines.Add(line);
                }
                catch (Exception error)
                {
                    lines.Add($"  {index}: {error.GetType().Name} 0x{error.HResult:X8} {error.Message}");
                }
                finally
                {
                    if (item != null && Marshal.IsComObject(item)) Marshal.ReleaseComObject(item);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(windows);
        }
        return string.Join("\n", lines);
    }

    private static IShellBrowser? BrowserOf(object? item)
    {
        if (item is not IComServiceProvider provider) return null;
        var service = ShellIds.SID_STopLevelBrowser;
        var iid = ShellIds.IID_IShellBrowser;
        return provider.QueryService(ref service, ref iid, out var result) == 0 ? result as IShellBrowser : null;
    }

    private static IReadOnlyList<string> ItemsOf(IShellBrowser browser)
    {
        if (browser.QueryActiveShellView(out var view) != 0 || view is not IFolderView folder) return [];
        var iid = ShellIds.IID_IShellItemArray;
        if (folder.Items(ShellIds.SVGIO_SELECTION | ShellIds.SVGIO_FLAG_VIEWORDER, ref iid, out var result) != 0 || result is not IShellItemArray array)
            return [];
        var paths = new List<string>();
        if (array.GetCount(out var count) != 0) return paths;
        for (uint index = 0; index < count; index++)
        {
            if (array.GetItemAt(index, out var item) != 0 || item == null) continue;
            if (item.GetDisplayName(ShellIds.SIGDN_FILESYSPATH, out var name) == 0 && name != IntPtr.Zero)
            {
                var path = Marshal.PtrToStringUni(name);
                Marshal.FreeCoTaskMem(name);
                if (!string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path))) paths.Add(path);
            }
            Marshal.ReleaseComObject(item);
        }
        return paths;
    }

    // Thumbnails

    /// Explorer's own thumbnail (or icon) for a file, frozen so any thread can draw it.
    public static Task<BitmapSource?> ThumbnailAsync(string path, int size, bool iconOnly = false) =>
        StaWorker.Run("thumbnails", () => Thumbnail(path, size, iconOnly), TimeSpan.FromSeconds(4), null);

    public static BitmapSource? Thumbnail(string path, int size, bool iconOnly = false)
    {
        try
        {
            var factory = (IShellItemImageFactory)Native.SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID);
            try
            {
                var flags = iconOnly ? ShellIds.SIIGBF_ICONONLY : ShellIds.SIIGBF_BIGGERSIZEOK;
                if (factory.GetImage(new NativeSize(size, size), flags, out var bitmap) != 0 || bitmap == IntPtr.Zero) return null;
                try
                {
                    return FromHBitmap(bitmap);
                }
                finally
                {
                    Native.DeleteObject(bitmap);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        catch (Exception error) when (error is COMException or ArgumentException or InvalidCastException or FileNotFoundException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// 32-bit DIB -> BitmapSource. Shell thumbnails carry premultiplied alpha, except photos, whose alpha is empty.
    internal static BitmapSource? FromHBitmap(IntPtr bitmap)
    {
        if (Native.GetObject(bitmap, Marshal.SizeOf<Native.BITMAP>(), out var info) == 0) return null;
        int width = info.bmWidth, height = Math.Abs(info.bmHeight);
        if (width <= 0 || height <= 0) return null;
        var header = new Native.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32,
        };
        var pixels = new byte[width * height * 4];
        var dc = Native.GetDC(IntPtr.Zero);
        try
        {
            if (Native.GetDIBits(dc, bitmap, 0, (uint)height, pixels, ref header, 0) == 0) return null;
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, dc);
        }
        var hasAlpha = false;
        for (var index = 3; index < pixels.Length; index += 4)
        {
            if (pixels[index] != 0)
            {
                hasAlpha = true;
                break;
            }
        }
        var format = hasAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgr32;
        var source = BitmapSource.Create(width, height, 96, 96, format, null, pixels, width * 4);
        source.Freeze();
        return source;
    }
}
