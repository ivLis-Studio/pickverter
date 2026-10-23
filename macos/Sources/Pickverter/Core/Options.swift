import AppKit
import Foundation

enum Turn: String, CaseIterable {
    case right, left, half, flipH, flipV

    var title: String { L("rot." + rawValue) }

    var symbol: String {
        switch self {
        case .right: return "rotate.right"
        case .left: return "rotate.left"
        case .half: return "arrow.triangle.2.circlepath"
        case .flipH: return "arrow.left.and.right.righttriangle.left.righttriangle.right"
        case .flipV: return "arrow.up.and.down.righttriangle.up.righttriangle.down"
        }
    }

    var isFlip: Bool { self == .flipH || self == .flipV }
}

enum CutoutStyle: String, CaseIterable {
    case transparent, crop, white
    var title: String { L("bg." + rawValue) }
}

/// Routes a "convert to X" request to the engine that understands the source file.
enum Converter {
    static func convert(_ src: URL, to ext: String, options: ConvertOptions = ConvertOptions(), ctx: PickContext) async throws -> URL {
        if FileNames.normalizedExt(src) == ext { throw PickSkip() }
        switch FileKind.of(src) {
        case .image:
            if ["mp4", "webm", "mov"].contains(ext) { return try await MediaEngine.convert(src, to: ext, options: options, ctx: ctx) }
            return try await ImageEngine.convert(src, to: ext, ctx: ctx)
        case .video, .audio:
            return try await MediaEngine.convert(src, to: ext, options: options, ctx: ctx)
        case .pdf:
            return try await PDFEngine.convert(src, to: ext, dpi: options.dpi, ctx: ctx)
        case .document:
            if ext == "epub" { return try await EPUBBuilder.fromDocuments([src], ctx: ctx) }
            return try await DocumentEngine.convert(src, to: ext, ctx: ctx)
        case .hwp:
            return try await HWPEngine.convert(src, to: ext, dpi: options.dpi, ctx: ctx)
        case .office:
            return try await OfficeEngine.convert(src, to: ext, ctx: ctx)
        case .table, .json:
            return try await DataEngine.convertTable(src, to: ext, ctx: ctx)
        case .subtitle:
            return try await DataEngine.convertSubtitles(src, to: ext, ctx: ctx)
        case .archive:
            return try await ArchiveEngine.convert(src, to: ext, ctx: ctx)
        case .folder, .other:
            throw PickError(L("err.not_convertible"))
        }
    }
}

enum PathStyle: String, CaseIterable {
    case full, tilde, name, quoted
    var title: String { L("path." + rawValue) }

    func format(_ url: URL) -> String {
        switch self {
        case .full: return url.path
        case .tilde: return (url.path as NSString).abbreviatingWithTildeInPath
        case .name: return url.lastPathComponent
        case .quoted: return "'" + url.path.replacingOccurrences(of: "'", with: "'\\''") + "'"
        }
    }
}

enum HashAlgorithm: String, CaseIterable {
    case sha256 = "SHA-256", sha1 = "SHA-1", md5 = "MD5"
}

enum ActionCatalog {
    static func action(_ id: String, _ title: String, _ detail: String, _ symbol: String, variants: [PickOption] = [],
                       _ perform: ((PickContext) async throws -> PickResult)? = nil) -> PickOption {
        PickOption(id: "action." + id, title: title, detail: detail, symbol: symbol, producesFiles: false, variants: variants,
                   interactive: perform == nil ? (variants.first?.interactive ?? false) : false,
                   perform: perform ?? variants[0].perform)
    }

    static func sub(_ parent: String, _ key: String, _ title: String, symbol: String = "", interactive: Bool = false,
                    _ perform: @escaping (PickContext) async throws -> PickResult) -> PickOption {
        PickOption(id: "action." + parent + "." + key, title: title, detail: "", symbol: symbol, producesFiles: false,
                   interactive: interactive, perform: perform)
    }

    static func options(for files: [URL]) -> [PickOption] {
        let count = files.count
        let preferred = TerminalChoice.current
        let terminals = [preferred] + TerminalChoice.allCases.filter { $0 != preferred && $0.isInstalled }
        let editors = EditorChoice.installed
        let folders = FileActions.quickFolders(excluding: files)

        // "Choose…" opens the folder picker first: its card waits until there is something to move.
        func relocate(_ move: Bool) -> [PickOption] {
            folders.map { folder in
                sub(move ? "move" : "duplicate", folder.key, folder.title, symbol: folder.symbol, interactive: folder.url == nil) { ctx in
                    try await MainActor.run { try FileActions.relocate(ctx.files, move: move, to: folder.url) }
                }
            }
        }

        return [
            action("airdrop", "AirDrop", L("action.airdrop.d"), "dot.radiowaves.left.and.right") { ctx in
                try await MainActor.run { try FileActions.airDrop(ctx.files) }
                return .quiet
            },
            action("terminal", L("action.terminal"), L("action.terminal.d", preferred.label), "terminal",
                   variants: terminals.count > 1 ? terminals.map { choice in
                       sub("terminal", choice.rawValue, choice.label) { ctx in
                           try await MainActor.run { try FileActions.openTerminal(ctx.files, choice: choice) }
                           return .quiet
                       }
                   } : []) { ctx in
                try await MainActor.run { try FileActions.openTerminal(ctx.files, choice: preferred) }
                return .quiet
            },
            action("copypath", L("action.copypath"), count > 1 ? LP("action.copypath.many.d", count) : L("action.copypath.d"), "link",
                   variants: PathStyle.allCases.map { style in
                       sub("copypath", style.rawValue, style.title) { ctx in
                           await MainActor.run { FileActions.copyPaths(ctx.files, style: style) }
                       }
                   }),
            action("copyfile", L("action.copyfile"), L("action.copyfile.d"), "doc.on.doc") { ctx in
                await MainActor.run { FileActions.copyFiles(ctx.files) }
            },
            action("move", L("action.move"), L("action.move.d"), "folder", variants: relocate(true)),
            action("duplicate", L("action.duplicate"), L("action.duplicate.d"), "plus.square.on.square", variants: relocate(false)),
            action("float", L("action.float"), L("action.float.d"), "pin") { ctx in
                await MainActor.run { FloatWindows.shared.open(ctx.files, near: ctx.anchor) }
                return .quiet
            },
            action("share", L("action.share"), L("action.share.d"), "square.and.arrow.up") { ctx in
                await MainActor.run { FileActions.share(ctx.files, at: ctx.anchor) }
                return .quiet
            },
            action("editor", editors[0].label, L("action.editor.d", editors[0].label), "chevron.left.forwardslash.chevron.right",
                   variants: editors.count > 1 ? editors.map { editor in
                       sub("editor", editor.bundleID, editor.label) { ctx in
                           try await MainActor.run { try FileActions.openInEditor(ctx.files, editor: editor) }
                           return .quiet
                       }
                   } : []) { ctx in
                try await MainActor.run { try FileActions.openInEditor(ctx.files, editor: editors[0]) }
                return .quiet
            },
            action("hash", L("action.hash"), L("action.hash.d"), "number", variants: HashAlgorithm.allCases.map { algorithm in
                sub("hash", algorithm.rawValue.lowercased(), algorithm.rawValue) { ctx in
                    try await FileActions.copyChecksums(ctx, algorithm: algorithm)
                }
            }),
        ]
    }
}
