import AppKit

/// Rich text documents through NSAttributedString (DOCX, RTF, ODT, HTML, TXT) plus Markdown both ways.
enum DocumentEngine {
    static func convert(_ src: URL, to ext: String, ctx: PickContext) async throws -> URL {
        let dst = ctx.destination(for: src, ext: ext)
        return try await ctx.produce(dst) { tmp in
            try await MainActor.run {
                let sourceExt = FileNames.normalizedExt(src)
                if sourceExt == "md", ext == "html" {
                    let markdown = try readText(src)
                    try Markdown.html(markdown, title: FileNames.split(src.lastPathComponent).base)
                        .write(to: tmp, atomically: true, encoding: .utf8)
                    return
                }
                try write(try read(src), to: tmp, ext: ext)
            }
        }
    }

    static func readText(_ url: URL) throws -> String {
        var encoding = String.Encoding.utf8
        if let text = try? String(contentsOf: url, usedEncoding: &encoding) { return text }
        let data = try Data(contentsOf: url)
        let korean = String.Encoding(rawValue: CFStringConvertEncodingToNSStringEncoding(CFStringEncoding(CFStringEncodings.EUC_KR.rawValue)))
        if let text = String(data: data, encoding: korean) { return text }
        return String(decoding: data, as: UTF8.self)
    }

    @MainActor
    static func read(_ url: URL) throws -> NSAttributedString {
        let ext = FileNames.normalizedExt(url)
        let body: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: 12), .foregroundColor: NSColor.black]
        switch ext {
        case "md":
            let html = Markdown.html(try readText(url), title: url.lastPathComponent)
            return try NSAttributedString(data: Data(html.utf8),
                                          options: [.documentType: NSAttributedString.DocumentType.html,
                                                    .characterEncoding: String.Encoding.utf8.rawValue],
                                          documentAttributes: nil)
        case "txt":
            return NSAttributedString(string: try readText(url), attributes: body)
        default:
            return try NSAttributedString(url: url, options: [:], documentAttributes: nil)
        }
    }

    @MainActor
    static func write(_ attributed: NSAttributedString, to url: URL, ext: String) throws {
        let range = NSRange(location: 0, length: attributed.length)
        func data(_ type: NSAttributedString.DocumentType) throws -> Data {
            try attributed.data(from: range, documentAttributes: [.documentType: type])
        }
        switch ext {
        case "txt": try attributed.string.write(to: url, atomically: true, encoding: .utf8)
        case "md": try Markdown.from(attributed).write(to: url, atomically: true, encoding: .utf8)
        case "html": try data(.html).write(to: url)
        case "rtf": try data(.rtf).write(to: url)
        case "docx": try data(.officeOpenXML).write(to: url)
        case "odt": try data(.openDocument).write(to: url)
        case "doc": try data(.docFormat).write(to: url)
        case "pdf": try PDFRenderer.render(attributed, to: url)
        default: throw PickError(L("err.cant_save_as", ext.uppercased()))
        }
    }

    static func copyText(_ ctx: PickContext) async throws -> PickResult {
        let text = try await MainActor.run { () throws -> String in
            try ctx.files.map { try read($0).string }.joined(separator: "\n\n")
        }
        await MainActor.run {
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(text, forType: .string)
        }
        return .note(LP("msg.chars_copied", text.count))
    }

    static func count(_ ctx: PickContext) async throws -> PickResult {
        let text = try await MainActor.run { () throws -> String in
            try ctx.files.map { try read($0).string }.joined(separator: "\n")
        }
        return .note(countMessage(text))
    }

    /// "1,234 characters · 1,456 with spaces · 321 words · 12 lines" (HWP documents count the same way).
    static func countMessage(_ text: String) -> String {
        let characters = text.filter { !$0.isWhitespace }.count
        let words = text.split(whereSeparator: { $0.isWhitespace }).count
        let lines = text.split(separator: "\n", omittingEmptySubsequences: true).count
        let format: (Int) -> String = { $0.formatted(.number.locale(Loc.locale)) }
        return L("msg.count", format(characters), format(text.count), format(words), format(lines))
    }

    /// Plain text, Markdown or rich text as Markdown (rich text goes through NSAttributedString).
    @MainActor
    static func markdown(of url: URL) throws -> String {
        switch FileNames.normalizedExt(url) {
        case "md": return try readText(url)
        default: return Markdown.from(try read(url))
        }
    }
}

