using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using ImageMagick;
using Xunit;

namespace Pickverter.Tests;

/// Sets environment variables for one test and puts the previous values back.
internal sealed class EnvironmentScope : IDisposable
{
    private readonly Dictionary<string, string?> saved = [];

    public EnvironmentScope Set(string name, string? value)
    {
        saved.TryAdd(name, Environment.GetEnvironmentVariable(name));
        Environment.SetEnvironmentVariable(name, value);
        return this;
    }

    public void Dispose()
    {
        foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
    }
}

/// rhwp's DocLang XML read into the document model, from inline XML (no rhwp needed).
public class DocLangTests
{
    private static Doc Parse(string body, string? folder = null) =>
        DocLang.Parse("<doclang version=\"0.6\">" + body + "</doclang>", folder, "doc");

    private static string Plain(IEnumerable<Run> runs) => Doc.Plain(runs);

    private static string Png(int width, int height, MagickFormat format = MagickFormat.Png)
    {
        using var image = new MagickImage(MagickColors.OrangeRed, (uint)width, (uint)height);
        return Convert.ToBase64String(image.ToByteArray(format));
    }

    [Fact]
    public void ReadsParagraphsHeadingsAndStyles()
    {
        var doc = Parse("<heading level=\"2\">Title <italic>here</italic></heading>" +
                        "<text>Plain <bold>bold</bold> <bold>next</bold> end\nsecond line</text><text>  </text>" +
                        "<text>A <href uri=\"https://example.com\">link</href> and x<superscript>2</superscript></text>" +
                        "<text><underline>u</underline><strikethrough>s</strikethrough><href uri=\"javascript:alert(1)\">no script</href></text>");
        Assert.Equal(4, doc.Blocks.Count);
        var heading = Assert.IsType<HeadingBlock>(doc.Blocks[0]);
        Assert.Equal(2, heading.Level);
        Assert.Equal("Title here", Plain(heading.Runs));
        Assert.Contains(heading.Runs, run => run.Text == "here" && run.Style.HasFlag(RunStyle.Italic));
        var first = Assert.IsType<ParagraphBlock>(doc.Blocks[1]);
        // The space between two bold words is content, and a newline is a line break.
        Assert.Equal("Plain bold next end\nsecond line", Plain(first.Runs));
        Assert.Contains(first.Runs, run => run.Text == "next" && run.Style == RunStyle.Bold);
        Assert.Contains(first.Runs, run => run.IsBreak);
        var second = Assert.IsType<ParagraphBlock>(doc.Blocks[2]);
        Assert.Equal("A link and x2", Plain(second.Runs));
        Assert.Contains(second.Runs, run => run.Text == "link" && run.Link == "https://example.com");
        var third = Assert.IsType<ParagraphBlock>(doc.Blocks[3]);
        Assert.Contains(third.Runs, run => run.Text == "u" && run.Style == RunStyle.Underline);
        Assert.Contains(third.Runs, run => run.Text == "s" && run.Style == RunStyle.Strike);
        Assert.All(third.Runs, run => Assert.Null(run.Link));
    }

    [Fact]
    public void ReadsListsWithNesting()
    {
        var doc = Parse("<list class=\"ordered\"><ldiv><marker>1.</marker></ldiv><text>First</text><ldiv/><text>Second</text><text>continued</text>" +
                        "<list class=\"unordered\"><ldiv/><text>Nested</text></list><ldiv/></list>" +
                        "<list class=\"unordered\"><ldiv/><text>Dot</text></list>");
        var ordered = Assert.IsType<ListBlock>(doc.Blocks[0]);
        Assert.True(ordered.Ordered);
        Assert.Equal(["First", "Second\ncontinued", "Nested"], ordered.Entries.Select(entry => Plain(entry.Runs)));
        Assert.Equal([0, 0, 1], ordered.Entries.Select(entry => entry.Level));
        var bullets = Assert.IsType<ListBlock>(doc.Blocks[1]);
        Assert.False(bullets.Ordered);
        Assert.Equal("Dot", Plain(bullets.Entries.Single().Runs));
        Assert.DoesNotContain("1.", doc.PlainText().Replace("1. First", ""));
    }

