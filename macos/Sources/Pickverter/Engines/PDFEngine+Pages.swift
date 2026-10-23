import AppKit
import PDFKit

struct PDFPageEntry: Codable, Equatable {
    /// Index into the selected files.
    var file: Int
    var page: Int
    /// Extra clockwise rotation in degrees (multiples of 90).
    var rotation: Int
}

struct PDFArrangement: Codable, Equatable {
    var pages: [PDFPageEntry]
}

extension PDFEngine {
    static func write(_ doc: PDFDocument, like src: URL, suffix: String, ctx: PickContext) async throws -> URL {
        try await ctx.produce(ctx.destination(for: src, ext: "pdf", suffix: suffix)) { tmp in
            guard doc.write(to: tmp) else { throw PickError(L("err.pdf_save")) }
        }
    }

    /// Builds a new document from pages of one or more PDFs, in the given order and rotation.
    static func arrange(_ files: [URL], _ arrangement: PDFArrangement, suffix: String? = nil, ctx: PickContext) async throws -> URL {
        guard !arrangement.pages.isEmpty else { throw PickError(L("err.pdf_empty")) }
        let docs = try files.map { try open($0) }
        let result = PDFDocument()
        for entry in arrangement.pages {
            guard docs.indices.contains(entry.file), let page = docs[entry.file].page(at: entry.page)?.copy() as? PDFPage else { continue }
            page.rotation = ((page.rotation + entry.rotation) % 360 + 360) % 360
            result.insert(page, at: result.pageCount)
        }
        guard result.pageCount > 0 else { throw PickError(L("err.pdf_empty")) }
        let name = suffix ?? (files.count > 1 ? L("sfx.merged") : L("sfx.edited"))
        return try await write(result, like: files[0], suffix: name, ctx: ctx)
    }

    static func select(_ src: URL, pages: [Int], suffix: String, ctx: PickContext) async throws -> URL {
        let doc = try open(src)
        let valid = pages.filter { $0 >= 0 && $0 < doc.pageCount }
        guard !valid.isEmpty else { throw PickError(L("err.no_pages")) }
        return try await arrange([src], PDFArrangement(pages: valid.map { PDFPageEntry(file: 0, page: $0, rotation: 0) }),
                                 suffix: suffix, ctx: ctx)
    }

    static func reverse(_ src: URL, ctx: PickContext) async throws -> URL {
        let doc = try open(src)
        guard doc.pageCount > 1 else { throw PickError(L("err.single_page")) }
        return try await select(src, pages: Array((0..<doc.pageCount).reversed()), suffix: L("sfx.reversed"), ctx: ctx)
    }

    static func oddEven(_ src: URL, odd: Bool, ctx: PickContext) async throws -> URL {
        let doc = try open(src)
        guard doc.pageCount > 1 else { throw PickError(L("err.single_page")) }
        let pages = (0..<doc.pageCount).filter { ($0 % 2 == 0) == odd }
        return try await select(src, pages: pages, suffix: odd ? L("sfx.odd") : L("sfx.even"), ctx: ctx)
    }

