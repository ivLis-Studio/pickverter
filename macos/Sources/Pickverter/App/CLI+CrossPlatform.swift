import AppKit

/// Self-test for files moving between Macs and Windows PCs and for the result previews: SAMI subtitles, CP949 text,
/// ZIP names and clutter, split letters in names, the hub's size estimate and the "Under …" sizes.
extension CLI {
    static func crossPlatformTests(_ files: [URL]) -> Int {
        var failures = 0
        let fm = FileManager.default
        let scratch = fm.temporaryDirectory.appendingPathComponent("pickverter-selftest-\(UUID().uuidString)", isDirectory: true)
        try? fm.createDirectory(at: scratch, withIntermediateDirectories: true)
        defer { try? fm.removeItem(at: scratch) }
        let nfd = { (text: String) in text.decomposedStringWithCanonicalMapping }
        let bytes = { (text: String) in [UInt8](text.utf8) }

        // SAMI: the Korean class wins, &nbsp; ends a cue, the last one lasts four seconds.
        let sami = """
        <SAMI><BODY>
        <SYNC Start=1000><P Class=ENCC>Hello
        <SYNC Start=1000><P Class=KRCC>안녕<br>세상 &amp; 친구
        <SYNC Start=3500><P Class=KRCC>&nbsp;
        <SYNC Start="5000"><P Class=KRCC>두 번째
        </BODY></SAMI>
        """
        let cues = DataEngine.parseSAMI(sami)
        let srt = cues.map { "\(DataEngine.stamp($0.start, separator: ",")) \(DataEngine.stamp($0.end, separator: ",")) \($0.text)" }
        check("SAMI → cues", srt.joined(separator: " | "),
              "00:00:01,000 00:00:03,500 안녕\n세상 & 친구 | 00:00:05,000 00:00:09,000 두 번째", &failures)

        // CP949 text from Korean Windows.
        let korean = "한글 텍스트, 똠방각하".data(using: TextCodec.cp949) ?? Data()
        check("CP949 decodes", TextCodec.decode(korean), "한글 텍스트, 똠방각하", &failures)
        let legacy = scratch.appendingPathComponent("legacy.txt")
        let modern = scratch.appendingPathComponent("modern.txt")
        try? korean.write(to: legacy)
        try? Data("한글".utf8).write(to: modern)
        check("Fix Encoding offered for CP949 only", "\(TextCodec.needsUTF8(legacy)) \(TextCodec.needsUTF8(modern))", "true false", &failures)
        let cut = Data("가나다".utf8).dropLast()
        check("a character cut at 256 KB still counts as UTF-8", "\(TextCodec.isUTF8(Data(cut), cut: true))", "true", &failures)
        check("CSV gets a byte-order mark", "\((try? DataEngine.writeTable([["이름"]], ext: "csv"))?.hasPrefix("\u{FEFF}") == true)", "true", &failures)

        // ZIP: composed UTF-8 names with the flag, no clutter, empty folders kept, and back again.
        let folder = scratch.appendingPathComponent(nfd("자료"), isDirectory: true)
        try? fm.createDirectory(at: folder.appendingPathComponent(nfd("빈 폴더")), withIntermediateDirectories: true)
        try? Data(String(repeating: "안녕 ", count: 200).utf8).write(to: folder.appendingPathComponent(nfd("메모.txt")))
        try? Data("x".utf8).write(to: folder.appendingPathComponent(".DS_Store"))
        try? Data("x".utf8).write(to: folder.appendingPathComponent("._" + nfd("메모.txt")))
        let archive = scratch.appendingPathComponent("test.zip")
        do {
            let writer = try ZipWriter(url: archive)
            try writer.add(folder)
            try writer.finish()
            let entries = try ZipReader.entries(archive)
            check("ZIP names", entries.map(\.name).joined(separator: " "), "자료/ 자료/메모.txt 자료/빈 폴더/", &failures)
            check("…composed and flagged UTF-8", "\(entries.allSatisfy { bytes($0.name) == bytes($0.name.precomposedStringWithCanonicalMapping) && $0.flags & 0x800 != 0 })",
                  "true", &failures)
            let out = scratch.appendingPathComponent("out", isDirectory: true)
            try fm.createDirectory(at: out, withIntermediateDirectories: true)
            try ZipReader.extract(archive, into: out)
            let text = try String(contentsOf: out.appendingPathComponent("자료/메모.txt"), encoding: .utf8)
            check("ZIP round trip", "\(text.count) \(fm.fileExists(atPath: out.appendingPathComponent("자료/빈 폴더").path))", "600 true", &failures)
        } catch {
            failures += 1
            print("FAIL ZIP: \(error.localizedDescription)")
        }

        // ZIP from Korean Windows: CP949 names without the flag; climbing out with .. is refused.
        let windows = scratch.appendingPathComponent("windows.zip")
        writeRawZip(windows, names: [("보고서.txt".data(using: TextCodec.cp949) ?? Data(), Data("a".utf8)), (Data("../evil.txt".utf8), Data("b".utf8))])
        let extracted = scratch.appendingPathComponent("windows", isDirectory: true)
        try? fm.createDirectory(at: extracted, withIntermediateDirectories: true)
        try? ZipReader.extract(windows, into: extracted)
        check("CP949 names read right", ((try? fm.contentsOfDirectory(atPath: extracted.path)) ?? []).joined(separator: " "), "보고서.txt", &failures)
        check("…and nothing lands outside", "\(fm.fileExists(atPath: scratch.appendingPathComponent("evil.txt").path))", "false", &failures)

        // Split letters: Hangul jamo and kana marks join, accents alone don't count.
        check("split Hangul", NameFixer.composed(bytes(nfd("한글.txt"))).map { String(decoding: $0, as: UTF8.self) } ?? "nil", "한글.txt", &failures)
        check("split kana", NameFixer.composed(bytes(nfd("ポスト"))).map { String(decoding: $0, as: UTF8.self) } ?? "nil", "ポスト", &failures)
        check("accents alone are left", NameFixer.composed(bytes(nfd("café"))) == nil ? "left" : "joined", "left", &failures)
        let names = scratch.appendingPathComponent("names", isDirectory: true)
        try? fm.createDirectory(at: names, withIntermediateDirectories: true)
        let rawFolder = NameFixer.pathBytes(names)
        _ = NameFixer.withCPath(rawFolder + [NameFixer.slash] + bytes(nfd("가나.txt"))) { creat($0, 0o644) }
        let fixed = (try? wait {
            try await NameFixer.fix(PickContext(files: [names], location: .sameFolder) { _, _ in })
        })?.message ?? "nil"
        check("names inside a folder join", "\(fixed == LP("msg.renamed", 1)) \(NameFixer.rawNames(in: rawFolder) == [bytes("가나.txt")])",
              "true true", &failures)

        // Hub estimate and "Under …" sizes.
        check("estimate: smaller", SizeEstimator.change(from: 1000, to: 290), "\u{2212}71%", &failures)
        check("estimate: larger", SizeEstimator.change(from: 1000, to: 1120), "+12%", &failures)
        check("custom size suggestion", "\(TargetSize.suggestion(for: 50_000_000)) \(TargetSize.suggestion(for: 7_300_000)) \(TargetSize.suggestion(for: 900_000))",
              "25.0 3.7 0.45", &failures)
        check("custom size typing", "\(TargetSize.megabytes("1,5") ?? 0) \(TargetSize.megabytes("2 MB") ?? 0) \(TargetSize.megabytes("1,5 Mo") ?? 0) \(TargetSize.megabytes("abc") == nil)",
              "1.5 2.0 1.5 true", &failures)
        let photo = [URL(fileURLWithPath: "/tmp/pickverter-symbols/a.jpg")]
        let compress = Catalog.options(for: photo, mode: .tools).first { $0.id == "tool.compress" }
        check("Compress offers sizes", compress?.variants.map(\.id).filter { $0.contains(".max") }.joined(separator: " ") ?? "",
              "tool.compress.max500k tool.compress.max1m tool.compress.max2m tool.compress.max5m tool.compress.maxcustom", &failures)
        failures += estimateTests(files)
        return failures
    }