    [Fact]
    public void ReadsOtslTables()
    {
        var doc = Parse("<table><caption>Sales <bold>2026</bold></caption><ched/><text>Region</text><ched/><text>Total</text><lcel/><nl/>" +
                        "<fcel/><text>North</text><fcel/><text>1</text><text>2</text><ecel/><nl/>" +
                        "<ucel/><fcel/><table><fcel/><text>a</text><fcel/><text>b</text><nl/><fcel/><text>c</text><fcel/><text>d</text><nl/></table><xcel/><nl/></table>" +
                        "<table><fcel/><text>box</text><nl/></table>");
        Assert.Equal(3, doc.Blocks.Count);
        var caption = Assert.IsType<ParagraphBlock>(doc.Blocks[0]);
        Assert.Equal("Sales 2026", Plain(caption.Runs));
        Assert.All(caption.Runs, run => Assert.True(run.Style.HasFlag(RunStyle.Italic)));
        Assert.Contains(caption.Runs, run => run.Text == "2026" && run.Style.HasFlag(RunStyle.Bold));
        var table = Assert.IsType<TableBlock>(doc.Blocks[1]);
        Assert.True(table.HeaderRow);
        Assert.Equal(3, table.Rows.Count);
        Assert.All(table.Rows, row => Assert.Equal(3, row.Count));
        Assert.Equal(["Region", "Total", ""], table.Rows[0].Select(Plain));
        Assert.Equal(["North", "1\n2", ""], table.Rows[1].Select(Plain));
        // A table inside a cell: one line per row, cells apart by tabs.
        Assert.Equal(["", "a\tb\nc\td", ""], table.Rows[2].Select(Plain));
        var box = Assert.IsType<TableBlock>(doc.Blocks[2]);
        Assert.False(box.HeaderRow);
        Assert.Equal("box", Plain(box.Rows.Single().Single()));
    }

    [Fact]
    public void KeepsPicturesFromDataUrisAndTheFolderOnly()
    {
        using var sandbox = new Sandbox();
        var folder = Path.Combine(sandbox.Root, "run");
        Directory.CreateDirectory(Path.Combine(folder, "assets"));
        File.WriteAllBytes(Path.Combine(folder, "assets", "p.png"), Convert.FromBase64String(Png(5, 2)));
        File.WriteAllBytes(Path.Combine(sandbox.Root, "outside.png"), Convert.FromBase64String(Png(6, 6)));
        var doc = Parse($"<picture><src uri=\"data:image/png;base64,{Png(4, 3)}\"/></picture>" +
                        $"<picture><src uri=\"data:image/bmp;base64,{Png(7, 2, MagickFormat.Bmp)}\"/></picture>" +
                        $"<table><fcel/><picture><src uri=\"data:image/png;base64,{Png(2, 2)}\"/></picture><text>Logo</text><nl/></table>" +
                        "<picture><src uri=\"assets/p.png\"/></picture>" +
                        "<picture><src uri=\"../outside.png\"/></picture>" +
                        $"<picture><src uri=\"{Path.Combine(sandbox.Root, "outside.png")}\"/></picture>" +
                        "<picture><src uri=\"https://example.com/p.png\"/></picture>", folder);
        var images = doc.Blocks.OfType<ImageBlock>().ToList();
        Assert.Equal([(4, 3), (7, 2), (2, 2), (5, 2)], images.Select(image => (image.Width, image.Height)));
        // Uncompressed BMP (common in HWP) becomes lossless PNG.
        Assert.Equal("png", images[1].Ext);
        Assert.Equal(0x89, images[1].Data[0]);
        // The model's tables hold text: a picture from a cell follows its table.
        var table = doc.Blocks.FindIndex(block => block is TableBlock);
        Assert.Equal("Logo", Plain(((TableBlock)doc.Blocks[table]).Rows[0][0]));
        Assert.IsType<ImageBlock>(doc.Blocks[table + 1]);
    }

    [Fact]
    public void KeepsTheStyleOfInlineElementsOutsideText()
    {
        var doc = Parse("<bold>Loose</bold><list class=\"unordered\"><ldiv/><bold>Strong</bold></list><table><fcel/><italic>lean</italic><nl/></table>");
        Assert.Contains(Assert.IsType<ParagraphBlock>(doc.Blocks[0]).Runs, run => run.Text == "Loose" && run.Style == RunStyle.Bold);
        Assert.Contains(Assert.IsType<ListBlock>(doc.Blocks[1]).Entries[0].Runs, run => run.Text == "Strong" && run.Style == RunStyle.Bold);
        Assert.Contains(Assert.IsType<TableBlock>(doc.Blocks[2]).Rows[0][0], run => run.Text == "lean" && run.Style == RunStyle.Italic);
    }

