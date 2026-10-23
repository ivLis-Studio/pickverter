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
            try fixtureHWPX(root.appendingPathComponent("report.hwpx"))
            try fixturesCrossPlatform(root)
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

    /// A small HWPX (OWPML in a ZIP): a bold 18 pt title, two paragraphs, a 3×2 table and a closing paragraph — the
    /// same document the Windows fixtures make. Tests turn it into HWP with rhwp when they need the binary format.
    static func fixtureHWPX(_ url: URL) throws {
        let fm = FileManager.default
        let folder = fm.temporaryDirectory.appendingPathComponent("pickverter-hwpx-\(UUID().uuidString)")
        defer { try? fm.removeItem(at: folder) }
        let declaration = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
        let ns = "xmlns:hp=\"http://www.hancom.co.kr/hwpml/2011/paragraph\" xmlns:hc=\"http://www.hancom.co.kr/hwpml/2011/core\""
        func escape(_ text: String) -> String {
            text.replacingOccurrences(of: "&", with: "&amp;").replacingOccurrences(of: "<", with: "&lt;").replacingOccurrences(of: ">", with: "&gt;")
        }
        func charPr(_ id: Int, _ height: Int, bold: Bool) -> String {
            "<hh:charPr id=\"\(id)\" height=\"\(height)\" textColor=\"#000000\" shadeColor=\"none\" useFontSpace=\"0\" useKerning=\"0\" symMark=\"NONE\" borderFillIDRef=\"1\">"
                + "<hh:fontRef hangul=\"0\" latin=\"0\" hanja=\"0\" japanese=\"0\" other=\"0\" symbol=\"0\" user=\"0\"/>"
                + "<hh:ratio hangul=\"100\" latin=\"100\" hanja=\"100\" japanese=\"100\" other=\"100\" symbol=\"100\" user=\"100\"/>"
                + "<hh:spacing hangul=\"0\" latin=\"0\" hanja=\"0\" japanese=\"0\" other=\"0\" symbol=\"0\" user=\"0\"/>"
                + "<hh:relSz hangul=\"100\" latin=\"100\" hanja=\"100\" japanese=\"100\" other=\"100\" symbol=\"100\" user=\"100\"/>"
                + "<hh:offset hangul=\"0\" latin=\"0\" hanja=\"0\" japanese=\"0\" other=\"0\" symbol=\"0\" user=\"0\"/>"
                + (bold ? "<hh:bold/>" : "") + "</hh:charPr>"
        }
        func border(_ id: Int, _ kind: String) -> String {
            let sides = ["left", "right", "top", "bottom"].map { "<hh:\($0)Border type=\"\(kind)\" width=\"0.12 mm\" color=\"#000000\"/>" }.joined()
            return "<hh:borderFill id=\"\(id)\" threeD=\"0\" shadow=\"0\" centerLine=\"NONE\" breakCellSeparateLine=\"0\">"
                + "<hh:slash type=\"NONE\" Crooked=\"0\" isCounter=\"0\"/><hh:backSlash type=\"NONE\" Crooked=\"0\" isCounter=\"0\"/>" + sides
                + "<hh:diagonal type=\"NONE\" width=\"0.1 mm\" color=\"#000000\"/></hh:borderFill>"
        }
        let header = declaration
            + "<hh:head xmlns:hh=\"http://www.hancom.co.kr/hwpml/2011/head\" \(ns) version=\"1.4\" secCnt=\"1\"><hh:refList>"
            + "<hh:fontfaces itemCnt=\"2\">"
            + "<hh:fontface lang=\"HANGUL\" fontCnt=\"1\"><hh:font id=\"0\" face=\"함초롬바탕\" type=\"TTF\" isEmbedded=\"0\"/></hh:fontface>"
            + "<hh:fontface lang=\"LATIN\" fontCnt=\"1\"><hh:font id=\"0\" face=\"함초롬바탕\" type=\"TTF\" isEmbedded=\"0\"/></hh:fontface>"
            + "</hh:fontfaces>"
            + "<hh:borderFills itemCnt=\"2\">" + border(1, "NONE") + border(2, "SOLID") + "</hh:borderFills>"
            + "<hh:charProperties itemCnt=\"2\">" + charPr(0, 1000, bold: false) + charPr(1, 1800, bold: true) + "</hh:charProperties>"
            + "<hh:paraProperties itemCnt=\"1\"><hh:paraPr id=\"0\" tabPrIDRef=\"0\" condense=\"0\" fontLineHeight=\"0\" snapToGrid=\"1\" suppressLineNumbers=\"0\" checked=\"0\">"
            + "<hh:align horizontal=\"JUSTIFY\" vertical=\"BASELINE\"/><hh:heading type=\"NONE\" idRef=\"0\" level=\"0\"/>"
            + "<hh:breakSetting breakLatinWord=\"KEEP_WORD\" breakNonLatinWord=\"KEEP_WORD\" widowOrphan=\"0\" keepWithNext=\"0\" keepLines=\"0\" pageBreakBefore=\"0\" lineWrap=\"BREAK\"/>"
            + "<hh:autoSpacing eAsianEng=\"0\" eAsianNum=\"0\"/><hh:margin><hc:intent value=\"0\" unit=\"HWPUNIT\"/><hc:left value=\"0\" unit=\"HWPUNIT\"/><hc:right value=\"0\" unit=\"HWPUNIT\"/><hc:prev value=\"0\" unit=\"HWPUNIT\"/><hc:next value=\"600\" unit=\"HWPUNIT\"/></hh:margin>"
            + "<hh:lineSpacing type=\"PERCENT\" value=\"160\" unit=\"HWPUNIT\"/><hh:border borderFillIDRef=\"1\" offsetLeft=\"0\" offsetRight=\"0\" offsetTop=\"0\" offsetBottom=\"0\" connect=\"0\" ignoreMargin=\"0\"/></hh:paraPr></hh:paraProperties>"
            + "</hh:refList><hh:compatibleDocument targetProgram=\"HWP201X\"><hh:layoutCompatibility/></hh:compatibleDocument></hh:head>"
        var paragraphID = 0
        func paragraph(_ text: String, char: Int = 0, inner: String = "") -> String {
            paragraphID += 1
            let run = "<hp:run charPrIDRef=\"\(char)\">" + inner + (text.isEmpty ? "" : "<hp:t>\(escape(text))</hp:t>") + "</hp:run>"
            return "<hp:p id=\"\(paragraphID)\" paraPrIDRef=\"0\" styleIDRef=\"0\" pageBreak=\"0\" columnBreak=\"0\" merged=\"0\">" + run + "</hp:p>"
        }
        let section = "<hp:secPr id=\"\" textDirection=\"HORIZONTAL\" spaceColumns=\"1134\" tabStop=\"8000\" tabStopVal=\"4000\" tabStopUnit=\"HWPUNIT\" outlineShapeIDRef=\"1\" memoShapeIDRef=\"0\" textVerticalWidthHead=\"0\" masterPageCnt=\"0\">"
            + "<hp:grid lineGrid=\"0\" charGrid=\"0\" wonggojiFormat=\"0\" strikeContinue=\"0\"/><hp:startNum pageStartsOn=\"BOTH\" page=\"0\" pic=\"0\" tbl=\"0\" equation=\"0\"/>"
            + "<hp:visibility hideFirstHeader=\"0\" hideFirstFooter=\"0\" hideFirstMasterPage=\"0\" border=\"SHOW_ALL\" fill=\"SHOW_ALL\" hideFirstPageNum=\"0\" hideFirstEmptyLine=\"0\" showLineNumber=\"0\"/>"
            + "<hp:lineNumberShape restartType=\"0\" countBy=\"0\" distance=\"0\" startNumber=\"0\"/>"
            + "<hp:pagePr landscape=\"WIDELY\" width=\"59528\" height=\"84188\" gutterType=\"LEFT_ONLY\"><hp:margin header=\"4252\" footer=\"4252\" gutter=\"0\" left=\"8504\" right=\"8504\" top=\"5668\" bottom=\"4252\"/></hp:pagePr>"
            + "</hp:secPr><hp:ctrl><hp:colPr id=\"\" type=\"NEWSPAPER\" layout=\"LEFT\" colCount=\"1\" sameSz=\"1\" sameGap=\"0\"/></hp:ctrl>"
        let width = 21000
        func cell(_ text: String, column: Int, row: Int) -> String {
            paragraphID += 1
            return "<hp:tc name=\"\" header=\"0\" hasMargin=\"0\" protect=\"0\" editable=\"0\" dirty=\"0\" borderFillIDRef=\"2\">"
                + "<hp:subList id=\"\" textDirection=\"HORIZONTAL\" lineWrap=\"BREAK\" vertAlign=\"CENTER\" linkListIDRef=\"0\" linkListNextIDRef=\"0\" textWidth=\"0\" textHeight=\"0\" hasTextRef=\"0\" hasNumRef=\"0\">"
                + "<hp:p id=\"\(paragraphID)\" paraPrIDRef=\"0\" styleIDRef=\"0\" pageBreak=\"0\" columnBreak=\"0\" merged=\"0\"><hp:run charPrIDRef=\"0\"><hp:t>\(escape(text))</hp:t></hp:run></hp:p>"
                + "</hp:subList><hp:cellAddr colAddr=\"\(column)\" rowAddr=\"\(row)\"/><hp:cellSpan colSpan=\"1\" rowSpan=\"1\"/><hp:cellSz width=\"\(width)\" height=\"1800\"/><hp:cellMargin left=\"510\" right=\"510\" top=\"141\" bottom=\"141\"/></hp:tc>"
        }
        let rows = [("항목", "Value"), ("사과", "1,200원"), ("Pickverter", "HWP · HWPX")]
        let opening = paragraph("Pickverter 한글 문서", char: 1, inner: section)
            + paragraph("이 문서는 HWP·HWPX 변환 테스트에 쓰는 문서예요. The quick brown fox jumps over the lazy dog.")
            + paragraph("두 번째 문단: 숫자 12345와 기호 % & < > \"따옴표\"도 그대로 남아야 해요.")
        let table = "<hp:tbl id=\"100\" zOrder=\"0\" numberingType=\"TABLE\" textWrap=\"TOP_AND_BOTTOM\" textFlow=\"BOTH_SIDES\" lock=\"0\" dropcapstyle=\"None\" pageBreak=\"CELL\" repeatHeader=\"1\" "
            + "rowCnt=\"\(rows.count)\" colCnt=\"2\" cellSpacing=\"0\" borderFillIDRef=\"2\" noAdjust=\"0\">"
            + "<hp:sz width=\"\(width * 2)\" widthRelTo=\"ABSOLUTE\" height=\"\(1800 * rows.count)\" heightRelTo=\"ABSOLUTE\" protect=\"0\"/>"
            + "<hp:pos treatAsChar=\"0\" affectLSpacing=\"0\" flowWithText=\"1\" allowOverlap=\"0\" holdAnchorAndSO=\"0\" vertRelTo=\"PARA\" horzRelTo=\"COLUMN\" vertAlign=\"TOP\" horzAlign=\"LEFT\" vertOffset=\"0\" horzOffset=\"0\"/>"
            + "<hp:outMargin left=\"0\" right=\"0\" top=\"0\" bottom=\"0\"/><hp:inMargin left=\"510\" right=\"510\" top=\"141\" bottom=\"141\"/>"
            + rows.enumerated().map { "<hp:tr>" + cell($1.0, column: 0, row: $0) + cell($1.1, column: 1, row: $0) + "</hp:tr>" }.joined()
            + "</hp:tbl>"
        let document = opening + paragraph("", inner: table) + paragraph("표 다음 문단입니다. 끝.")
        try writeTree([
            "mimetype": "application/hwp+zip",
            "version.xml": declaration + "<hv:HCFVersion xmlns:hv=\"http://www.hancom.co.kr/hwpml/2011/version\" tagetApplication=\"WORDPROCESSOR\" "
                + "major=\"5\" minor=\"1\" micro=\"1\" buildNumber=\"0\" os=\"1\" xmlVersion=\"1.4\" application=\"Pickverter\" appVersion=\"1, 0, 0, 0\"/>",
            "META-INF/container.xml": declaration + "<ocf:container xmlns:ocf=\"urn:oasis:names:tc:opendocument:xmlns:container\" xmlns:hpf=\"http://www.hancom.co.kr/schema/2011/hpf\">"
                + "<ocf:rootfiles><ocf:rootfile full-path=\"Contents/content.hpf\" media-type=\"application/hwpml-package+xml\"/></ocf:rootfiles></ocf:container>",
            "META-INF/manifest.xml": declaration + "<odf:manifest xmlns:odf=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\"/>",
            "Contents/content.hpf": declaration + "<opf:package xmlns:opf=\"http://www.idpf.org/2007/opf/\" version=\"\" unique-identifier=\"\" id=\"\">"
                + "<opf:metadata><opf:title>Pickverter 한글 문서</opf:title><opf:language>ko</opf:language></opf:metadata>"
                + "<opf:manifest><opf:item id=\"header\" href=\"Contents/header.xml\" media-type=\"application/xml\"/><opf:item id=\"section0\" href=\"Contents/section0.xml\" media-type=\"application/xml\"/></opf:manifest>"
                + "<opf:spine><opf:itemref idref=\"header\"/><opf:itemref idref=\"section0\"/></opf:spine></opf:package>",
            "Contents/header.xml": header,
            "Contents/section0.xml": declaration + "<hs:sec xmlns:hs=\"http://www.hancom.co.kr/hwpml/2011/section\" \(ns)>" + document + "</hs:sec>",
        ], to: folder)
        // Like EPUB: "mimetype" first and stored, then everything else.
        try? fm.removeItem(at: url)
        for arguments in [["-X0", "-q", url.path, "mimetype"], ["-X", "-r", "-9", "-q", url.path, "version.xml", "META-INF", "Contents"]] {
            let process = Process()
            process.executableURL = URL(fileURLWithPath: "/usr/bin/zip")
            process.arguments = arguments
            process.currentDirectoryURL = folder
            try process.run()
            process.waitUntilExit()
            guard process.terminationStatus == 0 else { throw PickError("zip failed") }
        }
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

    /// Files as they arrive from Windows (CP949 text, SAMI subtitles, a ZIP with CP949 names) and from a Mac (a ZIP
    /// and a folder with decomposed names), plus a noisy photo big enough for the "Under …" sizes.
    static func fixturesCrossPlatform(_ root: URL) throws {
        let cp949 = TextCodec.cp949
        guard let notes = "회의록\r\n- 똠방각하 일정 확인\r\n".data(using: cp949) else { throw PickError("fixture cp949") }
        try notes.write(to: root.appendingPathComponent("notes-cp949.txt"))
        let sami = """
        <SAMI><HEAD><STYLE TYPE="text/css"><!--
        .KRCC { Name:Korean; lang:ko-KR; }
        .ENCC { Name:English; lang:en-US; }
        --></STYLE></HEAD><BODY>
        <SYNC Start=1000><P Class=ENCC>Hello
        <SYNC Start=1000><P Class=KRCC>안녕하세요<br>반갑습니다
        <SYNC Start=3000><P Class=KRCC>&nbsp;
        <SYNC Start=4000><P Class=KRCC>두 번째 자막
        </BODY></SAMI>
        """
        guard let subtitles = sami.data(using: cp949) else { throw PickError("fixture smi") }
        try subtitles.write(to: root.appendingPathComponent("movie.smi"))
        try #"[{"name": "Alice", "score": 92.5}, {"name": "민수", "score": 88}]"#
            .write(to: root.appendingPathComponent("people.json"), atomically: true, encoding: .utf8)
        writeRawZip(root.appendingPathComponent("windows.zip"), names: [
            ("보고서/".data(using: cp949) ?? Data(), Data()),
            ("보고서/결과.txt".data(using: cp949) ?? Data(), Data("ok".utf8)),
        ])
        let decomposed = { (text: String) in Data(text.decomposedStringWithCanonicalMapping.utf8) }
        writeRawZip(root.appendingPathComponent("mac.zip"), names: [(decomposed("사진 모음/"), Data()),
                                                                    (decomposed("사진 모음/메모.txt"), Data("ok".utf8))])
        // A folder named on a Mac: the names inside are decomposed on disk (POSIX calls keep the bytes as they are).
        let names = root.appendingPathComponent("names", isDirectory: true)
        try FileManager.default.createDirectory(at: names, withIntermediateDirectories: true)
        let folder = NameFixer.pathBytes(names)
        for name in ["보고서.txt", "ポスト.txt"] {
            let path = folder + [NameFixer.slash] + [UInt8](name.decomposedStringWithCanonicalMapping.utf8)
            let descriptor = NameFixer.withCPath(path) { creat($0, 0o644) }
            if descriptor >= 0 { close(descriptor) }
        }
        try Data("x".utf8).write(to: names.appendingPathComponent(".DS_Store"))
        // Noise compresses badly: about 2–3 MB as a JPEG.
        var generator = SystemRandomNumberGenerator()
        let noise = bitmap(2000, 1500, alpha: false) { ctx in
            guard let data = ctx.data else { return }
            let pixels = data.bindMemory(to: UInt32.self, capacity: 2000 * 1500)
            for index in 0..<(2000 * 1500) { pixels[index] = UInt32.random(in: 0...UInt32.max, using: &generator) | 0xFF00_0000 }
        }
        try writeImage(noise, to: root.appendingPathComponent("noise.jpg"), type: "public.jpeg",
                       properties: [kCGImageDestinationLossyCompressionQuality: 0.9])
    }
}
