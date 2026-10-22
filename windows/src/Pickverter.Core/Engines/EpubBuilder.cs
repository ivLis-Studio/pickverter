using System.IO.Compression;

namespace Pickverter;

/// Minimal, valid EPUB 3 (with an EPUB 2 NCX for older readers). Everything is XHTML we generate ourselves.
public static class EpubBuilder
{
    public sealed record Chapter(string Title, string Body);
    public sealed record Asset(string Name, byte[] Data, string MediaType);

    private static string Escape(string text) => Html.Attribute(text);

    /// Splits on level-1 headings when there are several, otherwise on level 2, otherwise one chapter.
    public static List<(string Title, Doc Doc)> Sections(Doc doc, string fallbackTitle)
    {
        var h1 = doc.Blocks.OfType<HeadingBlock>().Count(heading => heading.Level == 1);
        var h2 = doc.Blocks.OfType<HeadingBlock>().Count(heading => heading.Level == 2);
        var level = h1 >= 2 ? 1 : h2 >= 2 ? 2 : 0;
        if (level == 0)
        {
            var first = doc.Blocks.OfType<HeadingBlock>().FirstOrDefault(heading => heading.Level == 1);
            return [(first != null ? Doc.Plain(first.Runs) : fallbackTitle, doc)];
        }
        var result = new List<(string, Doc)>();
        var current = new Doc { Title = fallbackTitle };
        var title = fallbackTitle;
        foreach (var block in doc.Blocks)
        {
            if (block is HeadingBlock heading && heading.Level == level)
            {
                if (current.Blocks.Count > 0) result.Add((title, current));
                current = new Doc { Title = Doc.Plain(heading.Runs) };
                title = current.Title;
            }
            current.Blocks.Add(block);
        }
        if (current.Blocks.Count > 0) result.Add((title, current));
        return result;
    }

    public static async Task<string> FromDocuments(IReadOnlyList<string> files, PickContext ctx)
    {
        var sorted = NaturalComparer.SortByName(files);
        var chapters = new List<Chapter>();
        var assets = new List<Asset>();
        var sample = new StringBuilder();
        for (var index = 0; index < sorted.Count; index++)
        {
            ctx.Check();
            var file = sorted[index];
            ctx.Report((double)index / sorted.Count * 0.7, FileNames.Name(file));
            var name = FileNames.Base(file);
            var doc = await DocumentEngine.ReadAsync(file, ctx.Token);
            var plain = doc.PlainText();
            sample.Append(plain.Length > 2000 ? plain[..2000] : plain);
            var parts = sorted.Count == 1 ? Sections(doc, name) : [(Sections(doc, name)[0].Title, doc)];
            foreach (var (title, part) in parts)
            {
                var body = Html.Body(part, image =>
                {
                    var (data, ext) = Docx.WordImage(image);
                    var asset = $"img{assets.Count + 1:000}.{ext}";
                    assets.Add(new Asset(asset, data, Doc.ImageMime(ext)));
                    return "../images/" + asset;
                }, xhtml: true);
                chapters.Add(new Chapter(title, body));
            }
        }
        if (chapters.Count == 0) throw new PickException(L("err.no_text"));
        var bookTitle = sorted.Count == 1
            ? (chapters.Count == 1 ? chapters[0].Title : FileNames.Base(sorted[0]))
            : FileNames.Name(Path.GetDirectoryName(Path.GetFullPath(sorted[0])) ?? FileNames.Base(sorted[0]));
        var destination = ctx.DestinationFor(sorted[0], "epub", sorted.Count > 1 ? LP("sfx.and_more", sorted.Count - 1) : null);
        var language = Language.Detect(sample.ToString());
        return await ctx.Produce(destination, tmp => Write(bookTitle, language, chapters, assets, null, tmp));
    }

    public static async Task<string> FromImages(IReadOnlyList<string> files, PickContext ctx)
    {
        var sorted = NaturalComparer.SortByName(files);
        var assets = new List<Asset>();
        var chapters = new List<Chapter>();
        for (var index = 0; index < sorted.Count; index++)
        {
            ctx.Check();
            var file = sorted[index];
            ctx.Report((double)index / sorted.Count * 0.85, FileNames.Name(file));
            var (data, ext) = await Task.Run(() => ImageEngine.EncodeForBook(file), ctx.Token);
            var name = $"{index + 1:000}.{ext}";
            assets.Add(new Asset(name, data, Doc.ImageMime(ext)));
            chapters.Add(new Chapter(L("pages.page_short", (index + 1).ToString(CultureInfo.InvariantCulture)),
                $"<div class=\"page\"><img src=\"../images/{name}\" alt=\"{Escape(FileNames.Base(file))}\"/></div>"));
        }
        if (chapters.Count == 0) throw new PickException(L("err.read_image"));
        var title = sorted.Count > 1 ? FileNames.Name(Path.GetDirectoryName(Path.GetFullPath(sorted[0])) ?? "") : FileNames.Base(sorted[0]);
        var destination = ctx.DestinationFor(sorted[0], "epub", sorted.Count > 1 ? LP("sfx.and_more", sorted.Count - 1) : null);
        return await ctx.Produce(destination, tmp => Write(title, Loc.Language, chapters, assets, assets[0].Name, tmp));
    }

