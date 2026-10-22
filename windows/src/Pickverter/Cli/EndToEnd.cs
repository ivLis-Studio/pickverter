using System.Diagnostics;
using System.Runtime.InteropServices;
using Pickverter.Input;
using Pickverter.Interop;
using Pickverter.UI;
using Pickverter.UI.Wheel;

namespace Pickverter.Cli;

/// Drives the real thing on a Windows desktop: File Explorer with a file selected, a synthetic Alt tap, the wheel,
/// arrow + Enter, the conversion and its card. Screenshots of each step go into the folder.
internal static class EndToEnd
{
    private static void Keys(params (ushort Vk, bool Up)[] keys)
    {
        var inputs = keys.Select(key => new Native.INPUT
        {
            type = Native.INPUT_KEYBOARD,
            U = new Native.InputUnion
            {
                ki = new Native.KEYBDINPUT
                {
                    wVk = key.Vk,
                    wScan = (ushort)Native.MapVirtualKey(key.Vk, 0),
                    dwFlags = key.Up ? Native.KEYEVENTF_KEYUP : 0,
                },
            },
        }).ToArray();
        Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());
    }

    private static void Tap(ushort vk) => Keys((vk, false), (vk, true));

    private static IEnumerable<IntPtr> ExplorerWindows()
    {
        var found = new List<IntPtr>();
        var after = IntPtr.Zero;
        while ((after = Native.FindWindowEx(IntPtr.Zero, after, "CabinetWClass", null)) != IntPtr.Zero) found.Add(after);
        return found;
    }

    public static async Task<int> Run(string folder)
    {
        folder = Path.GetFullPath(folder);
        var shots = Path.Combine(folder, "shots");
        Directory.CreateDirectory(shots);
        var files = Fixtures.Make(Path.Combine(folder, "files"), media: false);
        var photo = files.First(path => Path.GetFileName(path) == "photo.jpg");
        var failures = 0;
        void Report(bool ok, string what)
        {
            Console.WriteLine((ok ? "ok    " : "FAIL  ") + what);
            if (!ok) failures++;
        }

        JobCenter.Shared.Presenter = new CardPresenter();
        var tray = new TrayIcon();
        Report(tray.Bounds != null, "tray icon in the notification area");
        InputMonitor.Shared.Start();
        Report(InputMonitor.Shared.IsRunning, "keyboard and mouse hooks");

        Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + photo + "\"") { UseShellExecute = true });
        IntPtr window = IntPtr.Zero;
        IReadOnlyList<string> selection = [];
        var watch = Stopwatch.StartNew();
        var scripted = false;
        while (selection.Count == 0 && watch.Elapsed < TimeSpan.FromSeconds(20))
        {
            await Task.Delay(250);
            // A fresh folder can open before /select applies (seen on Windows Server 2025): select the file the way a
            // script would. What's being tested is reading the selection, not Explorer's command line.
            if (!scripted && watch.Elapsed > TimeSpan.FromSeconds(4))
            {
                scripted = true;
                var done = await StaWorker.Run("e2e select", () => SelectWithAutomation(photo), TimeSpan.FromSeconds(10), false);
                Console.WriteLine($"  /select left nothing selected; selected through Explorer's automation: {done}");
            }
            foreach (var candidate in ExplorerWindows())
            {
                var selected = await Explorer.SelectionAsync(candidate);
                if (selected.Any(path => string.Equals(path, photo, StringComparison.OrdinalIgnoreCase)))
                {
                    window = candidate;
                    selection = selected;
                    break;
                }
            }
        }
        Report(selection.Count > 0, $"File Explorer selection read over COM ({selection.Count} item, {watch.ElapsedMilliseconds} ms)");
        if (window == IntPtr.Zero)
        {
            var frames = ExplorerWindows().ToList();
            Console.WriteLine($"  File Explorer windows: {frames.Count}");
            foreach (var frame in frames) Console.WriteLine(await Explorer.DiagnoseAsync(frame));
            Snapshots.CaptureScreen(Path.Combine(shots, "0-no-selection.png"));
            tray.Dispose();
            return 1;
        }
        Windowing.Bring(window);
        await Task.Delay(600);
        Report(Explorer.ForegroundExplorer() == window, "File Explorer in front");
        Report(!Explorer.IsEditingText(window), "focus is in the file list");
        Snapshots.CaptureScreen(Path.Combine(shots, "1-explorer.png"));

        Tap(0xA4);
        for (var wait = 0; wait < 30 && !WheelController.Shared.IsVisible; wait++) await Task.Delay(100);
        Report(WheelController.Shared.IsVisible, "tapping Alt opens the wheel");
        await Task.Delay(400);
        var thread = Native.GetWindowThreadProcessId(window, out _);
        var info = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf<Native.GUITHREADINFO>() };
        Native.GetGUIThreadInfo(thread, ref info);
        Report((info.flags & Native.GUI_INMENUMODE) == 0, "File Explorer's menu stayed closed");
        Snapshots.CaptureScreen(Path.Combine(shots, "2-wheel.png"));

        Tap(0x27);
        await Task.Delay(300);
        Snapshots.CaptureScreen(Path.Combine(shots, "3-highlight.png"));
        var target = WheelController.Shared.Model.Active?.Option;
        Report(target != null, "arrow key highlights a slot (" + (target?.Title ?? "none") + ")");
        Tap(0x0D);
        var expected = target?.OutputExt is { } ext ? Path.Combine(Path.GetDirectoryName(photo)!, FileNames.Base(photo) + "." + ext) : null;
        for (var wait = 0; wait < 60 && (expected == null || !File.Exists(expected)); wait++) await Task.Delay(100);
        Report(!WheelController.Shared.IsVisible, "Enter runs it and closes the wheel");
        Report(expected != null && File.Exists(expected), "result next to the original: " + (expected == null ? "?" : Path.GetFileName(expected)));
        await Task.Delay(900);
        Snapshots.CaptureScreen(Path.Combine(shots, "4-card.png"));

        // Esc closes without running anything.
        Windowing.Bring(window);
        await Task.Delay(400);
        Tap(0xA4);
        for (var wait = 0; wait < 30 && !WheelController.Shared.IsVisible; wait++) await Task.Delay(100);
        Tap(0x1B);
        await Task.Delay(400);
        Report(!WheelController.Shared.IsVisible, "Esc closes the wheel");

        // Tab switches rings, Ctrl+C sends one result to the clipboard.
        Tap(0xA4);
        for (var wait = 0; wait < 30 && !WheelController.Shared.IsVisible; wait++) await Task.Delay(100);
        Tap(0x09);
        await Task.Delay(300);
        Report(WheelController.Shared.Model.Mode == WheelMode.Tools, "Tab switches to the Tools ring");
        Tap(0x09);
        Tap(0x09);
        await Task.Delay(300);
        Report(WheelController.Shared.Model.Mode == WheelMode.Convert, "Tab cycles back to Convert");
        Tap(0x27);
        await Task.Delay(200);
        Keys((0xA2, false), (0x43, false), (0x43, true), (0xA2, true));
        await Task.Delay(1500);
        var formats = await Platform.Clipboard.DescribeAsync();
        Report(formats.Contains("FileDrop"), "Ctrl+C puts the result on the clipboard (" + formats + ")");
        Snapshots.CaptureScreen(Path.Combine(shots, "5-clipboard.png"));

        InputMonitor.Shared.Stop();
        tray.Dispose();
        foreach (var explorer in ExplorerWindows()) Native.PostMessage(explorer, 0x0010, IntPtr.Zero, IntPtr.Zero);
        Console.WriteLine(failures == 0 ? "end-to-end passed" : $"{failures} end-to-end check(s) failed");
        return failures == 0 ? 0 : 1;
    }

    /// Selects a file in the File Explorer window showing its folder, through Explorer's scripting objects.
    private static bool SelectWithAutomation(string path)
    {
        var type = Type.GetTypeFromCLSID(ShellIds.CLSID_ShellWindows);
        if (type == null || Activator.CreateInstance(type) is not IShellWindows windows) return false;
        var folder = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(path)!);
        try
        {
            var count = windows.Count;
            for (var index = 0; index < count; index++)
            {
                try
                {
                    dynamic? item = windows.Item(index);
                    if (item == null) continue;
                    var document = item.Document;
                    string location = document.Folder.Self.Path;
                    if (!string.Equals(Path.TrimEndingDirectorySeparator(location), folder, StringComparison.OrdinalIgnoreCase)) continue;
                    var target = document.Folder.ParseName(Path.GetFileName(path));
                    if (target == null) continue;
                    // SVSI_SELECT | SVSI_DESELECTOTHERS | SVSI_ENSUREVISIBLE | SVSI_FOCUSED
                    document.SelectItem(target, 1 | 4 | 8 | 16);
                    return true;
                }
                catch (Exception error)
                {
                    Console.WriteLine("  automation: " + error.Message);
                }
            }
            return false;
        }
        finally
        {
            Marshal.ReleaseComObject(windows);
        }
    }
}
