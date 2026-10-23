import AppKit
import UniformTypeIdentifiers

/// Private cache for results that only live on the clipboard.
enum ClipboardStore {
    static var root: URL {
        let caches = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask).first
            ?? FileManager.default.temporaryDirectory
        return caches.appendingPathComponent("com.pickverter.app/Clipboard", isDirectory: true)
    }

    static func folder(for job: UUID) -> URL {
        let folder = root.appendingPathComponent(job.uuidString, isDirectory: true)
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        return folder
    }

    static func contains(_ url: URL) -> Bool {
        url.standardizedFileURL.path.hasPrefix(root.standardizedFileURL.path)
    }

    /// Drops cached results older than a day (the clipboard has long moved on by then).
    static func prune() {
        let fm = FileManager.default
        guard let entries = try? fm.contentsOfDirectory(at: root, includingPropertiesForKeys: [.contentModificationDateKey]) else { return }
        let cutoff = Date().addingTimeInterval(-86_400)
        for entry in entries {
            let modified = (try? entry.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate) ?? .distantPast
            if modified < cutoff { try? fm.removeItem(at: entry) }
        }
    }
}

@MainActor
enum ClipboardWriter {
    static let textTypes: Set<String> = ["txt", "md", "csv", "tsv", "json", "srt", "vtt", "html", "rtf"]

    /// Images go up as image data plus the file, text as text, everything else as files.
    static func write(_ urls: [URL]) {
        let board = NSPasteboard.general
        board.clearContents()
        guard urls.count == 1, let url = urls.first, FileKind.of(url) != .folder else {
            board.writeObjects(urls as [NSURL])
            return
        }
        let ext = FileNames.normalizedExt(url)
        let item = NSPasteboardItem()
        if textTypes.contains(ext), let data = try? Data(contentsOf: url) {
            // Decoding drops the byte-order mark a CSV starts with: it isn't part of the text.
            let text = TextCodec.decode(data)
            if ext == "html" { item.setString(text, forType: .html) }
            if ext == "rtf", let data = try? Data(contentsOf: url) { item.setData(data, forType: .rtf) }
            item.setString(text, forType: .string)
            board.writeObjects([item])
            return
        }
        if let type = UTType(filenameExtension: ext), let data = try? Data(contentsOf: url) {
            if type.conforms(to: .image) {
                item.setData(data, forType: NSPasteboard.PasteboardType(type.identifier))
                if let image = NSImage(data: data), let tiff = image.tiffRepresentation {
                    if type != .png, let rep = NSBitmapImageRep(data: tiff), let png = rep.representation(using: .png, properties: [:]) {
                        item.setData(png, forType: .png)
                    }
                    item.setData(tiff, forType: .tiff)
                }
            } else if type.conforms(to: .pdf) {
                item.setData(data, forType: .pdf)
            }
        }
        item.setString(url.absoluteString, forType: .fileURL)
        board.writeObjects([item])
    }
}
