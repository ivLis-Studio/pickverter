import Foundation
import ImageIO

/// Word documents straight from the DocLang tree: styled runs and links, headings, lists, tables with merged cells,
/// pictures and footnotes. Cocoa's own DOCX writer drops tables and pictures, which HWP documents are full of.
enum DocxWriter {
    /// A4 with 2 cm side margins; the text column is 9,638 twips (6.69 in) wide.
    static let textWidth = 9638
    static let maxImageWidth = 6_120_000

    static func write(_ document: DocLang.Document, title: String, to url: URL) throws {
        var context = Context()
        var body = document.blocks.map { context.block($0, depth: 0) }.joined()
        if !document.footnotes.isEmpty {
            body += "<w:p><w:pPr><w:pBdr><w:top w:val=\"single\" w:sz=\"4\" w:space=\"4\" w:color=\"A0A0A5\"/></w:pBdr></w:pPr></w:p>"
            for (index, note) in document.footnotes.enumerated() {
                let text = note.map { DocLang.plain($0, depth: 0) }.filter { !$0.isEmpty }.joined(separator: " ")
                body += "<w:p><w:r><w:rPr><w:sz w:val=\"18\"/></w:rPr><w:t xml:space=\"preserve\">\(escape("\(index + 1). " + text))</w:t></w:r></w:p>"
            }
        }
        body += "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1440\" w:right=\"1134\" w:bottom=\"1440\" w:left=\"1134\" w:header=\"720\" w:footer=\"720\" w:gutter=\"0\"/></w:sectPr>"

        let declaration = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n"
        let relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships"
        var rels = "<Relationship Id=\"rId1\" Type=\"\(relationships)/styles\" Target=\"styles.xml\"/>"
        for image in context.images {
            rels += "<Relationship Id=\"\(image.id)\" Type=\"\(relationships)/image\" Target=\"media/\(image.name)\"/>"
        }
        for link in context.links {
            rels += "<Relationship Id=\"\(link.id)\" Type=\"\(relationships)/hyperlink\" Target=\"\(escape(link.url))\" TargetMode=\"External\"/>"
        }
        let documentXML = declaration
            + "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"\(relationships)\" "
            + "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" "
            + "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" "
            + "xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><w:body>" + body + "</w:body></w:document>"

        let fm = FileManager.default
        let folder = fm.temporaryDirectory.appendingPathComponent("pickverter-docx-" + UUID().uuidString, isDirectory: true)
        defer { try? fm.removeItem(at: folder) }
        let files: [String: String] = [
            "[Content_Types].xml": declaration + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
                + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>"
                + "<Default Extension=\"xml\" ContentType=\"application/xml\"/>"
                + "<Default Extension=\"png\" ContentType=\"image/png\"/><Default Extension=\"jpg\" ContentType=\"image/jpeg\"/>"
                + "<Default Extension=\"gif\" ContentType=\"image/gif\"/><Default Extension=\"bmp\" ContentType=\"image/bmp\"/>"
                + "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>"
                + "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>"
                + "<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/></Types>",
            "_rels/.rels": declaration + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + "<Relationship Id=\"rId1\" Type=\"\(relationships)/officeDocument\" Target=\"word/document.xml\"/>"
                + "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>"
                + "</Relationships>",
            "docProps/core.xml": declaration + "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" "
                + "xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>\(escape(title))</dc:title></cp:coreProperties>",
            "word/_rels/document.xml.rels": declaration + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" + rels + "</Relationships>",
            "word/styles.xml": declaration + styles,
            "word/document.xml": documentXML,
        ]
        for (path, text) in files {
            let file = folder.appendingPathComponent(path)
            try fm.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
            try Data(text.utf8).write(to: file)
        }
        if !context.images.isEmpty {
            let media = folder.appendingPathComponent("word/media", isDirectory: true)
            try fm.createDirectory(at: media, withIntermediateDirectories: true)
            for image in context.images { try image.data.write(to: media.appendingPathComponent(image.name)) }
        }
        try? fm.removeItem(at: url)
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/zip")
        process.arguments = ["-X", "-r", "-q", url.path, "[Content_Types].xml", "_rels", "docProps", "word"]
        process.currentDirectoryURL = folder
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        try process.run()
        process.waitUntilExit()
        guard process.terminationStatus == 0 else { throw PickError(L("err.cant_save_as", "DOCX")) }
    }

