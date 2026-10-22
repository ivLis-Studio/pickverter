import AppKit
import QuickLookThumbnailing

/// Office Open XML, OpenDocument and iWork files.
///  • Built in: XLSX -> CSV/TSV/JSON/MD/HTML, PPTX -> Markdown/text, media extraction, preview images (Quick Look).
///  • With Pages/Keynote/Numbers or LibreOffice installed: high-fidelity PDF/DOCX/PPTX/XLSX exports.
enum OfficeEngine {
    static let exts: Set<String> = ["xlsx", "xlsm", "pptx", "ppsx", "numbers", "pages", "key", "ods", "odp"]

    enum IWork: String, CaseIterable {
        case pages, keynote, numbers

        var bundleID: String {
            switch self {
            case .pages: return "com.apple.iWork.Pages"
            case .keynote: return "com.apple.iWork.Keynote"
            case .numbers: return "com.apple.iWork.Numbers"
            }
        }

        var name: String {
            switch self {
            case .pages: return "Pages"
            case .keynote: return "Keynote"
            case .numbers: return "Numbers"
            }
        }

        var isInstalled: Bool { NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID) != nil }

        /// AppleScript export format names.
        func format(for target: String) -> String? {
            switch (self, target) {
            case (.pages, "pdf"), (.keynote, "pdf"), (.numbers, "pdf"): return "PDF"
            case (.pages, "docx"): return "Microsoft Word"
            case (.pages, "txt"): return "unformatted text"
            case (.pages, "epub"): return "EPUB"
            case (.keynote, "pptx"): return "Microsoft PowerPoint"
            case (.numbers, "xlsx"): return "Microsoft Excel"
            case (.numbers, "csv"): return "CSV"
            default: return nil
            }
        }
    }

    enum Route {
        case builtIn
        case iWork(IWork)
        case libreOffice(String)
    }

    static var libreOffice: String? {
        let candidates = ["/Applications/LibreOffice.app/Contents/MacOS/soffice",
                          NSHomeDirectory() + "/Applications/LibreOffice.app/Contents/MacOS/soffice"]
        if let app = NSWorkspace.shared.urlForApplication(withBundleIdentifier: "org.libreoffice.script") {
            let path = app.appendingPathComponent("Contents/MacOS/soffice").path
            if FileManager.default.isExecutableFile(atPath: path) { return path }
        }
        return candidates.first { FileManager.default.isExecutableFile(atPath: $0) }
    }

    static func targets(for ext: String) -> [String] {
        switch ext {
        case "xlsx", "xlsm": return ["csv", "tsv", "json", "md", "html", "pdf", "png"]
        case "pptx", "ppsx": return ["pdf", "md", "txt", "png"]
        case "numbers": return ["xlsx", "csv", "pdf", "png"]
        case "pages": return ["pdf", "docx", "txt", "epub", "png"]
        case "key": return ["pdf", "pptx", "png"]
        case "ods": return ["xlsx", "csv", "pdf"]
        case "odp": return ["pptx", "pdf"]
        default: return []
        }
    }

    static func isBuiltIn(_ source: String, _ target: String) -> Bool {
        if target == "png" { return true }
        switch source {
        case "xlsx", "xlsm": return ["csv", "tsv", "json", "md", "html"].contains(target)
        case "pptx", "ppsx": return ["md", "txt"].contains(target)
        default: return false
        }
    }

    /// iWork apps can open their own files plus Office ones (Keynote opens PPTX, Numbers opens XLSX).
    static func iWorkCandidate(_ source: String) -> IWork? {
        switch source {
        case "pages": return .pages
        case "key", "pptx", "ppsx": return .keynote
        case "numbers", "xlsx", "xlsm": return .numbers
        default: return nil
        }
    }

    static func route(_ source: String, _ target: String) -> Route? {
        if isBuiltIn(source, target) { return .builtIn }
        let native = ["pages", "key", "numbers"].contains(source)
        let apple: Route? = iWorkCandidate(source).flatMap { app in
            app.isInstalled && app.format(for: target) != nil ? .iWork(app) : nil
        }
        let libre: Route? = libreOffice.map { .libreOffice($0) }
        return native ? (apple ?? libre) : (libre ?? apple)
    }

    static func unavailable(_ source: String, _ target: String) -> String? {
        guard route(source, target) == nil else { return nil }
        var apps: [String] = []
        if let app = iWorkCandidate(source), app.format(for: target) != nil { apps.append(app.name) }
        apps.append("LibreOffice")
        return L("err.needs_app", apps.joined(separator: " · "))
    }

    // MARK: Converting

    static func convert(_ src: URL, to ext: String, ctx: PickContext) async throws -> URL {
        let source = FileNames.normalizedExt(src)
        guard let route = route(source, ext) else { throw PickError(unavailable(source, ext) ?? L("err.not_convertible")) }
        switch route {
        case .builtIn:
            if ext == "png" {
                return try await ctx.produce(ctx.destination(for: src, ext: "png")) { tmp in try await renderPreview(src, to: tmp) }
            }
            if source.hasPrefix("ppt") {
                let slides = try await readSlides(src)
                let text = ext == "md" ? slidesMarkdown(slides, title: FileNames.split(src.lastPathComponent).base) : slidesText(slides)
                return try await ctx.produce(ctx.destination(for: src, ext: ext)) { tmp in
                    try text.write(to: tmp, atomically: true, encoding: .utf8)
                }
            }
            return try await convertWorkbook(src, to: ext, ctx: ctx)
        case .iWork(let app):
            ctx.report(nil, L("note.opening_in", app.name))
            return try await ctx.produce(ctx.destination(for: src, ext: ext)) { tmp in
                try await viaIWork(app, src: src, target: ext, to: tmp)
            }
        case .libreOffice(let soffice):
            ctx.report(nil, L("note.opening_in", "LibreOffice"))
            return try await ctx.produce(ctx.destination(for: src, ext: ext)) { tmp in
                try await viaLibreOffice(soffice, src: src, target: ext, to: tmp)
            }
        }
    }

    static func renderPreview(_ src: URL, to dst: URL) async throws {
        let request = QLThumbnailGenerator.Request(fileAt: src, size: CGSize(width: 2048, height: 2048), scale: 1,
                                                   representationTypes: .thumbnail)
        guard let representation = try? await QLThumbnailGenerator.shared.generateBestRepresentation(for: request) else {
            throw PickError(L("err.no_preview"))
        }
        try await ImageEngine.encode(representation.cgImage, to: dst, ext: "png")
    }

    static func viaIWork(_ app: IWork, src: URL, target: String, to dst: URL) async throws {
        guard let format = app.format(for: target) else { throw PickError(L("err.not_convertible")) }
        let wasRunning = !NSRunningApplication.runningApplications(withBundleIdentifier: app.bundleID).isEmpty
        let script = """
        on run argv
            set inFile to POSIX file (item 1 of argv)
            set outFile to POSIX file (item 2 of argv)
            tell application id "\(app.bundleID)"
                set theDoc to open inFile
                export theDoc to outFile as \(format)
                close theDoc saving no
            end tell
        end run
        """
        defer {
            if !wasRunning {
                Task.detached { try? await Shell.run("/usr/bin/osascript", ["-e", "tell application id \"\(app.bundleID)\" to quit"]) }
            }
        }
        do {
            try await Shell.run("/usr/bin/osascript", ["-e", script, src.path, dst.path])
        } catch let failure as Shell.Failure where failure.stderr.contains("-1743") {
            throw PickError(L("err.automation_denied", app.name))
        }
    }

    static func viaLibreOffice(_ soffice: String, src: URL, target: String, to dst: URL) async throws {
        let fm = FileManager.default
        let outDir = dst.deletingLastPathComponent().appendingPathComponent("lo-" + UUID().uuidString, isDirectory: true)
        try fm.createDirectory(at: outDir, withIntermediateDirectories: true)
        defer { try? fm.removeItem(at: outDir) }
        let profile = fm.temporaryDirectory.appendingPathComponent("pickverter-libreoffice", isDirectory: true)
        let filter: String
        switch target {
        case "txt": filter = "txt:Text (encoded):UTF8"
        case "csv": filter = "csv:Text - txt - csv (StarCalc):44,34,76,1"
        default: filter = target
        }
        try await Shell.run(soffice, ["-env:UserInstallation=" + profile.absoluteString, "--headless", "--norestore",
                                      "--convert-to", filter, "--outdir", outDir.path, src.path])
        guard let produced = try fm.contentsOfDirectory(at: outDir, includingPropertiesForKeys: nil).first else {
            throw PickError(L("err.no_result"))
        }
        try fm.moveItem(at: produced, to: dst)
    }

    // MARK: Packages

    /// Zip-based documents are unpacked to a temporary folder; package folders are read in place.
    static func withUnpacked<T>(_ src: URL, _ body: (URL) async throws -> T) async throws -> T {
        var isDir: ObjCBool = false
        if FileManager.default.fileExists(atPath: src.path, isDirectory: &isDir), isDir.boolValue { return try await body(src) }
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("pickverter-office-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        do {
            try await Shell.run("/usr/bin/ditto", ["-x", "-k", src.path, folder.path])
        } catch is Shell.Failure {
            throw PickError(L("err.office_damaged"))
        }
        return try await body(folder)
    }

    static let mediaExts: Set<String> = ["png", "jpg", "jpeg", "gif", "tif", "tiff", "bmp", "heic", "webp", "svg", "emf", "wmf",
                                         "mp4", "mov", "m4v", "mp3", "m4a", "wav", "aiff", "pdf"]

    static func extractMedia(_ src: URL, ctx: PickContext) async throws -> URL {
        ctx.report(nil, L("note.extracting"))
        return try await withUnpacked(src) { root in
            let fm = FileManager.default
            var found: [URL] = []
            for folder in ["word/media", "xl/media", "ppt/media", "Data", "Pictures", "media"] {
                let dir = root.appendingPathComponent(folder)
                guard let walker = fm.enumerator(at: dir, includingPropertiesForKeys: [.isRegularFileKey]) else { continue }
                for case let file as URL in walker.allObjects where mediaExts.contains(file.pathExtension.lowercased()) {
                    // iWork keeps tiny "-small" copies for its own previews.
                    if file.deletingPathExtension().lastPathComponent.hasSuffix("-small") { continue }
                    found.append(file)
                }
            }
            guard !found.isEmpty else { throw PickError(L("err.no_media")) }
            return try await ctx.produce(ctx.destination(for: src, ext: nil, suffix: L("sfx.media"))) { folder in
                try fm.createDirectory(at: folder, withIntermediateDirectories: true)
                for file in found {
                    try fm.copyItem(at: file, to: FileNames.unique(folder.appendingPathComponent(file.lastPathComponent)))
                }
            }
        }
    }

    static func copyText(_ ctx: PickContext) async throws -> PickResult {
        var chunks: [String] = []
        for file in ctx.files {
            let ext = FileNames.normalizedExt(file)
            if ext.hasPrefix("ppt") {
                chunks.append(slidesText(try await readSlides(file)))
            } else if ext.hasPrefix("xls") {
                let sheets = try await readWorkbook(file)
                chunks.append(sheets.map { sheet in
                    (sheets.count > 1 ? sheet.name + "\n" : "") + sheet.rows.map { $0.joined(separator: "\t") }.joined(separator: "\n")
                }.joined(separator: "\n\n"))
            }
        }
        let text = chunks.joined(separator: "\n\n").trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { throw PickError(L("err.no_text")) }
        await MainActor.run {
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(text, forType: .string)
        }
        return .note(LP("msg.chars_copied", text.count))
    }

    // MARK: XLSX

    struct Sheet {
        var name: String
        var rows: [[String]]
    }

    static func relationships(_ url: URL) -> [String: String] {
        var map: [String: String] = [:]
        XMLWalker.walk(url, start: { name, attrs in
            if name == "Relationship", let id = attrs["Id"], let target = attrs["Target"] { map[id] = target }
        })
        return map
    }

    static func resolve(_ target: String, base: String) -> String {
        target.hasPrefix("/") ? String(target.dropFirst()) : base + target
    }

    static func columnIndex(_ reference: String) -> Int? {
        var value = 0
        var seen = false
        for scalar in reference.unicodeScalars {
            guard scalar.value >= 65, scalar.value <= 90 else { break }
            value = value * 26 + Int(scalar.value - 64)
            seen = true
        }
        return seen ? value - 1 : nil
    }

    static let builtInDateFormats: Set<Int> = Set(14...22).union(27...36).union(45...47).union(50...58)

    static func isDateFormat(_ code: String) -> Bool {
        var cleaned = code.lowercased()
        cleaned = Markdown.replace(cleaned, "\"[^\"]*\"", "")
        cleaned = Markdown.replace(cleaned, "\\[[^\\]]*\\]", "")
        cleaned = Markdown.replace(cleaned, "\\\\.", "")
        guard cleaned != "general" else { return false }
        return cleaned.contains("y") || cleaned.contains("d") || cleaned.contains("h") || cleaned.contains("s")
            || (cleaned.contains("m") && !cleaned.contains("0"))
    }

    static func readWorkbook(_ src: URL) async throws -> [Sheet] {
        try await withUnpacked(src) { root in
            let xl = root.appendingPathComponent("xl")
            var sheetRefs: [(name: String, rid: String)] = []
            var date1904 = false
            XMLWalker.walk(xl.appendingPathComponent("workbook.xml"), start: { name, attrs in
                if name == "sheet", let sheetName = attrs["name"],
                   let rid = attrs.first(where: { $0.key.hasSuffix(":id") || $0.key == "id" })?.value {
                    sheetRefs.append((sheetName, rid))
                } else if name == "workbookPr", let flag = attrs["date1904"] {
                    date1904 = flag == "1" || flag == "true"
                }
            })
            guard !sheetRefs.isEmpty else { throw PickError(L("err.office_damaged")) }
            let rels = relationships(xl.appendingPathComponent("_rels/workbook.xml.rels"))

            var shared: [String] = []
            var current = ""
            var inItem = false, inText = false, skip = 0
            XMLWalker.walk(xl.appendingPathComponent("sharedStrings.xml"), start: { name, _ in
                switch name {
                case "si": inItem = true; current = ""
                case "t": inText = true
                case "rPh": skip += 1
                default: break
                }
            }, end: { name in
                switch name {
                case "si": shared.append(current); inItem = false
                case "t": inText = false
                case "rPh": skip -= 1
                default: break
                }
            }, text: { text in
                if inItem, inText, skip == 0 { current += text }
            })

            var formats: [Int: String] = [:]
            var styles: [Int] = []
            var inCellXfs = false
            XMLWalker.walk(xl.appendingPathComponent("styles.xml"), start: { name, attrs in
                if name == "numFmt", let id = Int(attrs["numFmtId"] ?? ""), let code = attrs["formatCode"] { formats[id] = code }
                if name == "cellXfs" { inCellXfs = true }
                if name == "xf", inCellXfs { styles.append(Int(attrs["numFmtId"] ?? "0") ?? 0) }
            }, end: { name in
                if name == "cellXfs" { inCellXfs = false }
            })
            func dateStyle(_ style: Int?) -> (isDate: Bool, hasTime: Bool) {
                guard let style, styles.indices.contains(style) else { return (false, false) }
                let id = styles[style]
                if builtInDateFormats.contains(id) { return (true, [18, 19, 20, 21, 22, 45, 46, 47].contains(id)) }
                guard let code = formats[id], isDateFormat(code) else { return (false, false) }
                let lower = code.lowercased()
                return (true, lower.contains("h") || lower.contains("s"))
            }
            let epoch = date1904 ? Date(timeIntervalSince1970: -2_082_844_800) : Date(timeIntervalSince1970: -2_209_161_600)
            let dateFormatter = DateFormatter()
            dateFormatter.locale = Locale(identifier: "en_US_POSIX")
            dateFormatter.timeZone = TimeZone(identifier: "UTC")

            var sheets: [Sheet] = []
            for ref in sheetRefs {
                guard let target = rels[ref.rid] else { continue }
                let path = root.appendingPathComponent(resolve(target, base: "xl/"))
                var cells: [Int: [Int: String]] = [:]
                var rowIndex = -1, column = -1
                var type = "", style: Int?
                var value = "", inValue = false, inInline = false
                XMLWalker.walk(path, start: { name, attrs in
                    switch name {
                    case "row":
                        rowIndex = (Int(attrs["r"] ?? "") ?? (rowIndex + 2)) - 1
                        column = -1
                    case "c":
                        column = attrs["r"].flatMap(columnIndex) ?? (column + 1)
                        type = attrs["t"] ?? "n"
                        style = attrs["s"].flatMap { Int($0) }
                        value = ""
                    case "v": inValue = true
                    case "is": inInline = true
                    default: break
                    }
                }, end: { name in
                    switch name {
                    case "v": inValue = false
                    case "is": inInline = false
                    case "c":
                        var text = value
                        switch type {
                        case "s": text = Int(value).flatMap { shared.indices.contains($0) ? shared[$0] : nil } ?? ""
                        case "b": text = value == "1" ? "TRUE" : "FALSE"
                        case "str", "inlineStr", "e": break
                        default:
                            if let number = Double(value) {
                                let date = dateStyle(style)
                                if date.isDate {
                                    dateFormatter.dateFormat = date.hasTime ? (number < 1 ? "HH:mm:ss" : "yyyy-MM-dd HH:mm:ss") : "yyyy-MM-dd"
                                    text = dateFormatter.string(from: epoch.addingTimeInterval((number * 86_400).rounded()))
                                } else if number == number.rounded(), abs(number) < 1e15 {
                                    text = String(Int64(number))
                                } else {
                                    text = String(number)
                                }
                            }
                        }
                        if !text.isEmpty, rowIndex >= 0, column >= 0 { cells[rowIndex, default: [:]][column] = text }
                    default: break
                    }
                }, text: { text in
                    if inValue || inInline { value += text }
                })
                let maxRow = cells.keys.max() ?? -1
                let maxColumn = cells.values.compactMap { $0.keys.max() }.max() ?? -1
                guard maxRow >= 0, maxColumn >= 0 else { continue }
                let rows = (0...maxRow).map { row in (0...maxColumn).map { cells[row]?[$0] ?? "" } }
                sheets.append(Sheet(name: ref.name, rows: rows))
            }
            guard !sheets.isEmpty else { throw PickError(L("err.table_empty")) }
            return sheets
        }
    }

    static func convertWorkbook(_ src: URL, to ext: String, ctx: PickContext) async throws -> URL {
        ctx.report(nil, L("note.reading"))
        let sheets = try await readWorkbook(src)
        if sheets.count == 1 {
            let text = try DataEngine.writeTable(sheets[0].rows, ext: ext)
            return try await ctx.produce(ctx.destination(for: src, ext: ext)) { tmp in
                try text.write(to: tmp, atomically: true, encoding: .utf8)
            }
        }
        switch ext {
        case "csv", "tsv":
            return try await ctx.produce(ctx.destination(for: src, ext: nil, suffix: ext.uppercased())) { folder in
                try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
                for sheet in sheets {
                    let name = sheet.name.replacingOccurrences(of: "/", with: "-").replacingOccurrences(of: ":", with: "-")
                    let file = FileNames.unique(folder.appendingPathComponent(name + "." + ext))
                    try DataEngine.writeTable(sheet.rows, ext: ext).write(to: file, atomically: true, encoding: .utf8)
                }
            }
        case "json":
            var book: [String: Any] = [:]
            for sheet in sheets {
                guard let header = sheet.rows.first else { continue }
                let keys = DataEngine.uniqued(header)
                book[sheet.name] = sheet.rows.dropFirst().map { Dictionary(uniqueKeysWithValues: zip(keys, $0)) }
            }
            let data = try JSONSerialization.data(withJSONObject: book, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])
            return try await ctx.produce(ctx.destination(for: src, ext: "json")) { tmp in try data.write(to: tmp) }
        default:
            let markdown = try sheets.map { "## \($0.name)\n\n" + (try DataEngine.writeTable($0.rows, ext: "md")) }.joined(separator: "\n")
            let text = ext == "md" ? markdown : Markdown.html(markdown, title: FileNames.split(src.lastPathComponent).base)
            return try await ctx.produce(ctx.destination(for: src, ext: ext)) { tmp in
                try text.write(to: tmp, atomically: true, encoding: .utf8)
            }
        }
    }

    // MARK: PPTX

    struct Slide {
        var title: String?
        var paragraphs: [String]
    }

    static func readSlides(_ src: URL) async throws -> [Slide] {
        try await withUnpacked(src) { root in
            var order: [String] = []
            XMLWalker.walk(root.appendingPathComponent("ppt/presentation.xml"), start: { name, attrs in
                if name == "sldId", let rid = attrs.first(where: { $0.key.hasSuffix(":id") })?.value { order.append(rid) }
            })
            let rels = relationships(root.appendingPathComponent("ppt/_rels/presentation.xml.rels"))
            var slides: [Slide] = []
            for rid in order {
                guard let target = rels[rid] else { continue }
                var slide = Slide(title: nil, paragraphs: [])
                var shapeDepth = 0
                var shapeIsTitle = false
                var shapeParagraphs: [String] = []
                var paragraph = ""
                var inText = false
                XMLWalker.walk(root.appendingPathComponent(resolve(target, base: "ppt/")), start: { name, attrs in
                    switch name {
                    case "sp":
                        shapeDepth += 1
                        if shapeDepth == 1 { shapeIsTitle = false; shapeParagraphs = [] }
                    case "ph":
                        if ["title", "ctrTitle"].contains(attrs["type"] ?? "") { shapeIsTitle = true }
                    case "p": paragraph = ""
                    case "t": inText = true
                    case "br": paragraph += "\n"
                    default: break
                    }
                }, end: { name in
                    switch name {
                    case "t": inText = false
                    case "p":
                        let line = paragraph.trimmingCharacters(in: .whitespacesAndNewlines)
                        if !line.isEmpty {
                            if shapeDepth > 0 { shapeParagraphs.append(line) } else { slide.paragraphs.append(line) }
                        }
                    case "sp":
                        if shapeDepth == 1 {
                            if shapeIsTitle, slide.title == nil {
                                slide.title = shapeParagraphs.joined(separator: " ")
                            } else {
                                slide.paragraphs += shapeParagraphs
                            }
                        }
                        shapeDepth = max(0, shapeDepth - 1)
                    default: break
                    }
                }, text: { text in
                    if inText { paragraph += text }
                })
                slides.append(slide)
            }
            guard !slides.isEmpty else { throw PickError(L("err.office_damaged")) }
            return slides
        }
    }

    static func slidesMarkdown(_ slides: [Slide], title: String) -> String {
        var lines = ["# \(title)", ""]
        for (index, slide) in slides.enumerated() {
            let heading = slide.title.map { "\(index + 1). \($0)" } ?? L("pages.slide", "\(index + 1)")
            lines.append("## " + heading)
            lines.append("")
            for paragraph in slide.paragraphs { lines.append("- " + paragraph.replacingOccurrences(of: "\n", with: " ")) }
            lines.append("")
        }
        return lines.joined(separator: "\n")
    }

    static func slidesText(_ slides: [Slide]) -> String {
        slides.enumerated().map { index, slide in
            ([slide.title.map { "\(index + 1). \($0)" } ?? L("pages.slide", "\(index + 1)")] + slide.paragraphs).joined(separator: "\n")
        }.joined(separator: "\n\n") + "\n"
    }
}