/// Paginates attributed text into an A4 PDF using the system print engine.
@MainActor
enum PDFRenderer {
    static func render(_ attributed: NSAttributedString, to url: URL) throws {
        let paper = NSSize(width: 595, height: 842)
        let margin: CGFloat = 56
        let info = NSPrintInfo(dictionary: [.jobDisposition: NSPrintInfo.JobDisposition.save, .jobSavingURL: url])
        info.paperSize = paper
        info.topMargin = margin
        info.bottomMargin = margin
        info.leftMargin = margin
        info.rightMargin = margin
        info.horizontalPagination = .fit
        info.verticalPagination = .automatic
        info.isHorizontallyCentered = false
        info.isVerticallyCentered = false

        let width = paper.width - margin * 2
        let textView = NSTextView(frame: NSRect(x: 0, y: 0, width: width, height: 100))
        textView.appearance = NSAppearance(named: .aqua)
        textView.drawsBackground = false
        textView.textContainerInset = .zero
        textView.textStorage?.setAttributedString(attributed)
        if let container = textView.textContainer, let layout = textView.layoutManager {
            container.containerSize = NSSize(width: width, height: .greatestFiniteMagnitude)
            container.widthTracksTextView = true
            layout.ensureLayout(for: container)
            let used = layout.usedRect(for: container)
            textView.frame = NSRect(x: 0, y: 0, width: width, height: max(used.height + 4, 20))
        }
        let operation = NSPrintOperation(view: textView, printInfo: info)
        operation.showsPrintPanel = false
        operation.showsProgressPanel = false
        guard operation.run() else { throw PickError(L("err.pdf_save")) }
    }
}

enum Markdown {
    static func escape(_ text: String) -> String {
        text.replacingOccurrences(of: "&", with: "&amp;")
            .replacingOccurrences(of: "<", with: "&lt;")
            .replacingOccurrences(of: ">", with: "&gt;")
    }

    static func replace(_ text: String, _ pattern: String, _ template: String) -> String {
        guard let regex = try? NSRegularExpression(pattern: pattern) else { return text }
        return regex.stringByReplacingMatches(in: text, range: NSRange(text.startIndex..., in: text), withTemplate: template)
    }

    static func inline(_ text: String) -> String {
        var out = escape(text)
        out = replace(out, "\u{60}([^\u{60}]+)\u{60}", "<code>$1</code>")
        out = replace(out, "!\\[([^\\]]*)\\]\\(([^)]+)\\)", "<img alt=\"$1\" src=\"$2\">")
        out = replace(out, "\\[([^\\]]+)\\]\\(([^)]+)\\)", "<a href=\"$2\">$1</a>")
        out = replace(out, "\\*\\*([^*]+)\\*\\*", "<strong>$1</strong>")
        out = replace(out, "__([^_]+)__", "<strong>$1</strong>")
        out = replace(out, "(?<![*\\w])\\*([^*\\n]+)\\*(?![*\\w])", "<em>$1</em>")
        out = replace(out, "(?<![\\w])_([^_\\n]+)_(?![\\w])", "<em>$1</em>")
        out = replace(out, "~~([^~]+)~~", "<del>$1</del>")
        return out
    }

    static func html(_ markdown: String, title: String) -> String {
        """
        <!DOCTYPE html>
        <html><head><meta charset="utf-8"><title>\(escape(title))</title>
        <style>
        body { font-family: -apple-system, "Apple SD Gothic Neo", sans-serif; font-size: 12pt; line-height: 1.6; color: #1d1d1f; max-width: 760px; margin: 2em auto; padding: 0 1em; }
        h1 { font-size: 22pt; } h2 { font-size: 17pt; } h3 { font-size: 14pt; }
        code { font-family: Menlo, monospace; font-size: 10.5pt; background: #f2f2f5; }
        pre { background: #f5f5f7; padding: 10px; }
        blockquote { color: #555; border-left: 3px solid #ccc; margin-left: 0; padding-left: 12px; }
        table { border-collapse: collapse; } th, td { border: 1px solid #ccc; padding: 4px 8px; }
        </style></head><body>
        \(body(markdown))
        </body></html>
        """
    }

