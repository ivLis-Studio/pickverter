using ImageMagick;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Pickverter;

/// PDF pages with PDFsharp, text with PdfPig, rendering with Windows' own PDF engine.
public static class PdfEngine
{
    public static PdfDocument Open(string path, PdfDocumentOpenMode mode = PdfDocumentOpenMode.Import)
    {
        try
        {
            return PdfReader.Open(path, mode);
        }
        catch (Exception error) when (error.Message.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                                      error.Message.Contains("encrypt", StringComparison.OrdinalIgnoreCase) ||
                                      error.Message.Contains("protected", StringComparison.OrdinalIgnoreCase))
        {
            throw new PickException(L("err.pdf_locked"));
        }
        catch (Exception error) when (error is not PickException)
        {
            Debug.Log("pdf open: " + error.Message);
            throw new PickException(L("err.pdf_open"));
        }
    }

    public static int PageCount(string path)
    {
        using var doc = Open(path, PdfDocumentOpenMode.Import);
        return doc.PageCount;
    }

    public static async Task<string> Convert(string src, string ext, double dpi, PickContext ctx)
    {
        switch (ext)
        {
            case "png":
            case "jpg":
                return await ToImages(src, ext, dpi, ctx);
            case "txt":
            {
                var text = await ExtractText(src, ctx);
                return await ctx.Produce(ctx.DestinationFor(src, "txt"), tmp => FileSystem.WriteText(tmp, text));
            }
            default:
            {
                var text = await ExtractText(src, ctx);
                var doc = Doc.FromText(text, FileNames.Base(src));
                return await ctx.Produce(ctx.DestinationFor(src, ext), tmp => DocumentEngine.Write(doc, tmp, ext));
            }
        }
    }

    /// Renders a page on white into a PNG or JPG file.
    public static async Task RenderToFile(string pdf, int index, double dpi, string path, string ext, CancellationToken token)
    {
        if (ext == "png")
        {
            await Platform.Pdf.RenderPageAsync(pdf, index, dpi, path, token);
            return;
        }
        var png = path + ".pv.png";
        try
        {
            await Platform.Pdf.RenderPageAsync(pdf, index, dpi, png, token);
            using var image = new MagickImage(png);
            image.Quality = 90;
            image.Write(path, MagickFormat.Jpeg);
        }
        finally
        {
            FileSystem.TryDelete(png);
        }
    }

    public static async Task<string> ToImages(string src, string ext, double dpi, PickContext ctx)
    {
        var count = PageCount(src);
        if (count == 0) throw new PickException(L("err.pdf_empty"));
        if (count == 1) return await ctx.Produce(ctx.DestinationFor(src, ext), tmp => RenderToFile(src, 0, dpi, tmp, ext, ctx.Token));
        var stem = FileNames.Base(src);
        var digits = Math.Max(2, count.ToString(CultureInfo.InvariantCulture).Length);
        return await ctx.Produce(ctx.DestinationFor(src, null, ext.ToUpperInvariant()), async folder =>
        {
            Directory.CreateDirectory(folder);
            for (var index = 0; index < count; index++)
            {
                ctx.Check();
                ctx.Report((double)index / count, L("note.page", (index + 1).ToString(CultureInfo.InvariantCulture), count.ToString(CultureInfo.InvariantCulture)));
                var number = (index + 1).ToString(new string('0', digits), CultureInfo.InvariantCulture);
                await RenderToFile(src, index, dpi, Path.Combine(folder, FileNames.Sanitize($"{stem}-{number}.{ext}")), ext, ctx.Token);
            }
        });
    }

    /// Text layer first; pages without one (scans) fall back to Windows OCR.
    public static async Task<string> ExtractText(string path, PickContext ctx)
    {
        var pages = new List<string>();
        PigDocument document;
        try
        {
            document = PigDocument.Open(path);
        }
        catch (Exception error) when (error.GetType().Name.Contains("Encrypted", StringComparison.Ordinal) ||
                                      error.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            throw new PickException(L("err.pdf_locked"));
        }
        catch (Exception)
        {
            throw new PickException(L("err.pdf_open"));
        }
        using (document)
        {
            var count = document.NumberOfPages;
            for (var number = 1; number <= count; number++)
            {
                ctx.Check();
                string text;
                try
                {
                    text = ContentOrderTextExtractor.GetText(document.GetPage(number));
                }
                catch (Exception)
                {
                    text = "";
                }
                if (text.Trim().Length == 0 && Platform.Pdf.Available && Platform.Vision.OcrAvailable)
                {
                    var png = Path.Combine(Path.GetTempPath(), "pickverter-ocr-" + Guid.NewGuid().ToString("N")[..10] + ".png");
                    try
                    {
                        await Platform.Pdf.RenderPageAsync(path, number - 1, 200, png, ctx.Token);
                        text = string.Join("\n", (await Platform.Vision.RecognizeAsync(png, ctx.Token)).Select(line => line.Text));
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        Debug.Log("ocr page: " + error.Message);
                    }
                    finally
                    {
                        FileSystem.TryDelete(png);
                    }
                }
                pages.Add(text.Replace("\r\n", "\n"));
                ctx.Report((double)number / Math.Max(count, 1));
            }
        }
        var joined = string.Join("\n\n", pages).Trim();
        if (joined.Length == 0) throw new PickException(L("err.no_text"));
        return joined;
    }

