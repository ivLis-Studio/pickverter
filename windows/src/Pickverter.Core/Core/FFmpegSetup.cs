using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pickverter;

/// Downloads ffmpeg and ffprobe for this PC into %LOCALAPPDATA%\Pickverter\ffmpeg, so every video and audio format works
/// without winget. The builds are BtbN's (github.com/BtbN/FFmpeg-Builds, linked from ffmpeg.org): the newest release branch,
/// shared GPL build for this CPU (x64 or Arm64). GitHub publishes every file's SHA-256 with the release; a download is
/// used only when it matches.
public static class FFmpegSetup
{
    public const string Releases = "https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/latest";

    public enum SetupPhase { Idle, Downloading, Installing, Failed }

    public sealed record Asset(string Name, string Url, long Size, string? Sha256, string Version);

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

    /// Where the download goes (PICKVERTER_FFMPEG_DIR overrides it for tests).
    public static string Folder => Environment.GetEnvironmentVariable("PICKVERTER_FFMPEG_DIR") is { Length: > 0 } custom
        ? custom
        : Path.Combine(Platform.Paths.Cache, "ffmpeg");

    /// The ffmpeg in use is the one Pickverter downloaded.
    public static bool IsManaged =>
        Tools.FFmpeg is { } path && path.StartsWith(Folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

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

    /// "win64" or "winarm64": native builds for this PC.
    public static string Architecture => RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "winarm64" : "win64";

    /// One line for Settings and the welcome window while something is happening.
    public static string? StatusLine => Phase switch
    {
        SetupPhase.Downloading => L("ffmpeg.downloading", ((int)(Progress * 100)).ToString(CultureInfo.InvariantCulture)),
        SetupPhase.Installing => L("ffmpeg.installing"),
        SetupPhase.Failed => Failure,
        _ => null,
    };

    /// The job failed only because ffmpeg is missing (a batch prefixes the message with a file name).
    public static bool IsMissing(Exception error) => Tools.FFmpeg == null && error.Message.Contains(Tools.FFmpegHint, StringComparison.Ordinal);

    /// The same option, but it installs ffmpeg first when it's missing (progress on the job's own card).
    public static PickOption Prepare(PickOption option) => option with
    {
        Unavailable = null,
        Perform = async ctx =>
        {
            if (Tools.FFmpeg != null) return await option.Perform(ctx);
            var download = ctx.Portion(0, 0.5);
            await InstallAsync(fraction => download.Report(fraction,
                L("ffmpeg.downloading", ((int)(fraction * 100)).ToString(CultureInfo.InvariantCulture))), ctx.Token);
            ctx.Report(0.5, "");
            return await option.Perform(ctx.Portion(0.5, 1));
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

    /// Deletes the downloaded copy (Settings → Remove).
    public static void Remove()
    {
        if (IsWorking) return;
        try
        {
            if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true);
        }
        catch (Exception error)
        {
            Debug.Log("ffmpeg remove: " + error.Message);
        }
        Tools.Refresh();
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
            await DownloadAsync(Folder, Report, () => Set(SetupPhase.Installing, 1, null), token);
            Tools.Refresh();
            Set(SetupPhase.Idle, 0, null);
            _ = Task.Run(Tools.FFmpegEncoders);
        }
        catch (OperationCanceledException)
        {
            Set(SetupPhase.Idle, 0, null);
            throw;
        }
        catch (Exception error)
        {
            Debug.Log("ffmpeg setup: " + error);
            var message = error is PickException ? error.Message : L("ffmpeg.err.download", error.Message);
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

    /// Release lookup → download → SHA-256 → unpack bin\ → swap into place → run once.
    public static async Task DownloadAsync(string folder, Action<double> progress, Action installing, CancellationToken token)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Pickverter");
        // CI runners share an address and GitHub's anonymous API limit; they pass their token.
        if (Environment.GetEnvironmentVariable("PICKVERTER_GITHUB_TOKEN") is { Length: > 0 } bearer)
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        using var release = JsonDocument.Parse(await client.GetStringAsync(Releases, token));
        var asset = PickAsset(release.RootElement, Architecture) ?? throw new PickException(L("ffmpeg.err.download", "no build for " + Architecture));
        var expected = asset.Sha256 ?? await ChecksumFromListAsync(client, release.RootElement, asset.Name, token)
            ?? throw new PickException(L("update.err.checksum"));
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
            var zip = Path.Combine(staging, asset.Name);
            using (var response = await client.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? asset.Size;
                await using var input = await response.Content.ReadAsStreamAsync(token);
                await using var output = new FileStream(zip, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
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
            await using (var check = File.OpenRead(zip))
            {
                var digest = Convert.ToHexString(await SHA256.HashDataAsync(check, token)).ToLowerInvariant();
                if (!string.Equals(digest, expected, StringComparison.OrdinalIgnoreCase)) throw new PickException(L("update.err.checksum"));
            }
            installing();
            var unpacked = Path.Combine(staging, "bin");
            Extract(zip, unpacked);
            Swap(unpacked, folder);
            // Prove it starts on this PC before any job depends on it.
            if (OperatingSystem.IsWindows()) await Shell.RunAsync(Path.Combine(folder, "ffmpeg.exe"), ["-hide_banner", "-version"], token: token);
            await File.WriteAllTextAsync(Path.Combine(folder, "version.txt"), asset.Version, token);
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

    /// The newest release branch ("ffmpeg-n9.0-latest-win64-gpl-shared-9.0.zip"), or master when there is none.
    public static Asset? PickAsset(JsonElement release, string architecture)
    {
        var branch = new Regex("^ffmpeg-n(\\d+)\\.(\\d+)-latest-" + Regex.Escape(architecture) + "-gpl-shared-[\\d.]+\\.zip$");
        var masterName = "ffmpeg-master-latest-" + architecture + "-gpl-shared.zip";
        Asset? best = null, master = null;
        (int, int) bestVersion = (-1, -1);
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in assets.EnumerateArray())
        {
            var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var url = item.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
            var size = item.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;
            string? sha = null;
            if (item.TryGetProperty("digest", out var d) && d.GetString() is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                sha = digest["sha256:".Length..].ToLowerInvariant();
            if (url.Length == 0 || !url.StartsWith("https://", StringComparison.Ordinal)) continue;
            var match = branch.Match(name);
            if (match.Success)
            {
                var version = (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
                if (version.CompareTo(bestVersion) > 0)
                {
                    bestVersion = version;
                    best = new Asset(name, url, size, sha, version.Item1 + "." + version.Item2);
                }
            }
            else if (name == masterName)
            {
                master = new Asset(name, url, size, sha, "master");
            }
        }
        return best ?? master;
    }

    /// Older releases without per-file digests: the "checksums.sha256" list published with them.
    private static async Task<string?> ChecksumFromListAsync(HttpClient client, JsonElement release, string name, CancellationToken token)
    {
        foreach (var item in release.GetProperty("assets").EnumerateArray())
        {
            if (item.GetProperty("name").GetString() != "checksums.sha256") continue;
            var list = await client.GetStringAsync(item.GetProperty("browser_download_url").GetString(), token);
            foreach (var line in list.Split('\n'))
            {
                var parts = line.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[1].TrimStart('*') == name && parts[0].Length == 64) return parts[0].ToLowerInvariant();
            }
        }
        return null;
    }

    /// Takes ffmpeg.exe, ffprobe.exe and the DLLs they share from the archive's bin folder.
    public static void Extract(string zip, string destination)
    {
        Directory.CreateDirectory(destination);
        using var archive = ZipFile.OpenRead(zip);
        foreach (var entry in archive.Entries)
        {
            var parts = entry.FullName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !parts[^2].Equals("bin", StringComparison.OrdinalIgnoreCase)) continue;
            var name = parts[^1];
            var wanted = name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("ffprobe.exe", StringComparison.OrdinalIgnoreCase)
                         || name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
            if (!wanted || name.Contains("..", StringComparison.Ordinal)) continue;
            entry.ExtractToFile(Path.Combine(destination, name), overwrite: true);
        }
        if (!File.Exists(Path.Combine(destination, "ffmpeg.exe")) || !File.Exists(Path.Combine(destination, "ffprobe.exe")))
            throw new PickException(L("update.err.checksum"));
    }

    /// Replaces the previous copy (if any) with the freshly unpacked files.
    private static void Swap(string unpacked, string folder)
    {
        foreach (var old in Directory.GetFiles(folder))
        {
            var name = Path.GetFileName(old);
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) File.Delete(old);
        }
        foreach (var file in Directory.GetFiles(unpacked)) File.Move(file, Path.Combine(folder, Path.GetFileName(file)), overwrite: true);
    }
}

