import AppKit
import AVFoundation
import SwiftUI

/// Release tooling, fixtures and the extended self-test.
extension CLI {
    // MARK: Self-test: mouse, drop and destination flows

    static func check(_ label: String, _ actual: String?, _ expected: String, _ failures: inout Int) {
        let ok = actual == expected
        if !ok { failures += 1 }
        print("\(ok ? "PASS" : "FAIL") \(label): \(actual ?? "nil")\(ok ? "" : " (expected \(expected))")")
    }

    static func pointerTests(_ files: [URL]) -> Int {
        var failures = 0
        let wheel = WheelController(demo: true)
        var launched: (id: String, destination: ResultDestination)?
        wheel.onLaunch = { option, destination in launched = (option.id, destination) }
        wheel.destinationOverride = .file
        let g = wheel.geometry
        let middle = (g.innerRadius + g.outerRadius) / 2
        let outer = (g.subInner + g.subOuter) / 2
        func point(_ angle: Double, _ radius: CGFloat) -> CGPoint {
            CGPoint(x: wheel.center.x + CGFloat(sin(angle)) * radius, y: wheel.center.y + CGFloat(cos(angle)) * radius)
        }
        func open(_ mode: WheelMode, origin: WheelController.Origin = .selection) {
            wheel.present(files: files, origin: origin, mode: mode, at: CGPoint(x: 600, y: 500))
            wheel.forceVisibleForTesting()
        }
        func press(_ code: CGKeyCode) {
            let event = CGEvent(keyboardEventSource: nil, virtualKey: code, keyDown: true)!
            event.flags = []
            _ = wheel.handleKey(code: Int64(code), event: event)
        }

        open(.tools)
        guard let rotate = wheel.items.firstIndex(where: { $0.id == "tool.rotate" }) else {
            print("FAIL no rotate slot for \(files.first?.lastPathComponent ?? "?")")
            return 1
        }
        let span = g.mainSpan(wheel.items.count)
        wheel.pointerMoved(appKit: point(Double(rotate) * span, middle))
        check("mouse over a slot", wheel.activeOption?.parent?.id, "tool.rotate", &failures)
        check("slot shows its default", wheel.activeOption?.implied == true ? wheel.activeOption?.option.id : "explicit",
              "tool.rotate.right", &failures)
        if let ring = wheel.subRing {
            wheel.pointerMoved(appKit: point(ring.angle(of: 1), outer))
            check("mouse on the outer ring", wheel.activeOption?.option.id, "tool.rotate.left", &failures)
            wheel.commit()
            check("click runs that option", launched?.id, "tool.rotate.left", &failures)
            check("…as a file", launched?.destination.rawValue, "file", &failures)
        } else {
            failures += 1
            print("FAIL outer ring did not open")
        }

        launched = nil
        open(.tools, origin: .drag)
        wheel.pointerMoved(appKit: point(Double(rotate) * span, middle))
        if let ring = wheel.subRing {
            let accepted = wheel.performDrop(at: point(ring.angle(of: 2), outer), files: files)
            check("drop on an outer option", accepted ? launched?.id : "rejected", "tool.rotate.half", &failures)
        } else {
            failures += 1
            print("FAIL outer ring did not open while dragging")
        }

        launched = nil
        wheel.destinationOverride = .ask
        open(.convert)
        wheel.pointerMoved(appKit: point(0, middle))
        let format = wheel.activeOption?.option.id
        wheel.commit()
        check("ask: wheel turns into a choice", wheel.choice != nil ? "choice" : "none", "choice", &failures)
        check("ask: three destinations", "\(wheel.items.count)", "3", &failures)
        press(124)
        press(36)
        check("ask: → then ↩︎ copies", launched.map { "\($0.id) \($0.destination.rawValue)" },
              "\(format ?? "?") clipboard", &failures)

        launched = nil
        wheel.destinationOverride = .file
        open(.convert)
        wheel.pointerMoved(appKit: point(0, middle))
        wheel.commit(flip: true)
        check("⌥ flips file -> clipboard", launched?.destination.rawValue, "clipboard", &failures)
        open(.convert)
        wheel.pointerMoved(appKit: point(0, middle))
        wheel.commit(forced: .file)
        check("⌘S forces a file", launched?.destination.rawValue, "file", &failures)

        open(.convert)
        let tab = wheel.geometry.tabRect(.actions, wheelCenter: wheel.center)
        let quartz = CGPoint(x: tab.midX, y: ScreenGeometry.primaryHeight - tab.midY)
        _ = wheel.handleClick(quartz: quartz, flags: [])
        check("click on a tab switches rings", wheel.mode.title, L("mode.actions"), &failures)
        return failures
    }