    public static async Task<PickResult> CopyText(PickContext ctx)
    {
        var chunks = new List<string>();
        foreach (var file in ctx.Files) chunks.Add(await ExtractText(file, ctx));
        var text = string.Join("\n\n", chunks);
        await Platform.Clipboard.SetTextAsync(text.Replace("\n", "\r\n"));
        return PickResult.Note(LP("msg.chars_copied", text.Length));
    }

    // Compression: re-encode embedded photos as JPEG at screen resolution, compress streams.

    private static void Recompress(PdfDictionary image)
    {
        var elements = image.Elements;
        if (elements.ContainsKey("/SMask") || elements.ContainsKey("/Mask") || elements.GetBoolean("/ImageMask")) return;
        if (image.Stream == null) return;
        var width = elements.GetInteger("/Width");
        var height = elements.GetInteger("/Height");
        if (width * height < 96 * 96) return;
        var colorSpace = elements.GetName("/ColorSpace");
        if (colorSpace is not ("/DeviceRGB" or "/DeviceGray")) return;
        var filter = elements.GetName("/Filter");
        var original = image.Stream.Value;
        byte[]? encoded = null;
        uint newWidth = (uint)width, newHeight = (uint)height;
        try
        {
            MagickImage? magick = null;
            if (filter == "/DCTDecode")
            {
                magick = new MagickImage(original);
            }
            else if (filter == "/FlateDecode" && elements.GetInteger("/BitsPerComponent") == 8 && !elements.ContainsKey("/DecodeParms"))
            {
                var raw = image.Stream.UnfilteredValue;
                var channels = colorSpace == "/DeviceRGB" ? 3 : 1;
                if (raw.Length == width * height * channels)
                {
                    magick = new MagickImage(raw, new MagickReadSettings
                    {
                        Width = (uint)width,
                        Height = (uint)height,
                        Format = channels == 3 ? MagickFormat.Rgb : MagickFormat.Gray,
                        Depth = 8,
                    });
                }
            }
            if (magick == null) return;
            using (magick)
            {
                if (Math.Max(magick.Width, magick.Height) > 2000) magick.Resize(new MagickGeometry(2000, 2000) { Greater = true });
                magick.Strip();
                magick.Quality = 70;
                newWidth = magick.Width;
                newHeight = magick.Height;
                encoded = magick.ToByteArray(MagickFormat.Jpeg);
            }
        }
        catch (Exception)
        {
            return;
        }
        if (encoded == null || encoded.Length >= original.Length * 0.9) return;
        image.Stream.Value = encoded;
        elements.SetName("/Filter", "/DCTDecode");
        elements.Remove("/DecodeParms");
        elements.SetInteger("/Width", (int)newWidth);
        elements.SetInteger("/Height", (int)newHeight);
        elements.SetInteger("/BitsPerComponent", 8);
    }

    public static async Task<string> Compress(string src, PickContext ctx)
    {
        ctx.Report(null, L("note.optimizing"));
        var output = await ctx.Produce(ctx.DestinationFor(src, "pdf", L("sfx.compressed")), tmp =>
        {
            using var document = Open(src, PdfDocumentOpenMode.Modify);
            var seen = new HashSet<PdfDictionary>();
            foreach (var page in document.Pages)
            {
                ctx.Check();
                var resources = page.Elements.GetDictionary("/Resources");
                var objects = resources?.Elements.GetDictionary("/XObject");
                if (objects == null) continue;
                foreach (var key in objects.Elements.Keys.ToList())
                {
                    if (objects.Elements.GetReference(key)?.Value is not PdfDictionary item) continue;
                    if (item.Elements.GetName("/Subtype") != "/Image" || !seen.Add(item)) continue;
                    Recompress(item);
                }
            }
            document.Options.CompressContentStreams = true;
            document.Options.NoCompression = false;
            document.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
            document.Save(tmp);
        });
        ImageEngine.KeepIfSmaller(output, src);
        return output;
    }

