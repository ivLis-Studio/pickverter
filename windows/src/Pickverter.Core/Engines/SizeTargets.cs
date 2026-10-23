namespace Pickverter;

/// "Under N" compression: targets in decimal units (1 MB = 1,000,000 bytes, the way upload and attachment limits
/// count), their labels, and the batch that runs them.
public static class SizeTargets
{
    public static readonly IReadOnlyList<long> Image = [500_000, 1_000_000, 2_000_000, 5_000_000];
    public static readonly IReadOnlyList<long> Video = [10_000_000, 25_000_000, 50_000_000, 100_000_000];

    /// The variant id: tool.compress.max500k, tool.compress.max1m…
    public static string Key(long bytes) =>
        bytes >= 1_000_000 ? $"max{bytes / 1_000_000}m" : $"max{bytes / 1000}k";

    /// "500 KB", "1 MB", "2.5 MB": decimal units in the byte formatter's style. (The formatter itself counts in
    /// binary units like File Explorer, which would turn "Under 1 MB" into "Under 977 KB".)
    public static string Label(long bytes)
    {
        var culture = Loc.Culture;
        static string Number(double value, CultureInfo culture) => value.ToString(value < 10 ? "0.##" : value < 100 ? "0.#" : "0", culture);
        // A no-break space: "500 KB" never splits across two lines of a wheel label.
        if (bytes >= 1_000_000_000) return Number(bytes / 1e9, culture) + "\u00A0GB";
        if (bytes >= 1_000_000) return Number(bytes / 1e6, culture) + "\u00A0MB";
        if (bytes >= 1_000) return Number(bytes / 1e3, culture) + "\u00A0KB";
        return bytes.ToString("N0", culture) + "\u00A0B";
    }

    /// The custom size dialog's suggestion: half the current size in MB, whole from 10 MB, one decimal from 1 MB, two
    /// below (at least 0.01).
    public static double Suggested(long bytes)
    {
        var half = bytes / 2.0 / 1_000_000;
        if (half >= 10) return Math.Round(half);
        if (half >= 1) return Math.Round(half, 1);
        return Math.Max(0.01, Math.Round(half, 2));
    }

    /// "1.5", "1,5", "1.5 MB", "1,5 МБ" -> 1.5. Null for anything that isn't a positive number.
    public static double? ParseMegabytes(string raw)
    {
        var text = raw.Trim();
        // "1,5 Mo" in French, "1,5 МБ" in Russian.
        if (text.EndsWith("mb", StringComparison.OrdinalIgnoreCase) || text.EndsWith("mo", StringComparison.OrdinalIgnoreCase)
            || text.EndsWith("мб", StringComparison.OrdinalIgnoreCase))
            text = text[..^2].TrimEnd();
        text = text.Replace(',', '.');
        if (!double.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite,
                CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || value <= 0) return null;
        return value;
    }

    public static long Bytes(double megabytes) => Math.Max(1, (long)Math.Round(megabytes * 1_000_000));

    /// One file's result: the output (null: it was under the target already) and, when the target couldn't be
    /// reached, the note that says so.
    public sealed record Fit(string? Output, string? Missed)
    {
        public static Fit AlreadyUnder { get; } = new(null, null);
    }

    public delegate Task<Fit> Fitter(string source, long target, PickContext ctx);

    public static string Missed(long target, long result) => L("msg.target_missed", Label(target), Label(result));

    public static PickAction Under(long target, Fitter fit) => ctx => RunAll(ctx, target, fit);

    /// "Custom Size…": asks once (for the first file), then applies the size to every selected file.
    public static PickAction Custom(Fitter fit) => async ctx =>
    {
        var first = ctx.Files[0];
        var megabytes = await Platform.Editors.SizeAsync(first, FileNames.Size(first), ctx.Token);
        ctx.BeginWork();
        return await RunAll(ctx, Bytes(megabytes), fit);
    };

    /// Like Batch.PerFile, plus: files under the target already are left alone (all of them: a note says so) and a
    /// missed target is reported under the result.
    private static async Task<PickResult> RunAll(PickContext ctx, long target, Fitter fit)
    {
        var outputs = new List<string>();
        var failures = new List<string>();
        string? missed = null;
        var count = ctx.Files.Count;
        for (var index = 0; index < count; index++)
        {
            ctx.Check();
            var file = ctx.Files[index];
            if (count > 1) ctx.Report((double)index / count, $"{index + 1}/{count} · {FileNames.Name(file)}");
            try
            {
                var result = await fit(file, target, ctx.Slice(index, count));
                if (result.Output is not { } output) continue;
                outputs.Add(output);
                missed ??= result.Missed;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                if (ctx.Token.IsCancellationRequested) throw new OperationCanceledException(ctx.Token);
                if (count == 1) throw;
                failures.Add($"{FileNames.Name(file)}: {Errors.Describe(error)}");
            }
        }
        if (outputs.Count == 0)
        {
            if (failures.Count > 0) throw new PickException(failures[0]);
            return PickResult.Note(L("msg.already_under", Label(target)));
        }
        var notes = new List<string>();
        if (failures.Count > 0) notes.Add(LP("job.failed_some", failures.Count, failures[0]));
        if (missed != null) notes.Add(missed);
        return new PickResult { Outputs = outputs, Message = notes.Count > 0 ? string.Join("\n", notes) : null };
    }
}
