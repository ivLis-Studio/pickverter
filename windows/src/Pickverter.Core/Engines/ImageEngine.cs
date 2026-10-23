using ImageMagick;
using ImageMagick.Drawing;

namespace Pickverter;

/// Pictures through ImageMagick (Magick.NET): reads almost anything (HEIC, AVIF, RAW, PSD, SVG…), writes JPG, PNG,
/// WebP, AVIF, TIFF, GIF, BMP and multi-size ICO. HEIC export goes through Windows' own encoder when it is installed.
public static class ImageEngine
{
    private static readonly Dictionary<string, MagickFormat> Formats = new()
    {
        ["jpg"] = MagickFormat.Jpeg, ["png"] = MagickFormat.Png, ["webp"] = MagickFormat.WebP, ["avif"] = MagickFormat.Avif,
        ["heic"] = MagickFormat.Heic, ["tiff"] = MagickFormat.Tiff, ["gif"] = MagickFormat.Gif, ["bmp"] = MagickFormat.Bmp,
        ["ico"] = MagickFormat.Ico, ["jp2"] = MagickFormat.Jp2, ["jxl"] = MagickFormat.Jxl, ["tga"] = MagickFormat.Tga,
        ["psd"] = MagickFormat.Psd, ["exr"] = MagickFormat.Exr,
    };

    public static readonly HashSet<string> RawExts = ["dng", "cr2", "cr3", "nef", "arw", "raf", "orf", "rw2"];

    private static readonly Lazy<HashSet<MagickFormat>> Writable = new(() =>
        MagickNET.SupportedFormats.Where(info => info.SupportsWriting).Select(info => info.Format).ToHashSet());

    private static bool MagickWrites(string ext) => Formats.TryGetValue(ext, out var format) && Writable.Value.Contains(format);

    public static bool CanEncode(string ext) => ext == "pdf" || MagickWrites(ext) || (ext == "heic" && Platform.Codecs.CanEncodeHeic);

    public static string? EncodeUnavailable(string ext)
    {
        if (CanEncode(ext)) return null;
        return ext == "heic" ? L("err.heic_encoder") : L("err.encoder_missing", Catalog.FormatLabel(ext));
    }

    public static bool SupportsAlpha(string ext) => ext is not ("jpg" or "bmp");

    /// Writes through a stream: given a path, ImageMagick reads "%d", "%x"… as frame numbers, so "sale 50%.ico" or a
    /// "… 50%.ico" resize came out under another name (and the job reported that nothing was produced).
    public static void WriteTo(MagickImage image, string path, MagickFormat format)
    {
        using var stream = File.Create(path);
        image.Write(stream, format);
    }

    public static void WriteTo(MagickImageCollection images, string path, MagickFormat format)
    {
        using var stream = File.Create(path);
        images.Write(stream, format);
    }