    /// The hub's size preview, wired like a live wheel: it follows the highlight and leaves no files behind.
    static func estimateTests(_ files: [URL]) -> Int {
        var failures = 0
        guard let photo = files.first, FileKind.of(photo) == .image else { return 0 }
        let cache = ClipboardStore.root
        let before = (try? FileManager.default.contentsOfDirectory(atPath: cache.path))?.count ?? 0
        let wheel = WheelController(demo: true)
        wheel.previewsSizes = true
        wheel.present(files: [photo], origin: .selection, mode: .tools, at: CGPoint(x: 600, y: 500))
        wheel.forceVisibleForTesting()
        guard let compress = wheel.items.firstIndex(where: { $0.id == "tool.compress" }),
              let rotate = wheel.items.firstIndex(where: { $0.id == "tool.rotate" }) else { return 1 }
        wheel.demoHighlight(compress)
        let deadline = Date(timeIntervalSinceNow: 15)
        while Date() < deadline, wheel.estimator.state == .none || wheel.estimator.state == .working { pump(0.05) }
        var line = ""
        if case .result(let text) = wheel.estimator.state { line = text }
        check("the hub sizes Compress", line.hasPrefix("≈") ? "sized" : "\(wheel.estimator.state)", "sized", &failures)
        wheel.demoHighlight(rotate)
        check("…and lets go on Rotate", "\(wheel.estimator.state)", "none", &failures)
        wheel.demoHighlight(compress)
        check("…and remembers it", "\(wheel.estimator.state)", "\(SizeEstimator.State.result(line))", &failures)
        pump(0.3)
        let after = (try? FileManager.default.contentsOfDirectory(atPath: cache.path))?.count ?? 0
        check("…without leaving files", "\(after <= before)", "true", &failures)
        return failures
    }

