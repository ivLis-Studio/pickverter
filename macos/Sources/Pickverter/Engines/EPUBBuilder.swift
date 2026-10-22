import AppKit
import NaturalLanguage

/// Minimal, valid EPUB 3 (with an EPUB 2 NCX for older readers). Everything is XHTML we generate ourselves.
enum EPUBBuilder {
    struct Chapter {
        var title: String
        /// Well-formed XHTML fragment.
        var body: String
    }

    struct Asset {
        var name: String
        var data: Data
        var mediaType: String
    }

    static let imageTypes: [String: String] = ["jpg": "image/jpeg", "jpeg": "image/jpeg", "png": "image/png", "gif": "image/gif",
                                               "svg": "image/svg+xml", "webp": "image/webp"]

    static func escape(_ text: String) -> String {
        Markdown.escape(text).replacingOccurrences(of: "\"", with: "&quot;")
    }

    /// HTML 4 habits -> XML: void elements must be closed.
    static func xhtml(_ html: String) -> String {
        var out = Markdown.replace(html, "<(hr|br)\\s*>", "<$1/>")
        out = Markdown.replace(out, "<img([^>]*?)\\s*/?>", "<img$1/>")
        return out
    }

    static func language(of text: String) -> String {
        let recognizer = NLLanguageRecognizer()
        recognizer.processString(String(text.prefix(6000)))
        return recognizer.dominantLanguage?.rawValue ?? Loc.language
    }

    /// Splits on # headings when there are several of them, otherwise on ##, otherwise one chapter.
    static func sections(_ markdown: String, fallbackTitle: String) -> [(title: String, markdown: String)] {
        let lines = markdown.replacingOccurrences(of: "\r\n", with: "\n").components(separatedBy: "\n")
        var inCode = false
        var headingLines: [Int: (level: Int, text: String)] = [:]
        for (index, line) in lines.enumerated() {
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            if trimmed.hasPrefix("\u{60}\u{60}\u{60}") { inCode.toggle(); continue }
            guard !inCode else { continue }
            for level in [1, 2] where trimmed.hasPrefix(String(repeating: "#", count: level) + " ") {
                headingLines[index] = (level, String(trimmed.dropFirst(level + 1)).trimmingCharacters(in: .whitespaces))
            }
        }
        let level = headingLines.values.filter { $0.level == 1 }.count >= 2 ? 1
            : (headingLines.values.filter { $0.level == 2 }.count >= 2 ? 2 : 0)
        guard level > 0 else {
            let title = headingLines.values.first(where: { $0.level == 1 })?.text ?? fallbackTitle
            return [(title, markdown)]
        }
        var result: [(String, String)] = []
        var current: [String] = []
        var title = fallbackTitle
        for (index, line) in lines.enumerated() {
            if let heading = headingLines[index], heading.level == level {
                if current.contains(where: { !$0.trimmingCharacters(in: .whitespaces).isEmpty }) {
                    result.append((title, current.joined(separator: "\n")))
                }
                current = []
                title = heading.text
            }
            current.append(line)
        }
        if !current.isEmpty { result.append((title, current.joined(separator: "\n"))) }
        return result
    }

    /// Copies local images next to the book and rewrites their paths; remote ones become their alt text.
    static func resolveImages(_ html: String, base: URL?, assets: inout [Asset]) -> String {
        guard let regex = try? NSRegularExpression(pattern: "<img alt=\"([^\"]*)\" src=\"([^\"]+)\">") else { return html }
        var output = html
        for match in regex.matches(in: html, range: NSRange(html.startIndex..., in: html)).reversed() {
            guard let whole = Range(match.range, in: html), let altRange = Range(match.range(at: 1), in: html),
                  let srcRange = Range(match.range(at: 2), in: html) else { continue }
            let alt = String(html[altRange])
            let src = String(html[srcRange]).replacingOccurrences(of: "&amp;", with: "&")
            var replacement = alt.isEmpty ? "" : "<span>[\(alt)]</span>"
            if let base, !src.contains("://"), let decoded = src.removingPercentEncoding {
                let file = URL(fileURLWithPath: decoded, relativeTo: base).standardizedFileURL
                let ext = file.pathExtension.lowercased()
                if let type = imageTypes[ext], let data = try? Data(contentsOf: file) {
                    let name = String(format: "img%03d.%@", assets.count + 1, ext == "jpeg" ? "jpg" : ext)
                    assets.append(Asset(name: name, data: data, mediaType: type))
                    replacement = "<img alt=\"\(alt)\" src=\"../images/\(name)\">"
                }
            }
            output.replaceSubrange(Range(match.range, in: output) ?? whole, with: replacement)
        }
        return output
    }

    static func textChapter(_ text: String, title: String) -> Chapter {
        let blocks = text.replacingOccurrences(of: "\r\n", with: "\n").components(separatedBy: "\n\n")
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { !$0.isEmpty }
            .map { "<p>" + escape($0).replacingOccurrences(of: "\n", with: "<br/>") + "</p>" }
        return Chapter(title: title, body: blocks.joined(separator: "\n"))
    }

