using System.ComponentModel;
using System.Diagnostics;

namespace Pickverter;

/// Locates optional helpers: ffmpeg/ffprobe, LibreOffice, 7-Zip, Microsoft Edge, terminals and code editors.
public static class Tools
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, string?> Cache = new();
    private static HashSet<string>? encoders;

    public static string FFmpegHint => L("err.ffmpeg");

    /// PICKVERTER_NO_FFMPEG=1 pretends ffmpeg is missing (tests of the built-in media path).
    public static bool FFmpegDisabled => Environment.GetEnvironmentVariable("PICKVERTER_NO_FFMPEG") is { Length: > 0 };

    public static string? FFmpeg => FFmpegDisabled ? null : Find("ffmpeg");
    public static string? FFprobe => FFmpegDisabled ? null : Find("ffprobe");
    public static string? SevenZip => Find("7z");

    public static string? Tar
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return File.Exists("/usr/bin/tar") ? "/usr/bin/tar" : null;
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
            return File.Exists(path) ? path : null;
        }
    }

    public static string? LibreOffice
    {
        get
        {
            lock (Gate)
            {
                if (Cache.TryGetValue("#soffice", out var hit)) return hit;
            }
            string? found = null;
            foreach (var root in ProgramFolders())
            {
                var path = Path.Combine(root, "LibreOffice", "program", "soffice.exe");
                if (File.Exists(path))
                {
                    found = path;
                    break;
                }
            }
            found ??= AppPath("soffice.exe");
            if (found == null && OperatingSystem.IsMacOS() && File.Exists("/Applications/LibreOffice.app/Contents/MacOS/soffice"))
                found = "/Applications/LibreOffice.app/Contents/MacOS/soffice";
            lock (Gate) Cache["#soffice"] = found;
            return found;
        }
    }

    public static string? Edge
    {
        get
        {
            lock (Gate)
            {
                if (Cache.TryGetValue("#edge", out var hit)) return hit;
            }
            string? found = AppPath("msedge.exe");
            if (found == null)
            {
                foreach (var root in ProgramFolders())
                {
                    var path = Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe");
                    if (File.Exists(path))
                    {
                        found = path;
                        break;
                    }
                }
            }
            lock (Gate) Cache["#edge"] = found;
            return found;
        }
    }

    public static void Refresh()
    {
        lock (Gate)
        {
            Cache.Clear();
            encoders = null;
        }
    }

    public static string? Find(string name)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(name, out var hit)) return hit;
        }
        string? found = null;
        foreach (var candidate in Candidates(name))
        {
            try
            {
                if (File.Exists(candidate))
                {
                    found = candidate;
                    break;
                }
            }
            catch (Exception)
            {
            }
        }
        lock (Gate) Cache[name] = found;
        return found;
    }

    public static IEnumerable<string> ProgramFolders()
    {
        foreach (var variable in new[] { "ProgramW6432", "ProgramFiles", "ProgramFiles(x86)", "ProgramFiles(Arm)" })
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrEmpty(value)) yield return value;
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local)) yield return Path.Combine(local, "Programs");
    }

    /// PATH as the user has it now (a winget install changes it after Pickverter started).
    public static IEnumerable<string> SearchPath()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sources = new List<string?> { Environment.GetEnvironmentVariable("PATH") };
        if (OperatingSystem.IsWindows())
        {
            sources.Add(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));
            sources.Add(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));
        }
        foreach (var source in sources)
        {
            if (string.IsNullOrEmpty(source)) continue;
            foreach (var raw in source.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var dir = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
                if (dir.Length > 0 && seen.Add(dir)) yield return dir;
            }
        }
    }

    private static IEnumerable<string> Candidates(string name)
    {
        var exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
        var forced = Environment.GetEnvironmentVariable("PICKVERTER_TOOLS_DIR");
        if (!string.IsNullOrEmpty(forced)) yield return Path.Combine(forced, exe);
        yield return Path.Combine(AppContext.BaseDirectory, "tools", exe);
        yield return Path.Combine(Platform.Paths.Cache, "tools", exe);
        foreach (var dir in SearchPath()) yield return Path.Combine(dir, exe);
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            yield return Path.Combine(local, "Microsoft", "WinGet", "Links", exe);
            var packages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(packages))
            {
                foreach (var package in SafeDirectories(packages, "*FFmpeg*"))
                {
                    foreach (var build in SafeDirectories(package, "*"))
                    {
                        yield return Path.Combine(build, "bin", exe);
                        yield return Path.Combine(build, exe);
                    }
                }
            }
            yield return Path.Combine(home, "scoop", "shims", exe);
            yield return Path.Combine(Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData", "chocolatey", "bin", exe);
            yield return Path.Combine(@"C:\ffmpeg\bin", exe);
            foreach (var root in ProgramFolders())
            {
                yield return Path.Combine(root, "ffmpeg", "bin", exe);
                yield return Path.Combine(root, "7-Zip", exe);
            }
        }
        else
        {
            foreach (var dir in new[] { "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin" }) yield return Path.Combine(dir, exe);
        }
    }

    private static IEnumerable<string> SafeDirectories(string root, string pattern)
    {
        try
        {
            return Directory.GetDirectories(root, pattern);
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// HKLM/HKCU App Paths registration (how Windows itself finds msedge.exe, soffice.exe…).
    public static string? AppPath(string exe)
    {
        if (!OperatingSystem.IsWindows()) return null;
        foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
        {
            try
            {
                using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe);
                if (key?.GetValue(null) is string value)
                {
                    var path = Environment.ExpandEnvironmentVariables(value.Trim('"'));
                    if (File.Exists(path)) return path;
                }
            }
            catch (Exception)
            {
            }
        }
        return null;
    }

    public static HashSet<string> FFmpegEncoders()
    {
        lock (Gate)
        {
            if (encoders != null) return encoders;
        }
        var set = new HashSet<string>();
        var ffmpeg = FFmpeg;
        if (ffmpeg != null)
        {
            try
            {
                var info = new ProcessStartInfo(ffmpeg)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                info.ArgumentList.Add("-hide_banner");
                info.ArgumentList.Add("-encoders");
                using var process = Process.Start(info);
                if (process != null)
                {
                    var output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(10_000);
                    foreach (var line in output.Split('\n'))
                    {
                        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2 && parts[0].Length == 6) set.Add(parts[1]);
                    }
                }
            }
            catch (Exception)
            {
            }
        }
        lock (Gate) encoders = set;
        return set;
    }

    public static bool HasEncoder(string name) => FFmpegEncoders().Contains(name);
}

