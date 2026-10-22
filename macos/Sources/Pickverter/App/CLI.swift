import AppKit
import SwiftUI

/// Developer commands that exercise engines and render UI without the event tap:
///   Pickverter cli list <files…>
///   Pickverter cli run <option-id> <files…>
///   Pickverter cli snapshot <out.png> <convert|tools|actions|choice> <slot|-1> <option|-1> <light|dark> <files…>
///   Pickverter cli card <out.png> <light|dark> <file>
///   Pickverter cli menu <out.png>
///   Pickverter cli selftest <file>
///   Pickverter cli icon <iconset-dir>
///   Pickverter cli i18n-plist <Resources-dir>
/// PICKVERTER_LANG=ja selects the UI language.
@MainActor
enum CLI {
    /// True while a developer command renders UI, so views skip first-run bookkeeping.
    static var isSnapshotting = false

    static func run(_ args: [String]) -> Int32 {
        _ = NSApplication.shared
        isSnapshotting = true
        guard let command = args.first else { return usage() }
        let rest = Array(args.dropFirst())
        switch command {
        case "list": return list(rest.map { URL(fileURLWithPath: $0) })
        case "run": return runOption(rest)
        case "snapshot": return snapshot(rest)
        case "card": return cardSnapshot(rest)
        case "menu": return menuSnapshot(rest)
        case "selftest": return selfTest(rest.map { URL(fileURLWithPath: $0) })
        case "icon": return rest.first.map { renderIcons(into: $0) } ?? usage()
        case "i18n-plist": return rest.first.map { exportInfoPlistStrings(into: $0) } ?? usage()
        case "fixtures": return rest.first.map { makeFixtures($0) } ?? usage()
        case "editor-snapshot": return editorSnapshot(rest)
        case "update-keygen": return rest.first.map { updateKeygen($0) } ?? usage()
        case "update-pubkey": return updatePublicKey(rest.first)
        case "update-manifest": return updateManifest(rest)
        case "update-verify": return updateVerify(rest)
        case "update-check": return updateCheck()
        case "update-selftest": return rest.first.map { updateSelfTest($0) } ?? usage()
        case "symbols": return checkSymbols()
        case "inspect": return rest.first.map { inspect($0) } ?? usage()
        case "ffmpeg-install": return ffmpegInstall()
        default: return usage()
        }
    }

    static func usage() -> Int32 {
        print("usage: Pickverter cli list|run|snapshot|card|menu|selftest|icon|i18n-plist|fixtures|editor-snapshot|symbols|"
              + "update-keygen|update-manifest|update-verify|update-check|update-selftest|ffmpeg-install …")
        return 64
    }

    /// Downloads ffmpeg exactly like Settings does (PICKVERTER_FFMPEG_DIR picks the folder).
    static func ffmpegInstall() -> Int32 {
        let started = Date()
        do {
            try wait {
                try await FFmpegInstaller.shared.install { fraction in
                    let percent = Int((fraction * 100).rounded(.down))
                    if percent % 25 == 0 { print("  … \(percent)%") }
                }
            }
            print(String(format: "OK %@ · %@ · %.1fs", Tools.ffmpeg ?? "?", FFmpegInstaller.installedVersion ?? "?",
                         Date().timeIntervalSince(started)))
            return 0
        } catch {
            print("ERR \(error.localizedDescription)")
            return 1
        }
    }

    static func list(_ files: [URL]) -> Int32 {
        for mode in WheelMode.allCases {
            print("[\(mode.title)]")
            for option in Catalog.options(for: files, mode: mode) {
                let flag = option.unavailable.map { "  ⚠︎ \($0)" } ?? ""
                print("  \(option.id.padding(toLength: 26, withPad: " ", startingAt: 0)) \(option.title) — \(option.detail)\(flag)")
                for variant in option.variants {
                    print("      \(variant.id.padding(toLength: 30, withPad: " ", startingAt: 0)) \(variant.title)")
                }
            }
        }
        return 0
    }

    static func pump(_ seconds: Double) {
        let until = Date(timeIntervalSinceNow: seconds)
        while Date() < until { RunLoop.main.run(mode: .default, before: Date(timeIntervalSinceNow: 0.03)) }
    }

    static func wait<T>(_ operation: @escaping () async throws -> T) throws -> T {
        var result: Result<T, Error>?
        Task { @MainActor in
            do { result = .success(try await operation()) } catch { result = .failure(error) }
        }
        while result == nil { RunLoop.main.run(mode: .default, before: Date(timeIntervalSinceNow: 0.02)) }
        return try result!.get()
    }

