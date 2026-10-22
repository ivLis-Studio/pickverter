using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace Pickverter;

/// One downloadable build in update-windows.json.
public sealed class UpdateAsset
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    /// Ed25519 over the signed message, base64.
    [JsonPropertyName("signature")] public string Signature { get; set; } = "";
}

/// What a GitHub release publishes as update-windows.json next to the Windows builds.
public sealed class UpdateManifest
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("build")] public string? Build { get; set; }
    [JsonPropertyName("published")] public string? Published { get; set; }
    [JsonPropertyName("minimumSystemVersion")] public string? MinimumSystemVersion { get; set; }
    [JsonPropertyName("notes")] public string? Notes { get; set; }
    [JsonPropertyName("notesURL")] public string? NotesUrl { get; set; }
    /// Keyed by architecture: "x64", "arm64".
    [JsonPropertyName("assets")] public Dictionary<string, UpdateAsset> Assets { get; set; } = new();

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

public static class Versions
{
    /// Numeric dotted compare; a pre-release ("1.0.0-beta.2") sorts before its release.
    public static int Compare(string a, string b)
    {
        static (int[] Numbers, string? Pre) Split(string value)
        {
            var trimmed = value.StartsWith('v') ? value[1..] : value;
            trimmed = trimmed.Split('+')[0];
            var parts = trimmed.Split('-', 2);
            var numbers = parts[0].Split('.').Select(part => int.TryParse(part, out var n) ? n : 0).ToArray();
            return (numbers, parts.Length > 1 ? parts[1] : null);
        }
        var (na, pa) = Split(a);
        var (nb, pb) = Split(b);
        for (var index = 0; index < Math.Max(na.Length, nb.Length); index++)
        {
            var x = index < na.Length ? na[index] : 0;
            var y = index < nb.Length ? nb[index] : 0;
            if (x != y) return x < y ? -1 : 1;
        }
        if (pa == null && pb == null) return 0;
        if (pa == null) return 1;
        if (pb == null) return -1;
        return NaturalComparer.Instance.Compare(pa, pb) switch { < 0 => -1, > 0 => 1, _ => 0 };
    }
}

public static class UpdateSigning
{
    public static byte[] SignedMessage(string version, string sha256, long size) =>
        Encoding.UTF8.GetBytes($"pickverter-update:1\n{version}\n{sha256.ToLowerInvariant()}\n{size}");

    public static (string PrivateKey, string PublicKey) GenerateKey()
    {
        var key = new Ed25519PrivateKeyParameters(new SecureRandom());
        return (Convert.ToBase64String(key.GetEncoded()), Convert.ToBase64String(key.GeneratePublicKey().GetEncoded()));
    }

    private static Ed25519PrivateKeyParameters PrivateKey(string base64)
    {
        var bytes = Convert.FromBase64String(base64.Trim());
        if (bytes.Length != 32) throw new PickException("bad key");
        return new Ed25519PrivateKeyParameters(bytes, 0);
    }

    public static string PublicKey(string privateKey) => Convert.ToBase64String(PrivateKey(privateKey).GeneratePublicKey().GetEncoded());