    [Fact]
    public void FootnotesComeLastAndLayoutPartsStayOut()
    {
        var doc = Parse("<page_header><text>Header text</text></page_header><text>Body</text>" +
                        "<footnote><text>First note</text><text>more</text></footnote><group><thread thread_id=\"1\"/></group><text>After</text>" +
                        "<page_footer><text>Page 1</text></page_footer><page_break/><formula>x^2 + y^2</formula>" +
                        "<custom><hwp_prop>key=value</hwp_prop></custom><location value=\"1\" resolution=\"512\"/><footnote><text>Second note</text></footnote>");
        Assert.Equal(5, doc.Blocks.Count);
        Assert.Equal("Body", Plain(Assert.IsType<ParagraphBlock>(doc.Blocks[0]).Runs));
        Assert.Equal("After", Plain(Assert.IsType<ParagraphBlock>(doc.Blocks[1]).Runs));
        var formula = Assert.IsType<ParagraphBlock>(doc.Blocks[2]);
        Assert.Equal("x^2 + y^2", Plain(formula.Runs));
        Assert.All(formula.Runs, run => Assert.True(run.Style.HasFlag(RunStyle.Code)));
        Assert.IsType<RuleBlock>(doc.Blocks[3]);
        var notes = Assert.IsType<ListBlock>(doc.Blocks[4]);
        Assert.True(notes.Ordered);
        Assert.Equal(["First note more", "Second note"], notes.Entries.Select(entry => Plain(entry.Runs)));
        var text = doc.PlainText();
        Assert.DoesNotContain("Header text", text);
        Assert.DoesNotContain("Page 1", text);
        Assert.DoesNotContain("key=value", text);
    }

    [Fact]
    public void NeverThrowsOnOddInput()
    {
        foreach (var xml in new[]
                 {
                     "", "not xml at all <<<", "\0\u0001binary", "<doclang><text>kept</text><text>unclosed",
                     "<doclang><text>a &undefined; b</text></doclang>",
                     "<?xml version=\"1.0\"?><!DOCTYPE d [<!ENTITY x SYSTEM \"file:///etc/hosts\">]><doclang><text>before &x; after</text></doclang>",
                     "<doclang><list><ldiv/></list><table><nl/><nl/></table><table/><heading level=\"x\">H</heading><picture/>" +
                     "<picture><src uri=\"data:image/png;base64,!!!\"/></picture><picture><src uri=\"data:,plain\"/></picture><lcel/><unknown><bold>B</bold></unknown></doclang>",
                     "<html><body><p>Some <b>bold</b> text</p></body></html>",
                 })
        {
            var doc = DocLang.Parse(xml, null, "odd");
            Assert.NotNull(doc.PlainText());
            Assert.DoesNotContain("localhost", doc.PlainText());
        }
        Assert.Contains("kept", DocLang.Parse("<doclang><text>kept</text><text>unclosed", null, "x").PlainText());
        var mixed = DocLang.Parse("<doclang><heading level=\"x\">H</heading><unknown><bold>B</bold></unknown></doclang>", null, "x");
        Assert.Equal(1, Assert.IsType<HeadingBlock>(mixed.Blocks[0]).Level);
        Assert.Equal("B", Plain(Assert.IsType<ParagraphBlock>(mixed.Blocks[1]).Runs));
        using var noise = new MemoryStream(Enumerable.Range(0, 4096).Select(index => (byte)(index * 37 % 251)).ToArray());
        Assert.Empty(DocLang.Parse(noise, null, "noise").Blocks);
        Assert.Empty(DocLang.Read(Path.Combine(Path.GetTempPath(), "pickverter-missing-" + Guid.NewGuid().ToString("N") + ".xml"), null, "none").Blocks);
    }
}

/// The rhwp download: which archive, what gets unpacked, and when HWP options offer it.
public class HwpSetupTests
{
    [Fact]
    public void PicksThePinnedArchiveForThisComputer()
    {
        Assert.Equal(HwpSetup.WindowsX64, HwpSetup.ArchiveFor(true, false, Architecture.X64));
        // Windows on Arm runs the x64 build (Windows 11's emulation).
        Assert.Equal(HwpSetup.WindowsX64, HwpSetup.ArchiveFor(true, false, Architecture.Arm64));
        Assert.Equal(HwpSetup.MacArm64, HwpSetup.ArchiveFor(false, true, Architecture.Arm64));
        Assert.Equal(HwpSetup.MacX64, HwpSetup.ArchiveFor(false, true, Architecture.X64));
        Assert.Null(HwpSetup.ArchiveFor(false, false, Architecture.X64));
        Assert.Equal("https://github.com/edwardkim/rhwp/releases/download/v0.8.7/rhwp-v0.8.7-windows-x86_64.zip", HwpSetup.WindowsX64.Url);
        Assert.All([HwpSetup.WindowsX64, HwpSetup.MacArm64, HwpSetup.MacX64], asset => Assert.Matches("^[0-9a-f]{64}$", asset.Sha256));
        Assert.False(HwpSetup.Runs(true, Architecture.Arm64, 19045));
        Assert.True(HwpSetup.Runs(true, Architecture.Arm64, 22631));
        Assert.True(HwpSetup.Runs(true, Architecture.X64, 19045));
        Assert.True(HwpSetup.Runs(false, Architecture.Arm64, 0));
    }