public sealed class ToolFailure(string tool, int status, string stderr) : Exception(Describe(tool, status, stderr))
{
    public string Tool { get; } = tool;
    public int Status { get; } = status;
    public string Stderr { get; } = stderr;

    private static string Describe(string tool, int status, string stderr)
    {
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tail = string.Join(" ", lines.TakeLast(2)).Trim();
        if (tail.Length == 0) return L("err.tool_failed", tool, status.ToString(CultureInfo.InvariantCulture));
        return tool + ": " + (tail.Length > 220 ? tail[..220] : tail);
    }
}

public static class Shell
{
    /// Runs a program without a shell (no quoting pitfalls). Cancelling kills the whole process tree.
    public static async Task<string> RunAsync(string executable, IEnumerable<string> arguments, string? cwd = null, string? stdoutTo = null,
                                              Action<string>? onLine = null, CancellationToken token = default)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = stdoutTo == null ? Encoding.UTF8 : null,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        if (cwd != null) info.WorkingDirectory = cwd;
        var tool = Path.GetFileNameWithoutExtension(executable);
        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
        }
        catch (Win32Exception error)
        {
            throw new PickException(L("err.run_tool", tool) + " · " + error.Message);
        }
        try { process.StandardInput.Close(); } catch (Exception) { }
        var output = new StringBuilder();
        var errors = new StringBuilder();
        Task outTask;
        if (stdoutTo != null)
        {
            outTask = Task.Run(async () =>
            {
                await using var file = new FileStream(stdoutTo, FileMode.Create, FileAccess.Write, FileShare.Read);
                await process.StandardOutput.BaseStream.CopyToAsync(file);
            });
        }
        else
        {
            outTask = Task.Run(async () =>
            {
                string? line;
                while ((line = await process.StandardOutput.ReadLineAsync()) != null)
                {
                    if (output.Length < 4_000_000) output.Append(line).Append('\n');
                    onLine?.Invoke(line);
                }
            });
        }
        var errTask = Task.Run(async () =>
        {
            var buffer = new char[4096];
            int read;
            while ((read = await process.StandardError.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                lock (errors)
                {
                    errors.Append(buffer, 0, read);
                    if (errors.Length > 16_000) errors.Remove(0, errors.Length - 8_000);
                }
            }
        });
        await using (token.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
            }
        }))
        {
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(outTask, errTask); } catch (Exception) { }
        }
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
        {
            string stderr;
            lock (errors) stderr = errors.ToString();
            throw new ToolFailure(tool, process.ExitCode, stderr);
        }
        return output.ToString();
    }
}

public enum TerminalKind { WindowsTerminal, PowerShell, WindowsPowerShell, CommandPrompt, GitBash }

public static class Terminals
{
    public static readonly TerminalKind[] All =
        [TerminalKind.WindowsTerminal, TerminalKind.PowerShell, TerminalKind.WindowsPowerShell, TerminalKind.CommandPrompt, TerminalKind.GitBash];

    public static string Raw(this TerminalKind kind) => kind switch
    {
        TerminalKind.WindowsTerminal => "wt",
        TerminalKind.PowerShell => "pwsh",
        TerminalKind.WindowsPowerShell => "powershell",
        TerminalKind.CommandPrompt => "cmd",
        _ => "gitbash",
    };

