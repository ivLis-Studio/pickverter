import Foundation
import UniformTypeIdentifiers

enum FileKind: String, CaseIterable {
    case image, video, audio, pdf, document, office, table, json, subtitle, archive, folder, other

    static let imageExts: Set<String> = ["jpg", "jpeg", "png", "heic", "heif", "webp", "avif", "tif", "tiff", "gif", "bmp",
                                          "ico", "icns", "svg", "jp2", "psd", "tga", "dng", "cr2", "cr3", "nef", "arw",
                                          "raf", "orf", "rw2", "exr"]
    static let videoExts: Set<String> = ["mp4", "m4v", "mov", "mkv", "webm", "avi", "wmv", "flv", "mpg", "mpeg", "3gp",
                                          "ts", "mts", "m2ts", "ogv"]
    static let audioExts: Set<String> = ["mp3", "m4a", "aac", "wav", "aif", "aiff", "aifc", "flac", "ogg", "oga", "opus",
                                          "wma", "caf", "amr", "m4b"]
    static let documentExts: Set<String> = ["docx", "doc", "rtf", "rtfd", "txt", "text", "md", "markdown", "html", "htm",
                                             "odt", "webarchive", "wordml"]
    static let tableExts: Set<String> = ["csv", "tsv"]
    static let subtitleExts: Set<String> = ["srt", "vtt"]
    static let archiveExts: Set<String> = ["zip", "tar", "gz", "tgz", "bz2", "tbz", "tbz2", "xz", "txz", "rar", "7z"]

    static func of(_ url: URL) -> FileKind {
        let ext = url.pathExtension.lowercased()
        // iWork documents can be package folders, so check them before the folder test.
        if OfficeEngine.exts.contains(ext) { return .office }
        var isDir: ObjCBool = false
        if FileManager.default.fileExists(atPath: url.path, isDirectory: &isDir), isDir.boolValue {
            return ext == "rtfd" ? .document : .folder
        }
        if ext == "pdf" { return .pdf }
        if ext == "json" { return .json }
        if imageExts.contains(ext) { return .image }
        if videoExts.contains(ext) { return .video }
        if audioExts.contains(ext) { return .audio }
        if documentExts.contains(ext) { return .document }
        if tableExts.contains(ext) { return .table }
        if subtitleExts.contains(ext) { return .subtitle }
        if archiveExts.contains(ext) { return .archive }
        if let type = UTType(filenameExtension: ext) {
            if type.conforms(to: .image) { return .image }
            if type.conforms(to: .movie) { return .video }
            if type.conforms(to: .audio) { return .audio }
        }
        return .other
    }
}

enum FileNames {
    static let compoundExtensions = ["tar.gz", "tar.bz2", "tar.xz"]

    /// Splits a file name into base and lowercased extension, aware of ".tar.gz" style names.
    static func split(_ name: String) -> (base: String, ext: String) {
        let lower = name.lowercased()
        for compound in compoundExtensions where lower.hasSuffix("." + compound) && name.count > compound.count + 1 {
            return (String(name.dropLast(compound.count + 1)), compound)
        }
        let ns = name as NSString
        let ext = ns.pathExtension
        let base = ns.deletingPathExtension
        if ext.isEmpty || base.isEmpty { return (name, "") }
        return (base, ext.lowercased())
    }

    static func normalizedExt(_ url: URL) -> String {
        let ext = split(url.lastPathComponent).ext
        switch ext {
        case "jpeg": return "jpg"
        case "tif": return "tiff"
        case "heif": return "heic"
        case "htm": return "html"
        case "aif", "aifc": return "aiff"
        case "markdown": return "md"
        case "text": return "txt"
        case "tgz": return "tar.gz"
        case "oga": return "ogg"
        default: return ext
        }
    }

    /// Finder-style unique name: "photo.png" -> "photo 2.png" -> "photo 3.png".
    static func unique(_ url: URL) -> URL {
        let fm = FileManager.default
        guard fm.fileExists(atPath: url.path) else { return url }
        let dir = url.deletingLastPathComponent()
        let (base, ext) = split(url.lastPathComponent)
        var n = 2
        while true {
            let name = ext.isEmpty ? "\(base) \(n)" : "\(base) \(n).\(ext)"
            let candidate = dir.appendingPathComponent(name)
            if !fm.fileExists(atPath: candidate.path) { return candidate }
            n += 1
        }
    }

    static func size(of url: URL) -> Int64 {
        let fm = FileManager.default
        var isDir: ObjCBool = false
        guard fm.fileExists(atPath: url.path, isDirectory: &isDir) else { return 0 }
        if !isDir.boolValue {
            return (try? fm.attributesOfItem(atPath: url.path)[.size] as? Int64) ?? 0
        }
        var total: Int64 = 0
        if let walker = fm.enumerator(at: url, includingPropertiesForKeys: [.fileSizeKey]) {
            for case let item as URL in walker {
                total += Int64((try? item.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0)
            }
        }
        return total
    }

    static func formatBytes(_ bytes: Int64) -> String {
        ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file)
    }
}
