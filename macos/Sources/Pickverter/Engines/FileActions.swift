import AppKit
import CryptoKit
import Quartz

struct EditorChoice {
    let label: String
    let bundleID: String

    static let candidates: [(String, String)] = [
        ("VS Code", "com.microsoft.VSCode"),
        ("Cursor", "com.todesktop.230313mzl4w4u92"),
        ("Zed", "dev.zed.Zed"),
        ("Sublime Text", "com.sublimetext.4"),
        ("Nova", "com.panic.Nova"),
        ("BBEdit", "com.barebones.bbedit"),
        ("Xcode", "com.apple.dt.Xcode"),
    ]

    /// Installed code editors, best first; TextEdit when none is installed.
    static var installed: [EditorChoice] {
        let found = candidates
            .filter { NSWorkspace.shared.urlForApplication(withBundleIdentifier: $0.1) != nil }
            .map { EditorChoice(label: $0.0, bundleID: $0.1) }
        return found.isEmpty ? [EditorChoice(label: L("app.textedit"), bundleID: "com.apple.TextEdit")] : found
    }
}

struct QuickFolder {
    let key: String
    let title: String
    let symbol: String
    let url: URL?
}

@MainActor
enum FileActions {
    static func folder(for url: URL) -> URL {
        let values = try? url.resourceValues(forKeys: [.isDirectoryKey, .isPackageKey])
        if values?.isDirectory == true, values?.isPackage != true { return url }
        return url.deletingLastPathComponent()
    }

    static func airDrop(_ urls: [URL]) throws {
        guard let service = NSSharingService(named: .sendViaAirDrop), service.canPerform(withItems: urls) else {
            throw PickError(L("err.airdrop"))
        }
        NSApp.activate(ignoringOtherApps: true)
        service.perform(withItems: urls)
    }

    static func openTerminal(_ urls: [URL], choice: TerminalChoice) throws {
        guard let first = urls.first else { return }
        let appURL = NSWorkspace.shared.urlForApplication(withBundleIdentifier: choice.bundleID)
            ?? NSWorkspace.shared.urlForApplication(withBundleIdentifier: TerminalChoice.terminal.bundleID)
        guard let appURL else { throw PickError(L("err.no_terminal")) }
        let config = NSWorkspace.OpenConfiguration()
        config.activates = true
        NSWorkspace.shared.open([folder(for: first)], withApplicationAt: appURL, configuration: config)
    }

    static func openInEditor(_ urls: [URL], editor: EditorChoice) throws {
        guard let appURL = NSWorkspace.shared.urlForApplication(withBundleIdentifier: editor.bundleID) else {
            throw PickError(L("err.no_app", editor.label))
        }
        let config = NSWorkspace.OpenConfiguration()
        config.activates = true
        NSWorkspace.shared.open(urls, withApplicationAt: appURL, configuration: config)
    }

    static func copyPaths(_ urls: [URL], style: PathStyle) -> PickResult {
        let text = urls.map { style.format($0) }.joined(separator: "\n")
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
        return .note(urls.count > 1 ? LP("msg.paths_copied", urls.count) : L("msg.path_copied", style.format(urls[0])))
    }

    static func copyFiles(_ urls: [URL]) -> PickResult {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.writeObjects(urls as [NSURL])
        return .note(LP("msg.files_copied", urls.count))
    }

    // MARK: Move / copy to

    nonisolated private static let recentsKey = "recentFolders"

    static var recentFolders: [URL] {
        (UserDefaults.standard.stringArray(forKey: recentsKey) ?? [])
            .map { URL(fileURLWithPath: $0) }
            .filter { FileManager.default.fileExists(atPath: $0.path) }
    }

    static func remember(_ folder: URL) {
        var list = (UserDefaults.standard.stringArray(forKey: recentsKey) ?? []).filter { $0 != folder.path }
        list.insert(folder.path, at: 0)
        UserDefaults.standard.set(Array(list.prefix(6)), forKey: recentsKey)
    }