    static func layoutTests(_ files: [URL]) -> Int {
        var failures = 0
        // Folder-access errors: TCC refusals (EPERM) get the "Grant Access" treatment, plain Unix permissions don't.
        let tcc = NSError(domain: NSCocoaErrorDomain, code: NSFileReadNoPermissionError,
                          userInfo: [NSUnderlyingErrorKey: NSError(domain: NSPOSIXErrorDomain, code: Int(EPERM))])
        let unix = NSError(domain: NSCocoaErrorDomain, code: NSFileWriteNoPermissionError,
                           userInfo: [NSUnderlyingErrorKey: NSError(domain: NSPOSIXErrorDomain, code: Int(EACCES))])
        let tool = Shell.Failure(tool: "ffmpeg", status: 1, stderr: "x.mov: Operation not permitted")
        check("blocked folder is recognized", "\(FileAccess.isAccessDenied(tcc)) \(FileAccess.isAccessDenied(tool))", "true true", &failures)
        check("unix permissions are not", "\(FileAccess.isAccessDenied(unix))", "false", &failures)
        if let desktop = ProtectedFolder.desktop.url {
            check("blocked place is named", FileAccess.placeName(for: desktop.appendingPathComponent("a/b.png")), L("folder.desktop"), &failures)
        }
        let suite = "com.pickverter.selftest.\(UUID().uuidString)"
        guard let store = UserDefaults(suiteName: suite) else { return 1 }
        defer { store.removePersistentDomain(forName: suite) }
        let layout = WheelLayout(store: store)
        let key = WheelLayout.key(mode: .tools, files: files)
        let options = Catalog.options(for: files, mode: .tools)
        let natural = options.map(\.id)
        layout.hide("tool.ocr", title: "OCR")
        var ids = layout.arrange(options, key: key).map(\.id)
        check("hide a slot", ids.contains("tool.ocr") ? "shown" : "hidden", "hidden", &failures)
        let before = ids.firstIndex(of: "tool.rotate") ?? -1
        layout.move("tool.rotate", by: -1, visible: ids, key: key)
        ids = layout.arrange(options, key: key).map(\.id)
        check("move a slot earlier", "\(ids.firstIndex(of: "tool.rotate") ?? -1)", "\(before - 1)", &failures)
        layout.pin("tool.resize.1920", title: "Resize · 1920px", key: key)
        ids = layout.arrange(options, key: key).map(\.id)
        let resize = ids.firstIndex(of: "tool.resize") ?? -9
        check("pin an option next to its slot", "\(ids.firstIndex(of: "tool.resize.1920") ?? -1)", "\(resize + 1)", &failures)
        if let rotate = options.first(where: { $0.id == "tool.rotate" }) {
            layout.setDefault(slot: rotate.id, variant: "tool.rotate.left")
            check("explicit default option", rotate.variants[layout.preferredVariant(of: rotate)].id, "tool.rotate.left", &failures)
            layout.setDefault(slot: rotate.id, variant: nil)
            layout.noteUsed(slot: rotate.id, variant: "tool.rotate.half")
            check("remembers the last option", rotate.variants[layout.preferredVariant(of: rotate)].id, "tool.rotate.half", &failures)
        }
        layout.resetAll()
        check("reset restores the stock wheel", layout.arrange(options, key: key).map(\.id) == natural ? "stock" : "custom", "stock", &failures)
        return failures
    }

