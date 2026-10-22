import AppKit
import AVFoundation
import ImageIO

/// `cli fixtures <dir>`: small sample files for every engine, generated without any external tool
/// (so CI can test on a bare runner).
extension CLI {
    static func makeFixtures(_ dir: String) -> Int32 {
        let root = URL(fileURLWithPath: dir)
        try? FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        do {
            try fixturePhoto(root.appendingPathComponent("photo.jpg"))
            try fixtureLogo(root.appendingPathComponent("logo.png"))
            try fixtureSign(root.appendingPathComponent("sign.png"))
            try fixturePDF(root.appendingPathComponent("report.pdf"))
            try fixtureWAV(root.appendingPathComponent("tone.wav"), seconds: 4)
            try fixtureWorkbook(root.appendingPathComponent("sheet.xlsx"))
            try fixtureDeck(root.appendingPathComponent("deck.pptx"))
            try fixtureMarkdown.write(to: root.appendingPathComponent("book.md"), atomically: true, encoding: .utf8)
            try "Plain notes.\n\nSecond paragraph with a line\nbreak.\n".write(to: root.appendingPathComponent("notes.txt"),
                                                                                 atomically: true, encoding: .utf8)
            try "name,score\nAlice,92.5\nBob,88\n".write(to: root.appendingPathComponent("people.csv"), atomically: true, encoding: .utf8)
        } catch {
            print("ERR \(error.localizedDescription)")
            return 1
        }
        // Virtual machines without a video encoder can't write H.264; media cases are skipped then.
        do {
            try wait { try await fixtureMovie(root.appendingPathComponent("clip.mov")) }
        } catch {
            FileHandle.standardError.write(Data("warning: no clip.mov (\(error.localizedDescription))\n".utf8))
        }
        for name in (try? FileManager.default.contentsOfDirectory(atPath: root.path).sorted()) ?? [] { print(root.appendingPathComponent(name).path) }
        return 0
    }

    static let fixtureMarkdown = """
    # The First Chapter

    Pickverter turns **files** into other *files*. Here is a [link](https://example.com) and some \u{60}code\u{60}.

    - one
    - two

    ![Logo](logo.png)

    ---

    # The Second Chapter

    | Name | Score |
    | --- | --- |
    | Alice | 92 |

    > A quote & an ampersand.
    """

