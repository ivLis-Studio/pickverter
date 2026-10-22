using System.IO.Compression;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pickverter;

/// Excel, PowerPoint, OpenDocument and iWork files.
///  • Built in: XLSX -> CSV/TSV/JSON/MD/HTML, PPTX -> Markdown/text, media extraction.
///  • With Microsoft Office or LibreOffice installed: faithful PDF, XLSX, PPTX and slide-image exports.
public static partial class OfficeEngine
{
    public static readonly HashSet<string> Exts = new(StringComparer.OrdinalIgnoreCase)
    {
        "xlsx", "xlsm", "xls", "pptx", "ppsx", "ppt", "numbers", "pages", "key", "ods", "odp",
    };

    public static bool IsZipBased(string ext) =>
        ext is "xlsx" or "xlsm" or "pptx" or "ppsx" or "ods" or "odp" or "numbers" or "pages" or "key" or "docx" or "odt";

    public static IReadOnlyList<string> Targets(string ext) => ext switch
    {
        "xlsx" or "xlsm" => ["csv", "tsv", "json", "md", "html", "pdf", "png"],
        "xls" => ["xlsx", "csv", "pdf", "png"],
        "pptx" or "ppsx" => ["pdf", "md", "txt", "png"],
        "ppt" => ["pptx", "pdf", "png"],
        "numbers" => ["xlsx", "csv", "pdf"],
        "pages" => ["pdf", "docx", "txt"],
        "key" => ["pdf", "pptx"],
        "ods" => ["xlsx", "csv", "pdf"],
        "odp" => ["pptx", "pdf"],
        _ => [],
    };

    private static bool IsBuiltIn(string source, string target) => source switch
    {
        "xlsx" or "xlsm" => target is "csv" or "tsv" or "json" or "md" or "html",
        "pptx" or "ppsx" => target is "md" or "txt",
        _ => false,
    };

    private static OfficeApp? AppFor(string source) => source switch
    {
        "xls" or "xlsx" or "xlsm" or "ods" => OfficeApp.Excel,
        "ppt" or "pptx" or "ppsx" or "odp" => OfficeApp.PowerPoint,
        _ => null,
    };

    private static bool OfficeSupports(OfficeApp app, string target) => app switch
    {
        OfficeApp.Excel => target is "pdf" or "xlsx" or "csv" or "png",
        OfficeApp.PowerPoint => target is "pdf" or "pptx" or "png",
        _ => target is "pdf" or "docx" or "rtf" or "txt" or "html" or "odt",
    };

    public abstract record Route
    {
        public sealed record BuiltIn : Route;
        public sealed record Office(OfficeApp App) : Route;
        public sealed record Libre(string Path) : Route;
    }

    public static Route? RouteFor(string source, string target)
    {
        if (IsBuiltIn(source, target)) return new Route.BuiltIn();
        if (AppFor(source) is { } app && Platform.Office.Has(app) && OfficeSupports(app, target)) return new Route.Office(app);
        if (Tools.LibreOffice is { } soffice) return new Route.Libre(soffice);
        return null;
    }

    public static string? Unavailable(string source, string target)
    {
        if (RouteFor(source, target) != null) return null;
        var app = AppFor(source) switch
        {
            OfficeApp.Excel => "Microsoft Excel · ",
            OfficeApp.PowerPoint => "Microsoft PowerPoint · ",
            _ => "",
        };
        return L("err.needs_app", app + "LibreOffice");
    }

    private static string AppName(OfficeApp app) => app switch
    {
        OfficeApp.Excel => "Microsoft Excel",
        OfficeApp.PowerPoint => "Microsoft PowerPoint",
        _ => "Microsoft Word",
    };

