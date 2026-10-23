import Foundation

/// Tables (CSV/TSV/JSON), subtitles and JSON formatting — plain text transforms, no dependencies.
enum DataEngine {
    // MARK: Tables

    static func parseDelimited(_ text: String, delimiter: Character) -> [[String]] {
        var rows: [[String]] = []
        var row: [String] = []
        var field = ""
        var quoted = false
        var chars = Array(text)
        if chars.first == "\u{FEFF}" { chars.removeFirst() }
        var index = 0
        while index < chars.count {
            let c = chars[index]
            if quoted {
                if c == "\"" {
                    if index + 1 < chars.count, chars[index + 1] == "\"" { field.append("\""); index += 1 } else { quoted = false }
                } else {
                    field.append(c)
                }
            } else if c == "\"" && field.isEmpty {
                quoted = true
            } else if c == delimiter {
                row.append(field); field = ""
            } else if c == "\n" || c == "\r\n" || c == "\r" {
                row.append(field); rows.append(row); row = []; field = ""
            } else {
                field.append(c)
            }
            index += 1
        }
        if !field.isEmpty || !row.isEmpty { row.append(field); rows.append(row) }
        return rows.filter { !($0.count == 1 && $0[0].isEmpty) }
    }

    static func readTable(_ url: URL) throws -> [[String]] {
        let ext = FileNames.normalizedExt(url)
        let text = try DocumentEngine.readText(url)
        if ext == "json" {
            guard let object = try? JSONSerialization.jsonObject(with: Data(text.utf8)) else { throw PickError(L("err.json_invalid")) }
            let records: [[String: Any]]
            if let array = object as? [[String: Any]] { records = array }
            else if let dict = object as? [String: Any], let array = dict.values.first(where: { $0 is [[String: Any]] }) as? [[String: Any]] { records = array }
            else { throw PickError(L("err.json_table")) }
            var keys: [String] = []
            for record in records { for key in record.keys.sorted() where !keys.contains(key) { keys.append(key) } }
            let rows = records.map { record in keys.map { stringify(record[$0]) } }
            return [keys] + rows
        }
        let delimiter: Character = ext == "tsv" ? "\t" : (text.prefix(2000).filter { $0 == ";" }.count > text.prefix(2000).filter { $0 == "," }.count ? ";" : ",")
        return parseDelimited(text, delimiter: delimiter)
    }

    static func stringify(_ value: Any?) -> String {
        switch value {
        case nil, is NSNull: return ""
        case let s as String: return s
        case let n as NSNumber: return n.stringValue
        case let other?:
            if JSONSerialization.isValidJSONObject(other), let data = try? JSONSerialization.data(withJSONObject: other) {
                return String(decoding: data, as: UTF8.self)
            }
            return "\(other)"
        }
    }