    /// Every SF Symbol the catalog uses must exist on this macOS.
    static func checkSymbols() -> Int32 {
        let samples = ["a.jpg", "b.png", "c.mov", "d.mp3", "e.pdf", "f.md", "g.docx", "h.xlsx", "i.pptx", "j.key", "k.pages",
                       "l.csv", "m.json", "n.srt", "o.zip"]
        var missing = Set<String>()
        for sample in samples {
            for count in [1, 2] {
                let files = (0..<count).map { URL(fileURLWithPath: "/tmp/pickverter-symbols/\($0)-" + sample) }
                for option in Catalog.allOptions(for: files) where !option.symbol.isEmpty {
                    if NSImage(systemSymbolName: option.symbol, accessibilityDescription: nil) == nil {
                        missing.insert("\(option.symbol) (\(option.id))")
                    }
                }
            }
        }
        let ui = ["circle.circle", "arrow.down.circle", "arrow.down.circle.fill", "pin", "pin.slash", "minus.square",
                  "person.crop.square", "text.viewfinder", "rotate.left", "rotate.right", "plus.square.on.square", "trash",
                  "arrow.up.arrow.down", "ellipsis.circle", "checkmark.circle", "eye.slash", "arrow.counterclockwise",
                  "arrow.clockwise", "arrow.uturn.backward", "gearshape", "list.bullet", "info.circle"]
        for symbol in ui where NSImage(systemSymbolName: symbol, accessibilityDescription: nil) == nil { missing.insert(symbol) }
        for item in missing.sorted() { print("MISSING \(item)") }
        print(missing.isEmpty ? "OK all symbols exist" : "FAIL \(missing.count) missing")
        return missing.isEmpty ? 0 : 1
    }

    // MARK: Editor snapshots (visual QA without showing windows)