    public static async Task<string> Convert(string src, string ext, PickContext ctx)
    {
        var source = FileNames.NormalizedExt(src);
        var route = RouteFor(source, ext) ?? throw new PickException(Unavailable(source, ext) ?? L("err.not_convertible"));
        switch (route)
        {
            case Route.BuiltIn:
                if (source.StartsWith("pp", StringComparison.Ordinal))
                {
                    var slides = ReadSlides(src);
                    var text = ext == "md" ? SlidesMarkdown(slides, FileNames.Base(src)) : SlidesText(slides);
                    return await ctx.Produce(ctx.DestinationFor(src, ext), tmp => FileSystem.WriteText(tmp, text));
                }
                return await ConvertWorkbook(src, ext, ctx);
            case Route.Office office:
                ctx.Report(null, L("note.opening_in", AppName(office.App)));
                if (ext == "png" && office.App == OfficeApp.PowerPoint)
                {
                    return await ctx.Produce(ctx.DestinationFor(src, null, "PNG"),
                        folder => Platform.Office.ExportAsync(office.App, src, folder, "png", ctx.Token));
                }
                if (ext == "png") return await FirstPagePng(src, ctx, tmp => Platform.Office.ExportAsync(office.App, src, tmp, "pdf", ctx.Token));
                return await ctx.Produce(ctx.DestinationFor(src, ext), tmp => Platform.Office.ExportAsync(office.App, src, tmp, ext, ctx.Token));
            case Route.Libre libre:
                ctx.Report(null, L("note.opening_in", "LibreOffice"));
                if (ext == "png") return await FirstPagePng(src, ctx, tmp => ViaLibreOffice(libre.Path, src, "pdf", tmp, ctx.Token));
                return await ctx.Produce(ctx.DestinationFor(src, ext), tmp => ViaLibreOffice(libre.Path, src, ext, tmp, ctx.Token));
            default:
                throw new PickException(L("err.not_convertible"));
        }
    }

    /// Exports a PDF to a scratch file, then renders its first page.
    private static async Task<string> FirstPagePng(string src, PickContext ctx, Func<string, Task> exportPdf)
    {
        if (!Platform.Pdf.Available) throw new PickException(L("err.no_preview"));
        return await ctx.Produce(ctx.DestinationFor(src, "png"), async tmp =>
        {
            var pdf = tmp + ".pv.pdf";
            try
            {
                await exportPdf(pdf);
                await Platform.Pdf.RenderPageAsync(pdf, 0, 150, tmp, ctx.Token);
            }
            finally
            {
                FileSystem.TryDelete(pdf);
            }
        });
    }

    public static async Task ViaLibreOffice(string soffice, string src, string target, string destination, CancellationToken token)
    {
        var outDir = Path.Combine(Path.GetDirectoryName(destination) ?? Path.GetTempPath(), "lo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outDir);
        try
        {
            var profile = Path.Combine(Path.GetTempPath(), "pickverter-libreoffice");
            var filter = target switch
            {
                "txt" => "txt:Text (encoded):UTF8",
                "csv" => "csv:Text - txt - csv (StarCalc):44,34,76,1",
                _ => target,
            };
            await Shell.RunAsync(soffice, ["-env:UserInstallation=" + new Uri(profile + Path.DirectorySeparatorChar).AbsoluteUri, "--headless", "--norestore",
                "--convert-to", filter, "--outdir", outDir, src], token: token);
            var produced = Directory.GetFiles(outDir).FirstOrDefault() ?? throw new PickException(L("err.no_result"));
            File.Move(produced, destination, overwrite: true);
        }
        finally
        {
            FileSystem.TryDelete(outDir);
        }
    }

    // Packages

    private static ZipArchive OpenPackage(string src)
    {
        try
        {
            return ZipFile.OpenRead(src);
        }
        catch (InvalidDataException)
        {
            throw new PickException(L("err.office_damaged"));
        }
    }

    private static readonly HashSet<string> MediaExts = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpg", "jpeg", "gif", "tif", "tiff", "bmp", "heic", "webp", "svg", "emf", "wmf", "mp4", "mov", "m4v", "mp3", "m4a", "wav", "aiff", "pdf",
    };

