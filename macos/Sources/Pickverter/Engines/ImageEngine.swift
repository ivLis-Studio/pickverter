import AppKit
import CoreImage
import ImageIO
import UniformTypeIdentifiers
import Vision

enum ImageEngine {
    static let writableTypes: Set<String> = Set((CGImageDestinationCopyTypeIdentifiers() as? [String]) ?? [])

    static func uti(for ext: String) -> String? {
        switch ext {
        case "jpg", "jpeg": return UTType.jpeg.identifier
        case "png": return UTType.png.identifier
        case "heic": return "public.heic"
        case "webp": return "org.webmproject.webp"
        case "avif": return "public.avif"
        case "tiff", "tif": return UTType.tiff.identifier
        case "gif": return UTType.gif.identifier
        case "bmp": return "com.microsoft.bmp"
        case "ico": return "com.microsoft.ico"
        case "icns": return "com.apple.icns"
        case "jp2": return "public.jpeg-2000"
        default: return nil
        }
    }

    static func canEncode(_ ext: String) -> Bool {
        if ext == "pdf" { return true }
        if let type = uti(for: ext), writableTypes.contains(type) { return true }
        if Tools.magick != nil { return true }
        if ext == "webp" { return Tools.cwebp != nil }
        return false
    }

    static func supportsAlpha(_ ext: String) -> Bool { !["jpg", "jpeg", "bmp"].contains(ext) }

    struct Loaded {
        let image: CGImage
        let properties: [CFString: Any]
    }

    static func pixelSize(_ url: URL) -> CGSize? {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
              let props = CGImageSourceCopyPropertiesAtIndex(source, CGImageSourceGetPrimaryImageIndex(source), nil) as? [CFString: Any],
              let w = props[kCGImagePropertyPixelWidth] as? Int, let h = props[kCGImagePropertyPixelHeight] as? Int
        else { return nil }
        return CGSize(width: w, height: h)
    }

    /// Decodes the primary image with EXIF orientation applied, optionally downsampled.
    static func load(_ url: URL, maxPixel: Int? = nil) throws -> Loaded {
        if let source = CGImageSourceCreateWithURL(url as CFURL, nil), CGImageSourceGetCount(source) > 0 {
            let index = CGImageSourceGetPrimaryImageIndex(source)
            let props = (CGImageSourceCopyPropertiesAtIndex(source, index, nil) as? [CFString: Any]) ?? [:]
            let side = max(props[kCGImagePropertyPixelWidth] as? Int ?? 0, props[kCGImagePropertyPixelHeight] as? Int ?? 0)
            let orientation = props[kCGImagePropertyOrientation] as? Int ?? 1
            let target = maxPixel.map { min($0, side > 0 ? side : $0) } ?? side
            if orientation == 1, target >= side, side > 0, let image = CGImageSourceCreateImageAtIndex(source, index, nil) {
                return Loaded(image: image, properties: props)
            }
            if target > 0 {
                let options: [CFString: Any] = [
                    kCGImageSourceCreateThumbnailFromImageAlways: true,
                    kCGImageSourceCreateThumbnailWithTransform: true,
                    kCGImageSourceThumbnailMaxPixelSize: target,
                    kCGImageSourceShouldCacheImmediately: true,
                ]
                if let image = CGImageSourceCreateThumbnailAtIndex(source, index, options as CFDictionary) {
                    return Loaded(image: image, properties: props)
                }
            }
            if let image = CGImageSourceCreateImageAtIndex(source, index, nil) {
                return Loaded(image: image, properties: props)
            }
        }
        // Vector and exotic formats (SVG...) go through NSImage.
        if let ns = NSImage(contentsOf: url) {
            var size = ns.size
            if size.width < 1 || size.height < 1 { size = CGSize(width: 1024, height: 1024) }
            var scale: CGFloat = 2
            if let maxPixel { scale = min(scale, CGFloat(maxPixel) / max(size.width, size.height)) }
            let pw = max(1, Int(size.width * scale)), ph = max(1, Int(size.height * scale))
            if let ctx = CGContext(data: nil, width: pw, height: ph, bitsPerComponent: 8, bytesPerRow: 0,
                                   space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                   bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) {
                NSGraphicsContext.saveGraphicsState()
                NSGraphicsContext.current = NSGraphicsContext(cgContext: ctx, flipped: false)
                ns.draw(in: NSRect(x: 0, y: 0, width: pw, height: ph))
                NSGraphicsContext.restoreGraphicsState()
                if let image = ctx.makeImage() { return Loaded(image: image, properties: [:]) }
            }
        }
        throw PickError(L("err.read_image"))
    }