    [Fact]
    public void MalgunGothicStandsInWhereKoreanFontsAreMissing()
    {
        using var sandbox = new Sandbox();
        var system = Path.Combine(sandbox.Root, "Fonts");
        var user = Path.Combine(sandbox.Root, "UserFonts");
        Directory.CreateDirectory(system);
        Directory.CreateDirectory(user);
        // An English Windows: no Batang, no D2Coding.
        Assert.Equal(["--fallback-serif", "Malgun Gothic", "--fallback-mono", "Malgun Gothic"], HwpEngine.FontFallbacks([system, user]));
        Assert.Equal(["--fallback-serif", "Malgun Gothic", "--fallback-mono", "Malgun Gothic"], HwpEngine.FontFallbacks([Path.Combine(sandbox.Root, "missing")]));
        // A Korean Windows has Batang; D2Coding installed for one account counts too.
        File.WriteAllBytes(Path.Combine(system, "batang.ttc"), []);
        Assert.Equal(["--fallback-mono", "Malgun Gothic"], HwpEngine.FontFallbacks([system, user]));
        File.WriteAllBytes(Path.Combine(user, "D2Coding-Ver1.3.2-20180524.ttc"), []);
        Assert.Empty(HwpEngine.FontFallbacks([system, user]));
    }

    [Fact]
    public void UnpacksOnlyTheProgramAndItsLicense()
    {
        using var sandbox = new Sandbox();
        var zip = Path.Combine(sandbox.Root, "rhwp.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "rhwp/rhwp.exe", "rhwp/LICENSE", "rhwp/README.md", "other/rhwp.exe", "rhwp/../evil.exe" })
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(name);
            }
        }
        var fromZip = Path.Combine(sandbox.Root, "zip");
        Assert.Equal("rhwp.exe", HwpSetup.Extract(zip, fromZip));
        Assert.Equal(["LICENSE", "rhwp.exe"], Directory.GetFiles(fromZip).Select(Path.GetFileName).Order());
        Assert.Equal("rhwp/rhwp.exe", File.ReadAllText(Path.Combine(fromZip, "rhwp.exe")));

