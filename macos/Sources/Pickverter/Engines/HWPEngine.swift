import AppKit
import Foundation

/// HWP and HWPX documents (Hangul Word Processor) through rhwp, the open-source HWP engine Pickverter downloads on
/// first use (`HWPInstaller`). rhwp lays pages out the way Hangul does, so PDFs and page images come straight from it,
/// and so does HWP ↔ HWPX. DOCX, HTML, Markdown and plain text start from its DocLang export, which keeps
/// paragraphs, headings, lists, merged table cells and pictures.
enum HWPEngine {
    static let exts: Set<String> = ["hwp", "hwpx"]

    static func targets(for source: String) -> [String] {
        ["pdf", "docx", source == "hwpx" ? "hwp" : "hwpx", "png", "jpg", "txt", "md", "html"]
    }

    /// Why HWP options can't run yet: rhwp isn't downloaded (the job downloads it first).
    static var unavailable: String? { HWPInstaller.executable == nil ? Tools.hwpHint : nil }

    // MARK: Converting

    static func convert(_ src: URL, to ext: String, dpi: CGFloat = 300, ctx: PickContext) async throws -> URL {
        let session = Session(src)
        switch ext {
        case "pdf":
            ctx.report(nil, L("note.rendering"))
            return try await session.unlocking {
                try await ctx.produce(ctx.destination(for: src, ext: "pdf")) { tmp in
                    try await session.run(["export-pdf", src.path, "-o", tmp.path], unlock: false)
                }
            }
        case "png", "jpg":
            ctx.report(nil, L("note.rendering"))
            let scratch = try Scratch()
            defer { scratch.remove() }
            let pdf = scratch.url.appendingPathComponent("document.pdf")
            try await session.run(["export-pdf", src.path, "-o", pdf.path])
            return try await PDFEngine.toImages(pdf, ext: ext, dpi: dpi, ctx: ctx, named: src)
        case "hwpx", "hwp":
            // rhwp names its own output: export-hwpx writes HWPX, convert writes HWP (and wants the .hwp name).
            let command = ext == "hwpx" ? "export-hwpx" : "convert"
            return try await session.unlocking {
                try await ctx.produce(ctx.destination(for: src, ext: ext)) { tmp in
                    try await session.run([command, src.path, tmp.path], unlock: false)
                }
            }
        default:
            ctx.report(nil, L("note.reading"))
            let document = try await read(src, session: session)
            let title = FileNames.split(src.lastPathComponent).base
            return try await ctx.produce(ctx.destination(for: src, ext: ext)) { tmp in
                switch ext {
                case "txt":
                    try document.plainText.write(to: tmp, atomically: true, encoding: .utf8)
                case "md":
                    try document.markdown.write(to: tmp, atomically: true, encoding: .utf8)
                case "html":
                    try document.html(title: title).write(to: tmp, atomically: true, encoding: .utf8)
                case "docx":
                    try DocxWriter.write(document, title: title, to: tmp)
                default:
                    let html = document.html(title: title)
                    try await MainActor.run {
                        let attributed = try NSAttributedString(data: Data(html.utf8),
                                                                options: [.documentType: NSAttributedString.DocumentType.html,
                                                                          .characterEncoding: String.Encoding.utf8.rawValue],
                                                                documentAttributes: nil)
                        try DocumentEngine.write(attributed, to: tmp, ext: ext)
                    }
                }
            }
        }
    }

    // MARK: Text

    /// The document tree from rhwp's DocLang export. DocLang can't open encrypted or distribution (read-only)
    /// documents: rhwp first rewrites those as a plain HWPX in the scratch folder (asking for the password when there
    /// is one), and DocLang reads that.
    static func read(_ src: URL, session: Session) async throws -> DocLang.Document {
        let scratch = try Scratch()
        defer { scratch.remove() }
        let input = "document." + (FileNames.normalizedExt(src) == "hwpx" ? "hwpx" : "hwp")
        try FileManager.default.copyItem(at: src, to: scratch.url.appendingPathComponent(input))
        func export(_ name: String) async throws {
            // Pictures stay inline (data: URIs): rhwp 0.8.7 names the files of pictures in table cells differently
            // from the URIs it writes for them, so an assets folder would lose those.
            try await session.run(["export-doclang", name, "-o", "document.xml"],
                                  cwd: scratch.url, unlock: false, sendPassword: false)
        }
        do {
            try await export(input)
        } catch {
            if error is CancellationError { throw error }
            try await session.run(["export-hwpx", input, "unlocked.hwpx"], cwd: scratch.url)
            do {
                try await export("unlocked.hwpx")
            } catch is Locked {
                throw PickError(L("err.hwp_open"))
            }
        }
        let data = try Data(contentsOf: scratch.url.appendingPathComponent("document.xml"))
        do {
            return try DocLang.parse(data, base: scratch.url)
        } catch {
            Debug.log("doclang: \(error)")
            throw PickError(L("err.hwp_open"))
        }
    }

