using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace Pickverter.Services;

/// Media Foundation (Windows' own codecs) for the basics when ffmpeg isn't installed: MP4/M4A/MP3/WAV/WMA,
/// trimming, frame grabs and durations.
internal sealed class WinMedia : INativeMedia
{
    private static readonly HashSet<string> VideoSources = new(StringComparer.OrdinalIgnoreCase) { "mp4", "m4v", "mov", "wmv", "avi", "mkv", "3gp" };
    private static readonly HashSet<string> AudioSources = new(StringComparer.OrdinalIgnoreCase) { "mp3", "m4a", "aac", "wav", "wma", "flac" };
    private static readonly HashSet<string> VideoTargets = new(StringComparer.OrdinalIgnoreCase) { "mp4", "wmv" };
    private static readonly HashSet<string> AudioTargets = new(StringComparer.OrdinalIgnoreCase) { "m4a", "mp3", "wav", "wma", "flac" };

    private readonly Lazy<bool> available = new(() =>
    {
        // Windows N editions and Windows Server ship without Media Foundation unless the feature is added.
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return File.Exists(Path.Combine(system, "mfplat.dll")) && File.Exists(Path.Combine(system, "mfreadwrite.dll"));
    });

    public bool Available => available.Value;

    public bool CanTranscode(string sourceExt, string targetExt)
    {
        if (!Available) return false;
        var video = VideoSources.Contains(sourceExt);
        if (!video && !AudioSources.Contains(sourceExt)) return false;
        if (AudioTargets.Contains(targetExt)) return true;
        return video && VideoTargets.Contains(targetExt);
    }

    private static MediaEncodingProfile Profile(string ext) => ext switch
    {
        "mp4" => MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto),
        "wmv" => MediaEncodingProfile.CreateWmv(VideoEncodingQuality.Auto),
        "m4a" => MediaEncodingProfile.CreateM4a(AudioEncodingQuality.High),
        "mp3" => MediaEncodingProfile.CreateMp3(AudioEncodingQuality.High),
        "wav" => MediaEncodingProfile.CreateWav(AudioEncodingQuality.High),
        "wma" => MediaEncodingProfile.CreateWma(AudioEncodingQuality.High),
        "flac" => MediaEncodingProfile.CreateFlac(AudioEncodingQuality.High),
        _ => throw new PickException(Tools.FFmpegHint),
    };

    private static async Task<StorageFile> Open(string path) => await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));

    public async Task TranscodeAsync(string source, string destination, string targetExt, double? start, double? end, PickContext ctx)
    {
        var input = await Open(source);
        await File.WriteAllBytesAsync(destination, []);
        var output = await Open(destination);
        var profile = Profile(targetExt);
        if (profile.Video != null && await DisplaySizeAsync(source) is { } size)
        {
            profile.Video.Width = (uint)(size.Width / 2 * 2);
            profile.Video.Height = (uint)(size.Height / 2 * 2);
        }
        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
        if (start is > 0) transcoder.TrimStartTime = TimeSpan.FromSeconds(start.Value);
        if (end is { } stop)
        {
            var duration = await DurationAsync(source);
            // TrimStopTime is measured back from the end of the file.
            if (duration > stop) transcoder.TrimStopTime = TimeSpan.FromSeconds(duration - stop);
        }
        var prepared = await transcoder.PrepareFileTranscodeAsync(input, output, profile);
        if (!prepared.CanTranscode)
        {
            Debug.Log("media foundation: " + prepared.FailureReason);
            throw new PickException(Tools.FFmpegHint);
        }
        var operation = prepared.TranscodeAsync();
        operation.Progress = (_, percent) => ctx.Report(percent / 100.0);
        using var registration = ctx.Token.Register(() => operation.Cancel());
        try
        {
            await operation;
        }
        catch (Exception) when (ctx.Token.IsCancellationRequested)
        {
            throw new OperationCanceledException(ctx.Token);
        }
    }

    public async Task SnapshotAsync(string source, double position, string pngPath, CancellationToken token)
    {
        var file = await Open(source);
        var clip = await MediaClip.CreateFromFileAsync(file);
        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        var size = await DisplaySizeAsync(source) ?? (1280, 720);
        var time = TimeSpan.FromSeconds(Math.Clamp(position, 0, Math.Max(0, clip.OriginalDuration.TotalSeconds - 0.05)));
        using var frame = await composition.GetThumbnailAsync(time, size.Width, size.Height, VideoFramePrecision.NearestFrame);
        token.ThrowIfCancellationRequested();
        var decoder = await BitmapDecoder.CreateAsync(frame);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
        await WriteBitmap(bitmap, pngPath, BitmapEncoder.PngEncoderId);
    }

    internal static async Task WriteBitmap(SoftwareBitmap bitmap, string path, Guid encoderId, BitmapPropertySet? properties = null)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = properties == null
            ? await BitmapEncoder.CreateAsync(encoderId, stream)
            : await BitmapEncoder.CreateAsync(encoderId, stream, properties);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        stream.Seek(0);
        await using var output = File.Create(path);
        await stream.AsStreamForRead().CopyToAsync(output);
    }

    public async Task<double> DurationAsync(string source)
    {
        try
        {
            var file = await Open(source);
            var video = await file.Properties.GetVideoPropertiesAsync();
            if (video.Duration > TimeSpan.Zero) return video.Duration.TotalSeconds;
            var music = await file.Properties.GetMusicPropertiesAsync();
            return music.Duration.TotalSeconds;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    public async Task<(int Width, int Height)?> DisplaySizeAsync(string source)
    {
        try
        {
            var file = await Open(source);
            var video = await file.Properties.GetVideoPropertiesAsync();
            if (video.Width == 0 || video.Height == 0) return null;
            var turned = video.Orientation is VideoOrientation.Rotate90 or VideoOrientation.Rotate270;
            return turned ? ((int)video.Height, (int)video.Width) : ((int)video.Width, (int)video.Height);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
