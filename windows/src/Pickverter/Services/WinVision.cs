using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.FaceAnalysis;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Pickverter.Services;

/// Windows' built-in text recognition (the OCR languages installed with Windows) and face detection.
internal sealed class WinVision : IVisionService
{
    private readonly Lazy<OcrEngine?> engine = new(CreateEngine);
    private readonly Lazy<bool> faces = new(() =>
    {
        try
        {
            return FaceDetector.IsSupported;
        }
        catch (Exception)
        {
            return false;
        }
    });

    private static OcrEngine? CreateEngine()
    {
        try
        {
            var engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine != null) return engine;
            var language = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault();
            return language == null ? null : OcrEngine.TryCreateFromLanguage(language);
        }
        catch (Exception error)
        {
            Debug.Log("ocr: " + error.Message);
            return null;
        }
    }

    public bool OcrAvailable => engine.Value != null;
    public bool FacesAvailable => faces.Value;

    /// Installed OCR languages, for Settings.
    public static IReadOnlyList<string> OcrLanguages
    {
        get
        {
            try
            {
                return OcrEngine.AvailableRecognizerLanguages.Select(language => language.NativeName).ToList();
            }
            catch (Exception)
            {
                return [];
            }
        }
    }

    public async Task<IReadOnlyList<RecognizedText>> RecognizeAsync(string imagePath, CancellationToken token)
    {
        var ocr = engine.Value ?? throw new PickException(L("err.ocr_missing"));
        var (bitmap, width, height) = await Load(imagePath, (int)Math.Min(OcrEngine.MaxImageDimension, 4000u), BitmapPixelFormat.Bgra8);
        using (bitmap)
        {
            token.ThrowIfCancellationRequested();
            var result = await ocr.RecognizeAsync(bitmap);
            var cjk = ocr.RecognizerLanguage.LanguageTag is var tag && (tag.StartsWith("ja") || tag.StartsWith("zh") || tag.StartsWith("ko"));
            var lines = new List<RecognizedText>();
            foreach (var line in result.Lines)
            {
                if (line.Words.Count == 0) continue;
                double x0 = double.MaxValue, y0 = double.MaxValue, x1 = 0, y1 = 0;
                foreach (var word in line.Words)
                {
                    var box = word.BoundingRect;
                    x0 = Math.Min(x0, box.X);
                    y0 = Math.Min(y0, box.Y);
                    x1 = Math.Max(x1, box.X + box.Width);
                    y1 = Math.Max(y1, box.Y + box.Height);
                }
                var text = cjk ? JoinCjk(line.Words.Select(word => word.Text)) : line.Text;
                lines.Add(new RecognizedText(text, new NormRect(x0 / width, y0 / height, (x1 - x0) / width, (y1 - y0) / height).Clipped()));
            }
            return lines;
        }
    }

    /// Windows puts a space between every CJK word; keep spaces only next to Latin text.
    private static string JoinCjk(IEnumerable<string> words)
    {
        var builder = new StringBuilder();
        foreach (var word in words)
        {
            if (builder.Length > 0 && !(IsCjk(builder[^1]) && word.Length > 0 && IsCjk(word[0]))) builder.Append(' ');
            builder.Append(word);
        }
        return builder.ToString();
    }

    private static bool IsCjk(char c) => c is >= '\u3000' and <= '\u9FFF' or >= '\uAC00' and <= '\uD7AF' or >= '\uF900' and <= '\uFAFF' or >= '\uFF00' and <= '\uFFEF';

    public async Task<IReadOnlyList<NormRect>> DetectFacesAsync(string imagePath, CancellationToken token)
    {
        if (!FacesAvailable) return [];
        var detector = await FaceDetector.CreateAsync();
        var (bitmap, width, height) = await Load(imagePath, 2000, BitmapPixelFormat.Bgra8);
        using (bitmap)
        using (var gray = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Gray8))
        {
            token.ThrowIfCancellationRequested();
            var found = await detector.DetectFacesAsync(gray);
            return found.Select(face => new NormRect(face.FaceBox.X / (double)width, face.FaceBox.Y / (double)height,
                face.FaceBox.Width / (double)width, face.FaceBox.Height / (double)height).Clipped()).ToList();
        }
    }

    /// Decodes an image (EXIF orientation applied), scaled down so its longest side is at most maxSide.
    internal static async Task<(SoftwareBitmap Bitmap, int Width, int Height)> Load(string path, int maxSide, BitmapPixelFormat format)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        using IRandomAccessStream stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        double width = decoder.OrientedPixelWidth, height = decoder.OrientedPixelHeight;
        var scale = Math.Min(1, maxSide / Math.Max(width, height));
        var transform = new BitmapTransform();
        if (scale < 1)
        {
            // The transform scales before orientation is applied.
            var swap = decoder.OrientedPixelWidth != decoder.PixelWidth;
            transform.ScaledWidth = (uint)Math.Max(1, Math.Round((swap ? height : width) * scale));
            transform.ScaledHeight = (uint)Math.Max(1, Math.Round((swap ? width : height) * scale));
            transform.InterpolationMode = BitmapInterpolationMode.Fant;
        }
        var bitmap = await decoder.GetSoftwareBitmapAsync(format, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        return (bitmap, bitmap.PixelWidth, bitmap.PixelHeight);
    }
}
