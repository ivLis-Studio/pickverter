import Foundation

/// Text that crosses between Windows and the Mac: Korean Windows saves plain text as CP949 (EUC-KR), and Excel only
/// reads a CSV as UTF-8 when it starts with a byte-order mark.
enum TextCodec {
    static let cp949 = String.Encoding(rawValue: CFStringConvertEncodingToNSStringEncoding(
        CFStringEncoding(CFStringEncodings.dosKorean.rawValue)))
    static let cp437 = String.Encoding(rawValue: CFStringConvertEncodingToNSStringEncoding(
        CFStringEncoding(CFStringEncodings.dosLatinUS.rawValue)))
    static let bom: [UInt8] = [0xEF, 0xBB, 0xBF]

    static func encoding(_ value: CFStringEncodings) -> String.Encoding {
        String.Encoding(rawValue: CFStringConvertEncodingToNSStringEncoding(CFStringEncoding(value.rawValue)))
    }

    /// What Windows wrote plain text in for Japanese, Chinese and Russian: tried before CP949, since Shift-JIS, GBK,
    /// Big5 or Windows-1251 text often passes as CP949 and would come out as nonsense.
    static var regionalText: String.Encoding? {
        switch Loc.language {
        case "ja": return encoding(.dosJapanese)
        case "zh-Hans": return encoding(.dosChineseSimplif)
        case "zh-Hant": return encoding(.dosChineseTrad)
        case "ru": return .windowsCP1251
        default: return nil
        }
    }

    /// The same for ZIP names, which Windows writes in the DOS code page (866 for Russian).
    static var regionalZipNames: String.Encoding? {
        Loc.language == "ru" ? encoding(.dosRussian) : regionalText
    }

    /// Files the "Fix Encoding" tool looks at.
    static let fixableExts: Set<String> = ["txt", "text", "md", "markdown", "csv", "tsv", "srt", "vtt", "smi", "sami", "json",
                                           "html", "htm"]
    /// How much of a file decides whether it needs fixing.
    static let sniffLength = 256 * 1024

    // MARK: Reading

    /// Byte-order mark first (UTF-8, UTF-16 LE/BE), then strict UTF-8, then CP949 (after the interface language's own
    /// legacy encoding for Japanese, Chinese and Russian), then the best partial reading: CP949 line by line with a
    /// Korean interface, Windows-1252 with a Latin one, UTF-8 with replacement marks otherwise.
    static func decode(_ data: Data) -> String {
        let head = [UInt8](data.prefix(3))
        if head.starts(with: bom) { return String(decoding: data.dropFirst(3), as: UTF8.self) }
        if head.starts(with: [0xFF, 0xFE]) {
            return String(data: Data(data.dropFirst(2)), encoding: .utf16LittleEndian) ?? String(decoding: data, as: UTF8.self)
        }
        if head.starts(with: [0xFE, 0xFF]) {
            return String(data: Data(data.dropFirst(2)), encoding: .utf16BigEndian) ?? String(decoding: data, as: UTF8.self)
        }
        if let text = String(data: data, encoding: .utf8) { return text }
        if let regional = regionalText, let text = String(data: data, encoding: regional) { return text }
        if let text = String(data: data, encoding: cp949) { return text }
        switch Loc.language {
        case "ko": return decodeCP949(data)
        case "ja", "zh-Hans", "zh-Hant", "ru": break
        default: if let text = String(data: data, encoding: .windowsCP1252) { return text }
        }
        return String(decoding: data, as: UTF8.self)
    }

    static func read(_ url: URL) throws -> String {
        decode(try Data(contentsOf: url))
    }

    static func hasBOM(_ data: Data) -> Bool {
        let head = [UInt8](data.prefix(3))
        return head.starts(with: bom) || head.starts(with: [0xFF, 0xFE]) || head.starts(with: [0xFE, 0xFF])
    }

    /// Strict UTF-8. With cut set the bytes are the start of a longer file, so a character split by the cut is fine.
    static func isUTF8(_ data: Data, cut: Bool = false) -> Bool {
        if String(data: data, encoding: .utf8) != nil { return true }
        guard cut else { return false }
        // A multi-byte character can lose up to three bytes at the cut.
        for drop in 1...3 where data.count > drop {
            guard data.suffix(drop).contains(where: { $0 >= 0xC0 }) else { continue }
            if String(data: Data(data.dropLast(drop)), encoding: .utf8) != nil { return true }
        }
        return false
    }

    static func isCP949(_ data: Data, cut: Bool = false) -> Bool {
        if String(data: data, encoding: cp949) != nil { return true }
        return cut && data.count > 1 && String(data: Data(data.dropLast()), encoding: cp949) != nil
    }

    private static let cacheLock = NSLock()
    nonisolated(unsafe) private static var cache: [String: Bool] = [:]

