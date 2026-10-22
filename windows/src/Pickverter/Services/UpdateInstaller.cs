using System.Diagnostics;
using System.IO.Compression;
using System.Windows;
using Microsoft.Win32;

namespace Pickverter.Services;

/// Installs a verified update in place: the new build (staged in the cache) waits for this process to exit,
/// swaps the install folder (keeping the old one until the new one is complete), then starts itself.
internal sealed class UpdateInstaller : IUpdateInstaller
{
    public static string InstallFolder => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    public async Task<string> StageAsync(string zipPath, string version, CancellationToken token)
    {
        var folder = Path.Combine(Path.GetDirectoryName(zipPath)!, "app");
        FileSystem.TryDelete(folder);
        await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, folder), token);
        var exe = Directory.GetFiles(folder, "Pickverter.exe", SearchOption.AllDirectories).OrderBy(path => path.Length).FirstOrDefault()
            ?? throw new PickException(L("update.err.bundle"));
        var info = FileVersionInfo.GetVersionInfo(exe);
        var product = (info.ProductVersion ?? "").Split('+')[0];
        if (Versions.Compare(product, version) != 0)
        {
            Debug.Log($"update: staged build says {product}, expected {version}");
            throw new PickException(L("update.err.bundle"));
        }
        return exe;
    }

    public bool CanInstallInPlace
    {
        get
        {
            var parent = Path.GetDirectoryName(InstallFolder);
            return parent != null && FileSystem.CanWrite(InstallFolder) && FileSystem.CanWrite(parent);
        }
    }

    public void InstallAndRelaunch(string stagedExecutable)
    {
        // Started from its own folder: a process whose current directory is inside the install folder keeps it from moving.
        var info = new ProcessStartInfo(stagedExecutable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(stagedExecutable)! };
        info.ArgumentList.Add("--apply-update");
        info.ArgumentList.Add(InstallFolder);
        info.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        Process.Start(info);
        Application.Current?.Shutdown();
    }
}

/// Runs inside the staged copy of the new version ("Pickverter.exe --apply-update <folder> <pid>").
internal static class UpdateApplier
{
    public static int Apply(string[] args)
    {
        if (args.Length < 2) return 64;
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[0]));
        var source = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase)) return 64;
        if (int.TryParse(args[1], out var pid))
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                process.WaitForExit(30_000);
            }
            catch (ArgumentException)
            {
            }
        }
        var backup = target + ".old";
        FileSystem.TryDelete(backup);
        var moved = false;
        for (var attempt = 0; attempt < 20 && !moved; attempt++)
        {
            try
            {
                Directory.Move(target, backup);
                moved = true;
            }
            catch (IOException)
            {
                Thread.Sleep(500);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(500);
            }
        }
        try
        {
            CopyTree(source, target);
        }
        catch (Exception)
        {
            if (moved)
            {
                FileSystem.TryDelete(target);
                try
                {
                    Directory.Move(backup, target);
                }
                catch (Exception)
                {
                }
            }
            Start(Path.Combine(target, "Pickverter.exe"));
            return 1;
        }
        UpdateUninstallEntry(target);
        Start(Path.Combine(target, "Pickverter.exe"), "--updated");
        return 0;
    }

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Copy(file, destination, true);
                    break;
                }
                catch (IOException) when (attempt < 10)
                {
                    Thread.Sleep(300);
                }
            }
        }
    }

    private static void Start(string exe, string? argument = null)
    {
        try
        {
            var info = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
            if (argument != null) info.ArgumentList.Add(argument);
            Process.Start(info);
        }
        catch (Exception)
        {
        }
    }

    /// Keeps "Installed apps" in Settings showing the right version after an in-place update.
    private static void UpdateUninstallEntry(string target)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(Path.Combine(target, "Pickverter.exe")).ProductVersion?.Split('+')[0];
            if (version == null) return;
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + Installer.UninstallKey, true);
            key?.SetValue("DisplayVersion", version);
        }
        catch (Exception)
        {
        }
    }
}

internal static class Installer
{
    /// Inno Setup's uninstall key: the AppId in installer/Pickverter.iss plus "_is1".
    public const string UninstallKey = "{8C3C8B57-5D0F-4E8B-9B7A-6F1E1B5C2A10}_is1";

    /// The previous version, kept next to the install folder until the new one started fine.
    public static void CleanUpAfterUpdate() => _ = Task.Run(() =>
    {
        Thread.Sleep(3000);
        FileSystem.TryDelete(UpdateInstaller.InstallFolder + ".old");
    });
}