/// Tiny SAX helper. Element names are reported without their namespace prefix ("a:t" -> "t").
final class XMLWalker: NSObject, XMLParserDelegate {
    private let start: (String, [String: String]) -> Void
    private let end: (String) -> Void
    private let text: (String) -> Void

    private init(start: @escaping (String, [String: String]) -> Void, end: @escaping (String) -> Void, text: @escaping (String) -> Void) {
        self.start = start
        self.end = end
        self.text = text
    }

    static func walk(_ url: URL, start: @escaping (String, [String: String]) -> Void = { _, _ in },
                     end: @escaping (String) -> Void = { _ in }, text: @escaping (String) -> Void = { _ in }) {
        guard let parser = XMLParser(contentsOf: url) else { return }
        let walker = XMLWalker(start: start, end: end, text: text)
        parser.delegate = walker
        parser.parse()
    }

    private static func local(_ name: String) -> String {
        guard let colon = name.lastIndex(of: ":") else { return name }
        return String(name[name.index(after: colon)...])
    }

    func parser(_ parser: XMLParser, didStartElement elementName: String, namespaceURI: String?, qualifiedName qName: String?,
                attributes attributeDict: [String: String] = [:]) {
        start(Self.local(elementName), attributeDict)
    }

    func parser(_ parser: XMLParser, didEndElement elementName: String, namespaceURI: String?, qualifiedName qName: String?) {
        end(Self.local(elementName))
    }

    func parser(_ parser: XMLParser, foundCharacters string: String) { text(string) }
}
