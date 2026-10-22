import AppKit
import PDFKit

enum PDFEngine {
    static func open(_ url: URL) throws -> PDFDocument {
        guard let doc = PDFDocument(url: url) else { throw PickError(L("err.pdf_open")) }
        if doc.isLocked { throw PickError(L("err.pdf_locked")) }
        return doc
    }

    static func convert(_ src: URL, to ext: String, dpi: CGFloat = 300, ctx: PickContext) async throws -> URL {
        switch ext {
        case "png", "jpg":
            return try await toImages(src, ext: ext, dpi: dpi, ctx: ctx)
        case "txt":
            let text = try extractText(open(src), ctx: ctx)
            return try await ctx.produce(ctx.destination(for: src, ext: "txt")) { tmp in
                try text.write(to: tmp, atomically: true, encoding: .utf8)
            }
        default:
            return try await ctx.produce(ctx.destination(for: src, ext: ext)) { tmp in
                try await MainActor.run {
                    let doc = try open(src)
                    guard let attributed = doc.selectionForEntireDocument?.attributedString, attributed.length > 0 else {
                        throw PickError(L("err.no_text"))
                    }
                    try DocumentEngine.write(attributed, to: tmp, ext: ext)
                }
            }
        }
    }

    /// Renders a page with rotation applied on a white background.
    static func render(_ page: PDFPage, dpi: CGFloat) -> CGImage? {
        let box = page.bounds(for: .mediaBox)
        let quarter = (page.rotation % 180) != 0
        let width = quarter ? box.height : box.width
        let height = quarter ? box.width : box.height
        var scale = dpi / 72
        if max(width, height) * scale > 9000 { scale = 9000 / max(width, height) }
        let pw = max(1, Int((width * scale).rounded())), ph = max(1, Int((height * scale).rounded()))
        guard let ctx = CGContext(data: nil, width: pw, height: ph, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue) else { return nil }
        ctx.setFillColor(CGColor(gray: 1, alpha: 1))
        ctx.fill(CGRect(x: 0, y: 0, width: pw, height: ph))
        ctx.interpolationQuality = .high
        ctx.scaleBy(x: scale, y: scale)
        page.draw(with: .mediaBox, to: ctx)
        return ctx.makeImage()
    }

    static func toImages(_ src: URL, ext: String, dpi: CGFloat, ctx: PickContext) async throws -> URL {
        let doc = try open(src)
        let count = doc.pageCount
        guard count > 0 else { throw PickError(L("err.pdf_empty")) }
        if count == 1 {
            return try await ctx.produce(ctx.destination(for: src, ext: ext)) { tmp in
                guard let page = doc.page(at: 0), let image = render(page, dpi: dpi) else { throw PickError(L("err.render_page")) }
                try await ImageEngine.encode(image, to: tmp, ext: ext, quality: 0.9)
            }
        }
        let base = FileNames.split(src.lastPathComponent).base
        let digits = max(2, String(count).count)
        return try await ctx.produce(ctx.destination(for: src, ext: nil, suffix: ext.uppercased())) { folder in
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            for index in 0..<count {
                try Task.checkCancellation()
                ctx.report(Double(index) / Double(count), L("note.page", "\(index + 1)", "\(count)"))
                guard let page = doc.page(at: index), let image = render(page, dpi: dpi) else { continue }
                let number = String(format: "%0\(digits)d", index + 1)
                try await ImageEngine.encode(image, to: folder.appendingPathComponent("\(base)-\(number).\(ext)"),
                                             ext: ext, quality: 0.9)
            }
        }
    }

    /// Text layer first; pages without one (scans) fall back to on-device OCR.
    static func extractText(_ doc: PDFDocument, ctx: PickContext) throws -> String {
        var pages: [String] = []
        for index in 0..<doc.pageCount {
            try Task.checkCancellation()
            guard let page = doc.page(at: index) else { continue }
            var text = page.string ?? ""
            if text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, let image = render(page, dpi: 200) {
                text = (try? ImageEngine.recognizeText(in: image)) ?? ""
            }
            pages.append(text)
            ctx.report(Double(index + 1) / Double(max(doc.pageCount, 1)))
        }
        let joined = pages.joined(separator: "\n\n").trimmingCharacters(in: .whitespacesAndNewlines)
        guard !joined.isEmpty else { throw PickError(L("err.no_text")) }
        return joined
    }

