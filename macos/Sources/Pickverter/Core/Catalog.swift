import AppKit
import Foundation

struct ConvertOptions {
    var dpi: CGFloat = 300
    var gifWidth: Int? = 720
    var gifFPS = 15
}

/// Decides which options appear on each ring for the current selection.
/// Order matters: the first item sits at 12 o'clock and positions stay stable between uses.
enum Catalog {
    static func options(for files: [URL], mode: WheelMode) -> [PickOption] {
        guard !files.isEmpty else { return [] }
        let kinds = files.map { FileKind.of($0) }
        switch mode {
        case .convert: return convertOptions(files: files, kinds: kinds)
        case .tools: return toolOptions(files: files, kinds: kinds)
        case .actions: return ActionCatalog.options(for: files)
        }
    }

    /// Every option and every variant, for the CLI.
    static func allOptions(for files: [URL]) -> [PickOption] {
        WheelMode.allCases.flatMap { options(for: files, mode: $0) }.flatMap { [$0] + $0.variants }
    }

    // MARK: - Building blocks

    static func variant(_ parent: String, _ key: String, _ title: String, symbol: String = "", files: Bool = true,
                        unavailable: String? = nil, interactive: Bool = false,
                        _ perform: @escaping (PickContext) async throws -> PickResult) -> PickOption {
        PickOption(id: parent + "." + key, title: title, detail: "", symbol: symbol, unavailable: unavailable,
                   producesFiles: files, interactive: interactive, perform: perform)
    }

    static func tool(_ key: String, symbol: String, detail: String? = nil, files: Bool = true, unavailable: String? = nil,
                     interactive: Bool = false, variants: [PickOption] = [],
                     _ perform: ((PickContext) async throws -> PickResult)? = nil) -> PickOption {
        PickOption(id: "tool." + key, title: L("tool." + key), detail: detail ?? L("tool." + key + ".d"), symbol: symbol,
                   unavailable: unavailable, producesFiles: perform == nil ? (variants.first?.producesFiles ?? files) : files,
                   variants: variants,
                   interactive: perform == nil ? (variants.first?.interactive ?? false) : interactive,
                   perform: perform ?? variants[0].perform)
    }

    /// Asks once (editor for the first file), then applies the answer to every selected file.
    static func edited<Answer>(_ ask: @escaping (PickContext) async throws -> Answer,
                               _ apply: @escaping (URL, Answer, PickContext) async throws -> URL) -> (PickContext) async throws -> PickResult {
        return { ctx in
            let answer = try await ask(ctx)
            ctx.beginWork()
            return try await Batch.perFile { try await apply($0, answer, $1) }(ctx)
        }
    }

    struct Level {
        let key: String
        let title: String
        let image: Double
        let crf: Int
    }

    static var levels: [Level] {
        [Level(key: "balanced", title: L("q.balanced"), image: 0.68, crf: 26),
         Level(key: "high", title: L("q.high"), image: 0.82, crf: 22),
         Level(key: "small", title: L("q.small"), image: 0.5, crf: 30)]
    }

    static func speedTitle(_ factor: Double) -> String {
        factor == factor.rounded() ? "\(Int(factor))×" : "\(factor)×"
    }

    // MARK: - Convert

    static func targets(for url: URL, kind: FileKind) -> [String] {
        let source = FileNames.normalizedExt(url)
        switch kind {
        case .image:
            if source == "gif" { return ["mp4", "webm", "png", "jpg", "webp", "heic", "tiff", "pdf"] }
            return ["jpg", "png", "heic", "webp", "avif", "tiff", "gif", "bmp", "pdf", "ico"]
        case .video: return ["mp4", "mov", "webm", "mkv", "gif", "mp3", "m4a", "wav"]
        case .audio: return ["mp3", "m4a", "wav", "flac", "aiff", "ogg", "opus"]
        case .pdf: return ["png", "jpg", "txt", "md", "docx", "rtf", "html"]
        case .document: return ["pdf", "docx", "rtf", "txt", "md", "html", "odt", "epub"]
        case .hwp: return HWPEngine.targets(for: source)
        case .office: return OfficeEngine.targets(for: source)
        case .table: return ["csv", "tsv", "json", "md", "html"]
        case .json: return ["csv", "tsv", "md", "html"]
        case .subtitle: return ["srt", "vtt", "txt"]
        case .archive: return ["zip", "tar.gz"]
        case .folder, .other: return []
        }
    }

