import AppKit
import ImageIO

/// "Under 1 MB" and friends: the best quality that still fits a mail or upload limit. Sizes are decimal
/// (1 MB = 1,000,000 bytes), the way upload forms count.
enum TargetSize {
    static let imageTargets: [(key: String, bytes: Int64)] = [("max500k", 500_000), ("max1m", 1_000_000),
                                                               ("max2m", 2_000_000), ("max5m", 5_000_000)]
    static let videoTargets: [(key: String, bytes: Int64)] = [("max10m", 10_000_000), ("max25m", 25_000_000),
                                                               ("max50m", 50_000_000), ("max100m", 100_000_000)]

    /// The size an "Under …" option aims for (nil for every other option, "Custom Size…" included).
    static func target(of optionID: String) -> Int64? {
        (imageTargets + videoTargets).first { "tool.compress." + $0.key == optionID }?.bytes
    }

    /// The "Under …" options of Compress plus "Custom Size…".
    static func variants(video: Bool, unavailable: String? = nil) -> [PickOption] {
        (video ? videoTargets : imageTargets).map { key, bytes in
            // A no-break space: "500 KB" never splits across two lines of a wheel label.
            Catalog.variant("tool.compress", key, L("size.under", FileNames.formatBytes(bytes).replacingOccurrences(of: " ", with: "\u{00A0}")),
                            unavailable: unavailable, run(video: video, target: bytes))
        } + [Catalog.variant("tool.compress", "maxcustom", L("size.custom"), unavailable: unavailable, interactive: true,
                             run(video: video, target: nil))]
    }

    /// Files already under the target are left alone; one that can't get there keeps its smallest version.
    static func run(video: Bool, target fixed: Int64?) -> (PickContext) async throws -> PickResult {
        return { ctx in
            let target: Int64
            if let fixed {
                target = fixed
            } else {
                target = try await MainActor.run { try ask(for: ctx.files[0]) }
                ctx.beginWork()
            }
            let count = ctx.files.count
            var outputs: [URL] = []
            var missed: [String] = []
            var failures: [String] = []
            for (index, file) in ctx.files.enumerated() {
                try Task.checkCancellation()
                if count > 1 { ctx.report(Double(index) / Double(count), "\(index + 1)/\(count) · \(file.lastPathComponent)") }
                guard FileNames.size(of: file) > target else { continue }
                do {
                    let slice = ctx.slice(index, of: count)
                    let output = video ? try await compressVideo(file, under: target, ctx: slice)
                                       : try await compressImage(file, under: target, ctx: slice)
                    outputs.append(output)
                    let size = FileNames.size(of: output)
                    if size > target {
                        missed.append(L("msg.target_missed", FileNames.formatBytes(target), FileNames.formatBytes(size)))
                    }
                } catch {
                    if Task.isCancelled || error is CancellationError { throw CancellationError() }
                    if count == 1 { throw error }
                    failures.append("\(file.lastPathComponent): \(error.localizedDescription)")
                }
            }
            if outputs.isEmpty {
                if let failure = failures.first { throw PickError(failure) }
                return .note(L("msg.already_under", FileNames.formatBytes(target)))
            }
            var notes = Array(missed.prefix(1))
            if let failure = failures.first { notes.append(LP("job.failed_some", failures.count, failure)) }
            return PickResult(outputs: outputs, message: notes.isEmpty ? nil : notes.joined(separator: "\n"))
        }
    }

    // MARK: Custom size

    /// Half the file's size as a starting point: whole megabytes from 10 MB, one decimal from 1 MB, two below.
    static func suggestion(for bytes: Int64) -> Double {
        let half = Double(bytes) / 2 / 1_000_000
        if half >= 10 { return half.rounded() }
        if half >= 1 { return (half * 10).rounded() / 10 }
        return max(0.01, (half * 100).rounded() / 100)
    }

    static func megabytes(_ text: String) -> Double? {
        var cleaned = text.trimmingCharacters(in: .whitespaces).lowercased()
        // "1,5 Mo" in French, "1,5 МБ" in Russian.
        for unit in ["mb", "mo", "мб"] where cleaned.hasSuffix(unit) { cleaned = String(cleaned.dropLast(unit.count)) }
        cleaned = cleaned.replacingOccurrences(of: ",", with: ".").trimmingCharacters(in: .whitespaces)
        guard let value = Double(cleaned), value > 0, value.isFinite else { return nil }
        return value
    }

    static func format(_ megabytes: Double) -> String {
        let formatter = NumberFormatter()
        formatter.numberStyle = .decimal
        formatter.usesGroupingSeparator = false
        formatter.maximumFractionDigits = 2
        return formatter.string(from: NSNumber(value: megabytes)) ?? String(megabytes)
    }