    /// A ZIP the way old Windows tools write it: stored entries, names in whatever bytes they had, no UTF-8 flag.
    static func writeRawZip(_ url: URL, names entries: [(name: Data, data: Data)]) {
        var body = Data()
        var directory = Data()
        for (name, data) in entries {
            let crc = CRC32.of(data)
            let offset = UInt32(body.count)
            var header = ZipKit.le32(0x0403_4B50) + ZipKit.le16(20) + ZipKit.le16(0) + ZipKit.le16(0) + ZipKit.le16(0) + ZipKit.le16(0x21)
            header += ZipKit.le32(crc) + ZipKit.le32(UInt32(data.count)) + ZipKit.le32(UInt32(data.count))
            header += ZipKit.le16(UInt16(name.count)) + ZipKit.le16(0)
            body += header + name + data
            var record = ZipKit.le32(0x0201_4B50) + ZipKit.le16(20) + ZipKit.le16(20) + ZipKit.le16(0) + ZipKit.le16(0)
            record += ZipKit.le16(0) + ZipKit.le16(0x21) + ZipKit.le32(crc) + ZipKit.le32(UInt32(data.count)) + ZipKit.le32(UInt32(data.count))
            record += ZipKit.le16(UInt16(name.count)) + ZipKit.le16(0) + ZipKit.le16(0) + ZipKit.le16(0) + ZipKit.le16(0)
            record += ZipKit.le32(0) + ZipKit.le32(offset)
            directory += record + name
        }
        var end = ZipKit.le32(0x0605_4B50) + ZipKit.le16(0) + ZipKit.le16(0)
        end += ZipKit.le16(UInt16(entries.count)) + ZipKit.le16(UInt16(entries.count))
        end += ZipKit.le32(UInt32(directory.count)) + ZipKit.le32(UInt32(body.count)) + ZipKit.le16(0)
        try? (body + directory + end).write(to: url)
    }
}