    // MARK: Sources

    static func fromDocuments(_ files: [URL], ctx: PickContext) async throws -> URL {
        let sorted = files.sorted { $0.lastPathComponent.localizedStandardCompare($1.lastPathComponent) == .orderedAscending }
        var chapters: [Chapter] = []
        var assets: [Asset] = []
        var sample = ""
        for (index, file) in sorted.enumerated() {
            try Task.checkCancellation()
            ctx.report(Double(index) / Double(sorted.count) * 0.7, file.lastPathComponent)
            let base = FileNames.split(file.lastPathComponent).base
            if FileNames.normalizedExt(file) == "txt" {
                let text = try DocumentEngine.readText(file)
                sample += text.prefix(2000)
                chapters.append(textChapter(text, title: base))
                continue
            }
            let markdown = try await MainActor.run { try DocumentEngine.markdown(of: file) }
            sample += markdown.prefix(2000)
            let parts = sorted.count == 1 ? sections(markdown, fallbackTitle: base) : [(sections(markdown, fallbackTitle: base).first?.title ?? base, markdown)]
            for part in parts {
                let html = resolveImages(Markdown.body(part.markdown), base: file.deletingLastPathComponent(), assets: &assets)
                chapters.append(Chapter(title: part.title, body: xhtml(html)))
            }
        }
        guard !chapters.isEmpty else { throw PickError(L("err.no_text")) }
        let title = sorted.count == 1 ? (chapters.count == 1 ? chapters[0].title : FileNames.split(sorted[0].lastPathComponent).base)
            : sorted[0].deletingLastPathComponent().lastPathComponent
        let dst = ctx.destination(for: sorted[0], ext: "epub", suffix: sorted.count > 1 ? LP("sfx.and_more", sorted.count - 1) : nil)
        return try await ctx.produce(dst) { tmp in
            try await write(title: title, language: language(of: sample), chapters: chapters, assets: assets, cover: nil, to: tmp)
        }
    }

    static func fromImages(_ files: [URL], ctx: PickContext) async throws -> URL {
        let sorted = files.sorted { $0.lastPathComponent.localizedStandardCompare($1.lastPathComponent) == .orderedAscending }
        var assets: [Asset] = []
        var chapters: [Chapter] = []
        for (index, file) in sorted.enumerated() {
            try Task.checkCancellation()
            ctx.report(Double(index) / Double(sorted.count) * 0.85, file.lastPathComponent)
            let image = try ImageEngine.load(file, maxPixel: 2400).image
            let alpha = ImageEngine.hasAlpha(image)
            let ext = alpha ? "png" : "jpg"
            let staging = FileManager.default.temporaryDirectory.appendingPathComponent("pickverter-page-\(UUID().uuidString).\(ext)")
            defer { try? FileManager.default.removeItem(at: staging) }
            try await ImageEngine.encode(image, to: staging, ext: ext, quality: 0.88)
            let name = String(format: "%03d.%@", index + 1, ext)
            assets.append(Asset(name: name, data: try Data(contentsOf: staging), mediaType: imageTypes[ext]!))
            chapters.append(Chapter(title: L("pages.page_short", "\(index + 1)"),
                                    body: "<div class=\"page\"><img src=\"../images/\(name)\" alt=\"\(escape(FileNames.split(file.lastPathComponent).base))\"/></div>"))
        }
        guard !chapters.isEmpty else { throw PickError(L("err.read_image")) }
        let title = sorted.count > 1 ? sorted[0].deletingLastPathComponent().lastPathComponent : FileNames.split(sorted[0].lastPathComponent).base
        let dst = ctx.destination(for: sorted[0], ext: "epub", suffix: sorted.count > 1 ? LP("sfx.and_more", sorted.count - 1) : nil)
        return try await ctx.produce(dst) { tmp in
            try await write(title: title, language: Loc.language, chapters: chapters, assets: assets, cover: assets.first?.name, to: tmp)
        }
    }

    // MARK: Packaging