    static func copyText(_ ctx: PickContext) async throws -> PickResult {
        var chunks: [String] = []
        for file in ctx.files { chunks.append(try extractText(open(file), ctx: ctx)) }
        let text = chunks.joined(separator: "\n\n")
        await MainActor.run {
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(text, forType: .string)
        }
        return .note(LP("msg.chars_copied", text.count))
    }

    static func compress(_ src: URL, ctx: PickContext) async throws -> URL {
        let doc = try open(src)
        ctx.report(nil, L("note.optimizing"))
        let out = try await ctx.produce(ctx.destination(for: src, ext: "pdf", suffix: L("sfx.compressed"))) { tmp in
            let options: [PDFDocumentWriteOption: Any] = [.saveImagesAsJPEGOption: true, .optimizeImagesForScreenOption: true]
            guard doc.write(to: tmp, withOptions: options) else { throw PickError(L("err.pdf_save")) }
        }
        return try ImageEngine.keepIfSmaller(out, than: src)
    }

    static func merge(_ ctx: PickContext) async throws -> PickResult {
        let files = ctx.files.sorted { $0.lastPathComponent.localizedStandardCompare($1.lastPathComponent) == .orderedAscending }
        let merged = PDFDocument()
        for (index, file) in files.enumerated() {
            ctx.report(Double(index) / Double(files.count), file.lastPathComponent)
            let doc = try open(file)
            for pageIndex in 0..<doc.pageCount {
                if let page = doc.page(at: pageIndex)?.copy() as? PDFPage {
                    merged.insert(page, at: merged.pageCount)
                }
            }
        }
        let dst = ctx.destination(for: files[0], ext: "pdf", suffix: L("sfx.merged"))
        let out = try await ctx.produce(dst) { tmp in
            guard merged.write(to: tmp) else { throw PickError(L("err.pdf_save")) }
        }
        return PickResult(outputs: [out])
    }

    static func split(_ src: URL, ctx: PickContext) async throws -> URL {
        let doc = try open(src)
        guard doc.pageCount > 1 else { throw PickError(L("err.single_page")) }
        let base = FileNames.split(src.lastPathComponent).base
        let digits = max(2, String(doc.pageCount).count)
        return try await ctx.produce(ctx.destination(for: src, ext: nil, suffix: L("sfx.pages"))) { folder in
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            for index in 0..<doc.pageCount {
                guard let page = doc.page(at: index)?.copy() as? PDFPage else { continue }
                let single = PDFDocument()
                single.insert(page, at: 0)
                let number = String(format: "%0\(digits)d", index + 1)
                _ = single.write(to: folder.appendingPathComponent("\(base)-\(number).pdf"))
                ctx.report(Double(index + 1) / Double(doc.pageCount))
            }
        }
    }

    static func rotate(_ src: URL, turn: Turn, ctx: PickContext) async throws -> URL {
        let degrees: Int
        switch turn {
        case .left: degrees = 270
        case .half: degrees = 180
        default: degrees = 90
        }
        let doc = try open(src)
        for index in 0..<doc.pageCount {
            if let page = doc.page(at: index) { page.rotation = (page.rotation + degrees) % 360 }
        }
        return try await ctx.produce(ctx.destination(for: src, ext: "pdf", suffix: L("sfx.rotated"))) { tmp in
            guard doc.write(to: tmp) else { throw PickError(L("err.pdf_save")) }
        }
    }

    static func encrypt(_ src: URL, ctx: PickContext) async throws -> URL {
        guard let password = await MainActor.run(body: { askPassword(for: src) }) else { throw CancellationError() }
        let doc = try open(src)
        return try await ctx.produce(ctx.destination(for: src, ext: "pdf", suffix: L("sfx.locked"))) { tmp in
            let options: [PDFDocumentWriteOption: Any] = [.userPasswordOption: password, .ownerPasswordOption: password]
            guard doc.write(to: tmp, withOptions: options) else { throw PickError(L("err.pdf_save")) }
        }
    }

    @MainActor
    static func askPassword(for url: URL) -> String? {
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = L("pdf.password.title")
        alert.informativeText = L("pdf.password.info", url.lastPathComponent)
        let field = NSSecureTextField(frame: NSRect(x: 0, y: 0, width: 260, height: 24))
        field.placeholderString = L("pdf.password.placeholder")
        alert.accessoryView = field
        alert.addButton(withTitle: L("pdf.password.lock"))
        alert.addButton(withTitle: L("common.cancel"))
        alert.window.initialFirstResponder = field
        guard alert.runModal() == .alertFirstButtonReturn, !field.stringValue.isEmpty else { return nil }
        return field.stringValue
    }
}