    /// Asks for the size in MB above the result cards. Tests answer through PICKVERTER_EDITOR_SIZE (MB or "cancel").
    @MainActor
    static func ask(for file: URL) throws -> Int64 {
        let size = FileNames.size(of: file)
        if let injected = ProcessInfo.processInfo.environment["PICKVERTER_EDITOR_SIZE"] {
            guard let value = megabytes(injected) else { throw CancellationError() }
            return Int64((value * 1_000_000).rounded())
        }
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = L("size.custom.title")
        alert.informativeText = L("size.custom.info", file.lastPathComponent, FileNames.formatBytes(size))
        let field = NSTextField(string: format(suggestion(for: size)))
        field.placeholderString = L("size.custom.field")
        field.alignment = .right
        field.translatesAutoresizingMaskIntoConstraints = false
        field.widthAnchor.constraint(equalToConstant: 110).isActive = true
        let unit = NSTextField(labelWithString: "MB")
        unit.textColor = .secondaryLabelColor
        let row = NSStackView(views: [field, unit])
        row.orientation = .horizontal
        row.spacing = 6
        row.frame = NSRect(x: 0, y: 0, width: 260, height: 24)
        alert.accessoryView = row
        alert.addButton(withTitle: L("tool.compress"))
        alert.addButton(withTitle: L("common.cancel"))
        alert.window.initialFirstResponder = field
        while true {
            guard DialogLevel.runModal(alert.window, alert.runModal) == .alertFirstButtonReturn else { throw CancellationError() }
            if let value = megabytes(field.stringValue) { return Int64((value * 1_000_000).rounded()) }
            NSSound.beep()
        }
    }

    // MARK: Images

    /// Lossy formats (kept when this Mac can write them): the highest quality between 0.30 and 0.92 that fits, found
    /// in about six encodes; if 0.30 is still too big the picture shrinks by sqrt(target / size) × 0.95 and the search
    /// runs again (four times at most). PNG (pictures that use transparency) only shrinks, six times at most.
    static func compressImage(_ src: URL, under target: Int64, ctx: PickContext) async throws -> URL {
        let loaded = try ImageEngine.load(src)
        let source = FileNames.normalizedExt(src)
        let ext: String
        if ["jpg", "heic", "webp", "avif"].contains(source), ImageEngine.canEncode(source) {
            ext = source
        } else {
            ext = ImageEngine.usesAlpha(loaded.image) ? "png" : "jpg"
        }
        var original = loaded.image
        if !ImageEngine.supportsAlpha(ext), ImageEngine.hasAlpha(original) {
            original = ImageEngine.redraw(original, width: original.width, height: original.height, opaque: true) ?? original
        }
        let metadata = ImageEngine.metadata(loaded.properties)
        let lossy = ext != "png"
        let rounds = lossy ? 5 : 7
        var image = original
        var scale = 1.0
        var smallest: Data?
        var fit: Data?
        func note(_ data: Data) { if data.count < smallest?.count ?? .max { smallest = data } }
        for round in 0..<rounds {
            try Task.checkCancellation()
            ctx.report(Double(round) / Double(rounds))
            let floor: Data
            if lossy {
                let best = try await encode(image, ext: ext, quality: 0.92, metadata: metadata)
                note(best)
                if Int64(best.count) <= target { fit = best; break }
                floor = try await encode(image, ext: ext, quality: 0.30, metadata: metadata)
                note(floor)
                if Int64(floor.count) <= target {
                    var low = 0.30, high = 0.92
                    var found = floor
                    for _ in 0..<4 {
                        let middle = (low + high) / 2
                        let data = try await encode(image, ext: ext, quality: middle, metadata: metadata)
                        if Int64(data.count) <= target {
                            low = middle
                            found = data
                        } else {
                            high = middle
                        }
                    }
                    fit = found
                    break
                }
            } else {
                floor = try await encode(image, ext: ext, quality: nil, metadata: metadata)
                note(floor)
                if Int64(floor.count) <= target { fit = floor; break }
            }
            guard round + 1 < rounds else { break }
            scale *= (Double(target) / Double(floor.count)).squareRoot() * 0.95
            let width = max(1, Int(Double(original.width) * scale)), height = max(1, Int(Double(original.height) * scale))
            guard width < image.width || height < image.height,
                  let smaller = ImageEngine.redraw(original, width: width, height: height) else { break }
            image = smaller
        }
        guard let output = fit ?? smallest else { throw PickError(L("err.write_image")) }
        return try await ctx.produce(ctx.destination(for: src, ext: ext, suffix: L("sfx.compressed"))) { tmp in
            try output.write(to: tmp)
        }
    }

