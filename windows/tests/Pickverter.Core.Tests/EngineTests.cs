using System.IO.Compression;
using System.Xml.Linq;
using ImageMagick;
using Xunit;

namespace Pickverter.Tests;

public class ImageTests
{
    [Fact]
    public async Task ConvertsAndKeepsEveryOriginal()
    {
        using var sandbox = new Sandbox();
        var photo = sandbox.Fixture("photo.jpg");
        foreach (var ext in new[] { "png", "webp", "avif", "tiff", "gif", "bmp", "ico", "pdf" })
        {
            var output = Sandbox.Output(await Sandbox.Run("convert." + ext, photo));
            Assert.EndsWith("." + ext, output);
        }
        Assert.True(File.Exists(photo));
        var icon = Path.ChangeExtension(photo, ".ico");
        using var frames = new MagickImageCollection(icon);
        Assert.True(frames.Count >= 5);
        Assert.Equal(256u, frames.Max(frame => frame.Width));
    }

    [Fact]
    public async Task ResizesCropsAndRotates()
    {
        using var sandbox = new Sandbox();
        var photo = sandbox.Fixture("photo.jpg");
        Assert.Equal((800u, 500u), Sandbox.Size(Sandbox.Output(await Sandbox.Run("tool.resize.50", photo))));
        Assert.Equal((1000u, 1000u), Sandbox.Size(Sandbox.Output(await Sandbox.Run("tool.crop.square", photo))));
        using (Sandbox.Editor("crop", "{\"rect\":{\"x\":0.25,\"y\":0.25,\"w\":0.5,\"h\":0.5}}"))
            Assert.Equal((800u, 500u), Sandbox.Size(Sandbox.Output(await Sandbox.Run("tool.crop.custom", photo))));
        Assert.Equal((1000u, 1600u), Sandbox.Size(Sandbox.Output(await Sandbox.Run("tool.rotate.left", photo))));
        Assert.Equal((1600u, 1000u), Sandbox.Size(Sandbox.Output(await Sandbox.Run("tool.rotate.flipH", photo))));
    }

    private static bool HasGps(string path)
    {
        using var image = new MagickImage(path);
        return image.GetExifProfile()?.GetValue(ExifTag.GPSLatitude) != null;
    }

    [Fact]
    public async Task RemovesLocationAndRedacts()
    {
        using var sandbox = new Sandbox();
        var photo = sandbox.Fixture("photo.jpg");
        Assert.True(HasGps(photo));
        Assert.False(HasGps(Sandbox.Output(await Sandbox.Run("tool.meta.gps", photo))));
        using (Sandbox.Editor("redact", "{\"regions\":[{\"x\":0.1,\"y\":0.1,\"w\":0.3,\"h\":0.3}],\"style\":\"pixelate\"}"))
            Assert.False(HasGps(Sandbox.Output(await Sandbox.Run("tool.redact.custom", photo))));
        using (Sandbox.Editor("redact", "{\"regions\":[{\"x\":0.5,\"y\":0.5,\"w\":0.2,\"h\":0.2}],\"style\":\"blur\"}"))
            Assert.EndsWith(".jpg", Sandbox.Output(await Sandbox.Run("tool.redact.custom", photo)));
        using (Sandbox.Editor("redact", "{\"regions\":[{\"x\":0.5,\"y\":0.5,\"w\":0.2,\"h\":0.2}],\"style\":\"fill\"}"))
            Assert.EndsWith(".jpg", Sandbox.Output(await Sandbox.Run("tool.redact.custom", photo)));
    }

