import AppKit
import CoreImage
import ImageIO
import Vision

enum CollageLayout: String, CaseIterable {
    case grid, row, column
    var title: String { L("collage." + rawValue) }
    var symbol: String {
        switch self {
        case .grid: return "square.grid.2x2"
        case .row: return "rectangle.split.3x1"
        case .column: return "rectangle.split.1x2"
        }
    }
}

extension ImageEngine {
    /// Pixel size with EXIF orientation applied (what the user actually sees).
    static func orientedPixelSize(_ url: URL) -> CGSize? {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
              let props = CGImageSourceCopyPropertiesAtIndex(source, CGImageSourceGetPrimaryImageIndex(source), nil) as? [CFString: Any],
              let w = props[kCGImagePropertyPixelWidth] as? Int, let h = props[kCGImagePropertyPixelHeight] as? Int
        else { return nil }
        let orientation = props[kCGImagePropertyOrientation] as? Int ?? 1
        return (5...8).contains(orientation) ? CGSize(width: h, height: w) : CGSize(width: w, height: h)
    }

    // MARK: Crop

    static func crop(_ src: URL, rect: NormRect, ctx: PickContext) async throws -> URL {
        let loaded = try load(src)
        let size = CGSize(width: loaded.image.width, height: loaded.image.height)
        let box = rect.clipped().pixels(in: size)
        guard box.width >= 1, box.height >= 1, let cropped = loaded.image.cropping(to: box) else {
            throw PickError(L("err.crop_empty"))
        }
        let ext = outputExt(for: src)
        return try await ctx.produce(ctx.destination(for: src, ext: ext, suffix: L("sfx.cropped"))) { tmp in
            try await encode(cropped, to: tmp, ext: ext, quality: defaultQuality(ext), metadata: metadata(loaded.properties))
        }
    }

    /// Largest centered rect with the given pixel aspect ratio.
    static func centeredRect(ratio: Double, in size: CGSize) -> NormRect {
        let normalized = ratio * Double(size.height / max(size.width, 1))
        var w = 1.0, h = 1.0 / normalized
        if h > 1 { h = 1; w = normalized }
        return NormRect(x: (1 - w) / 2, y: (1 - h) / 2, w: w, h: h)
    }

    static func centerCrop(_ src: URL, ratio: Double, ctx: PickContext) async throws -> URL {
        let size = orientedPixelSize(src) ?? CGSize(width: 1, height: 1)
        let rect = centeredRect(ratio: ratio, in: size)
        if rect.isFull { throw PickError(L("err.already_ratio")) }
        return try await crop(src, rect: rect, ctx: ctx)
    }

    // MARK: Redaction

    enum Detection: Sendable { case faces, text }

    /// Faces get generous margins (hair, ears); text boxes a small one.
    static func detect(_ kind: Detection, in image: CGImage) -> [NormRect] {
        let handler = VNImageRequestHandler(cgImage: image, options: [:])
        switch kind {
        case .faces:
            let request = VNDetectFaceRectanglesRequest()
            try? handler.perform([request])
            return (request.results ?? []).map { NormRect.vision($0.boundingBox).expanded(by: 0.18) }
        case .text:
            let request = VNRecognizeTextRequest()
            request.recognitionLevel = .fast
            request.usesLanguageCorrection = false
            try? handler.perform([request])
            return (request.results ?? []).map { NormRect.vision($0.boundingBox).expanded(by: 0.08) }
        }
    }

    /// Draws the effect into each region. Block and blur sizes scale with the region so small and large
    /// areas look equally hidden, and previews match the full-size result.
    static func applyRedaction(_ image: CGImage, regions: [NormRect], style: RedactStyle) -> CGImage? {
        guard !regions.isEmpty else { return image }
        let width = image.width, height = image.height
        let size = CGSize(width: width, height: height)
        guard let ctx = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: rgbSpace(image), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        ctx.draw(image, in: CGRect(origin: .zero, size: size))
        let ci = CIContext(options: [.cacheIntermediates: false])
        for region in regions {
            let box = region.clipped().pixels(in: size)
            guard box.width >= 2, box.height >= 2 else { continue }
            let target = CGRect(x: box.minX, y: CGFloat(height) - box.maxY, width: box.width, height: box.height)
            switch style {
            case .fill:
                ctx.setFillColor(CGColor(gray: 0.08, alpha: 1))
                ctx.fill(target)
            case .pixelate:
                guard let piece = image.cropping(to: box) else { continue }
                let block = max(6, Int(min(box.width, box.height) / 9))
                let cols = max(1, Int(box.width) / block), rows = max(1, Int(box.height) / block)
                guard let small = redraw(piece, width: cols, height: rows) else { continue }
                ctx.saveGState()
                ctx.interpolationQuality = .none
                ctx.draw(small, in: target)
                ctx.restoreGState()
            case .blur:
                guard let piece = image.cropping(to: box) else { continue }
                let sigma = max(6, Double(min(box.width, box.height)) / 7)
                let input = CIImage(cgImage: piece)
                let blurred = input.clampedToExtent().applyingGaussianBlur(sigma: sigma)
                    .applyingGaussianBlur(sigma: sigma * 0.6)
                    .cropped(to: input.extent)
                guard let rendered = ci.createCGImage(blurred, from: input.extent) else { continue }
                ctx.draw(rendered, in: target)
            }
        }
        return ctx.makeImage()
    }