    // MARK: Body

    private struct Context {
        var images: [(id: String, name: String, data: Data)] = []
        var links: [(id: String, url: String)] = []
        private var next = 2

        private mutating func relationship() -> String {
            defer { next += 1 }
            return "rId\(next)"
        }

        mutating func block(_ block: DocLang.Block, depth: Int) -> String {
            switch block {
            case .paragraph(let spans):
                return paragraph(DocLang.trimmed(spans))
            case .heading(let level, let spans):
                return paragraph(DocLang.trimmed(spans), properties: "<w:pStyle w:val=\"Heading\(level)\"/>")
            case .list(let ordered, let items):
                var xml = ""
                for (index, item) in items.enumerated() {
                    let indent = "<w:ind w:left=\"\(360 * (depth + 1))\" w:hanging=\"360\"/>"
                    for (position, content) in item.enumerated() {
                        if position == 0, case .paragraph(let spans) = content {
                            let marker = DocLang.Span(text: ordered ? "\(index + 1).\t" : "•\t")
                            xml += paragraph([marker] + DocLang.trimmed(spans), properties: indent)
                        } else if case .list = content {
                            xml += self.block(content, depth: depth + 1)
                        } else {
                            xml += self.block(content, depth: depth)
                        }
                    }
                }
                return xml
            case .table(let table):
                return self.table(table)
            case .picture(let data, let ext):
                return "<w:p>" + picture(data, ext: ext) + "</w:p>"
            case .formula(let text):
                return "<w:p><w:r><w:rPr><w:rFonts w:ascii=\"Consolas\" w:hAnsi=\"Consolas\"/></w:rPr>"
                    + "<w:t xml:space=\"preserve\">\(DocxWriter.escape(text))</w:t></w:r></w:p>"
            }
        }

        mutating func paragraph(_ spans: [DocLang.Span], properties: String = "") -> String {
            "<w:p>" + (properties.isEmpty ? "" : "<w:pPr>\(properties)</w:pPr>") + runs(spans) + "</w:p>"
        }

        mutating func runs(_ spans: [DocLang.Span]) -> String {
            var xml = ""
            for span in spans {
                var props = ""
                if span.link != nil { props += "<w:rStyle w:val=\"Hyperlink\"/>" }
                if span.bold { props += "<w:b/><w:bCs/>" }
                if span.italic { props += "<w:i/><w:iCs/>" }
                if span.strike { props += "<w:strike/>" }
                if span.underline { props += "<w:u w:val=\"single\"/>" }
                if span.isSuperscript { props += "<w:vertAlign w:val=\"superscript\"/>" } else if span.isSubscript { props += "<w:vertAlign w:val=\"subscript\"/>" }
                let rPr = props.isEmpty ? "" : "<w:rPr>\(props)</w:rPr>"
                var piece = ""
                for (index, line) in span.text.components(separatedBy: "\n").enumerated() {
                    if index > 0 { piece += "<w:r>\(rPr)<w:br/></w:r>" }
                    for (part, chunk) in line.components(separatedBy: "\t").enumerated() {
                        if part > 0 { piece += "<w:r>\(rPr)<w:tab/></w:r>" }
                        if !chunk.isEmpty { piece += "<w:r>\(rPr)<w:t xml:space=\"preserve\">\(DocxWriter.escape(chunk))</w:t></w:r>" }
                    }
                }
                if let link = span.link, !link.isEmpty {
                    let id = relationship()
                    links.append((id, link))
                    piece = "<w:hyperlink r:id=\"\(id)\">" + piece + "</w:hyperlink>"
                }
                xml += piece
            }
            return xml
        }

