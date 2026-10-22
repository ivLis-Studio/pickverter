import AppKit
import ImageIO
import PDFKit

struct MetadataEdit: Codable, Equatable {
    /// Only the fields the user changed.
    var fields: [String: String]
    var removeLocation: Bool
    /// Swap the edited file in for the original (the original goes to the Trash).
    var replaceOriginal: Bool
}

struct MetaField: Identifiable, Equatable {
    let key: String
    var value: String
    var id: String { key }
}

struct MetaInfoRow: Identifiable {
    let id = UUID()
    let label: String
    let value: String
}

struct MetaSnapshot {
    enum Family { case image, pdf, media }
    let family: Family
    var fields: [MetaField]
    var info: [MetaInfoRow]
    var hasLocation = false
    var writable = true
    var reason: String?
}

enum MetadataKit {
    static func family(of url: URL) -> MetaSnapshot.Family? {
        switch FileKind.of(url) {
        case .image: return .image
        case .pdf: return .pdf
        case .video, .audio: return .media
        default: return nil
        }
    }

    static func keys(for family: MetaSnapshot.Family) -> [String] {
        switch family {
        case .image: return ["title", "author", "copyright", "date", "keywords"]
        case .pdf: return ["title", "author", "subject", "keywords", "creator"]
        case .media: return MediaEngine.tagKeys
        }
    }

    static func label(_ key: String, family: MetaSnapshot.Family) -> String {
        family == .media && key == "date" ? L("meta.year") : L("meta." + key)
    }

    static func placeholder(_ key: String) -> String {
        switch key {
        case "date": return "2026:10:22 14:30:00"
        case "keywords": return L("meta.keywords.hint")
        default: return ""
        }
    }

    // MARK: Reading

    static func read(_ url: URL) async throws -> MetaSnapshot {
        guard let family = family(of: url) else { throw PickError(L("err.meta_unsupported")) }
        var values: [String: String] = [:]
        var info: [MetaInfoRow] = []
        var snapshot = MetaSnapshot(family: family, fields: [], info: [])
        switch family {
        case .image:
            guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
                  let props = CGImageSourceCopyPropertiesAtIndex(source, CGImageSourceGetPrimaryImageIndex(source), nil) as? [CFString: Any]
            else { throw PickError(L("err.read_image")) }
            let tiff = props[kCGImagePropertyTIFFDictionary] as? [CFString: Any] ?? [:]
            let exif = props[kCGImagePropertyExifDictionary] as? [CFString: Any] ?? [:]
            let iptc = props[kCGImagePropertyIPTCDictionary] as? [CFString: Any] ?? [:]
            values["title"] = (tiff[kCGImagePropertyTIFFImageDescription] ?? iptc[kCGImagePropertyIPTCCaptionAbstract]) as? String
            values["author"] = (tiff[kCGImagePropertyTIFFArtist] as? String) ?? (iptc[kCGImagePropertyIPTCByline] as? [String])?.first
            values["copyright"] = (tiff[kCGImagePropertyTIFFCopyright] ?? iptc[kCGImagePropertyIPTCCopyrightNotice]) as? String
            values["date"] = (exif[kCGImagePropertyExifDateTimeOriginal] ?? tiff[kCGImagePropertyTIFFDateTime]) as? String
            values["keywords"] = (iptc[kCGImagePropertyIPTCKeywords] as? [String])?.joined(separator: ", ")
            snapshot.hasLocation = props[kCGImagePropertyGPSDictionary] != nil
            if let size = ImageEngine.orientedPixelSize(url) {
                info.append(MetaInfoRow(label: L("meta.size"), value: "\(Int(size.width)) × \(Int(size.height))"))
            }
            let camera = [tiff[kCGImagePropertyTIFFMake] as? String, tiff[kCGImagePropertyTIFFModel] as? String]
                .compactMap { $0?.trimmingCharacters(in: .whitespaces) }.joined(separator: " ")
            if !camera.isEmpty { info.append(MetaInfoRow(label: L("meta.camera"), value: camera)) }
            if let lens = exif[kCGImagePropertyExifLensModel] as? String { info.append(MetaInfoRow(label: L("meta.lens"), value: lens)) }
            if let gps = props[kCGImagePropertyGPSDictionary] as? [CFString: Any],
               let lat = gps[kCGImagePropertyGPSLatitude] as? Double, let lon = gps[kCGImagePropertyGPSLongitude] as? Double {
                let ns = (gps[kCGImagePropertyGPSLatitudeRef] as? String) ?? "N"
                let ew = (gps[kCGImagePropertyGPSLongitudeRef] as? String) ?? "E"
                info.append(MetaInfoRow(label: L("meta.location"), value: String(format: "%.5f° %@, %.5f° %@", lat, ns, lon, ew)))
            }
            let type = CGImageSourceGetType(source) as String? ?? ""
            if !((CGImageDestinationCopyTypeIdentifiers() as? [String]) ?? []).contains(type) {
                snapshot.writable = Tools.magick != nil || ImageEngine.canEncode(ImageEngine.outputExt(for: url))
            }
        case .pdf:
            values = try PDFEngine.info(url)
            if let doc = try? PDFEngine.open(url) {
                info.append(MetaInfoRow(label: L("meta.pages"), value: "\(doc.pageCount)"))
                if let producer = doc.documentAttributes?[PDFDocumentAttribute.producerAttribute] as? String {
                    info.append(MetaInfoRow(label: L("meta.producer"), value: producer))
                }
            }
        case .media:
            values = await MediaEngine.readTags(url)
            let probe = await MediaEngine.probe(url)
            if probe.duration > 0 { info.append(MetaInfoRow(label: L("meta.duration"), value: TimeText.format(probe.duration, precise: false))) }
            if let size = probe.displaySize { info.append(MetaInfoRow(label: L("meta.size"), value: "\(Int(size.width)) × \(Int(size.height))")) }
            let codecs = [probe.videoCodec, probe.audioCodec].compactMap { $0 }.filter { $0 != "native" }
            if !codecs.isEmpty { info.append(MetaInfoRow(label: L("meta.codecs"), value: codecs.joined(separator: " · "))) }
            if Tools.ffmpeg == nil {
                snapshot.writable = false
                snapshot.reason = Tools.ffmpegHint
            }
        }
        info.append(MetaInfoRow(label: L("meta.file_size"), value: FileNames.formatBytes(FileNames.size(of: url))))
        snapshot.fields = keys(for: family).map { MetaField(key: $0, value: values[$0] ?? "") }
        snapshot.info = info
        return snapshot
    }

