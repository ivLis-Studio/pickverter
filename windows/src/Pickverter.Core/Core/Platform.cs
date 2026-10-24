using System.Diagnostics;
using System.Text.Json;

namespace Pickverter;

/// Services the Windows layer (Pickverter.Kit) provides. Defaults keep the core usable in tests and on other systems.
public static class Platform
{
    public static IMainThread Main { get; set; } = new InlineMainThread();
    public static IShellService Shell { get; set; } = new DefaultShell();
    public static IClipboardService Clipboard { get; set; } = new MemoryClipboard();
    public static IVisionService Vision { get; set; } = new NoVision();
    public static IPdfRenderer Pdf { get; set; } = new NoPdfRenderer();
    public static IHtmlPrinter Printer { get; set; } = new NoPrinter();
    public static IOfficeService Office { get; set; } = new LibreOfficeOnly();
    public static INativeMedia Media { get; set; } = new NoNativeMedia();
    public static IEditors Editors { get; set; } = new InjectedEditors();
    public static IActionsService Actions { get; set; } = new NoActions();
    public static IFeedback Feedback { get; set; } = new NoFeedback();
    public static IStartup Startup { get; set; } = new NoStartup();
    public static IImageCodecs Codecs { get; set; } = new NoCodecs();
    public static AppPaths Paths { get; set; } = AppPaths.Default();
}

/// Settings: settings.json and the wheel layout. Cache: logs, the clipboard cache and the downloads (ffmpeg, rhwp, the
/// background-removal model). Temp: scratch files; null means the system's temp folder.
public sealed record AppPaths(string Settings, string Cache, string? Temp = null)
{
    public static AppPaths Default()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(roaming)) roaming = Path.GetTempPath();
        if (string.IsNullOrEmpty(local)) local = Path.GetTempPath();
        return new AppPaths(Path.Combine(roaming, "Pickverter"), Path.Combine(local, "Pickverter"));
    }

    /// The Store edition's folders, inside its package's LocalCache (%LOCALAPPDATA%\Packages\<family name>\LocalCache),
    /// exactly where Windows would redirect %APPDATA%\Pickverter, %LOCALAPPDATA%\Pickverter and %TEMP% for a packaged app.
    /// Spelled out, the paths are real for every program: File Explorer pastes clipboard results, Edge, Office and the
    /// downloaded tools open the scratch files, whether or not they run inside the package.
    public static AppPaths InPackage(string localCache) => new(
        Path.Combine(localCache, "Roaming", "Pickverter"),
        Path.Combine(localCache, "Local", "Pickverter"),
        Path.Combine(localCache, "Local", "Temp"));
}

public interface IMainThread
{
    bool IsMainThread { get; }
    void Post(Action action);
    Task RunAsync(Action action);
    Task<T> RunAsync<T>(Func<T> func);
    IDisposable After(TimeSpan delay, Action action);
}

/// Runs everything inline (tests, command line). Delayed work fires on a timer thread.
public sealed class InlineMainThread : IMainThread
{
    public bool IsMainThread => true;
    public void Post(Action action) => action();

    public Task RunAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    public Task<T> RunAsync<T>(Func<T> func) => Task.FromResult(func());

    public IDisposable After(TimeSpan delay, Action action) =>
        new Timer(_ => action(), null, delay, Timeout.InfiniteTimeSpan);
}

public interface IShellService
{
    string DownloadsFolder { get; }
    string DesktopFolder { get; }
    string DocumentsFolder { get; }
    string? OneDriveFolder { get; }
    /// "image", "video", "audio" or null (Windows' PerceivedType for the extension).
    string? PerceivedType(string ext);
    string FormatBytes(long bytes);
    void Reveal(IReadOnlyList<string> paths);
    void Open(string path);
    void OpenUrl(string url);
    Task RecycleAsync(IReadOnlyList<string> paths);
    /// Windows Security's ransomware protection is on (it blocks unknown apps in Documents, Pictures…).
    bool ControlledFolderAccess { get; }
    void OpenControlledFolderSettings();
    /// The Windows accent color as ffmpeg's 0xRRGGBB (audio visualizer).
    string AccentHex { get; }
    /// Items inside a folder were renamed: File Explorer windows showing it refresh.
    void NotifyFolderChanged(string folder);
}

