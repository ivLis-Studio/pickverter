import Foundation
import ImageIO
import UniformTypeIdentifiers

/// rhwp's DocLang v0.6 export — paragraphs, headings, lists, OTSL tables, pictures, formulas and footnotes in XML —
/// read into a small document tree and written back out as HTML, Markdown or plain text.
enum DocLang {
    /// A run of text with one style. "\n" inside the text is a line break.
    struct Span: Equatable {
        var text: String
        var bold = false
        var italic = false
        var underline = false
        var strike = false
        var isSuperscript = false
        var isSubscript = false
        var link: String?

        func sameStyle(as other: Span) -> Bool {
            var copy = other
            copy.text = text
            return copy == self
        }
    }

    indirect enum Block {
        case paragraph([Span])
        case heading(Int, [Span])
        case list(ordered: Bool, items: [[Block]])
        case table(Table)
        case picture(Data, ext: String)
        case formula(String)
    }

    struct Cell {
        var blocks: [Block] = []
        var columns = 1
        var rows = 1
        var header = false
    }

    /// `rows[r][c]` is the cell that starts there, or nil where a merged cell covers the position.
    struct Table {
        var caption: [Span] = []
        var rows: [[Cell?]] = []
        var width: Int { rows.map(\.count).max() ?? 0 }
    }

    struct Document {
        var blocks: [Block] = []
        var footnotes: [[Block]] = []
    }

    // MARK: Reading

    /// `base` is the folder rhwp ran in: picture URIs are relative to it and must stay inside it.
    static func parse(_ data: Data, base: URL) throws -> Document {
        let xml = try XMLDocument(data: data, options: [.nodePreserveWhitespace, .nodeLoadExternalEntitiesNever])
        guard let root = xml.rootElement() else { return Document() }
        var reader = Reader(base: base.standardizedFileURL)
        let blocks = reader.blocks(root.children ?? [])
        return Document(blocks: blocks, footnotes: reader.footnotes)
    }

    private enum Token: Equatable {
        case anchor(Int), left, up, cross
    }

    private struct Reader {
        let base: URL
        var footnotes: [[Block]] = []

        /// Running headers and footers repeat on every page; groups, locations and custom payloads carry layout only.
        static let skipped: Set<String> = ["page_header", "page_footer", "group", "location", "custom", "hwp_prop", "thread",
                                           "page_break", "caption", "marker", "src"]

