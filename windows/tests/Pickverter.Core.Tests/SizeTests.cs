using System.Globalization;
using ImageMagick;
using Xunit;

namespace Pickverter.Tests;

public class SizeTests
{
    /// A noisy picture: big, and it stays big under compression.
    private static string Noise(string folder, string name, uint width, uint height, double? alpha = null)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        using var image = new MagickImage(new MagickColor("#808080"), width, height);
        image.AddNoise(NoiseType.Random);
        if (alpha is { } opacity)
        {
            image.Alpha(AlphaOption.Set);
            image.Evaluate(Channels.Alpha, EvaluateOperator.Multiply, opacity);
        }
        image.Quality = 95;
        image.Write(path);
        return path;
    }

    private static PickOption Option(string file, string id) => Catalog.AllOptions([file]).First(option => option.Id == id);

    private sealed class Scratch : IDisposable
    {
        private readonly string previous = SizeEstimator.ScratchRoot;

        public Scratch(Sandbox sandbox)
        {
            Folder = Path.Combine(sandbox.Root, "estimates");
            SizeEstimator.ScratchRoot = Folder;
            SizeEstimator.Shared.Clear();
        }

        public string Folder { get; }

        public void Dispose()
        {
            SizeEstimator.ScratchRoot = previous;
            SizeEstimator.Shared.Clear();
        }
    }

    [Fact]
    public void ParsesAndLabelsSizes()
    {
        using var sandbox = new Sandbox();
        Assert.Equal(12, SizeTargets.Suggested(24_600_000));
        Assert.Equal(1.2, SizeTargets.Suggested(2_400_000));
        Assert.Equal(0.25, SizeTargets.Suggested(500_000));
        Assert.Equal(0.01, SizeTargets.Suggested(1_000));
        Assert.Equal(1.5, SizeTargets.ParseMegabytes("1,5"));
        Assert.Equal(1.5, SizeTargets.ParseMegabytes(" 1.5 MB "));
        Assert.Equal(3, SizeTargets.ParseMegabytes("3mb"));
        Assert.Equal(1.5, SizeTargets.ParseMegabytes("1,5 Mo"));
        Assert.Null(SizeTargets.ParseMegabytes("abc"));
        Assert.Null(SizeTargets.ParseMegabytes("0"));
        Assert.Null(SizeTargets.ParseMegabytes("-2"));
        Assert.Equal("500\u00A0KB", SizeTargets.Label(500_000));
        Assert.Equal("1\u00A0MB", SizeTargets.Label(1_000_000));
        Assert.Equal("2.5\u00A0MB", SizeTargets.Label(2_500_000));
        Assert.Equal(500_000, SizeEstimator.FixedTarget("tool.compress.max500k"));
        Assert.Equal(100_000_000, SizeEstimator.FixedTarget("tool.compress.max100m"));
        Assert.Null(SizeEstimator.FixedTarget("tool.compress.maxcustom"));
        Assert.Null(SizeEstimator.FixedTarget("tool.compress.small"));

        var photo = sandbox.Fixture("photo.jpg");
        var compress = Catalog.Options([photo], WheelMode.Tools).First(option => option.Id == "tool.compress");
        Assert.Equal(["tool.compress.balanced", "tool.compress.high", "tool.compress.small", "tool.compress.max500k", "tool.compress.max1m",
                      "tool.compress.max2m", "tool.compress.max5m", "tool.compress.maxcustom"], compress.Variants.Select(variant => variant.Id));
        Assert.Equal(L("size.under", "500\u00A0KB"), compress.Variants[3].Title);
        Assert.True(compress.Variants[^1].Interactive);
        Assert.False(compress.Interactive);
        var clip = Path.Combine(sandbox.Root, "clip.mp4");
        var video = Catalog.Options([clip], WheelMode.Tools).First(option => option.Id == "tool.compress");
        Assert.Equal(["tool.compress.max10m", "tool.compress.max25m", "tool.compress.max50m", "tool.compress.max100m", "tool.compress.maxcustom"],
                     video.Variants.Skip(3).Select(variant => variant.Id));
    }

    [Fact]
    public async Task EstimatesOnTheSideAndLeavesNothingBehind()
    {
        using var sandbox = new Sandbox();
        using var scratch = new Scratch(sandbox);
        var photo = Noise(Path.Combine(sandbox.Root, "photos"), "noise.jpg", 1200, 800);
        var folder = Path.GetDirectoryName(photo)!;
        var before = Directory.GetFileSystemEntries(folder);
        var token = TestContext.Current.CancellationToken;

        var webp = Option(photo, "convert.webp");
        Assert.True(SizeEstimator.IsEstimable(webp, null, photo));
        var estimate = await SizeEstimator.Shared.EstimateAsync(webp, photo, token);
        Assert.Equal(EstimateKind.Size, estimate.Kind);
        Assert.True(estimate.Bytes > 0);
        Assert.Equal(new FileInfo(photo).Length, estimate.Source);
        Assert.Matches(@"^≈ .+ · [−+]?\d+%$", estimate.Line);
        Assert.Equal(before, Directory.GetFileSystemEntries(folder));
        Assert.Empty(Directory.GetFileSystemEntries(scratch.Folder));
        Assert.Equal(estimate, SizeEstimator.Shared.Known(webp, photo));

        var smallest = await SizeEstimator.Shared.EstimateAsync(Option(photo, "tool.compress.small"), photo, token);
        Assert.Equal(EstimateKind.Size, smallest.Kind);
        Assert.StartsWith("\u2212", smallest.Change);
        Assert.InRange(smallest.Bytes, 1, estimate.Source - 1);

        // A size the picture is under already: known without running anything.
        Assert.Equal(SizeEstimate.Small, SizeEstimator.Shared.Known(Option(photo, "tool.compress.max5m"), photo));
        Assert.Null(SizeEstimator.Shared.Known(Option(photo, "tool.compress.max500k"), photo));
        // A picture that can't get any smaller.
        var tiny = Path.Combine(folder, "tiny.jpg");
        using (var image = new MagickImage(new MagickColor("#3366AA"), 16, 16) { Quality = 20 }) image.Write(tiny);
        Assert.Equal(EstimateKind.Small, (await SizeEstimator.Shared.EstimateAsync(Option(tiny, "tool.compress.balanced"), tiny, token)).Kind);
        // Never what asks first, nor what isn't a picture or a PDF.
        Assert.False(SizeEstimator.IsEstimable(Option(photo, "tool.compress.maxcustom"), null, photo));
        Assert.False(SizeEstimator.IsEstimable(Option(photo, "convert.pdf"), null, photo));
        var report = sandbox.Fixture("report.pdf");
        var pdf = Option(report, "tool.compress");
        Assert.True(SizeEstimator.IsEstimable(pdf, null, report));
        Assert.NotEqual(EstimateKind.None, (await SizeEstimator.Shared.EstimateAsync(pdf, report, token)).Kind);
        Assert.Empty(Directory.GetFileSystemEntries(scratch.Folder));
        Assert.Equal(["noise.jpg", "tiny.jpg"], Directory.GetFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task WheelShowsTheEstimateUnderTheHighlight()
    {
        using var sandbox = new Sandbox();
        using var scratch = new Scratch(sandbox);
        var photo = Noise(Path.Combine(sandbox.Root, "wheel"), "noise.jpg", 900, 600);
        var wheel = new WheelModel { Estimates = true };
        wheel.Present([photo], WheelModel.WheelOrigin.Selection, WheelMode.Tools, ScreenPoint.Zero);
        var compress = wheel.Items.ToList().FindIndex(item => item.Id == "tool.compress");
        var variants = wheel.Items[compress].Variants.ToList();
        // A size the picture is under already: right away.
        wheel.DemoHighlight(compress, variants.FindIndex(variant => variant.Id == "tool.compress.max5m"));
        Assert.Equal(L("est.small"), wheel.EstimateLine);
        // A conversion: nothing while the highlight settles, then the result.
        wheel.DemoSetMode(WheelMode.Convert);
        wheel.DemoHighlight(wheel.Items.ToList().FindIndex(item => item.Id == "convert.webp"));
        Assert.Null(wheel.EstimateLine);
        for (var wait = 0; wait < 300 && wheel.Estimate == null; wait++) await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(EstimateKind.Size, wheel.Estimate?.Kind);
        Assert.StartsWith("≈ ", wheel.EstimateLine);
        // Moving on clears it.
        wheel.DemoHighlight(null);
        Assert.Null(wheel.EstimateLine);
        // Several files: no estimate.
        var pair = new WheelModel { Estimates = true };
        pair.Present([photo, sandbox.Fixture("photo.jpg")], WheelModel.WheelOrigin.Selection, WheelMode.Tools, ScreenPoint.Zero);
        var slot = pair.Items.ToList().FindIndex(item => item.Id == "tool.compress");
        pair.DemoHighlight(slot, pair.Items[slot].Variants.ToList().FindIndex(variant => variant.Id == "tool.compress.max5m"));
        Assert.Null(pair.EstimateLine);
    }

    [Fact]
    public async Task CompressesPicturesUnderASize()
    {
        using var sandbox = new Sandbox();
        var folder = Path.Combine(sandbox.Root, "under");
        var photo = Noise(folder, "noise.jpg", 2400, 1600);
        Assert.True(new FileInfo(photo).Length > 1_000_000);
        var result = await Sandbox.Run("tool.compress.max1m", photo);
        var output = Sandbox.Output(result);
        Assert.Equal("noise compressed.jpg", Path.GetFileName(output));
        Assert.InRange(new FileInfo(output).Length, 1, 1_000_000);
        Assert.Null(result.Message);

        // Under the target already: a note, nothing written.
        var small = sandbox.Fixture("photo.jpg");
        var already = await Sandbox.Run("tool.compress.max5m", small);
        Assert.Empty(already.Outputs);
        Assert.Equal(L("msg.already_under", "5\u00A0MB"), already.Message);

        // Transparency in use keeps PNG (only shrinking helps); an alpha channel nobody uses doesn't.
        var clear = Noise(folder, "clear.png", 1200, 900, alpha: 0.5);
        var png = Sandbox.Output(await Sandbox.Run("tool.compress.max500k", clear));
        Assert.EndsWith(".png", png);
        Assert.InRange(new FileInfo(png).Length, 1, 500_000);
        Assert.True(Sandbox.Size(png).Width < 1200);
        var opaque = Noise(folder, "opaque.png", 1200, 900, alpha: 1.0);
        Assert.EndsWith(".jpg", Sandbox.Output(await Sandbox.Run("tool.compress.max500k", opaque)));

        // Custom size (the dialog's answer), and one out of reach: the smallest result, and a note.
        using (Sandbox.Editor("size", "0.3"))
            Assert.InRange(new FileInfo(Sandbox.Output(await Sandbox.Run("tool.compress.maxcustom", photo))).Length, 1, 300_000);
        using (Sandbox.Editor("size", "0.0001"))
        {
            var missed = await Sandbox.Run("tool.compress.maxcustom", photo);
            var smallest = Sandbox.Output(missed);
            Assert.Equal(SizeTargets.Missed(100, new FileInfo(smallest).Length), missed.Message);
        }
        using (Sandbox.Editor("size", "cancel"))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Sandbox.Run("tool.compress.maxcustom", photo));
    }

    [Fact]
    public async Task CompressesVideosUnderASize()
    {
        using var sandbox = new Sandbox();
        if (Tools.FFmpeg == null || !sandbox.HasFixture("clip.mp4")) return;
        var clip = sandbox.Fixture("clip.mp4");
        var already = await Sandbox.Run("tool.compress.max10m", clip);
        Assert.Empty(already.Outputs);
        Assert.Equal(L("msg.already_under", "10\u00A0MB"), already.Message);
        Assert.Equal(SizeEstimate.Small, SizeEstimator.Shared.Known(Option(clip, "tool.compress.max10m"), clip));

        var target = new FileInfo(clip).Length / 2;
        using (Sandbox.Editor("size", (target / 1e6).ToString("R", CultureInfo.InvariantCulture)))
        {
            var result = await Sandbox.Run("tool.compress.maxcustom", clip);
            var output = Sandbox.Output(result);
            Assert.Equal("clip compressed.mp4", Path.GetFileName(output));
            var bytes = new FileInfo(output).Length;
            if (bytes > target) Assert.Equal(SizeTargets.Missed(target, bytes), result.Message);
            else Assert.Null(result.Message);
            Assert.InRange(await MediaEngine.Duration(output), 5.5, 6.5);
        }
    }
}