        // The macOS archives hold the "rhwp" folder itself as an entry: only regular files count.
        var tgz = Path.Combine(sandbox.Root, "rhwp.tar.gz");
        using (var file = File.Create(tgz))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Ustar))
        {
            tar.WriteEntry(new UstarTarEntry(TarEntryType.Directory, "rhwp/"));
            foreach (var name in new[] { "rhwp/rhwp", "rhwp/LICENSE", "rhwp/README_EN.md" })
                tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(name)) });
        }
        var fromTar = Path.Combine(sandbox.Root, "tar");
        Assert.Equal("rhwp", HwpSetup.Extract(tgz, fromTar));
        Assert.Equal(["LICENSE", "rhwp"], Directory.GetFiles(fromTar).Select(Path.GetFileName).Order());
        if (!OperatingSystem.IsWindows()) Assert.True(File.GetUnixFileMode(Path.Combine(fromTar, "rhwp")).HasFlag(UnixFileMode.UserExecute));

        // An archive without the program is not installed.
        var empty = Path.Combine(sandbox.Root, "empty.zip");
        using (var archive = ZipFile.Open(empty, ZipArchiveMode.Create)) archive.CreateEntry("rhwp/LICENSE");
        Assert.Throws<PickException>(() => HwpSetup.Extract(empty, Path.Combine(sandbox.Root, "none")));
    }

    [Fact]
    public void OptionsOfferTheDownloadUntilThePinnedVersionIsInstalled()
    {
        using var sandbox = new Sandbox();
        var folder = Path.Combine(sandbox.Root, "rhwp");
        using var environment = new EnvironmentScope().Set("PICKVERTER_RHWP_DIR", folder).Set("PICKVERTER_RHWP", null);
        if (!HwpSetup.RunsHere) return;
        var document = sandbox.Fixture("report.hwpx");
        Assert.Equal(FileKind.Hwp, FileKinds.Of(document));
        Assert.Null(HwpSetup.Executable);
        Assert.Equal(Tools.HwpHint, HwpSetup.Unavailable());

        var convert = Catalog.Options([document], WheelMode.Convert);
        Assert.Equal(["convert.pdf", "convert.docx", "convert.hwp", "convert.png", "convert.jpg", "convert.txt", "convert.md", "convert.html"],
                     convert.Select(option => option.Id));
        Assert.Equal(["300 dpi", "150 dpi", "600 dpi"], convert.First(option => option.Id == "convert.png").Variants.Select(variant => variant.Title));
        Assert.Equal(Tools.HwpHint, convert.First(option => option.Id == "convert.pdf").Unavailable);
        Assert.Equal(["tool.copytext", "tool.count", "tool.zip"], Catalog.Options([document], WheelMode.Tools).Select(option => option.Id));
        Assert.Contains("convert.hwpx", Catalog.Options([Path.ChangeExtension(document, ".hwp")], WheelMode.Convert).Select(option => option.Id));

        Assert.True(Tools.IsDownload(Tools.HwpHint));
        Assert.Equal(L("hwp.click"), Tools.DownloadPrompt(Tools.HwpHint));
        Assert.Null(Tools.Preparing(convert[0], Tools.HwpHint).Unavailable);
        Assert.Equal(Tools.HwpHint, Tools.MissingEngine(new PickException("report.hwpx: " + Tools.HwpHint)));
        Assert.Equal(L("engine.install_hwp"), Tools.InstallTitle(Tools.HwpHint));

        // Like ffmpeg: the wheel badges the slot and a click runs it (the job downloads rhwp first).
        var wheel = new WheelModel(demo: true);
        wheel.Present([document], WheelModel.WheelOrigin.Selection, WheelMode.Convert, ScreenPoint.Zero);
        wheel.ForceVisibleForTesting();
        wheel.DestinationOverride = ResultDestination.File;
        PickOption? launched = null;
        wheel.Launched += (option, _, _, _, _) => launched = option;
        var slot = wheel.Items.ToList().FindIndex(item => item.AsOption?.Id == "convert.pdf");
        Assert.True(wheel.Segments[slot].NeedsDownload);
        wheel.Commit(slot);
        Assert.Null(launched?.Unavailable);
        Assert.NotNull(launched);
        Assert.Null(wheel.Flash);

        // Only the pinned version counts.
        Directory.CreateDirectory(folder);
        var binary = Path.Combine(folder, HwpSetup.BinaryName);
        File.WriteAllText(binary, "stand-in");
        File.WriteAllText(Path.Combine(folder, "version.txt"), "0.8.6\n");
        Assert.Null(HwpSetup.Executable);
        File.WriteAllText(Path.Combine(folder, "version.txt"), HwpSetup.Version + "\n");
        Assert.Equal(binary, HwpSetup.Executable);
        Assert.True(HwpSetup.IsManaged);
        Assert.Null(HwpSetup.Unavailable());
        Assert.All(Catalog.Options([document], WheelMode.Convert).Where(option => option.Id is not ("convert.png" or "convert.jpg")),
                   option => Assert.Null(option.Unavailable));
        environment.Set("PICKVERTER_RHWP", Path.Combine(sandbox.Root, "missing-rhwp"));
        Assert.Null(HwpSetup.Executable);
        environment.Set("PICKVERTER_RHWP", binary);
        Assert.Equal(binary, HwpSetup.Executable);
    }

    /// The real download (≈20 MB from GitHub) into PICKVERTER_RHWP_DIR when it's set, otherwise a scratch folder.
    /// Opt in with PICKVERTER_NET_TESTS=1.
    [Fact]
    public async Task DownloadsThePinnedRelease()
    {
        if (Environment.GetEnvironmentVariable("PICKVERTER_NET_TESTS") != "1") return;
        using var sandbox = new Sandbox();
        var folder = Environment.GetEnvironmentVariable("PICKVERTER_RHWP_DIR") is { Length: > 0 } custom ? custom : Path.Combine(sandbox.Root, "rhwp");
        var seen = new List<double>();
        var installing = false;
        await HwpSetup.DownloadAsync(folder, HwpSetup.Archive!, seen.Add, () => installing = true, TestContext.Current.CancellationToken);
        Assert.True(installing);
        Assert.NotEmpty(seen);
        Assert.True(File.Exists(Path.Combine(folder, HwpSetup.BinaryName)));
        Assert.True(File.Exists(Path.Combine(folder, "LICENSE")));
        Assert.Equal(HwpSetup.Version, File.ReadAllText(Path.Combine(folder, "version.txt")).Trim());
        Assert.Empty(Directory.GetDirectories(folder));
        var banner = await Shell.RunAsync(Path.Combine(folder, HwpSetup.BinaryName), ["--version"], token: TestContext.Current.CancellationToken);
        Assert.Contains("0.8.7", banner);
    }
}