    // MARK: Writing

    /// "2026-10-22 14:30" / "2026.10.22" -> EXIF's "2026:10:22 14:30:00".
    static func exifDate(_ raw: String) -> String {
        let trimmed = raw.trimmingCharacters(in: .whitespaces)
        guard !trimmed.isEmpty else { return "" }
        let parts = trimmed.split(separator: " ", maxSplits: 1).map(String.init)
        let date = parts[0].replacingOccurrences(of: "-", with: ":").replacingOccurrences(of: ".", with: ":")
            .replacingOccurrences(of: "/", with: ":")
        var time = parts.count > 1 ? parts[1] : "00:00:00"
        if time.split(separator: ":").count == 2 { time += ":00" }
        return date + " " + time
    }

    static func writeImage(_ src: URL, edit: MetadataEdit, to dst: URL) async throws {
        guard let source = CGImageSourceCreateWithURL(src as CFURL, nil), let type = CGImageSourceGetType(source) else {
            throw PickError(L("err.read_image"))
        }
        let fields = edit.fields
        let keywords = fields["keywords"].map { $0.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty } }
        let date = fields["date"].map(exifDate)
        let meta = CGImageMetadataCreateMutable()
        func set(_ dictionary: CFString, _ key: CFString, _ value: CFTypeRef?) {
            guard let value else { return }
            CGImageMetadataSetValueMatchingImageProperty(meta, dictionary, key, value)
        }
        set(kCGImagePropertyTIFFDictionary, kCGImagePropertyTIFFImageDescription, fields["title"] as CFString?)
        set(kCGImagePropertyIPTCDictionary, kCGImagePropertyIPTCCaptionAbstract, fields["title"] as CFString?)
        set(kCGImagePropertyTIFFDictionary, kCGImagePropertyTIFFArtist, fields["author"] as CFString?)
        set(kCGImagePropertyIPTCDictionary, kCGImagePropertyIPTCByline, fields["author"].map { [$0] as CFArray })
        set(kCGImagePropertyTIFFDictionary, kCGImagePropertyTIFFCopyright, fields["copyright"] as CFString?)
        set(kCGImagePropertyIPTCDictionary, kCGImagePropertyIPTCCopyrightNotice, fields["copyright"] as CFString?)
        set(kCGImagePropertyExifDictionary, kCGImagePropertyExifDateTimeOriginal, date as CFString?)
        set(kCGImagePropertyIPTCDictionary, kCGImagePropertyIPTCKeywords, keywords.map { $0 as CFArray })