    /// True for a text file that isn't UTF-8 but reads as CP949: what "Fix Encoding" is for. Remembered per file
    /// version, since the wheel asks every time it opens.
    static func needsUTF8(_ url: URL) -> Bool {
        guard fixableExts.contains(url.pathExtension.lowercased()),
              let values = try? url.resourceValues(forKeys: [.fileSizeKey, .contentModificationDateKey]),
              let size = values.fileSize, size > 0 else { return false }
        let key = "\(url.path)|\(size)|\(values.contentModificationDate?.timeIntervalSince1970 ?? 0)"
        if let known = cacheLock.withLock({ cache[key] }) { return known }
        let result = sniff(url)
        cacheLock.withLock {
            if cache.count > 256 { cache.removeAll() }
            cache[key] = result
        }
        return result
    }

    private static func sniff(_ url: URL) -> Bool {
        guard let handle = try? FileHandle(forReadingFrom: url) else { return false }
        defer { try? handle.close() }
        guard let chunk = try? handle.read(upToCount: sniffLength + 1), !chunk.isEmpty else { return false }
        let cut = chunk.count > sniffLength
        let head = cut ? Data(chunk.prefix(sniffLength)) : chunk
        if hasBOM(head) || isUTF8(head, cut: cut) { return false }
        // Japanese, Chinese or Russian text from that language's Windows often passes as CP949 too: leave it alone.
        if let regional = regionalText, String(data: cut ? Data(head.dropLast()) : head, encoding: regional) != nil { return false }
        return isCP949(head, cut: cut)
    }

    /// CP949 for the whole file; a file that is mostly CP949 with a few odd lines is decoded line by line.
    static func decodeCP949(_ data: Data) -> String {
        if let text = String(data: data, encoding: cp949) { return text }
        var lines: [String] = []
        for line in data.split(separator: 0x0A, omittingEmptySubsequences: false) {
            let bytes = Data(line)
            lines.append(String(data: bytes, encoding: cp949) ?? String(data: bytes, encoding: .utf8)
                ?? String(decoding: bytes, as: UTF8.self))
        }
        return lines.joined(separator: "\n")
    }

    // MARK: Writing

    /// UTF-8 with a byte-order mark for CSV (Excel on Windows reads it as UTF-8 only then), plain UTF-8 otherwise.
    static func encode(_ text: String, ext: String) -> Data {
        var data = Data()
        if ext == "csv", !text.hasPrefix("\u{FEFF}") { data.append(contentsOf: bom) }
        data.append(Data(text.utf8))
        return data
    }

    /// Adds the byte-order mark to a CSV written by another app (Numbers, LibreOffice), or to every CSV in a folder.
    static func addBOM(to url: URL) {
        let fm = FileManager.default
        var isDir: ObjCBool = false
        guard fm.fileExists(atPath: url.path, isDirectory: &isDir) else { return }
        if isDir.boolValue {
            for name in (try? fm.contentsOfDirectory(atPath: url.path)) ?? [] where name.lowercased().hasSuffix(".csv") {
                addBOM(to: url.appendingPathComponent(name))
            }
            return
        }
        guard let data = try? Data(contentsOf: url), !data.starts(with: bom) else { return }
        // Only UTF-8 gets the mark: a file in a legacy encoding would be misread as UTF-8 then.
        guard String(data: data, encoding: .utf8) != nil else { return }
        try? (Data(bom) + data).write(to: url)
    }

    /// "Fix Encoding": the same file rewritten as UTF-8 ("notes UTF-8.txt"). Files that are already fine are skipped.
    static func fixEncoding(_ src: URL, ctx: PickContext) async throws -> URL {
        let data = try Data(contentsOf: src)
        guard !hasBOM(data), String(data: data, encoding: .utf8) == nil else { throw PickSkip() }
        var text = decodeCP949(data)
        let ext = FileNames.split(src.lastPathComponent).ext
        if ["html", "htm"].contains(ext) { text = declareUTF8(text) }
        let output = encode(text, ext: FileNames.normalizedExt(src))
        return try await ctx.produce(ctx.destination(for: src, ext: ext, suffix: L("sfx.utf8"))) { tmp in
            try output.write(to: tmp)
        }
    }

    /// A page that declared EUC-KR has to say UTF-8 once its bytes are UTF-8.
    static func declareUTF8(_ html: String) -> String {
        guard let regex = try? NSRegularExpression(pattern: #"(<meta\b[^>]*?charset\s*=\s*["']?)([A-Za-z0-9._:-]+)"#,
                                                   options: [.caseInsensitive]) else { return html }
        let range = NSRange(html.startIndex..., in: html)
        return regex.stringByReplacingMatches(in: html, range: range, withTemplate: "$1utf-8")
    }

    /// The encoding to tell the HTML reader when a page doesn't declare one (it would guess Latin-1 otherwise).
    static func htmlEncoding(_ data: Data) -> String.Encoding? {
        let head = String(decoding: data.prefix(4096), as: UTF8.self).lowercased()
        if head.contains("charset") || hasBOM(data) { return nil }
        if String(data: data, encoding: .utf8) != nil { return .utf8 }
        if String(data: data, encoding: cp949) != nil { return cp949 }
        return nil
    }
}