/// HWP conversions through a real rhwp. They run when one is installed (PICKVERTER_RHWP_DIR or PICKVERTER_RHWP), as in
/// CI after "cli rhwp-install"; elsewhere they return at once.
public class HwpEngineTests
{
    private static bool Ready =>
        (Environment.GetEnvironmentVariable("PICKVERTER_RHWP_DIR") is { Length: > 0 } || Environment.GetEnvironmentVariable("PICKVERTER_RHWP") is { Length: > 0 })
        && HwpSetup.Executable != null;

    private const string Quote = "두 번째 문단: 숫자 12345와 기호 % & < > \"따옴표\"도 그대로 남아야 해요.";

    private static string PdfText(string path)
    {
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(path);
        return string.Join("\n", pdf.GetPages().Select(page => page.Text));
    }

    /// Draws white pages two by three inches: the core has no PDF renderer of its own (Windows' renders in the app).
    private sealed class BlankRenderer : IPdfRenderer
    {
        public List<double> Dpis { get; } = [];
        public bool Available => true;

        public Task RenderPageAsync(string pdf, int index, double dpi, string pngPath, CancellationToken token)
        {
            Dpis.Add(dpi);
            using var image = new MagickImage(MagickColors.White, (uint)(2 * dpi), (uint)(3 * dpi));
            image.Write(pngPath, MagickFormat.Png);
            return Task.CompletedTask;
        }
    }

    /// Answers the password question and counts how often it was asked.
    private sealed class Unlocker(string answer) : InjectedEditors
    {
        public List<bool> Asked { get; } = [];

        public override Task<string> UnlockAsync(string file, bool retry, CancellationToken token)
        {
            Asked.Add(retry);
            if (answer == "cancel") throw new OperationCanceledException();
            return Task.FromResult(answer);
        }
    }

    [Fact]
    public async Task ConvertsToPdfAndBetweenHwpAndHwpx()
    {
        if (!Ready) return;
        using var sandbox = new Sandbox();
        var report = sandbox.Fixture("report.hwpx");
        var pdf = Sandbox.Output(await Sandbox.Run("convert.pdf", report));
        Assert.Equal(Path.Combine(Path.GetDirectoryName(report)!, "report.pdf"), pdf);
        Assert.Equal(1, Sandbox.Pages(pdf));
        Assert.Contains("Pickverter", PdfText(pdf));

        var hwp = Sandbox.Output(await Sandbox.Run("convert.hwp", report));
        Assert.EndsWith("report.hwp", hwp);
        // HWP 5 is an OLE compound file.
        Assert.Equal([0xD0, 0xCF, 0x11, 0xE0], File.ReadAllBytes(hwp)[..4]);
        var text = File.ReadAllText(Sandbox.Output(await Sandbox.Run("convert.txt", hwp)));
        Assert.Contains(Quote, text);
        Assert.Contains("표 다음 문단입니다. 끝.", text);

        var back = Sandbox.Output(await Sandbox.Run("convert.hwpx", hwp));
        Assert.EndsWith(".hwpx", back);
        Assert.Equal("application/hwp+zip", Sandbox.ZipText(back, "mimetype"));
        Assert.True(File.Exists(report));
    }