        // Lossless path: copy the compressed image data, only the metadata changes.
        if let dest = CGImageDestinationCreateWithURL(dst as CFURL, type, 1, nil) {
            var options: [CFString: Any] = [kCGImageDestinationMetadata: meta, kCGImageDestinationMergeMetadata: true]
            if edit.removeLocation { options[kCGImageMetadataShouldExcludeGPS] = true }
            if CGImageDestinationCopyImageSource(dest, source, options as CFDictionary, nil) { return }
        }
        try? FileManager.default.removeItem(at: dst)
        // Formats ImageIO can't rewrite in place: re-encode once at high quality.
        let loaded = try ImageEngine.load(src)
        var props = ImageEngine.metadata(loaded.properties, dropLocation: edit.removeLocation)
        var tiff = props[kCGImagePropertyTIFFDictionary] as? [CFString: Any] ?? [:]
        var exif = props[kCGImagePropertyExifDictionary] as? [CFString: Any] ?? [:]
        var iptc = props[kCGImagePropertyIPTCDictionary] as? [CFString: Any] ?? [:]
        if let title = fields["title"] { tiff[kCGImagePropertyTIFFImageDescription] = title; iptc[kCGImagePropertyIPTCCaptionAbstract] = title }
        if let author = fields["author"] { tiff[kCGImagePropertyTIFFArtist] = author; iptc[kCGImagePropertyIPTCByline] = [author] }
        if let copyright = fields["copyright"] { tiff[kCGImagePropertyTIFFCopyright] = copyright; iptc[kCGImagePropertyIPTCCopyrightNotice] = copyright }
        if let date { exif[kCGImagePropertyExifDateTimeOriginal] = date }
        if let keywords { iptc[kCGImagePropertyIPTCKeywords] = keywords }
        props[kCGImagePropertyTIFFDictionary] = tiff
        props[kCGImagePropertyExifDictionary] = exif
        props[kCGImagePropertyIPTCDictionary] = iptc
        let ext = FileNames.normalizedExt(dst)
        try await ImageEngine.encode(loaded.image, to: dst, ext: ext, quality: 0.95, metadata: props)
    }

    static func apply(_ src: URL, edit: MetadataEdit, ctx: PickContext) async throws -> PickResult {
        guard let family = family(of: src) else { throw PickError(L("err.meta_unsupported")) }
        let (base, sourceExt) = FileNames.split(src.lastPathComponent)
        var ext = sourceExt
        if family == .image, !ImageEngine.canEncode(ext),
           !(CGImageDestinationCopyTypeIdentifiers() as? [String] ?? []).contains(ImageEngine.uti(for: ext) ?? "") {
            ext = ImageEngine.outputExt(for: src)
        }
        let suffix = L("sfx.edited")
        let dst = edit.replaceOriginal
            ? src.deletingLastPathComponent().appendingPathComponent("\(base) \(suffix).\(ext)")
            : ctx.destination(for: src, ext: ext, suffix: suffix)
        let out = try await ctx.produce(dst) { tmp in
            switch family {
            case .image: try await writeImage(src, edit: edit, to: tmp)
            case .pdf: try PDFEngine.writeInfo(src, fields: edit.fields, to: tmp)
            case .media: try await MediaEngine.writeTags(src, tags: edit.fields, to: tmp, ctx: ctx)
            }
        }
        guard edit.replaceOriginal, ext == sourceExt else { return PickResult(outputs: [out]) }
        try FileManager.default.trashItem(at: src, resultingItemURL: nil)
        try FileManager.default.moveItem(at: out, to: src)
        return PickResult(outputs: [src], message: L("meta.replaced"), undoable: false)
    }
}