        mutating func blocks(_ nodes: [XMLNode]) -> [Block] {
            var out: [Block] = []
            for node in nodes {
                guard let element = node as? XMLElement, let name = element.name else {
                    if node.kind == .text, let text = node.stringValue, !DocLang.isBlank(text) { out.append(.paragraph([Span(text: text)])) }
                    continue
                }
                switch name {
                case "text":
                    let spans = DocLang.spans(element.children ?? [])
                    if !DocLang.isBlank(spans) { out.append(.paragraph(spans)) }
                case "heading":
                    let level = Int(element.attribute(forName: "level")?.stringValue ?? "") ?? 1
                    let spans = DocLang.spans(element.children ?? [])
                    if !DocLang.isBlank(spans) { out.append(.heading(min(max(level, 1), 6), spans)) }
                case "list":
                    if let list = list(element) { out.append(list) }
                case "table":
                    let table = table(element)
                    if table.width > 0 { out.append(.table(table)) }
                case "picture":
                    if let picture = picture(element) { out.append(picture) }
                case "formula":
                    let text = (element.stringValue ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
                    if !text.isEmpty { out.append(.formula(text)) }
                case "footnote":
                    let content = blocks(element.children ?? [])
                    if !content.isEmpty { footnotes.append(content) }
                default:
                    if Self.skipped.contains(name) { continue }
                    out += blocks(element.children ?? [])
                }
            }
            return out
        }

        /// Items start at each <ldiv/>; the blocks after it, up to the next one, are the item.
        mutating func list(_ element: XMLElement) -> Block? {
            let ordered = element.attribute(forName: "class")?.stringValue == "ordered"
            var items: [[Block]] = []
            for node in element.children ?? [] {
                if (node as? XMLElement)?.name == "ldiv" {
                    items.append([])
                    continue
                }
                let content = blocks([node])
                guard !content.isEmpty else { continue }
                if items.isEmpty { items.append([]) }
                items[items.count - 1] += content
            }
            items.removeAll { $0.isEmpty }
            return items.isEmpty ? nil : .list(ordered: ordered, items: items)
        }

        /// OTSL: ched/fcel/ecel start a cell (its blocks follow), lcel/ucel/xcel continue a merged one, nl ends a row.
        mutating func table(_ element: XMLElement) -> Table {
            var grid: [[Token]] = [[]]
            var cells: [Cell] = []
            var current: Int?
            var caption: [Span] = []
            for node in element.children ?? [] {
                guard let child = node as? XMLElement, let name = child.name else {
                    if let current, node.kind == .text, let text = node.stringValue, !DocLang.isBlank(text) {
                        cells[current].blocks.append(.paragraph([Span(text: text)]))
                    }
                    continue
                }
                switch name {
                case "caption":
                    caption = DocLang.spans(child.children ?? [])
                case "ched", "fcel", "ecel":
                    cells.append(Cell(header: name == "ched"))
                    current = cells.count - 1
                    grid[grid.count - 1].append(.anchor(cells.count - 1))
                case "lcel", "ucel", "xcel":
                    grid[grid.count - 1].append(name == "lcel" ? .left : name == "ucel" ? .up : .cross)
                    current = nil
                case "nl":
                    grid.append([])
                    current = nil
                case "location", "custom":
                    continue
                default:
                    if let current { cells[current].blocks += blocks([child]) }
                }
            }
            while grid.last?.isEmpty == true { grid.removeLast() }
            var rows: [[Cell?]] = grid.map { row in
                row.map { token -> Cell? in
                    if case .anchor(let index) = token { return cells[index] }
                    return nil
                }
            }
            for (r, row) in grid.enumerated() {
                for (c, token) in row.enumerated() {
                    guard case .anchor = token else { continue }
                    var columns = 1
                    while c + columns < row.count, row[c + columns] == .left { columns += 1 }
                    var spanRows = 1
                    while r + spanRows < grid.count, c < grid[r + spanRows].count, grid[r + spanRows][c] == .up { spanRows += 1 }
                    rows[r][c]?.columns = columns
                    rows[r][c]?.rows = spanRows
                }
            }
            return Table(caption: caption, rows: rows)
        }

        /// The picture's bytes: a data: URI, or a file rhwp wrote next to the XML (never outside that folder).
        func picture(_ element: XMLElement) -> Block? {
            guard let uri = element.elements(forName: "src").first?.attribute(forName: "uri")?.stringValue else { return nil }
            if uri.hasPrefix("data:") {
                guard let comma = uri.firstIndex(of: ","), uri[..<comma].hasSuffix(";base64"),
                      let data = Data(base64Encoded: String(uri[uri.index(after: comma)...])) else { return nil }
                let mime = uri.dropFirst(5).prefix { $0 != ";" }
                let ext = String(mime.split(separator: "/").last ?? "png").replacingOccurrences(of: "jpeg", with: "jpg")
                return DocLang.picture(data, ext: ext)
            }
            let file = base.appendingPathComponent(uri).standardizedFileURL
            guard file.path.hasPrefix(base.path + "/"), let data = try? Data(contentsOf: file) else { return nil }
            return DocLang.picture(data, ext: file.pathExtension.lowercased())
        }
    }

    /// HWP keeps many pictures as uncompressed BMP (or TIFF): those become lossless PNG, often a tenth of the size.
    static func picture(_ data: Data, ext: String) -> Block {
        let kind = ext.lowercased()
        guard ["bmp", "x-ms-bmp", "tif", "tiff"].contains(kind),
              let source = CGImageSourceCreateWithData(data as CFData, nil),
              let image = CGImageSourceCreateImageAtIndex(source, 0, nil) else {
            return .picture(data, ext: kind == "x-ms-bmp" ? "bmp" : kind)
        }
        let out = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(out, UTType.png.identifier as CFString, 1, nil) else {
            return .picture(data, ext: kind)
        }
        CGImageDestinationAddImage(destination, image, nil)
        guard CGImageDestinationFinalize(destination) else { return .picture(data, ext: kind) }
        return .picture(out as Data, ext: "png")
    }