    [Fact]
    public async Task ConvertsToDocumentsThroughDocLang()
    {
        if (!Ready) return;
        using var sandbox = new Sandbox();
        var report = sandbox.Fixture("report.hwpx");
        var docx = Sandbox.Output(await Sandbox.Run("convert.docx", report));
        var doc = Docx.Read(docx, "report");
        Assert.Contains("Pickverter 한글 문서", doc.PlainText());
        Assert.Contains(Quote, doc.PlainText());
        var table = Assert.Single(doc.Blocks.OfType<TableBlock>());
        Assert.Equal(["항목", "Value"], table.Rows[0].Select(Doc.Plain));
        Assert.Equal(["사과", "1,200원"], table.Rows[1].Select(Doc.Plain));
        var body = Sandbox.ZipText(docx, "word/document.xml");
        // Borders on the table itself (Pages and Quick Look ignore table styles); no header row: the HWP table has none.
        Assert.Contains("<w:tbl><w:tblPr><w:tblStyle w:val=\"TableGrid\"/><w:tblW w:w=\"0\" w:type=\"auto\"/><w:tblBorders>", body);
        Assert.DoesNotContain("<w:tblHeader/>", body);

        var text = File.ReadAllText(Sandbox.Output(await Sandbox.Run("convert.txt", report)));
        Assert.StartsWith("Pickverter 한글 문서\n\n", text);
        Assert.Contains(Quote, text);
        Assert.Contains("사과\t1,200원", text);
        var markdown = File.ReadAllText(Sandbox.Output(await Sandbox.Run("convert.md", report)));
        Assert.Contains("**Pickverter 한글 문서**", markdown);
        Assert.Contains("| 사과 | 1,200원 |", markdown);
        var html = File.ReadAllText(Sandbox.Output(await Sandbox.Run("convert.html", report)));
        Assert.Contains("<td>1,200원</td>", html);
        Assert.Contains("따옴표", html);
    }

    [Fact]
    public async Task RendersPagesNamedAfterTheDocument()
    {
        if (!Ready) return;
        using var sandbox = new Sandbox();
        var renderer = new BlankRenderer();
        var previous = Platform.Pdf;
        Platform.Pdf = renderer;
        try
        {
            var report = sandbox.Fixture("report.hwpx");
            var png = Sandbox.Output(await Sandbox.Run("convert.png", report));
            Assert.Equal(Path.Combine(Path.GetDirectoryName(report)!, "report.png"), png);
            var jpg = Sandbox.Output(await Sandbox.Run("convert.jpg.150", report));
            Assert.Equal(Path.Combine(Path.GetDirectoryName(report)!, "report.jpg"), jpg);
            Assert.Equal([300.0, 150.0], renderer.Dpis);
            Assert.Equal((300u, 450u), Sandbox.Size(jpg));
            Assert.Equal((600u, 900u), Sandbox.Size(png));
        }
        finally
        {
            Platform.Pdf = previous;
        }
    }

    [Fact]
    public async Task CopiesAndCountsText()
    {
        if (!Ready) return;
        using var sandbox = new Sandbox();
        var report = sandbox.Fixture("report.hwpx");
        var copied = await Sandbox.Run("tool.copytext", report);
        Assert.Contains("Pickverter 한글 문서", sandbox.Clipboard.Text);
        Assert.Contains("1,200원", sandbox.Clipboard.Text);
        Assert.NotNull(copied.Message);
        var counted = await Sandbox.Run("tool.count", report, sandbox.Fixture("report.hwpx"));
        Assert.NotNull(counted.Message);
    }

    [Fact]
    public async Task OddNamesStayIntact()
    {
        if (!Ready) return;
        using var sandbox = new Sandbox();
        var source = sandbox.Fixture("report.hwpx");
        var odd = Path.Combine(Path.GetDirectoryName(source)!, "보도 자료 [최종] 50%.hwpx");
        File.Move(source, odd);
        Assert.EndsWith("보도 자료 [최종] 50%.pdf", Sandbox.Output(await Sandbox.Run("convert.pdf", odd)));
        Assert.EndsWith("보도 자료 [최종] 50%.docx", Sandbox.Output(await Sandbox.Run("convert.docx", odd)));
        Assert.EndsWith("보도 자료 [최종] 50%.hwp", Sandbox.Output(await Sandbox.Run("convert.hwp", odd)));
    }