    public static async Task<PickResult> Merge(PickContext ctx)
    {
        var files = NaturalComparer.SortByName(ctx.Files);
        var destination = ctx.DestinationFor(files[0], "pdf", L("sfx.merged"));
        return PickResult.Of(await ctx.Produce(destination, tmp =>
        {
            using var merged = new PdfDocument();
            for (var index = 0; index < files.Count; index++)
            {
                ctx.Check();
                ctx.Report((double)index / files.Count, FileNames.Name(files[index]));
                using var input = Open(files[index]);
                foreach (var page in input.Pages) merged.AddPage(page);
            }
            merged.Save(tmp);
        }));
    }

    public static async Task<string> Split(string src, PickContext ctx)
    {
        using var input = Open(src);
        if (input.PageCount < 2) throw new PickException(L("err.single_page"));
        var stem = FileNames.Base(src);
        var digits = Math.Max(2, input.PageCount.ToString(CultureInfo.InvariantCulture).Length);
        return await ctx.Produce(ctx.DestinationFor(src, null, L("sfx.pages")), folder =>
        {
            Directory.CreateDirectory(folder);
            for (var index = 0; index < input.PageCount; index++)
            {
                ctx.Check();
                using var single = new PdfDocument();
                single.AddPage(input.Pages[index]);
                var number = (index + 1).ToString(new string('0', digits), CultureInfo.InvariantCulture);
                single.Save(Path.Combine(folder, FileNames.Sanitize($"{stem}-{number}.pdf")));
                ctx.Report((double)(index + 1) / input.PageCount);
            }
        });
    }

    public static async Task<string> Rotate(string src, Turn turn, PickContext ctx)
    {
        var degrees = turn switch { Turn.Left => 270, Turn.Half => 180, _ => 90 };
        return await ctx.Produce(ctx.DestinationFor(src, "pdf", L("sfx.rotated")), tmp =>
        {
            using var document = Open(src, PdfDocumentOpenMode.Modify);
            foreach (var page in document.Pages) page.Rotate = (page.Rotate + degrees) % 360;
            document.Save(tmp);
        });
    }

    public static async Task<string> Encrypt(string src, PickContext ctx)
    {
        var password = await Platform.Editors.PasswordAsync(src, ctx.Token);
        if (string.IsNullOrEmpty(password)) throw new OperationCanceledException();
        ctx.BeginWork();
        return await ctx.Produce(ctx.DestinationFor(src, "pdf", L("sfx.locked")), tmp =>
        {
            using var document = Open(src, PdfDocumentOpenMode.Modify);
            document.SecurityHandler.SetEncryptionToV5(true);
            document.SecuritySettings.UserPassword = password;
            document.SecuritySettings.OwnerPassword = password;
            document.Save(tmp);
        });
    }

    // Pages

    /// Builds a new document from pages of one or more PDFs, in the given order and rotation.
    public static async Task<string> Arrange(IReadOnlyList<string> files, PdfArrangement arrangement, string? suffix, PickContext ctx)
    {
        if (arrangement.Pages.Count == 0) throw new PickException(L("err.pdf_empty"));
        var name = suffix ?? (files.Count > 1 ? L("sfx.merged") : L("sfx.edited"));
        return await ctx.Produce(ctx.DestinationFor(files[0], "pdf", name), tmp =>
        {
            var documents = files.Select(file => Open(file)).ToList();
            try
            {
                using var result = new PdfDocument();
                foreach (var entry in arrangement.Pages)
                {
                    if (entry.File < 0 || entry.File >= documents.Count || entry.Page < 0 || entry.Page >= documents[entry.File].PageCount) continue;
                    var page = result.AddPage(documents[entry.File].Pages[entry.Page]);
                    page.Rotate = ((page.Rotate + entry.Rotation) % 360 + 360) % 360;
                }
                if (result.PageCount == 0) throw new PickException(L("err.pdf_empty"));
                result.Save(tmp);
            }
            finally
            {
                foreach (var document in documents) document.Dispose();
            }
        });
    }

    public static Task<string> Select(string src, IReadOnlyList<int> pages, string suffix, PickContext ctx)
    {
        var count = PageCount(src);
        var valid = pages.Where(page => page >= 0 && page < count).ToList();
        if (valid.Count == 0) throw new PickException(L("err.no_pages"));
        return Arrange([src], new PdfArrangement(valid.Select(page => new PdfPageEntry(0, page, 0)).ToList()), suffix, ctx);
    }

    public static Task<string> Reverse(string src, PickContext ctx)
    {
        var count = PageCount(src);
        if (count < 2) throw new PickException(L("err.single_page"));
        return Select(src, Enumerable.Range(0, count).Reverse().ToList(), L("sfx.reversed"), ctx);
    }

    public static Task<string> OddEven(string src, bool odd, PickContext ctx)
    {
        var count = PageCount(src);
        if (count < 2) throw new PickException(L("err.single_page"));
        return Select(src, Enumerable.Range(0, count).Where(index => index % 2 == 0 == odd).ToList(), odd ? L("sfx.odd") : L("sfx.even"), ctx);
    }