    /// "Choose…", Desktop, Downloads and recently used folders (minus the folder the files are already in).
    nonisolated static func quickFolders(excluding files: [URL]) -> [QuickFolder] {
        let fm = FileManager.default
        let desktop = fm.urls(for: .desktopDirectory, in: .userDomainMask).first
        let downloads = fm.urls(for: .downloadsDirectory, in: .userDomainMask).first
        let current = Set(files.map { $0.deletingLastPathComponent().standardizedFileURL.path })
        var list = [QuickFolder(key: "choose", title: L("folder.choose"), symbol: "ellipsis.circle", url: nil)]
        if let desktop, !current.contains(desktop.standardizedFileURL.path) {
            list.append(QuickFolder(key: "desktop", title: L("folder.desktop"), symbol: "menubar.dock.rectangle", url: desktop))
        }
        if let downloads, !current.contains(downloads.standardizedFileURL.path) {
            list.append(QuickFolder(key: "downloads", title: L("folder.downloads"), symbol: "arrow.down.circle", url: downloads))
        }
        let fixed = Set([desktop, downloads].compactMap { $0?.standardizedFileURL.path })
        let recents = (UserDefaults.standard.stringArray(forKey: recentsKey) ?? [])
            .filter { !fixed.contains($0) && !current.contains($0) && fm.fileExists(atPath: $0) }
            .prefix(4)
        for (index, path) in recents.enumerated() {
            let url = URL(fileURLWithPath: path)
            list.append(QuickFolder(key: "recent\(index)", title: url.lastPathComponent, symbol: "folder", url: url))
        }
        return list
    }

    static func relocate(_ urls: [URL], move: Bool, to target: URL?) throws -> PickResult {
        var destination = target
        if destination == nil {
            NSApp.activate(ignoringOtherApps: true)
            let panel = NSOpenPanel()
            panel.canChooseDirectories = true
            panel.canChooseFiles = false
            panel.canCreateDirectories = true
            panel.allowsMultipleSelection = false
            panel.prompt = move ? L("panel.move.prompt") : L("panel.copy.prompt")
            panel.message = move ? LP("panel.move.message", urls.count) : LP("panel.copy.message", urls.count)
            if let last = recentFolders.first { panel.directoryURL = last }
            guard DialogLevel.runModal(panel, panel.runModal) == .OK, let picked = panel.url else { return .quiet }
            destination = picked
        }
        guard let destination else { return .quiet }
        remember(destination)
        var results: [URL] = []
        for url in urls {
            let item = FileNames.unique(destination.appendingPathComponent(url.lastPathComponent))
            if move { try FileManager.default.moveItem(at: url, to: item) } else { try FileManager.default.copyItem(at: url, to: item) }
            results.append(item)
        }
        let place = destination.lastPathComponent
        return PickResult(outputs: results, message: move ? L("msg.moved_to", place) : L("msg.copied_to", place), undoable: !move)
    }

    // MARK: Share

    private static var shareAnchor: NSPanel?
    private static var shareDelegate: ShareDelegate?

