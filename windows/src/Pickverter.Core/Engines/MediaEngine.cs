using System.Text.Json;

namespace Pickverter;

/// Video and audio. ffmpeg when available (with live progress); Windows' own media stack for the basics otherwise.
public static class MediaEngine
{
    public sealed class Probe
    {
        public double Duration { get; set; }
        public string? VideoCodec { get; set; }
        public string? AudioCodec { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int SampleRate { get; set; }
        /// Display-matrix rotation in degrees (phones record portrait video as rotated landscape).
        public int Rotation { get; set; }
        public bool HasVideo => VideoCodec != null;
        public bool HasAudio => AudioCodec != null;

        public (int Width, int Height)? DisplaySize =>
            Width <= 0 || Height <= 0 ? null : Math.Abs(Rotation) % 180 == 90 ? (Height, Width) : (Width, Height);
    }

    public static readonly HashSet<string> AudioExts = ["mp3", "m4a", "wav", "aiff", "flac", "ogg", "opus"];

    private static string Seconds(double value) => Math.Max(0, value).ToString("0.000", CultureInfo.InvariantCulture);

    public static async Task<Probe> ProbeAsync(string path, CancellationToken token = default)
    {
        var info = new Probe();
        if (Tools.FFprobe is { } ffprobe)
        {
            try
            {
                var output = await Shell.RunAsync(ffprobe, ["-v", "error", "-print_format", "json", "-show_streams", "-show_format", path], token: token);
                using var json = JsonDocument.Parse(output);
                var root = json.RootElement;
                if (root.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var duration) &&
                    double.TryParse(duration.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                    info.Duration = seconds;
                if (root.TryGetProperty("streams", out var streams))
                {
                    foreach (var stream in streams.EnumerateArray())
                    {
                        var type = stream.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                        var codec = stream.TryGetProperty("codec_name", out var c) ? c.GetString() : null;
                        var attached = stream.TryGetProperty("disposition", out var disposition) && disposition.TryGetProperty("attached_pic", out var pic) && pic.GetInt32() == 1;
                        if (type == "video" && !attached && info.VideoCodec == null)
                        {
                            info.VideoCodec = codec ?? "video";
                            info.Width = stream.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                            info.Height = stream.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
                            if (stream.TryGetProperty("tags", out var tags) && tags.TryGetProperty("rotate", out var rotate) &&
                                int.TryParse(rotate.GetString(), out var degrees)) info.Rotation = degrees;
                            if (stream.TryGetProperty("side_data_list", out var sideData))
                            {
                                foreach (var side in sideData.EnumerateArray())
                                {
                                    if (side.TryGetProperty("rotation", out var rotation) && rotation.TryGetDouble(out var value)) info.Rotation = (int)value;
                                }
                            }
                        }
                        else if (type == "audio" && info.AudioCodec == null)
                        {
                            info.AudioCodec = codec ?? "audio";
                            info.SampleRate = stream.TryGetProperty("sample_rate", out var rate) && int.TryParse(rate.GetString(), out var hz) ? hz : 0;
                        }
                    }
                }
                return info;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Debug.Log("ffprobe: " + error.Message);
            }
        }
        info.Duration = await Platform.Media.DurationAsync(path);
        if (await Platform.Media.DisplaySizeAsync(path) is { } size)
        {
            info.VideoCodec = "native";
            info.Width = size.Width;
            info.Height = size.Height;
        }
        if (FileKinds.Of(path) is FileKind.Audio or FileKind.Video) info.AudioCodec = "native";
        return info;
    }

    /// arguments holds every input and option; progress comes from ffmpeg's -progress stream.
    public static async Task RunFFmpeg(IReadOnlyList<string> arguments, string output, double duration, PickContext ctx)
    {
        var ffmpeg = Tools.FFmpeg ?? throw new PickException(Tools.FFmpegHint);
        var all = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error" };
        all.AddRange(arguments);
        all.AddRange(["-progress", "pipe:1", "-nostats", output]);
        await Shell.RunAsync(ffmpeg, all, onLine: line =>
        {
            if (duration <= 0 || !(line.StartsWith("out_time_us=", StringComparison.Ordinal) || line.StartsWith("out_time_ms=", StringComparison.Ordinal))) return;
            if (double.TryParse(line[(line.IndexOf('=') + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var micro))
                ctx.Report(Math.Min(0.99, micro / 1_000_000 / duration));
        }, token: ctx.Token);
    }

    public static Task RunFFmpeg(string input, string output, IReadOnlyList<string> args, double duration, PickContext ctx, IReadOnlyList<string>? preInput = null)
    {
        var arguments = new List<string>(preInput ?? []) { "-i", input };
        arguments.AddRange(args);
        return RunFFmpeg(arguments, output, duration, ctx);
    }

    public static List<string> X264(int crf)
    {
        if (Tools.HasEncoder("libx264")) return ["-c:v", "libx264", "-preset", "medium", "-crf", crf.ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p"];
        if (Tools.HasEncoder("h264_mf")) return ["-c:v", "h264_mf", "-b:v", "6M", "-pix_fmt", "yuv420p"];
        return ["-c:v", "mpeg4", "-q:v", "3"];
    }

    public static List<string> AudioCodecArgs(string ext, Probe info, bool allowCopy = false) => ext switch
    {
        "mp3" => allowCopy && info.AudioCodec == "mp3" ? ["-c:a", "copy"] : ["-c:a", "libmp3lame", "-q:a", "2"],
        "m4a" => allowCopy && info.AudioCodec == "aac" ? ["-c:a", "copy"] : ["-c:a", "aac", "-b:a", "256k"],
        "wav" => ["-c:a", "pcm_s16le"],
        "aiff" => ["-c:a", "pcm_s16be"],
        "flac" => ["-c:a", "flac"],
        "ogg" => Tools.HasEncoder("libvorbis") ? ["-c:a", "libvorbis", "-q:a", "6"] : ["-c:a", "libopus", "-b:a", "160k"],
        "opus" => ["-c:a", "libopus", "-b:a", "160k"],
        _ => ["-c:a", "aac", "-b:a", "192k"],
    };

    public static List<string> ConversionArgs(string ext, Probe info, bool allowCopy, ConvertOptions options)
    {
        if (AudioExts.Contains(ext)) return ["-vn", "-map", "0:a:0", .. AudioCodecArgs(ext, info, allowCopy)];
        const string even = "scale=trunc(iw/2)*2:trunc(ih/2)*2";
        switch (ext)
        {
            case "mp4":
            case "m4v":
            case "mov":
            {
                var args = new List<string> { "-map", "0:v:0?", "-map", "0:a:0?" };
                if (allowCopy && info.VideoCodec is "h264" or "hevc")
                {
                    args.AddRange(["-c:v", "copy"]);
                    if (info.VideoCodec == "hevc") args.AddRange(["-tag:v", "hvc1"]);
                }
                else
                {
                    args.AddRange(["-vf", even, .. X264(20)]);
                }
                if (info.AudioCodec != null) args.AddRange(allowCopy && info.AudioCodec == "aac" ? ["-c:a", "copy"] : ["-c:a", "aac", "-b:a", "192k"]);
                if (ext != "mov") args.AddRange(["-movflags", "+faststart"]);
                return args;
            }
            case "mkv":
                if (allowCopy) return ["-map", "0", "-c", "copy"];
                return ["-map", "0:v:0?", "-map", "0:a:0?", "-vf", even, .. X264(20), "-c:a", "aac", "-b:a", "192k"];
            case "webm":
            {
                // VP9 when this ffmpeg has it. BtbN's Windows on Arm build has no libvpx, so AV1, which WebM also carries
                // and every browser plays. GIFs decode to palette/RGBA frames that the encoders refuse: hand them 4:2:0.
                List<string> video = Tools.HasEncoder("libvpx-vp9")
                    ? ["-c:v", "libvpx-vp9", "-pix_fmt", "yuv420p", "-crf", "32", "-b:v", "0", "-row-mt", "1", "-deadline", "good", "-cpu-used", "4"]
                    : Tools.HasEncoder("libsvtav1")
                        ? ["-vf", even, "-c:v", "libsvtav1", "-pix_fmt", "yuv420p", "-crf", "35", "-preset", "8"]
                        : ["-vf", even, "-c:v", "libaom-av1", "-pix_fmt", "yuv420p", "-crf", "34", "-b:v", "0", "-cpu-used", "6", "-row-mt", "1"];
                return ["-map", "0:v:0?", "-map", "0:a:0?", .. video, "-c:a", "libopus", "-b:a", "128k"];
            }
            case "gif":
            {
                var scale = options.GifWidth is { } width ? $"scale='min({width},iw)':-2:flags=lanczos," : "";
                return ["-vf", $"fps={options.GifFps},{scale}split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4",
                        "-loop", "0"];
            }
            default:
                return ["-map", "0:v:0?", "-map", "0:a:0?", .. X264(20), "-c:a", "aac"];
        }
    }

    public static async Task<string> Convert(string src, string ext, ConvertOptions options, PickContext ctx)
    {
        var destination = ctx.DestinationFor(src, ext);
        if (Tools.FFmpeg == null) return await NativeConvert(src, ext, destination, ctx);
        var info = await ProbeAsync(src, ctx.Token);
        if (AudioExts.Contains(ext) && !info.HasAudio) throw new PickException(L("err.no_audio"));
        var primary = ConversionArgs(ext, info, true, options);
        try
        {
            return await ctx.Produce(destination, tmp => RunFFmpeg(src, tmp, primary, info.Duration, ctx));
        }
        catch (ToolFailure)
        {
            var fallback = ConversionArgs(ext, info, false, options);
            if (fallback.SequenceEqual(primary)) throw;
            return await ctx.Produce(destination, tmp => RunFFmpeg(src, tmp, fallback, info.Duration, ctx));
        }
    }

    private static async Task<string> NativeConvert(string src, string ext, string destination, PickContext ctx)
    {
        if (!Platform.Media.CanTranscode(FileNames.NormalizedExt(src), ext)) throw new PickException(Tools.FFmpegHint);
        return await ctx.Produce(destination, tmp => Platform.Media.TranscodeAsync(src, tmp, ext, null, null, ctx));
    }

    // Tools

    public static async Task<string> Tool(string src, string ext, string suffix, PickContext ctx, Func<Probe, List<string>> build,
                                          IReadOnlyList<string>? preInput = null)
    {
        if (Tools.FFmpeg == null) throw new PickException(Tools.FFmpegHint);
        var info = await ProbeAsync(src, ctx.Token);
        var args = build(info);
        return await ctx.Produce(ctx.DestinationFor(src, ext, suffix), tmp => RunFFmpeg(src, tmp, args, info.Duration, ctx, preInput));
    }

    public static string AudioOutputExt(string src)
    {
        var ext = FileNames.NormalizedExt(src);
        return AudioExts.Contains(ext) ? ext : "m4a";
    }

    public static async Task<string> CompressVideo(string src, int crf, PickContext ctx)
    {
        var output = await Tool(src, "mp4", L("sfx.compressed"), ctx, info =>
        {
            if (!info.HasVideo) throw new PickException(L("err.no_video"));
            var args = new List<string> { "-map", "0:v:0", "-map", "0:a:0?", "-vf", "scale='min(1920,trunc(iw/2)*2)':-2" };
            args.AddRange(X264(crf));
            if (info.HasAudio) args.AddRange(["-c:a", "aac", "-b:a", crf >= 28 ? "96k" : "128k"]);
            args.AddRange(["-movflags", "+faststart"]);
            return args;
        });
        ImageEngine.KeepIfSmaller(output, src);
        return output;
    }

    public static Task<string> Downscale(string src, int height, PickContext ctx) =>
        Tool(src, "mp4", $"{height}p", ctx, info =>
        {
            if (!info.HasVideo) throw new PickException(L("err.no_video"));
            if (info.Height > 0 && Math.Min(info.Height, info.Width) <= height) throw new PickException(L("err.already_res", $"{height}p"));
            var filter = info.Height >= info.Width ? $"scale={height}:-2" : $"scale=-2:{height}";
            var args = new List<string> { "-map", "0:v:0", "-map", "0:a:0?", "-vf", filter };
            args.AddRange(X264(23));
            if (info.HasAudio) args.AddRange(["-c:a", "aac", "-b:a", "160k"]);
            args.AddRange(["-movflags", "+faststart"]);
            return args;
        });

    /// Containers ffmpeg writes back with the picture untouched: muting keeps the file's own format.
    private static readonly HashSet<string> MuteInPlace = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "m4v", "mov", "mkv", "webm", "avi", "wmv", "flv", "mpg", "mpeg", "3gp", "ts", "mts", "m2ts", "ogv",
    };

    public static async Task<string> Mute(string src, PickContext ctx)
    {
        var source = FileNames.NormalizedExt(src);
        var ext = MuteInPlace.Contains(source) ? source : "mp4";
        try
        {
            return await Tool(src, ext, L("sfx.muted"), ctx, info =>
            {
                if (!info.HasAudio) throw new PickException(L("err.already_silent"));
                return ["-map", "0:v", "-c:v", "copy", "-an"];
            });
        }
        catch (ToolFailure error)
        {
            // The picture can't be copied into that container (WMV or Flash video into MP4, say): encode it instead.
            Debug.Log("mute copy: " + error.Message);
            return await Tool(src, "mp4", L("sfx.muted"), ctx, _ =>
                ["-map", "0:v:0", "-an", "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2", .. X264(20), "-movflags", "+faststart"]);
        }
    }

    /// position: 0 = first frame, 0.5 = middle, 1 = last frame.
    public static async Task<string> Snapshot(string src, double position, PickContext ctx)
    {
        var destination = ctx.DestinationFor(src, "png", L("sfx.snapshot"));
        if (Tools.FFmpeg != null)
        {
            var info = await ProbeAsync(src, ctx.Token);
            var time = Math.Max(0, Math.Min(info.Duration - 0.05, info.Duration * position));
            // A fast seek first. MPEG-TS, MPEG-PS and WMV have no index, so the seek can land between keyframes or past
            // the last frame and nothing comes out: then decode the end of the file, or from the start.
            var attempts = new List<(List<string> PreInput, List<string> Args)>
            {
                (["-ss", Seconds(time)], ["-frames:v", "1", "-update", "1"]),
            };
            if (position >= 1)
            {
                attempts.Add((["-sseof", "-3"], ["-update", "1"]));
                attempts.Add(([], ["-update", "1"]));
            }
            else if (time > 0)
            {
                attempts.Add(([], ["-ss", Seconds(time), "-frames:v", "1", "-update", "1"]));
            }
            for (var index = 0; ; index++)
            {
                var (preInput, args) = attempts[index];
                try
                {
                    return await ctx.Produce(destination, tmp => RunFFmpeg(src, tmp, args, 0, ctx, preInput));
                }
                catch (Exception error) when (error is PickException or ToolFailure && index < attempts.Count - 1)
                {
                    Debug.Log("snapshot retry: " + error.Message);
                }
            }
        }
        var duration = await Platform.Media.DurationAsync(src);
        var seconds = Math.Max(0, Math.Min(duration - 0.05, duration * position));
        return await ctx.Produce(destination, tmp => Platform.Media.SnapshotAsync(src, seconds, tmp, ctx.Token));
    }

    /// atempo only accepts 0.5–2 on older ffmpeg builds, so larger factors are chained.
    public static string Atempo(double factor)
    {
        var remaining = factor;
        var parts = new List<string>();
        while (remaining > 2)
        {
            parts.Add("atempo=2.0");
            remaining /= 2;
        }
        while (remaining < 0.5)
        {
            parts.Add("atempo=0.5");
            remaining /= 0.5;
        }
        parts.Add("atempo=" + remaining.ToString(CultureInfo.InvariantCulture));
        return string.Join(",", parts);
    }

    public static Task<string> Speed(string src, double factor, PickContext ctx)
    {
        var isAudio = FileKinds.Of(src) == FileKind.Audio;
        var ext = isAudio ? AudioOutputExt(src) : "mp4";
        return Tool(src, ext, Catalog.SpeedTitle(factor), ctx, info =>
        {
            var pts = (1 / factor).ToString("0.0000", CultureInfo.InvariantCulture);
            if (isAudio || !info.HasVideo) return ["-vn", "-filter:a", Atempo(factor), .. AudioCodecArgs(ext, info)];
            if (info.HasAudio)
                return ["-filter_complex", $"[0:v:0]setpts={pts}*PTS[v];[0:a:0]{Atempo(factor)}[a]", "-map", "[v]", "-map", "[a]",
                        .. X264(21), "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"];
            return ["-filter:v", $"setpts={pts}*PTS", "-an", .. X264(21), "-movflags", "+faststart"];
        });
    }

    public static Task<string> RotateVideo(string src, Turn turn, PickContext ctx)
    {
        var filter = turn switch
        {
            Turn.Right => "transpose=1",
            Turn.Left => "transpose=2",
            Turn.Half => "transpose=1,transpose=1",
            Turn.FlipH => "hflip",
            _ => "vflip",
        };
        return Tool(src, "mp4", turn.IsFlip() ? L("sfx.flipped") : L("sfx.rotated"), ctx, info =>
        {
            var args = new List<string> { "-map", "0:v:0", "-map", "0:a:0?", "-vf", filter };
            args.AddRange(X264(20));
            if (info.HasAudio) args.AddRange(["-c:a", "aac", "-b:a", "192k"]);
            args.AddRange(["-movflags", "+faststart"]);
            return args;
        });
    }

    public static Task<string> Normalize(string src, int lufs, PickContext ctx)
    {
        var ext = AudioOutputExt(src);
        return Tool(src, ext, L("sfx.normalized"), ctx, info =>
        {
            var rate = info.SampleRate > 0 ? info.SampleRate : 48_000;
            return ["-vn", "-af", $"loudnorm=I={lufs}:TP=-1.5:LRA=11", "-ar", rate.ToString(CultureInfo.InvariantCulture), .. AudioCodecArgs(ext, info)];
        });
    }

    public static Task<string> TrimSilence(string src, PickContext ctx)
    {
        var ext = AudioOutputExt(src);
        const string edge = "silenceremove=start_periods=1:start_silence=0.1:start_threshold=-50dB";
        return Tool(src, ext, L("sfx.trimmed"), ctx, info => ["-vn", "-af", $"{edge},areverse,{edge},areverse", .. AudioCodecArgs(ext, info)]);
    }

    public static async Task<string> CompressAudio(string src, int kbps, PickContext ctx)
    {
        var isMp3 = FileNames.NormalizedExt(src) == "mp3";
        var output = await Tool(src, isMp3 ? "mp3" : "m4a", $"{kbps}k", ctx, _ =>
            isMp3 ? ["-vn", "-c:a", "libmp3lame", "-b:a", $"{kbps}k"] : ["-vn", "-c:a", "aac", "-b:a", $"{kbps}k"]);
        ImageEngine.KeepIfSmaller(output, src);
        return output;
    }

    public static Task<string> Mono(string src, PickContext ctx)
    {
        var ext = AudioOutputExt(src);
        return Tool(src, ext, L("sfx.mono"), ctx, info => ["-vn", "-ac", "1", .. AudioCodecArgs(ext, info)]);
    }

    /// AAC/MP3/ALAC go into MP4 as they are, anything else is re-encoded.
    private static List<string> AudioForMp4(Probe info)
    {
        if (info.AudioCodec == null) return [];
        return info.AudioCodec is "aac" or "mp3" or "alac" ? ["-c:a", "copy"] : ["-c:a", "aac", "-b:a", "192k"];
    }

    // Frames and geometry

    public static async Task<double> Duration(string path)
    {
        var info = await ProbeAsync(path);
        if (info.Duration > 0) return info.Duration;
        return await Platform.Media.DurationAsync(path);
    }

    public static async Task<(int Width, int Height)?> DisplaySize(string path)
    {
        var info = await ProbeAsync(path);
        return info.DisplaySize ?? await Platform.Media.DisplaySizeAsync(path);
    }

    /// An upright frame (PNG file) for the editors.
    public static async Task<string> PreviewFrame(string path, int maxSide, double position, CancellationToken token)
    {
        var png = Path.Combine(Path.GetTempPath(), "pickverter-frame-" + Guid.NewGuid().ToString("N")[..10] + ".png");
        var duration = await Duration(path);
        var time = Math.Max(0, duration * position);
        if (Tools.FFmpeg is { } ffmpeg)
        {
            await Shell.RunAsync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-ss", Seconds(time), "-i", path, "-frames:v", "1",
                "-vf", $"scale='min({maxSide},iw)':'min({maxSide},ih)':force_original_aspect_ratio=decrease", png], token: token);
            return png;
        }
        await Platform.Media.SnapshotAsync(path, time, png, token);
        return png;
    }

    // Trim

    public static async Task<string> Cut(string src, TrimSelection range, PickContext ctx)
    {
        var isAudio = FileKinds.Of(src) == FileKind.Audio;
        var sourceExt = FileNames.NormalizedExt(src);
        var length = Math.Max(0.05, range.End - range.Start);
        if (Tools.FFmpeg != null)
        {
            var info = await ProbeAsync(src, ctx.Token);
            string ext;
            List<string> args;
            if (range.Lossless)
            {
                ext = sourceExt.Length == 0 ? (isAudio ? "m4a" : "mp4") : sourceExt;
                args = ["-t", Seconds(length), .. (isAudio || !info.HasVideo ? new[] { "-map", "0:a" } : ["-map", "0:v:0", "-map", "0:a?"]),
                        "-c", "copy", "-avoid_negative_ts", "make_zero"];
                if (ext is "mp4" or "mov" or "m4a") args.AddRange(["-movflags", "+faststart"]);
            }
            else if (isAudio || !info.HasVideo)
            {
                ext = AudioOutputExt(src);
                args = ["-t", Seconds(length), "-vn", .. AudioCodecArgs(ext, info)];
            }
            else
            {
                ext = sourceExt is "mov" or "m4v" ? sourceExt : "mp4";
                args = ["-t", Seconds(length), "-map", "0:v:0", "-map", "0:a:0?", .. X264(18)];
                if (info.HasAudio) args.AddRange(["-c:a", "aac", "-b:a", "192k"]);
                args.AddRange(["-movflags", "+faststart"]);
            }
            var destination = ctx.DestinationFor(src, ext, L("sfx.clip"));
            return await ctx.Produce(destination, tmp => RunFFmpeg(src, tmp, args, length, ctx, ["-ss", Seconds(range.Start)]));
        }
        var target = isAudio ? (Platform.Media.CanTranscode(sourceExt, sourceExt) && sourceExt is "mp3" or "wav" or "flac" ? sourceExt : "m4a") : "mp4";
        return await ctx.Produce(ctx.DestinationFor(src, target, L("sfx.clip")),
            tmp => Platform.Media.TranscodeAsync(src, tmp, target, range.Start, range.End, ctx));
    }

    // Crop and redact

    public static async Task<string> CropVideo(string src, NormRect rect, PickContext ctx)
    {
        var size = await DisplaySize(src) ?? throw new PickException(L("err.no_video"));
        var box = rect.Clipped().Pixels(size.Width, size.Height, even: true);
        var filter = $"crop={box.Width}:{box.Height}:{box.X}:{box.Y}";
        return await Tool(src, "mp4", L("sfx.cropped"), ctx, info =>
        {
            if (!info.HasVideo) throw new PickException(L("err.no_video"));
            return ["-map", "0:v:0", "-map", "0:a:0?", "-vf", filter, .. X264(19), .. AudioForMp4(info), "-movflags", "+faststart"];
        });
    }

    public static async Task<string> CenterCropVideo(string src, double ratio, PickContext ctx)
    {
        var size = await DisplaySize(src) ?? throw new PickException(L("err.no_video"));
        var rect = NormRect.Centered(ratio, size.Width, size.Height);
        if (rect.IsFull) throw new PickException(L("err.already_ratio"));
        return await CropVideo(src, rect, ctx);
    }

    /// Static regions over the whole clip. Pixel and blur sizes follow the picture redaction.
    public static async Task<string> RedactVideo(string src, IReadOnlyList<NormRect> regions, RedactStyle style, PickContext ctx)
    {
        var size = await DisplaySize(src) ?? throw new PickException(L("err.no_video"));
        var boxes = regions.Select(region => region.Clipped().Pixels(size.Width, size.Height, even: true)).Where(box => box.Width >= 4 && box.Height >= 4).ToList();
        if (boxes.Count == 0) throw new PickException(L("err.crop_empty"));
        var graph = new List<string>();
        if (style == RedactStyle.Fill)
        {
            var draws = boxes.Select(box => $"drawbox=x={box.X}:y={box.Y}:w={box.Width}:h={box.Height}:color=0x141414:t=fill");
            graph.Add("[0:v:0]" + string.Join(",", draws) + "[vout]");
        }
        else
        {
            graph.Add($"[0:v:0]split={boxes.Count + 1}[base]" + string.Concat(boxes.Select((_, index) => $"[c{index}]")));
            var last = "base";
            for (var index = 0; index < boxes.Count; index++)
            {
                var box = boxes[index];
                string effect;
                if (style == RedactStyle.Pixelate)
                {
                    var block = Math.Max(6, Math.Min(box.Width, box.Height) / 9);
                    effect = $"scale={Math.Max(1, box.Width / block)}:{Math.Max(1, box.Height / block)}:flags=area,scale={box.Width}:{box.Height}:flags=neighbor";
                }
                else
                {
                    effect = $"gblur=sigma={Math.Max(6, Math.Min(box.Width, box.Height) / 6)}:steps=3";
                }
                graph.Add($"[c{index}]crop={box.Width}:{box.Height}:{box.X}:{box.Y},{effect}[e{index}]");
                var output = index == boxes.Count - 1 ? "vout" : $"o{index}";
                graph.Add($"[{last}][e{index}]overlay={box.X}:{box.Y}[{output}]");
                last = output;
            }
        }
        return await Tool(src, "mp4", L("sfx.redacted"), ctx, info =>
        {
            if (!info.HasVideo) throw new PickException(L("err.no_video"));
            return ["-filter_complex", string.Join(";", graph), "-map", "[vout]", "-map", "0:a:0?", .. X264(19), .. AudioForMp4(info), "-movflags", "+faststart"];
        });
    }

    // Split and join

    public static async Task<string> Split(string src, SplitPlan plan, PickContext ctx)
    {
        var total = await Duration(src);
        if (total <= 1) throw new PickException(L("err.too_short"));
        double segment;
        switch (plan)
        {
            case SplitPlan.Parts parts:
                segment = total / Math.Max(parts.Count, 2) + 0.05;
                break;
            case SplitPlan.Every every:
                if (total <= every.Seconds * 1.05) throw new PickException(L("err.shorter_than", TimeText.Format(every.Seconds, precise: false)));
                segment = every.Seconds;
                break;
            default:
                throw new PickException(L("err.too_short"));
        }
        var isAudio = FileKinds.Of(src) == FileKind.Audio;
        var sourceExt = FileNames.NormalizedExt(src);
        var stem = FileNames.Base(src);
        var count = (int)Math.Ceiling(total / segment);
        if (Tools.FFmpeg != null)
        {
            var ext = sourceExt.Length == 0 ? (isAudio ? "m4a" : "mp4") : sourceExt;
            return await ctx.Produce(ctx.DestinationFor(src, null, L("sfx.parts")), async folder =>
            {
                Directory.CreateDirectory(folder);
                var pattern = Path.Combine(folder, FileNames.Sanitize(stem).Replace("%", "%%") + " %02d." + ext);
                List<string> maps = isAudio ? ["-map", "0:a"] : ["-map", "0:v:0", "-map", "0:a?"];
                List<string> segmenting = ["-f", "segment", "-segment_time", Seconds(segment), "-reset_timestamps", "1", "-segment_start_number", "1"];
                await RunFFmpeg(src, pattern, [.. maps, "-c", "copy", .. segmenting], total, ctx);
                // Stream copy can only cut at keyframes. When that leaves fewer parts than asked for, re-encode once
                // with keyframes forced exactly at the split points.
                var parts = Directory.GetFiles(folder);
                if (!isAudio && parts.Length < count)
                {
                    foreach (var part in parts) File.Delete(part);
                    var info = await ProbeAsync(src, ctx.Token);
                    var output = ext is "mp4" or "mov" or "m4v" or "mkv" ? pattern : Path.Combine(folder, FileNames.Sanitize(stem).Replace("%", "%%") + " %02d.mp4");
                    var args = new List<string>(maps);
                    args.AddRange(X264(20));
                    if (info.HasAudio) args.AddRange(["-c:a", "aac", "-b:a", "192k"]);
                    args.AddRange(["-force_key_frames", $"expr:gte(t,n_forced*{Seconds(segment)})", .. segmenting]);
                    await RunFFmpeg(src, output, args, total, ctx);
                }
            });
        }
        var target = isAudio ? "m4a" : "mp4";
        return await ctx.Produce(ctx.DestinationFor(src, null, L("sfx.parts")), async folder =>
        {
            Directory.CreateDirectory(folder);
            for (var index = 0; index < count; index++)
            {
                ctx.Check();
                var start = index * segment;
                var name = $"{FileNames.Sanitize(stem)} {index + 1:00}.{target}";
                await Platform.Media.TranscodeAsync(src, Path.Combine(folder, name), target, start, Math.Min(total, start + segment), ctx.Slice(index, count));
            }
        });
    }

    public static async Task<PickResult> Join(PickContext ctx)
    {
        var files = NaturalComparer.SortByName(ctx.Files);
        if (files.Count < 2) throw new PickException(L("err.need_two"));
        if (Tools.FFmpeg == null) throw new PickException(Tools.FFmpegHint);
        var isAudio = files.All(file => FileKinds.Of(file) == FileKind.Audio);
        var firstExt = FileNames.NormalizedExt(files[0]);
        var sameExt = files.All(file => FileNames.NormalizedExt(file) == firstExt);
        var infos = new List<Probe>();
        foreach (var file in files) infos.Add(await ProbeAsync(file, ctx.Token));
        var total = infos.Sum(info => info.Duration);
        var outExt = isAudio ? (sameExt && AudioExts.Contains(firstExt) ? firstExt : "m4a")
                             : (sameExt && firstExt is "mp4" or "mov" or "mkv" or "webm" or "m4v" ? firstExt : "mp4");
        var destination = ctx.DestinationFor(files[0], outExt, L("sfx.joined"));

        // Identical streams: join without re-encoding.
        string Signature(Probe info) => $"{info.VideoCodec ?? "-"}|{info.Width}x{info.Height}|{info.AudioCodec ?? "-"}|{info.SampleRate}";
        if (sameExt && infos.Select(Signature).Distinct().Count() == 1)
        {
            try
            {
                var output = await ctx.Produce(destination, async tmp =>
                {
                    var list = Path.Combine(Path.GetDirectoryName(tmp)!, "files.txt");
                    FileSystem.WriteText(list, string.Join("\n", files.Select(file => "file '" + file.Replace("'", "'\\''") + "'")) + "\n");
                    var args = new List<string> { "-f", "concat", "-safe", "0", "-i", list, "-map", "0", "-c", "copy" };
                    if (outExt is "mp4" or "mov" or "m4a" or "m4v") args.AddRange(["-movflags", "+faststart"]);
                    await RunFFmpeg(args, tmp, total, ctx);
                });
                return PickResult.Of(output);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                Debug.Log("lossless join failed, re-encoding: " + error.Message);
            }
        }

        var inputs = new List<string>();
        foreach (var file in files) inputs.AddRange(["-i", file]);
        var graph = new List<string>();
        var extra = files.Count;
        if (isAudio)
        {
            for (var index = 0; index < files.Count; index++) graph.Add($"[{index}:a:0]aresample=48000,aformat=channel_layouts=stereo[a{index}]");
            graph.Add(string.Concat(files.Select((_, index) => $"[a{index}]")) + $"concat=n={files.Count}:v=0:a=1[a]");
            var codec = AudioCodecArgs(outExt, new Probe());
            var output = await ctx.Produce(destination, tmp =>
                RunFFmpeg([.. inputs, "-filter_complex", string.Join(";", graph), "-map", "[a]", .. codec], tmp, total, ctx));
            return PickResult.Of(output);
        }
        var first = infos[0].DisplaySize ?? (1920, 1080);
        int width = first.Width, height = first.Height;
        if (Math.Max(width, height) > 3840)
        {
            var scale = 3840.0 / Math.Max(width, height);
            width = (int)(width * scale);
            height = (int)(height * scale);
        }
        width -= width % 2;
        height -= height % 2;
        for (var index = 0; index < infos.Count; index++)
        {
            graph.Add($"[{index}:v:0]scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30,format=yuv420p[v{index}]");
            if (infos[index].HasAudio)
            {
                graph.Add($"[{index}:a:0]aresample=48000,aformat=channel_layouts=stereo[a{index}]");
            }
            else
            {
                inputs.AddRange(["-f", "lavfi", "-t", Seconds(Math.Max(infos[index].Duration, 0.1)), "-i", "anullsrc=r=48000:cl=stereo"]);
                graph.Add($"[{extra}:a]anull[a{index}]");
                extra++;
            }
        }
        graph.Add(string.Concat(files.Select((_, index) => $"[v{index}][a{index}]")) + $"concat=n={files.Count}:v=1:a=1[v][a]");
        var videoDestination = outExt == "mp4" ? destination : ctx.DestinationFor(files[0], "mp4", L("sfx.joined"));
        var joined = await ctx.Produce(videoDestination, tmp =>
            RunFFmpeg([.. inputs, "-filter_complex", string.Join(";", graph), "-map", "[v]", "-map", "[a]", .. X264(20),
                       "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"], tmp, total, ctx));
        return PickResult.Of(joined);
    }

    // Audio visualizer

    public static Task<string> Visualize(string src, Visualizer style, PickContext ctx)
    {
        var color = Platform.Shell.AccentHex;
        return Tool(src, "mp4", L("sfx.visualizer"), ctx, info =>
        {
            if (!info.HasAudio) throw new PickException(L("err.no_audio"));
            string graph;
            if (style == Visualizer.Spectrum)
            {
                graph = "[0:a:0]showspectrum=s=1280x720:mode=combined:color=intensity:scale=log:slide=scroll:fps=30,format=yuv420p[v]";
            }
            else
            {
                var (w, h) = style == Visualizer.Wave ? (1280, 720) : (1080, 1080);
                graph = $"color=c=0x141416:s={w}x{h}:r=30[bg];" +
                        $"[0:a:0]aformat=channel_layouts=mono,showwaves=s={w}x{h / 2}:mode=cline:rate=30:scale=sqrt:colors={color}[w];" +
                        $"[bg][w]overlay=0:{h / 4}:shortest=1,format=yuv420p[v]";
            }
            return ["-filter_complex", graph, "-map", "[v]", "-map", "0:a:0", .. X264(23), "-c:a", "aac", "-b:a", "192k", "-shortest", "-movflags", "+faststart"];
        });
    }

    // Tags

    public static readonly string[] TagKeys = ["title", "artist", "album", "date", "genre", "comment"];

    public static async Task<Dictionary<string, string>> ReadTags(string path)
    {
        var tags = new Dictionary<string, string>();
        if (Tools.FFprobe is not { } ffprobe) return tags;
        try
        {
            var output = await Shell.RunAsync(ffprobe, ["-v", "error", "-print_format", "json", "-show_format", path]);
            using var json = JsonDocument.Parse(output);
            if (json.RootElement.TryGetProperty("format", out var format) && format.TryGetProperty("tags", out var raw))
            {
                foreach (var property in raw.EnumerateObject())
                {
                    var key = property.Name.ToLowerInvariant();
                    if (TagKeys.Contains(key) && property.Value.ValueKind == JsonValueKind.String) tags[key] = property.Value.GetString() ?? "";
                }
            }
        }
        catch (Exception error)
        {
            Debug.Log("tags: " + error.Message);
        }
        return tags;
    }

    public static async Task WriteTags(string src, IReadOnlyDictionary<string, string> tags, string destination, PickContext ctx)
    {
        if (Tools.FFmpeg == null) throw new PickException(Tools.FFmpegHint);
        var meta = new List<string>();
        foreach (var key in TagKeys)
        {
            if (tags.TryGetValue(key, out var value)) meta.AddRange(["-metadata", $"{key}={value}"]);
        }
        try
        {
            await RunFFmpeg(src, destination, ["-map", "0", "-map_metadata", "0", "-c", "copy", .. meta], 0, ctx);
        }
        catch (ToolFailure)
        {
            await RunFFmpeg(src, destination, ["-map", "0:v?", "-map", "0:a?", "-map_metadata", "0", "-c", "copy", .. meta], 0, ctx);
        }
    }
}