    /// The converted blocks without the surrounding document.
    static func body(_ markdown: String) -> String {
        let lines = markdown.replacingOccurrences(of: "\r\n", with: "\n").components(separatedBy: "\n")
        var out: [String] = []
        var paragraph: [String] = []
        var listTag: String?
        func flush() {
            if !paragraph.isEmpty { out.append("<p>" + inline(paragraph.joined(separator: " ")) + "</p>"); paragraph = [] }
        }
        func closeList() {
            if let tag = listTag { out.append("</\(tag)>"); listTag = nil }
        }
        var index = 0
        while index < lines.count {
            let line = lines[index]
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            if trimmed.hasPrefix("\u{60}\u{60}\u{60}") {
                flush(); closeList()
                var code: [String] = []
                index += 1
                while index < lines.count, !lines[index].trimmingCharacters(in: .whitespaces).hasPrefix("\u{60}\u{60}\u{60}") {
                    code.append(escape(lines[index]))
                    index += 1
                }
                out.append("<pre><code>" + code.joined(separator: "\n") + "</code></pre>")
                index += 1
                continue
            }
            if trimmed.isEmpty { flush(); closeList(); index += 1; continue }
            if trimmed.hasPrefix("#") {
                let level = trimmed.prefix(while: { $0 == "#" }).count
                if level <= 6, trimmed.dropFirst(level).first == " " {
                    flush(); closeList()
                    out.append("<h\(level)>" + inline(String(trimmed.dropFirst(level + 1))) + "</h\(level)>")
                    index += 1
                    continue
                }
            }
            if ["---", "***", "___"].contains(trimmed) { flush(); closeList(); out.append("<hr>"); index += 1; continue }
            if trimmed.hasPrefix(">") {
                flush(); closeList()
                out.append("<blockquote><p>" + inline(String(trimmed.dropFirst()).trimmingCharacters(in: .whitespaces)) + "</p></blockquote>")
                index += 1
                continue
            }
            if trimmed.hasPrefix("|"), index + 1 < lines.count, lines[index + 1].contains("---"), lines[index + 1].contains("|") {
                flush(); closeList()
                func cells(_ row: String) -> [String] {
                    row.trimmingCharacters(in: .whitespaces).trimmingCharacters(in: CharacterSet(charactersIn: "|"))
                        .components(separatedBy: "|").map { $0.trimmingCharacters(in: .whitespaces) }
                }
                var table = "<table><tr>" + cells(trimmed).map { "<th>\(inline($0))</th>" }.joined() + "</tr>"
                index += 2
                while index < lines.count, lines[index].trimmingCharacters(in: .whitespaces).hasPrefix("|") {
                    table += "<tr>" + cells(lines[index]).map { "<td>\(inline($0))</td>" }.joined() + "</tr>"
                    index += 1
                }
                out.append(table + "</table>")
                continue
            }
            let bullet = ["- ", "* ", "+ "].first { trimmed.hasPrefix($0) }
            let ordered = trimmed.range(of: "^[0-9]+[.)] ", options: .regularExpression)
            if bullet != nil || ordered != nil {
                flush()
                let tag = bullet != nil ? "ul" : "ol"
                if listTag != tag { closeList(); out.append("<\(tag)>"); listTag = tag }
                let text = bullet != nil ? String(trimmed.dropFirst(2)) : String(trimmed[ordered!.upperBound...])
                var item = inline(text)
                if item.hasPrefix("[ ] ") { item = "☐ " + item.dropFirst(4) }
                if item.hasPrefix("[x] ") || item.hasPrefix("[X] ") { item = "☑︎ " + item.dropFirst(4) }
                out.append("<li>" + item + "</li>")
                index += 1
                continue
            }
            closeList()
            paragraph.append(trimmed)
            index += 1
        }
        flush(); closeList()
        return out.joined(separator: "\n")
    }

    /// Best-effort Markdown from rich text: headings by font size, bold/italic/links, lists.
    static func from(_ attributed: NSAttributedString) -> String {
        var result: [String] = []
        var listFlags: [Bool] = []
        let ns = attributed.string as NSString
        ns.enumerateSubstrings(in: NSRange(location: 0, length: ns.length), options: .byParagraphs) { _, range, _, _ in
            let paragraph = attributed.attributedSubstring(from: range)
            let plain = paragraph.string.trimmingCharacters(in: .whitespaces)
            guard !plain.isEmpty else { return }
            var largest: CGFloat = 0
            paragraph.enumerateAttribute(.font, in: NSRange(location: 0, length: paragraph.length)) { value, _, _ in
                if let font = value as? NSFont { largest = max(largest, font.pointSize) }
            }
            var prefix = ""
            if largest >= 21 { prefix = "# " } else if largest >= 16.5 { prefix = "## " } else if largest >= 14.5 { prefix = "### " }
            let styledList = (paragraph.attribute(.paragraphStyle, at: 0, effectiveRange: nil) as? NSParagraphStyle)?.textLists.isEmpty == false
            let isList = styledList || plain.range(of: "^([-*+•]|[0-9]+[.)])\\s", options: .regularExpression) != nil
            var body = ""
            paragraph.enumerateAttributes(in: NSRange(location: 0, length: paragraph.length)) { attrs, runRange, _ in
                var run = (paragraph.string as NSString).substring(with: runRange).replacingOccurrences(of: "\n", with: "")
                let core = run.trimmingCharacters(in: .whitespaces)
                guard !core.isEmpty else { body += run; return }
                let leading = String(run.prefix(while: { $0 == " " }))
                let trailing = String(run.reversed().prefix(while: { $0 == " " }))
                var styled = core
                if let link = attrs[.link] { styled = "[\(styled)](\(link))" }
                let traits = (attrs[.font] as? NSFont)?.fontDescriptor.symbolicTraits ?? []
                if prefix.isEmpty, traits.contains(.bold) { styled = "**\(styled)**" }
                if traits.contains(.italic) { styled = "*\(styled)*" }
                run = leading + styled + trailing
                body += run
            }
            if styledList {
                body = body.replacingOccurrences(of: "^\\s*([•◦▪︎\\-]|[0-9]+[.)])\\s*", with: "", options: .regularExpression)
                prefix = "- "
            }
            result.append(prefix + body.trimmingCharacters(in: .whitespaces))
            listFlags.append(isList)
        }
        var text = ""
        for (index, line) in result.enumerated() {
            if index > 0 { text += listFlags[index] && listFlags[index - 1] ? "\n" : "\n\n" }
            text += line
        }
        return text.trimmingCharacters(in: .whitespacesAndNewlines) + "\n"
    }
}