    static func formatLabel(_ ext: String) -> String {
        switch ext {
        case "webp": return "WebP"
        case "opus": return "Opus"
        case "webm": return "WebM"
        default: return ext.uppercased()
        }
    }

    static func formatSymbol(_ ext: String) -> String {
        switch ext {
        case "jpg", "png", "heic", "webp", "avif", "tiff", "bmp", "ico": return "photo"
        case "gif", "mp4", "mov", "webm", "mkv": return "film"
        case "mp3", "m4a", "wav", "flac", "aiff", "ogg", "opus": return "waveform"
        case "pdf": return "doc.richtext"
        case "csv", "tsv", "xlsx": return "tablecells"
        case "json": return "curlybraces"
        case "srt", "vtt": return "captions.bubble"
        case "zip", "tar.gz": return "archivebox"
        case "epub": return "book"
        case "pptx": return "rectangle.on.rectangle"
        default: return "doc.text"
        }
    }

    static func convertOptions(files: [URL], kinds: [FileKind]) -> [PickOption] {
        var common: [String]?
        for (file, kind) in zip(files, kinds) {
            let list = targets(for: file, kind: kind)
            common = common.map { current in current.filter { list.contains($0) } } ?? list
        }
        let sources = Set(files.map { FileNames.normalizedExt($0) })
        let kindSet = Set(kinds)
        return (common ?? [])
            .filter { !(sources.count == 1 && sources.contains($0)) }
            .map { ext in
                let unavailable = convertAvailability(ext, files: files, kinds: kindSet)
                let run: (ConvertOptions) -> (PickContext) async throws -> PickResult = { options in
                    Batch.perFile { src, ctx in try await Converter.convert(src, to: ext, options: options, ctx: ctx) }
                }
                let id = "convert.\(ext)"
                var option = PickOption(id: id, title: formatLabel(ext), detail: "", symbol: formatSymbol(ext), isFormat: true,
                                        outputExt: ext, unavailable: unavailable, perform: run(ConvertOptions()))
                if ext == "gif", kindSet.contains(.video) {
                    option.variants = [
                        variant(id, "720", L("gif.medium"), unavailable: unavailable, run(ConvertOptions(gifWidth: 720, gifFPS: 15))),
                        variant(id, "480", L("gif.small"), unavailable: unavailable, run(ConvertOptions(gifWidth: 480, gifFPS: 12))),
                        variant(id, "full", L("gif.large"), unavailable: unavailable, run(ConvertOptions(gifWidth: nil, gifFPS: 15))),
                    ]
                }
                if ["png", "jpg"].contains(ext), kindSet == [.pdf] || kindSet == [.hwp] {
                    option.variants = [300, 150, 600].map { dpi in
                        variant(id, "\(dpi)", "\(dpi) dpi", unavailable: unavailable, run(ConvertOptions(dpi: CGFloat(dpi))))
                    }
                }
                return option
            }
    }

    static func convertAvailability(_ ext: String, files: [URL], kinds: Set<FileKind>) -> String? {
        let hasFFmpeg = Tools.ffmpeg != nil
        for kind in kinds {
            switch kind {
            case .image:
                if ["mp4", "webm", "mov"].contains(ext) {
                    if !hasFFmpeg { return Tools.ffmpegHint }
                } else if !ImageEngine.canEncode(ext) {
                    return L("err.encoder_missing", formatLabel(ext))
                }
            case .video:
                if !hasFFmpeg && !["mp4", "mov", "m4a"].contains(ext) { return Tools.ffmpegHint }
            case .audio:
                if !hasFFmpeg && !["m4a", "wav", "aiff", "flac"].contains(ext) { return Tools.ffmpegHint }
            case .office:
                for file in files where FileKind.of(file) == .office {
                    if let reason = OfficeEngine.unavailable(FileNames.normalizedExt(file), ext) { return reason }
                }
            case .hwp:
                if let reason = HWPEngine.unavailable { return reason }
            default:
                break
            }
        }
        return nil
    }

