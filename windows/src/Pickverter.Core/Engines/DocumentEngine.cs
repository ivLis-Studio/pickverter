namespace Pickverter;

/// Text documents: DOCX, ODT, RTF, HTML, Markdown and plain text through one document model.
/// PDFs are printed by Microsoft Edge (or exported by Word / LibreOffice when they are installed).
public static class DocumentEngine
{
    public static async Task<Doc> ReadAsync(string path, CancellationToken token = default)
    {
        var ext = FileNames.NormalizedExt(path);
        var title = FileNames.Base(path);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        switch (ext)
        {
            case "md":
                return Markdown.Parse(FileSystem.ReadText(path), directory, title);
            case "txt":
                return Doc.FromText(FileSystem.ReadText(path), title);
            case "html":
                return Html.Parse(FileSystem.ReadText(path), directory, title);
            case "docx":
                return Docx.Read(path, title);
            case "odt":
                return Odt.Read(path, title);
            case "rtf":
                return Rtf.Read(File.ReadAllText(path, Encoding.Latin1), title);
            case "doc":
            {
                var folder = FileSystem.TempFolder("pickverter-doc");
                try
                {
                    var docx = Path.Combine(folder, title + ".docx");
                    await ExportLegacy(path, docx, "docx", token);
                    return Docx.Read(docx, title);
                }
                finally
                {
                    FileSystem.TryDelete(folder);
                }
            }
            default:
                throw new PickException(L("err.not_convertible"));
        }
    }

    private static bool HasWord => Platform.Office.Has(OfficeApp.Word);

    public static string? Unavailable(string source, string target)
    {
        if (source == "doc" && !HasWord && Tools.LibreOffice == null) return L("err.needs_app", "Microsoft Word · LibreOffice");
        if (target == "pdf" && !Platform.Printer.Available && !HasWord && Tools.LibreOffice == null) return L("err.edge_missing");
        return null;
    }

    /// Word (or LibreOffice) opens .doc files and makes the best PDFs of Word documents.
    private static async Task ExportLegacy(string source, string destination, string target, CancellationToken token)
    {
        if (HasWord)
        {
            await Platform.Office.ExportAsync(OfficeApp.Word, source, destination, target, token);
            return;
        }
        if (Tools.LibreOffice is { } soffice)
        {
            await OfficeEngine.ViaLibreOffice(soffice, source, target, destination, token);
            return;
        }
        throw new PickException(L("err.needs_app", "Microsoft Word · LibreOffice"));
    }

    public static void Write(Doc doc, string path, string ext)
    {
        switch (ext)
        {
            case "txt":
                FileSystem.WriteText(path, doc.PlainText());
                break;
            case "md":
                FileSystem.WriteText(path, Markdown.Write(doc));
                break;
            case "html":
                FileSystem.WriteText(path, Html.Document(doc));
                break;
            case "rtf":
                File.WriteAllText(path, Rtf.Write(doc), Encoding.ASCII);
                break;
            case "docx":
                Docx.Write(doc, path);
                break;
            case "odt":
                Odt.Write(doc, path);
                break;
            default:
                throw new PickException(L("err.cant_save_as", ext.ToUpperInvariant()));
        }
    }

    /// HTML -> PDF with Edge, or LibreOffice when Edge isn't there.
    public static async Task PrintPdf(Doc doc, string destination, CancellationToken token)
    {
        var folder = Path.GetDirectoryName(destination) ?? Path.GetTempPath();
        var html = Path.Combine(folder, ".pickverter-print-" + Guid.NewGuid().ToString("N")[..8] + ".html");
        try
        {
            FileSystem.WriteText(html, Html.Document(doc));
            if (Platform.Printer.Available)
            {
                await Platform.Printer.PrintAsync(html, destination, token);
                return;
            }
            if (Tools.LibreOffice is { } soffice)
            {
                await OfficeEngine.ViaLibreOffice(soffice, html, "pdf", destination, token);
                return;
            }
            throw new PickException(L("err.edge_missing"));
        }
        finally
        {
            FileSystem.TryDelete(html);
        }
    }

    public static async Task<string> Convert(string src, string ext, PickContext ctx)
    {
        var source = FileNames.NormalizedExt(src);
        var destination = ctx.DestinationFor(src, ext);
        if (source == "doc" || (ext == "pdf" && HasWord && source is "docx" or "rtf" or "odt"))
        {
            ctx.Report(null, L("note.opening_in", HasWord ? "Microsoft Word" : "LibreOffice"));
            if (source == "doc" && ext is not ("pdf" or "docx" or "odt" or "rtf" or "txt" or "html"))
            {
                var doc = await ReadAsync(src, ctx.Token);
                return await ctx.Produce(destination, tmp => Write(doc, tmp, ext));
            }
            return await ctx.Produce(destination, tmp => ExportLegacy(src, tmp, ext, ctx.Token));
        }
        var parsed = await ReadAsync(src, ctx.Token);
        if (ext == "pdf") return await ctx.Produce(destination, tmp => PrintPdf(parsed, tmp, ctx.Token));
        return await ctx.Produce(destination, tmp => Write(parsed, tmp, ext));
    }

    private static async Task<string> AllText(IReadOnlyList<string> files, CancellationToken token)
    {
        var chunks = new List<string>();
        foreach (var file in files) chunks.Add((await ReadAsync(file, token)).PlainText().Trim());
        return string.Join("\n\n", chunks.Where(chunk => chunk.Length > 0));
    }

    public static async Task<PickResult> CopyText(PickContext ctx)
    {
        var text = await AllText(ctx.Files, ctx.Token);
        return await CopyText(text);
    }

    /// Puts text read from documents on the clipboard (Windows line endings).
    public static async Task<PickResult> CopyText(string text)
    {
        if (text.Length == 0) throw new PickException(L("err.no_text"));
        await Platform.Clipboard.SetTextAsync(text.Replace("\r\n", "\n").Replace("\n", "\r\n"));
        return PickResult.Note(LP("msg.chars_copied", text.Length));
    }

    public static async Task<PickResult> Count(PickContext ctx) => Count(await AllText(ctx.Files, ctx.Token));

    /// Characters (without and with spaces), words and lines.
    public static PickResult Count(string text)
    {
        var characters = text.Count(c => !char.IsWhiteSpace(c));
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(line => line.Trim().Length > 0);
        return PickResult.Note(L("msg.count", Loc.FormatNumber(characters), Loc.FormatNumber(text.Length), Loc.FormatNumber(words), Loc.FormatNumber(lines)));
    }
}