    static func redact(_ src: URL, regions: [NormRect], style: RedactStyle, ctx: PickContext) async throws -> URL {
        let loaded = try load(src)
        guard let result = applyRedaction(loaded.image, regions: regions, style: style) else { throw PickError(L("err.write_image")) }
        let ext = outputExt(for: src)
        return try await ctx.produce(ctx.destination(for: src, ext: ext, suffix: L("sfx.redacted"))) { tmp in
            try await encode(result, to: tmp, ext: ext, quality: defaultQuality(ext), metadata: metadata(loaded.properties, dropLocation: true))
        }
    }

    static func autoRedact(_ src: URL, kind: Detection, style: RedactStyle, ctx: PickContext) async throws -> URL {
        ctx.report(0.15, L(kind == .faces ? "note.finding_faces" : "note.finding_text"))
        let probe = try load(src, maxPixel: 2400).image
        let regions = detect(kind, in: probe)
        guard !regions.isEmpty else { throw PickError(L(kind == .faces ? "redact.no_faces" : "redact.no_text")) }
        ctx.report(0.5)
        return try await redact(src, regions: regions, style: style, ctx: ctx)
    }

    // MARK: Collage

    static func collageImage(_ images: [CGImage], layout: CollageLayout) -> CGImage? {
        guard !images.isEmpty else { return nil }
        let aspects = images.map { Double($0.width) / Double(max($0.height, 1)) }
        let opaque = !images.contains(where: hasAlpha)
        var placements: [CGRect] = []
        var canvas = CGSize.zero
        switch layout {
        case .grid:
            // Justified rows: every row shares one height, nothing is cropped.
            let pad = 16.0, gap = 12.0, width = 2400.0
            let inner = width - pad * 2
            let columns = max(1, Int(Double(images.count).squareRoot().rounded(.up)))
            let median = aspects.sorted()[aspects.count / 2]
            let targetHeight = (inner - gap * Double(columns - 1)) / Double(columns) / median
            var rows: [(range: Range<Int>, height: Double)] = []
            var start = 0
            var sum = 0.0
            for index in images.indices {
                sum += aspects[index]
                let count = index - start + 1
                let height = (inner - gap * Double(count - 1)) / sum
                if height <= targetHeight || index == images.count - 1 {
                    rows.append((start..<(index + 1), min(height, targetHeight)))
                    start = index + 1
                    sum = 0
                }
            }
            var y = pad
            for row in rows {
                let rowWidth = row.range.reduce(0.0) { $0 + aspects[$1] * row.height } + gap * Double(row.range.count - 1)
                var x = pad + max(0, (inner - rowWidth) / 2)
                for index in row.range {
                    let w = aspects[index] * row.height
                    placements.append(CGRect(x: x, y: y, width: w, height: row.height))
                    x += w + gap
                }
                y += row.height + gap
            }
            canvas = CGSize(width: width, height: y - gap + pad)
        case .row:
            var height = Double(images.map(\.height).sorted()[images.count / 2])
            height = min(height, 2400)
            let total = aspects.reduce(0) { $0 + $1 * height }
            if total > 16_000 { height *= 16_000 / total }
            var x = 0.0
            for aspect in aspects {
                placements.append(CGRect(x: x, y: 0, width: aspect * height, height: height))
                x += aspect * height
            }
            canvas = CGSize(width: x, height: height)
        case .column:
            var width = Double(images.map(\.width).sorted()[images.count / 2])
            width = min(width, 2400)
            let total = aspects.reduce(0) { $0 + width / $1 }
            if total > 16_000 { width *= 16_000 / total }
            var y = 0.0
            for aspect in aspects {
                placements.append(CGRect(x: 0, y: y, width: width, height: width / aspect))
                y += width / aspect
            }
            canvas = CGSize(width: width, height: y)
        }
        let pw = max(1, Int(canvas.width.rounded())), ph = max(1, Int(canvas.height.rounded()))
        guard let ctx = CGContext(data: nil, width: pw, height: ph, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: (opaque ? CGImageAlphaInfo.noneSkipLast : .premultipliedLast).rawValue) else { return nil }
        if layout == .grid || opaque {
            ctx.setFillColor(CGColor(gray: 1, alpha: 1))
            ctx.fill(CGRect(x: 0, y: 0, width: pw, height: ph))
        }
        ctx.interpolationQuality = .high
        for (image, place) in zip(images, placements) {
            let flipped = CGRect(x: place.minX, y: CGFloat(ph) - place.maxY, width: place.width, height: place.height)
            ctx.draw(image, in: flipped.integral)
        }
        return ctx.makeImage()
    }

    static func collage(_ ctx: PickContext, layout: CollageLayout) async throws -> PickResult {
        let files = ctx.files.sorted { $0.lastPathComponent.localizedStandardCompare($1.lastPathComponent) == .orderedAscending }
        guard files.count > 1 else { throw PickError(L("err.need_two")) }
        var images: [CGImage] = []
        for (index, file) in files.enumerated() {
            try Task.checkCancellation()
            ctx.report(Double(index) / Double(files.count) * 0.8, file.lastPathComponent)
            images.append(try load(file, maxPixel: layout == .grid ? 1600 : 2400).image)
        }
        guard let result = collageImage(images, layout: layout) else { throw PickError(L("err.write_image")) }
        let ext = hasAlpha(result) ? "png" : "jpg"
        let dst = ctx.destination(for: files[0], ext: ext, suffix: L("sfx.collage"))
        let out = try await ctx.produce(dst) { tmp in try await encode(result, to: tmp, ext: ext, quality: 0.9) }
        return PickResult(outputs: [out])
    }
}