    // MARK: - Tools

    static func toolOptions(files: [URL], kinds: [FileKind]) -> [PickOption] {
        let kindSet = Set(kinds)
        var list: [PickOption] = []
        if kindSet.count == 1, let kind = kindSet.first {
            list = kindTools(kind, files: files)
        }
        // Text from Korean Windows that would show up garbled: the fix comes first.
        if let fix = encodingTool(files) { list.insert(fix, at: 0) }
        if kindSet != [.archive] {
            let many = files.count > 1
            list.append(PickOption(id: "tool.zip", title: L(many ? "tool.zip.many" : "tool.zip"),
                                   detail: many ? LP("tool.zip.many.d", files.count) : L("tool.zip.d"), symbol: "archivebox") { ctx in
                PickResult(outputs: [try await ArchiveEngine.compress(ctx.files, format: "zip", ctx: ctx)])
            })
        }
        if kindSet == [.folder] {
            list.append(PickOption(id: "tool.targz", title: "TAR.GZ", detail: L("tool.targz.d"), symbol: "shippingbox") { ctx in
                PickResult(outputs: [try await ArchiveEngine.compress(ctx.files, format: "tar.gz", ctx: ctx)])
            })
            list.append(tool("listing", symbol: "list.bullet", files: false) { ctx in try await ArchiveEngine.copyListing(ctx.files) })
        }
        return list
    }

    /// "Fix Encoding" when the selection is text and some of it is CP949 rather than UTF-8.
    static func encodingTool(_ files: [URL]) -> PickOption? {
        guard files.allSatisfy({ TextCodec.fixableExts.contains($0.pathExtension.lowercased()) }),
              files.prefix(20).contains(where: { NameFixer.isLocal($0) && TextCodec.needsUTF8($0) }) else { return nil }
        return tool("utf8", symbol: "text.badge.checkmark", Batch.perFile { try await TextCodec.fixEncoding($0, ctx: $1) })
    }

    static func rotateVariants(_ turns: [Turn], unavailable: String? = nil,
                               _ body: @escaping (URL, Turn, PickContext) async throws -> URL) -> [PickOption] {
        turns.map { turn in
            variant("tool.rotate", turn.rawValue, turn.title, symbol: turn.symbol, unavailable: unavailable,
                    Batch.perFile { try await body($0, turn, $1) })
        }
    }

    static func cropVariants(video: Bool, unavailable: String?) -> [PickOption] {
        let custom = variant("tool.crop", "custom", L("crop.custom"), symbol: "crop", unavailable: unavailable, interactive: true,
                             edited({ ctx in try await CanvasEditors.crop(ctx.files[0], count: ctx.files.count) }) { src, answer, ctx in
                                 video ? try await MediaEngine.cropVideo(src, rect: answer.rect, ctx: ctx)
                                       : try await ImageEngine.crop(src, rect: answer.rect, ctx: ctx)
                             })
        let presets: [(String, String, Double)] = [("square", "1:1", 1), ("wide", "16:9", 16.0 / 9), ("tall", "9:16", 9.0 / 16),
                                                   ("portrait", "4:5", 0.8)]
        return [custom] + presets.map { key, title, ratio in
            variant("tool.crop", key, L("crop.center", title), unavailable: unavailable, Batch.perFile { src, ctx in
                video ? try await MediaEngine.centerCropVideo(src, ratio: ratio, ctx: ctx)
                      : try await ImageEngine.centerCrop(src, ratio: ratio, ctx: ctx)
            })
        }
    }

