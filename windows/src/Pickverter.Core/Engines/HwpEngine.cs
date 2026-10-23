using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Pickverter;

/// HWP and HWPX documents (Hancom Office's Hangul Word Processor) through rhwp, the open-source HWP engine Pickverter
/// downloads on first use (HwpSetup). rhwp lays pages out the way Hangul does, so PDFs and page images come straight from
/// it, and so does HWP ↔ HWPX. DOCX, HTML, Markdown and plain text start from its DocLang export, read into the shared
/// document model.
public static class HwpEngine
{
    public static IReadOnlyList<string> Targets(string source) =>
        ["pdf", "docx", source == "hwpx" ? "hwp" : "hwpx", "png", "jpg", "txt", "md", "html"];

    // Converting

    public static async Task<string> Convert(string src, string ext, double dpi, PickContext ctx)
    {
        switch (ext)
        {
            case "pdf":
                return await Unlocking(ctx, src, () => ctx.Produce(ctx.DestinationFor(src, "pdf"), async tmp =>
                {
                    ctx.Report(null, L("note.rendering"));
                    await Rhwp(ctx, src, ["export-pdf", src, "-o", tmp]);
                }));
            case "png":
            case "jpg":
            {
                // rhwp lays the pages out into a PDF; the images are named after the document and land next to it.
                var scratch = FileSystem.TempFolder("pickverter-hwp");
                try
                {
                    var pdf = Path.Combine(scratch, "document.pdf");
                    await Unlocking(ctx, src, async () =>
                    {
                        ctx.Report(null, L("note.rendering"));
                        await Rhwp(ctx, src, ["export-pdf", src, "-o", pdf]);
                        return pdf;
                    });
                    return await PdfEngine.ToImages(pdf, ext, dpi, ctx, nameFrom: src);
                }
                finally
                {
                    FileSystem.TryDelete(scratch);
                }
            }
            case "hwpx":
            case "hwp":
            {
                // export-hwpx writes HWPX; convert writes HWP (and wants the .hwp name, which the staged path keeps).
                var command = ext == "hwpx" ? "export-hwpx" : "convert";
                return await Unlocking(ctx, src, () => ctx.Produce(ctx.DestinationFor(src, ext), tmp => Rhwp(ctx, src, [command, src, tmp])));
            }
            default:
            {
                var doc = await ReadAsync(src, ctx);
                return await ctx.Produce(ctx.DestinationFor(src, ext), tmp => DocumentEngine.Write(doc, tmp, ext));
            }
        }
    }

    // Text

    /// The document model from rhwp's DocLang export. DocLang opens neither locked nor distribution (read-only)
    /// documents, nor HWP 3.x files: rhwp first rewrites those as a plain HWPX in the scratch folder (asking for the
    /// password when there is one), and DocLang reads that. Pictures come inline in the XML.
    public static async Task<Doc> ReadAsync(string src, PickContext ctx)
    {
        var scratch = FileSystem.TempFolder("pickverter-hwp");
        try
        {
            var input = "document." + (FileNames.NormalizedExt(src) == "hwpx" ? "hwpx" : "hwp");
            File.Copy(src, Path.Combine(scratch, input));
            ctx.Report(null, L("note.reading"));
            if (!await TryRhwp(ctx, ["export-doclang", input, "-o", "document.xml"], scratch))
            {
                await Unlocking(ctx, src, async () =>
                {
                    await Rhwp(ctx, src, ["export-hwpx", input, "unlocked.hwpx"], scratch);
                    return true;
                });
                await Rhwp(ctx, null, ["export-doclang", "unlocked.hwpx", "-o", "document.xml"], scratch);
            }
            var xml = Path.Combine(scratch, "document.xml");
            if (!File.Exists(xml)) throw new PickException(L("err.hwp_open"));
            return DocLang.Read(xml, scratch, FileNames.Base(src));
        }
        finally
        {
            FileSystem.TryDelete(scratch);
        }
    }

    private static async Task<string> AllText(PickContext ctx)
    {
        var chunks = new List<string>();
        for (var index = 0; index < ctx.Files.Count; index++)
        {
            ctx.Check();
            ctx.Report((double)index / ctx.Files.Count, L("note.reading"));
            chunks.Add((await ReadAsync(ctx.Files[index], ctx)).PlainText().Trim());
        }
        var text = string.Join("\n\n", chunks.Where(chunk => chunk.Length > 0));
        if (text.Length == 0) throw new PickException(L("err.no_text"));
        return text;
    }

