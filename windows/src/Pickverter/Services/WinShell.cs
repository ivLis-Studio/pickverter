using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Pickverter.Interop;

namespace Pickverter.Services;

internal sealed class WinShell : DefaultShell
{
    private string? downloads;

    public override string DownloadsFolder => downloads ??= Native.KnownFolder(Native.FOLDERID_Downloads) is { Length: > 0 } path
        ? path
        : base.DownloadsFolder;

    public override string? PerceivedType(string ext)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey("." + ext.TrimStart('.'));
            return key?.GetValue("PerceivedType") as string is { } type && type is "image" or "video" or "audio" ? type : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// Opens one File Explorer window per folder with the items selected.
    public override void Reveal(IReadOnlyList<string> paths)
    {
        var existing = paths.Where(path => File.Exists(path) || Directory.Exists(path)).ToList();
        if (existing.Count == 0) return;
        Native.AllowSetForegroundWindow(Native.ASFW_ANY);
        _ = StaWorker.Run("reveal", () =>
        {
            foreach (var group in existing.GroupBy(path => Path.GetDirectoryName(path) ?? path, StringComparer.OrdinalIgnoreCase))
                Select(group.Key, group.ToList());
            return true;
        }, TimeSpan.FromSeconds(10), false);
    }

    private static void Select(string folder, IReadOnlyList<string> items)
    {
        if (Native.SHParseDisplayName(folder, IntPtr.Zero, out var parent, 0, out _) != 0)
        {
            Launcher.StartRaw("explorer.exe", "/select,\"" + items[0] + "\"");
            return;
        }
        var children = new List<IntPtr>();
        try
        {
            foreach (var item in items)
            {
                if (Native.SHParseDisplayName(item, IntPtr.Zero, out var child, 0, out _) == 0) children.Add(child);
            }
            var result = Native.SHOpenFolderAndSelectItems(parent, (uint)children.Count, children.ToArray(), 0);
            if (result != 0) Launcher.StartRaw("explorer.exe", "/select,\"" + items[0] + "\"");
        }
        finally
        {
            foreach (var child in children) Native.ILFree(child);
            Native.ILFree(parent);
        }
    }

    public override void Open(string path)
    {
        try
        {
            Native.AllowSetForegroundWindow(Native.ASFW_ANY);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            Debug.Log("open: " + error.Message);
            Platform.Feedback.Beep();
        }
    }

    public override void OpenUrl(string url) => Open(url);

    /// Into the Recycle Bin (the user can restore it there), on a shell thread.
    public override Task RecycleAsync(IReadOnlyList<string> paths)
    {
        var existing = paths.Where(path => File.Exists(path) || Directory.Exists(path)).Select(Path.GetFullPath).ToList();
        if (existing.Count == 0) return Task.CompletedTask;
        return StaWorker.For("recycle").Run(() =>
        {
            var operation = new Native.SHFILEOPSTRUCT
            {
                wFunc = Native.FO_DELETE,
                pFrom = string.Join("\0", existing) + "\0\0",
                fFlags = (ushort)(Native.FOF_ALLOWUNDO | Native.FOF_NOCONFIRMATION | Native.FOF_SILENT | Native.FOF_NOERRORUI),
            };
            var result = Native.SHFileOperation(ref operation);
            if (result != 0)
            {
                Debug.Log($"recycle failed ({result}); deleting");
                foreach (var path in existing) FileSystem.TryDelete(path);
            }
            return true;
        });
    }

    private const string ExploitGuard = @"SOFTWARE\Microsoft\Windows Defender\Windows Defender Exploit Guard\Controlled Folder Access";
    private const string ExploitGuardPolicy = @"SOFTWARE\Policies\Microsoft\Windows Defender\Windows Defender Exploit Guard\Controlled Folder Access";

    /// Windows Security's ransomware protection (it blocks unknown apps from writing to Documents, Pictures, Desktop…).
    public override bool ControlledFolderAccess => FolderProtection.State == FolderProtectionState.Blocking;

    public override void OpenControlledFolderSettings() => FolderProtection.OpenSettings();

    public override string AccentHex
    {
        get
        {
            var color = UI.Theme.SystemAccent;
            return $"0x{color.R:X2}{color.G:X2}{color.B:X2}";
        }
    }

    /// File Explorer watches local folders itself; network shares and some views only refresh when told.
    public override void NotifyFolderChanged(string folder)
    {
        try
        {
            Native.SHChangeNotify(Native.SHCNE_UPDATEDIR, Native.SHCNF_PATHW | Native.SHCNF_FLUSHNOWAIT, folder, null);
        }
        catch (Exception error)
        {
            Debug.Log("change notify: " + error.Message);
        }
    }

    internal static int? ReadExploitGuard(string name)
    {
        foreach (var path in new[] { ExploitGuardPolicy, ExploitGuard })
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(path);
                if (key?.GetValue(name) is int value) return value;
            }
            catch (Exception)
            {
            }
        }
        return null;
    }

    internal static bool IsAllowedApp(string exe)
    {
        foreach (var path in new[] { ExploitGuardPolicy + @"\AllowedApplications", ExploitGuard + @"\AllowedApplications" })
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(path);
                if (key?.GetValueNames().Any(name => string.Equals(name, exe, StringComparison.OrdinalIgnoreCase)) == true) return true;
            }
            catch (Exception)
            {
            }
        }
        return false;
    }
}

public enum FolderProtectionState { Off, Allowed, Blocking, Unknown }

/// Controlled folder access: whether it is on, and whether Pickverter is on its allow list.
public static class FolderProtection
{
    public static FolderProtectionState State
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return FolderProtectionState.Off;
            var mode = WinShell.ReadExploitGuard("EnableControlledFolderAccess");
            if (mode is null or 0) return FolderProtectionState.Off;
            // 2 = audit mode (logs only), 3/4 = block disk modification only.
            if (mode == 2) return FolderProtectionState.Off;
            var exe = Environment.ProcessPath ?? "";
            return WinShell.IsAllowedApp(exe) ? FolderProtectionState.Allowed : FolderProtectionState.Blocking;
        }
    }

    public static void OpenSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("windowsdefender://ransomwareprotection/") { UseShellExecute = true });
        }
        catch (Exception)
        {
            Platform.Shell.OpenUrl("ms-settings:windowsdefender");
        }
    }

    /// Adds Pickverter to the allow list (Windows asks for administrator approval). Returns false when declined.
    public static async Task<bool> AllowAsync()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;
        var command = "Add-MpPreference -ControlledFolderAccessAllowedApplications '" + exe.Replace("'", "''") + "'";
        var info = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"" + command + "\"",
        };
        try
        {
            using var process = Process.Start(info);
            if (process == null) return false;
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
