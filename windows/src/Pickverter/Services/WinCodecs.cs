using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Pickverter.Services;

/// HEIC export through Windows' own encoder. It needs the "HEIF Image Extensions" and "HEVC Video Extensions"
/// from the Microsoft Store; without them the option says so instead of failing halfway.
internal sealed class WinCodecs : IImageCodecs
{
    private volatile int heic = -1;

    public bool CanEncodeHeic
    {
        get
        {
            if (heic < 0) heic = Probe() ? 1 : 0;
            return heic == 1;
        }
    }

    public void Refresh() => heic = -1;

    private static bool Probe()
    {
        try
        {
            var probe = Task.Run(async () =>
            {
                if (!BitmapEncoder.GetEncoderInformationEnumerator().Any(info => info.CodecId == BitmapEncoder.HeifEncoderId)) return false;
                using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, 64, 64, BitmapAlphaMode.Premultiplied);
                using var stream = new InMemoryRandomAccessStream();
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.HeifEncoderId, stream);
                encoder.SetSoftwareBitmap(bitmap);
                await encoder.FlushAsync();
                return stream.Size > 0;
            });
            return probe.Wait(TimeSpan.FromSeconds(5)) && probe.Result;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task EncodeHeicAsync(string pngPath, string destination, double quality, CancellationToken token)
    {
        if (!CanEncodeHeic) throw new PickException(L("err.heic_encoder"));
        var (bitmap, _, _) = await WinVision.Load(pngPath, 16384, BitmapPixelFormat.Bgra8);
        using (bitmap)
        {
            token.ThrowIfCancellationRequested();
            var properties = new BitmapPropertySet
            {
                { "ImageQuality", new BitmapTypedValue((float)Math.Clamp(quality, 0.05, 1.0), Windows.Foundation.PropertyType.Single) },
            };
            try
            {
                await WinMedia.WriteBitmap(bitmap, destination, BitmapEncoder.HeifEncoderId, properties);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Debug.Log("heic: " + error.Message);
                throw new PickException(L("err.heic_encoder"));
            }
        }
    }
}