    public static async Task<string> ExtractMedia(string src, PickContext ctx)
    {
        ctx.Report(null, L("note.extracting"));
        using var zip = OpenPackage(src);
        string[] folders = ["word/media/", "xl/media/", "ppt/media/", "Data/", "Pictures/", "media/"];
        var found = zip.Entries.Where(entry => entry.Length > 0 && folders.Any(folder => entry.FullName.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) &&
                                               MediaExts.Contains(Path.GetExtension(entry.Name).TrimStart('.')) &&
                                               !Path.GetFileNameWithoutExtension(entry.Name).EndsWith("-small", StringComparison.Ordinal)).ToList();
        if (found.Count == 0) throw new PickException(L("err.no_media"));
        return await ctx.Produce(ctx.DestinationFor(src, null, L("sfx.media")), folder =>
        {
            Directory.CreateDirectory(folder);
            foreach (var entry in found)
            {
                var target = FileNames.Unique(Path.Combine(folder, FileNames.Sanitize(entry.Name)));
                entry.ExtractToFile(target);
            }
        });
    }

    public static async Task<PickResult> CopyText(PickContext ctx)
    {
        var chunks = new List<string>();
        foreach (var file in ctx.Files)
        {
            var ext = FileNames.NormalizedExt(file);
            if (ext.StartsWith("ppt", StringComparison.Ordinal))
            {
                chunks.Add(SlidesText(ReadSlides(file)));
            }
            else if (ext.StartsWith("xls", StringComparison.Ordinal))
            {
                var sheets = ReadWorkbook(file);
                chunks.Add(string.Join("\n\n", sheets.Select(sheet =>
                    (sheets.Count > 1 ? sheet.Name + "\n" : "") + string.Join("\n", sheet.Rows.Select(row => string.Join("\t", row))))));
            }
        }
        var text = string.Join("\n\n", chunks).Trim();
        if (text.Length == 0) throw new PickException(L("err.no_text"));
        await Platform.Clipboard.SetTextAsync(text.Replace("\n", "\r\n"));
        return PickResult.Note(LP("msg.chars_copied", text.Length));
    }

    // XLSX

    public sealed record Sheet(string Name, List<List<string>> Rows);

    private static Dictionary<string, string> Relationships(ZipArchive zip, string name)
    {
        var map = new Dictionary<string, string>();
        var entry = zip.GetEntry(name);
        if (entry == null) return map;
        using var stream = entry.Open();
        Xml.Walk(stream, start: (element, attributes) =>
        {
            if (element == "Relationship" && attributes.TryGetValue("Id", out var id) && attributes.TryGetValue("Target", out var target)) map[id] = target;
        });
        return map;
    }

    private static string Resolve(string target, string folder) => target.StartsWith('/') ? target[1..] : folder + target;

    private static void Walk(ZipArchive zip, string name, Action<string, IReadOnlyDictionary<string, string>>? start = null, Action<string>? end = null,
                             Action<string>? text = null)
    {
        var entry = zip.GetEntry(name);
        if (entry == null) return;
        using var stream = entry.Open();
        Xml.Walk(stream, start, end, text);
    }

    public static int? ColumnIndex(string reference)
    {
        var value = 0;
        var seen = false;
        foreach (var c in reference)
        {
            if (c is < 'A' or > 'Z') break;
            value = value * 26 + (c - 'A' + 1);
            seen = true;
        }
        return seen ? value - 1 : null;
    }

    private static readonly HashSet<int> BuiltInDateFormats = [.. Enumerable.Range(14, 9), .. Enumerable.Range(27, 10), 45, 46, 47, .. Enumerable.Range(50, 9)];

    [GeneratedRegex("\"[^\"]*\"|\\[[^\\]]*\\]|\\\\.")]
    private static partial Regex FormatNoise();

    public static bool IsDateFormat(string code)
    {
        var cleaned = FormatNoise().Replace(code.ToLowerInvariant(), "");
        if (cleaned == "general") return false;
        return cleaned.Contains('y') || cleaned.Contains('d') || cleaned.Contains('h') || cleaned.Contains('s') || (cleaned.Contains('m') && !cleaned.Contains('0'));
    }

