using System.Net.Http;
using System.Security.Cryptography;
using ImageMagick;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Pickverter;

/// The background-removal model: IS-Net (general use, Apache-2.0), downloaded once on request.
public static class BackgroundModel
{
    public const string FileName = "isnet-general-use.onnx";
    public const string Url = "https://github.com/danielgatis/rembg/releases/download/v0.0.0/isnet-general-use.onnx";
    public const long Size = 178_648_008;
    public const string Sha256 = "60920e99c45464f2ba57bee2ad08c919a52bbf852739e96947fbb4358c0d964a";

    public static string ModelPath => Environment.GetEnvironmentVariable("PICKVERTER_BG_MODEL") is { Length: > 0 } custom
        ? custom
        : Path.Combine(Platform.Paths.Cache, "models", FileName);

    public static bool IsInstalled
    {
        get
        {
            try
            {
                var info = new FileInfo(ModelPath);
                return info.Exists && (info.Length == Size || Environment.GetEnvironmentVariable("PICKVERTER_BG_MODEL") != null);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public static string SizeLabel => FileNames.FormatBytes(Size);

    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task EnsureAsync(PickContext ctx)
    {
        if (IsInstalled) return;
        await DownloadAsync(fraction => ctx.Report(fraction * 0.6, L("bg.model.downloading", ((int)(fraction * 100)).ToString(CultureInfo.InvariantCulture))), ctx.Token);
    }

    /// Downloads, checks size and SHA-256, then moves the model into place.
    public static async Task DownloadAsync(Action<double> progress, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try
        {
            if (IsInstalled) return;
            var folder = Path.GetDirectoryName(ModelPath)!;
            Directory.CreateDirectory(folder);
            var partial = ModelPath + ".download";
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Pickverter");
            using (var response = await client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(token);
                await using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                var buffer = new byte[1 << 16];
                long total = 0;
                int read;
                var last = DateTime.MinValue;
                while ((read = await input.ReadAsync(buffer, token)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    total += read;
                    if ((DateTime.UtcNow - last).TotalMilliseconds > 150)
                    {
                        last = DateTime.UtcNow;
                        progress(Math.Min(1, total / (double)Size));
                    }
                }
            }
            if (new FileInfo(partial).Length != Size) throw new PickException(L("update.err.checksum"));
            await using (var check = File.OpenRead(partial))
            {
                var digest = Convert.ToHexString(await SHA256.HashDataAsync(check, token)).ToLowerInvariant();
                if (digest != Sha256)
                {
                    FileSystem.TryDelete(partial);
                    throw new PickException(L("update.err.checksum"));
                }
            }
            File.Move(partial, ModelPath, overwrite: true);
            progress(1);
        }
        finally
        {
            Gate.Release();
        }
    }

    public static void Remove()
    {
        BackgroundRemover.Unload();
        FileSystem.TryDelete(ModelPath);
    }
}

/// Runs IS-Net with ONNX Runtime (CPU) and returns an alpha mask at the image's size.
public static class BackgroundRemover
{
    private const int Side = 1024;
    private static readonly object Gate = new();
    private static InferenceSession? session;
    private static Timer? idle;

    private static InferenceSession Session()
    {
        lock (Gate)
        {
            idle?.Dispose();
            idle = new Timer(_ => Unload(), null, TimeSpan.FromMinutes(2), Timeout.InfiniteTimeSpan);
            if (session != null) return session;
            var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
            };
            session = new InferenceSession(BackgroundModel.ModelPath, options);
            return session;
        }
    }

    public static void Unload()
    {
        lock (Gate)
        {
            session?.Dispose();
            session = null;
            idle?.Dispose();
            idle = null;
        }
    }

    public static MagickImage Mask(MagickImage image)
    {
        using var input = new MagickImage(image);
        if (input.HasAlpha)
        {
            input.BackgroundColor = MagickColors.White;
            input.Alpha(AlphaOption.Remove);
            input.Alpha(AlphaOption.Off);
        }
        input.ColorSpace = ColorSpace.sRGB;
        input.Resize(new MagickGeometry(Side, Side) { IgnoreAspectRatio = true });
        using var pixels = input.GetPixels();
        var rgb = pixels.ToByteArray(PixelMapping.RGB) ?? throw new PickException(L("err.read_image"));
        var max = Math.Max(rgb.Max() / 255f, 1e-6f);
        var tensor = new DenseTensor<float>([1, 3, Side, Side]);
        for (var y = 0; y < Side; y++)
        {
            for (var x = 0; x < Side; x++)
            {
                var offset = (y * Side + x) * 3;
                tensor[0, 0, y, x] = rgb[offset] / 255f / max - 0.5f;
                tensor[0, 1, y, x] = rgb[offset + 1] / 255f / max - 0.5f;
                tensor[0, 2, y, x] = rgb[offset + 2] / 255f / max - 0.5f;
            }
        }
        var model = Session();
        var name = model.InputMetadata.Keys.First();
        using var results = model.Run([NamedOnnxValue.CreateFromTensor(name, tensor)]);
        var output = results.First().AsTensor<float>();
        var values = new float[Side * Side];
        var low = float.MaxValue;
        var high = float.MinValue;
        for (var y = 0; y < Side; y++)
        {
            for (var x = 0; x < Side; x++)
            {
                var value = output[0, 0, y, x];
                values[y * Side + x] = value;
                if (value < low) low = value;
                if (value > high) high = value;
            }
        }
        var range = Math.Max(high - low, 1e-6f);
        var bytes = new byte[Side * Side];
        for (var index = 0; index < bytes.Length; index++) bytes[index] = (byte)Math.Clamp((values[index] - low) / range * 255f, 0, 255);
        var mask = new MagickImage(bytes, new MagickReadSettings { Width = Side, Height = Side, Format = MagickFormat.Gray, Depth = 8 });
        mask.Resize(new MagickGeometry(image.Width, image.Height) { IgnoreAspectRatio = true });
        return mask;
    }

    /// Bounding box of the opaque part, or null when nothing stands out.
    public static PixelRect? SubjectBounds(MagickImage mask)
    {
        using var pixels = mask.GetPixels();
        var data = pixels.ToByteArray(PixelMapping.RGB) ?? [];
        int width = (int)mask.Width, height = (int)mask.Height;
        int minX = width, minY = height, maxX = -1, maxY = -1;
        long solid = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (data[(y * width + x) * 3] < 128) continue;
                solid++;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        if (maxX < 0 || solid < width * (long)height / 1000) return null;
        return new PixelRect(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }
}