    /// One encode in memory (ImageIO formats) or through a scratch file (WebP and other external encoders).
    static func encode(_ image: CGImage, ext: String, quality: Double?, metadata: [CFString: Any]) async throws -> Data {
        if let type = ImageEngine.uti(for: ext), ImageEngine.writableTypes.contains(type) {
            let data = NSMutableData()
            guard let destination = CGImageDestinationCreateWithData(data, type as CFString, 1, nil) else {
                throw PickError(L("err.write_image"))
            }
            var properties = metadata
            properties[kCGImagePropertyOrientation] = 1
            if let quality { properties[kCGImageDestinationLossyCompressionQuality] = quality }
            CGImageDestinationAddImage(destination, image, properties as CFDictionary)
            guard CGImageDestinationFinalize(destination) else { throw PickError(L("err.write_image")) }
            return data as Data
        }
        let scratch = FileManager.default.temporaryDirectory.appendingPathComponent("pickverter-size-\(UUID().uuidString).\(ext)")
        defer { try? FileManager.default.removeItem(at: scratch) }
        try await ImageEngine.encode(image, to: scratch, ext: ext, quality: quality, metadata: metadata)
        return try Data(contentsOf: scratch)
    }

    // MARK: Video

    /// H.264 + AAC at the bitrate the target allows: total kbps = target × 8 / 1000 / seconds × 0.95, audio a tenth of
    /// it (48–128 kbps), the picture capped at 480p below 300 kbps, 720p below 900, 1080p below 2500 (never enlarged).
    /// Still too big: once more at video × target / result × 0.95, keeping the smaller of the two.
    static func compressVideo(_ src: URL, under target: Int64, ctx: PickContext) async throws -> URL {
        guard Tools.ffmpeg != nil else { throw PickError(Tools.ffmpegHint) }
        let info = await MediaEngine.probe(src)
        guard info.hasVideo else { throw PickError(L("err.no_video")) }
        guard info.duration > 0 else { throw PickError(L("err.export")) }
        let total = Double(target) * 8 / 1000 / info.duration * 0.95
        let audio = info.hasAudio ? min(max(total * 0.1, 48), 128) : 0
        let video = max(total - audio, 32)
        let frame = info.displaySize ?? CGSize(width: info.width, height: info.height)
        func arguments(_ kbps: Double) -> [String] {
            let rate = max(32, Int(kbps.rounded()))
            let cap: Int? = kbps < 300 ? 480 : kbps < 900 ? 720 : kbps < 2500 ? 1080 : nil
            var filter = "scale=trunc(iw/2)*2:trunc(ih/2)*2"
            if let cap, min(frame.width, frame.height) > CGFloat(cap) {
                filter = frame.height <= frame.width ? "scale=-2:\(cap)" : "scale=\(cap):-2"
            }
            var args = ["-map", "0:v:0"] + (audio > 0 ? ["-map", "0:a:0"] : []) + ["-vf", filter]
            if Tools.hasEncoder("libx264") {
                args += ["-c:v", "libx264", "-preset", "medium", "-b:v", "\(rate)k", "-maxrate", "\(Int(Double(rate) * 1.3))k",
                         "-bufsize", "\(rate * 2)k", "-pix_fmt", "yuv420p"]
            } else {
                args += ["-c:v", "h264_videotoolbox", "-b:v", "\(rate)k", "-pix_fmt", "yuv420p"]
            }
            args += audio > 0 ? ["-c:a", "aac", "-b:a", "\(Int(audio.rounded()))k"] : ["-an"]
            return args + ["-movflags", "+faststart"]
        }
        let destination = ctx.destination(for: src, ext: "mp4", suffix: L("sfx.compressed"))
        let first = try await ctx.produce(destination) { tmp in
            try await MediaEngine.runFFmpeg(input: src, output: tmp, args: arguments(video), duration: info.duration,
                                            ctx: ctx.portion(0, 0.6))
        }
        let size = FileNames.size(of: first)
        guard size > target else { return first }
        let retry = video * Double(target) / Double(size) * 0.95
        let second = try await ctx.produce(destination) { tmp in
            try await MediaEngine.runFFmpeg(input: src, output: tmp, args: arguments(retry), duration: info.duration,
                                            ctx: ctx.portion(0.6, 1))
        }
        let fm = FileManager.default
        guard FileNames.size(of: second) < size else {
            try? fm.removeItem(at: second)
            return first
        }
        try? fm.removeItem(at: first)
        return (try? NameFixer.place(second, at: first)) ?? second
    }
}
