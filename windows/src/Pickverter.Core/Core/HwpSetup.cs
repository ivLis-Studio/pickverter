using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Pickverter;

/// Downloads rhwp (github.com/edwardkim/rhwp, MIT), the open-source HWP engine, into %LOCALAPPDATA%\Pickverter\rhwp the
/// first time an HWP or HWPX file is converted.
///
/// The release is pinned: Pickverter knows the version and the SHA-256 of each archive, so a download is used only when
/// it is exactly the file this build was tested with. A newer Pickverter can move the pin; the old copy is then replaced
/// on its next use. Windows on Arm runs the x64 build through Windows 11's emulation. The macOS archives only serve the
/// core's tests on a Mac.
public static class HwpSetup
{
    public const string Version = "0.8.7";
    public const string Releases = "https://github.com/edwardkim/rhwp/releases/download/v" + Version + "/";

    public enum SetupPhase { Idle, Downloading, Installing, Failed }

    /// A release archive and its SHA-256 (from the release's SHA256SUMS.txt).
    public sealed record Asset(string Name, string Sha256)
    {
        public string Url => Releases + Name;
    }

    public static readonly Asset WindowsX64 = new("rhwp-v0.8.7-windows-x86_64.zip", "f7ac326e98f595ae8fecd8da7a614ee9a37db77bdf938782a8e07c9da473ec6f");
    public static readonly Asset MacArm64 = new("rhwp-v0.8.7-macos-aarch64.tar.gz", "6ed46aa63f6d10c447842999f73abc59cf9586c5645fefc5e763bd6a6d43662d");
    public static readonly Asset MacX64 = new("rhwp-v0.8.7-macos-x86_64.tar.gz", "07b1a5ea570e410d1b52b4d848a94f00c64674e84004883c62efad7daab61da4");

    private static readonly object Gate = new();
    private static Task? current;
    private static CancellationTokenSource? cancellation;
    private static readonly List<Action<double>> Listeners = [];
    private static int lastPercent = -1;

    public static SetupPhase Phase { get; private set; }
    public static double Progress { get; private set; }
    public static string? Failure { get; private set; }
    public static bool IsWorking => Phase is SetupPhase.Downloading or SetupPhase.Installing;

    /// Raised on the UI thread whenever Phase or Progress changes.
    public static event Action? Changed;

    /// Where the download goes (PICKVERTER_RHWP_DIR overrides it for tests and CI).
    public static string Folder => Environment.GetEnvironmentVariable("PICKVERTER_RHWP_DIR") is { Length: > 0 } custom
        ? custom
        : Path.Combine(Platform.Paths.Cache, "rhwp");

    /// "rhwp.exe" on Windows, "rhwp" on a Mac.
    public static string BinaryName => OperatingSystem.IsWindows() ? "rhwp.exe" : "rhwp";