    [Fact]
    public async Task EditsInfoWithoutReencodingJpeg()
    {
        using var sandbox = new Sandbox();
        var photo = sandbox.Fixture("photo.jpg");
        using (Sandbox.Editor("metadata", "{\"fields\":{\"title\":\"Hello Pickverter\",\"keywords\":\"a, b\"},\"removeLocation\":true,\"replaceOriginal\":false}"))
        {
            var output = Sandbox.Output(await Sandbox.Run("tool.meta.edit", photo));
            using var image = new MagickImage(output);
            Assert.Equal("Hello Pickverter", image.GetExifProfile()!.GetValue(ExifTag.ImageDescription)!.Value);
            Assert.False(HasGps(output));
            var snapshot = await MetadataKit.Read(output);
            Assert.Equal("a, b", snapshot.Fields.First(field => field.Key == "keywords").Value);
            // Same compressed picture data: only the EXIF segment changed size.
            Assert.InRange(new FileInfo(output).Length, new FileInfo(photo).Length - 2000, new FileInfo(photo).Length + 2000);
        }
    }

    [Fact]
    public async Task CompressesAndCombines()
    {
        using var sandbox = new Sandbox();
        var photo = sandbox.Fixture("photo.jpg");
        var logo = sandbox.Fixture("logo.png");
        var collage = Sandbox.Output(await Sandbox.Run("tool.collage.grid", photo, logo));
        Assert.EndsWith(".jpg", collage);
        var strip = Sandbox.Output(await Sandbox.Run("tool.collage.row", photo, sandbox.Fixture("photo.jpg")));
        Assert.Equal((3200u, 1000u), Sandbox.Size(strip));
        var pdf = Sandbox.Output(await Sandbox.Run("tool.topdf", photo, logo));
        Assert.Equal(2, Sandbox.Pages(pdf));
        var book = Sandbox.Output(await Sandbox.Run("tool.picturebook", photo, logo));
        AssertEpub(book);
    }

    [Fact]
    public async Task CopiesToClipboardCache()
    {
        using var sandbox = new Sandbox();
        var photo = sandbox.Fixture("photo.jpg");
        var result = await Sandbox.Run("convert.png", ResultDestination.Clipboard, photo);
        Assert.True(ClipboardStore.Contains(Sandbox.Output(result)));
        Assert.False(File.Exists(Path.ChangeExtension(photo, ".png")));
    }

    [Fact]
    public async Task RemovesBackgroundWhenTheModelIsThere()
    {
        using var sandbox = new Sandbox();
        var photo = sandbox.Fixture("logo.png");
        if (!BackgroundModel.IsInstalled)
        {
            await Assert.ThrowsAsync<ModelMissingException>(() => Sandbox.Run("tool.removebg.transparent", photo));
            return;
        }
        var output = Sandbox.Output(await Sandbox.Run("tool.removebg.transparent", sandbox.Fixture("photo.jpg")));
        using var image = new MagickImage(output);
        Assert.True(image.HasAlpha);
    }

    internal static void AssertEpub(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        Assert.Equal("mimetype", zip.Entries[0].FullName);
        Assert.Equal("application/epub+zip", Sandbox.ZipText(path, "mimetype"));
        foreach (var entry in zip.Entries.Where(entry => entry.FullName.EndsWith(".xhtml") || entry.FullName.EndsWith(".opf") || entry.FullName.EndsWith(".ncx") || entry.FullName.EndsWith("container.xml")))
        {
            using var stream = entry.Open();
            XDocument.Load(stream);
        }
    }
}

