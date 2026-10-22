import AVFoundation
import Foundation

/// Video and audio. ffmpeg when available (with live progress), AVFoundation/afconvert otherwise.
enum MediaEngine {
    struct Probe {
        var duration: Double = 0
        var videoCodec: String?
        var audioCodec: String?
        var width = 0
        var height = 0
        var sampleRate = 0
        /// Display-matrix rotation in degrees (phones record portrait video as rotated landscape).
        var rotation = 0
        var hasVideo: Bool { videoCodec != nil }
        var hasAudio: Bool { audioCodec != nil }
        var displaySize: CGSize? {
            guard width > 0, height > 0 else { return nil }
            return abs(rotation) % 180 == 90 ? CGSize(width: height, height: width) : CGSize(width: width, height: height)
        }
    }

    static let audioExts: Set<String> = ["mp3", "m4a", "wav", "aiff", "flac", "ogg", "opus"]

    static func probe(_ url: URL) async -> Probe {
        var info = Probe()
        if let ffprobe = Tools.ffprobe,
           let out = try? await Shell.run(ffprobe, ["-v", "error", "-print_format", "json", "-show_streams", "-show_format", url.path]),
           let json = try? JSONSerialization.jsonObject(with: Data(out.utf8)) as? [String: Any] {
            if let format = json["format"] as? [String: Any], let d = format["duration"] as? String {
                info.duration = Double(d) ?? 0
            }
            for stream in json["streams"] as? [[String: Any]] ?? [] {
                let type = stream["codec_type"] as? String
                let codec = stream["codec_name"] as? String
                let attached = ((stream["disposition"] as? [String: Any])?["attached_pic"] as? Int) == 1
                if type == "video", !attached, info.videoCodec == nil {
                    info.videoCodec = codec
                    info.width = stream["width"] as? Int ?? 0
                    info.height = stream["height"] as? Int ?? 0
                    if let tags = stream["tags"] as? [String: Any], let rotate = tags["rotate"] as? String {
                        info.rotation = Int(rotate) ?? 0
                    }
                    for side in stream["side_data_list"] as? [[String: Any]] ?? [] {
                        if let value = side["rotation"] as? NSNumber { info.rotation = value.intValue }
                    }
                } else if type == "audio", info.audioCodec == nil {
                    info.audioCodec = codec
                    info.sampleRate = Int(stream["sample_rate"] as? String ?? "") ?? 0
                }
            }
            return info
        }
        let asset = AVURLAsset(url: url)
        if let duration = try? await asset.load(.duration) { info.duration = duration.seconds }
        if let tracks = try? await asset.load(.tracks) {
            for track in tracks {
                if track.mediaType == .video, info.videoCodec == nil {
                    info.videoCodec = "native"
                    if let size = try? await track.load(.naturalSize) { info.width = Int(size.width); info.height = Int(size.height) }
                }
                if track.mediaType == .audio, info.audioCodec == nil { info.audioCodec = "native" }
            }
        }
        return info
    }

    static func runFFmpeg(input: URL, output: URL, args: [String], preInput: [String] = [], duration: Double,
                          ctx: PickContext) async throws {
        try await runFFmpeg(arguments: preInput + ["-i", input.path] + args, output: output, duration: duration, ctx: ctx)
    }

    /// `arguments` holds every input and option; progress is parsed from ffmpeg's -progress stream.
    static func runFFmpeg(arguments: [String], output: URL, duration: Double, ctx: PickContext) async throws {
        guard let ffmpeg = Tools.ffmpeg else { throw PickError(Tools.ffmpegHint) }
        let all = ["-hide_banner", "-nostdin", "-y", "-loglevel", "error"] + arguments + ["-progress", "pipe:1", "-nostats", output.path]
        try await Shell.run(ffmpeg, all, onLine: { line in
            guard duration > 0, line.hasPrefix("out_time_us=") || line.hasPrefix("out_time_ms=") else { return }
            let micro = Double(line.split(separator: "=").last ?? "") ?? 0
            ctx.report(min(0.99, micro / 1_000_000 / duration))
        })
    }

    static func x264(crf: Int) -> [String] {
        if Tools.hasEncoder("libx264") {
            return ["-c:v", "libx264", "-preset", "medium", "-crf", "\(crf)", "-pix_fmt", "yuv420p"]
        }
        return ["-c:v", "h264_videotoolbox", "-b:v", "6M", "-pix_fmt", "yuv420p"]
    }