public class DefaultShell : IShellService
{
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public virtual string DownloadsFolder => Path.Combine(Home, "Downloads");
    public virtual string DesktopFolder => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) is { Length: > 0 } desktop ? desktop : Path.Combine(Home, "Desktop");
    public virtual string DocumentsFolder => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    public virtual string? OneDriveFolder => Environment.GetEnvironmentVariable("OneDrive");
    public virtual string? PerceivedType(string ext) => null;

    public virtual string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        var culture = Loc.Culture;
        return unit == 0 ? $"{bytes.ToString("N0", culture)} {units[0]}" : $"{value.ToString(value < 10 ? "0.#" : "0", culture)} {units[unit]}";
    }

    public virtual void Reveal(IReadOnlyList<string> paths) { }
    public virtual void Open(string path) { }
    public virtual void OpenUrl(string url) { }

    public virtual Task RecycleAsync(IReadOnlyList<string> paths)
    {
        foreach (var path in paths) FileSystem.TryDelete(path);
        return Task.CompletedTask;
    }

    public virtual bool ControlledFolderAccess => false;
    public virtual void OpenControlledFolderSettings() { }
    public virtual string AccentHex => "0x0A84FF";
    public virtual void NotifyFolderChanged(string folder) { }
}

public interface IClipboardService
{
    Task SetTextAsync(string text);
    Task SetFilesAsync(IReadOnlyList<string> paths);
    /// Images go up as pixels plus the file, text as text, everything else as files.
    Task WriteResultsAsync(IReadOnlyList<string> paths);
    /// The formats now on the clipboard (command line checks).
    Task<string> DescribeAsync();
}

public sealed class MemoryClipboard : IClipboardService
{
    public string? Text { get; private set; }
    public IReadOnlyList<string> Files { get; private set; } = [];

    public Task SetTextAsync(string text)
    {
        Text = text;
        Files = [];
        return Task.CompletedTask;
    }

    public Task SetFilesAsync(IReadOnlyList<string> paths)
    {
        Files = paths;
        Text = null;
        return Task.CompletedTask;
    }

    public Task WriteResultsAsync(IReadOnlyList<string> paths) => SetFilesAsync(paths);

    public Task<string> DescribeAsync() => Task.FromResult(Text != null ? "text" : Files.Count > 0 ? "files" : "");
}

public sealed record RecognizedText(string Text, NormRect Box);

public interface IVisionService
{
    bool OcrAvailable { get; }
    /// Lines of text with their boxes (normalized, top-left origin), in reading order.
    Task<IReadOnlyList<RecognizedText>> RecognizeAsync(string imagePath, CancellationToken token);
    bool FacesAvailable { get; }
    Task<IReadOnlyList<NormRect>> DetectFacesAsync(string imagePath, CancellationToken token);
}

public sealed class NoVision : IVisionService
{
    public bool OcrAvailable => false;
    public Task<IReadOnlyList<RecognizedText>> RecognizeAsync(string imagePath, CancellationToken token) =>
        throw new PickException(L("err.ocr_missing"));
    public bool FacesAvailable => false;
    public Task<IReadOnlyList<NormRect>> DetectFacesAsync(string imagePath, CancellationToken token) =>
        Task.FromResult<IReadOnlyList<NormRect>>([]);
}

public interface IPdfRenderer
{
    bool Available { get; }
    /// Renders one page (its rotation applied) on white into a PNG file.
    Task RenderPageAsync(string pdf, int index, double dpi, string pngPath, CancellationToken token);
}

public sealed class NoPdfRenderer : IPdfRenderer
{
    public bool Available => false;
    public Task RenderPageAsync(string pdf, int index, double dpi, string pngPath, CancellationToken token) =>
        throw new PickException(L("err.render_page"));
}

/// HTML -> PDF (Microsoft Edge in headless mode on Windows).
public interface IHtmlPrinter
{
    bool Available { get; }
    Task PrintAsync(string htmlPath, string pdfPath, CancellationToken token);
}

public sealed class NoPrinter : IHtmlPrinter
{
    public bool Available => false;
    public Task PrintAsync(string htmlPath, string pdfPath, CancellationToken token) => throw new PickException(L("err.edge_missing"));
}

public enum OfficeApp { Word, Excel, PowerPoint }