    static func redactTool(video: Bool, unavailable: String?) -> PickOption {
        let custom = variant("tool.redact", "custom", L("redact.custom"), symbol: "hand.draw", unavailable: unavailable, interactive: true,
                             edited({ ctx in try await CanvasEditors.redact(ctx.files[0], count: ctx.files.count) }) { src, answer, ctx in
                                 video ? try await MediaEngine.redactVideo(src, regions: answer.regions, style: answer.style, ctx: ctx)
                                       : try await ImageEngine.redact(src, regions: answer.regions, style: answer.style, ctx: ctx)
                             })
        guard !video else {
            return tool("redact", symbol: "checkerboard.rectangle", unavailable: unavailable, interactive: true, custom.perform)
        }
        return tool("redact", symbol: "checkerboard.rectangle", variants: [
            custom,
            variant("tool.redact", "faces", L("redact.auto_faces"), symbol: "person.crop.square",
                    Batch.perFile { try await ImageEngine.autoRedact($0, kind: .faces, style: .pixelate, ctx: $1) }),
            variant("tool.redact", "text", L("redact.auto_text"), symbol: "text.viewfinder",
                    Batch.perFile { try await ImageEngine.autoRedact($0, kind: .text, style: .fill, ctx: $1) }),
        ])
    }

    /// "Edit…" (single file) plus the quick removals for images.
    static func metaTool(files: [URL], image: Bool, unavailable: String? = nil) -> PickOption? {
        var variants: [PickOption] = []
        if files.count == 1 {
            variants.append(variant("tool.meta", "edit", L("meta.edit"), symbol: "pencil", files: false, unavailable: unavailable,
                                    interactive: true) { ctx in
                let edit = try await MetadataEditor.edit(ctx.files[0])
                ctx.beginWork()
                return try await MetadataKit.apply(ctx.files[0], edit: edit, ctx: ctx)
            })
        }
        if image {
            variants.append(variant("tool.meta", "all", L("strip.all"), symbol: "eye.slash",
                                    Batch.perFile { try await ImageEngine.stripMetadata($0, gpsOnly: false, ctx: $1) }))
            variants.append(variant("tool.meta", "gps", L("strip.gps"), symbol: "location.slash",
                                    Batch.perFile { try await ImageEngine.stripMetadata($0, gpsOnly: true, ctx: $1) }))
        }
        guard !variants.isEmpty else { return nil }
        if variants.count == 1 {
            let only = variants[0]
            return tool("meta", symbol: "info.circle", detail: L("tool.meta.d"), files: false, unavailable: unavailable,
                        interactive: true, only.perform)
        }
        return tool("meta", symbol: "info.circle", detail: L("tool.meta.image.d"), variants: variants)
    }

    static func splitVariants(unavailable: String?) -> [PickOption] {
        let plans: [(String, String, SplitPlan)] = [
            ("2", L("split.halves"), .parts(2)), ("3", L("split.thirds"), .parts(3)),
            ("60", L("split.every", "1"), .every(60)), ("300", L("split.every", "5"), .every(300)),
            ("600", L("split.every", "10"), .every(600)),
        ]
        return plans.map { key, title, plan in
            variant("tool.parts", key, title, unavailable: unavailable, Batch.perFile { try await MediaEngine.split($0, plan: plan, ctx: $1) })
        }
    }

    static func cutTool(video: Bool) -> PickOption {
        tool("cut", symbol: "timeline.selection", interactive: true) { ctx in
            let first = ctx.files[0]
            let duration = await MediaEngine.duration(first)
            let range = try await TrimEditor.pick(first, duration: duration, isVideo: video)
            ctx.beginWork()
            return try await Batch.perFile { try await MediaEngine.cut($0, range: range, ctx: $1) }(ctx)
        }
    }

    static func joinTool(_ count: Int, audio: Bool) -> PickOption {
        tool("join", symbol: "rectangle.stack.badge.plus", detail: LP(audio ? "tool.join.audio.d" : "tool.join.d", count)) { ctx in
            try await MediaEngine.join(ctx)
        }
    }

