using System.IO.Compression;
using ImageMagick;
using ImageMagick.Drawing;

namespace Pickverter;

/// Small sample files for every engine, generated without any external tool (tests on a bare machine).
public static class Fixtures
{
    private static readonly string[] Names =
    [
        "photo.jpg", "logo.png", "sign.png", "report.pdf", "tone.wav", "sheet.xlsx", "deck.pptx", "book.md", "notes.txt", "people.csv",
        "data.json", "captions.srt", "page.html", "letter.docx", "letter.odt", "letter.rtf", "bundle.zip", "clip.mp4", "tone.mp3",
    ];

    public static readonly string BookMarkdown = string.Join("\n",
        "# The First Chapter",
        "",
        "Pickverter turns **files** into other *files*. Here is a [link](https://example.com) and some \u0060code\u0060.",
        "",
        "- one",
        "- two",
        "",
        "![Logo](logo.png)",
        "",
        "---",
        "",
        "# The Second Chapter",
        "",
        "| Name | Score |",
        "| --- | --- |",
        "| Alice | 92 |",
        "",
        "> A quote & an ampersand.");

    public static IReadOnlyList<string> Make(string folder, bool media = true)
    {
        Directory.CreateDirectory(folder);
        // Writers create zip containers from scratch: start from a clean slate when the folder is reused.
        foreach (var name in Names) FileSystem.TryDelete(Path.Combine(folder, name));
        Photo(Path.Combine(folder, "photo.jpg"));
        Logo(Path.Combine(folder, "logo.png"));
        Sign(Path.Combine(folder, "sign.png"));
        Report(Path.Combine(folder, "report.pdf"));
        Wav(Path.Combine(folder, "tone.wav"), 4);
        Workbook(Path.Combine(folder, "sheet.xlsx"));
        Deck(Path.Combine(folder, "deck.pptx"), Path.Combine(folder, "logo.png"));
        FileSystem.WriteText(Path.Combine(folder, "book.md"), BookMarkdown + "\n");
        FileSystem.WriteText(Path.Combine(folder, "notes.txt"), "Plain notes.\n\nSecond paragraph with a line\nbreak.\n");
        FileSystem.WriteText(Path.Combine(folder, "people.csv"), "name,score\nAlice,92.5\nBob,88\n");
        FileSystem.WriteText(Path.Combine(folder, "data.json"), "[{\"name\":\"Alice\",\"score\":92.5,\"tags\":[\"a\",\"b\"]},{\"name\":\"Bob\",\"score\":88}]");
        FileSystem.WriteText(Path.Combine(folder, "captions.srt"), "1\n00:00:01,000 --> 00:00:02,500\nHello there\n\n2\n00:00:03,000 --> 00:00:04,000\n<i>General</i> Kenobi\n");
        FileSystem.WriteText(Path.Combine(folder, "page.html"),
            "<!DOCTYPE html><html><head><title>Sample page</title><style>p{color:red}</style></head><body><h1>Sample page</h1>" +
            "<p>Some <b>bold</b>, <i>italic</i> and a <a href=\"https://example.com\">link</a>.</p><ul><li>First</li><li>Second<ul><li>Nested</li></ul></li></ul>" +
            "<table><tr><th>Key</th><th>Value</th></tr><tr><td>a</td><td>1</td></tr></table><pre>code\n  indented</pre></body></html>");
        var doc = Markdown.Parse(BookMarkdown, folder, "book");
        Docx.Write(doc, Path.Combine(folder, "letter.docx"));
        Odt.Write(doc, Path.Combine(folder, "letter.odt"));
        File.WriteAllText(Path.Combine(folder, "letter.rtf"), Rtf.Write(doc), Encoding.ASCII);
        using (var zip = ZipFile.Open(Path.Combine(folder, "bundle.zip"), ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(Path.Combine(folder, "notes.txt"), "bundle/notes.txt");
            zip.CreateEntryFromFile(Path.Combine(folder, "people.csv"), "bundle/people.csv");
        }
        if (media && Tools.FFmpeg is { } ffmpeg)
        {
            try
            {
                Shell.RunAsync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc=duration=6:size=640x360:rate=30",
                    "-f", "lavfi", "-i", "sine=frequency=330:duration=6", "-shortest", .. MediaEngine.X264(23), "-c:a", "aac", Path.Combine(folder, "clip.mp4")]).Wait();
                Shell.RunAsync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-i", Path.Combine(folder, "tone.wav"), "-c:a", "libmp3lame", "-q:a", "4",
                    Path.Combine(folder, "tone.mp3")]).Wait();
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("warning: no media fixtures (" + error.Message + ")");
            }
        }
        return Directory.GetFiles(folder).OrderBy(path => path, StringComparer.Ordinal).ToList();
    }

    private static void Photo(string path)
    {
        using var image = new MagickImage(new MagickColor("#8CB8DB"), 1600, 1000);
        new Drawables()
            .FillColor(new MagickColor("#F2C14E")).Circle(1200, 260, 1290, 260)
            .FillColor(new MagickColor("#3F7D4E")).Rectangle(0, 700, 1600, 1000)
            .FillColor(new MagickColor("#2E5E3A")).Polygon(new PointD(100, 700), new PointD(420, 330), new PointD(760, 700))
            .Draw(image);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Make, "Pickverter");
        exif.SetValue(ExifTag.Model, "Fixture Camera");
        exif.SetValue(ExifTag.Artist, "Fixture");
        exif.SetValue(ExifTag.DateTimeOriginal, "2026:10:22 09:30:00");
        exif.SetValue(ExifTag.GPSLatitudeRef, "N");
        exif.SetValue(ExifTag.GPSLatitude, [new Rational(37, 1), new Rational(33, 1), new Rational(0, 1)]);
        exif.SetValue(ExifTag.GPSLongitudeRef, "E");
        exif.SetValue(ExifTag.GPSLongitude, [new Rational(126, 1), new Rational(58, 1), new Rational(0, 1)]);
        image.SetProfile(exif);
        image.Quality = 90;
        image.Write(path, MagickFormat.Jpeg);
    }

    private static void Logo(string path)
    {
        using var image = new MagickImage(MagickColors.Transparent, 512, 512);
        new Drawables().FillColor(new MagickColor("#0A84FF")).Circle(256, 256, 256, 40).FillColor(MagickColors.White).Circle(256, 256, 256, 170).Draw(image);
        image.Write(path, MagickFormat.Png);
    }

    private static void Sign(string path)
    {
        using var image = new MagickImage(MagickColors.White, 1400, 420);
        try
        {
            var drawables = new Drawables().FillColor(MagickColors.Black).FontPointSize(120).TextAlignment(TextAlignment.Left);
            var font = OperatingSystem.IsWindows() ? @"C:\Windows\Fonts\arialbd.ttf" : "/System/Library/Fonts/Supplemental/Arial Bold.ttf";
            if (File.Exists(font)) drawables = drawables.Font(font);
            drawables.Text(80, 260, "PICKVERTER 2026").Draw(image);
        }
        catch (MagickException)
        {
            new Drawables().FillColor(MagickColors.Black).Rectangle(80, 150, 1300, 290).Draw(image);
        }
        image.Write(path, MagickFormat.Png);
    }

    /// Three pages: text, blank, text (blank-page removal and range tests).
    public static void Report(string path)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 5 0 R 7 0 R] /Count 3 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 9 0 R >> >> >>",
            Stream("BT /F1 28 Tf 72 700 Td (Quarterly report) Tj ET\nBT /F1 14 Tf 72 660 Td (Revenue grew in every region this quarter.) Tj ET"),
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R >>",
            Stream(""),
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 8 0 R /Resources << /Font << /F1 9 0 R >> >> >>",
            Stream("BT /F1 28 Tf 72 700 Td (Appendix) Tj ET\nBT /F1 14 Tf 72 660 Td (Numbers and notes.) Tj ET"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.Append(index + 1).Append(" 0 obj\n").Append(objects[index]).Append("\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets) builder.Append(offset.ToString("0000000000", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        builder.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        File.WriteAllText(path, builder.ToString(), Encoding.ASCII);
    }

    private static string Stream(string content) => $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream";

    public static void Wav(string path, double seconds)
    {
        const int rate = 44_100;
        var samples = (int)(rate * seconds);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + samples * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples * 2);
        for (var index = 0; index < samples; index++)
        {
            var envelope = Math.Min(1, Math.Min(index / 2000.0, (samples - index) / 2000.0));
            writer.Write((short)(Math.Sin(2 * Math.PI * 440 * index / rate) * 12_000 * envelope));
        }
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    public static void Workbook(string path)
    {
        const string main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(zip, "[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
            "<Override PartName=\"/xl/worksheets/sheet2.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
            "<Override PartName=\"/xl/sharedStrings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml\"/>" +
            "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>");
        Add(zip, "_rels/.rels", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            $"<Relationship Id=\"rId1\" Type=\"{rel}/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
        Add(zip, "xl/workbook.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><workbook xmlns=\"{main}\" xmlns:r=\"{rel}\"><sheets>" +
            "<sheet name=\"Sales\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"Notes\" sheetId=\"2\" r:id=\"rId2\"/></sheets></workbook>");
        Add(zip, "xl/_rels/workbook.xml.rels", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            $"<Relationship Id=\"rId1\" Type=\"{rel}/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"{rel}/worksheet\" Target=\"worksheets/sheet2.xml\"/>" +
            $"<Relationship Id=\"rId3\" Type=\"{rel}/sharedStrings\" Target=\"sharedStrings.xml\"/><Relationship Id=\"rId4\" Type=\"{rel}/styles\" Target=\"styles.xml\"/></Relationships>");
        Add(zip, "xl/sharedStrings.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><sst xmlns=\"{main}\" count=\"6\" uniqueCount=\"6\">" +
            "<si><t>Region</t></si><si><t>Total</t></si><si><t>Date</t></si><si><t>North</t></si><si><t>South</t></si><si><t>Remember the ferry</t></si></sst>");
        Add(zip, "xl/styles.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><styleSheet xmlns=\"{main}\"><cellXfs count=\"2\"><xf numFmtId=\"0\"/><xf numFmtId=\"14\" applyNumberFormat=\"1\"/></cellXfs></styleSheet>");
        Add(zip, "xl/worksheets/sheet1.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"{main}\"><sheetData>" +
            "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c><c r=\"C1\" t=\"s\"><v>2</v></c></row>" +
            "<row r=\"2\"><c r=\"A2\" t=\"s\"><v>3</v></c><c r=\"B2\"><v>1250.5</v></c><c r=\"C2\" s=\"1\"><v>46317</v></c></row>" +
            "<row r=\"3\"><c r=\"A3\" t=\"s\"><v>4</v></c><c r=\"B3\"><v>980</v></c><c r=\"C3\" s=\"1\"><v>46318</v></c></row></sheetData></worksheet>");
        Add(zip, "xl/worksheets/sheet2.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"{main}\"><sheetData>" +
            "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>5</v></c></row></sheetData></worksheet>");
    }

    public static void Deck(string path, string imagePath)
    {
        const string p = "http://schemas.openxmlformats.org/presentationml/2006/main";
        const string a = "http://schemas.openxmlformats.org/drawingml/2006/main";
        const string rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(zip, "[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Default Extension=\"png\" ContentType=\"image/png\"/>" +
            "<Override PartName=\"/ppt/presentation.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml\"/>" +
            "<Override PartName=\"/ppt/slides/slide1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slide+xml\"/>" +
            "<Override PartName=\"/ppt/slides/slide2.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slide+xml\"/></Types>");
        Add(zip, "_rels/.rels", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            $"<Relationship Id=\"rId1\" Type=\"{rel}/officeDocument\" Target=\"ppt/presentation.xml\"/></Relationships>");
        Add(zip, "ppt/presentation.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><p:presentation xmlns:p=\"{p}\" xmlns:r=\"{rel}\"><p:sldIdLst>" +
            "<p:sldId id=\"256\" r:id=\"rId2\"/><p:sldId id=\"257\" r:id=\"rId3\"/></p:sldIdLst></p:presentation>");
        Add(zip, "ppt/_rels/presentation.xml.rels", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            $"<Relationship Id=\"rId2\" Type=\"{rel}/slide\" Target=\"slides/slide1.xml\"/><Relationship Id=\"rId3\" Type=\"{rel}/slide\" Target=\"slides/slide2.xml\"/></Relationships>");
        string Slide(string title, params string[] points) =>
            $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><p:sld xmlns:p=\"{p}\" xmlns:a=\"{a}\"><p:cSld><p:spTree>" +
            $"<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Title\"/><p:cNvSpPr/><p:nvPr><p:ph type=\"title\"/></p:nvPr></p:nvSpPr><p:txBody><a:p><a:r><a:t>{Xml.Escape(title)}</a:t></a:r></a:p></p:txBody></p:sp>" +
            "<p:sp><p:nvSpPr><p:cNvPr id=\"3\" name=\"Body\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:txBody>" +
            string.Concat(points.Select(point => $"<a:p><a:r><a:t>{Xml.Escape(point)}</a:t></a:r></a:p>")) + "</p:txBody></p:sp></p:spTree></p:cSld></p:sld>";
        Add(zip, "ppt/slides/slide1.xml", Slide("Welcome", "First point", "Second point"));
        Add(zip, "ppt/slides/slide2.xml", Slide("Next steps", "Ship it"));
        zip.CreateEntryFromFile(imagePath, "ppt/media/image1.png");
    }
}