    static func audioCodecArgs(_ ext: String, info: Probe, allowCopy: Bool = false) -> [String] {
        switch ext {
        case "mp3": return allowCopy && info.audioCodec == "mp3" ? ["-c:a", "copy"] : ["-c:a", "libmp3lame", "-q:a", "2"]
        case "m4a": return allowCopy && info.audioCodec == "aac" ? ["-c:a", "copy"] : ["-c:a", "aac", "-b:a", "256k"]
        case "wav": return ["-c:a", "pcm_s16le"]
        case "aiff": return ["-c:a", "pcm_s16be"]
        case "flac": return ["-c:a", "flac"]
        case "ogg": return Tools.hasEncoder("libvorbis") ? ["-c:a", "libvorbis", "-q:a", "6"] : ["-c:a", "libopus", "-b:a", "160k"]
        case "opus": return ["-c:a", "libopus", "-b:a", "160k"]
        default: return ["-c:a", "aac", "-b:a", "192k"]
        }
    }

    static func conversionArgs(_ ext: String, info: Probe, allowCopy: Bool, options: ConvertOptions) -> [String] {
        if audioExts.contains(ext) {
            return ["-vn", "-map", "0:a:0"] + audioCodecArgs(ext, info: info, allowCopy: allowCopy)
        }
        let even = "scale=trunc(iw/2)*2:trunc(ih/2)*2"
        switch ext {
        case "mp4", "m4v", "mov":
            var args = ["-map", "0:v:0?", "-map", "0:a:0?"]
            if allowCopy, let codec = info.videoCodec, ["h264", "hevc"].contains(codec) {
                args += ["-c:v", "copy"]
                if codec == "hevc" { args += ["-tag:v", "hvc1"] }
            } else {
                args += ["-vf", even] + x264(crf: 20)
            }
            if let codec = info.audioCodec {
                args += allowCopy && codec == "aac" ? ["-c:a", "copy"] : ["-c:a", "aac", "-b:a", "192k"]
            }
            if ext != "mov" { args += ["-movflags", "+faststart"] }
            return args
        case "mkv":
            if allowCopy { return ["-map", "0", "-c", "copy"] }
            return ["-map", "0:v:0?", "-map", "0:a:0?", "-vf", even] + x264(crf: 20) + ["-c:a", "aac", "-b:a", "192k"]
        case "webm":
            return ["-map", "0:v:0?", "-map", "0:a:0?", "-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-row-mt", "1",
                    "-deadline", "good", "-cpu-used", "4", "-c:a", "libopus", "-b:a", "128k"]
        case "gif":
            let scale = options.gifWidth.map { "scale='min(\($0),iw)':-2:flags=lanczos," } ?? ""
            return ["-vf", "fps=\(options.gifFPS),\(scale)split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4",
                    "-loop", "0"]
        default:
            return ["-map", "0:v:0?", "-map", "0:a:0?"] + x264(crf: 20) + ["-c:a", "aac"]
        }
    }

    static func convert(_ src: URL, to ext: String, options: ConvertOptions = ConvertOptions(), ctx: PickContext) async throws -> URL {
        let dst = ctx.destination(for: src, ext: ext)
        guard Tools.ffmpeg != nil else { return try await nativeConvert(src, to: ext, dst: dst, ctx: ctx) }
        let info = await probe(src)
        if audioExts.contains(ext), !info.hasAudio { throw PickError(L("err.no_audio")) }
        let primary = conversionArgs(ext, info: info, allowCopy: true, options: options)
        do {
            return try await ctx.produce(dst) { tmp in
                try await runFFmpeg(input: src, output: tmp, args: primary, duration: info.duration, ctx: ctx)
            }
        } catch let failure as Shell.Failure {
            let fallback = conversionArgs(ext, info: info, allowCopy: false, options: options)
            guard fallback != primary else { throw failure }
            return try await ctx.produce(dst) { tmp in
                try await runFFmpeg(input: src, output: tmp, args: fallback, duration: info.duration, ctx: ctx)
            }
        }
    }

    static func nativeConvert(_ src: URL, to ext: String, dst: URL, ctx: PickContext) async throws -> URL {
        switch ext {
        case "mp4", "mov", "m4v", "m4a":
            let preset = ext == "m4a" ? AVAssetExportPresetAppleM4A : AVAssetExportPresetHighestQuality
            let type: AVFileType = ext == "mp4" ? .mp4 : ext == "mov" ? .mov : ext == "m4v" ? .m4v : .m4a
            return try await ctx.produce(dst) { tmp in try await avExport(src, to: tmp, preset: preset, type: type, ctx: ctx) }
        case "wav", "aiff", "flac":
            let format: [String] = ext == "wav" ? ["-f", "WAVE", "-d", "LEI16"]
                : ext == "aiff" ? ["-f", "AIFF", "-d", "BEI16"] : ["-f", "flac", "-d", "flac"]
            return try await ctx.produce(dst) { tmp in try await Shell.run("/usr/bin/afconvert", format + [src.path, tmp.path]) }
        default:
            throw PickError(Tools.ffmpegHint)
        }
    }