    static func write(title: String, language: String, chapters: [Chapter], assets: [Asset], cover: String?, to url: URL) async throws {
        let fm = FileManager.default
        let root = url.deletingLastPathComponent().appendingPathComponent("epub-" + UUID().uuidString, isDirectory: true)
        let oebps = root.appendingPathComponent("OEBPS", isDirectory: true)
        try fm.createDirectory(at: oebps.appendingPathComponent("text"), withIntermediateDirectories: true)
        try fm.createDirectory(at: root.appendingPathComponent("META-INF"), withIntermediateDirectories: true)
        defer { try? fm.removeItem(at: root) }
        func save(_ text: String, _ path: String) throws {
            try text.write(to: root.appendingPathComponent(path), atomically: true, encoding: .utf8)
        }
        let lang = escape(language)
        let bookTitle = escape(title)
        try save("application/epub+zip", "mimetype")
        try save("""
        <?xml version="1.0" encoding="UTF-8"?>
        <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
          <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
        </container>
        """, "META-INF/container.xml")
        try save("""
        body { font-family: serif; line-height: 1.6; margin: 0 4%; }
        h1, h2, h3, h4 { font-family: sans-serif; line-height: 1.3; }
        img { max-width: 100%; height: auto; }
        pre { white-space: pre-wrap; font-size: 0.9em; }
        code { font-family: monospace; }
        blockquote { margin-left: 0; padding-left: 1em; border-left: 3px solid #ccc; }
        table { border-collapse: collapse; } th, td { border: 1px solid #999; padding: 0.2em 0.5em; }
        .page { margin: 0; padding: 0; text-align: center; }
        .page img { max-height: 96vh; }
        """, "OEBPS/style.css")

        var manifest: [String] = [
            "<item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>",
            "<item id=\"ncx\" href=\"toc.ncx\" media-type=\"application/x-dtbncx+xml\"/>",
            "<item id=\"css\" href=\"style.css\" media-type=\"text/css\"/>",
        ]
        var spine: [String] = []
        var navItems: [String] = []
        var ncxItems: [String] = []
        for (index, chapter) in chapters.enumerated() {
            let id = String(format: "ch%03d", index + 1)
            let heading = escape(chapter.title)
            try save("""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE html>
            <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops" lang="\(lang)" xml:lang="\(lang)">
            <head><meta charset="UTF-8"/><title>\(heading)</title><link rel="stylesheet" type="text/css" href="../style.css"/></head>
            <body>
            <section epub:type="chapter">
            \(chapter.body)
            </section>
            </body>
            </html>
            """, "OEBPS/text/\(id).xhtml")
            manifest.append("<item id=\"\(id)\" href=\"text/\(id).xhtml\" media-type=\"application/xhtml+xml\"/>")
            spine.append("<itemref idref=\"\(id)\"/>")
            navItems.append("<li><a href=\"text/\(id).xhtml\">\(heading)</a></li>")
            ncxItems.append("<navPoint id=\"np\(index + 1)\" playOrder=\"\(index + 1)\"><navLabel><text>\(heading)</text></navLabel><content src=\"text/\(id).xhtml\"/></navPoint>")
        }
        if !assets.isEmpty {
            try fm.createDirectory(at: oebps.appendingPathComponent("images"), withIntermediateDirectories: true)
        }
        for (index, asset) in assets.enumerated() {
            try asset.data.write(to: oebps.appendingPathComponent("images/" + asset.name))
            let isCover = asset.name == cover ? " properties=\"cover-image\"" : ""
            let id = asset.name == cover ? "cover-image" : String(format: "img%03d", index + 1)
            manifest.append("<item id=\"\(id)\" href=\"images/\(asset.name)\" media-type=\"\(asset.mediaType)\"\(isCover)/>")
        }
        let identifier = "urn:uuid:" + UUID().uuidString.lowercased()
        let modified = ISO8601DateFormatter().string(from: Date())
        try save("""
        <?xml version="1.0" encoding="UTF-8"?>
        <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="bookid" xml:lang="\(lang)">
          <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
            <dc:identifier id="bookid">\(identifier)</dc:identifier>
            <dc:title>\(bookTitle)</dc:title>
            <dc:language>\(lang)</dc:language>
            <meta property="dcterms:modified">\(modified)</meta>
            \(cover != nil ? "<meta name=\"cover\" content=\"cover-image\"/>" : "")
          </metadata>
          <manifest>
            \(manifest.joined(separator: "\n    "))
          </manifest>
          <spine toc="ncx">
            \(spine.joined(separator: "\n    "))
          </spine>
        </package>
        """, "OEBPS/content.opf")
        try save("""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE html>
        <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops" lang="\(lang)" xml:lang="\(lang)">
        <head><meta charset="UTF-8"/><title>\(bookTitle)</title></head>
        <body>
        <nav epub:type="toc" id="toc"><h1>\(bookTitle)</h1>
        <ol>
        \(navItems.joined(separator: "\n"))
        </ol>
        </nav>
        </body>
        </html>
        """, "OEBPS/nav.xhtml")
        try save("""
        <?xml version="1.0" encoding="UTF-8"?>
        <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1">
          <head><meta name="dtb:uid" content="\(identifier)"/></head>
          <docTitle><text>\(bookTitle)</text></docTitle>
          <navMap>
            \(ncxItems.joined(separator: "\n    "))
          </navMap>
        </ncx>
        """, "OEBPS/toc.ncx")

        // The mimetype entry must come first and be stored uncompressed.
        try await Shell.run("/usr/bin/zip", ["-X0", "-q", url.path, "mimetype"], cwd: root)
        try await Shell.run("/usr/bin/zip", ["-X", "-r", "-9", "-q", "-D", url.path, "META-INF", "OEBPS"], cwd: root)
    }
}