    [Fact]
    public async Task AsksForAPasswordOncePerConversion()
    {
        if (!Ready) return;
        using var sandbox = new Sandbox();
        var report = sandbox.Fixture("report.hwpx");
        var locked = Path.Combine(Path.GetDirectoryName(report)!, "locked.hwpx");
        await Shell.RunAsync(HwpSetup.Executable!, ["export-hwpx", report, locked, "--output-password", "pv1234"], token: TestContext.Current.CancellationToken);
        var previous = Platform.Editors;
        try
        {
            var right = new Unlocker("pv1234");
            Platform.Editors = right;
            Assert.Equal(1, Sandbox.Pages(Sandbox.Output(await Sandbox.Run("convert.pdf", locked))));
            Assert.Contains(Quote, Docx.Read(Sandbox.Output(await Sandbox.Run("convert.docx", locked)), "x").PlainText());
            Assert.EndsWith(".hwp", Sandbox.Output(await Sandbox.Run("convert.hwp", locked)));
            Assert.Equal([false, false, false], right.Asked);
            // One job that reads the same locked document twice asks once.
            right.Asked.Clear();
            await Sandbox.Run("tool.copytext", locked, locked);
            Assert.Single(right.Asked);

            var wrong = new Unlocker("nope");
            Platform.Editors = wrong;
            var failure = await Assert.ThrowsAsync<PickException>(() => Sandbox.Run("convert.pdf", locked));
            Assert.Equal(L("err.hwp_password"), failure.Message);
            Assert.Equal([false, true, true], wrong.Asked);

            Platform.Editors = new Unlocker("cancel");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Sandbox.Run("convert.txt", locked));

            // The command line and CI answer through PICKVERTER_EDITOR_UNLOCK.
            Platform.Editors = new InjectedEditors();
            using (Sandbox.Editor("unlock", "pv1234"))
                Assert.Contains("1,200원", File.ReadAllText(Sandbox.Output(await Sandbox.Run("convert.md", locked))));
            using (Sandbox.Editor("unlock", "cancel"))
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Sandbox.Run("convert.pdf", locked));
        }
        finally
        {
            Platform.Editors = previous;
        }
    }

    /// Every HWP and HWPX in the folder PICKVERTER_HWP_SAMPLES names, to every format (opt in: real documents from the
    /// field). Locked ones open with PICKVERTER_HWP_PASSWORD (default 123456, rhwp's own samples). The results stay in
    /// PICKVERTER_HWP_SAMPLES_OUT when it's set.
    [Fact]
    public async Task ConvertsRealDocuments()
    {
        if (!Ready || Environment.GetEnvironmentVariable("PICKVERTER_HWP_SAMPLES") is not { Length: > 0 } samples) return;
        using var sandbox = new Sandbox();
        var root = Environment.GetEnvironmentVariable("PICKVERTER_HWP_SAMPLES_OUT") is { Length: > 0 } keep ? keep : sandbox.Root;
        var renderer = new BlankRenderer();
        var (pdfRenderer, editors) = (Platform.Pdf, Platform.Editors);
        Platform.Pdf = renderer;
        Platform.Editors = new Unlocker(Environment.GetEnvironmentVariable("PICKVERTER_HWP_PASSWORD") ?? "123456");
        var failures = new List<string>();
        try
        {
            foreach (var sample in Directory.GetFiles(samples).Where(file => FileKinds.Of(file) == FileKind.Hwp).Order(StringComparer.Ordinal))
            {
                var folder = Path.Combine(root, FileNames.Base(sample) + "-" + FileNames.NormalizedExt(sample));
                Directory.CreateDirectory(folder);
                var file = Path.Combine(folder, Path.GetFileName(sample));
                File.Copy(sample, file);
                foreach (var ext in HwpEngine.Targets(FileNames.NormalizedExt(file)))
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        var output = Sandbox.Output(await Sandbox.Run("convert." + ext, file));
                        var detail = ext switch
                        {
                            "pdf" => Sandbox.Pages(output) + " pages",
                            "hwp" => File.ReadAllBytes(output)[..4].SequenceEqual(new byte[] { 0xD0, 0xCF, 0x11, 0xE0 }) ? "ole" : throw new PickException("not OLE"),
                            "hwpx" => Sandbox.ZipText(output, "mimetype"),
                            "docx" => Docx.Read(output, "x") is var doc ? $"{doc.Blocks.Count} blocks, {doc.Blocks.OfType<TableBlock>().Count()} tables, {doc.Blocks.OfType<ImageBlock>().Count()} pictures" : "",
                            "png" or "jpg" => Directory.Exists(output) ? Directory.GetFiles(output).Length + " pages" : "1 page",
                            _ => new FileInfo(output).Length + " bytes",
                        };
                        TestContext.Current.SendDiagnosticMessage($"ok   {Path.GetFileName(sample)} → {ext} ({watch.ElapsedMilliseconds} ms): {detail}");
                    }
                    catch (Exception error)
                    {
                        failures.Add($"{Path.GetFileName(sample)} → {ext}: {error.Message}");
                        TestContext.Current.SendDiagnosticMessage($"FAIL {Path.GetFileName(sample)} → {ext} ({watch.ElapsedMilliseconds} ms): {error.Message}");
                    }
                }
            }
        }
        finally
        {
            (Platform.Pdf, Platform.Editors) = (pdfRenderer, editors);
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