    public static List<Sheet> ReadWorkbook(string src)
    {
        using var zip = OpenPackage(src);
        var sheetRefs = new List<(string Name, string Rid)>();
        var date1904 = false;
        Walk(zip, "xl/workbook.xml", start: (name, attributes) =>
        {
            if (name == "sheet" && attributes.TryGetValue("name", out var sheetName))
            {
                var rid = attributes.TryGetValue("r:id", out var id) ? id : attributes.FirstOrDefault(pair => pair.Key.EndsWith(":id", StringComparison.Ordinal)).Value;
                if (rid != null) sheetRefs.Add((sheetName, rid));
            }
            else if (name == "workbookPr" && attributes.TryGetValue("date1904", out var flag))
            {
                date1904 = flag is "1" or "true";
            }
        });
        if (sheetRefs.Count == 0) throw new PickException(L("err.office_damaged"));
        var rels = Relationships(zip, "xl/_rels/workbook.xml.rels");

        var shared = new List<string>();
        var current = new StringBuilder();
        bool inItem = false, inText = false;
        var skip = 0;
        Walk(zip, "xl/sharedStrings.xml", start: (name, _) =>
        {
            switch (name)
            {
                case "si": inItem = true; current.Clear(); break;
                case "t": inText = true; break;
                case "rPh": skip++; break;
            }
        }, end: name =>
        {
            switch (name)
            {
                case "si": shared.Add(current.ToString()); inItem = false; break;
                case "t": inText = false; break;
                case "rPh": skip--; break;
            }
        }, text: value =>
        {
            if (inItem && inText && skip == 0) current.Append(value);
        });

        var formats = new Dictionary<int, string>();
        var styles = new List<int>();
        var inCellXfs = false;
        Walk(zip, "xl/styles.xml", start: (name, attributes) =>
        {
            if (name == "numFmt" && int.TryParse(attributes.GetValueOrDefault("numFmtId"), out var id) && attributes.TryGetValue("formatCode", out var code)) formats[id] = code;
            if (name == "cellXfs") inCellXfs = true;
            if (name == "xf" && inCellXfs) styles.Add(int.TryParse(attributes.GetValueOrDefault("numFmtId"), out var format) ? format : 0);
        }, end: name =>
        {
            if (name == "cellXfs") inCellXfs = false;
        });
        (bool IsDate, bool HasTime) DateStyle(int? style)
        {
            if (style is not { } index || index < 0 || index >= styles.Count) return (false, false);
            var id = styles[index];
            if (BuiltInDateFormats.Contains(id)) return (true, id is 18 or 19 or 20 or 21 or 22 or 45 or 46 or 47);
            if (!formats.TryGetValue(id, out var code) || !IsDateFormat(code)) return (false, false);
            var lower = code.ToLowerInvariant();
            return (true, lower.Contains('h') || lower.Contains('s'));
        }
        var epoch = date1904 ? new DateTime(1904, 1, 1) : new DateTime(1899, 12, 30);

        var sheets = new List<Sheet>();
        foreach (var (sheetName, rid) in sheetRefs)
        {
            if (!rels.TryGetValue(rid, out var target)) continue;
            var cells = new Dictionary<int, Dictionary<int, string>>();
            int rowIndex = -1, column = -1;
            var type = "";
            int? style = null;
            var value = new StringBuilder();
            bool inValue = false, inInline = false;
            Walk(zip, Resolve(target, "xl/"), start: (name, attributes) =>
            {
                switch (name)
                {
                    case "row":
                        rowIndex = (int.TryParse(attributes.GetValueOrDefault("r"), out var r) ? r : rowIndex + 2) - 1;
                        column = -1;
                        break;
                    case "c":
                        column = attributes.TryGetValue("r", out var reference) && ColumnIndex(reference) is { } index ? index : column + 1;
                        type = attributes.GetValueOrDefault("t") ?? "n";
                        style = int.TryParse(attributes.GetValueOrDefault("s"), out var s) ? s : null;
                        value.Clear();
                        break;
                    case "v": inValue = true; break;
                    case "is": inInline = true; break;
                }
            }, end: name =>
            {
                switch (name)
                {
                    case "v": inValue = false; break;
                    case "is": inInline = false; break;
                    case "c":
                    {
                        var raw = value.ToString();
                        var text = raw;
                        switch (type)
                        {
                            case "s":
                                text = int.TryParse(raw, out var sharedIndex) && sharedIndex >= 0 && sharedIndex < shared.Count ? shared[sharedIndex] : "";
                                break;
                            case "b":
                                text = raw == "1" ? "TRUE" : "FALSE";
                                break;
                            case "str" or "inlineStr" or "e":
                                break;
                            default:
                                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                                {
                                    var date = DateStyle(style);
                                    if (date.IsDate)
                                    {
                                        var moment = epoch.AddSeconds(Math.Round(number * 86_400));
                                        text = moment.ToString(date.HasTime ? (number < 1 ? "HH:mm:ss" : "yyyy-MM-dd HH:mm:ss") : "yyyy-MM-dd", CultureInfo.InvariantCulture);
                                    }
                                    else if (number == Math.Round(number) && Math.Abs(number) < 1e15)
                                    {
                                        text = ((long)number).ToString(CultureInfo.InvariantCulture);
                                    }
                                    else
                                    {
                                        text = number.ToString("R", CultureInfo.InvariantCulture);
                                    }
                                }
                                break;
                        }
                        if (text.Length > 0 && rowIndex >= 0 && column >= 0)
                        {
                            if (!cells.TryGetValue(rowIndex, out var row)) cells[rowIndex] = row = new Dictionary<int, string>();
                            row[column] = text;
                        }
                        break;
                    }
                }
            }, text: chunk =>
            {
                if (inValue || inInline) value.Append(chunk);
            });
            if (cells.Count == 0) continue;
            var maxRow = cells.Keys.Max();
            var maxColumn = cells.Values.Where(row => row.Count > 0).Max(row => row.Keys.Max());
            var rows = Enumerable.Range(0, maxRow + 1).Select(row =>
                Enumerable.Range(0, maxColumn + 1).Select(col => cells.TryGetValue(row, out var line) && line.TryGetValue(col, out var cell) ? cell : "").ToList()).ToList();
            sheets.Add(new Sheet(sheetName, rows));
        }
        if (sheets.Count == 0) throw new PickException(L("err.table_empty"));
        return sheets;
    }