public interface IOfficeService
{
    bool Has(OfficeApp app);
    /// Exports with Microsoft Office. target: pdf, docx, rtf, txt, html, odt, xlsx, csv, pptx, png (a folder of slides).
    Task ExportAsync(OfficeApp app, string source, string destination, string target, CancellationToken token);
}

public sealed class LibreOfficeOnly : IOfficeService
{
    public bool Has(OfficeApp app) => false;
    public Task ExportAsync(OfficeApp app, string source, string destination, string target, CancellationToken token) =>
        throw new PickException(L("err.needs_app", app.ToString()));
}

/// Windows' own media stack (Media Foundation) for the basics when ffmpeg isn't installed.
public interface INativeMedia
{
    bool Available { get; }
    bool CanTranscode(string sourceExt, string targetExt);
    Task TranscodeAsync(string source, string destination, string targetExt, double? start, double? end, PickContext ctx);
    Task SnapshotAsync(string source, double position, string pngPath, CancellationToken token);
    Task<double> DurationAsync(string source);
    Task<(int Width, int Height)?> DisplaySizeAsync(string source);
}

public sealed class NoNativeMedia : INativeMedia
{
    public bool Available => false;
    public bool CanTranscode(string sourceExt, string targetExt) => false;
    public Task TranscodeAsync(string source, string destination, string targetExt, double? start, double? end, PickContext ctx) =>
        throw new PickException(Tools.FFmpegHint);
    public Task SnapshotAsync(string source, double position, string pngPath, CancellationToken token) =>
        throw new PickException(Tools.FFmpegHint);
    public Task<double> DurationAsync(string source) => Task.FromResult(0.0);
    public Task<(int Width, int Height)?> DisplaySizeAsync(string source) => Task.FromResult<(int, int)?>(null);
}

public interface IEditors
{
    Task<CropSelection> CropAsync(string file, int count, CancellationToken token);
    Task<RedactSelection> RedactAsync(string file, int count, CancellationToken token);
    Task<TrimSelection> TrimAsync(string file, double duration, bool isVideo, CancellationToken token);
    Task<PdfArrangement> PagesAsync(IReadOnlyList<string> files, CancellationToken token);
    Task<MetadataEdit> MetadataAsync(string file, MetaSnapshot snapshot, CancellationToken token);
    Task<string> PasswordAsync(string file, CancellationToken token);
    Task<string> RangeAsync(string file, int pageCount, CancellationToken token);
    /// The open password of a locked document (HWP, HWPX). retry: the last one didn't match.
    Task<string> UnlockAsync(string file, bool retry, CancellationToken token);
    /// "Compress › Custom Size…": the size to get under, in MB (1 MB = 1,000,000 bytes). bytes: the file's size now.
    Task<double> SizeAsync(string file, long bytes, CancellationToken token);
}

/// Editors answered through PICKVERTER_EDITOR_<NAME> (JSON, or "cancel"), so tests never open a window.
public static class EditorInjection
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static bool TryGet<T>(string name, out T value)
    {
        value = default!;
        var key = "PICKVERTER_EDITOR_" + name.ToUpperInvariant();
        var raw = Environment.GetEnvironmentVariable(key);
        if (raw == null) return false;
        if (raw == "cancel") throw new OperationCanceledException();
        if (typeof(T) == typeof(string))
        {
            value = (T)(object)raw;
            return true;
        }
        try
        {
            value = JsonSerializer.Deserialize<T>(raw, Json) ?? throw new PickException(key + ": empty");
            return true;
        }
        catch (JsonException error)
        {
            throw new PickException($"{key}: {error.Message}");
        }
    }

    /// PICKVERTER_EDITOR_SIZE: megabytes the way someone would type them ("1.5", "1,5", "2 MB"); "cancel", or anything
    /// that isn't a size, cancels.
    public static bool TryGetSize(out double megabytes)
    {
        megabytes = 0;
        var raw = Environment.GetEnvironmentVariable("PICKVERTER_EDITOR_SIZE");
        if (raw == null) return false;
        megabytes = SizeTargets.ParseMegabytes(raw) ?? throw new OperationCanceledException();
        return true;
    }
}

public class InjectedEditors : IEditors
{
    private static T Answer<T>(string name) =>
        EditorInjection.TryGet<T>(name, out var value) ? value : throw new PickException(L("err.no_editor"));