    static func spans(_ nodes: [XMLNode], style: Span = Span(text: "")) -> [Span] {
        var out: [Span] = []
        for node in nodes {
            guard let element = node as? XMLElement, let name = element.name else {
                if node.kind == .text, let text = node.stringValue, !text.isEmpty {
                    var span = style
                    span.text = text
                    out.append(span)
                }
                continue
            }
            var inner = style
            switch name {
            case "bold": inner.bold = true
            case "italic": inner.italic = true
            case "underline": inner.underline = true
            case "strikethrough": inner.strike = true
            case "superscript": inner.isSuperscript = true
            case "subscript": inner.isSubscript = true
            case "href": inner.link = element.attribute(forName: "uri")?.stringValue
            case "location", "custom", "hwp_prop", "thread", "marker": continue
            default: break
            }
            out += spans(element.children ?? [], style: inner)
        }
        return merged(out)
    }

    /// Neighbours with the same style become one span.
    static func merged(_ spans: [Span]) -> [Span] {
        var out: [Span] = []
        for span in spans where !span.text.isEmpty {
            if let last = out.last, last.sameStyle(as: span) {
                out[out.count - 1].text += span.text
            } else {
                out.append(span)
            }
        }
        return out
    }

    static func isBlank(_ text: String) -> Bool { text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
    static func isBlank(_ spans: [Span]) -> Bool { spans.allSatisfy { isBlank($0.text) } }

    /// Leading and trailing whitespace of a paragraph goes (HWP indents with spaces).
    static func trimmed(_ spans: [Span]) -> [Span] {
        var out = merged(spans)
        while let first = out.first, isBlank(first.text) { out.removeFirst() }
        while let last = out.last, isBlank(last.text) { out.removeLast() }
        if !out.isEmpty {
            out[0].text = String(out[0].text.drop { $0.isWhitespace })
            out[out.count - 1].text = String(out[out.count - 1].text.reversed().drop { $0.isWhitespace }.reversed())
        }
        return out
    }

    static func plain(_ spans: [Span]) -> String { trimmed(spans).map(\.text).joined() }
}

// MARK: - Plain text (the same rules as the Windows document model)

extension DocLang.Document {
    var plainText: String {
        var parts = blocks.map { DocLang.plain($0, depth: 0) }
        if !footnotes.isEmpty {
            parts.append(footnotes.enumerated().map { index, note in
                "\(index + 1). " + note.map { DocLang.plain($0, depth: 0) }.filter { !$0.isEmpty }.joined(separator: " ")
            }.joined(separator: "\n"))
        }
        let text = parts.filter { !$0.isEmpty }.joined(separator: "\n\n").trimmingCharacters(in: .whitespacesAndNewlines)
        return text.isEmpty ? "" : text + "\n"
    }
}

extension DocLang {
    static func plain(_ block: Block, depth: Int) -> String {
        switch block {
        case .paragraph(let spans), .heading(_, let spans):
            return plain(spans)
        case .list(let ordered, let items):
            return items.enumerated().map { index, item in
                let lines = item.map { plain($0, depth: depth + 1) }.filter { !$0.isEmpty }
                return String(repeating: "  ", count: depth) + (ordered ? "\(index + 1). " : "• ") + lines.joined(separator: "\n")
            }.joined(separator: "\n")
        case .table(let table):
            var lines: [String] = []
            if !isBlank(table.caption) { lines.append(plain(table.caption)) }
            for row in table.rows {
                lines.append(row.map { cell in cellText(cell) }.joined(separator: "\t"))
            }
            return lines.joined(separator: "\n")
        case .picture:
            return ""
        case .formula(let text):
            return text
        }
    }