    public static async Task<string> ConvertWorkbook(string src, string ext, PickContext ctx)
    {
        ctx.Report(null, L("note.reading"));
        var sheets = ReadWorkbook(src);
        if (sheets.Count == 1)
        {
            var text = DataEngine.WriteTable(sheets[0].Rows, ext);
            return await ctx.Produce(ctx.DestinationFor(src, ext), tmp => FileSystem.WriteText(tmp, text));
        }
        switch (ext)
        {
            case "csv":
            case "tsv":
                return await ctx.Produce(ctx.DestinationFor(src, null, ext.ToUpperInvariant()), folder =>
                {
                    Directory.CreateDirectory(folder);
                    foreach (var sheet in sheets)
                    {
                        var file = FileNames.Unique(Path.Combine(folder, FileNames.Sanitize(sheet.Name) + "." + ext));
                        FileSystem.WriteText(file, DataEngine.WriteTable(sheet.Rows, ext));
                    }
                });
            case "json":
            {
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
                {
                    writer.WriteStartObject();
                    foreach (var sheet in sheets)
                    {
                        if (sheet.Rows.Count == 0) continue;
                        var keys = DataEngine.Uniqued(sheet.Rows[0]);
                        writer.WriteStartArray(sheet.Name);
                        foreach (var row in sheet.Rows.Skip(1))
                        {
                            writer.WriteStartObject();
                            for (var index = 0; index < keys.Count; index++) writer.WriteString(keys[index], index < row.Count ? row[index] : "");
                            writer.WriteEndObject();
                        }
                        writer.WriteEndArray();
                    }
                    writer.WriteEndObject();
                }
                var json = Encoding.UTF8.GetString(stream.ToArray()) + "\n";
                return await ctx.Produce(ctx.DestinationFor(src, "json"), tmp => FileSystem.WriteText(tmp, json));
            }
            default:
            {
                var markdown = string.Join("\n", sheets.Select(sheet => "## " + sheet.Name + "\n\n" + DataEngine.WriteTable(sheet.Rows, "md")));
                var text = ext == "md" ? markdown : Html.Document(Markdown.Parse(markdown, null, FileNames.Base(src)));
                return await ctx.Produce(ctx.DestinationFor(src, ext), tmp => FileSystem.WriteText(tmp, text));
            }
        }
    }