    /// A page is blank when it has no text and almost every pixel is close to white.
    public static async Task<bool> IsBlank(string path, PigDocument document, int index, CancellationToken token)
    {
        try
        {
            if (document.GetPage(index + 1).Text.Trim().Length > 0) return false;
        }
        catch (Exception)
        {
        }
        var png = Path.Combine(Path.GetTempPath(), "pickverter-blank-" + Guid.NewGuid().ToString("N")[..10] + ".png");
        try
        {
            await Platform.Pdf.RenderPageAsync(path, index, 30, png, token);
            using var image = new MagickImage(png);
            using var pixels = image.GetPixels();
            var data = pixels.ToByteArray(PixelMapping.RGB) ?? [];
            long ink = 0;
            for (var offset = 0; offset + 2 < data.Length; offset += 3)
            {
                if (data[offset] + data[offset + 1] + data[offset + 2] < 3 * 235) ink++;
            }
            return ink / (double)Math.Max(1, data.Length / 3) < 0.002;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            FileSystem.TryDelete(png);
        }
    }

    public static async Task<string> RemoveBlank(string src, PickContext ctx)
    {
        using var document = PigDocument.Open(src);
        var count = document.NumberOfPages;
        var keep = new List<int>();
        for (var index = 0; index < count; index++)
        {
            ctx.Check();
            ctx.Report((double)index / Math.Max(count, 1) * 0.8,
                L("note.page", (index + 1).ToString(CultureInfo.InvariantCulture), count.ToString(CultureInfo.InvariantCulture)));
            if (!await IsBlank(src, document, index, ctx.Token)) keep.Add(index);
        }
        if (keep.Count == count) throw new PickException(L("err.no_blank"));
        if (keep.Count == 0) throw new PickException(L("err.all_blank"));
        return await Select(src, keep, L("sfx.no_blank"), ctx);
    }

    /// "1-3, 5, 8-" -> zero-based page indexes (in the typed order, duplicates kept).
    public static List<int>? ParseRanges(string text, int pageCount)
    {
        var pages = new List<int>();
        var parts = text.Replace('–', '-').Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries);
        foreach (var piece in parts)
        {
            if (piece.Contains('-'))
            {
                var ends = piece.Split('-');
                if (ends.Length != 2) return null;
                var lower = ends[0].Length == 0 ? 1 : int.TryParse(ends[0], out var a) ? a : -1;
                var upper = ends[1].Length == 0 ? pageCount : int.TryParse(ends[1], out var b) ? b : -1;
                if (lower < 1 || upper < 1) return null;
                var range = lower <= upper ? Enumerable.Range(lower, upper - lower + 1) : Enumerable.Range(upper, lower - upper + 1).Reverse();
                pages.AddRange(range.Where(page => page <= pageCount).Select(page => page - 1));
            }
            else
            {
                if (!int.TryParse(piece, out var number) || number < 1 || number > pageCount) return null;
                pages.Add(number - 1);
            }
        }
        return pages.Count == 0 ? null : pages;
    }

    public static async Task<string> ExtractRange(string src, PickContext ctx)
    {
        var count = PageCount(src);
        var text = await Platform.Editors.RangeAsync(src, count, ctx.Token);
        var pages = ParseRanges(text, count) ?? throw new PickException(L("err.bad_range", text));
        ctx.BeginWork();
        return await Select(src, pages, L("sfx.pages_range"), ctx);
    }

    // Document info

    public static Dictionary<string, string> Info(string path)
    {
        using var document = Open(path, PdfDocumentOpenMode.Import);
        var info = document.Info;
        return new Dictionary<string, string>
        {
            ["title"] = info.Title ?? "",
            ["author"] = info.Author ?? "",
            ["subject"] = info.Subject ?? "",
            ["keywords"] = info.Keywords ?? "",
            ["creator"] = info.Creator ?? "",
            ["producer"] = info.Producer ?? "",
            ["pages"] = document.PageCount.ToString(CultureInfo.InvariantCulture),
        };
    }

    public static void WriteInfo(string src, IReadOnlyDictionary<string, string> fields, string destination)
    {
        using var document = Open(src, PdfDocumentOpenMode.Modify);
        var info = document.Info;
        if (fields.TryGetValue("title", out var title)) info.Title = title;
        if (fields.TryGetValue("author", out var author)) info.Author = author;
        if (fields.TryGetValue("subject", out var subject)) info.Subject = subject;
        if (fields.TryGetValue("keywords", out var keywords)) info.Keywords = keywords;
        if (fields.TryGetValue("creator", out var creator)) info.Creator = creator;
        document.Save(destination);
    }
}