    static func share(_ urls: [URL], at point: CGPoint) {
        let anchor = NSPanel(contentRect: NSRect(x: point.x - 1, y: point.y - 1, width: 2, height: 2),
                             styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        anchor.isOpaque = false
        anchor.backgroundColor = .clear
        anchor.level = .popUpMenu
        anchor.orderFrontRegardless()
        shareAnchor?.orderOut(nil)
        shareAnchor = anchor
        let delegate = ShareDelegate { anchor.orderOut(nil) }
        shareDelegate = delegate
        let picker = NSSharingServicePicker(items: urls)
        picker.delegate = delegate
        NSApp.activate(ignoringOtherApps: true)
        if let view = anchor.contentView {
            picker.show(relativeTo: view.bounds, of: view, preferredEdge: .maxY)
        }
    }

    final class ShareDelegate: NSObject, NSSharingServicePickerDelegate {
        let done: () -> Void
        init(done: @escaping () -> Void) { self.done = done }
        func sharingServicePicker(_ picker: NSSharingServicePicker, didChoose service: NSSharingService?) { done() }
    }

    // MARK: Checksums

    nonisolated static func digest(_ url: URL, algorithm: HashAlgorithm) throws -> String {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        func run<H: HashFunction>(_ hasher: inout H) throws -> String {
            while let chunk = try handle.read(upToCount: 4 << 20), !chunk.isEmpty { hasher.update(data: chunk) }
            return hasher.finalize().map { String(format: "%02x", $0) }.joined()
        }
        switch algorithm {
        case .sha256: var h = SHA256(); return try run(&h)
        case .sha1: var h = Insecure.SHA1(); return try run(&h)
        case .md5: var h = Insecure.MD5(); return try run(&h)
        }
    }

    nonisolated static func copyChecksums(_ ctx: PickContext, algorithm: HashAlgorithm) async throws -> PickResult {
        var lines: [String] = []
        for (index, file) in ctx.files.enumerated() {
            try Task.checkCancellation()
            ctx.report(Double(index) / Double(ctx.files.count), file.lastPathComponent)
            if FileKind.of(file) == .folder { continue }
            let value = try digest(file, algorithm: algorithm)
            lines.append(ctx.files.count > 1 ? "\(value)  \(file.lastPathComponent)" : value)
        }
        guard !lines.isEmpty else { throw PickError(L("err.folder_hash")) }
        let text = lines.joined(separator: "\n")
        await MainActor.run {
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(text, forType: .string)
        }
        return .note(lines.count > 1 ? LP("msg.hashes_copied", lines.count, algorithm.rawValue)
                                     : L("msg.hash_copied", algorithm.rawValue, String(lines[0].prefix(16))))
    }
}

/// Always-on-top Quick Look window per file.
@MainActor
final class FloatWindows: NSObject, NSWindowDelegate {
    static let shared = FloatWindows()
    private var panels: [NSPanel] = []

    func open(_ urls: [URL], near point: CGPoint) {
        for (index, url) in urls.prefix(6).enumerated() {
            var size = CGSize(width: 380, height: 300)
            if let pixels = ImageEngine.pixelSize(url), pixels.width > 0, pixels.height > 0 {
                let scale = min(420 / pixels.width, 420 / pixels.height, 1)
                size = CGSize(width: max(200, pixels.width * scale), height: max(150, pixels.height * scale))
            }
            let origin = CGPoint(x: point.x + 24 + CGFloat(index) * 28, y: point.y - size.height - CGFloat(index) * 28)
            let panel = NSPanel(contentRect: CGRect(origin: origin, size: size),
                                styleMask: [.titled, .closable, .resizable, .utilityWindow, .nonactivatingPanel],
                                backing: .buffered, defer: false)
            panel.title = url.lastPathComponent
            panel.level = .floating
            panel.isFloatingPanel = true
            panel.hidesOnDeactivate = false
            panel.isReleasedWhenClosed = false
            panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
            panel.delegate = self
            if let preview = QLPreviewView(frame: CGRect(origin: .zero, size: size), style: .compact) {
                preview.autoresizingMask = [.width, .height]
                preview.previewItem = url as NSURL
                panel.contentView = preview
            }
            if let screen = ScreenGeometry.screen(containing: point) {
                var frame = panel.frame
                frame.origin.x = min(max(frame.minX, screen.visibleFrame.minX), screen.visibleFrame.maxX - frame.width)
                frame.origin.y = min(max(frame.minY, screen.visibleFrame.minY), screen.visibleFrame.maxY - frame.height)
                panel.setFrame(frame, display: false)
            }
            panel.orderFrontRegardless()
            panels.append(panel)
        }
    }

    func windowWillClose(_ notification: Notification) {
        guard let window = notification.object as? NSPanel else { return }
        (window.contentView as? QLPreviewView)?.close()
        panels.removeAll { $0 === window }
    }
}