    // PPTX

    public sealed record Slide(string? Title, List<string> Paragraphs);

    public static List<Slide> ReadSlides(string src)
    {
        using var zip = OpenPackage(src);
        var order = new List<string>();
        Walk(zip, "ppt/presentation.xml", start: (name, attributes) =>
        {
            if (name == "sldId")
            {
                var rid = attributes.TryGetValue("r:id", out var id) ? id : attributes.FirstOrDefault(pair => pair.Key.EndsWith(":id", StringComparison.Ordinal)).Value;
                if (rid != null) order.Add(rid);
            }
        });
        var rels = Relationships(zip, "ppt/_rels/presentation.xml.rels");
        var slides = new List<Slide>();
        foreach (var rid in order)
        {
            if (!rels.TryGetValue(rid, out var target)) continue;
            string? title = null;
            var paragraphs = new List<string>();
            var shapeDepth = 0;
            var shapeIsTitle = false;
            var shapeParagraphs = new List<string>();
            var paragraph = new StringBuilder();
            var inText = false;
            Walk(zip, Resolve(target, "ppt/"), start: (name, attributes) =>
            {
                switch (name)
                {
                    case "sp":
                        shapeDepth++;
                        if (shapeDepth == 1)
                        {
                            shapeIsTitle = false;
                            shapeParagraphs = [];
                        }
                        break;
                    case "ph":
                        if (attributes.GetValueOrDefault("type") is "title" or "ctrTitle") shapeIsTitle = true;
                        break;
                    case "p": paragraph.Clear(); break;
                    case "t": inText = true; break;
                    case "br": paragraph.Append('\n'); break;
                }
            }, end: name =>
            {
                switch (name)
                {
                    case "t": inText = false; break;
                    case "p":
                    {
                        var line = paragraph.ToString().Trim();
                        if (line.Length > 0)
                        {
                            if (shapeDepth > 0) shapeParagraphs.Add(line); else paragraphs.Add(line);
                        }
                        break;
                    }
                    case "sp":
                        if (shapeDepth == 1)
                        {
                            if (shapeIsTitle && title == null) title = string.Join(" ", shapeParagraphs);
                            else paragraphs.AddRange(shapeParagraphs);
                        }
                        shapeDepth = Math.Max(0, shapeDepth - 1);
                        break;
                }
            }, text: value =>
            {
                if (inText) paragraph.Append(value);
            });
            slides.Add(new Slide(title, paragraphs));
        }
        if (slides.Count == 0) throw new PickException(L("err.office_damaged"));
        return slides;
    }

    public static string SlidesMarkdown(IReadOnlyList<Slide> slides, string title)
    {
        var lines = new List<string> { "# " + title, "" };
        for (var index = 0; index < slides.Count; index++)
        {
            var slide = slides[index];
            lines.Add("## " + (slide.Title != null ? $"{index + 1}. {slide.Title}" : L("pages.slide", (index + 1).ToString(CultureInfo.InvariantCulture))));
            lines.Add("");
            lines.AddRange(slide.Paragraphs.Select(paragraph => "- " + paragraph.Replace("\n", " ")));
            lines.Add("");
        }
        return string.Join("\n", lines);
    }

    public static string SlidesText(IReadOnlyList<Slide> slides) =>
        string.Join("\n\n", slides.Select((slide, index) =>
            string.Join("\n", new[] { slide.Title != null ? $"{index + 1}. {slide.Title}" : L("pages.slide", (index + 1).ToString(CultureInfo.InvariantCulture)) }
                .Concat(slide.Paragraphs)))) + "\n";
}