    public static string Sign(byte[] message, string privateKey)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, PrivateKey(privateKey));
        signer.BlockUpdate(message, 0, message.Length);
        return Convert.ToBase64String(signer.GenerateSignature());
    }

    public static bool Verify(byte[] message, string signature, byte[] publicKey)
    {
        try
        {
            if (publicKey.Length != 32) return false;
            var signer = new Ed25519Signer();
            signer.Init(false, new Ed25519PublicKeyParameters(publicKey, 0));
            signer.BlockUpdate(message, 0, message.Length);
            return signer.VerifySignature(Convert.FromBase64String(signature));
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool Verify(UpdateManifest manifest, UpdateAsset asset, byte[] publicKey) =>
        Verify(SignedMessage(manifest.Version, asset.Sha256, asset.Size), asset.Signature, publicKey);

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

/// Swaps the running app for a staged build (Windows layer).
public interface IUpdateInstaller
{
    /// Extracts and checks the downloaded zip; returns the ready-to-install executable.
    Task<string> StageAsync(string zipPath, string version, CancellationToken token);
    bool CanInstallInPlace { get; }
    void InstallAndRelaunch(string stagedExecutable);
}

/// GitHub Releases updater: check -> download -> verify (Ed25519, size, SHA-256, version) -> swap -> relaunch.
public sealed class Updater : INotifyPropertyChanged
{
    public static Updater Shared { get; } = new();

    public enum UpdatePhase { Idle, Checking, UpToDate, Available, Downloading, Ready, Installing, Failed }

    private UpdatePhase phase = UpdatePhase.Idle;
    private double fraction;
    private string? failure;
    private UpdateManifest? latest;
    private string? stagedExecutable;
    private string? stagedVersion;
    private CancellationTokenSource? download;
    private IDisposable? timer;
    private readonly HashSet<string> announced = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Changed([CallerMemberName] string? name = null) =>
        Platform.Main.Post(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)));

    public UpdatePhase Phase { get => phase; private set { phase = value; Changed(); } }
    public double Fraction { get => fraction; private set { fraction = value; Changed(); } }
    public string? Failure { get => failure; private set { failure = value; Changed(); } }
    public UpdateManifest? Latest { get => latest; private set { latest = value; Changed(); } }
    public DateTimeOffset? LastChecked => Preferences.Shared.LastUpdateCheck;
    public IUpdateInstaller? Installer { get; set; }

    private static string? Metadata(string key) =>
        typeof(Updater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(attribute => attribute.Key == key)?.Value;

    public string CurrentVersion
    {
        get
        {
            var forced = Environment.GetEnvironmentVariable("PICKVERTER_FAKE_VERSION");
            if (!string.IsNullOrEmpty(forced)) return forced;
            var informational = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(Updater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            return informational.Split('+')[0];
        }
    }

    public string Repository => Metadata("PickverterRepository") ?? "";
    public bool IsReleaseBuild => Metadata("PickverterRelease") == "true" || Environment.GetEnvironmentVariable("PICKVERTER_UPDATE_TEST") != null;

    public byte[] PublicKey
    {
        get
        {
            try { return Convert.FromBase64String(Metadata("PickverterUpdateKey") ?? ""); } catch (FormatException) { return []; }
        }
    }

    public bool IsEnabled => IsReleaseBuild && Repository.Contains('/') && PublicKey.Length == 32;

    public static string Architecture => RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";

    public string ManifestUrl => Environment.GetEnvironmentVariable("PICKVERTER_UPDATE_URL") is { Length: > 0 } custom
        ? custom
        : $"https://github.com/{Repository}/releases/latest/download/update-windows.json";

    public string ReleasesPage => $"https://github.com/{Repository}/releases";
    public string RepositoryPage => $"https://github.com/{Repository}";

    public static string UpdatesFolder => Path.Combine(Platform.Paths.Cache, "Updates");

    public bool IsBusy => Phase is UpdatePhase.Checking or UpdatePhase.Downloading or UpdatePhase.Installing;

    public string? UpdatedFrom { get; private set; }

    public void Start()
    {
        var from = Preferences.Shared.UpdatedFrom;
        if (from != null)
        {
            Preferences.Shared.UpdatedFrom = null;
            if (from != CurrentVersion) UpdatedFrom = from;
        }
        FileSystem.TryDelete(UpdatesFolder);
        if (!IsEnabled) return;
        timer = Platform.Main.After(TimeSpan.FromSeconds(20), () => Platform.Main.Post(Tick));
    }

    private void Tick()
    {
        CheckIfDue();
        timer?.Dispose();
        timer = Platform.Main.After(TimeSpan.FromHours(1), () => Platform.Main.Post(Tick));
    }

    public void CheckIfDue()
    {
        if (!IsEnabled || !Preferences.Shared.AutoCheckUpdates || IsBusy || Phase == UpdatePhase.Ready) return;
        if (LastChecked is { } last && DateTimeOffset.Now - last < TimeSpan.FromHours(20)) return;
        _ = CheckAsync(false);
    }

    private static HttpClient Client(TimeSpan timeout)
    {
        var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Pickverter-Updater");
        return client;
    }

    public async Task CheckAsync(bool userInitiated)
    {
        if (!IsEnabled)
        {
            if (userInitiated)
            {
                Failure = L("update.disabled");
                Phase = UpdatePhase.Failed;
            }
            return;
        }
        if (IsBusy) return;
        Phase = UpdatePhase.Checking;
        try
        {
            using var client = Client(TimeSpan.FromSeconds(20));
            using var response = await client.GetAsync(ManifestUrl);
            Preferences.Shared.LastUpdateCheck = DateTimeOffset.Now;
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // No Windows build in the latest release yet.
                Latest = null;
                Phase = UpdatePhase.UpToDate;
                return;
            }
            if (!response.IsSuccessStatusCode) throw new PickException(L("update.err.server", ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)));
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(await response.Content.ReadAsStringAsync(), UpdateManifest.Json)
                ?? throw new PickException(L("update.err.server", "json"));
            if (!manifest.Assets.TryGetValue(Architecture, out var asset) || !UpdateSigning.Verify(manifest, asset, PublicKey))
                throw new PickException(L("update.err.signature"));
            if (Versions.Compare(manifest.Version, CurrentVersion) <= 0 || !SystemSupports(manifest))
            {
                Latest = null;
                Phase = UpdatePhase.UpToDate;
                return;
            }
            Latest = manifest;
            Phase = stagedVersion == manifest.Version && stagedExecutable != null ? UpdatePhase.Ready : UpdatePhase.Available;
            if (userInitiated || Phase != UpdatePhase.Available || Preferences.Shared.SkippedVersion == manifest.Version) return;
            if (Preferences.Shared.AutoInstallUpdates) Download();
            else Platform.Main.Post(() => AnnounceAvailable(manifest));
        }
        catch (Exception error)
        {
            Debug.Log("update check failed: " + error);
            Failure = Errors.Describe(error);
            Phase = userInitiated ? UpdatePhase.Failed : UpdatePhase.Idle;
        }
    }

    private static bool SystemSupports(UpdateManifest manifest)
    {
        if (manifest.MinimumSystemVersion is not { Length: > 0 } minimum || !Version.TryParse(minimum, out var required)) return true;
        return Environment.OSVersion.Version >= required;
    }

    public void Skip()
    {
        if (Latest == null) return;
        Preferences.Shared.SkippedVersion = Latest.Version;
        Phase = UpdatePhase.Idle;
    }

    public void Download()
    {
        if (Latest is not { } manifest || Phase == UpdatePhase.Ready || Phase == UpdatePhase.Downloading) return;
        download = new CancellationTokenSource();
        var token = download.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var executable = await Stage(manifest, token);
                stagedExecutable = executable;
                stagedVersion = manifest.Version;
                Phase = UpdatePhase.Ready;
                Platform.Main.Post(() => AnnounceReady(manifest));
            }
            catch (OperationCanceledException)
            {
                Phase = UpdatePhase.Available;
            }
            catch (Exception error)
            {
                Failure = Errors.Describe(error);
                Phase = UpdatePhase.Failed;
            }
        }, token);
    }

    public void CancelDownload() => download?.Cancel();

    /// Downloads, verifies and unpacks a release. Returns the ready-to-install executable.
    public async Task<string> Stage(UpdateManifest manifest, CancellationToken token)
    {
        if (!manifest.Assets.TryGetValue(Architecture, out var asset) || !Uri.TryCreate(asset.Url, UriKind.Absolute, out var url) || url.Scheme != "https")
            throw new PickException(L("update.err.bundle"));
        if (!UpdateSigning.Verify(manifest, asset, PublicKey)) throw new PickException(L("update.err.signature"));
        Phase = UpdatePhase.Downloading;
        Fraction = 0;
        var folder = Path.Combine(UpdatesFolder, manifest.Version);
        FileSystem.TryDelete(folder);
        Directory.CreateDirectory(folder);
        var zip = Path.Combine(folder, $"Pickverter-{manifest.Version}-win-{Architecture}.zip");
        using (var client = Client(TimeSpan.FromMinutes(30)))
        using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token))
        {
            if (!response.IsSuccessStatusCode) throw new PickException(L("update.err.server", ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)));
            await using var input = await response.Content.ReadAsStreamAsync(token);
            await using var output = new FileStream(zip, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            var buffer = new byte[1 << 16];
            long total = 0;
            int read;
            var last = DateTime.MinValue;
            while ((read = await input.ReadAsync(buffer, token)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), token);
                total += read;
                if ((DateTime.UtcNow - last).TotalMilliseconds > 120 && asset.Size > 0)
                {
                    last = DateTime.UtcNow;
                    Fraction = Math.Min(1, total / (double)asset.Size);
                }
            }
        }
        if (FileNames.Size(zip) != asset.Size) throw new PickException(L("update.err.checksum"));
        var digest = await Task.Run(() => UpdateSigning.Sha256(zip), token);
        if (digest != asset.Sha256.ToLowerInvariant()) throw new PickException(L("update.err.checksum"));
        var installer = Installer ?? throw new PickException(L("update.disabled"));
        var executable = await installer.StageAsync(zip, manifest.Version, token);
        FileSystem.TryDelete(zip);
        return executable;
    }

    public void InstallAndRelaunch()
    {
        if (stagedExecutable == null)
        {
            Download();
            return;
        }
        if (Installer is not { } installer || !installer.CanInstallInPlace)
        {
            Failure = L("update.err.location");
            Phase = UpdatePhase.Failed;
            Platform.Shell.Reveal([stagedExecutable]);
            return;
        }
        Phase = UpdatePhase.Installing;
        try
        {
            Preferences.Shared.UpdatedFrom = CurrentVersion;
            installer.InstallAndRelaunch(stagedExecutable);
        }
        catch (Exception error)
        {
            Preferences.Shared.UpdatedFrom = null;
            Failure = Errors.Describe(error);
            Phase = UpdatePhase.Failed;
        }
    }

    public Action? ShowUpdates { get; set; }

    private void AnnounceAvailable(UpdateManifest manifest)
    {
        if (!announced.Add("available-" + manifest.Version)) return;
        JobCenter.Shared.Notice(L("update.card.available", manifest.Version), L("update.card.available.d"), "arrow.down.circle", JobTone.Info,
            new NoticeAction(L("update.card.details"), "info.circle", () => ShowUpdates?.Invoke()), 12);
    }

    private void AnnounceReady(UpdateManifest manifest)
    {
        if (!announced.Add("ready-" + manifest.Version)) return;
        JobCenter.Shared.Notice(L("update.card.ready", manifest.Version), L("update.card.ready.d"), "arrow.down.circle", JobTone.Info,
            new NoticeAction(L("update.restart"), "arrow.clockwise", InstallAndRelaunch), 20);
    }

    public void AcknowledgeUpdate() => UpdatedFrom = null;

    /// Snapshots and tests: show a state without talking to GitHub.
    internal void Simulate(UpdatePhase phase, UpdateManifest? manifest, double progress = 0, string? failure = null)
    {
        latest = manifest;
        fraction = progress;
        this.failure = failure;
        this.phase = phase;
    }

    public string StatusText
    {
        get
        {
            switch (Phase)
            {
                case UpdatePhase.Idle:
                case UpdatePhase.UpToDate:
                    if (LastChecked is not { } date) return Phase == UpdatePhase.UpToDate ? L("update.latest") : L("update.never");
                    var when = date.ToLocalTime().ToString("g", Loc.Culture);
                    return Phase == UpdatePhase.UpToDate ? L("update.latest") + " · " + when : L("update.checked", when);
                case UpdatePhase.Checking: return L("update.checking");
                case UpdatePhase.Available: return L("update.available", Latest?.Version ?? "");
                case UpdatePhase.Downloading: return L("update.downloading", ((int)(Fraction * 100)).ToString(CultureInfo.InvariantCulture));
                case UpdatePhase.Ready: return L("update.ready", Latest?.Version ?? "");
                case UpdatePhase.Installing: return L("update.installing");
                default: return Failure ?? "";
            }
        }
    }
}