    static func text(of files: [URL], ctx: PickContext) async throws -> String {
        var chunks: [String] = []
        for (index, file) in files.enumerated() {
            try Task.checkCancellation()
            ctx.report(Double(index) / Double(max(files.count, 1)), L("note.reading"))
            let text = try await read(file, session: Session(file)).plainText
            chunks.append(text.trimmingCharacters(in: .whitespacesAndNewlines))
        }
        let text = chunks.filter { !$0.isEmpty }.joined(separator: "\n\n")
        guard !text.isEmpty else { throw PickError(L("err.no_text")) }
        return text
    }

    static func copyText(_ ctx: PickContext) async throws -> PickResult {
        let text = try await text(of: ctx.files, ctx: ctx)
        await MainActor.run {
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(text, forType: .string)
        }
        return .note(LP("msg.chars_copied", text.count))
    }

    static func count(_ ctx: PickContext) async throws -> PickResult {
        .note(DocumentEngine.countMessage(try await text(of: ctx.files, ctx: ctx)))
    }

    // MARK: Running rhwp

    /// rhwp stopped at the locked document: no password yet, or the last one didn't match.
    struct Locked: Error {
        var wrong = false
    }

    enum Problem {
        case needsPassword, wrongPassword, other

        /// rhwp explains failures in Korean on stderr; the password ones are the only ones Pickverter can fix.
        init(_ stderr: String) {
            if stderr.contains("일치하지 않") {
                self = .wrongPassword
            } else if stderr.contains("비밀번호") || stderr.range(of: "encrypt", options: .caseInsensitive) != nil {
                self = .needsPassword
            } else {
                self = .other
            }
        }
    }

    /// One document's rhwp runs. Once the user typed the password it is reused, so a conversion asks only once.
    final class Session: @unchecked Sendable {
        let source: URL
        private(set) var password: String?

        init(_ source: URL) { self.source = source }

        /// `unlock: false` reports a locked document as `Locked` instead of asking (inside `ctx.produce`, where a
        /// dialog would hold a queue slot; `unlocking` around it asks); `sendPassword: false` runs without the
        /// password (on a copy that no longer needs one).
        func run(_ arguments: [String], cwd: URL? = nil, unlock: Bool = true, sendPassword: Bool = true) async throws {
            if unlock {
                try await unlocking { try await self.run(arguments, cwd: cwd, unlock: false, sendPassword: sendPassword) }
                return
            }
            guard let rhwp = HWPInstaller.executable else { throw PickError(Tools.hwpHint) }
            try Task.checkCancellation()
            let secret = sendPassword ? password : nil
            do {
                try await Shell.run(rhwp, arguments + (secret == nil ? [] : ["--password-stdin"]), cwd: cwd,
                                    input: secret.map { $0 + "\n" })
            } catch let failure as Shell.Failure {
                let problem = Problem(failure.stderr)
                guard problem != .other else {
                    Debug.log("rhwp \(arguments.first ?? ""): \(failure.stderr.suffix(800))")
                    throw PickError(L("err.hwp_open"))
                }
                throw Locked(wrong: problem == .wrongPassword)
            }
        }

        /// Runs work that may meet the locked document and asks for its password between attempts, never while the
        /// work holds a queue slot or a half-written result. Three tries.
        func unlocking<T>(_ work: () async throws -> T) async throws -> T {
            var asked = 0
            while true {
                do {
                    return try await work()
                } catch let locked as Locked {
                    if asked >= 3 { throw PickError(L("err.hwp_password")) }
                    let source = source
                    guard let answer = await MainActor.run(body: { HWPEngine.askPassword(for: source, wrong: locked.wrong) }) else {
                        throw CancellationError()
                    }
                    password = answer
                    asked += 1
                }
            }
        }
    }

    /// The document's open password, above the result cards. Tests answer through PICKVERTER_EDITOR_UNLOCK.
    @MainActor
    static func askPassword(for url: URL, wrong: Bool) -> String? {
        if let injected = ProcessInfo.processInfo.environment["PICKVERTER_EDITOR_UNLOCK"] {
            return injected == "cancel" || injected.isEmpty ? nil : injected
        }
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = wrong ? L("err.hwp_password") : L("hwp.password.title")
        alert.informativeText = L("hwp.password.info", url.lastPathComponent)
        let field = NSSecureTextField(frame: NSRect(x: 0, y: 0, width: 260, height: 24))
        field.placeholderString = L("pdf.password.placeholder")
        alert.accessoryView = field
        alert.addButton(withTitle: L("hwp.password.open"))
        alert.addButton(withTitle: L("common.cancel"))
        alert.window.initialFirstResponder = field
        guard DialogLevel.runModal(alert.window, alert.runModal) == .alertFirstButtonReturn, !field.stringValue.isEmpty else { return nil }
        return field.stringValue
    }

    /// A private temporary folder for one conversion.
    struct Scratch {
        let url: URL

        init() throws {
            url = FileManager.default.temporaryDirectory.appendingPathComponent("pickverter-hwp-" + UUID().uuidString, isDirectory: true)
            try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        }

        func remove() { try? FileManager.default.removeItem(at: url) }
    }
}