    static func runOption(_ args: [String]) -> Int32 {
        guard args.count >= 2 else { return usage() }
        let files = args.dropFirst().map { URL(fileURLWithPath: $0) }
        guard var option = Catalog.allOptions(for: files).first(where: { $0.id == args[0] }) else {
            print("no option \(args[0]) for these files")
            return 2
        }
        // Like a click on the wheel: a missing ffmpeg is downloaded first.
        if option.unavailable == Tools.ffmpegHint { option = FFmpegInstaller.preparing(option) }
        if let reason = option.unavailable { print("unavailable: \(reason)"); return 3 }
        let destination = ResultDestination(rawValue: ProcessInfo.processInfo.environment["PICKVERTER_DEST"] ?? "") ?? .file
        var lastNote: String?
        let context = PickContext(files: files, location: .sameFolder, destination: destination) { _, note in
            if let note, note != lastNote, !note.isEmpty {
                lastNote = note
                print("  … \(note)")
            }
        }
        let started = Date()
        do {
            let result = try wait { try await option.perform(context) }
            for output in result.outputs {
                print("OUT \(output.path) (\(FileNames.formatBytes(FileNames.size(of: output))))")
            }
            if destination.copies, option.producesFiles, !result.outputs.isEmpty {
                ClipboardWriter.write(result.outputs)
                print("CLIP \(NSPasteboard.general.types?.map(\.rawValue).joined(separator: ", ") ?? "")")
            }
            if let message = result.message { print("MSG \(message)") }
            print(String(format: "OK %.2fs", Date().timeIntervalSince(started)))
            return 0
        } catch {
            print("ERR \(error.localizedDescription)")
            return 1
        }
    }

    static func writePNG(_ image: CGImage, to url: URL) -> Bool {
        guard let dest = CGImageDestinationCreateWithURL(url as CFURL, "public.png" as CFString, 1, nil) else { return false }
        CGImageDestinationAddImage(dest, image, nil)
        return CGImageDestinationFinalize(dest)
    }

    static func render<V: View>(_ view: V, to path: String, dark: Bool) -> Int32 {
        let renderer = ImageRenderer(content: view
            .environment(\.snapshotRendering, true)
            .environment(\.colorScheme, dark ? .dark : .light))
        renderer.scale = 2
        guard let image = renderer.cgImage, writePNG(image, to: URL(fileURLWithPath: path)) else {
            print("render failed")
            return 1
        }
        print(path)
        return 0
    }

    static func snapshot(_ args: [String]) -> Int32 {
        guard args.count >= 6 else { return usage() }
        let modeName = args[1]
        let mode: WheelMode = ["tools": .tools, "actions": .actions][modeName] ?? .convert
        let slot = Int(args[2]) ?? -1
        let sub = Int(args[3]) ?? -1
        let dark = args[4] == "dark"
        let files = args.dropFirst(5).map { URL(fileURLWithPath: $0) }
        let wheel = WheelController(demo: true)
        wheel.present(files: files, origin: .selection, mode: mode, at: .zero)
        if modeName == "choice" {
            wheel.demoChoice()
            if slot >= 0 { wheel.demoHighlight(slot) }
        } else {
            wheel.demoHighlight(slot >= 0 ? slot : nil, sub: sub >= 0 ? sub : nil)
        }
        pump(0.8)
        let size = wheel.geometry.windowSize
        let view = ZStack {
            (dark ? Color(hex: 0x1E1E22) : Color(hex: 0xE4E6EB))
            WheelView(model: wheel)
        }
        .frame(width: size.width + 40, height: size.height + 20)
        return render(view, to: args[0], dark: dark)
    }

    static func cardSnapshot(_ args: [String]) -> Int32 {
        guard args.count >= 3 else { return usage() }
        let dark = args[1] == "dark"
        let file = URL(fileURLWithPath: args[2])
        let option = PickOption(id: "convert.png", title: "PNG", detail: "", symbol: "photo", isFormat: true) { _ in PickResult() }
        let done = Job(option: option, mode: .convert, files: [file], destination: .file)
        done.complete(with: PickResult(outputs: [file]))
        let clip = Job(option: option, mode: .convert, files: [file], destination: .clipboard)
        let running = Job(option: Catalog.tool("removebg", symbol: "scissors") { _ in PickResult() }, mode: .tools, files: [file], destination: .file)
        running.progress = 0.42
        let failed = Job(option: option, mode: .convert, files: [file], destination: .file)
        failed.state = .failed(L("err.already_small"))
        let blocked = Job(option: option, mode: .convert, files: [file], destination: .file)
        blocked.state = .failed(L("err.no_access", L("folder.documents")))
        blocked.fixAction = Job.NoticeAction(title: L("access.fix"), symbol: "lock.open") {}
        pump(0.6)
        let view = VStack(spacing: 12) {
            JobCard(job: running, onClose: {}, onHover: { _ in })
            JobCard(job: done, onClose: {}, onHover: { _ in })
            JobCard(job: clip, onClose: {}, onHover: { _ in })
            JobCard(job: failed, onClose: {}, onHover: { _ in })
            JobCard(job: blocked, onClose: {}, onHover: { _ in })
        }
        .padding(20)
        .background(dark ? Color(hex: 0x1E1E22) : Color(hex: 0xE4E6EB))
        _ = clip
        return render(view, to: args[0], dark: dark)
    }