    static func cellText(_ cell: Cell?) -> String {
        guard let cell else { return "" }
        return cell.blocks.map { plain($0, depth: 0) }.filter { !$0.isEmpty }.joined(separator: " ")
            .replacingOccurrences(of: "\n", with: " ")
    }
}

// MARK: - Markdown (pictures stay out, like every other document → Markdown conversion)

extension DocLang.Document {
    var markdown: String {
        var parts = blocks.map { DocLang.markdown($0) }
        if !footnotes.isEmpty {
            parts.append("---")
            parts.append(footnotes.enumerated().map { index, note in
                "\(index + 1). " + note.map { DocLang.markdown($0) }.filter { !$0.isEmpty }.joined(separator: " ")
            }.joined(separator: "\n"))
        }
        let text = parts.filter { !$0.isEmpty }.joined(separator: "\n\n").trimmingCharacters(in: .whitespacesAndNewlines)
        return text.isEmpty ? "" : text + "\n"
    }
}

extension DocLang {
    static func markdown(_ block: Block) -> String {
        switch block {
        case .paragraph(let spans):
            return guardedStart(inlineMarkdown(trimmed(spans), lineBreak: "  \n"))
        case .heading(let level, let spans):
            return String(repeating: "#", count: level) + " " + inlineMarkdown(trimmed(spans), lineBreak: " ")
        case .list(let ordered, let items):
            return items.enumerated().map { index, item in
                let marker = ordered ? "\(index + 1). " : "- "
                let pad = String(repeating: " ", count: marker.count)
                let body = item.map { markdown($0) }.filter { !$0.isEmpty }.joined(separator: "\n\n")
                return marker + body.replacingOccurrences(of: "\n", with: "\n" + pad)
            }.joined(separator: "\n")
        case .table(let table):
            let width = table.width
            guard width > 0 else { return "" }
            func row(_ cells: [Cell?]) -> String {
                let texts = (0..<width).map { index -> String in
                    guard index < cells.count, let cell = cells[index] else { return "" }
                    return cell.blocks.map { cellMarkdown($0) }.filter { !$0.isEmpty }.joined(separator: "<br>")
                }
                return "| " + texts.joined(separator: " | ") + " |"
            }
            var lines: [String] = []
            if !isBlank(table.caption) { lines.append("*" + inlineMarkdown(trimmed(table.caption), lineBreak: " ") + "*\n") }
            lines.append(row(table.rows[0]))
            lines.append("|" + String(repeating: " --- |", count: width))
            lines += table.rows.dropFirst().map(row)
            return lines.joined(separator: "\n")
        case .picture:
            return ""
        case .formula(let text):
            let fence = text.contains("`") ? "`` " : "`"
            return fence + text + String(fence.reversed())
        }
    }

    /// A cell holds one line in a Markdown table: breaks become <br>, pipes are escaped.
    static func cellMarkdown(_ block: Block) -> String {
        switch block {
        case .paragraph(let spans), .heading(_, let spans):
            return inlineMarkdown(trimmed(spans), lineBreak: "<br>").replacingOccurrences(of: "|", with: "\\|")
        case .list, .table, .formula:
            return plain(block, depth: 0).replacingOccurrences(of: "\n", with: "<br>").replacingOccurrences(of: "|", with: "\\|")
        case .picture:
            return ""
        }
    }

    static func escapeMarkdown(_ text: String) -> String {
        var out = ""
        for character in text {
            if "\\*_`[]".contains(character) { out.append("\\") }
            out.append(character)
        }
        return out
    }

    /// A paragraph that starts like a heading, quote, list or rule stays a paragraph.
    static func guardedStart(_ text: String) -> String {
        if text.hasPrefix("#") || text.hasPrefix(">") || text.hasPrefix("- ") || text.hasPrefix("+ ")
            || text.range(of: "^[0-9]+[.)] ", options: .regularExpression) != nil {
            return "\\" + text
        }
        return text
    }