    public static string? InstalledVersion
    {
        get
        {
            try
            {
                var text = File.ReadAllText(Path.Combine(Folder, "version.txt")).Trim();
                return text.Length > 0 ? text : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// The rhwp to run: the pinned version Pickverter downloaded (PICKVERTER_RHWP names another binary, for tests).
    public static string? Executable
    {
        get
        {
            if (Environment.GetEnvironmentVariable("PICKVERTER_RHWP") is { Length: > 0 } custom) return File.Exists(custom) ? custom : null;
            var binary = Path.Combine(Folder, BinaryName);
            return InstalledVersion == Version && File.Exists(binary) ? binary : null;
        }
    }

    /// The rhwp in use is the one Pickverter downloaded.
    public static bool IsManaged =>
        Executable is { } path && path.StartsWith(Folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// The release archive for this computer (null where rhwp publishes none that Pickverter uses).
    public static Asset? ArchiveFor(bool windows, bool mac, Architecture architecture)
    {
        if (windows) return architecture is Architecture.X64 or Architecture.Arm64 ? WindowsX64 : null;
        if (mac) return architecture == Architecture.Arm64 ? MacArm64 : architecture == Architecture.X64 ? MacX64 : null;
        return null;
    }

    public static Asset? Archive => ArchiveFor(OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(), RuntimeInformation.OSArchitecture);

    /// Windows on Arm runs x64 programs only from Windows 11 (build 22000) on; Windows 10 on Arm can't run rhwp.
    public static bool Runs(bool windows, Architecture architecture, int build) => !(windows && architecture == Architecture.Arm64 && build < 22000);

    public static bool RunsHere => Runs(OperatingSystem.IsWindows(), RuntimeInformation.OSArchitecture, Environment.OSVersion.Version.Build);

    /// Why HWP options can't run yet: null when rhwp is ready, the download offer when it isn't (the job downloads it
    /// first), and a plain explanation where it can't run at all.
    public static string? Unavailable()
    {
        if (Executable != null) return null;
        return RunsHere ? Tools.HwpHint : L("err.hwp_arm");
    }

    /// One line for Settings while something is happening.
    public static string? StatusLine => Phase switch
    {
        SetupPhase.Downloading => L("hwp.downloading", ((int)(Progress * 100)).ToString(CultureInfo.InvariantCulture)),
        SetupPhase.Installing => L("hwp.installing"),
        SetupPhase.Failed => Failure,
        _ => null,
    };

    /// The job failed only because rhwp is missing (a batch prefixes the message with a file name).
    public static bool IsMissing(Exception error) => Executable == null && error.Message.Contains(Tools.HwpHint, StringComparison.Ordinal);

    /// The same option, but it downloads rhwp first when it's missing (progress on the job's own card).
    public static PickOption Prepare(PickOption option) => option with
    {
        Unavailable = null,
        Perform = async ctx =>
        {
            if (Executable != null) return await option.Perform(ctx);
            var download = ctx.Portion(0, 0.4);
            await InstallAsync(fraction => download.Report(fraction,
                L("hwp.downloading", ((int)(fraction * 100)).ToString(CultureInfo.InvariantCulture))), ctx.Token);
            ctx.Report(0.4, "");
            return await option.Perform(ctx.Portion(0.4, 1));
        },
    };

    /// For buttons: starts an install and reports through Phase and Changed.
    public static void Start()
    {
        if (IsWorking) return;
        _ = InstallAsync().ContinueWith(task => _ = task.Exception, TaskScheduler.Default);
    }

    public static void Cancel()
    {
        lock (Gate) cancellation?.Cancel();
    }

    /// Snapshots only: show a state without downloading anything.
    internal static void Simulate(SetupPhase phase, double progress = 0, string? failure = null) => Set(phase, progress, failure);

    /// Deletes the downloaded copy (Settings → Remove); the next HWP conversion downloads it again.
    public static void Remove()
    {
        if (IsWorking) return;
        try
        {
            if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true);
        }
        catch (Exception error)
        {
            Debug.Log("rhwp remove: " + error.Message);
        }
        Set(SetupPhase.Idle, 0, null);
    }

    /// Downloads and installs, or joins the install already running. progress gets 0…1.
    public static async Task InstallAsync(Action<double>? progress = null, CancellationToken token = default)
    {
        Task task;
        lock (Gate)
        {
            if (progress != null) Listeners.Add(progress);
            if (current == null)
            {
                cancellation = new CancellationTokenSource();
                var source = cancellation;
                current = Task.Run(() => RunAsync(source.Token));
            }
            task = current;
        }
        try
        {
            using (token.Register(Cancel))
            {
                await task;
            }
        }
        finally
        {
            if (progress != null)
            {
                lock (Gate) Listeners.Remove(progress);
            }
        }
    }

    private static async Task RunAsync(CancellationToken token)
    {
        lastPercent = -1;
        Set(SetupPhase.Downloading, 0, null);
        try
        {
            if (!RunsHere) throw new PickException(L("err.hwp_arm"));
            var asset = Archive ?? throw new PickException(L("hwp.err.download", "no build for " + RuntimeInformation.RuntimeIdentifier));
            await DownloadAsync(Folder, asset, Report, () => Set(SetupPhase.Installing, 1, null), token);
            Set(SetupPhase.Idle, 0, null);
        }
        catch (OperationCanceledException)
        {
            Set(SetupPhase.Idle, 0, null);
            throw;
        }
        catch (Exception error)
        {
            Debug.Log("rhwp setup: " + error);
            var message = error is PickException ? error.Message : L("hwp.err.download", error.Message);
            Set(SetupPhase.Failed, 0, message);
            throw new PickException(message);
        }
        finally
        {
            lock (Gate)
            {
                current = null;
                cancellation?.Dispose();
                cancellation = null;
            }
        }
    }

    private static void Report(double fraction)
    {
        // Whole percents only: cards and Settings redraw a hundred times, not once per network packet.
        var value = Math.Clamp(fraction, 0, 1);
        var percent = (int)(value * 100);
        Action<double>[] listeners;
        lock (Gate)
        {
            if (percent == lastPercent) return;
            lastPercent = percent;
            listeners = Listeners.ToArray();
        }
        Set(SetupPhase.Downloading, value, null);
        foreach (var listener in listeners) listener(value);
    }

    private static void Set(SetupPhase phase, double progress, string? failure)
    {
        Phase = phase;
        Progress = progress;
        Failure = failure;
        Platform.Main.Post(() => Changed?.Invoke());
    }

    // Steps

    /// Download → pinned SHA-256 → unpack → move into place → run once.
    public static async Task DownloadAsync(string folder, Asset asset, Action<double> progress, Action installing, CancellationToken token)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Pickverter");
        Directory.CreateDirectory(folder);
        // A download interrupted by quitting leaves its staging folder behind; clear those first.
        foreach (var leftover in Directory.GetDirectories(folder, ".download-*"))
        {
            try
            {
                Directory.Delete(leftover, recursive: true);
            }
            catch (Exception)
            {
            }
        }
        var staging = Path.Combine(folder, ".download-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(staging);
        try
        {
            var archive = Path.Combine(staging, asset.Name);
            using (var response = await client.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? 0;
                await using var input = await response.Content.ReadAsStreamAsync(token);
                await using var output = new FileStream(archive, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, token)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    done += read;
                    if (total > 0) progress(Math.Min(1, done / (double)total));
                }
            }
            await using (var check = File.OpenRead(archive))
            {
                var digest = Convert.ToHexString(await SHA256.HashDataAsync(check, token)).ToLowerInvariant();
                if (!string.Equals(digest, asset.Sha256, StringComparison.OrdinalIgnoreCase)) throw new PickException(L("update.err.checksum"));
            }
            installing();
            var unpacked = Path.Combine(staging, "unpacked");
            var binary = Extract(archive, unpacked);
            // The copy in place stops counting first: a failed check below leaves no version.txt behind.
            File.Delete(Path.Combine(folder, "version.txt"));
            foreach (var file in Directory.GetFiles(unpacked)) File.Move(file, Path.Combine(folder, Path.GetFileName(file)), overwrite: true);
            // Prove it starts on this computer before any job depends on it.
            var banner = await Shell.RunAsync(Path.Combine(folder, binary), ["--version"], token: token);
            if (!banner.Contains(Version, StringComparison.Ordinal)) throw new PickException(L("hwp.err.download", banner.Trim()));
            await File.WriteAllTextAsync(Path.Combine(folder, "version.txt"), Version, token);
        }
        finally
        {
            try
            {
                Directory.Delete(staging, recursive: true);
            }
            catch (Exception)
            {
            }
        }
    }

    /// Takes the program and its LICENSE from the archive's "rhwp" folder (a .zip for Windows, a .tar.gz for macOS)
    /// and returns the program's name. Folders, links, READMEs and odd paths are skipped.
    public static string Extract(string archive, string destination)
    {
        Directory.CreateDirectory(destination);
        var zip = archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        var binary = zip ? "rhwp.exe" : "rhwp";
        if (zip)
        {
            using var file = ZipFile.OpenRead(archive);
            foreach (var entry in file.Entries)
            {
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
                if (Wanted(entry.FullName, binary) is { } name) entry.ExtractToFile(Path.Combine(destination, name), overwrite: true);
            }
        }
        else
        {
            using var file = File.OpenRead(archive);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var tar = new TarReader(gzip);
            while (tar.GetNextEntry() is { } entry)
            {
                // The release also holds the "rhwp" folder itself: only regular files count.
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) continue;
                if (Wanted(entry.Name, binary) is { } name) entry.ExtractToFile(Path.Combine(destination, name), overwrite: true);
            }
        }
        var program = Path.Combine(destination, binary);
        if (!File.Exists(program)) throw new PickException(L("update.err.checksum"));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(program, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead |
                                          UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        return binary;
    }

    /// "rhwp/rhwp.exe" → "rhwp.exe"; null for anything Pickverter doesn't install.
    private static string? Wanted(string path, string binary)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Where(part => part != ".").ToArray();
        if (parts.Length < 2 || parts.Contains("..") || parts[^2] != "rhwp") return null;
        var name = parts[^1];
        return name == binary || name == "LICENSE" ? name : null;
    }
}