    static func writeTable(_ rows: [[String]], ext: String) throws -> String {
        let width = rows.map(\.count).max() ?? 0
        let padded = rows.map { $0 + Array(repeating: "", count: width - $0.count) }
        switch ext {
        case "csv", "tsv":
            let sep = ext == "csv" ? "," : "\t"
            // Excel on Windows reads a CSV as UTF-8 only with the byte-order mark.
            return (ext == "csv" ? "\u{FEFF}" : "") + padded.map { row in
                row.map { cell in
                    let needsQuotes = cell.contains(sep) || cell.contains("\"") || cell.contains("\n")
                    return needsQuotes ? "\"" + cell.replacingOccurrences(of: "\"", with: "\"\"") + "\"" : cell
                }.joined(separator: sep)
            }.joined(separator: "\n") + "\n"
        case "json":
            guard let header = padded.first else { return "[]" }
            let objects = padded.dropFirst().map { row in Dictionary(uniqueKeysWithValues: zip(uniqued(header), row)) }
            let data = try JSONSerialization.data(withJSONObject: Array(objects), options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])
            return String(decoding: data, as: UTF8.self) + "\n"
        case "md":
            guard let header = padded.first else { return "" }
            func line(_ cells: [String]) -> String {
                "| " + cells.map { $0.replacingOccurrences(of: "|", with: "\\|").replacingOccurrences(of: "\n", with: " ") }.joined(separator: " | ") + " |"
            }
            return ([line(header), "|" + Array(repeating: " --- |", count: header.count).joined()] + padded.dropFirst().map(line)).joined(separator: "\n") + "\n"
        case "html":
            guard let header = padded.first else { return "" }
            let head = header.map { "<th>\(Markdown.escape($0))</th>" }.joined()
            let body = padded.dropFirst().map { "<tr>" + $0.map { "<td>\(Markdown.escape($0))</td>" }.joined() + "</tr>" }.joined(separator: "\n")
            return """
            <!DOCTYPE html><html><head><meta charset="utf-8"><style>
            body { font-family: -apple-system, sans-serif; font-size: 13px; }
            table { border-collapse: collapse; } th, td { border: 1px solid #d0d0d5; padding: 5px 9px; text-align: left; }
            th { background: #f2f2f7; } tr:nth-child(even) td { background: #fafafc; }
            </style></head><body><table><tr>\(head)</tr>
            \(body)
            </table></body></html>
            """
        default:
            throw PickError(L("err.cant_save_as", ext.uppercased()))
        }
    }

    static func uniqued(_ header: [String]) -> [String] {
        var seen: [String: Int] = [:]
        return header.enumerated().map { index, raw in
            let name = raw.isEmpty ? "column\(index + 1)" : raw
            let n = seen[name, default: 0]
            seen[name] = n + 1
            return n == 0 ? name : "\(name)_\(n + 1)"
        }
    }

    static func convertTable(_ src: URL, to ext: String, ctx: PickContext) async throws -> URL {
        let rows = try readTable(src)
        guard !rows.isEmpty else { throw PickError(L("err.table_empty")) }
        let text = try writeTable(rows, ext: ext)
        return try await ctx.produce(ctx.destination(for: src, ext: ext)) { tmp in
            try text.write(to: tmp, atomically: true, encoding: .utf8)
        }
    }

    // MARK: JSON formatting (preserves key order — no re-serialization)

    static func reformatJSON(_ src: URL, pretty: Bool, ctx: PickContext) async throws -> URL {
        let text = try DocumentEngine.readText(src)
        guard (try? JSONSerialization.jsonObject(with: Data(text.utf8), options: [.fragmentsAllowed])) != nil else {
            throw PickError(L("err.json_invalid"))
        }
        var out = ""
        var depth = 0
        var inString = false
        var escaped = false
        let indent = "  "
        func newline() { out += "\n" + String(repeating: indent, count: depth) }
        var previous: Character?
        for c in text {
            if inString {
                out.append(c)
                if escaped { escaped = false } else if c == "\\" { escaped = true } else if c == "\"" { inString = false }
                previous = c
                continue
            }
            if c.isWhitespace { continue }
            switch c {
            case "\"":
                inString = true
                out.append(c)
            case "{", "[":
                out.append(c)
                depth += 1
                if pretty { newline() }
            case "}", "]":
                depth -= 1
                if pretty {
                    if previous == "{" || previous == "[" {
                        out = String(out.reversed().drop(while: { $0.isWhitespace }).reversed())
                    } else {
                        newline()
                    }
                }
                out.append(c)
            case ",":
                out.append(c)
                if pretty { newline() }
            case ":":
                out += pretty ? ": " : ":"
            default:
                out.append(c)
            }
            previous = c
        }
        if pretty { out += "\n" }
        return try await ctx.produce(ctx.destination(for: src, ext: "json", suffix: pretty ? L("sfx.pretty") : L("sfx.min"))) { tmp in
            try out.write(to: tmp, atomically: true, encoding: .utf8)
        }
    }

    // MARK: Subtitles

    struct Cue {
        var start: Double
        var end: Double
        var text: String
    }

    static func parseTime(_ raw: String) -> Double? {
        let cleaned = raw.trimmingCharacters(in: .whitespaces).replacingOccurrences(of: ",", with: ".")
        let parts = cleaned.split(separator: ":").map(String.init)
        guard (2...3).contains(parts.count) else { return nil }
        var seconds = 0.0
        for part in parts { guard let value = Double(part) else { return nil }; seconds = seconds * 60 + value }
        return seconds
    }

    static func parseSubtitles(_ text: String) -> [Cue] {
        let normalized = text.replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "\r", with: "\n")
        var cues: [Cue] = []
        for block in normalized.components(separatedBy: "\n\n") {
            let lines = block.components(separatedBy: "\n").filter { !$0.trimmingCharacters(in: .whitespaces).isEmpty }
            guard let timing = lines.firstIndex(where: { $0.contains("-->") }) else { continue }
            let sides = lines[timing].components(separatedBy: "-->")
            guard sides.count == 2, let start = parseTime(sides[0]),
                  let end = parseTime(sides[1].trimmingCharacters(in: .whitespaces).components(separatedBy: " ").first ?? "")
            else { continue }
            cues.append(Cue(start: start, end: end, text: lines[(timing + 1)...].joined(separator: "\n")))
        }
        return cues
    }

    static func stamp(_ seconds: Double, separator: String) -> String {
        let total = Int((seconds * 1000).rounded())
        return String(format: "%02d:%02d:%02d%@%03d", total / 3_600_000, (total / 60_000) % 60, (total / 1000) % 60, separator, total % 1000)
    }

    static func convertSubtitles(_ src: URL, to ext: String, ctx: PickContext) async throws -> URL {
        let text = try DocumentEngine.readText(src)
        let cues = ["smi", "sami"].contains(FileNames.normalizedExt(src)) ? parseSAMI(text) : parseSubtitles(text)
        guard !cues.isEmpty else { throw PickError(L("err.no_cues")) }
        let output: String
        switch ext {
        case "srt":
            output = cues.enumerated().map { i, cue in
                "\(i + 1)\n\(stamp(cue.start, separator: ",")) --> \(stamp(cue.end, separator: ","))\n\(cue.text)"
            }.joined(separator: "\n\n") + "\n"
        case "vtt":
            output = "WEBVTT\n\n" + cues.map { cue in
                "\(stamp(cue.start, separator: ".")) --> \(stamp(cue.end, separator: "."))\n\(cue.text)"
            }.joined(separator: "\n\n") + "\n"
        default:
            output = cues.map { $0.text.replacingOccurrences(of: "<[^>]+>", with: "", options: .regularExpression) }
                .joined(separator: "\n") + "\n"
        }
        return try await ctx.produce(ctx.destination(for: src, ext: ext)) { tmp in
            try output.write(to: tmp, atomically: true, encoding: .utf8)
        }
    }

    // MARK: SAMI (.smi), the subtitle format of Korean Windows players

    /// <SYNC Start=ms> blocks, one language: a Korean class (KRCC, KOCC, KR…, KO…) when there is one, otherwise the
    /// first class. <br> breaks a line, other tags go, entities are decoded. A block with nothing (or only &nbsp;) in it
    /// ends the cue before it; a cue otherwise runs to the next block, the last one for four seconds.
    static func parseSAMI(_ text: String) -> [Cue] {
        func regex(_ pattern: String) -> NSRegularExpression? {
            try? NSRegularExpression(pattern: pattern, options: [.caseInsensitive])
        }
        guard let sync = regex(#"<sync\b([^>]*)>"#), let start = regex(#"\bstart\s*=\s*["']?\s*(-?\d+)"#),
              let paragraph = regex(#"<p\b([^>]*)>"#), let klass = regex(#"\bclass\s*=\s*["']?([A-Za-z0-9_-]+)"#)
        else { return [] }
        let ns = text as NSString
        func first(_ expression: NSRegularExpression, in string: String) -> String? {
            let local = string as NSString
            guard let match = expression.firstMatch(in: string, range: NSRange(location: 0, length: local.length)) else { return nil }
            return local.substring(with: match.range(at: 1))
        }

        // Blocks: what follows each <SYNC> up to the next one (or </BODY>).
        let syncs = sync.matches(in: text, range: NSRange(location: 0, length: ns.length))
        var blocks: [(start: Double, body: String)] = []
        for (index, match) in syncs.enumerated() {
            guard let value = first(start, in: ns.substring(with: match.range(at: 1))), let ms = Double(value) else { continue }
            let from = match.range.location + match.range.length
            let to = index + 1 < syncs.count ? syncs[index + 1].range.location : ns.length
            var body = ns.substring(with: NSRange(location: from, length: max(0, to - from)))
            if let end = body.range(of: "</body", options: .caseInsensitive) { body = String(body[..<end.lowerBound]) }
            blocks.append((ms / 1000, body))
        }

        // Paragraphs of one block: (class, text) in order; text before the first <P> counts only when there is no <P>.
        func paragraphs(_ body: String) -> [(klass: String?, text: String)] {
            let local = body as NSString
            let matches = paragraph.matches(in: body, range: NSRange(location: 0, length: local.length))
            guard !matches.isEmpty else { return [(nil, body)] }
            return matches.enumerated().map { index, match in
                let from = match.range.location + match.range.length
                let to = index + 1 < matches.count ? matches[index + 1].range.location : local.length
                return (first(klass, in: local.substring(with: match.range(at: 1))),
                        local.substring(with: NSRange(location: from, length: max(0, to - from))))
            }
        }
        var classes: [String] = []
        for block in blocks {
            for (name, _) in paragraphs(block.body) {
                if let name, !classes.contains(where: { $0.caseInsensitiveCompare(name) == .orderedSame }) { classes.append(name) }
            }
        }
        let chosen = classes.first { ["KR", "KO"].contains(String($0.uppercased().prefix(2))) } ?? classes.first

        var entries: [(start: Double, text: String)] = []
        for block in blocks {
            let parts = paragraphs(block.body).filter { part in
                guard let chosen, let name = part.klass else { return true }
                return name.caseInsensitiveCompare(chosen) == .orderedSame
            }
            // A line in another language only: not part of this track.
            if parts.isEmpty { continue }
            entries.append((block.start, parts.map { samiText($0.text) }.filter { !$0.isEmpty }.joined(separator: "\n")))
        }
        let ordered = entries.enumerated().sorted { ($0.element.start, $0.offset) < ($1.element.start, $1.offset) }.map(\.element)
        var cues: [Cue] = []
        for (index, entry) in ordered.enumerated() where !entry.text.isEmpty {
            let end = index + 1 < ordered.count ? ordered[index + 1].start : entry.start + 4
            if end > entry.start { cues.append(Cue(start: entry.start, end: end, text: entry.text)) }
        }
        return cues
    }

    /// One SAMI paragraph as plain lines: source line breaks are spaces, <br> is a line break.
    static func samiText(_ html: String) -> String {
        var text = html.replacingOccurrences(of: "\r", with: " ").replacingOccurrences(of: "\n", with: " ")
            .replacingOccurrences(of: "\t", with: " ")
        text = text.replacingOccurrences(of: #"<br\s*/?>"#, with: "\n", options: [.regularExpression, .caseInsensitive])
        text = text.replacingOccurrences(of: #"<!--.*?-->"#, with: "", options: .regularExpression)
        text = text.replacingOccurrences(of: #"<[^>]*>"#, with: "", options: .regularExpression)
        text = decodeEntities(text)
        return text.components(separatedBy: "\n")
            .map { $0.replacingOccurrences(of: #"\s+"#, with: " ", options: .regularExpression).trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty }
            .joined(separator: "\n")
    }

    static func decodeEntities(_ text: String) -> String {
        guard text.contains("&") else { return text }
        let named: [String: String] = ["nbsp": " ", "amp": "&", "lt": "<", "gt": ">", "quot": "\"", "apos": "'", "#39": "'"]
        var out = ""
        var rest = Substring(text)
        while let amp = rest.firstIndex(of: "&") {
            out += rest[..<amp]
            let after = rest[rest.index(after: amp)...]
            if let semi = after.prefix(10).firstIndex(of: ";") {
                let name = String(after[..<semi])
                var replacement: String?
                if let value = named[name.lowercased()] {
                    replacement = value
                } else if name.hasPrefix("#") {
                    let digits = name.dropFirst()
                    let code = digits.lowercased().hasPrefix("x") ? UInt32(digits.dropFirst(), radix: 16) : UInt32(digits)
                    replacement = code.flatMap(Unicode.Scalar.init).map { String(Character($0)) }
                }
                if let replacement {
                    out += replacement
                    rest = after[after.index(after: semi)...]
                    continue
                }
            }
            out += "&"
            rest = after
        }
        return out + rest
    }
}