    public virtual Task<CropSelection> CropAsync(string file, int count, CancellationToken token) => Task.FromResult(Answer<CropSelection>("crop"));
    public virtual Task<RedactSelection> RedactAsync(string file, int count, CancellationToken token) => Task.FromResult(Answer<RedactSelection>("redact"));
    public virtual Task<TrimSelection> TrimAsync(string file, double duration, bool isVideo, CancellationToken token) => Task.FromResult(Answer<TrimSelection>("trim"));
    public virtual Task<PdfArrangement> PagesAsync(IReadOnlyList<string> files, CancellationToken token) => Task.FromResult(Answer<PdfArrangement>("pages"));
    public virtual Task<MetadataEdit> MetadataAsync(string file, MetaSnapshot snapshot, CancellationToken token) => Task.FromResult(Answer<MetadataEdit>("metadata"));
    public virtual Task<string> PasswordAsync(string file, CancellationToken token) => Task.FromResult(Answer<string>("password"));
    public virtual Task<string> RangeAsync(string file, int pageCount, CancellationToken token) => Task.FromResult(Answer<string>("range"));
    public virtual Task<string> UnlockAsync(string file, bool retry, CancellationToken token) => Task.FromResult(Answer<string>("unlock"));
    public virtual Task<double> SizeAsync(string file, long bytes, CancellationToken token) =>
        EditorInjection.TryGetSize(out var megabytes) ? Task.FromResult(megabytes) : throw new PickException(L("err.no_editor"));
}

public interface IActionsService
{
    Task ShareAsync(IReadOnlyList<string> files, ScreenPoint anchor);
    void OpenWith(string file);
    void Float(IReadOnlyList<string> files, ScreenPoint anchor);
    Task<string?> PickFolderAsync(bool move, int count, string? initial);
}

public sealed class NoActions : IActionsService
{
    public Task ShareAsync(IReadOnlyList<string> files, ScreenPoint anchor) => throw new PickException(L("err.airdrop"));
    public void OpenWith(string file) { }
    public void Float(IReadOnlyList<string> files, ScreenPoint anchor) { }
    public Task<string?> PickFolderAsync(bool move, int count, string? initial) => Task.FromResult(Environment.GetEnvironmentVariable("PICKVERTER_FOLDER"));
}

public interface IFeedback
{
    void Success();
    void Failure();
    void Beep();
    bool ScreenReaderActive { get; }
    void Announce(string text);
}

public sealed class NoFeedback : IFeedback
{
    public void Success() { }
    public void Failure() { }
    public void Beep() { }
    public bool ScreenReaderActive => false;
    public void Announce(string text) { }
}

/// Windows' own image codecs (WIC): HEIC export when the HEIF and HEVC extensions are installed.
public interface IImageCodecs
{
    bool CanEncodeHeic { get; }
    Task EncodeHeicAsync(string pngPath, string destination, double quality, CancellationToken token);
}

public sealed class NoCodecs : IImageCodecs
{
    public bool CanEncodeHeic => false;
    public Task EncodeHeicAsync(string pngPath, string destination, double quality, CancellationToken token) =>
        throw new PickException(L("err.heic_encoder"));
}

public interface IStartup
{
    bool LaunchAtLogin { get; set; }

    /// The whole story; the Run key only knows on and off.
    StartupState State => LaunchAtLogin ? StartupState.Enabled : StartupState.Disabled;

    /// Raised on the main thread when State changed by itself (Windows answered an earlier request).
    event Action? Changed
    {
        add { }
        remove { }
    }

    /// Asks Windows again (the user may have changed it in Task Manager meanwhile).
    Task RefreshAsync() => Task.CompletedTask;
}

public sealed class NoStartup : IStartup
{
    public bool LaunchAtLogin { get; set; }
}

public static class Launcher
{
    /// Starts a program without a console window flashing up (or with one, for terminals).
    public static Process? Start(string file, IEnumerable<string> arguments, string? workingDirectory = null, bool window = false)
    {
        var info = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = !window,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        if (workingDirectory != null) info.WorkingDirectory = workingDirectory;
        return Process.Start(info);
    }

    /// Raw command line (cmd.exe and Windows Terminal parse their own arguments).
    public static Process? StartRaw(string file, string arguments, string? workingDirectory = null)
    {
        var info = new ProcessStartInfo(file, arguments) { UseShellExecute = false };
        if (workingDirectory != null) info.WorkingDirectory = workingDirectory;
        return Process.Start(info);
    }
}