    public static async Task<PickResult> CopyText(PickContext ctx) => await DocumentEngine.CopyText(await AllText(ctx));

    public static async Task<PickResult> Count(PickContext ctx) => DocumentEngine.Count(await AllText(ctx));

    // Running rhwp

    private static string Executable() => HwpSetup.Executable ?? throw new PickException(HwpSetup.Unavailable() ?? Tools.HwpHint);

    private enum Problem { NeedsPassword, WrongPassword, Other }

    /// rhwp explains failures in Korean on stderr; the password ones are the only ones Pickverter can fix.
    private static Problem Classify(string stderr)
    {
        if (stderr.Contains("일치하지 않", StringComparison.Ordinal)) return Problem.WrongPassword;
        if (stderr.Contains("비밀번호", StringComparison.Ordinal) || stderr.Contains("encrypt", StringComparison.OrdinalIgnoreCase)) return Problem.NeedsPassword;
        return Problem.Other;
    }

    /// rhwp stopped at a locked document: no password yet, or the last one didn't match.
    private sealed class PasswordNeeded(bool wrong) : Exception(L("hwp.password.title"))
    {
        public bool Wrong { get; } = wrong;
    }

    /// Runs rhwp on one document. The password typed for it in this job goes in through standard input (never on the
    /// command line). A locked document stops the run with PasswordNeeded, so the question comes from Unlocking.
    /// document: null for a copy that needs no password.
    private static async Task Rhwp(PickContext ctx, string? document, IReadOnlyList<string> arguments, string? cwd = null)
    {
        var password = document == null ? null : Known(ctx, document);
        try
        {
            await Shell.RunAsync(Executable(), password == null ? arguments : [.. arguments, "--password-stdin"], cwd, token: ctx.Token,
                                 input: password == null ? null : password + "\n");
        }
        catch (ToolFailure failure)
        {
            var problem = Classify(failure.Stderr);
            if (document != null && problem != Problem.Other) throw new PasswordNeeded(problem == Problem.WrongPassword);
            Debug.Log("rhwp " + arguments[0] + ": " + Tail(failure.Stderr));
            throw new PickException(L("err.hwp_open"));
        }
    }

    /// A first attempt whose failure has a plan B (DocLang before the plain-HWPX detour).
    private static async Task<bool> TryRhwp(PickContext ctx, IReadOnlyList<string> arguments, string cwd)
    {
        try
        {
            await Shell.RunAsync(Executable(), arguments, cwd, token: ctx.Token);
            return true;
        }
        catch (ToolFailure failure)
        {
            Debug.Log("rhwp " + arguments[0] + ": " + Tail(failure.Stderr));
            return false;
        }
    }

    /// Runs work that may meet a locked document and asks for its password between attempts: never while the work holds
    /// a slot in the queue or a half-written result. Three tries, like the Mac.
    private static async Task<T> Unlocking<T>(PickContext ctx, string document, Func<Task<T>> work)
    {
        for (var asked = 0; ; asked++)
        {
            try
            {
                return await work();
            }
            catch (PasswordNeeded locked)
            {
                if (asked >= 3) throw new PickException(L("err.hwp_password"));
                var password = await Platform.Editors.UnlockAsync(document, locked.Wrong, ctx.Token);
                if (string.IsNullOrEmpty(password)) throw new OperationCanceledException();
                Remember(ctx, document, password);
            }
        }
    }

    // Passwords typed during a job, per document, so one conversion never asks twice. They go when the job does.
    private static readonly ConditionalWeakTable<StartSignal, ConcurrentDictionary<string, string>> Passwords = new();

    private static string Key(string path) => Path.GetFullPath(path);

    private static string? Known(PickContext ctx, string document) =>
        Passwords.TryGetValue(ctx.Started, out var known) && known.TryGetValue(Key(document), out var password) ? password : null;

    private static void Remember(PickContext ctx, string document, string password) =>
        Passwords.GetValue(ctx.Started, _ => new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase))[Key(document)] = password;

    private static string Tail(string stderr) => stderr.Length > 800 ? stderr[^800..] : stderr;
}