        /// Rows of grid positions: an anchor writes its cell (gridSpan/vMerge restart), a position covered from
        /// above writes a vMerge continuation, a covered position to the right is part of the anchor's gridSpan.
        mutating func table(_ table: DocLang.Table) -> String {
            let width = table.width
            guard width > 0 else { return "" }
            var xml = ""
            if !DocLang.isBlank(table.caption) {
                xml += paragraph(DocLang.trimmed(table.caption), properties: "<w:pStyle w:val=\"Caption\"/><w:keepNext/>")
            }
            let column = max(DocxWriter.textWidth / width, 200)
            // Borders on the table itself too: Pages, Google Docs and previews don't all apply table styles.
            xml += "<w:tbl><w:tblPr><w:tblStyle w:val=\"PickverterTable\"/><w:tblW w:w=\"0\" w:type=\"auto\"/>" + DocxWriter.borders
                + "<w:tblLook w:val=\"0000\"/></w:tblPr><w:tblGrid>"
                + String(repeating: "<w:gridCol w:w=\"\(column)\"/>", count: width) + "</w:tblGrid>"
            var continuations: [Int: [Int: Int]] = [:]
            for (r, row) in table.rows.enumerated() {
                for (c, cell) in row.enumerated() {
                    guard let cell, cell.rows > 1 else { continue }
                    for offset in 1..<cell.rows { continuations[r + offset, default: [:]][c] = cell.columns }
                }
            }
            for (r, row) in table.rows.enumerated() {
                xml += "<w:tr>"
                var c = 0
                while c < width {
                    if c < row.count, let cell = row[c] {
                        var props = "<w:tcW w:w=\"\(column * cell.columns)\" w:type=\"dxa\"/>"
                        if cell.columns > 1 { props += "<w:gridSpan w:val=\"\(cell.columns)\"/>" }
                        if cell.rows > 1 { props += "<w:vMerge w:val=\"restart\"/>" }
                        if cell.header { props += "<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"F2F2F5\"/>" }
                        xml += "<w:tc><w:tcPr>\(props)</w:tcPr>" + cellContent(cell.blocks) + "</w:tc>"
                        c += max(cell.columns, 1)
                    } else if let span = continuations[r]?[c] {
                        xml += "<w:tc><w:tcPr><w:tcW w:w=\"\(column * span)\" w:type=\"dxa\"/>" + (span > 1 ? "<w:gridSpan w:val=\"\(span)\"/>" : "")
                            + "<w:vMerge/></w:tcPr><w:p/></w:tc>"
                        c += max(span, 1)
                    } else {
                        xml += "<w:tc><w:tcPr><w:tcW w:w=\"\(column)\" w:type=\"dxa\"/></w:tcPr><w:p/></w:tc>"
                        c += 1
                    }
                }
                xml += "</w:tr>"
            }
            // Word keeps tables apart (and cells end) with a paragraph: a small spacer.
            return xml + "</w:tbl><w:p><w:pPr><w:spacing w:before=\"0\" w:after=\"0\" w:line=\"120\" w:lineRule=\"exact\"/></w:pPr></w:p>"
        }

        mutating func cellContent(_ blocks: [DocLang.Block]) -> String {
            let xml = blocks.map { block in
                if case .paragraph(let spans) = block {
                    return paragraph(DocLang.trimmed(spans), properties: "<w:spacing w:after=\"0\"/>")
                }
                return self.block(block, depth: 0)
            }.joined()
            return xml.hasSuffix("</w:p>") ? xml : xml + "<w:p/>"
        }

        mutating func picture(_ data: Data, ext: String) -> String {
            let kind = ext == "jpeg" ? "jpg" : ext
            guard ["png", "jpg", "gif", "bmp"].contains(kind) else { return "" }
            var width = 400.0, height = 300.0
            if let source = CGImageSourceCreateWithData(data as CFData, nil),
               let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
               let w = properties[kCGImagePropertyPixelWidth] as? Double, let h = properties[kCGImagePropertyPixelHeight] as? Double,
               w > 0, h > 0 {
                width = w
                height = h
            }
            var cx = width * 9525, cy = height * 9525
            if cx > Double(DocxWriter.maxImageWidth) {
                cy *= Double(DocxWriter.maxImageWidth) / cx
                cx = Double(DocxWriter.maxImageWidth)
            }
            let id = relationship()
            let number = images.count + 1
            let name = "image\(number).\(kind)"
            images.append((id, name, data))
            let extent = "cx=\"\(Int(cx))\" cy=\"\(Int(cy))\""
            return "<w:r><w:drawing><wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\"><wp:extent \(extent)/>"
                + "<wp:docPr id=\"\(number)\" name=\"Picture \(number)\"/><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">"
                + "<pic:pic><pic:nvPicPr><pic:cNvPr id=\"\(number)\" name=\"\(name)\"/><pic:cNvPicPr/></pic:nvPicPr>"
                + "<pic:blipFill><a:blip r:embed=\"\(id)\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>"
                + "<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext \(extent)/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr>"
                + "</pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r>"
        }
    }