    static func avExport(_ src: URL, to dst: URL, preset: String, type: AVFileType, timeRange: CMTimeRange? = nil,
                         ctx: PickContext) async throws {
        try await avExport(asset: AVURLAsset(url: src), to: dst, preset: preset, type: type, timeRange: timeRange, ctx: ctx)
    }

    static func avExport(asset: AVAsset, to dst: URL, preset: String, type: AVFileType, timeRange: CMTimeRange? = nil,
                         ctx: PickContext) async throws {
        guard let session = AVAssetExportSession(asset: asset, presetName: preset) else {
            throw PickError(L("err.export"))
        }
        session.outputURL = dst
        session.outputFileType = type
        if let timeRange { session.timeRange = timeRange }
        nonisolated(unsafe) let exporter = session
        let poll = Task {
            while !Task.isCancelled {
                ctx.report(Double(exporter.progress))
                try? await Task.sleep(nanoseconds: 250_000_000)
            }
        }
        await withTaskCancellationHandler {
            await exporter.export()
        } onCancel: {
            exporter.cancelExport()
        }
        poll.cancel()
        if session.status == .cancelled { throw CancellationError() }
        guard session.status == .completed else {
            throw PickError(session.error?.localizedDescription ?? L("err.export"))
        }
    }

    // MARK: Tools

    static func tool(_ src: URL, ext: String, suffix: String, ctx: PickContext, preInput: [String] = [],
                     build: (Probe) throws -> [String]) async throws -> URL {
        guard Tools.ffmpeg != nil else { throw PickError(Tools.ffmpegHint) }
        let info = await probe(src)
        let args = try build(info)
        return try await ctx.produce(ctx.destination(for: src, ext: ext, suffix: suffix)) { tmp in
            try await runFFmpeg(input: src, output: tmp, args: args, preInput: preInput, duration: info.duration, ctx: ctx)
        }
    }

    static func audioOutputExt(_ src: URL) -> String {
        let ext = FileNames.normalizedExt(src)
        return audioExts.contains(ext) ? ext : "m4a"
    }

    static func compressVideo(_ src: URL, crf: Int, ctx: PickContext) async throws -> URL {
        let out = try await tool(src, ext: "mp4", suffix: L("sfx.compressed"), ctx: ctx) { info in
            guard info.hasVideo else { throw PickError(L("err.no_video")) }
            return ["-map", "0:v:0", "-map", "0:a:0?", "-vf", "scale='min(1920,trunc(iw/2)*2)':-2"] + x264(crf: crf)
                + (info.hasAudio ? ["-c:a", "aac", "-b:a", crf >= 28 ? "96k" : "128k"] : []) + ["-movflags", "+faststart"]
        }
        return try ImageEngine.keepIfSmaller(out, than: src)
    }

    static func downscale(_ src: URL, height: Int, ctx: PickContext) async throws -> URL {
        try await tool(src, ext: "mp4", suffix: "\(height)p", ctx: ctx) { info in
            guard info.hasVideo else { throw PickError(L("err.no_video")) }
            if info.height > 0, min(info.height, info.width) <= height { throw PickError(L("err.already_res", "\(height)p")) }
            let filter = info.height >= info.width ? "scale=\(height):-2" : "scale=-2:\(height)"
            return ["-map", "0:v:0", "-map", "0:a:0?", "-vf", filter] + x264(crf: 23)
                + (info.hasAudio ? ["-c:a", "aac", "-b:a", "160k"] : []) + ["-movflags", "+faststart"]
        }
    }

    static func mute(_ src: URL, ctx: PickContext) async throws -> URL {
        let source = FileNames.normalizedExt(src)
        let ext = ["mp4", "mov", "mkv", "webm", "m4v"].contains(source) ? source : "mp4"
        return try await tool(src, ext: ext, suffix: L("sfx.muted"), ctx: ctx) { info in
            guard info.hasAudio else { throw PickError(L("err.already_silent")) }
            return ["-map", "0:v", "-c:v", "copy", "-an"]
        }
    }

    /// position: 0 = first frame, 0.5 = middle, 1 = last frame.
    static func snapshot(_ src: URL, position: Double, ctx: PickContext) async throws -> URL {
        let dst = ctx.destination(for: src, ext: "png", suffix: L("sfx.snapshot"))
        if Tools.ffmpeg != nil {
            let info = await probe(src)
            let time = max(0, min(info.duration - 0.05, info.duration * position))
            return try await ctx.produce(dst) { tmp in
                try await runFFmpeg(input: src, output: tmp, args: ["-frames:v", "1", "-update", "1"],
                                    preInput: ["-ss", String(format: "%.3f", max(0, time))], duration: 0, ctx: ctx)
            }
        }
        let asset = AVURLAsset(url: src)
        let duration = try await asset.load(.duration)
        let generator = AVAssetImageGenerator(asset: asset)
        generator.appliesPreferredTrackTransform = true
        let seconds = max(0, min(duration.seconds - 0.05, duration.seconds * position))
        let frame = try await generator.image(at: CMTime(seconds: seconds, preferredTimescale: 600)).image
        return try await ctx.produce(dst) { tmp in try await ImageEngine.encode(frame, to: tmp, ext: "png") }
    }