    public static void Write(string title, string language, IReadOnlyList<Chapter> chapters, IReadOnlyList<Asset> assets, string? cover, string path)
    {
        var lang = Escape(language);
        var bookTitle = Escape(title);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        void Add(string name, string text, CompressionLevel level = CompressionLevel.Optimal)
        {
            var entry = zip.CreateEntry(name, level);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(text);
        }
        // The mimetype entry must come first and be stored uncompressed.
        Add("mimetype", "application/epub+zip", CompressionLevel.NoCompression);
        Add("META-INF/container.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">\n" +
            "  <rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles>\n</container>\n");
        Add("OEBPS/style.css", "body { font-family: serif; line-height: 1.6; margin: 0 4%; }\nh1, h2, h3, h4 { font-family: sans-serif; line-height: 1.3; }\n" +
            "img { max-width: 100%; height: auto; }\npre { white-space: pre-wrap; font-size: 0.9em; }\ncode { font-family: monospace; }\n" +
            "blockquote { margin-left: 0; padding-left: 1em; border-left: 3px solid #ccc; }\ntable { border-collapse: collapse; } th, td { border: 1px solid #999; padding: 0.2em 0.5em; }\n" +
            ".page { margin: 0; padding: 0; text-align: center; }\n.page img { max-height: 96vh; }\n");
        var manifest = new List<string>
        {
            "<item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>",
            "<item id=\"ncx\" href=\"toc.ncx\" media-type=\"application/x-dtbncx+xml\"/>",
            "<item id=\"css\" href=\"style.css\" media-type=\"text/css\"/>",
        };
        var spine = new List<string>();
        var navItems = new List<string>();
        var ncxItems = new List<string>();
        for (var index = 0; index < chapters.Count; index++)
        {
            var id = $"ch{index + 1:000}";
            var heading = Escape(chapters[index].Title);
            Add($"OEBPS/text/{id}.xhtml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE html>\n" +
                $"<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\" lang=\"{lang}\" xml:lang=\"{lang}\">\n" +
                $"<head><meta charset=\"UTF-8\"/><title>{heading}</title><link rel=\"stylesheet\" type=\"text/css\" href=\"../style.css\"/></head>\n" +
                "<body>\n<section epub:type=\"chapter\">\n" + chapters[index].Body + "\n</section>\n</body>\n</html>\n");
            manifest.Add($"<item id=\"{id}\" href=\"text/{id}.xhtml\" media-type=\"application/xhtml+xml\"/>");
            spine.Add($"<itemref idref=\"{id}\"/>");
            navItems.Add($"<li><a href=\"text/{id}.xhtml\">{heading}</a></li>");
            ncxItems.Add($"<navPoint id=\"np{index + 1}\" playOrder=\"{index + 1}\"><navLabel><text>{heading}</text></navLabel><content src=\"text/{id}.xhtml\"/></navPoint>");
        }
        for (var index = 0; index < assets.Count; index++)
        {
            var asset = assets[index];
            var entry = zip.CreateEntry("OEBPS/images/" + asset.Name, CompressionLevel.NoCompression);
            using (var output = entry.Open()) output.Write(asset.Data);
            var isCover = asset.Name == cover;
            var id = isCover ? "cover-image" : $"img{index + 1:000}";
            manifest.Add($"<item id=\"{id}\" href=\"images/{asset.Name}\" media-type=\"{asset.MediaType}\"{(isCover ? " properties=\"cover-image\"" : "")}/>");
        }
        var identifier = "urn:uuid:" + Guid.NewGuid().ToString().ToLowerInvariant();
        var modified = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        Add("OEBPS/content.opf", "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            $"<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"bookid\" xml:lang=\"{lang}\">\n" +
            "  <metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">\n" +
            $"    <dc:identifier id=\"bookid\">{identifier}</dc:identifier>\n    <dc:title>{bookTitle}</dc:title>\n    <dc:language>{lang}</dc:language>\n" +
            $"    <meta property=\"dcterms:modified\">{modified}</meta>\n" + (cover != null ? "    <meta name=\"cover\" content=\"cover-image\"/>\n" : "") +
            "  </metadata>\n  <manifest>\n    " + string.Join("\n    ", manifest) + "\n  </manifest>\n  <spine toc=\"ncx\">\n    " +
            string.Join("\n    ", spine) + "\n  </spine>\n</package>\n");
        Add("OEBPS/nav.xhtml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE html>\n" +
            $"<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\" lang=\"{lang}\" xml:lang=\"{lang}\">\n" +
            $"<head><meta charset=\"UTF-8\"/><title>{bookTitle}</title></head>\n<body>\n<nav epub:type=\"toc\" id=\"toc\"><h1>{bookTitle}</h1>\n<ol>\n" +
            string.Join("\n", navItems) + "\n</ol>\n</nav>\n</body>\n</html>\n");
        Add("OEBPS/toc.ncx", "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<ncx xmlns=\"http://www.daisy.org/z3986/2005/ncx/\" version=\"2005-1\">\n" +
            $"  <head><meta name=\"dtb:uid\" content=\"{identifier}\"/></head>\n  <docTitle><text>{bookTitle}</text></docTitle>\n  <navMap>\n    " +
            string.Join("\n    ", ncxItems) + "\n  </navMap>\n</ncx>\n");
    }
}