    /// XML text: escaped, and without the control characters XML 1.0 can't carry.
    static func escape(_ text: String) -> String {
        var out = ""
        out.reserveCapacity(text.count)
        for scalar in text.unicodeScalars {
            switch scalar {
            case "&": out += "&amp;"
            case "<": out += "&lt;"
            case ">": out += "&gt;"
            case "\"": out += "&quot;"
            default:
                let value = scalar.value
                if value < 0x20 && value != 0x09 && value != 0x0A && value != 0x0D { continue }
                if value == 0xFFFE || value == 0xFFFF { continue }
                out.unicodeScalars.append(scalar)
            }
        }
        return out
    }

    static let styles: String = {
        let main = "http://schemas.openxmlformats.org/wordprocessingml/2006/main"
        let font = "<w:rFonts w:ascii=\"Malgun Gothic\" w:hAnsi=\"Malgun Gothic\" w:eastAsia=\"Malgun Gothic\" w:cs=\"Malgun Gothic\"/>"
        var xml = "<w:styles xmlns:w=\"\(main)\"><w:docDefaults><w:rPrDefault><w:rPr>\(font)<w:sz w:val=\"21\"/><w:szCs w:val=\"21\"/>"
            + "<w:lang w:val=\"ko-KR\" w:eastAsia=\"ko-KR\"/></w:rPr></w:rPrDefault><w:pPrDefault><w:pPr><w:spacing w:after=\"120\" w:line=\"300\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>"
            + "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:qFormat/></w:style>"
        for (level, size) in [36, 32, 28, 26, 24, 22].enumerated() {
            xml += "<w:style w:type=\"paragraph\" w:styleId=\"Heading\(level + 1)\"><w:name w:val=\"heading \(level + 1)\"/><w:basedOn w:val=\"Normal\"/>"
                + "<w:next w:val=\"Normal\"/><w:qFormat/><w:pPr><w:keepNext/><w:spacing w:before=\"240\" w:after=\"120\"/><w:outlineLvl w:val=\"\(level)\"/></w:pPr>"
                + "<w:rPr><w:b/><w:bCs/><w:sz w:val=\"\(size)\"/><w:szCs w:val=\"\(size)\"/></w:rPr></w:style>"
        }
        xml += "<w:style w:type=\"paragraph\" w:styleId=\"Caption\"><w:name w:val=\"caption\"/><w:basedOn w:val=\"Normal\"/><w:qFormat/>"
            + "<w:rPr><w:i/><w:sz w:val=\"18\"/><w:szCs w:val=\"18\"/></w:rPr></w:style>"
            + "<w:style w:type=\"character\" w:styleId=\"Hyperlink\"><w:name w:val=\"Hyperlink\"/><w:rPr><w:color w:val=\"0563C1\"/><w:u w:val=\"single\"/></w:rPr></w:style>"
            + "<w:style w:type=\"table\" w:styleId=\"PickverterTable\"><w:name w:val=\"Pickverter Table\"/><w:tblPr>" + borders
        xml += "<w:tblCellMar><w:top w:w=\"40\" w:type=\"dxa\"/><w:left w:w=\"100\" w:type=\"dxa\"/><w:bottom w:w=\"40\" w:type=\"dxa\"/>"
            + "<w:right w:w=\"100\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr></w:style></w:styles>"
        return xml
    }()

    static let borders: String = "<w:tblBorders>"
        + ["top", "left", "bottom", "right", "insideH", "insideV"].map { "<w:\($0) w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"8E8E93\"/>" }.joined()
        + "</w:tblBorders>"
}