    static func menuSnapshot(_ args: [String]) -> Int32 {
        guard let path = args.first else { return usage() }
        let sample = PickOption(id: "convert.png", title: "PNG", detail: "", symbol: "photo", isFormat: true) { _ in PickResult() }
        JobCenter.shared.run(sample, mode: .convert, files: [URL(fileURLWithPath: "/tmp/sample.heic")], anchor: .zero)
        pump(0.5)
        let view = MenuBarView()
            .environmentObject(Preferences.shared)
            .environmentObject(Permissions.shared)
            .environmentObject(JobCenter.shared)
            .background(Color(nsColor: .windowBackgroundColor))
        return render(view, to: path, dark: false)
    }

    /// Feeds synthetic key events straight into a hidden wheel and checks where the highlight lands.
    static func selfTest(_ files: [URL]) -> Int32 {
        let wheel = WheelController(demo: true)
        wheel.present(files: files, origin: .selection, mode: .convert, at: .zero)
        wheel.forceVisibleForTesting()
        var failures = 0
        func press(_ code: CGKeyCode, command: Bool = false) {
            let event = CGEvent(keyboardEventSource: nil, virtualKey: code, keyDown: true)!
            event.flags = command ? .maskCommand : []
            _ = wheel.handleKey(code: Int64(code), event: event)
        }
        func expect(_ label: String, _ actual: String?, _ expected: String) {
            let ok = actual == expected
            if !ok { failures += 1 }
            print("\(ok ? "PASS" : "FAIL") \(label): \(actual ?? "nil")\(ok ? "" : " (expected \(expected))")")
        }
        /// The slot's own title (the hub may show its default variant instead).
        func slotTitle() -> String? { wheel.activeOption.map { $0.parent?.title ?? $0.option.title } }
        let formats = wheel.items.compactMap { $0.option?.title }
        press(13); press(14)
        expect("type 'we'", slotTitle(), "WebP")
        let webp = formats.firstIndex(of: "WebP") ?? 0
        press(124)
        expect("→", slotTitle(), formats[(webp + 1) % formats.count])
        press(123); press(123)
        expect("← ←", slotTitle(), formats[(webp - 1 + formats.count) % formats.count])
        press(48)
        expect("Tab", wheel.mode.title, L("mode.tools"))
        press(124)
        expect("→ first tool", slotTitle(), L("tool.compress"))
        expect("hub shows the default option", wheel.activeOption?.option.title, L("q.balanced"))
        press(125)
        expect("↓ option", wheel.activeOption?.option.title, L("q.balanced"))
        press(125)
        expect("↓ option", wheel.activeOption?.option.title, L("q.high"))
        let tools = wheel.items.compactMap { $0.option?.id }
        for _ in 0..<(tools.firstIndex(of: "tool.rotate") ?? 0) { press(124) }
        expect("→ … rotate", slotTitle(), L("tool.rotate"))
        press(126)
        expect("↑ wraps to last", wheel.activeOption?.option.title, L("rot.flipV"))
        press(48)
        expect("Tab", wheel.mode.title, L("mode.actions"))
        press(124)
        expect("→ in actions", wheel.activeOption?.option.title, "AirDrop")
        press(53)
        expect("esc hides", wheel.isVisible ? "visible" : "hidden", "hidden")
        failures += pointerTests(files)
        failures += layoutTests(files)
        return failures == 0 ? 0 : 1
    }

    static func renderIcons(into dir: String) -> Int32 {
        let folder = URL(fileURLWithPath: dir)
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let entries: [(String, CGFloat)] = [
            ("icon_16x16.png", 16), ("icon_16x16@2x.png", 32), ("icon_32x32.png", 32), ("icon_32x32@2x.png", 64),
            ("icon_128x128.png", 128), ("icon_128x128@2x.png", 256), ("icon_256x256.png", 256),
            ("icon_256x256@2x.png", 512), ("icon_512x512.png", 512), ("icon_512x512@2x.png", 1024),
        ]
        for (name, pixels) in entries {
            let renderer = ImageRenderer(content: AppIconArt().frame(width: pixels, height: pixels))
            renderer.scale = 1
            guard let image = renderer.cgImage, writePNG(image, to: folder.appendingPathComponent(name)) else {
                print("failed \(name)")
                return 1
            }
        }
        print(folder.path)
        return 0
    }

    /// Writes <lang>.lproj/InfoPlist.strings so system prompts (Finder control) are localized too.
    static func exportInfoPlistStrings(into dir: String) -> Int32 {
        let root = URL(fileURLWithPath: dir)
        for language in Loc.languages {
            Loc.apply(preference: language.code)
            let folder = root.appendingPathComponent("\(language.code).lproj")
            try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            let escape: (String) -> String = { $0.replacingOccurrences(of: "\"", with: "\\\"") }
            var body = "\"NSAppleEventsUsageDescription\" = \"\(escape(L("plist.apple_events")))\";\n"
            for key in ["NSDesktopFolderUsageDescription", "NSDocumentsFolderUsageDescription", "NSDownloadsFolderUsageDescription",
                        "NSRemovableVolumesUsageDescription", "NSNetworkVolumesUsageDescription"] {
                body += "\"\(key)\" = \"\(escape(L("plist.files")))\";\n"
            }
            try? body.write(to: folder.appendingPathComponent("InfoPlist.strings"), atomically: true, encoding: .utf8)
        }
        print(root.path)
        return 0
    }
}
