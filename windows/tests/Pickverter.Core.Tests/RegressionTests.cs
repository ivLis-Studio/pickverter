using ImageMagick;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Xunit;

namespace Pickverter.Tests;

/// Real-world files the first releases tripped over.
public class RegressionTests
{
    /// Chrome, Word and macOS tag photos [/ICCBased profile] instead of /DeviceRGB; PDFsharp's GetName threw on that
    /// ("Object is not a name") and Compress crashed.
    [Fact]
    public async Task CompressesPdfsWhosePhotosCarryAnIccProfile()
    {
        using var sandbox = new Sandbox();
        var pdf = Path.Combine(sandbox.Root, "tagged.pdf");
        using (var photo = new MagickImage(MagickColors.CornflowerBlue, 1600, 1200))
        {
            photo.Quality = 98;
            var jpeg = Path.Combine(sandbox.Root, "photo.jpg");
            photo.Write(jpeg, MagickFormat.Jpeg);
            using var document = new PdfDocument();
            var page = document.AddPage();
            using (var graphics = PdfSharp.Drawing.XGraphics.FromPdfPage(page))
            using (var image = PdfSharp.Drawing.XImage.FromFile(jpeg))
            {
                graphics.DrawImage(image, 0, 0, page.Width.Point, page.Height.Point);
            }
            document.Save(pdf);
        }
        using (var document = PdfReader.Open(pdf, PdfDocumentOpenMode.Modify))
        {
            var objects = document.Pages[0].Elements.GetDictionary("/Resources")!.Elements.GetDictionary("/XObject")!;
            var image = (PdfDictionary)((PdfReference)objects.Elements.Values.First()!).Value;
            var profile = new PdfDictionary(document);
            profile.CreateStream([0, 0, 0, 0]);
            profile.Elements.SetInteger("/N", 3);
            document.Internals.AddObject(profile);
            image.Elements["/ColorSpace"] = new PdfArray(document, new PdfName("/ICCBased"), profile.Reference!);
            document.Save(pdf);
        }
        try
        {
            var output = Sandbox.Output(await Sandbox.Run("tool.compress", pdf));
            Assert.Equal(1, Sandbox.Pages(output));
        }
        catch (PickException error)
        {
            Assert.Equal(L("err.already_small"), error.Message);
        }
    }

    /// Statements and forms often open without a password but carry an owner password (restrictions only). Rotate and
    /// Password said "password-protected" although Split and Extract worked.
    [Fact]
    public async Task EditsPdfsThatOnlyCarryRestrictions()
    {
        using var sandbox = new Sandbox();
        var report = sandbox.Fixture("report.pdf");
        var restricted = Path.Combine(Path.GetDirectoryName(report)!, "restricted.pdf");
        using (var document = PdfReader.Open(report, PdfDocumentOpenMode.Modify))
        {
            document.SecuritySettings.OwnerPassword = "owner-only";
            document.SecuritySettings.PermitModifyDocument = false;
            document.Save(restricted);
        }
        var rotated = Sandbox.Output(await Sandbox.Run("tool.rotate.right", restricted));
        using (var opened = PdfReader.Open(rotated, PdfDocumentOpenMode.Import))
        {
            Assert.Equal(3, opened.PageCount);
            Assert.Equal(90, opened.Pages[0].Rotate);
        }
        using (Sandbox.Editor("password", "secret"))
        {
            var locked = Sandbox.Output(await Sandbox.Run("tool.lock", restricted));
            using var opened = PdfReader.Open(locked, "secret", PdfDocumentOpenMode.Import);
            Assert.Equal(3, opened.PageCount);
        }
    }

    /// ImageMagick reads "%d" and friends in a file name as frame numbers when it writes several frames, so icons named
    /// "… 50%.ico" came out under another name and the job said nothing was produced.
    [Fact]
    public async Task WritesIconsWhoseNamesHoldAPercentSign()
    {
        using var sandbox = new Sandbox();
        var logo = sandbox.Fixture("logo.png");
        var odd = Path.Combine(Path.GetDirectoryName(logo)!, "sale 50%.png");
        File.Move(logo, odd);
        var icon = Sandbox.Output(await Sandbox.Run("convert.ico", odd));
        Assert.EndsWith("sale 50%.ico", icon);
        var smaller = Sandbox.Output(await Sandbox.Run("tool.resize.50", icon));
        using var frames = new MagickImageCollection(smaller);
        Assert.Equal(128u, frames.Max(frame => frame.Width));
    }

    /// ImageMagick has no reader for the .icns container: the PNG inside is decoded instead.
    [Fact]
    public async Task ReadsMacIcons()
    {
        using var sandbox = new Sandbox();
        byte[] png;
        using (var picture = new MagickImage(MagickColors.Orange, 256, 256))
        {
            png = picture.ToByteArray(MagickFormat.Png);
        }
        var icns = Path.Combine(sandbox.Root, "app.icns");
        using (var stream = File.Create(icns))
        {
            void Write(uint value) => stream.Write([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
            stream.Write("icns"u8);
            Write((uint)(16 + png.Length));
            stream.Write("ic08"u8);
            Write((uint)(8 + png.Length));
            stream.Write(png);
        }
        var output = Sandbox.Output(await Sandbox.Run("convert.png", icns));
        Assert.Equal((256u, 256u), Sandbox.Size(output));
    }

    /// Password, Page range and "Choose…" open a dialog before any work. Their card used to appear at once, covering
    /// the dialog's buttons, and stayed behind as "Cancelled" when the dialog was closed.
    [Fact]
    public void DialogsComeBeforeTheirCards()
    {
        using var sandbox = new Sandbox();
        var options = Catalog.AllOptions([sandbox.Fixture("report.pdf")]).ToDictionary(option => option.Id);
        Assert.True(options["tool.lock"].Interactive);
        Assert.True(options["tool.pages.range"].Interactive);
        Assert.True(options["action.move.choose"].Interactive);
        Assert.True(options["action.duplicate.choose"].Interactive);
        // Picked from the ring itself, Move and Duplicate run "Choose…".
        Assert.True(options["action.move"].Interactive);
        Assert.True(options["action.duplicate"].Interactive);
        Assert.False(options["tool.compress"].Interactive);
    }
}