public class PdfTests
{
    [Fact]
    public async Task ReordersSelectsAndSplits()
    {
        using var sandbox = new Sandbox();
        var report = sandbox.Fixture("report.pdf");
        Assert.Equal(3, Sandbox.Pages(Sandbox.Output(await Sandbox.Run("tool.pages.reverse", report))));
        Assert.Equal(2, Sandbox.Pages(Sandbox.Output(await Sandbox.Run("tool.pages.odd", report))));
        using (Sandbox.Editor("range", "3, 1"))
            Assert.Equal(2, Sandbox.Pages(Sandbox.Output(await Sandbox.Run("tool.pages.range", report))));
        using (Sandbox.Editor("range", "9-12"))
            await Assert.ThrowsAsync<PickException>(() => Sandbox.Run("tool.pages.range", report));
        using (Sandbox.Editor("pages", "{\"pages\":[{\"file\":0,\"page\":2,\"rotation\":90},{\"file\":0,\"page\":0,\"rotation\":0}]}"))
        {
            var arranged = Sandbox.Output(await Sandbox.Run("tool.pages.organize", report));
            using var doc = PdfSharp.Pdf.IO.PdfReader.Open(arranged, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
            Assert.Equal(2, doc.PageCount);
            Assert.Equal(90, doc.Pages[0].Rotate);
        }
        var parts = Sandbox.Output(await Sandbox.Run("tool.split", report));
        Assert.Equal(3, Directory.GetFiles(parts).Length);
        var merged = Sandbox.Output(await Sandbox.Run("tool.merge", report, sandbox.Fixture("report.pdf")));
        Assert.Equal(6, Sandbox.Pages(merged));
        Assert.Equal(3, Sandbox.Pages(Sandbox.Output(await Sandbox.Run("tool.rotate.right", report))));
    }

    [Fact]
    public async Task ReadsTextAndInfo()
    {
        using var sandbox = new Sandbox();
        var report = sandbox.Fixture("report.pdf");
        var result = await Sandbox.Run("tool.copytext", report);
        Assert.Contains("Quarterly report", sandbox.Clipboard.Text);
        Assert.NotNull(result.Message);
        var text = Sandbox.Output(await Sandbox.Run("convert.txt", report));
        Assert.Contains("Appendix", File.ReadAllText(text));
        var docx = Sandbox.Output(await Sandbox.Run("convert.docx", report));
        Assert.Contains("Quarterly report", Docx.Read(docx, "x").PlainText());
        using (Sandbox.Editor("metadata", "{\"fields\":{\"title\":\"Quarterly\"},\"removeLocation\":false,\"replaceOriginal\":false}"))
        {
            var output = Sandbox.Output(await Sandbox.Run("tool.meta", report));
            Assert.Equal("Quarterly", PdfEngine.Info(output)["title"]);
        }
    }

    [Fact]
    public async Task LocksWithAPassword()
    {
        using var sandbox = new Sandbox();
        var report = sandbox.Fixture("report.pdf");
        using (Sandbox.Editor("password", "secret"))
        {
            var locked = Sandbox.Output(await Sandbox.Run("tool.lock", report));
            Assert.Throws<PickException>(() => PdfEngine.Open(locked));
            using var opened = PdfSharp.Pdf.IO.PdfReader.Open(locked, "secret", PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
            Assert.Equal(3, opened.PageCount);
        }
    }
}

public class DocumentTests
{
    [Fact]
    public async Task ConvertsBetweenTextFormats()
    {
        using var sandbox = new Sandbox();
        var book = sandbox.Fixture("book.md");
        foreach (var ext in new[] { "docx", "odt", "rtf", "html", "txt" })
        {
            var output = Sandbox.Output(await Sandbox.Run("convert." + ext, book));
            var doc = await DocumentEngine.ReadAsync(output);
            var text = doc.PlainText();
            Assert.Contains("The First Chapter", text);
            Assert.Contains("Alice", text);
            if (ext != "txt")
            {
                Assert.Contains(doc.Blocks, block => block is HeadingBlock { Level: 1 });
                Assert.Contains(doc.Blocks, block => block is TableBlock);
                Assert.Contains(doc.Blocks, block => block is ListBlock);
            }
            if (ext is "docx" or "odt" or "html") Assert.Contains(doc.Blocks, block => block is ImageBlock);
        }
        ImageTests.AssertEpub(Sandbox.Output(await Sandbox.Run("convert.epub", book)));
        ImageTests.AssertEpub(Sandbox.Output(await Sandbox.Run("tool.epub", book, sandbox.Fixture("notes.txt"))));
    }

    [Fact]
    public async Task ReadsOtherApps()
    {
        using var sandbox = new Sandbox();
        foreach (var name in new[] { "letter.docx", "letter.odt", "letter.rtf", "page.html" })
        {
            var output = Sandbox.Output(await Sandbox.Run("convert.md", sandbox.Fixture(name)));
            var markdown = File.ReadAllText(output);
            Assert.Contains("# ", markdown);
        }
        var html = Html.Parse(File.ReadAllText(sandbox.Fixture("page.html")), null, "x");
        Assert.Equal("Sample page", html.Title);
        var list = html.Blocks.OfType<ListBlock>().Single();
        Assert.Equal(3, list.Entries.Count);
        Assert.Equal(1, list.Entries[2].Level);
        Assert.Single(html.Blocks.OfType<CodeBlock>());
        await Sandbox.Run("tool.count", sandbox.Fixture("notes.txt"));
    }

    [Fact]
    public void ReadsWordStyleRtf()
    {
        const string rtf = "{\\rtf1\\ansi\\ansicpg1252{\\fonttbl{\\f0\\fswiss Arial;}{\\f1\\fnil\\fcharset129 Malgun Gothic;}}" +
            "{\\info{\\title Sample}}\\pard\\outlinelevel0\\b\\fs40 Heading\\b0\\fs24\\par" +
            "\\pard Plain {\\b bold} {\\i it}\\par\\pard\\f1 \\'c7\\'d1\\'b1\\'db \\u54620?\\par" +
            "{\\field{\\*\\fldinst HYPERLINK \"https://example.com\"}{\\fldrslt link}}\\par}";
        var doc = Rtf.Read(rtf, "x");
        Assert.Equal("Sample", doc.Title);
        Assert.IsType<HeadingBlock>(doc.Blocks[0]);
        var paragraph = Assert.IsType<ParagraphBlock>(doc.Blocks[1]);
        Assert.Contains(paragraph.Runs, run => run.Text == "bold" && run.Style.HasFlag(RunStyle.Bold));
        Assert.Contains("한글 한", doc.PlainText());
        Assert.Contains(doc.Blocks.OfType<ParagraphBlock>().SelectMany(block => block.Runs), run => run.Link == "https://example.com");
    }

    [Fact]
    public void MarkdownRoundTrips()
    {
        var doc = Markdown.Parse(Fixtures.BookMarkdown, null, "book");
        var again = Markdown.Parse(Markdown.Write(doc), null, "book");
        Assert.Equal(doc.PlainText(), again.PlainText());
        var runs = doc.Blocks.OfType<ParagraphBlock>().First().Runs;
        Assert.Contains(runs, run => run.Style.HasFlag(RunStyle.Bold) && run.Text == "files");
        Assert.Contains(runs, run => run.Style.HasFlag(RunStyle.Code));
        Assert.Contains(runs, run => run.Link == "https://example.com");
    }
}

public class OfficeAndDataTests
{
    [Fact]
    public async Task ReadsWorkbooksAndDecks()
    {
        using var sandbox = new Sandbox();
        var sheet = sandbox.Fixture("sheet.xlsx");
        var csv = Sandbox.Output(await Sandbox.Run("convert.csv", sheet));
        Assert.Equal(2, Directory.GetFiles(csv).Length);
        var sales = File.ReadAllText(Directory.GetFiles(csv).First(file => file.Contains("Sales")));
        Assert.Contains("North,1250.5,2026-10-22", sales);
        Assert.EndsWith(".json", Sandbox.Output(await Sandbox.Run("convert.json", sheet)));
        Assert.Contains("## Sales", File.ReadAllText(Sandbox.Output(await Sandbox.Run("convert.md", sheet))));
        var deck = sandbox.Fixture("deck.pptx");
        Assert.Contains("## 1. Welcome", File.ReadAllText(Sandbox.Output(await Sandbox.Run("convert.md", deck))));
        Assert.Single(Directory.GetFiles(Sandbox.Output(await Sandbox.Run("tool.media", deck))));
        await Sandbox.Run("tool.copytext", deck);
        Assert.Contains("Ship it", sandbox.Clipboard.Text);
    }

    [Fact]
    public async Task ConvertsTablesSubtitlesAndJson()
    {
        using var sandbox = new Sandbox();
        var json = Sandbox.Output(await Sandbox.Run("convert.json", sandbox.Fixture("people.csv")));
        Assert.Contains("\"name\": \"Alice\"", File.ReadAllText(json));
        var table = Sandbox.Output(await Sandbox.Run("convert.csv", sandbox.Fixture("data.json")));
        Assert.StartsWith("name,score,tags", File.ReadAllText(table));
        var vtt = Sandbox.Output(await Sandbox.Run("convert.vtt", sandbox.Fixture("captions.srt")));
        Assert.Contains("00:00:01.000 --> 00:00:02.500", File.ReadAllText(vtt));
        var pretty = Sandbox.Output(await Sandbox.Run("tool.pretty", sandbox.Fixture("data.json")));
        Assert.Contains("\n  {\n    \"name\": \"Alice\"", File.ReadAllText(pretty));
        var minified = Sandbox.Output(await Sandbox.Run("tool.minify", pretty));
        Assert.DoesNotContain("\n", File.ReadAllText(minified));
    }

    [Fact]
    public async Task ZipsExtractsAndHashes()
    {
        using var sandbox = new Sandbox();
        var bundle = sandbox.Fixture("bundle.zip");
        var extracted = Sandbox.Output(await Sandbox.Run("tool.extract", bundle));
        Assert.True(Directory.Exists(extracted));
        Assert.Equal("bundle", Path.GetFileName(extracted));
        var zip = Sandbox.Output(await Sandbox.Run("tool.zip", extracted));
        Assert.Contains("people.csv", Sandbox.ZipText(zip, "bundle/notes.txt").Length > 0 ? "people.csv" : "");
        var targz = Sandbox.Output(await Sandbox.Run("tool.targz", extracted));
        Assert.EndsWith(".tar.gz", targz);
        var converted = Sandbox.Output(await Sandbox.Run("convert.tar.gz", bundle));
        Assert.EndsWith(".tar.gz", converted);
        await Sandbox.Run("action.hash.sha-256", sandbox.Fixture("notes.txt"));
        Assert.Equal(64, sandbox.Clipboard.Text!.Length);
        await Sandbox.Run("tool.listing", extracted);
        Assert.Contains("notes.txt", sandbox.Clipboard.Text);
    }
}

public class MediaTests
{
    [Fact]
    public async Task UsesFFmpegWhenInstalled()
    {
        using var sandbox = new Sandbox();
        if (Tools.FFmpeg == null) return;
        var tone = sandbox.Fixture("tone.wav");
        Assert.EndsWith(".mp3", Sandbox.Output(await Sandbox.Run("convert.mp3", tone)));
        using (Sandbox.Editor("trim", "{\"start\":1,\"end\":2.5,\"lossless\":false}"))
        {
            var clip = Sandbox.Output(await Sandbox.Run("tool.cut", tone));
            Assert.InRange(await MediaEngine.Duration(clip), 1.2, 1.8);
        }
        Assert.Equal(2, Directory.GetFiles(Sandbox.Output(await Sandbox.Run("tool.parts.2", tone))).Length);
        if (!sandbox.HasFixture("clip.mp4")) return;
        var movie = sandbox.Fixture("clip.mp4");
        Assert.EndsWith(".png", Sandbox.Output(await Sandbox.Run("tool.snapshot.middle", movie)));
        Assert.EndsWith(".gif", Sandbox.Output(await Sandbox.Run("convert.gif.480", movie)));
        Assert.EndsWith(".mp4", Sandbox.Output(await Sandbox.Run("tool.rotate.left", movie)));
        Assert.EndsWith(".mp4", Sandbox.Output(await Sandbox.Run("tool.join", movie, sandbox.Fixture("clip.mp4"))));
    }
}