    static func bitmap(_ width: Int, _ height: Int, alpha: Bool, _ draw: (CGContext) -> Void) -> CGImage? {
        guard let ctx = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: (alpha ? CGImageAlphaInfo.premultipliedLast : .noneSkipLast).rawValue) else { return nil }
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(cgContext: ctx, flipped: false)
        draw(ctx)
        NSGraphicsContext.restoreGraphicsState()
        return ctx.makeImage()
    }

    static func writeImage(_ image: CGImage?, to url: URL, type: String, properties: [CFString: Any] = [:]) throws {
        guard let image, let dest = CGImageDestinationCreateWithURL(url as CFURL, type as CFString, 1, nil) else {
            throw PickError("fixture \(url.lastPathComponent)")
        }
        CGImageDestinationAddImage(dest, image, properties as CFDictionary)
        guard CGImageDestinationFinalize(dest) else { throw PickError("fixture \(url.lastPathComponent)") }
    }

    static func fixturePhoto(_ url: URL) throws {
        let image = bitmap(1600, 1000, alpha: false) { ctx in
            ctx.setFillColor(CGColor(srgbRed: 0.55, green: 0.72, blue: 0.86, alpha: 1))
            ctx.fill(CGRect(x: 0, y: 0, width: 1600, height: 1000))
            ctx.setFillColor(CGColor(srgbRed: 0.30, green: 0.52, blue: 0.32, alpha: 1))
            ctx.fill(CGRect(x: 0, y: 0, width: 1600, height: 380))
            ctx.setFillColor(CGColor(srgbRed: 0.98, green: 0.82, blue: 0.35, alpha: 1))
            ctx.fillEllipse(in: CGRect(x: 1180, y: 640, width: 220, height: 220))
            ctx.setFillColor(CGColor(srgbRed: 0.85, green: 0.35, blue: 0.30, alpha: 1))
            ctx.fill(CGRect(x: 260, y: 300, width: 320, height: 260))
            ("PICKVERTER 2026" as NSString).draw(at: CGPoint(x: 640, y: 420),
                                                 withAttributes: [.font: NSFont.boldSystemFont(ofSize: 72), .foregroundColor: NSColor.white])
        }
        let properties: [CFString: Any] = [
            kCGImageDestinationLossyCompressionQuality: 0.9,
            kCGImagePropertyTIFFDictionary: [kCGImagePropertyTIFFMake: "Pickverter", kCGImagePropertyTIFFModel: "Fixture Cam"],
            kCGImagePropertyExifDictionary: [kCGImagePropertyExifDateTimeOriginal: "2026:10:22 10:00:00"],
            kCGImagePropertyGPSDictionary: [kCGImagePropertyGPSLatitude: 37.5665, kCGImagePropertyGPSLatitudeRef: "N",
                                            kCGImagePropertyGPSLongitude: 126.978, kCGImagePropertyGPSLongitudeRef: "E"],
        ]
        try writeImage(image, to: url, type: "public.jpeg", properties: properties)
    }

    static func fixtureLogo(_ url: URL) throws {
        let image = bitmap(600, 600, alpha: true) { ctx in
            ctx.setFillColor(CGColor(srgbRed: 0.11, green: 0.11, blue: 0.12, alpha: 1))
            ctx.fillEllipse(in: CGRect(x: 60, y: 60, width: 480, height: 480))
            ctx.setFillColor(CGColor(srgbRed: 0.04, green: 0.52, blue: 1, alpha: 1))
            ctx.fillEllipse(in: CGRect(x: 220, y: 220, width: 160, height: 160))
        }
        try writeImage(image, to: url, type: "public.png")
    }

    static func fixtureSign(_ url: URL) throws {
        let image = bitmap(1200, 500, alpha: false) { ctx in
            ctx.setFillColor(CGColor(gray: 1, alpha: 1))
            ctx.fill(CGRect(x: 0, y: 0, width: 1200, height: 500))
            let attributes: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: 76, weight: .semibold),
                                                             .foregroundColor: NSColor.black]
            ("Hello Pickverter" as NSString).draw(at: CGPoint(x: 80, y: 290), withAttributes: attributes)
            ("Convert 1234" as NSString).draw(at: CGPoint(x: 80, y: 120), withAttributes: attributes)
        }
        try writeImage(image, to: url, type: "public.png")
    }

    static func fixturePDF(_ url: URL) throws {
        var box = CGRect(x: 0, y: 0, width: 612, height: 792)
        guard let ctx = CGContext(url as CFURL, mediaBox: &box, nil) else { throw PickError("fixture pdf") }
        let pages = ["Quarterly report\n\nRevenue grew twelve percent while costs stayed flat.", "",
                     "Appendix\n\nTables and notes for the curious reader."]
        for page in pages {
            ctx.beginPDFPage(nil)
            if !page.isEmpty {
                NSGraphicsContext.saveGraphicsState()
                NSGraphicsContext.current = NSGraphicsContext(cgContext: ctx, flipped: false)
                (page as NSString).draw(in: CGRect(x: 72, y: 72, width: 468, height: 648),
                                        withAttributes: [.font: NSFont.systemFont(ofSize: 20), .foregroundColor: NSColor.black])
                NSGraphicsContext.restoreGraphicsState()
            }
            ctx.endPDFPage()
        }
        ctx.closePDF()
    }

    /// 440 Hz with half a second of silence at both ends (for the silence trimmer).
    static func fixtureWAV(_ url: URL, seconds: Double) throws {
        let rate = 44_100
        let count = Int(Double(rate) * seconds)
        var data = Data()
        func append<T: FixedWidthInteger>(_ value: T) { withUnsafeBytes(of: value.littleEndian) { data.append(contentsOf: $0) } }
        data.append(Data("RIFF".utf8)); append(UInt32(36 + count * 2)); data.append(Data("WAVEfmt ".utf8))
        append(UInt32(16)); append(UInt16(1)); append(UInt16(1)); append(UInt32(rate)); append(UInt32(rate * 2))
        append(UInt16(2)); append(UInt16(16)); data.append(Data("data".utf8)); append(UInt32(count * 2))
        for index in 0..<count {
            let t = Double(index) / Double(rate)
            let amplitude = (t < 0.5 || t > seconds - 0.5) ? 0 : 0.4
            append(Int16(sin(2 * .pi * 440 * t) * amplitude * 32_767))
        }
        try data.write(to: url)
    }

    static func zipFolder(_ folder: URL, to url: URL) throws {
        try? FileManager.default.removeItem(at: url)
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/zip")
        process.arguments = ["-X", "-r", "-q", url.path, "."]
        process.currentDirectoryURL = folder
        try process.run()
        process.waitUntilExit()
        guard process.terminationStatus == 0 else { throw PickError("zip failed") }
    }

    static func writeTree(_ files: [String: String], to root: URL) throws {
        for (path, body) in files {
            let url = root.appendingPathComponent(path)
            try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            try body.write(to: url, atomically: true, encoding: .utf8)
        }
    }

    static func fixtureWorkbook(_ url: URL) throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("pickverter-xlsx-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: folder) }
        let xml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n"
        let main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main"
        let rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships"
        try writeTree([
            "[Content_Types].xml": xml + """
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/worksheets/sheet2.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/sharedStrings.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>
            """,
            "_rels/.rels": xml + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"\(rel)/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>",
            "xl/workbook.xml": xml + "<workbook xmlns=\"\(main)\" xmlns:r=\"\(rel)\"><sheets><sheet name=\"Scores\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"Notes\" sheetId=\"2\" r:id=\"rId2\"/></sheets></workbook>",
            "xl/_rels/workbook.xml.rels": xml + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"\(rel)/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"\(rel)/worksheet\" Target=\"worksheets/sheet2.xml\"/><Relationship Id=\"rId3\" Type=\"\(rel)/sharedStrings\" Target=\"sharedStrings.xml\"/><Relationship Id=\"rId4\" Type=\"\(rel)/styles\" Target=\"styles.xml\"/></Relationships>",
            "xl/sharedStrings.xml": xml + "<sst xmlns=\"\(main)\" count=\"5\" uniqueCount=\"5\"><si><t>Name</t></si><si><t>Score</t></si><si><t>Joined</t></si><si><r><t>Ali</t></r><r><t>ce</t></r></si><si><t>Bob</t></si></sst>",
            "xl/styles.xml": xml + "<styleSheet xmlns=\"\(main)\"><cellXfs count=\"2\"><xf numFmtId=\"0\"/><xf numFmtId=\"14\" applyNumberFormat=\"1\"/></cellXfs></styleSheet>",
            "xl/worksheets/sheet1.xml": xml + "<worksheet xmlns=\"\(main)\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c><c r=\"C1\" t=\"s\"><v>2</v></c></row><row r=\"2\"><c r=\"A2\" t=\"s\"><v>3</v></c><c r=\"B2\"><v>92.5</v></c><c r=\"C2\" s=\"1\"><v>46317</v></c></row><row r=\"3\"><c r=\"A3\" t=\"s\"><v>4</v></c><c r=\"B3\"><v>88</v></c><c r=\"C3\" s=\"1\"><v>46000</v></c></row></sheetData></worksheet>",
            "xl/worksheets/sheet2.xml": xml + "<worksheet xmlns=\"\(main)\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>Second sheet</t></is></c><c r=\"B1\" t=\"b\"><v>1</v></c></row></sheetData></worksheet>",
        ], to: folder)
        try zipFolder(folder, to: url)
    }

    static func fixtureDeck(_ url: URL) throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("pickverter-pptx-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: folder) }
        let xml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n"
        let ns = "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\""
        func slide(_ title: String, _ lines: [String]) -> String {
            let body = lines.map { "<a:p><a:r><a:t>\($0)</a:t></a:r></a:p>" }.joined()
            return xml + "<p:sld \(ns)><p:cSld><p:spTree><p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Title\"/><p:cNvSpPr/><p:nvPr><p:ph type=\"title\"/></p:nvPr></p:nvSpPr><p:txBody><a:p><a:r><a:t>\(title)</a:t></a:r></a:p></p:txBody></p:sp><p:sp><p:nvSpPr><p:cNvPr id=\"3\" name=\"Body\"/><p:cNvSpPr/><p:nvPr><p:ph idx=\"1\"/></p:nvPr></p:nvSpPr><p:txBody>\(body)</p:txBody></p:sp></p:spTree></p:cSld></p:sld>"
        }
        let rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships"
        try writeTree([
            "[Content_Types].xml": xml + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/ppt/presentation.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml\"/><Override PartName=\"/ppt/slides/slide1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slide+xml\"/><Override PartName=\"/ppt/slides/slide2.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slide+xml\"/></Types>",
            "_rels/.rels": xml + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"\(rel)/officeDocument\" Target=\"ppt/presentation.xml\"/></Relationships>",
            "ppt/presentation.xml": xml + "<p:presentation \(ns)><p:sldIdLst><p:sldId id=\"256\" r:id=\"rId2\"/><p:sldId id=\"257\" r:id=\"rId3\"/></p:sldIdLst></p:presentation>",
            "ppt/_rels/presentation.xml.rels": xml + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId2\" Type=\"\(rel)/slide\" Target=\"slides/slide1.xml\"/><Relationship Id=\"rId3\" Type=\"\(rel)/slide\" Target=\"slides/slide2.xml\"/></Relationships>",
            "ppt/slides/slide1.xml": slide("Quarterly Review", ["Revenue up 12%", "Costs flat"]),
            "ppt/slides/slide2.xml": slide("Next Steps", ["Ship Pickverter", "Collect feedback"]),
        ], to: folder)
        let media = folder.appendingPathComponent("ppt/media")
        try FileManager.default.createDirectory(at: media, withIntermediateDirectories: true)
        try fixtureLogo(media.appendingPathComponent("image1.png"))
        try zipFolder(folder, to: url)
    }

    /// Three seconds of H.264 with a moving block, written by AVFoundation (no ffmpeg needed).
    static func fixtureMovie(_ url: URL) async throws {
        try? FileManager.default.removeItem(at: url)
        let width = 640, height = 360
        let writer = try AVAssetWriter(outputURL: url, fileType: .mov)
        let input = AVAssetWriterInput(mediaType: .video, outputSettings: [AVVideoCodecKey: AVVideoCodecType.h264,
                                                                           AVVideoWidthKey: width, AVVideoHeightKey: height])
        input.expectsMediaDataInRealTime = false
        let adaptor = AVAssetWriterInputPixelBufferAdaptor(assetWriterInput: input, sourcePixelBufferAttributes: [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
            kCVPixelBufferWidthKey as String: width, kCVPixelBufferHeightKey as String: height,
        ])
        writer.add(input)
        guard writer.startWriting() else { throw writer.error ?? PickError("movie") }
        writer.startSession(atSourceTime: .zero)
        let colors: [(CGFloat, CGFloat, CGFloat)] = [(0.85, 0.3, 0.3), (0.3, 0.7, 0.4), (0.25, 0.45, 0.85)]
        for frame in 0..<90 {
            while !input.isReadyForMoreMediaData { try await Task.sleep(nanoseconds: 2_000_000) }
            guard let pool = adaptor.pixelBufferPool else { throw PickError("movie pool") }
            var buffer: CVPixelBuffer?
            CVPixelBufferPoolCreatePixelBuffer(nil, pool, &buffer)
            guard let buffer else { throw PickError("movie buffer") }
            CVPixelBufferLockBaseAddress(buffer, [])
            if let ctx = CGContext(data: CVPixelBufferGetBaseAddress(buffer), width: width, height: height, bitsPerComponent: 8,
                                   bytesPerRow: CVPixelBufferGetBytesPerRow(buffer), space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                   bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue) {
                let color = colors[frame / 30]
                ctx.setFillColor(CGColor(srgbRed: color.0, green: color.1, blue: color.2, alpha: 1))
                ctx.fill(CGRect(x: 0, y: 0, width: width, height: height))
                ctx.setFillColor(CGColor(gray: 1, alpha: 1))
                ctx.fill(CGRect(x: 40 + frame * 5, y: 140, width: 80, height: 80))
            }
            CVPixelBufferUnlockBaseAddress(buffer, [])
            adaptor.append(buffer, withPresentationTime: CMTime(value: CMTimeValue(frame), timescale: 30))
        }
        input.markAsFinished()
        await writer.finishWriting()
        guard writer.status == .completed else { throw writer.error ?? PickError("movie") }
    }
}
