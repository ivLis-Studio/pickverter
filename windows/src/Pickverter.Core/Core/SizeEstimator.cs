namespace Pickverter;

public enum EstimateKind { Size, Small, None }

/// What the size preview found: the result's size (and the original's), "already small", or nothing worth showing.
public readonly record struct SizeEstimate(EstimateKind Kind, long Bytes = 0, long Source = 0)
{
    public static SizeEstimate Small => new(EstimateKind.Small);
    public static SizeEstimate Nothing => new(EstimateKind.None);

    /// "−71%" (a minus sign, U+2212), "+12%", "0%".
    public string Change
    {
        get
        {
            if (Source <= 0) return "0%";
            var percent = (long)Math.Round(Math.Abs(Bytes - Source) * 100.0 / Source, MidpointRounding.AwayFromZero);
            var sign = percent == 0 ? "" : Bytes < Source ? "\u2212" : "+";
            return sign + percent.ToString(CultureInfo.InvariantCulture) + "%";
        }
    }

    /// The hub's line: "≈ 420 KB · −71%", "Already small", or nothing.
    public string? Line => Kind switch
    {
        EstimateKind.Size => L("est.result", FileNames.FormatBytes(Bytes), Change),
        EstimateKind.Small => L("est.small"),
        _ => null,
    };
}

/// The wheel's size preview. It runs the highlighted option for one file on the side: results go into a private
/// folder that is deleted right away; no work-queue wait, no progress, no cards, never a question. What came out is
/// remembered per file version and option for the session (64 of them).
public sealed class SizeEstimator
{
    public static SizeEstimator Shared { get; } = new();

    public const long ImageLimit = 80_000_000;
    public const long PdfLimit = 30_000_000;
    private const int Capacity = 64;

    private static readonly HashSet<string> ImageTargets = new(StringComparer.Ordinal) { "jpg", "png", "webp", "avif", "heic", "tiff", "gif", "bmp", "ico" };

    private readonly object gate = new();
    private readonly Dictionary<string, SizeEstimate> cache = new(StringComparer.Ordinal);
    private readonly Queue<string> order = new();
    private readonly SemaphoreSlim running = new(1, 1);

    /// Where the runs put their results (tests point it elsewhere).
    internal static string ScratchRoot { get; set; } = Path.Combine(Path.GetTempPath(), "pickverter-estimates");

    /// Image conversions to other image formats, every image Compress (sizes included) and Resize, PDF Compress — for
    /// files small enough to try quickly. Never an option that asks something first.
    public static bool IsEstimable(PickOption option, PickOption? parent, string file)
    {
        if (option.Interactive || option.Unavailable != null || parent?.Unavailable != null) return false;
        var size = SizeOf(file);
        if (size <= 0) return false;
        var id = option.Id;
        return FileKinds.Of(file) switch
        {
            FileKind.Image => size <= ImageLimit &&
                              (id.StartsWith("tool.compress.", StringComparison.Ordinal) || id.StartsWith("tool.resize.", StringComparison.Ordinal) ||
                               (id.StartsWith("convert.", StringComparison.Ordinal) && (parent?.OutputExt ?? option.OutputExt) is { } ext && ImageTargets.Contains(ext))),
            FileKind.Pdf => size <= PdfLimit && id == "tool.compress",
            _ => false,
        };
    }

    /// tool.compress.max500k -> 500,000 bytes, tool.compress.max1m -> 1,000,000; null for anything else (maxcustom too).
    internal static long? FixedTarget(string id)
    {
        const string prefix = "tool.compress.max";
        if (!id.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var key = id[prefix.Length..];
        if (key.Length < 2 || !long.TryParse(key[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var number)) return null;
        return key[^1] switch
        {
            'k' => number * 1000,
            'm' => number * 1_000_000,
            _ => null,
        };
    }

    /// What is known without running anything: an earlier result, or a fixed "Under N" size (images and videos) the
    /// file is at or under already.
    public SizeEstimate? Known(PickOption option, string file)
    {
        if (Key(option, file) is not { } key) return null;
        lock (gate)
        {
            if (cache.TryGetValue(key, out var known)) return known;
        }
        if (FixedTarget(option.Id) is not { } target || SizeOf(file) > target) return null;
        Store(key, SizeEstimate.Small);
        return SizeEstimate.Small;
    }

    /// Runs the option on the side (one run at a time) unless the answer is known.
    public async Task<SizeEstimate> EstimateAsync(PickOption option, string file, CancellationToken token)
    {
        if (Known(option, file) is { } known) return known;
        await running.WaitAsync(token);
        try
        {
            var key = Key(option, file);
            if (key != null)
            {
                lock (gate)
                {
                    if (cache.TryGetValue(key, out var meanwhile)) return meanwhile;
                }
            }
            var estimate = await Measure(option, file, token);
            if (key != null) Store(key, estimate);
            return estimate;
        }
        finally
        {
            running.Release();
        }
    }

    private static async Task<SizeEstimate> Measure(PickOption option, string file, CancellationToken token)
    {
        var source = SizeOf(file);
        var folder = Path.Combine(ScratchRoot, Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(folder);
        try
        {
            PickResult result;
            try
            {
                result = await Task.Run(() => option.Perform(PickContext.Scratch([file], folder, token)), token);
            }
            catch (PickException error) when (error.Message == L("err.already_small"))
            {
                return SizeEstimate.Small;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Debug.Log($"estimate {option.Id}: {error.Message}");
                return SizeEstimate.Nothing;
            }
            // Compress with nothing to write: under the target already.
            if (result.Outputs.Count == 0) return option.Id.StartsWith("tool.compress", StringComparison.Ordinal) ? SizeEstimate.Small : SizeEstimate.Nothing;
            var bytes = result.Outputs.Sum(FileNames.Size);
            return bytes > 0 ? new SizeEstimate(EstimateKind.Size, bytes, source) : SizeEstimate.Nothing;
        }
        finally
        {
            FileSystem.TryDelete(folder);
        }
    }

    private void Store(string key, SizeEstimate estimate)
    {
        lock (gate)
        {
            if (!cache.ContainsKey(key)) order.Enqueue(key);
            cache[key] = estimate;
            while (cache.Count > Capacity && order.TryDequeue(out var oldest)) cache.Remove(oldest);
        }
    }

    /// The file as it is now (path, size, modification time) and the option.
    private static string? Key(PickOption option, string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists) return null;
            return string.Join('\n', info.FullName, info.Length.ToString(CultureInfo.InvariantCulture),
                info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture), option.Id);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static long SizeOf(string file)
    {
        try
        {
            var info = new FileInfo(file);
            return info.Exists ? info.Length : -1;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    /// Tests: forget everything.
    internal void Clear()
    {
        lock (gate)
        {
            cache.Clear();
            order.Clear();
        }
    }
}