    /// A page is blank when it has no text and almost every pixel is close to white.
    static func isBlank(_ page: PDFPage) -> Bool {
        if let text = page.string, !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return false }
        guard let image = render(page, dpi: 30), let data = image.dataProvider?.data, let bytes = CFDataGetBytePtr(data) else {
            return false
        }
        let perRow = image.bytesPerRow, step = image.bitsPerPixel / 8
        var ink = 0
        for y in 0..<image.height {
            for x in 0..<image.width {
                let p = y * perRow + x * step
                if Int(bytes[p]) + Int(bytes[p + 1]) + Int(bytes[p + 2]) < 3 * 235 { ink += 1 }
            }
        }
        return Double(ink) / Double(max(1, image.width * image.height)) < 0.002
    }

    static func removeBlank(_ src: URL, ctx: PickContext) async throws -> URL {
        let doc = try open(src)
        var keep: [Int] = []
        for index in 0..<doc.pageCount {
            try Task.checkCancellation()
            ctx.report(Double(index) / Double(max(doc.pageCount, 1)) * 0.8, L("note.page", "\(index + 1)", "\(doc.pageCount)"))
            if let page = doc.page(at: index), !isBlank(page) { keep.append(index) }
        }
        if keep.count == doc.pageCount { throw PickError(L("err.no_blank")) }
        guard !keep.isEmpty else { throw PickError(L("err.all_blank")) }
        return try await select(src, pages: keep, suffix: L("sfx.no_blank"), ctx: ctx)
    }

    /// "1-3, 5, 8-" -> zero-based page indexes (in the typed order, duplicates kept).
    static func parseRanges(_ text: String, pageCount: Int) -> [Int]? {
        var pages: [Int] = []
        let parts = text.replacingOccurrences(of: "–", with: "-").split(whereSeparator: { $0 == "," || $0 == ";" || $0 == " " })
        for raw in parts where !raw.isEmpty {
            let piece = String(raw)
            if piece.contains("-") {
                let ends = piece.split(separator: "-", omittingEmptySubsequences: false).map { Int($0) }
                guard ends.count == 2 else { return nil }
                let lower = ends[0] ?? 1, upper = ends[1] ?? pageCount
                guard lower >= 1, upper >= 1 else { return nil }
                let range = lower <= upper ? Array(lower...upper) : Array((upper...lower).reversed())
                pages += range.filter { $0 <= pageCount }.map { $0 - 1 }
            } else {
                guard let number = Int(piece), number >= 1, number <= pageCount else { return nil }
                pages.append(number - 1)
            }
        }
        return pages.isEmpty ? nil : pages
    }

    @MainActor
    static func askRange(for url: URL, pageCount: Int) -> String? {
        if let injected = ProcessInfo.processInfo.environment["PICKVERTER_EDITOR_RANGE"] { return injected }
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = L("pdf.range.title")
        alert.informativeText = L("pdf.range.info", url.lastPathComponent, "\(pageCount)")
        let field = NSTextField(frame: NSRect(x: 0, y: 0, width: 260, height: 24))
        field.placeholderString = pageCount > 3 ? "1-3, \(pageCount)" : "1"
        alert.accessoryView = field
        alert.addButton(withTitle: L("pdf.range.extract"))
        alert.addButton(withTitle: L("common.cancel"))
        alert.window.initialFirstResponder = field
        guard DialogLevel.runModal(alert.window, alert.runModal) == .alertFirstButtonReturn else { return nil }
        return field.stringValue
    }

    static func extractRange(_ src: URL, ctx: PickContext) async throws -> URL {
        let doc = try open(src)
        guard let text = await MainActor.run(body: { askRange(for: src, pageCount: doc.pageCount) }) else { throw CancellationError() }
        guard let pages = parseRanges(text, pageCount: doc.pageCount) else { throw PickError(L("err.bad_range", text)) }
        return try await select(src, pages: pages, suffix: L("sfx.pages_range"), ctx: ctx)
    }

    // MARK: Document info

    static func info(_ url: URL) throws -> [String: String] {
        let doc = try open(url)
        var fields: [String: String] = [:]
        let attributes = doc.documentAttributes ?? [:]
        let map: [(PDFDocumentAttribute, String)] = [(.titleAttribute, "title"), (.authorAttribute, "author"),
                                                     (.subjectAttribute, "subject"), (.creatorAttribute, "creator")]
        for (attribute, key) in map {
            if let value = attributes[attribute] as? String { fields[key] = value }
        }
        if let keywords = attributes[PDFDocumentAttribute.keywordsAttribute] {
            fields["keywords"] = (keywords as? [String])?.joined(separator: ", ") ?? (keywords as? String) ?? ""
        }
        return fields
    }

    static func writeInfo(_ src: URL, fields: [String: String], to dst: URL) throws {
        let doc = try open(src)
        var attributes = doc.documentAttributes ?? [:]
        let map: [(String, PDFDocumentAttribute)] = [("title", .titleAttribute), ("author", .authorAttribute),
                                                     ("subject", .subjectAttribute), ("creator", .creatorAttribute)]
        for (key, attribute) in map {
            guard let value = fields[key] else { continue }
            attributes[attribute] = value.isEmpty ? nil : value
        }
        if let keywords = fields["keywords"] {
            let list = keywords.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
            attributes[PDFDocumentAttribute.keywordsAttribute] = list.isEmpty ? nil : list
        }
        doc.documentAttributes = attributes
        guard doc.write(to: dst) else { throw PickError(L("err.pdf_save")) }
    }
}