    static func kindTools(_ kind: FileKind, files: [URL]) -> [PickOption] {
        let noFFmpeg = Tools.ffmpeg == nil ? Tools.ffmpegHint : nil
        let many = files.count > 1
        switch kind {
        case .image:
            var list = [
                tool("compress", symbol: "arrow.down.right.and.arrow.up.left", variants: levels.map { level in
                    variant("tool.compress", level.key, level.title,
                            Batch.perFile { try await ImageEngine.compress($0, quality: level.image, ctx: $1) })
                } + TargetSize.variants(video: false)),
                tool("resize", symbol: "arrow.down.forward.and.arrow.up.backward", variants:
                    [0.5, 0.25, 0.75].map { scale in
                        variant("tool.resize", "\(Int(scale * 100))", "\(Int(scale * 100))%",
                                Batch.perFile { try await ImageEngine.resize($0, scale: scale, ctx: $1) })
                    } + ([1280, 1920, 3840] as [Int]).map { side in
                        variant("tool.resize", "\(side)", "\(side)px",
                                Batch.perFile { try await ImageEngine.resize($0, maxSide: side, ctx: $1) })
                    }),
                tool("crop", symbol: "crop", variants: cropVariants(video: false, unavailable: nil)),
                tool("removebg", symbol: "scissors", variants: CutoutStyle.allCases.map { style in
                    variant("tool.removebg", style.rawValue, style.title,
                            Batch.perFile { try await ImageEngine.removeBackground($0, style: style, ctx: $1) })
                }),
                redactTool(video: false, unavailable: nil),
                tool("ocr", symbol: "text.viewfinder", files: false) { ctx in try await ImageEngine.recognizeText(ctx) },
                tool("rotate", symbol: "rotate.right", variants: rotateVariants(Turn.allCases) { src, turn, ctx in
                    try await ImageEngine.transform(src, turn: turn, ctx: ctx)
                }),
            ]
            // SVG carries no photo metadata: editing or stripping it would only turn the drawing into a PNG.
            if !files.allSatisfy({ FileNames.normalizedExt($0) == "svg" }), let meta = metaTool(files: files, image: true) { list.append(meta) }
            if many {
                list.append(tool("topdf", symbol: "doc.richtext", detail: LP("tool.topdf.d", files.count)) { ctx in
                    try await ImageEngine.combineToPDF(ctx)
                })
                list.append(tool("collage", symbol: "square.grid.2x2", detail: LP("tool.collage.d", files.count),
                                 variants: CollageLayout.allCases.map { layout in
                    variant("tool.collage", layout.rawValue, layout.title, symbol: layout.symbol) { ctx in
                        try await ImageEngine.collage(ctx, layout: layout)
                    }
                }))
                list.append(tool("picturebook", symbol: "book", detail: LP("tool.picturebook.d", files.count)) { ctx in
                    PickResult(outputs: [try await EPUBBuilder.fromImages(ctx.files, ctx: ctx)])
                })
            }
            return list
        case .video:
            var list = [
                tool("compress", symbol: "arrow.down.right.and.arrow.up.left", unavailable: noFFmpeg, variants: levels.map { level in
                    variant("tool.compress", level.key, level.title, unavailable: noFFmpeg,
                            Batch.perFile { try await MediaEngine.compressVideo($0, crf: level.crf, ctx: $1) })
                } + TargetSize.variants(video: true, unavailable: noFFmpeg)),
                cutTool(video: true),
                tool("resolution", symbol: "rectangle.compress.vertical", unavailable: noFFmpeg, variants: [720, 1080, 480].map { height in
                    variant("tool.resolution", "\(height)", "\(height)p", unavailable: noFFmpeg,
                            Batch.perFile { try await MediaEngine.downscale($0, height: height, ctx: $1) })
                }),
                tool("crop", symbol: "crop", unavailable: noFFmpeg, variants: cropVariants(video: true, unavailable: noFFmpeg)),
                tool("speed", symbol: "hare", unavailable: noFFmpeg, variants: [2.0, 1.5, 4.0, 0.5].map { factor in
                    variant("tool.speed", "\(factor)", speedTitle(factor), unavailable: noFFmpeg,
                            Batch.perFile { try await MediaEngine.speed($0, factor: factor, ctx: $1) })
                }),
                tool("rotate", symbol: "rotate.right", unavailable: noFFmpeg,
                     variants: rotateVariants([.right, .left, .half, .flipH], unavailable: noFFmpeg) { src, turn, ctx in
                         try await MediaEngine.rotateVideo(src, turn: turn, ctx: ctx)
                     }),
                tool("mute", symbol: "speaker.slash", unavailable: noFFmpeg, Batch.perFile { try await MediaEngine.mute($0, ctx: $1) }),
                tool("snapshot", symbol: "camera", variants: [("middle", 0.5), ("first", 0.0), ("last", 1.0)].map { key, position in
                    variant("tool.snapshot", key, L("frame." + key),
                            Batch.perFile { try await MediaEngine.snapshot($0, position: position, ctx: $1) })
                }),
                tool("parts", symbol: "square.split.2x1", variants: splitVariants(unavailable: nil)),
                redactTool(video: true, unavailable: noFFmpeg),
            ]
            if let meta = metaTool(files: files, image: false, unavailable: noFFmpeg) { list.append(meta) }
            if many { list.insert(joinTool(files.count, audio: false), at: 1) }
            return list
        case .audio:
            var list = [
                cutTool(video: false),
                tool("normalize", symbol: "waveform", unavailable: noFFmpeg, variants: [("podcast", -16), ("stream", -14), ("broadcast", -23)].map { key, lufs in
                    variant("tool.normalize", key, L("lufs." + key), unavailable: noFFmpeg,
                            Batch.perFile { try await MediaEngine.normalize($0, lufs: lufs, ctx: $1) })
                }),
                tool("silence", symbol: "waveform.path.badge.minus", unavailable: noFFmpeg,
                     Batch.perFile { try await MediaEngine.trimSilence($0, ctx: $1) }),
                tool("compress", symbol: "arrow.down.right.and.arrow.up.left", unavailable: noFFmpeg, variants: [128, 64, 192].map { kbps in
                    variant("tool.compress", "\(kbps)", "\(kbps) kbps", unavailable: noFFmpeg,
                            Batch.perFile { try await MediaEngine.compressAudio($0, kbps: kbps, ctx: $1) })
                }),
                tool("mono", symbol: "speaker.wave.1", unavailable: noFFmpeg, Batch.perFile { try await MediaEngine.mono($0, ctx: $1) }),
                tool("speed", symbol: "hare", unavailable: noFFmpeg, variants: [2.0, 1.5, 0.5].map { factor in
                    variant("tool.speed", "\(factor)", speedTitle(factor), unavailable: noFFmpeg,
                            Batch.perFile { try await MediaEngine.speed($0, factor: factor, ctx: $1) })
                }),
                tool("parts", symbol: "square.split.2x1", variants: splitVariants(unavailable: nil)),
                tool("visualize", symbol: "waveform.circle", unavailable: noFFmpeg, variants: Visualizer.allCases.map { style in
                    variant("tool.visualize", style.rawValue, style.title, unavailable: noFFmpeg,
                            Batch.perFile { try await MediaEngine.visualize($0, style: style, ctx: $1) })
                }),
            ]
            if let meta = metaTool(files: files, image: false, unavailable: noFFmpeg) { list.append(meta) }
            if many { list.insert(joinTool(files.count, audio: true), at: 1) }
            return list
        case .pdf:
            var list = [
                tool("compress", symbol: "arrow.down.right.and.arrow.up.left", Batch.perFile { try await PDFEngine.compress($0, ctx: $1) }),
                tool("pages", symbol: "square.grid.3x3", variants: [
                    variant("tool.pages", "organize", L("pages.organize"), symbol: "square.grid.3x3", interactive: true) { ctx in
                        let arrangement = try await PDFOrganizer.arrange(ctx.files)
                        ctx.beginWork()
                        return PickResult(outputs: [try await PDFEngine.arrange(ctx.files, arrangement, ctx: ctx)])
                    },
                    variant("tool.pages", "range", L("pages.range"), symbol: "text.insert", interactive: true,
                            Batch.perFile { try await PDFEngine.extractRange($0, ctx: $1) }),
                    variant("tool.pages", "reverse", L("pages.reverse"), symbol: "arrow.up.arrow.down",
                            Batch.perFile { try await PDFEngine.reverse($0, ctx: $1) }),
                    variant("tool.pages", "blank", L("pages.blank"), symbol: "doc.badge.ellipsis",
                            Batch.perFile { try await PDFEngine.removeBlank($0, ctx: $1) }),
                    variant("tool.pages", "odd", L("pages.odd"), symbol: "1.square",
                            Batch.perFile { try await PDFEngine.oddEven($0, odd: true, ctx: $1) }),
                    variant("tool.pages", "even", L("pages.even"), symbol: "2.square",
                            Batch.perFile { try await PDFEngine.oddEven($0, odd: false, ctx: $1) }),
                ]),
                tool("split", symbol: "square.split.2x1", Batch.perFile { try await PDFEngine.split($0, ctx: $1) }),
                tool("rotate", symbol: "rotate.right", variants: rotateVariants([.right, .left, .half]) { src, turn, ctx in
                    try await PDFEngine.rotate(src, turn: turn, ctx: ctx)
                }),
                tool("copytext", symbol: "doc.on.clipboard", files: false) { ctx in try await PDFEngine.copyText(ctx) },
                tool("lock", symbol: "lock", interactive: true, Batch.perFile { try await PDFEngine.encrypt($0, ctx: $1) }),
            ]
            if let meta = metaTool(files: files, image: false) { list.append(meta) }
            if many {
                list.insert(tool("merge", symbol: "doc.on.doc", detail: LP("tool.merge.d", files.count)) { ctx in
                    try await PDFEngine.merge(ctx)
                }, at: 1)
            }
            return list
        case .document:
            var list = [
                tool("copytext", symbol: "doc.on.clipboard", files: false) { ctx in try await DocumentEngine.copyText(ctx) },
                tool("count", symbol: "number", files: false) { ctx in try await DocumentEngine.count(ctx) },
            ]
            if many {
                list.append(tool("epub", symbol: "book", detail: LP("tool.epub.d", files.count)) { ctx in
                    PickResult(outputs: [try await EPUBBuilder.fromDocuments(ctx.files, ctx: ctx)])
                })
            }
            if files.allSatisfy({ ["docx", "odt"].contains(FileNames.normalizedExt($0)) }) {
                list.append(tool("media", symbol: "photo.on.rectangle", Batch.perFile { try await OfficeEngine.extractMedia($0, ctx: $1) }))
            }
            return list
        case .hwp:
            let unavailable = HWPEngine.unavailable
            return [
                tool("copytext", symbol: "doc.on.clipboard", files: false, unavailable: unavailable) { ctx in try await HWPEngine.copyText(ctx) },
                tool("count", symbol: "number", files: false, unavailable: unavailable) { ctx in try await HWPEngine.count(ctx) },
            ]
        case .office:
            var list: [PickOption] = []
            if files.allSatisfy({ ["pptx", "ppsx", "xlsx", "xlsm"].contains(FileNames.normalizedExt($0)) }) {
                list.append(tool("copytext", symbol: "doc.on.clipboard", files: false) { ctx in try await OfficeEngine.copyText(ctx) })
            }
            list.append(tool("media", symbol: "photo.on.rectangle", Batch.perFile { try await OfficeEngine.extractMedia($0, ctx: $1) }))
            return list
        case .json:
            return [
                tool("pretty", symbol: "text.alignleft", Batch.perFile { try await DataEngine.reformatJSON($0, pretty: true, ctx: $1) }),
                tool("minify", symbol: "arrow.right.to.line", Batch.perFile { try await DataEngine.reformatJSON($0, pretty: false, ctx: $1) }),
            ]
        case .archive:
            return [tool("extract", symbol: "archivebox", Batch.perFile { try await ArchiveEngine.extract($0, ctx: $1) })]
        case .table, .subtitle, .folder, .other:
            return []
        }
    }
}
