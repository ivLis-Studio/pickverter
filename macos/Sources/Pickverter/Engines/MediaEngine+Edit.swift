import AppKit
import AVFoundation

enum SplitPlan {
    case parts(Int)
    case every(Double)
}

enum Visualizer: String, CaseIterable {
    case wave, square, spectrum
    var title: String { L("viz." + rawValue) }
}

extension MediaEngine {
    static func seconds(_ value: Double) -> String { String(format: "%.3f", max(0, value)) }

    /// AAC/MP3/ALAC go into MP4 as-is, anything else is re-encoded.
    static func audioForMP4(_ info: Probe) -> [String] {
        guard let codec = info.audioCodec else { return [] }
        return ["aac", "mp3", "alac"].contains(codec) ? ["-c:a", "copy"] : ["-c:a", "aac", "-b:a", "192k"]
    }

    // MARK: Frames & geometry

    static func duration(_ url: URL) async -> Double {
        let info = await probe(url)
        if info.duration > 0 { return info.duration }
        return (try? await AVURLAsset(url: url).load(.duration).seconds) ?? 0
    }

    /// Upright frame for the editors. AVFoundation first, ffmpeg for containers it can't open.
    static func previewFrame(_ url: URL, maxSide: Int = 1600, at position: Double = 0.33) async throws -> CGImage {
        let asset = AVURLAsset(url: url)
        if (try? await asset.load(.isPlayable)) == true, let length = try? await asset.load(.duration) {
            let generator = AVAssetImageGenerator(asset: asset)
            generator.appliesPreferredTrackTransform = true
            generator.maximumSize = CGSize(width: maxSide, height: maxSide)
            let time = CMTime(seconds: max(0, length.seconds * position), preferredTimescale: 600)
            if let frame = try? await generator.image(at: time).image { return frame }
        }
        guard let ffmpeg = Tools.ffmpeg else { throw PickError(Tools.ffmpegHint) }
        let info = await probe(url)
        let tmp = FileManager.default.temporaryDirectory.appendingPathComponent("pickverter-frame-\(UUID().uuidString).png")
        defer { try? FileManager.default.removeItem(at: tmp) }
        try await Shell.run(ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-ss", seconds(info.duration * position),
                                     "-i", url.path, "-frames:v", "1", "-vf",
                                     "scale='if(gt(iw,ih),min(\(maxSide),iw),-2)':'if(gt(iw,ih),-2,min(\(maxSide),ih))'", tmp.path])
        return try ImageEngine.load(tmp).image
    }

    /// Size of the picture as displayed (rotation applied), which is what ffmpeg filters see.
    static func displaySize(_ url: URL) async -> CGSize? {
        let asset = AVURLAsset(url: url)
        if let track = try? await asset.loadTracks(withMediaType: .video).first,
           let size = try? await track.load(.naturalSize), size.width > 0,
           let transform = try? await track.load(.preferredTransform) {
            let box = CGRect(origin: .zero, size: size).applying(transform)
            return CGSize(width: abs(box.width).rounded(), height: abs(box.height).rounded())
        }
        return await probe(url).displaySize
    }

    // MARK: Trim

    static func cut(_ src: URL, range: TrimSelection, ctx: PickContext) async throws -> URL {
        let isAudio = FileKind.of(src) == .audio
        let sourceExt = FileNames.normalizedExt(src)
        let length = max(0.05, range.end - range.start)
        if Tools.ffmpeg != nil {
            let info = await probe(src)
            var ext: String
            var args: [String]
            if range.lossless {
                ext = sourceExt.isEmpty ? (isAudio ? "m4a" : "mp4") : sourceExt
                args = ["-t", seconds(length)] + (isAudio || !info.hasVideo ? ["-map", "0:a"] : ["-map", "0:v:0", "-map", "0:a?"])
                    + ["-c", "copy", "-avoid_negative_ts", "make_zero"]
            } else if isAudio || !info.hasVideo {
                ext = audioOutputExt(src)
                args = ["-t", seconds(length), "-vn"] + audioCodecArgs(ext, info: info)
            } else {
                ext = ["mov", "m4v"].contains(sourceExt) ? sourceExt : "mp4"
                args = ["-t", seconds(length), "-map", "0:v:0", "-map", "0:a:0?"] + x264(crf: 18)
                    + (info.hasAudio ? ["-c:a", "aac", "-b:a", "192k"] : []) + ["-movflags", "+faststart"]
            }
            if ext == "mp4" || ext == "mov" || ext == "m4a", range.lossless { args += ["-movflags", "+faststart"] }
            let dst = ctx.destination(for: src, ext: ext, suffix: L("sfx.clip"))
            return try await ctx.produce(dst) { tmp in
                try await runFFmpeg(input: src, output: tmp, args: args, preInput: ["-ss", seconds(range.start)],
                                    duration: length, ctx: ctx)
            }
        }
        let time = CMTimeRange(start: CMTime(seconds: range.start, preferredTimescale: 600),
                               duration: CMTime(seconds: length, preferredTimescale: 600))
        let target: (ext: String, type: AVFileType, preset: String)
        if isAudio {
            target = ("m4a", .m4a, AVAssetExportPresetAppleM4A)
        } else {
            let mov = sourceExt == "mov"
            target = (mov ? "mov" : "mp4", mov ? .mov : .mp4,
                      range.lossless ? AVAssetExportPresetPassthrough : AVAssetExportPresetHighestQuality)
        }
        return try await ctx.produce(ctx.destination(for: src, ext: target.ext, suffix: L("sfx.clip"))) { tmp in
            try await avExport(src, to: tmp, preset: target.preset, type: target.type, timeRange: time, ctx: ctx)
        }
    }

    // MARK: Crop & redact

    static func cropVideo(_ src: URL, rect: NormRect, ctx: PickContext) async throws -> URL {
        guard let size = await displaySize(src) else { throw PickError(L("err.no_video")) }
        let box = rect.clipped().pixels(in: size, even: true)
        let filter = "crop=\(Int(box.width)):\(Int(box.height)):\(Int(box.minX)):\(Int(box.minY))"
        return try await tool(src, ext: "mp4", suffix: L("sfx.cropped"), ctx: ctx) { info in
            guard info.hasVideo else { throw PickError(L("err.no_video")) }
            return ["-map", "0:v:0", "-map", "0:a:0?", "-vf", filter] + x264(crf: 19) + audioForMP4(info) + ["-movflags", "+faststart"]
        }
    }

    static func centerCropVideo(_ src: URL, ratio: Double, ctx: PickContext) async throws -> URL {
        guard let size = await displaySize(src) else { throw PickError(L("err.no_video")) }
        let rect = ImageEngine.centeredRect(ratio: ratio, in: size)
        if rect.isFull { throw PickError(L("err.already_ratio")) }
        return try await cropVideo(src, rect: rect, ctx: ctx)
    }

    /// Static regions over the whole clip. Pixel and blur sizes follow the image redaction.
    static func redactVideo(_ src: URL, regions: [NormRect], style: RedactStyle, ctx: PickContext) async throws -> URL {
        guard let size = await displaySize(src) else { throw PickError(L("err.no_video")) }
        let boxes = regions.map { $0.clipped().pixels(in: size, even: true) }.filter { $0.width >= 4 && $0.height >= 4 }
        guard !boxes.isEmpty else { throw PickError(L("err.crop_empty")) }
        var graph: [String] = []
        if style == .fill {
            let draws = boxes.map { "drawbox=x=\(Int($0.minX)):y=\(Int($0.minY)):w=\(Int($0.width)):h=\(Int($0.height)):color=0x141414:t=fill" }
            graph = ["[0:v:0]" + draws.joined(separator: ",") + "[vout]"]
        } else {
            graph.append("[0:v:0]split=\(boxes.count + 1)[base]" + boxes.indices.map { "[c\($0)]" }.joined())
            var last = "base"
            for (index, box) in boxes.enumerated() {
                let w = Int(box.width), h = Int(box.height), x = Int(box.minX), y = Int(box.minY)
                let effect: String
                if style == .pixelate {
                    let block = max(6, min(w, h) / 9)
                    effect = "scale=\(max(1, w / block)):\(max(1, h / block)):flags=area,scale=\(w):\(h):flags=neighbor"
                } else {
                    effect = "gblur=sigma=\(max(6, min(w, h) / 6)):steps=3"
                }
                graph.append("[c\(index)]crop=\(w):\(h):\(x):\(y),\(effect)[e\(index)]")
                let out = index == boxes.count - 1 ? "vout" : "o\(index)"
                graph.append("[\(last)][e\(index)]overlay=\(x):\(y)[\(out)]")
                last = out
            }
        }
        return try await tool(src, ext: "mp4", suffix: L("sfx.redacted"), ctx: ctx) { info in
            guard info.hasVideo else { throw PickError(L("err.no_video")) }
            return ["-filter_complex", graph.joined(separator: ";"), "-map", "[vout]", "-map", "0:a:0?"] + x264(crf: 19)
                + audioForMP4(info) + ["-movflags", "+faststart"]
        }
    }

    // MARK: Split & join

    static func split(_ src: URL, plan: SplitPlan, ctx: PickContext) async throws -> URL {
        let total = await duration(src)
        guard total > 1 else { throw PickError(L("err.too_short")) }
        let segment: Double
        switch plan {
        case .parts(let count):
            segment = total / Double(max(count, 2)) + 0.05
        case .every(let length):
            guard total > length * 1.05 else { throw PickError(L("err.shorter_than", TimeText.format(length, precise: false))) }
            segment = length
        }
        let isAudio = FileKind.of(src) == .audio
        let sourceExt = FileNames.normalizedExt(src)
        let base = FileNames.split(src.lastPathComponent).base
        if Tools.ffmpeg != nil {
            let ext = sourceExt.isEmpty ? (isAudio ? "m4a" : "mp4") : sourceExt
            return try await ctx.produce(ctx.destination(for: src, ext: nil, suffix: L("sfx.parts"))) { folder in
                try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
                let pattern = folder.appendingPathComponent(base.replacingOccurrences(of: "%", with: "%%") + " %02d." + ext)
                let maps = isAudio ? ["-map", "0:a"] : ["-map", "0:v:0", "-map", "0:a?"]
                let segmenting = ["-f", "segment", "-segment_time", seconds(segment), "-reset_timestamps", "1", "-segment_start_number", "1"]
                try await runFFmpeg(input: src, output: pattern, args: maps + ["-c", "copy"] + segmenting, duration: total, ctx: ctx)
                // Stream copy can only cut at keyframes. When that leaves fewer parts than asked for,
                // re-encode once with keyframes forced exactly at the split points.
                let expected = Int((total / segment).rounded(.up))
                let fm = FileManager.default
                let parts = try fm.contentsOfDirectory(at: folder, includingPropertiesForKeys: nil)
                if !isAudio, parts.count < expected {
                    for part in parts { try fm.removeItem(at: part) }
                    let info = await probe(src)
                    let output = ["mp4", "mov", "m4v", "mkv"].contains(ext) ? pattern
                        : folder.appendingPathComponent(base.replacingOccurrences(of: "%", with: "%%") + " %02d.mp4")
                    try await runFFmpeg(input: src, output: output,
                                        args: maps + x264(crf: 20) + (info.hasAudio ? ["-c:a", "aac", "-b:a", "192k"] : [])
                                            + ["-force_key_frames", "expr:gte(t,n_forced*\(seconds(segment)))"] + segmenting,
                                        duration: total, ctx: ctx)
                }
            }
        }
        let mov = sourceExt == "mov"
        let target: (ext: String, type: AVFileType, preset: String) = isAudio
            ? ("m4a", .m4a, AVAssetExportPresetAppleM4A)
            : (mov ? "mov" : "mp4", mov ? .mov : .mp4, AVAssetExportPresetPassthrough)
        let count = Int((total / segment).rounded(.up))
        return try await ctx.produce(ctx.destination(for: src, ext: nil, suffix: L("sfx.parts"))) { folder in
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            for index in 0..<count {
                try Task.checkCancellation()
                let start = Double(index) * segment
                let range = CMTimeRange(start: CMTime(seconds: start, preferredTimescale: 600),
                                        duration: CMTime(seconds: min(segment, total - start), preferredTimescale: 600))
                let name = String(format: "%@ %02d.%@", base, index + 1, target.ext)
                try await avExport(src, to: folder.appendingPathComponent(name), preset: target.preset, type: target.type,
                                   timeRange: range, ctx: ctx.slice(index, of: count))
            }
        }
    }

    static func join(_ ctx: PickContext) async throws -> PickResult {
        let files = ctx.files.sorted { $0.lastPathComponent.localizedStandardCompare($1.lastPathComponent) == .orderedAscending }
        guard files.count > 1 else { throw PickError(L("err.need_two")) }
        let isAudio = files.allSatisfy { FileKind.of($0) == .audio }
        let firstExt = FileNames.normalizedExt(files[0])
        let sameExt = files.allSatisfy { FileNames.normalizedExt($0) == firstExt }
        guard Tools.ffmpeg != nil else { return try await nativeJoin(files, isAudio: isAudio, ctx: ctx) }

        var infos: [Probe] = []
        for file in files { infos.append(await probe(file)) }
        let total = infos.reduce(0) { $0 + $1.duration }
        let outExt = isAudio ? (sameExt && audioExts.contains(firstExt) ? firstExt : "m4a")
                             : (sameExt && ["mp4", "mov", "mkv", "webm", "m4v"].contains(firstExt) ? firstExt : "mp4")
        let dst = ctx.destination(for: files[0], ext: outExt, suffix: L("sfx.joined"))

        // Identical streams: join without re-encoding.
        let signature: (Probe) -> String = { "\($0.videoCodec ?? "-")|\($0.width)x\($0.height)|\($0.audioCodec ?? "-")|\($0.sampleRate)" }
        if sameExt, Set(infos.map(signature)).count == 1 {
            do {
                let out = try await ctx.produce(dst) { tmp in
                    let list = tmp.deletingLastPathComponent().appendingPathComponent("files.txt")
                    let body = files.map { "file '" + $0.path.replacingOccurrences(of: "'", with: "'\\''") + "'" }
                    try (body.joined(separator: "\n") + "\n").write(to: list, atomically: true, encoding: .utf8)
                    try await runFFmpeg(arguments: ["-f", "concat", "-safe", "0", "-i", list.path, "-map", "0", "-c", "copy"]
                                            + (["mp4", "mov", "m4a", "m4v"].contains(outExt) ? ["-movflags", "+faststart"] : []),
                                        output: tmp, duration: total, ctx: ctx)
                }
                return PickResult(outputs: [out])
            } catch is CancellationError {
                throw CancellationError()
            } catch {
                Debug.log("lossless join failed, re-encoding: \(error)")
            }
        }

        var inputs: [String] = []
        for file in files { inputs += ["-i", file.path] }
        var graph: [String] = []
        var extra = files.count
        if isAudio {
            for index in files.indices { graph.append("[\(index):a:0]aresample=48000,aformat=channel_layouts=stereo[a\(index)]") }
            graph.append(files.indices.map { "[a\($0)]" }.joined() + "concat=n=\(files.count):v=0:a=1[a]")
            let codec = audioCodecArgs(outExt, info: Probe())
            let out = try await ctx.produce(dst) { tmp in
                try await runFFmpeg(arguments: inputs + ["-filter_complex", graph.joined(separator: ";"), "-map", "[a]"] + codec,
                                    output: tmp, duration: total, ctx: ctx)
            }
            return PickResult(outputs: [out])
        }
        let first = await displaySize(files[0]) ?? CGSize(width: 1920, height: 1080)
        var width = Int(first.width), height = Int(first.height)
        if max(width, height) > 3840 {
            let scale = 3840 / Double(max(width, height))
            width = Int(Double(width) * scale)
            height = Int(Double(height) * scale)
        }
        width -= width % 2
        height -= height % 2
        for (index, info) in infos.enumerated() {
            graph.append("[\(index):v:0]scale=\(width):\(height):force_original_aspect_ratio=decrease,"
                         + "pad=\(width):\(height):(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30,format=yuv420p[v\(index)]")
            if info.hasAudio {
                graph.append("[\(index):a:0]aresample=48000,aformat=channel_layouts=stereo[a\(index)]")
            } else {
                inputs += ["-f", "lavfi", "-t", seconds(max(info.duration, 0.1)), "-i", "anullsrc=r=48000:cl=stereo"]
                graph.append("[\(extra):a]anull[a\(index)]")
                extra += 1
            }
        }
        graph.append(files.indices.map { "[v\($0)][a\($0)]" }.joined() + "concat=n=\(files.count):v=1:a=1[v][a]")
        let videoDst = outExt == "mp4" ? dst : ctx.destination(for: files[0], ext: "mp4", suffix: L("sfx.joined"))
        let out = try await ctx.produce(videoDst) { tmp in
            try await runFFmpeg(arguments: inputs + ["-filter_complex", graph.joined(separator: ";"), "-map", "[v]", "-map", "[a]"]
                                    + x264(crf: 20) + ["-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"],
                                output: tmp, duration: total, ctx: ctx)
        }
        return PickResult(outputs: [out])
    }

    static func nativeJoin(_ files: [URL], isAudio: Bool, ctx: PickContext) async throws -> PickResult {
        let composition = AVMutableComposition()
        let video = isAudio ? nil : composition.addMutableTrack(withMediaType: .video, preferredTrackID: kCMPersistentTrackID_Invalid)
        let audio = composition.addMutableTrack(withMediaType: .audio, preferredTrackID: kCMPersistentTrackID_Invalid)
        var cursor = CMTime.zero
        for (index, file) in files.enumerated() {
            let asset = AVURLAsset(url: file)
            let length = try await asset.load(.duration)
            let range = CMTimeRange(start: .zero, duration: length)
            if let track = try await asset.loadTracks(withMediaType: .video).first, let video {
                try video.insertTimeRange(range, of: track, at: cursor)
                if index == 0 { video.preferredTransform = try await track.load(.preferredTransform) }
            }
            if let track = try await asset.loadTracks(withMediaType: .audio).first {
                try audio?.insertTimeRange(range, of: track, at: cursor)
            }
            cursor = CMTimeAdd(cursor, length)
        }
        let ext = isAudio ? "m4a" : "mp4"
        let out = try await ctx.produce(ctx.destination(for: files[0], ext: ext, suffix: L("sfx.joined"))) { tmp in
            try await avExport(asset: composition, to: tmp, preset: isAudio ? AVAssetExportPresetAppleM4A : AVAssetExportPresetHighestQuality,
                               type: isAudio ? .m4a : .mp4, ctx: ctx)
        }
        return PickResult(outputs: [out])
    }

    // MARK: Audio visualizer

    /// The system accent color as ffmpeg's 0xRRGGBB.
    @MainActor
    static var accentHex: String {
        let color = NSColor.controlAccentColor.usingColorSpace(.sRGB) ?? .systemBlue
        return String(format: "0x%02X%02X%02X", Int(color.redComponent * 255), Int(color.greenComponent * 255),
                      Int(color.blueComponent * 255))
    }

    static func visualize(_ src: URL, style: Visualizer, ctx: PickContext) async throws -> URL {
        let color = await MainActor.run { accentHex }
        return try await tool(src, ext: "mp4", suffix: L("sfx.visualizer"), ctx: ctx) { info in
            guard info.hasAudio else { throw PickError(L("err.no_audio")) }
            let graph: String
            switch style {
            case .wave, .square:
                let (w, h) = style == .wave ? (1280, 720) : (1080, 1080)
                graph = "color=c=0x141416:s=\(w)x\(h):r=30[bg];"
                    + "[0:a:0]aformat=channel_layouts=mono,showwaves=s=\(w)x\(h / 2):mode=cline:rate=30:scale=sqrt:colors=\(color)[w];"
                    + "[bg][w]overlay=0:\(h / 4):shortest=1,format=yuv420p[v]"
            case .spectrum:
                graph = "[0:a:0]showspectrum=s=1280x720:mode=combined:color=intensity:scale=log:slide=scroll:fps=30,format=yuv420p[v]"
            }
            return ["-filter_complex", graph, "-map", "[v]", "-map", "0:a:0"] + x264(crf: 23)
                + ["-c:a", "aac", "-b:a", "192k", "-shortest", "-movflags", "+faststart"]
        }
    }

    // MARK: Tags

    static let tagKeys = ["title", "artist", "album", "date", "genre", "comment"]

    static func readTags(_ url: URL) async -> [String: String] {
        var tags: [String: String] = [:]
        if let ffprobe = Tools.ffprobe,
           let out = try? await Shell.run(ffprobe, ["-v", "error", "-print_format", "json", "-show_format", url.path]),
           let json = try? JSONSerialization.jsonObject(with: Data(out.utf8)) as? [String: Any],
           let raw = (json["format"] as? [String: Any])?["tags"] as? [String: Any] {
            for (key, value) in raw {
                let lower = key.lowercased()
                if tagKeys.contains(lower), let text = value as? String { tags[lower] = text }
            }
            return tags
        }
        let asset = AVURLAsset(url: url)
        let items = (try? await asset.load(.commonMetadata)) ?? []
        let map: [AVMetadataKey: String] = [.commonKeyTitle: "title", .commonKeyArtist: "artist", .commonKeyAlbumName: "album",
                                            .commonKeyCreationDate: "date", .commonKeyType: "genre", .commonKeyDescription: "comment"]
        for item in items {
            guard let key = item.commonKey, let name = map[key], let value = try? await item.load(.stringValue) else { continue }
            tags[name] = value
        }
        return tags
    }

    static func writeTags(_ src: URL, tags: [String: String], to dst: URL, ctx: PickContext) async throws {
        guard Tools.ffmpeg != nil else { throw PickError(Tools.ffmpegHint) }
        var meta: [String] = []
        for key in tagKeys {
            if let value = tags[key] { meta += ["-metadata", "\(key)=\(value)"] }
        }
        do {
            try await runFFmpeg(input: src, output: dst, args: ["-map", "0", "-map_metadata", "0", "-c", "copy"] + meta,
                                duration: 0, ctx: ctx)
        } catch is Shell.Failure {
            try await runFFmpeg(input: src, output: dst, args: ["-map", "0:v?", "-map", "0:a?", "-map_metadata", "0", "-c", "copy"] + meta,
                                duration: 0, ctx: ctx)
        }
    }
}