    /// The largest PNG or JPEG 2000 picture inside a macOS .icns (ImageMagick has no reader for the container).
    private static MagickImage? LoadIcns(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 16 || data[0] != (byte)'i' || data[1] != (byte)'c' || data[2] != (byte)'n' || data[3] != (byte)'s') return null;
        MagickImage? best = null;
        var offset = 8;
        while (offset + 8 <= data.Length)
        {
            var length = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 4, 4));
            if (length < 8 || length > data.Length - offset) break;
            var body = data.AsSpan(offset + 8, length - 8);
            var png = body.Length > 8 && body[0] == 0x89 && body[1] == (byte)'P' && body[2] == (byte)'N' && body[3] == (byte)'G';
            var jp2 = body.Length > 12 && ((body[4] == (byte)'j' && body[5] == (byte)'P') || (body[0] == 0xFF && body[1] == 0x4F));
            if (png || jp2)
            {
                try
                {
                    var candidate = new MagickImage(body.ToArray());
                    if (best == null || (long)candidate.Width * candidate.Height > (long)best.Width * best.Height)
                    {
                        best?.Dispose();
                        best = candidate;
                    }
                    else
                    {
                        candidate.Dispose();
                    }
                }
                catch (MagickException)
                {
                }
            }
            offset += length;
        }
        return best;
    }

    /// Decodes the primary image upright (EXIF orientation applied), optionally downsampled.
    public static MagickImage Load(string path, int? maxPixel = null)
    {
        var ext = FileNames.NormalizedExt(path);
        var settings = new MagickReadSettings();
        if (ext == "svg")
        {
            settings.Density = new Density(192);
            settings.BackgroundColor = MagickColors.None;
        }
        try
        {
            MagickImage image;
            if (ext == "icns")
            {
                image = LoadIcns(path) ?? throw new PickException(L("err.read_image"));
            }
            else if (ext is "ico" or "gif" or "tiff" or "psd")
            {
                using var frames = new MagickImageCollection(path, settings);
                if (frames.Count == 0) throw new PickException(L("err.read_image"));
                var chosen = ext == "ico" ? frames.OrderByDescending(frame => (long)frame.Width * frame.Height).First() : frames[0];
                image = new MagickImage(chosen);
            }
            else
            {
                image = new MagickImage(path, settings);
            }
            image.AutoOrient();
            if (maxPixel is { } limit && Math.Max(image.Width, image.Height) > limit)
                image.Resize(new MagickGeometry((uint)limit, (uint)limit) { Greater = true });
            return image;
        }
        catch (MagickException error)
        {
            Debug.Log("magick: " + error.Message);
            throw new PickException(L("err.read_image"));
        }
    }

    /// Width × height as shown (EXIF orientation applied), without decoding the pixels.
    public static (int Width, int Height)? OrientedSize(string path)
    {
        try
        {
            using var image = new MagickImage();
            image.Ping(path);
            var swap = image.Orientation is OrientationType.LeftTop or OrientationType.RightTop or OrientationType.RightBottom or OrientationType.LeftBottom;
            return swap ? ((int)image.Height, (int)image.Width) : ((int)image.Width, (int)image.Height);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void StripKeepingColor(MagickImage image)
    {
        var icc = image.GetColorProfile();
        image.Strip();
        if (icc != null) image.SetProfile(icc);
    }

    /// Drops GPS from EXIF (and an XMP packet that carries a location).
    public static void RemoveLocation(MagickImage image)
    {
        if (image.GetExifProfile() is { } exif)
        {
            RemoveGps(exif);
            image.SetProfile(exif);
        }
        if (image.GetProfile("xmp") is { } xmp && Encoding.UTF8.GetString(xmp.ToByteArray() ?? []).Contains("GPS", StringComparison.Ordinal))
            image.RemoveProfile("xmp");
    }

    public static void RemoveGps(IExifProfile exif)
    {
        foreach (var value in exif.Values.ToList())
        {
            if (value.Tag.ToString().StartsWith("GPS", StringComparison.Ordinal)) exif.RemoveValue(value.Tag);
        }
    }

    public static double? DefaultQuality(string ext) => ext switch
    {
        "jpg" => 0.9,
        "heic" => 0.82,
        "avif" => 0.7,
        "webp" => 0.86,
        _ => null,
    };

    /// Same format when it can be written, otherwise PNG (JPG for camera RAW and phone photos).
    public static string OutputExt(string source)
    {
        var ext = FileNames.NormalizedExt(source);
        if (RawExts.Contains(ext)) return "jpg";
        if (ext is "heic" or "avif" && !CanEncode(ext)) return "jpg";
        if (ext is "svg" or "pdf" or "icns" or "psd" or "gif" or "exr" or "tga" or "") return "png";
        return CanEncode(ext) ? ext : "png";
    }

    public static async Task Encode(MagickImage source, string path, string ext, double? quality = null, bool keepMetadata = true)
    {
        if (ext == "pdf")
        {
            WritePdf([source], path);
            return;
        }
        using var image = new MagickImage(source);
        if (!SupportsAlpha(ext) && image.HasAlpha)
        {
            image.BackgroundColor = MagickColors.White;
            image.Alpha(AlphaOption.Remove);
            image.Alpha(AlphaOption.Off);
        }
        if (!keepMetadata) StripKeepingColor(image);
        if (quality is { } q) image.Quality = (uint)Math.Clamp(Math.Round(q * 100), 1, 100);
        switch (ext)
        {
            case "ico":
                WriteIcon(image, path);
                return;
            case "heic" when !MagickWrites("heic"):
            {
                var png = path + ".pv.png";
                WriteTo(image, png, MagickFormat.Png);
                try
                {
                    await Platform.Codecs.EncodeHeicAsync(png, path, quality ?? 0.82, CancellationToken.None);
                }
                finally
                {
                    FileSystem.TryDelete(png);
                }
                return;
            }
            case "webp":
                image.Settings.SetDefine(MagickFormat.WebP, "method", "5");
                break;
            case "gif":
                image.Quantize(new QuantizeSettings { Colors = 256, DitherMethod = DitherMethod.FloydSteinberg });
                break;
        }
        if (!Formats.TryGetValue(ext, out var format)) throw new PickException(L("err.encoder_missing", Catalog.FormatLabel(ext)));
        WriteTo(image, path, format);
    }

    /// Multi-size icon: the picture's own size (up to 256) and the smaller sizes Explorer asks for, square and centered.
    private static void WriteIcon(MagickImage image, string path)
    {
        var side = Math.Max(image.Width, image.Height);
        if (image.Width != image.Height)
        {
            image.BackgroundColor = MagickColors.Transparent;
            image.Extent(side, side, Gravity.Center);
        }
        var largest = Math.Clamp(side, 16u, 256u);
        using var frames = new MagickImageCollection();
        foreach (var size in new[] { largest }.Concat(new uint[] { 128, 64, 48, 32, 24, 16 }.Where(size => size < largest)))
        {
            var frame = new MagickImage(image);
            frame.Resize(new MagickGeometry(size, size) { IgnoreAspectRatio = true });
            frames.Add(frame);
        }
        WriteTo(frames, path, MagickFormat.Ico);
    }

    public static void WritePdf(IReadOnlyList<MagickImage> images, string path)
    {
        using var pages = new MagickImageCollection();
        foreach (var source in images)
        {
            var page = new MagickImage(source);
            if (page.HasAlpha)
            {
                page.BackgroundColor = MagickColors.White;
                page.Alpha(AlphaOption.Remove);
                page.Alpha(AlphaOption.Off);
            }
            var side = Math.Max(page.Width, page.Height);
            var density = side > 1440 ? 72.0 * side / 1440 : 72.0;
            page.Density = new Density(density, density, DensityUnit.PixelsPerInch);
            page.Settings.Compression = CompressionMethod.JPEG;
            page.Quality = 90;
            pages.Add(page);
        }
        WriteTo(pages, path, MagickFormat.Pdf);
    }

    public static void KeepIfSmaller(string output, string source)
    {
        if (FileNames.Size(output) >= FileNames.Size(source))
        {
            FileSystem.TryDelete(output);
            throw new PickException(L("err.already_small"));
        }
    }

    private static Task<MagickImage> LoadAsync(string path, PickContext ctx, int? maxPixel = null) =>
        Task.Run(() => Load(path, maxPixel), ctx.Token);

    // Conversions and tools

    public static async Task<string> Convert(string src, string ext, PickContext ctx)
    {
        using var image = await LoadAsync(src, ctx);
        return await ctx.Produce(ctx.DestinationFor(src, ext), tmp => Encode(image, tmp, ext, DefaultQuality(ext)));
    }

    public static async Task<string> Compress(string src, double quality, PickContext ctx)
    {
        var ext = FileNames.NormalizedExt(src);
        var suffix = L("sfx.compressed");
        using var image = await LoadAsync(src, ctx);
        string output;
        if (ext is "jpg" or "webp" or "avif" || (ext == "heic" && CanEncode("heic")))
        {
            output = await ctx.Produce(ctx.DestinationFor(src, ext, suffix), tmp => Encode(image, tmp, ext, quality));
        }
        else if (ext == "png")
        {
            output = await ctx.Produce(ctx.DestinationFor(src, "png", suffix), tmp =>
            {
                using var copy = new MagickImage(image);
                StripKeepingColor(copy);
                if (quality < 0.8) copy.Quantize(new QuantizeSettings { Colors = quality < 0.6 ? 96u : 256u, DitherMethod = DitherMethod.FloydSteinberg });
                copy.Settings.SetDefine(MagickFormat.Png, "compression-level", "9");
                copy.Settings.SetDefine(MagickFormat.Png, "exclude-chunks", "date,time");
                WriteTo(copy, tmp, MagickFormat.Png);
                return Task.CompletedTask;
            });
        }
        else
        {
            var target = image.HasAlpha ? "webp" : "jpg";
            output = await ctx.Produce(ctx.DestinationFor(src, target, suffix), tmp => Encode(image, tmp, target, quality));
        }
        KeepIfSmaller(output, src);
        return output;
    }

    // Under N

    /// "Under N": the biggest result that fits. JPEG, HEIC, WebP and AVIF stay in their format when it can be written;
    /// anything else becomes PNG when it really uses transparency, JPEG otherwise. Lossy formats search the quality
    /// between 0.30 and 0.92 (about six encodes) for the biggest result under the target; when even 0.30 is too big the
    /// picture shrinks by √(target/size)·0.95 and the search runs again (up to four times). PNG only shrinks (up to
    /// six times). Every smaller size is made from the original. A target out of reach keeps the smallest result.
    public static async Task<SizeTargets.Fit> CompressUnder(string src, long target, PickContext ctx)
    {
        if (FileNames.Size(src) <= target) return SizeTargets.Fit.AlreadyUnder;
        using var original = await LoadAsync(src, ctx);
        var ext = FileNames.NormalizedExt(src);
        var format = ext is "jpg" or "heic" or "webp" or "avif" && CanEncode(ext) ? ext : UsesTransparency(original) ? "png" : "jpg";
        string? missed = null;
        var output = await ctx.Produce(ctx.DestinationFor(src, format, L("sfx.compressed")), async tmp =>
        {
            var scratch = FileSystem.TempFolder("pickverter-fit");
            try
            {
                var attempts = new Attempts(scratch, target, format, ctx);
                if (format == "png") await FitByShrinking(original, attempts, target);
                else await FitByQuality(original, attempts, target);
                var (path, bytes) = attempts.Result;
                File.Move(path, tmp);
                if (bytes > target) missed = SizeTargets.Missed(target, bytes);
            }
            finally
            {
                FileSystem.TryDelete(scratch);
            }
        });
        return new SizeTargets.Fit(output, missed);
    }

    private static async Task FitByQuality(MagickImage original, Attempts attempts, long target)
    {
        var scale = 1.0;
        for (var round = 0; round <= 4; round++)
        {
            using var scaled = round == 0 ? null : Scaled(original, scale);
            var image = scaled ?? original;
            var floor = await attempts.Encode(image, 0.30);
            if (floor > target)
            {
                scale *= Math.Sqrt((double)target / floor) * 0.95;
                continue;
            }
            if (await attempts.Encode(image, 0.92) <= target) return;
            double low = 0.30, high = 0.92;
            for (var step = 0; step < 4; step++)
            {
                var middle = (low + high) / 2;
                if (await attempts.Encode(image, middle) <= target) low = middle;
                else high = middle;
            }
            return;
        }
    }

    private static async Task FitByShrinking(MagickImage original, Attempts attempts, long target)
    {
        var scale = 1.0;
        var bytes = await attempts.Encode(original, null);
        for (var round = 0; round < 6 && bytes > target; round++)
        {
            scale *= Math.Sqrt((double)target / bytes) * 0.95;
            using var scaled = Scaled(original, scale);
            bytes = await attempts.Encode(scaled, null);
        }
    }

    /// The original at a fraction of its size (a fresh copy each time, never a copy of a copy).
    private static MagickImage Scaled(MagickImage original, double scale)
    {
        var copy = new MagickImage(original);
        var width = (uint)Math.Max(1, Math.Round(original.Width * scale));
        var height = (uint)Math.Max(1, Math.Round(original.Height * scale));
        copy.Resize(new MagickGeometry(width, height) { IgnoreAspectRatio = true });
        return copy;
    }

    /// Some pixel isn't fully opaque (checked on a copy of at most 512 px): an alpha channel alone doesn't count.
    public static bool UsesTransparency(MagickImage image)
    {
        if (!image.HasAlpha) return false;
        using var probe = new MagickImage(image);
        if (Math.Max(probe.Width, probe.Height) > 512) probe.Resize(new MagickGeometry(512, 512) { Greater = true });
        return !probe.IsOpaque;
    }

    /// The encodes of one Under-N run: keeps the biggest that fits and the smallest overall, deletes the rest.
    private sealed class Attempts(string folder, long target, string format, PickContext ctx)
    {
        private int count;
        private (string Path, long Bytes)? best;
        private (string Path, long Bytes)? smallest;

        /// The biggest result under the target, or the smallest of all when none is.
        public (string Path, long Bytes) Result => best ?? smallest ?? throw new PickException(L("err.no_result"));

        public async Task<long> Encode(MagickImage image, double? quality)
        {
            ctx.Check();
            var path = Path.Combine(folder, (count++).ToString(CultureInfo.InvariantCulture) + "." + format);
            if (format == "png")
            {
                using var copy = new MagickImage(image);
                StripKeepingColor(copy);
                copy.Settings.SetDefine(MagickFormat.Png, "compression-level", "9");
                copy.Settings.SetDefine(MagickFormat.Png, "exclude-chunks", "date,time");
                await Task.Run(() => WriteTo(copy, path, MagickFormat.Png), ctx.Token);
            }
            else
            {
                await ImageEngine.Encode(image, path, format, quality);
            }
            var bytes = new FileInfo(path).Length;
            ctx.Report(Math.Min(0.95, count / 10.0));
            var keep = false;
            if (bytes <= target && (best is not { } fitting || bytes > fitting.Bytes))
            {
                Drop(best);
                best = (path, bytes);
                keep = true;
            }
            if (smallest is not { } least || bytes < least.Bytes)
            {
                Drop(smallest);
                smallest = (path, bytes);
                keep = true;
            }
            if (!keep) FileSystem.TryDelete(path);
            return bytes;
        }

        /// Deletes a superseded encode unless the other slot still holds it.
        private void Drop((string Path, long Bytes)? attempt)
        {
            if (attempt is not { } old) return;
            if (best?.Path == old.Path && smallest?.Path == old.Path) return;
            FileSystem.TryDelete(old.Path);
        }
    }

    public static async Task<string> Resize(string src, double? scale, int? maxSide, PickContext ctx)
    {
        var size = OrientedSize(src) ?? (4096, 4096);
        var side = Math.Max(size.Width, size.Height);
        var target = scale is { } factor ? Math.Max(1, (int)(side * factor)) : maxSide ?? side;
        if (target >= side) throw new PickException(L("err.already_px", target.ToString(CultureInfo.InvariantCulture)));
        using var image = await LoadAsync(src, ctx, target);
        var ext = OutputExt(src);
        var suffix = scale is { } percent ? $"{(int)(percent * 100)}%" : $"{target}px";
        return await ctx.Produce(ctx.DestinationFor(src, ext, suffix), tmp => Encode(image, tmp, ext, DefaultQuality(ext)));
    }

    public static async Task<string> Transform(string src, Turn turn, PickContext ctx)
    {
        using var image = await LoadAsync(src, ctx);
        switch (turn)
        {
            case Turn.Right: image.Rotate(90); break;
            case Turn.Left: image.Rotate(-90); break;
            case Turn.Half: image.Rotate(180); break;
            case Turn.FlipH: image.Flop(); break;
            case Turn.FlipV: image.Flip(); break;
        }
        image.ResetPage();
        var ext = OutputExt(src);
        var suffix = turn.IsFlip() ? L("sfx.flipped") : L("sfx.rotated");
        return await ctx.Produce(ctx.DestinationFor(src, ext, suffix), tmp => Encode(image, tmp, ext, DefaultQuality(ext)));
    }

    public static async Task<string> StripMetadata(string src, bool gpsOnly, PickContext ctx)
    {
        using var image = await LoadAsync(src, ctx);
        if (gpsOnly) RemoveLocation(image); else StripKeepingColor(image);
        var ext = OutputExt(src);
        return await ctx.Produce(ctx.DestinationFor(src, ext, L("sfx.stripped")), tmp => Encode(image, tmp, ext, DefaultQuality(ext)));
    }

    public static async Task<string> RemoveBackground(string src, CutoutStyle style, PickContext ctx)
    {
        if (!BackgroundModel.IsInstalled) throw new ModelMissingException();
        using var image = await LoadAsync(src, ctx, 4096);
        ctx.Report(0.2, L("note.finding_subject"));
        using var mask = await Task.Run(() => BackgroundRemover.Mask(image), ctx.Token);
        ctx.Check();
        ctx.Report(0.7, L("note.removing_bg"));
        var bounds = BackgroundRemover.SubjectBounds(mask) ?? throw new PickException(L("err.no_subject"));
        image.Alpha(AlphaOption.Set);
        image.Composite(mask, CompositeOperator.CopyAlpha);
        if (style == CutoutStyle.Crop)
        {
            image.Crop(new MagickGeometry(bounds.X, bounds.Y, (uint)bounds.Width, (uint)bounds.Height));
            image.ResetPage();
        }
        var ext = "png";
        if (style == CutoutStyle.White)
        {
            image.BackgroundColor = MagickColors.White;
            image.Alpha(AlphaOption.Remove);
            image.Alpha(AlphaOption.Off);
            ext = "jpg";
        }
        var suffix = style == CutoutStyle.Crop ? L("sfx.subject") : L("sfx.no_bg");
        return await ctx.Produce(ctx.DestinationFor(src, ext, suffix), tmp => Encode(image, tmp, ext, 0.92));
    }

    /// A PNG copy (downsampled) for Windows' OCR and face detection.
    public static async Task<string> ProbeCopy(string src, int maxPixel, CancellationToken token)
    {
        using var image = await Task.Run(() => Load(src, maxPixel), token);
        var path = Path.Combine(Path.GetTempPath(), "pickverter-probe-" + Guid.NewGuid().ToString("N")[..10] + ".png");
        if (image.HasAlpha)
        {
            image.BackgroundColor = MagickColors.White;
            image.Alpha(AlphaOption.Remove);
        }
        WriteTo(image, path, MagickFormat.Png);
        return path;
    }

    public static async Task<IReadOnlyList<RecognizedText>> Recognize(string src, CancellationToken token)
    {
        var probe = await ProbeCopy(src, 4096, token);
        try
        {
            return await Platform.Vision.RecognizeAsync(probe, token);
        }
        finally
        {
            FileSystem.TryDelete(probe);
        }
    }

    public static async Task<PickResult> RecognizeText(PickContext ctx)
    {
        var chunks = new List<string>();
        for (var index = 0; index < ctx.Files.Count; index++)
        {
            ctx.Check();
            ctx.Report((double)index / ctx.Files.Count, FileNames.Name(ctx.Files[index]));
            var lines = await Recognize(ctx.Files[index], ctx.Token);
            var text = string.Join("\n", lines.Select(line => line.Text));
            if (text.Trim().Length > 0) chunks.Add(text);
        }
        var all = string.Join("\n\n", chunks);
        if (all.Trim().Length == 0) throw new PickException(L("err.no_text"));
        await Platform.Clipboard.SetTextAsync(all.Replace("\n", "\r\n"));
        return PickResult.Note(LP("msg.lines_copied", all.Split('\n').Count(line => line.Length > 0)));
    }

    public static async Task<PickResult> CombineToPdf(PickContext ctx)
    {
        var files = NaturalComparer.SortByName(ctx.Files);
        var images = new List<MagickImage>();
        try
        {
            for (var index = 0; index < files.Count; index++)
            {
                ctx.Check();
                ctx.Report((double)index / files.Count * 0.9, FileNames.Name(files[index]));
                images.Add(await LoadAsync(files[index], ctx, 3200));
            }
            var destination = ctx.DestinationFor(files[0], "pdf", LP("sfx.and_more", files.Count - 1));
            return PickResult.Of(await ctx.Produce(destination, tmp => WritePdf(images, tmp)));
        }
        finally
        {
            foreach (var image in images) image.Dispose();
        }
    }

    // Crop

    public static async Task<string> Crop(string src, NormRect rect, PickContext ctx)
    {
        using var image = await LoadAsync(src, ctx);
        var box = rect.Clipped().Pixels(image.Width, image.Height);
        if (box.Width < 1 || box.Height < 1) throw new PickException(L("err.crop_empty"));
        image.Crop(new MagickGeometry(box.X, box.Y, (uint)box.Width, (uint)box.Height));
        image.ResetPage();
        var ext = OutputExt(src);
        return await ctx.Produce(ctx.DestinationFor(src, ext, L("sfx.cropped")), tmp => Encode(image, tmp, ext, DefaultQuality(ext)));
    }

    public static Task<string> CenterCrop(string src, double ratio, PickContext ctx)
    {
        var size = OrientedSize(src) ?? (1, 1);
        var rect = NormRect.Centered(ratio, size.Width, size.Height);
        if (rect.IsFull) throw new PickException(L("err.already_ratio"));
        return Crop(src, rect, ctx);
    }

    // Redaction

    /// Draws the effect into each region. Block and blur sizes scale with the region, so small and large areas
    /// look equally hidden and previews match the full-size result.
    public static void ApplyRedaction(MagickImage image, IReadOnlyList<NormRect> regions, RedactStyle style)
    {
        foreach (var region in regions)
        {
            var box = region.Clipped().Pixels(image.Width, image.Height);
            if (box.Width < 2 || box.Height < 2) continue;
            switch (style)
            {
                case RedactStyle.Fill:
                    new Drawables().FillColor(new MagickColor("#141414")).Rectangle(box.X, box.Y, box.X + box.Width - 1, box.Y + box.Height - 1).Draw(image);
                    break;
                case RedactStyle.Pixelate:
                {
                    var block = Math.Max(6, Math.Min(box.Width, box.Height) / 9);
                    var columns = (uint)Math.Max(1, box.Width / block);
                    var rows = (uint)Math.Max(1, box.Height / block);
                    using var piece = (MagickImage)image.CloneArea(new MagickGeometry(box.X, box.Y, (uint)box.Width, (uint)box.Height));
                    piece.ResetPage();
                    piece.Scale(new MagickGeometry(columns, rows) { IgnoreAspectRatio = true });
                    piece.Sample(new MagickGeometry((uint)box.Width, (uint)box.Height) { IgnoreAspectRatio = true });
                    image.Composite(piece, box.X, box.Y, CompositeOperator.Over);
                    break;
                }
                case RedactStyle.Blur:
                {
                    var sigma = Math.Max(6, Math.Min(box.Width, box.Height) / 7.0);
                    var pad = (int)Math.Min(sigma * 2, 200);
                    var x0 = Math.Max(0, box.X - pad);
                    var y0 = Math.Max(0, box.Y - pad);
                    var x1 = Math.Min((int)image.Width, box.X + box.Width + pad);
                    var y1 = Math.Min((int)image.Height, box.Y + box.Height + pad);
                    using var area = (MagickImage)image.CloneArea(new MagickGeometry(x0, y0, (uint)(x1 - x0), (uint)(y1 - y0)));
                    area.ResetPage();
                    area.Blur(0, sigma);
                    area.Blur(0, sigma * 0.6);
                    area.Crop(new MagickGeometry(box.X - x0, box.Y - y0, (uint)box.Width, (uint)box.Height));
                    area.ResetPage();
                    image.Composite(area, box.X, box.Y, CompositeOperator.Over);
                    break;
                }
            }
        }
    }

    public static async Task<string> Redact(string src, IReadOnlyList<NormRect> regions, RedactStyle style, PickContext ctx)
    {
        using var image = await LoadAsync(src, ctx);
        await Task.Run(() => ApplyRedaction(image, regions, style), ctx.Token);
        RemoveLocation(image);
        var ext = OutputExt(src);
        return await ctx.Produce(ctx.DestinationFor(src, ext, L("sfx.redacted")), tmp => Encode(image, tmp, ext, DefaultQuality(ext)));
    }

    /// Faces get generous margins (hair, ears); text boxes a small one.
    public static async Task<IReadOnlyList<NormRect>> Detect(Detection kind, string src, CancellationToken token)
    {
        var probe = await ProbeCopy(src, 2400, token);
        try
        {
            if (kind == Detection.Faces) return (await Platform.Vision.DetectFacesAsync(probe, token)).Select(box => box.Expanded(0.18)).ToList();
            return (await Platform.Vision.RecognizeAsync(probe, token)).Select(line => line.Box.Expanded(0.08)).ToList();
        }
        finally
        {
            FileSystem.TryDelete(probe);
        }
    }

    public static async Task<string> AutoRedact(string src, Detection kind, RedactStyle style, PickContext ctx)
    {
        ctx.Report(0.15, L(kind == Detection.Faces ? "note.finding_faces" : "note.finding_text"));
        var regions = await Detect(kind, src, ctx.Token);
        if (regions.Count == 0) throw new PickException(L(kind == Detection.Faces ? "redact.no_faces" : "redact.no_text"));
        ctx.Report(0.5);
        return await Redact(src, regions, style, ctx);
    }

    // Collage

    public static MagickImage CollageImage(IReadOnlyList<MagickImage> images, CollageLayout layout)
    {
        var aspects = images.Select(image => image.Width / (double)Math.Max(image.Height, 1u)).ToList();
        var opaque = images.All(image => !image.HasAlpha);
        var placements = new List<(double X, double Y, double W, double H)>();
        double canvasWidth, canvasHeight;
        switch (layout)
        {
            case CollageLayout.Grid:
            {
                // Justified rows: every row shares one height, nothing is cropped.
                const double pad = 16, gap = 12, width = 2400;
                var inner = width - pad * 2;
                var columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(images.Count)));
                var median = aspects.OrderBy(a => a).ElementAt(aspects.Count / 2);
                var targetHeight = (inner - gap * (columns - 1)) / columns / median;
                var rows = new List<(int Start, int End, double Height)>();
                var start = 0;
                var sum = 0.0;
                for (var index = 0; index < images.Count; index++)
                {
                    sum += aspects[index];
                    var count = index - start + 1;
                    var height = (inner - gap * (count - 1)) / sum;
                    if (height <= targetHeight || index == images.Count - 1)
                    {
                        rows.Add((start, index + 1, Math.Min(height, targetHeight)));
                        start = index + 1;
                        sum = 0;
                    }
                }
                var y = pad;
                foreach (var row in rows)
                {
                    var rowWidth = Enumerable.Range(row.Start, row.End - row.Start).Sum(i => aspects[i] * row.Height) + gap * (row.End - row.Start - 1);
                    var x = pad + Math.Max(0, (inner - rowWidth) / 2);
                    for (var index = row.Start; index < row.End; index++)
                    {
                        var w = aspects[index] * row.Height;
                        placements.Add((x, y, w, row.Height));
                        x += w + gap;
                    }
                    y += row.Height + gap;
                }
                canvasWidth = width;
                canvasHeight = y - gap + pad;
                break;
            }
            case CollageLayout.Row:
            {
                var height = Math.Min(images.Select(image => (double)image.Height).OrderBy(h => h).ElementAt(images.Count / 2), 2400);
                var total = aspects.Sum(aspect => aspect * height);
                if (total > 16_000) height *= 16_000 / total;
                var x = 0.0;
                foreach (var aspect in aspects)
                {
                    placements.Add((x, 0, aspect * height, height));
                    x += aspect * height;
                }
                canvasWidth = x;
                canvasHeight = height;
                break;
            }
            default:
            {
                var width = Math.Min(images.Select(image => (double)image.Width).OrderBy(w => w).ElementAt(images.Count / 2), 2400);
                var total = aspects.Sum(aspect => width / aspect);
                if (total > 16_000) width *= 16_000 / total;
                var y = 0.0;
                foreach (var aspect in aspects)
                {
                    placements.Add((0, y, width, width / aspect));
                    y += width / aspect;
                }
                canvasWidth = width;
                canvasHeight = y;
                break;
            }
        }
        var background = layout == CollageLayout.Grid || opaque ? MagickColors.White : MagickColors.Transparent;
        var canvas = new MagickImage(background, (uint)Math.Max(1, Math.Round(canvasWidth)), (uint)Math.Max(1, Math.Round(canvasHeight)));
        for (var index = 0; index < images.Count; index++)
        {
            var (x, y, w, h) = placements[index];
            using var piece = new MagickImage(images[index]);
            piece.Resize(new MagickGeometry((uint)Math.Max(1, Math.Round(w)), (uint)Math.Max(1, Math.Round(h))) { IgnoreAspectRatio = true });
            canvas.Composite(piece, (int)Math.Round(x), (int)Math.Round(y), CompositeOperator.Over);
        }
        return canvas;
    }

    public static async Task<PickResult> Collage(PickContext ctx, CollageLayout layout)
    {
        var files = NaturalComparer.SortByName(ctx.Files);
        if (files.Count < 2) throw new PickException(L("err.need_two"));
        var images = new List<MagickImage>();
        try
        {
            for (var index = 0; index < files.Count; index++)
            {
                ctx.Check();
                ctx.Report((double)index / files.Count * 0.8, FileNames.Name(files[index]));
                images.Add(await LoadAsync(files[index], ctx, layout == CollageLayout.Grid ? 1600 : 2400));
            }
            using var result = await Task.Run(() => CollageImage(images, layout), ctx.Token);
            var ext = result.HasAlpha ? "png" : "jpg";
            var destination = ctx.DestinationFor(files[0], ext, L("sfx.collage"));
            return PickResult.Of(await ctx.Produce(destination, tmp => Encode(result, tmp, ext, 0.9)));
        }
        finally
        {
            foreach (var image in images) image.Dispose();
        }
    }

    /// A page for a picture book: JPEG (PNG when transparent), at most 2400 px.
    public static (byte[] Data, string Ext) EncodeForBook(string path)
    {
        using var image = Load(path, 2400);
        StripKeepingColor(image);
        if (image.HasAlpha) return (image.ToByteArray(MagickFormat.Png), "png");
        image.Quality = 88;
        return (image.ToByteArray(MagickFormat.Jpeg), "jpg");
    }

    /// PNG bytes for previews in editors and windows.
    public static byte[] PreviewPng(string path, int maxPixel)
    {
        using var image = Load(path, maxPixel);
        return image.ToByteArray(MagickFormat.Png);
    }
}
