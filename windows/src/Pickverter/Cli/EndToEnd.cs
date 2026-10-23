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

    /// Result cards that overlap the dialog, and how many of them sit above it (covering its buttons).
    private static (int Overlapping, int Above) CardsOver(System.Windows.Window dialog)
    {
        var target = new System.Windows.Interop.WindowInteropHelper(dialog).Handle;
        Native.GetWindowRect(target, out var area);
        var cards = System.Windows.Application.Current.Windows.OfType<CardWindow>().Where(card => card.IsVisible)
            .Select(card => new System.Windows.Interop.WindowInteropHelper(card).Handle).ToList();
        bool Overlaps(IntPtr card)
        {
            Native.GetWindowRect(card, out var rect);
            return rect.Left < area.Right && area.Left < rect.Right && rect.Top < area.Bottom && area.Top < rect.Bottom;
        }
        var overlapping = cards.Where(Overlaps).ToHashSet();
        var above = 0;
        for (var window = Native.GetWindow(target, Native.GW_HWNDPREV); window != IntPtr.Zero; window = Native.GetWindow(window, Native.GW_HWNDPREV))
        {
            if (overlapping.Contains(window)) above++;
        }
        return (overlapping.Count, above);
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

        async Task<(IntPtr Window, IReadOnlyList<string> Selection, long Ms)> SelectInExplorer(string file)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + file + "\"") { UseShellExecute = true });
            var found = IntPtr.Zero;
            IReadOnlyList<string> selected = [];
            var clock = Stopwatch.StartNew();
            var scripted = false;
            while (selected.Count == 0 && clock.Elapsed < TimeSpan.FromSeconds(20))
            {
                await Task.Delay(250);
                // A fresh folder can open before /select applies (seen on Windows Server 2025): select the file the way a
                // script would. What's being tested is reading the selection, not Explorer's command line.
                if (!scripted && clock.Elapsed > TimeSpan.FromSeconds(4))
                {
                    scripted = true;
                    var done = await StaWorker.Run("e2e select", () => SelectWithAutomation(file), TimeSpan.FromSeconds(10), false);
                    Console.WriteLine($"  /select left nothing selected; selected through Explorer's automation: {done}");
                }
                foreach (var candidate in ExplorerWindows())
                {
                    var items = await Explorer.SelectionAsync(candidate);
                    if (items.Any(path => string.Equals(path, file, StringComparison.OrdinalIgnoreCase)))
                    {
                        found = candidate;
                        selected = items;
                        break;
                    }
                }
            }
            return (found, selected, clock.ElapsedMilliseconds);
        }

        var (window, selection, selectMs) = await SelectInExplorer(photo);
        Report(selection.Count > 0, $"File Explorer selection read over COM ({selection.Count} item, {selectMs} ms)");
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
        Tap(0x1B);
        await Task.Delay(300);

        // A PDF password through the real dialog: Tools ring, Password, type it twice, Enter.
        // (The command line answers editors from the environment; this one wants the real window.)
        Platform.Editors = new Pickverter.UI.Editors.EditorHost();
        var pdf = files.First(path => Path.GetFileName(path) == "report.pdf");
        var (pdfWindow, pdfSelection, _) = await SelectInExplorer(pdf);
        Report(pdfSelection.Count > 0, "File Explorer selects the PDF");
        if (pdfWindow != IntPtr.Zero)
        {
            Windowing.Bring(pdfWindow);
            await Task.Delay(600);
            Tap(0xA4);
            for (var wait = 0; wait < 30 && !WheelController.Shared.IsVisible; wait++) await Task.Delay(100);
            Tap(0x09);
            await Task.Delay(300);
            for (var step = 0; step < 24 && WheelController.Shared.Model.Active?.Option.Id != "tool.lock"; step++)
            {
                Tap(0x27);
                await Task.Delay(120);
            }
            Report(WheelController.Shared.Model.Active?.Option.Id == "tool.lock", "the Tools ring reaches Password");
            var before = Directory.GetFiles(Path.GetDirectoryName(pdf)!, "*.pdf").ToHashSet(StringComparer.OrdinalIgnoreCase);
            Tap(0x0D);
            Pickverter.UI.Editors.PasswordDialog? dialog = null;
            for (var wait = 0; wait < 50 && dialog == null; wait++)
            {
                await Task.Delay(100);
                dialog = System.Windows.Application.Current.Windows.OfType<Pickverter.UI.Editors.PasswordDialog>()
                    .FirstOrDefault(candidate => candidate.IsVisible);
            }
            Report(dialog != null, "Password opens its dialog");
            await Task.Delay(500);
            if (dialog != null)
            {
                // A job that finishes while the dialog is open (here: another PNG) must slide its card in under it,
                // and the card left over from the PNG step must not come back on top either.
                var convert = Catalog.Options([photo], WheelMode.Convert).First(option => option.Id == "convert.png");
                JobCenter.Shared.Run(convert, WheelMode.Convert, [photo], default);
                await Task.Delay(1500);
                var (overlapping, above) = CardsOver(dialog);
                Console.WriteLine($"  {overlapping} card(s) overlap the dialog, {above} above it");
                Report(above == 0, "result cards stay under the password dialog");
            }
            Snapshots.CaptureScreen(Path.Combine(shots, "6-password.png"));
            foreach (var key in new ushort[] { 0x50, 0x56, 0x31, 0x32, 0x33, 0x34, 0x09, 0x50, 0x56, 0x31, 0x32, 0x33, 0x34, 0x0D })
            {
                Tap(key);
                await Task.Delay(60);
            }
            string? locked = null;
            for (var wait = 0; wait < 100 && locked == null; wait++)
            {
                await Task.Delay(100);
                locked = Directory.GetFiles(Path.GetDirectoryName(pdf)!, "*.pdf").FirstOrDefault(path => !before.Contains(path));
            }
            Report(locked != null, "the locked PDF lands next to the original: " + (locked == null ? "none" : Path.GetFileName(locked)));
            if (locked != null)
            {
                await Task.Delay(300);
                var needsPassword = false;
                try
                {
                    using var open = PdfSharp.Pdf.IO.PdfReader.Open(locked, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
                }
                catch (Exception)
                {
                    needsPassword = true;
                }
                var opens = false;
                try
                {
                    using var open = PdfSharp.Pdf.IO.PdfReader.Open(locked, "pv1234", PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
                    opens = open.PageCount > 0;
                }
                catch (Exception error)
                {
                    Console.WriteLine("  open with the password: " + error.Message);
                }
                Report(needsPassword && opens, "it asks for the password and opens with it");
            }
            await Task.Delay(700);
            Snapshots.CaptureScreen(Path.Combine(shots, "7-locked.png"));
        }

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