    /// Keeps camera/EXIF/GPS info on conversions (orientation is already baked in).
    static func metadata(_ props: [CFString: Any], dropLocation: Bool = false) -> [CFString: Any] {
        var keep: [CFString: Any] = [:]
        for key in [kCGImagePropertyExifDictionary, kCGImagePropertyGPSDictionary, kCGImagePropertyIPTCDictionary,
                    kCGImagePropertyTIFFDictionary, kCGImagePropertyDPIWidth, kCGImagePropertyDPIHeight] {
            if let value = props[key] { keep[key] = value }
        }
        if dropLocation { keep.removeValue(forKey: kCGImagePropertyGPSDictionary) }
        if var tiff = keep[kCGImagePropertyTIFFDictionary] as? [CFString: Any] {
            tiff[kCGImagePropertyTIFFOrientation] = 1
            keep[kCGImagePropertyTIFFDictionary] = tiff
        }
        keep[kCGImagePropertyOrientation] = 1
        return keep
    }

    static func hasAlpha(_ image: CGImage) -> Bool {
        switch image.alphaInfo {
        case .none, .noneSkipFirst, .noneSkipLast: return false
        default: return true
        }
    }

    /// An alpha channel that is actually used: some pixel isn't fully opaque (checked on a copy of at most 512 px,
    /// where a thin transparent edge still shows up as partly transparent).
    static func usesAlpha(_ image: CGImage) -> Bool {
        guard hasAlpha(image) else { return false }
        let scale = min(1, 512 / Double(max(image.width, image.height)))
        let width = max(1, Int(Double(image.width) * scale)), height = max(1, Int(Double(image.height) * scale))
        guard let context = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: width * 4,
                                      space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                      bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return true }
        context.interpolationQuality = .medium
        context.clear(CGRect(x: 0, y: 0, width: width, height: height))
        context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
        guard let data = context.data else { return true }
        let pixels = data.bindMemory(to: UInt8.self, capacity: width * height * 4)
        for index in 0..<(width * height) where pixels[index * 4 + 3] < 255 { return true }
        return false
    }

    static func rgbSpace(_ image: CGImage) -> CGColorSpace {
        if let space = image.colorSpace, space.model == .rgb { return space }
        return CGColorSpace(name: CGColorSpace.sRGB)!
    }

    static func redraw(_ image: CGImage, width: Int, height: Int, opaque: Bool = false,
                       transform: ((CGContext) -> Void)? = nil, drawRect: CGRect? = nil) -> CGImage? {
        let info = opaque ? CGImageAlphaInfo.noneSkipLast.rawValue : CGImageAlphaInfo.premultipliedLast.rawValue
        // HDR pictures (EXR, some HEIC) carry an extended-range space that an 8-bit context refuses: draw those in sRGB.
        guard let ctx = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: rgbSpace(image), bitmapInfo: info)
                ?? CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                             space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: info) else { return nil }
        ctx.interpolationQuality = .high
        if opaque {
            ctx.setFillColor(CGColor(gray: 1, alpha: 1))
            ctx.fill(CGRect(x: 0, y: 0, width: width, height: height))
        }
        transform?(ctx)
        ctx.draw(image, in: drawRect ?? CGRect(x: 0, y: 0, width: width, height: height))
        return ctx.makeImage()
    }

    static func scaled(_ image: CGImage, maxSide: Int) -> CGImage? {
        let side = max(image.width, image.height)
        guard side > maxSide else { return image }
        let ratio = Double(maxSide) / Double(side)
        return redraw(image, width: max(1, Int(Double(image.width) * ratio)), height: max(1, Int(Double(image.height) * ratio)))
    }

    static func encode(_ image: CGImage, to url: URL, ext: String, quality: Double? = nil,
                       metadata: [CFString: Any]? = nil) async throws {
        if ext == "pdf" { try writePDF([image], to: url); return }
        var output = image
        if !supportsAlpha(ext), hasAlpha(image) {
            output = redraw(image, width: image.width, height: image.height, opaque: true) ?? image
        }
        if ext == "ico" { try writeICO(output, to: url); return }
        if let type = uti(for: ext), writableTypes.contains(type) {
            guard let dest = CGImageDestinationCreateWithURL(url as CFURL, type as CFString, 1, nil) else {
                throw PickError(L("err.write_image"))
            }
            var props = metadata ?? [:]
            props[kCGImagePropertyOrientation] = 1
            if let quality { props[kCGImageDestinationLossyCompressionQuality] = quality }
            CGImageDestinationAddImage(dest, output, props as CFDictionary)
            guard CGImageDestinationFinalize(dest) else { throw PickError(L("err.write_image")) }
            return
        }
        try await encodeExternally(output, to: url, ext: ext, quality: quality)
    }

    /// A Windows icon holding the picture's own size (up to 256) and the smaller sizes Explorer asks for, each a PNG
    /// inside the .ico (Vista and later read those). ImageIO's ICO writer takes a single picture and refuses sizes such
    /// as 192 px, so a 75 % resize of an icon failed.
    static func writeICO(_ image: CGImage, to url: URL) throws {
        let side = max(image.width, image.height)
        var square = image
        if image.width != image.height {
            square = redraw(image, width: side, height: side,
                            drawRect: CGRect(x: (side - image.width) / 2, y: (side - image.height) / 2,
                                             width: image.width, height: image.height)) ?? image
        }
        let largest = min(max(side, 16), 256)
        let sizes = [largest] + [128, 64, 48, 32, 24, 16].filter { $0 < largest }
        var entries: [(size: Int, png: Data)] = []
        for size in sizes {
            guard let picture = redraw(square, width: size, height: size) else { continue }
            let data = NSMutableData()
            guard let dest = CGImageDestinationCreateWithData(data, UTType.png.identifier as CFString, 1, nil) else { continue }
            CGImageDestinationAddImage(dest, picture, nil)
            guard CGImageDestinationFinalize(dest) else { continue }
            entries.append((size, data as Data))
        }
        guard !entries.isEmpty else { throw PickError(L("err.write_image")) }
        var out = Data()
        func u16(_ value: Int) { withUnsafeBytes(of: UInt16(value).littleEndian) { out.append(contentsOf: $0) } }
        func u32(_ value: Int) { withUnsafeBytes(of: UInt32(value).littleEndian) { out.append(contentsOf: $0) } }
        u16(0)
        u16(1)
        u16(entries.count)
        var offset = 6 + 16 * entries.count
        for entry in entries {
            let dimension = UInt8(entry.size >= 256 ? 0 : entry.size)
            out.append(contentsOf: [dimension, dimension, 0, 0])
            u16(1)
            u16(32)
            u32(entry.png.count)
            u32(offset)
            offset += entry.png.count
        }
        for entry in entries { out.append(entry.png) }
        do {
            try out.write(to: url)
        } catch {
            throw PickError(L("err.write_image"))
        }
    }

    /// WebP (and anything ImageIO can't write) goes through cwebp or ImageMagick.
    static func encodeExternally(_ image: CGImage, to url: URL, ext: String, quality: Double?) async throws {
        let staging = url.deletingLastPathComponent().appendingPathComponent(".pv-\(UUID().uuidString).png")
        try await encode(image, to: staging, ext: "png")
        defer { try? FileManager.default.removeItem(at: staging) }
        let q = Int(((quality ?? 0.86) * 100).rounded())
        if ext == "webp", let cwebp = Tools.cwebp {
            try await Shell.run(cwebp, ["-quiet", "-q", "\(q)", "-alpha_q", "100", staging.path, "-o", url.path])
            return
        }
        if let magick = Tools.magick {
            try await Shell.run(magick, [staging.path, "-quality", "\(q)", url.path])
            return
        }
        throw PickError(L("err.encoder_missing", Catalog.formatLabel(ext)))
    }

    static func defaultQuality(_ ext: String) -> Double? {
        switch ext {
        case "jpg": return 0.9
        case "heic": return 0.82
        case "avif": return 0.78
        case "webp": return 0.86
        default: return nil
        }
    }

    static let rawExts: Set<String> = ["dng", "cr2", "cr3", "nef", "arw", "raf", "orf", "rw2"]

    /// Same format if we can write it, otherwise PNG (JPG for camera RAW).
    static func outputExt(for source: URL) -> String {
        let ext = FileNames.normalizedExt(source)
        if rawExts.contains(ext) { return "jpg" }
        if ["svg", "pdf", "icns", "psd", "gif", "exr", "tga", ""].contains(ext) { return "png" }
        return canEncode(ext) ? ext : "png"
    }

    // MARK: Conversions & tools

    static func convert(_ src: URL, to ext: String, ctx: PickContext) async throws -> URL {
        let loaded = try load(src, maxPixel: ext == "ico" ? 256 : nil)
        return try await ctx.produce(ctx.destination(for: src, ext: ext)) { tmp in
            try await encode(loaded.image, to: tmp, ext: ext, quality: defaultQuality(ext), metadata: metadata(loaded.properties))
        }
    }

    static func compress(_ src: URL, quality: Double, ctx: PickContext) async throws -> URL {
        let ext = FileNames.normalizedExt(src)
        let suffix = L("sfx.compressed")
        let out: URL
        if ["jpg", "heic", "avif", "webp"].contains(ext) {
            let loaded = try load(src)
            out = try await ctx.produce(ctx.destination(for: src, ext: ext, suffix: suffix)) { tmp in
                try await encode(loaded.image, to: tmp, ext: ext, quality: quality, metadata: metadata(loaded.properties))
            }
        } else if ext == "png", let magick = Tools.magick {
            var args = [src.path, "-strip", "-define", "png:compression-level=9"]
            if quality < 0.8 { args += ["-dither", "FloydSteinberg", "-colors", quality < 0.6 ? "96" : "256"] }
            out = try await ctx.produce(ctx.destination(for: src, ext: "png", suffix: suffix)) { tmp in
                try await Shell.run(magick, args + [tmp.path])
            }
        } else {
            let loaded = try load(src)
            let target = hasAlpha(loaded.image) ? "heic" : "jpg"
            out = try await ctx.produce(ctx.destination(for: src, ext: target, suffix: suffix)) { tmp in
                try await encode(loaded.image, to: tmp, ext: target, quality: quality, metadata: metadata(loaded.properties))
            }
        }
        return try keepIfSmaller(out, than: src)
    }

    static func keepIfSmaller(_ output: URL, than source: URL) throws -> URL {
        if FileNames.size(of: output) >= FileNames.size(of: source) {
            try? FileManager.default.removeItem(at: output)
            throw PickError(L("err.already_small"))
        }
        return output
    }

    static func resize(_ src: URL, scale: Double? = nil, maxSide: Int? = nil, ctx: PickContext) async throws -> URL {
        let size = pixelSize(src) ?? CGSize(width: 4096, height: 4096)
        let side = Int(max(size.width, size.height))
        let target = scale.map { max(1, Int(Double(side) * $0)) } ?? (maxSide ?? side)
        guard target < side else { throw PickError(L("err.already_px", "\(target)")) }
        let loaded = try load(src, maxPixel: target)
        let ext = outputExt(for: src)
        let suffix = scale.map { "\(Int($0 * 100))%" } ?? "\(target)px"
        return try await ctx.produce(ctx.destination(for: src, ext: ext, suffix: suffix)) { tmp in
            try await encode(loaded.image, to: tmp, ext: ext, quality: defaultQuality(ext), metadata: metadata(loaded.properties))
        }
    }

    static func transform(_ src: URL, turn: Turn, ctx: PickContext) async throws -> URL {
        let loaded = try load(src)
        let image = loaded.image
        let w = image.width, h = image.height
        let quarter = turn == .right || turn == .left
        let result = redraw(image, width: quarter ? h : w, height: quarter ? w : h, transform: { c in
            switch turn {
            case .right:
                c.translateBy(x: 0, y: CGFloat(w))
                c.rotate(by: -.pi / 2)
            case .left:
                c.translateBy(x: CGFloat(h), y: 0)
                c.rotate(by: .pi / 2)
            case .half:
                c.translateBy(x: CGFloat(w), y: CGFloat(h))
                c.rotate(by: .pi)
            case .flipH:
                c.translateBy(x: CGFloat(w), y: 0)
                c.scaleBy(x: -1, y: 1)
            case .flipV:
                c.translateBy(x: 0, y: CGFloat(h))
                c.scaleBy(x: 1, y: -1)
            }
        }, drawRect: CGRect(x: 0, y: 0, width: w, height: h))
        guard let result else { throw PickError(L("err.cant_rotate")) }
        let ext = outputExt(for: src)
        let suffix = turn.isFlip ? L("sfx.flipped") : L("sfx.rotated")
        return try await ctx.produce(ctx.destination(for: src, ext: ext, suffix: suffix)) { tmp in
            try await encode(result, to: tmp, ext: ext, quality: defaultQuality(ext), metadata: metadata(loaded.properties))
        }
    }

    static func stripMetadata(_ src: URL, gpsOnly: Bool, ctx: PickContext) async throws -> URL {
        let loaded = try load(src)
        let ext = outputExt(for: src)
        let kept = gpsOnly ? metadata(loaded.properties, dropLocation: true) : nil
        return try await ctx.produce(ctx.destination(for: src, ext: ext, suffix: L("sfx.stripped"))) { tmp in
            try await encode(loaded.image, to: tmp, ext: ext, quality: defaultQuality(ext), metadata: kept)
        }
    }

    static func removeBackground(_ src: URL, style: CutoutStyle, ctx: PickContext) async throws -> URL {
        let loaded = try load(src, maxPixel: 4096)
        ctx.report(0.2, L("note.finding_subject"))
        let request = VNGenerateForegroundInstanceMaskRequest()
        let handler = VNImageRequestHandler(cgImage: loaded.image, options: [:])
        try handler.perform([request])
        guard let result = request.results?.first, !result.allInstances.isEmpty else {
            throw PickError(L("err.no_subject"))
        }
        ctx.report(0.7, L("note.removing_bg"))
        let buffer = try result.generateMaskedImage(ofInstances: result.allInstances, from: handler,
                                                    croppedToInstancesExtent: style == .crop)
        let ci = CIImage(cvPixelBuffer: buffer)
        guard var cutout = CIContext().createCGImage(ci, from: ci.extent, format: .RGBA8,
                                                     colorSpace: CGColorSpace(name: CGColorSpace.sRGB)) else {
            throw PickError(L("err.write_image"))
        }
        var ext = "png"
        if style == .white {
            cutout = redraw(cutout, width: cutout.width, height: cutout.height, opaque: true) ?? cutout
            ext = "jpg"
        }
        let suffix = style == .crop ? L("sfx.subject") : L("sfx.no_bg")
        let image = cutout
        return try await ctx.produce(ctx.destination(for: src, ext: ext, suffix: suffix)) { tmp in
            try await encode(image, to: tmp, ext: ext, quality: 0.92)
        }
    }

    static func recognizeText(in image: CGImage) throws -> String {
        let request = VNRecognizeTextRequest()
        request.recognitionLevel = .accurate
        request.usesLanguageCorrection = true
        request.automaticallyDetectsLanguage = true
        let supported = Set((try? request.supportedRecognitionLanguages()) ?? [])
        let preferred = ["ko-KR", "en-US", "ja-JP", "zh-Hans", "zh-Hant", "es-ES", "fr-FR", "de-DE", "ru-RU", "pt-BR"]
            .filter { supported.contains($0) }
        if !preferred.isEmpty { request.recognitionLanguages = preferred }
        try VNImageRequestHandler(cgImage: image, options: [:]).perform([request])
        return (request.results ?? []).compactMap { $0.topCandidates(1).first?.string }.joined(separator: "\n")
    }

    static func recognizeText(_ ctx: PickContext) async throws -> PickResult {
        var chunks: [String] = []
        for (index, file) in ctx.files.enumerated() {
            try Task.checkCancellation()
            ctx.report(Double(index) / Double(ctx.files.count), file.lastPathComponent)
            let text = try recognizeText(in: load(file, maxPixel: 4096).image)
            if !text.isEmpty { chunks.append(text) }
        }
        let text = chunks.joined(separator: "\n\n")
        guard !text.isEmpty else { throw PickError(L("err.no_text")) }
        await MainActor.run {
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(text, forType: .string)
        }
        return .note(LP("msg.lines_copied", text.split(separator: "\n").count))
    }

    static func combineToPDF(_ ctx: PickContext) async throws -> PickResult {
        let files = ctx.files.sorted { $0.lastPathComponent.localizedStandardCompare($1.lastPathComponent) == .orderedAscending }
        var images: [CGImage] = []
        for (index, file) in files.enumerated() {
            try Task.checkCancellation()
            ctx.report(Double(index) / Double(files.count) * 0.9, file.lastPathComponent)
            images.append(try load(file, maxPixel: 3200).image)
        }
        let dst = ctx.destination(for: files[0], ext: "pdf", suffix: LP("sfx.and_more", files.count - 1))
        let out = try await ctx.produce(dst) { tmp in try writePDF(images, to: tmp) }
        return PickResult(outputs: [out])
    }

    static func writePDF(_ images: [CGImage], to url: URL) throws {
        guard let pdf = CGContext(url as CFURL, mediaBox: nil, nil) else { throw PickError(L("err.pdf_create")) }
        for image in images {
            let scale = min(1, 1440 / CGFloat(max(image.width, image.height)))
            var box = CGRect(x: 0, y: 0, width: CGFloat(image.width) * scale, height: CGFloat(image.height) * scale)
            pdf.beginPage(mediaBox: &box)
            pdf.interpolationQuality = .high
            pdf.draw(image, in: box)
            pdf.endPage()
        }
        pdf.closePDF()
    }
}