    /// Facts about a result file for scripts/test.sh: size=, pages=, duration=, gps=, title=, files=.
    static func inspect(_ path: String) -> Int32 {
        let url = URL(fileURLWithPath: path)
        var isDir: ObjCBool = false
        guard FileManager.default.fileExists(atPath: path, isDirectory: &isDir) else {
            print("missing")
            return 1
        }
        if isDir.boolValue, FileKind.of(url) == .folder {
            let names = (try? FileManager.default.contentsOfDirectory(atPath: path))?.filter { !$0.hasPrefix(".") } ?? []
            print("files=\(names.count)")
        }
        switch FileKind.of(url) {
        case .image:
            if let size = ImageEngine.orientedPixelSize(url) { print("size=\(Int(size.width))x\(Int(size.height))") }
            if let source = CGImageSourceCreateWithURL(url as CFURL, nil),
               let props = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any] {
                print("gps=\(props[kCGImagePropertyGPSDictionary] != nil ? "yes" : "no")")
                if let title = (props[kCGImagePropertyTIFFDictionary] as? [CFString: Any])?[kCGImagePropertyTIFFImageDescription] as? String {
                    print("title=\(title)")
                }
            }
        case .pdf:
            if let doc = try? PDFEngine.open(url) {
                print("pages=\(doc.pageCount)")
                if let title = try? PDFEngine.info(url)["title"] { print("title=\(title)") }
                if let first = doc.page(at: 0) { print("rotation=\(first.rotation)") }
            }
        case .video, .audio:
            let facts: (Double, CGSize?, [String: String]) = (try? wait {
                (await MediaEngine.duration(url), await MediaEngine.displaySize(url), await MediaEngine.readTags(url))
            }) ?? (0, nil, [:])
            print(String(format: "duration=%.2f", facts.0))
            if let size = facts.1 { print("size=\(Int(size.width))x\(Int(size.height))") }
            if let title = facts.2["title"] { print("title=\(title)") }
        default:
            break
        }
        print("bytes=\(FileNames.size(of: url))")
        return 0
    }

    static func editorSnapshot(_ args: [String]) -> Int32 {
        guard args.count >= 3 else { return usage() }
        let kind = args[0]
        let file = URL(fileURLWithPath: args[1])
        let dark = args.count > 3 && args[3] == "dark"
        do {
            let built: (AnyView, CGSize) = try wait { @MainActor () async throws -> (AnyView, CGSize) in
                switch kind {
                case "crop":
                    let source = try await CanvasSource.load(file)
                    return (AnyView(CropEditorView(source: source, count: 1, finish: { _ in })),
                            CanvasEditors.windowSize(for: source, extra: 58))
                case "redact":
                    let source = try await CanvasSource.load(file)
                    return (AnyView(RedactEditorView(source: source, count: 2, finish: { _ in })),
                            CanvasEditors.windowSize(for: source, extra: 104))
                case "pages":
                    let model = PDFOrganizerModel(documents: [try PDFEngine.open(file)], names: [file.lastPathComponent])
                    if let first = model.items.first { model.click(first) }
                    return (AnyView(PDFOrganizerView(model: model, finish: { _ in })), CGSize(width: 880, height: 640))
                case "meta":
                    let snapshot = try await MetadataKit.read(file)
                    return (AnyView(MetadataEditorView(snapshot: snapshot, finish: { _ in })), CGSize(width: 540, height: 560))
                case "trim":
                    let duration = await MediaEngine.duration(file)
                    let model = TrimModel(duration: duration, playable: false)
                    model.start = duration * 0.2
                    model.end = duration * 0.7
                    return (AnyView(TrimEditorView(url: file, model: model, finish: { _ in })), CGSize(width: 640, height: 250))
                case "onboarding":
                    return (AnyView(OnboardingView()), CGSize(width: 871, height: 664))
                case "integrations":
                    return (AnyView(IntegrationSettings()), CGSize(width: 600, height: 760))
                default:
                    return (AnyView(UpdateView(close: {})), CGSize(width: 500, height: 200))
                }
            }
            return snapshotWindow(built.0, size: built.1, dark: dark, to: args[2])
        } catch {
            print("ERR \(error.localizedDescription)")
            return 1
        }
    }

    static func snapshotWindow(_ view: AnyView, size: CGSize, dark: Bool, to path: String) -> Int32 {
        let window = NSWindow(contentRect: CGRect(origin: .zero, size: size), styleMask: [.titled, .closable],
                              backing: .buffered, defer: false)
        window.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
        let host = NSHostingView(rootView: view.environmentObject(Preferences.shared).environmentObject(Permissions.shared))
        host.frame = CGRect(origin: .zero, size: size)
        window.contentView = host
        pump(1.2)
        host.layoutSubtreeIfNeeded()
        guard let rep = host.bitmapImageRepForCachingDisplay(in: host.bounds) else { return 1 }
        host.cacheDisplay(in: host.bounds, to: rep)
        guard let data = rep.representation(using: .png, properties: [:]), (try? data.write(to: URL(fileURLWithPath: path))) != nil else {
            print("write failed")
            return 1
        }
        print(path)
        return 0
    }

    // MARK: Updates

    static func options(_ args: [String]) -> [String: String] {
        var result: [String: String] = [:]
        var index = 0
        while index < args.count {
            let arg = args[index]
            if arg.hasPrefix("--"), index + 1 < args.count {
                result[String(arg.dropFirst(2))] = args[index + 1]
                index += 2
            } else {
                result["_\(result.count)"] = arg
                index += 1
            }
        }
        return result
    }

    static var defaultKeyPath: String { NSHomeDirectory() + "/.config/pickverter/update_ed25519.key" }

    static func signingKey(_ path: String?) throws -> String {
        if let path { return try String(contentsOfFile: path, encoding: .utf8) }
        if let key = ProcessInfo.processInfo.environment["PICKVERTER_UPDATE_KEY"], !key.isEmpty { return key }
        if let key = try? String(contentsOfFile: defaultKeyPath, encoding: .utf8) { return key }
        throw PickError("no signing key: set PICKVERTER_UPDATE_KEY or create \(defaultKeyPath) with update-keygen")
    }

    static func updateKeygen(_ path: String) -> Int32 {
        let url = URL(fileURLWithPath: (path as NSString).expandingTildeInPath)
        guard !FileManager.default.fileExists(atPath: url.path) else {
            print("refusing to overwrite \(url.path)")
            return 1
        }
        let pair = UpdateSigning.generateKey()
        do {
            try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            try (pair.privateKey + "\n").write(to: url, atomically: true, encoding: .utf8)
            try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
        } catch {
            print("ERR \(error.localizedDescription)")
            return 1
        }
        print(pair.publicKey)
        return 0
    }

    /// Prints the public half of the signing key (PVUpdatePublicKey in Info.plist).
    static func updatePublicKey(_ path: String?) -> Int32 {
        do {
            print(try UpdateSigning.publicKey(forPrivate: signingKey(path)))
            return 0
        } catch {
            print("ERR \(error.localizedDescription)")
            return 1
        }
    }

    /// update-manifest --zip Z --version V --url U [--build B] [--notes F] [--notes-url U] [--min 14.0] [--key F]
    static func updateManifest(_ args: [String]) -> Int32 {
        let opts = options(args)
        guard let zip = opts["zip"], let version = opts["version"], let url = opts["url"] else { return usage() }
        do {
            let file = URL(fileURLWithPath: zip)
            let size = FileNames.size(of: file)
            let digest = try UpdateSigning.sha256(of: file)
            let key = try signingKey(opts["key"])
            let signature = try UpdateSigning.sign(UpdateManifest.signedMessage(version: version, sha256: digest, size: size), privateKey: key)
            let notes = opts["notes"].flatMap { try? String(contentsOfFile: $0, encoding: .utf8) }
            let minimum = opts["min"] ?? (Bundle.main.object(forInfoDictionaryKey: "LSMinimumSystemVersion") as? String)
            let manifest = UpdateManifest(version: version, build: opts["build"], published: ISO8601DateFormatter().string(from: Date()),
                                          minimumSystemVersion: minimum, url: url, size: size, sha256: digest, signature: signature,
                                          notes: notes?.trimmingCharacters(in: .whitespacesAndNewlines), notesURL: opts["notes-url"])
            if let bundled = Updater.shared.publicKey, !UpdateSigning.verify(manifest, publicKey: bundled) {
                FileHandle.standardError.write(Data("warning: this key does not match PVUpdatePublicKey in the app\n".utf8))
            }
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
            print(String(decoding: try encoder.encode(manifest), as: UTF8.self))
            return 0
        } catch {
            print("ERR \(error.localizedDescription)")
            return 1
        }
    }

    /// update-verify <update.json> [zip] [--public KEY]
    static func updateVerify(_ args: [String]) -> Int32 {
        let opts = options(args)
        guard let path = opts["_0"] else { return usage() }
        do {
            let manifest = try JSONDecoder().decode(UpdateManifest.self, from: Data(contentsOf: URL(fileURLWithPath: path)))
            guard let key = opts["public"].flatMap({ Data(base64Encoded: $0) }) ?? Updater.shared.publicKey else {
                print("no public key")
                return 1
            }
            let signed = UpdateSigning.verify(manifest, publicKey: key)
            print("signature: \(signed ? "valid" : "INVALID")")
            var ok = signed
            if let zip = opts["_1"] {
                let file = URL(fileURLWithPath: zip)
                let sizeOK = FileNames.size(of: file) == manifest.size
                let digestOK = (try? UpdateSigning.sha256(of: file)) == manifest.sha256
                print("size: \(sizeOK ? "ok" : "MISMATCH")  sha256: \(digestOK ? "ok" : "MISMATCH")")
                ok = ok && sizeOK && digestOK
            }
            return ok ? 0 : 1
        } catch {
            print("ERR \(error.localizedDescription)")
            return 1
        }
    }

    static func updateCheck() -> Int32 {
        let updater = Updater.shared
        print("enabled=\(updater.isEnabled) repo=\(updater.repository) current=\(updater.currentVersion) url=\(updater.manifestURL?.absoluteString ?? "-")")
        _ = try? wait { await updater.check(userInitiated: true) }
        print("phase=\(updater.phase) latest=\(updater.latest?.version ?? "-")")
        if case .failed = updater.phase { return 1 }
        return 0
    }

    /// Downloads and verifies the newest release, then swaps it into <target-app> (a scratch copy) without relaunching.
    static func updateSelfTest(_ target: String) -> Int32 {
        let updater = Updater.shared
        _ = try? wait { await updater.check(userInitiated: true) }
        guard let manifest = updater.latest else {
            print("no release newer than \(updater.currentVersion) (phase \(updater.phase))")
            return 2
        }
        do {
            let staged = try wait { try await updater.stage(manifest) }
            print("staged \(staged.path) adhoc=\(Updater.isAdHoc(staged))")
            let gone = Process()
            gone.executableURL = URL(fileURLWithPath: "/usr/bin/true")
            try gone.run()
            gone.waitUntilExit()
            let installer = try updater.launchInstaller(staged: staged, target: URL(fileURLWithPath: target),
                                                        pid: gone.processIdentifier, relaunch: false, resetPermissions: false)
            installer.waitUntilExit()
            let info = NSDictionary(contentsOf: URL(fileURLWithPath: target).appendingPathComponent("Contents/Info.plist"))
            let installed = info?["CFBundleShortVersionString"] as? String ?? "?"
            print("installer exit=\(installer.terminationStatus) installed=\(installed)")
            return installed == manifest.version ? 0 : 1
        } catch {
            print("ERR \(error.localizedDescription)")
            return 1
        }
    }
}