    public static TerminalKind? Parse(string? raw) => All.Cast<TerminalKind?>().FirstOrDefault(kind => kind!.Value.Raw() == raw);

    public static string Label(this TerminalKind kind) => kind switch
    {
        TerminalKind.WindowsTerminal => "Windows Terminal",
        TerminalKind.PowerShell => "PowerShell 7",
        TerminalKind.WindowsPowerShell => "Windows PowerShell",
        TerminalKind.CommandPrompt => L("term.cmd"),
        _ => "Git Bash",
    };

    public static string? Executable(this TerminalKind kind)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        IEnumerable<string> candidates = kind switch
        {
            TerminalKind.WindowsTerminal => [Path.Combine(local, "Microsoft", "WindowsApps", "wt.exe")],
            TerminalKind.PowerShell => Tools.ProgramFolders().Select(root => Path.Combine(root, "PowerShell", "7", "pwsh.exe"))
                .Concat(Tools.SearchPath().Select(dir => Path.Combine(dir, "pwsh.exe"))),
            TerminalKind.WindowsPowerShell => [Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe")],
            TerminalKind.CommandPrompt => [Path.Combine(system, "cmd.exe")],
            _ => Tools.ProgramFolders().Select(root => Path.Combine(root, "Git", "git-bash.exe")),
        };
        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception)
            {
            }
        }
        return null;
    }

    public static bool IsInstalled(this TerminalKind kind) => kind.Executable() != null;

    public static IReadOnlyList<TerminalKind> Installed => All.Where(IsInstalled).ToList();

    /// The preferred terminal, or the first installed one.
    public static TerminalKind Preferred
    {
        get
        {
            var chosen = Preferences.Shared.Terminal;
            if (chosen.IsInstalled() || !OperatingSystem.IsWindows()) return chosen;
            return Installed.FirstOrDefault(TerminalKind.CommandPrompt);
        }
    }

    public static string FolderFor(string path) => Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;

    public static void Open(TerminalKind kind, string folder)
    {
        var exe = kind.Executable() ?? throw new PickException(L("err.no_terminal"));
        var dir = folder.EndsWith('\\') ? folder + "." : folder;
        switch (kind)
        {
            case TerminalKind.WindowsTerminal:
                Launcher.StartRaw(exe, "-d \"" + dir.Replace(";", "\\;") + "\"", folder);
                break;
            case TerminalKind.PowerShell:
            case TerminalKind.WindowsPowerShell:
                Launcher.Start(exe, ["-NoExit", "-NoLogo"], folder, window: true);
                break;
            case TerminalKind.CommandPrompt:
                Launcher.Start(exe, ["/K"], folder, window: true);
                break;
            default:
                Launcher.Start(exe, ["--cd=" + folder], folder, window: true);
                break;
        }
    }
}

public sealed record EditorChoice(string Label, string Path);

public static class Editors
{
    private static IEnumerable<(string Label, string[] Paths)> Candidates()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programs = Tools.ProgramFolders().ToArray();
        string[] Under(params string[] parts) => programs.Select(root => Path.Combine([root, .. parts])).ToArray();
        yield return ("VS Code", [Path.Combine(local, "Programs", "Microsoft VS Code", "Code.exe"), .. Under("Microsoft VS Code", "Code.exe")]);
        yield return ("Cursor", [Path.Combine(local, "Programs", "cursor", "Cursor.exe")]);
        yield return ("Windsurf", [Path.Combine(local, "Programs", "Windsurf", "Windsurf.exe")]);
        yield return ("Zed", [Path.Combine(local, "Programs", "Zed", "Zed.exe"), .. Under("Zed", "Zed.exe")]);
        yield return ("Sublime Text", Under("Sublime Text", "sublime_text.exe"));
        yield return ("Notepad++", Under("Notepad++", "notepad++.exe"));
    }

    /// Installed code editors, best first; Notepad when none is installed.
    public static IReadOnlyList<EditorChoice> Installed
    {
        get
        {
            var found = new List<EditorChoice>();
            if (OperatingSystem.IsWindows())
            {
                foreach (var (label, paths) in Candidates())
                {
                    var path = paths.FirstOrDefault(File.Exists);
                    if (path != null) found.Add(new EditorChoice(label, path));
                }
            }
            if (found.Count == 0)
            {
                var notepad = OperatingSystem.IsWindows()
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe")
                    : "notepad.exe";
                found.Add(new EditorChoice(L("app.textedit"), notepad));
            }
            return found;
        }
    }

    public static void Open(IReadOnlyList<string> files, EditorChoice editor)
    {
        if (!File.Exists(editor.Path)) throw new PickException(L("err.no_app", editor.Label));
        Launcher.Start(editor.Path, files, Terminals.FolderFor(files[0]), window: true);
    }
}