    /// atempo only accepts 0.5–2 on older ffmpeg builds, so larger factors are chained.
    static func atempo(_ factor: Double) -> String {
        var remaining = factor
        var parts: [String] = []
        while remaining > 2 { parts.append("atempo=2.0"); remaining /= 2 }
        while remaining < 0.5 { parts.append("atempo=0.5"); remaining /= 0.5 }
        parts.append("atempo=\(remaining)")
        return parts.joined(separator: ",")
    }

    static func speed(_ src: URL, factor: Double, ctx: PickContext) async throws -> URL {
        let isAudio = FileKind.of(src) == .audio
        let ext = isAudio ? audioOutputExt(src) : "mp4"
        return try await tool(src, ext: ext, suffix: Catalog.speedTitle(factor), ctx: ctx) { info in
            let pts = String(format: "%.4f", 1 / factor)
            if isAudio || !info.hasVideo {
                return ["-vn", "-filter:a", atempo(factor)] + audioCodecArgs(ext, info: info)
            }
            if info.hasAudio {
                return ["-filter_complex", "[0:v:0]setpts=\(pts)*PTS[v];[0:a:0]\(atempo(factor))[a]", "-map", "[v]", "-map", "[a]"]
                    + x264(crf: 21) + ["-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"]
            }
            return ["-filter:v", "setpts=\(pts)*PTS", "-an"] + x264(crf: 21) + ["-movflags", "+faststart"]
        }
    }

    static func rotateVideo(_ src: URL, turn: Turn, ctx: PickContext) async throws -> URL {
        let filter: String
        switch turn {
        case .right: filter = "transpose=1"
        case .left: filter = "transpose=2"
        case .half: filter = "transpose=1,transpose=1"
        case .flipH: filter = "hflip"
        case .flipV: filter = "vflip"
        }
        return try await tool(src, ext: "mp4", suffix: turn.isFlip ? L("sfx.flipped") : L("sfx.rotated"), ctx: ctx) { info in
            ["-map", "0:v:0", "-map", "0:a:0?", "-vf", filter] + x264(crf: 20)
                + (info.hasAudio ? ["-c:a", "aac", "-b:a", "192k"] : []) + ["-movflags", "+faststart"]
        }
    }

    static func normalize(_ src: URL, lufs: Int, ctx: PickContext) async throws -> URL {
        let ext = audioOutputExt(src)
        return try await tool(src, ext: ext, suffix: L("sfx.normalized"), ctx: ctx) { info in
            let rate = info.sampleRate > 0 ? info.sampleRate : 48_000
            return ["-vn", "-af", "loudnorm=I=\(lufs):TP=-1.5:LRA=11", "-ar", "\(rate)"] + audioCodecArgs(ext, info: info)
        }
    }

    static func trimSilence(_ src: URL, ctx: PickContext) async throws -> URL {
        let ext = audioOutputExt(src)
        let edge = "silenceremove=start_periods=1:start_silence=0.1:start_threshold=-50dB"
        return try await tool(src, ext: ext, suffix: L("sfx.trimmed"), ctx: ctx) { info in
            ["-vn", "-af", "\(edge),areverse,\(edge),areverse"] + audioCodecArgs(ext, info: info)
        }
    }

    static func compressAudio(_ src: URL, kbps: Int, ctx: PickContext) async throws -> URL {
        let isMP3 = FileNames.normalizedExt(src) == "mp3"
        let out = try await tool(src, ext: isMP3 ? "mp3" : "m4a", suffix: "\(kbps)k", ctx: ctx) { _ in
            isMP3 ? ["-vn", "-c:a", "libmp3lame", "-b:a", "\(kbps)k"] : ["-vn", "-c:a", "aac", "-b:a", "\(kbps)k"]
        }
        return try ImageEngine.keepIfSmaller(out, than: src)
    }

    static func mono(_ src: URL, ctx: PickContext) async throws -> URL {
        let ext = audioOutputExt(src)
        return try await tool(src, ext: ext, suffix: L("sfx.mono"), ctx: ctx) { info in
            ["-vn", "-ac", "1"] + audioCodecArgs(ext, info: info)
        }
    }
}