    static func inlineMarkdown(_ spans: [Span], lineBreak: String) -> String {
        spans.map { span in
            let body = escapeMarkdown(span.text).replacingOccurrences(of: "\n", with: lineBreak)
            let core = body.trimmingCharacters(in: .whitespaces)
            guard !core.isEmpty else { return body }
            let leading = String(body.prefix { $0 == " " })
            let trailing = String(body.reversed().prefix { $0 == " " })
            var styled = core
            if span.strike { styled = "~~\(styled)~~" }
            if span.italic { styled = "*\(styled)*" }
            if span.bold { styled = "**\(styled)**" }
            if let link = span.link, !link.isEmpty { styled = "[\(styled)](\(link.replacingOccurrences(of: " ", with: "%20")))" }
            return leading + styled + trailing
        }.joined()
    }
}

// MARK: - HTML (self-contained: pictures are embedded)

extension DocLang.Document {
    func html(title: String) -> String {
        var body = blocks.map { DocLang.html($0) }.filter { !$0.isEmpty }
        if !footnotes.isEmpty {
            body.append("<hr>\n<ol class=\"notes\">" + footnotes.map { note in
                "<li>" + note.map { DocLang.html($0) }.joined() + "</li>"
            }.joined() + "</ol>")
        }
        return """
        <!DOCTYPE html>
        <html><head><meta charset="utf-8"><title>\(Markdown.escape(title))</title>
        <style>
        body { font-family: -apple-system, "Apple SD Gothic Neo", "Malgun Gothic", "Noto Sans KR", sans-serif; font-size: 11pt; line-height: 1.6; color: #1d1d1f; max-width: 820px; margin: 2em auto; padding: 0 1em; }
        h1 { font-size: 20pt; } h2 { font-size: 16pt; } h3 { font-size: 13pt; }
        table { border-collapse: collapse; margin: 0.8em 0; } th, td { border: 1px solid #b8b8bd; padding: 4px 8px; vertical-align: top; }
        th { background: #f2f2f5; } caption { caption-side: top; font-style: italic; padding-bottom: 4px; }
        img { max-width: 100%; height: auto; } code { font-family: Menlo, Consolas, monospace; }
        ol.notes { font-size: 9.5pt; color: #444; }
        </style></head><body>
        \(body.joined(separator: "\n"))
        </body></html>
        """
    }
}

extension DocLang {
    static let webImages: [String: String] = ["png": "image/png", "jpg": "image/jpeg", "jpeg": "image/jpeg", "gif": "image/gif",
                                              "bmp": "image/bmp", "webp": "image/webp", "svg": "image/svg+xml"]

    static func html(_ block: Block) -> String {
        switch block {
        case .paragraph(let spans):
            return "<p>" + inlineHTML(trimmed(spans)) + "</p>"
        case .heading(let level, let spans):
            return "<h\(level)>" + inlineHTML(trimmed(spans)) + "</h\(level)>"
        case .list(let ordered, let items):
            let tag = ordered ? "ol" : "ul"
            return "<\(tag)>" + items.map { item in "<li>" + itemHTML(item) + "</li>" }.joined() + "</\(tag)>"
        case .table(let table):
            var out = "<table>"
            if !isBlank(table.caption) { out += "<caption>" + inlineHTML(trimmed(table.caption)) + "</caption>" }
            for row in table.rows {
                out += "<tr>"
                for cell in row.compactMap({ $0 }) {
                    let tag = cell.header ? "th" : "td"
                    var attributes = ""
                    if cell.columns > 1 { attributes += " colspan=\"\(cell.columns)\"" }
                    if cell.rows > 1 { attributes += " rowspan=\"\(cell.rows)\"" }
                    out += "<\(tag)\(attributes)>" + itemHTML(cell.blocks) + "</\(tag)>"
                }
                out += "</tr>"
            }
            return out + "</table>"
        case .picture(let data, let ext):
            guard let mime = webImages[ext] else { return "" }
            return "<p><img alt=\"\" src=\"data:\(mime);base64,\(data.base64EncodedString())\"></p>"
        case .formula(let text):
            return "<p><code>" + Markdown.escape(text) + "</code></p>"
        }
    }

    /// Inside list items and table cells, paragraphs become lines rather than spaced-out blocks.
    static func itemHTML(_ blocks: [Block]) -> String {
        var out = ""
        var lines: [String] = []
        func flush() {
            if !lines.isEmpty { out += lines.joined(separator: "<br>"); lines = [] }
        }
        for block in blocks {
            switch block {
            case .paragraph(let spans), .heading(_, let spans):
                lines.append(inlineHTML(trimmed(spans)))
            default:
                flush()
                out += html(block)
            }
        }
        flush()
        return out
    }

    static func inlineHTML(_ spans: [Span]) -> String {
        spans.map { span in
            var text = Markdown.escape(span.text).replacingOccurrences(of: "\n", with: "<br>")
            if span.isSubscript { text = "<sub>\(text)</sub>" }
            if span.isSuperscript { text = "<sup>\(text)</sup>" }
            if span.strike { text = "<s>\(text)</s>" }
            if span.underline { text = "<u>\(text)</u>" }
            if span.italic { text = "<em>\(text)</em>" }
            if span.bold { text = "<strong>\(text)</strong>" }
            if let link = span.link, !link.isEmpty {
                text = "<a href=\"\(Markdown.escape(link).replacingOccurrences(of: "\"", with: "&quot;"))\">\(text)</a>"
            }
            return text
        }.joined()
    }
}

