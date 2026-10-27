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

    /// Holding ⇧ ⌘ ⌃ while dragging files: the ring opens once the pointer rests (the same timelines as DragHoldTests.cs).
    static func dragHoldTests() -> Int {
        var failures = 0
        let command: Int64 = 55
        func due(_ hold: inout DragHold, _ now: TimeInterval) -> String { hold.due(now: now).map(String.init) ?? "-" }

        var during = DragHold(slop: 6)
        during.dragStarted(now: 0, x: 100, y: 100)
        during.moved(now: 0.2, x: 160, y: 120)
        during.press(command, now: 0.3, x: 160, y: 120)
        let early = [due(&during, 0.5), due(&during, 0.79)].joined(separator: " ")
        check("hold during a drag waits for the rest", early, "- -", &failures)
        check("hold during a drag opens after 0.5 s", due(&during, 0.81), "55", &failures)
        check("… only once", due(&during, 1.5), "-", &failures)

        var before = DragHold(slop: 6)
        before.press(command, now: 0, x: 100, y: 100)
        before.dragStarted(now: 1, x: 104, y: 100)
        check("key held before the drag counts from the drag", "\(due(&before, 1.4)) \(due(&before, 1.5))", "- 55", &failures)

        var moving = DragHold(slop: 6)
        moving.press(command, now: 0, x: 100, y: 100)
        moving.dragStarted(now: 0.1, x: 104, y: 100)
        var opened = false
        for step in 1...40 {
            let now = 0.1 + Double(step) * 0.05
            moving.moved(now: now, x: 104 + Double(step) * 8, y: 100)
            if moving.due(now: now) != nil { opened = true }
        }
        check("a moving pointer leaves Finder's drag alone", "\(opened)", "false", &failures)
        check("… until it rests", "\(due(&moving, 2.5)) \(due(&moving, 2.61))", "- 55", &failures)

        var jitter = DragHold(slop: 6)
        jitter.dragStarted(now: 0, x: 100, y: 100)
        jitter.press(command, now: 0, x: 100, y: 100)
        jitter.moved(now: 0.2, x: 105, y: 97)
        jitter.moved(now: 0.4, x: 96, y: 104)
        check("jitter still counts as resting", due(&jitter, 0.5), "55", &failures)

        var cancelled = DragHold(slop: 6)
        cancelled.dragStarted(now: 0, x: 100, y: 100)
        cancelled.press(command, now: 0, x: 100, y: 100)
        cancelled.cancel()
        check("releasing the key cancels", due(&cancelled, 2), "-", &failures)

        var again = DragHold(slop: 6)
        again.press(command, now: 0, x: 100, y: 100)
        again.dragStarted(now: 0.1, x: 104, y: 100)
        _ = again.due(now: 0.61)
        again.dragStarted(now: 2, x: 300, y: 300)
        check("every new drag can open again", "\(due(&again, 2.2)) \(due(&again, 2.5))", "- 55", &failures)
        return failures
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

        // Reach: whatever is highlighted runs on click; past the reach nothing is highlighted and a click closes.
        func click(_ appKit: CGPoint) -> Bool {
            wheel.handleClick(quartz: CGPoint(x: appKit.x, y: ScreenGeometry.primaryHeight - appKit.y), flags: [])
        }
        launched = nil
        open(.convert)
        let far = point(.pi / 2, (g.discRadius + 26 + g.reach) / 2)
        wheel.pointerMoved(appKit: far)
        let target = wheel.activeOption?.option.id
        check("past the disc a slot is still targeted", target == nil ? "none" : "slot", "slot", &failures)
        _ = click(far)
        check("…and the click runs it", launched?.id, target ?? "?", &failures)
        open(.convert)
        let outside = point(.pi / 2, g.reach + 6)
        wheel.pointerMoved(appKit: outside)
        check("past the reach nothing is highlighted", wheel.highlighted == nil ? "none" : "slot", "none", &failures)
        check("…and a click closes the wheel", "\(click(outside)) \(wheel.isVisible)", "false false", &failures)

        // Edges: the wheel moves inward until every slot, the outer ring and the tabs fit (1440×900, menu bar, Dock).
        let area = CGRect(x: 0, y: 70, width: 1440, height: 805)
        let corner = g.center(for: CGPoint(x: 2, y: 899), in: area)
        check("corner: centre moves inward", "\(Int(corner.x)) \(Int(corner.y))",
              "\(Int(area.minX + g.envelope)) \(Int(area.maxY - g.envelope))", &failures)
        let probes = [CGPoint(x: 0, y: 0), CGPoint(x: 1440, y: 900), CGPoint(x: 720, y: 72), CGPoint(x: 1439, y: 300)]
        let drawn = g.subOuter + 4
        let fits = probes.allSatisfy { probe in
            let c = g.center(for: probe, in: area)
            return c.x - drawn >= area.minX && c.x + drawn <= area.maxX && c.y + drawn <= area.maxY
                && c.y - max(drawn, g.tabBarOffset + g.tabBarHeight / 2) >= area.minY
        }
        check("edges: nothing hangs off the screen", fits ? "inside" : "clipped", "inside", &failures)
        let free = g.center(for: CGPoint(x: 700, y: 480), in: area)
        check("away from edges it opens under the pointer", "\(Int(free.x)) \(Int(free.y))", "700 480", &failures)
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
        // One fixed scratch domain: cfprefsd keeps an empty file per domain even after it is removed.
        let suite = "com.pickverter.selftest"
        guard let store = UserDefaults(suiteName: suite) else { return 1 }
        store.removePersistentDomain(forName: suite)
        defer { store.removePersistentDomain(forName: suite) }
        let layout = WheelLayout(store: store)
        let key = WheelLayout.key(mode: .tools, files: files)
        let options = Catalog.options(for: files, mode: .tools)
        let natural = options.map(\.id)
        layout.hide("tool.ocr", title: "OCR")
        var ids = layout.arrange(options, key: key).map(\.id)
        check("hide a slot", ids.contains("tool.ocr") ? "shown" : "hidden", "hidden", &failures)
        layout.unhide("tool.ocr")
        ids = layout.arrange(options, key: key).map(\.id)
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
        failures += ringLayoutTests(layout, files: files)
        layout.resetAll()
        check("reset restores the stock wheel", layout.arrange(options, key: key).map(\.id) == natural ? "stock" : "custom", "stock", &failures)
        return failures
    }

    /// Removing, restoring and borrowing slots per ring and file type (Settings › Wheel).
    static func ringLayoutTests(_ layout: WheelLayout, files: [URL]) -> Int {
        var failures = 0
        let key = WheelLayout.key(mode: .tools, files: files)
        let tools = Catalog.options(for: files, mode: .tools)
        let pdf = [URL(fileURLWithPath: "/tmp/pickverter-layout/a.pdf")]
        let pdfKey = WheelLayout.key(mode: .tools, kind: .pdf)
        let pdfTools = Catalog.options(for: pdf, mode: .tools)
        func has(_ id: String, _ options: [PickOption], _ key: String, pool: [PickOption] = []) -> String {
            layout.arrange(options, key: key, pool: pool).contains { $0.id == id } ? "in" : "out"
        }
        layout.remove("tool.zip", key: key)
        check("remove a slot from one ring", has("tool.zip", tools, key), "out", &failures)
        check("other file types keep it", has("tool.zip", pdfTools, pdfKey), "in", &failures)
        layout.restore("tool.zip", key: key)
        check("put a removed slot back", has("tool.zip", tools, key), "in", &failures)

        // Slots from the other rings: a whole slot and one option of a slot, in the order they were added.
        let actionsKey = WheelLayout.key(mode: .actions, files: files)
        let actions = Catalog.options(for: files, mode: .actions)
        let pool = tools + Catalog.options(for: files, mode: .convert)
        layout.addFromRing("tool.resize", key: actionsKey)
        layout.addFromRing("tool.rotate.left", key: actionsKey)
        let borrowed = layout.arrange(actions, key: actionsKey, pool: pool).suffix(2).map(\.id)
        check("bring slots in from another ring", borrowed.joined(separator: " "), "tool.resize tool.rotate.left", &failures)
        if let native = actions.first?.id {
            layout.addFromRing(native, key: actionsKey)
            let count = layout.arrange(actions, key: actionsKey, pool: pool).filter { $0.id == native }.count
            check("a slot never shows twice", "\(count)", "1", &failures)
        }
        layout.remove("tool.rotate.left", key: actionsKey)
        check("removing a borrowed slot sends it back", has("tool.rotate.left", actions, actionsKey, pool: pool), "out", &failures)
        check("…without setting it aside", "\(layout.removed[actionsKey]?.contains("tool.rotate.left") == true)", "false", &failures)
        layout.reset(key: actionsKey)
        check("reset one ring", layout.arrange(actions, key: actionsKey, pool: pool).map(\.id) == actions.map(\.id) ? "stock" : "custom",
              "stock", &failures)

        // One ring's layout for every file type.
        layout.remove("tool.zip", key: key)
        layout.applyToAllKinds(from: key, kinds: WheelLayoutEditor.kinds)
        check("use a ring for every file type", has("tool.zip", pdfTools, pdfKey), "out", &failures)
        check("other rings stay", has(actions.first?.id ?? "", actions, actionsKey), "in", &failures)
        layout.reset(key: key)
        layout.reset(key: pdfKey)

        // Settings lists every slot a file type can have once (GIF-only formats join the image ring).
        let image = WheelLayoutEditor.nativeOptions(.image, .convert).map(\.id)
        check("image ring lists every format once", "\(Set(image).count == image.count && image.contains("convert.mp4"))", "true", &failures)
        let office = WheelLayoutEditor.nativeOptions(.office, .convert).map(\.id)
        check("office ring lists every format once", "\(Set(office).count == office.count && office.count > 3)", "true", &failures)
        return failures
    }

    /// Settings › Wheel's editor, on a scratch layout (the user's wheel stays as it is): what the list shows is what
    /// the wheel shows.
    static func editorTests() -> Int {
        var failures = 0
        let suite = "com.pickverter.selftest.editor"
        guard let store = UserDefaults(suiteName: suite) else { return 1 }
        store.removePersistentDomain(forName: suite)
        defer { UserDefaults.standard.removePersistentDomain(forName: suite) }
        WheelLayout.useScratch(store)
        let editor = WheelLayoutEditor()
        editor.kind = .image
        editor.mode = .tools
        let stock = editor.rows.map(\.id)
        check("editor lists the stock ring", "\(stock == WheelLayoutEditor.nativeOptions(.image, .tools).map(\.id))", "true", &failures)
        guard stock.count > 3 else { return failures + 1 }
        editor.move(from: IndexSet(integer: 2), to: 0)
        editor.reload()
        check("drag a slot to the top", editor.rows.first?.id ?? "", stock[2], &failures)
        check("the preview follows", editor.preview.items.first?.id ?? "", stock[2], &failures)
        if let first = editor.rows.first {
            editor.remove(first)
            editor.reload()
            check("remove a slot in Settings", "\(editor.rows.contains { $0.id == first.id })", "false", &failures)
            check("Add Slot offers it back", "\(editor.removedCandidates.contains { $0.id == first.id })", "true", &failures)
            if let back = editor.removedCandidates.first(where: { $0.id == first.id }) { editor.restore(back) }
            editor.reload()
            check("put it back", "\(editor.rows.contains { $0.id == first.id })", "true", &failures)
        }
        if let parent = editor.optionCandidates.first, let option = parent.children.first {
            editor.pin(option)
            editor.reload()
            let origin = editor.rows.first { $0.id == option.id }?.origin
            check("an option as a slot of its own", "\(origin == .pinned(parent: parent.title))", "true", &failures)
        }
        if let ring = editor.ringCandidates.first(where: { $0.mode == .actions }),
           let item = ring.items.first(where: { $0.children.isEmpty }) {
            editor.addFromRing(item)
            editor.reload()
            let origin = editor.rows.first { $0.id == item.id }?.origin
            check("a slot from the Actions ring", "\(origin == .added(.actions))", "true", &failures)
            check("the wheel pages past ten", "\(editor.preview.items.count <= WheelController.maxPerPage)", "true", &failures)
        }
        editor.applyToAllKinds()
        check("one layout for every file type", "\(WheelLayout.shared.isCustomized(WheelLayout.key(mode: .tools, kind: .video)))",
              "true", &failures)
        editor.reset()
        editor.reload()
        check("reset the ring in Settings", "\(editor.rows.map(\.id) == stock)", "true", &failures)
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
                  "arrow.clockwise", "arrow.uturn.backward", "gearshape", "list.bullet", "info.circle", "minus.circle",
                  "minus.circle.fill", "slider.horizontal.3", "plus", "circle.dashed", "pin.fill", "rectangle.split.2x1",
                  "arrow.left.and.right", "text.badge.checkmark", "character.textbox", "sparkles"]
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
        case .hwp:
            // rhwp's own reading: page count and format prove an HWP/HWPX result opens.
            if let rhwp = HWPInstaller.executable,
               let json = try? wait({ try await Shell.run(rhwp, ["info", path, "--json"]) }),
               let info = try? JSONSerialization.jsonObject(with: Data(json.utf8)) as? [String: Any] {
                if let pages = info["pageCount"] as? Int { print("pages=\(pages)") }
                if let format = info["format"] as? String { print("format=\(format)") }
            }
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
                case "compare":
                    // PICKVERTER_COMPARE_AFTER: the result to compare with (the same file otherwise).
                    let after = ProcessInfo.processInfo.environment["PICKVERTER_COMPARE_AFTER"].map { URL(fileURLWithPath: $0) } ?? file
                    guard let model = CompareModel(before: file, after: after) else { throw PickError(L("err.read_image")) }
                    if let split = ProcessInfo.processInfo.environment["PICKVERTER_COMPARE_SPLIT"].flatMap(Double.init) {
                        model.position = CGFloat(split)
                    }
                    return (AnyView(CompareView(model: model, close: {})), CompareWindow.windowSize(for: model.frameAspect))
                case "onboarding":
                    return (AnyView(OnboardingView()), CGSize(width: 871, height: 664))
                case "onboarding-signed":
                    CodeIdentity.previewClearedGrants()
                    return (AnyView(OnboardingView()), CGSize(width: 871, height: 664))
                case "onboarding-progress":
                    FFmpegInstaller.shared.preview(.downloading(0.42))
                    return (AnyView(OnboardingView()), CGSize(width: 871, height: 664))
                case "integrations":
                    return (AnyView(IntegrationSettings()), CGSize(width: 600, height: 760))
                case "integrations-progress":
                    FFmpegInstaller.shared.preview(.downloading(0.42))
                    return (AnyView(IntegrationSettings()), CGSize(width: 600, height: 760))
                case "integrations-failed":
                    FFmpegInstaller.shared.preview(.failed(L("ffmpeg.err.download", "The Internet connection appears to be offline.")))
                    return (AnyView(IntegrationSettings()), CGSize(width: 600, height: 760))
                case "wheel", "wheel-custom":
                    // A scratch layout, so the user's own wheel stays as it is.
                    if let store = UserDefaults(suiteName: snapshotSuite) {
                        store.removePersistentDomain(forName: snapshotSuite)
                        WheelLayout.useScratch(store)
                    }
                    if kind == "wheel-custom" {
                        let key = WheelLayout.key(mode: .tools, kind: .image)
                        let layout = WheelLayout.shared
                        layout.remove("tool.collage", key: key)
                        layout.pin("tool.rotate.left", title: "Rotate · Left", key: key)
                        layout.addFromRing("action.airdrop", key: key)
                        layout.setDefault(slot: "tool.resize", variant: "tool.resize.1920")
                        SettingsRouter.shared.showLayout(mode: .tools, kind: .image)
                    }
                    // PICKVERTER_LAYOUT=0|folder opens another ring and file type.
                    if let request = ProcessInfo.processInfo.environment["PICKVERTER_LAYOUT"]?.split(separator: "|"), request.count == 2,
                       let ring = Int(request[0]).flatMap(WheelMode.init(rawValue:)), let fileKind = FileKind(rawValue: String(request[1])) {
                        SettingsRouter.shared.showLayout(mode: ring, kind: fileKind)
                    }
                    // The whole Settings window, tabs included, as people see it.
                    SettingsRouter.shared.tab = .wheel
                    return (AnyView(SettingsView().background(Color(nsColor: .windowBackgroundColor))), CGSize(width: 640, height: 560))
                default:
                    return (AnyView(UpdateView(close: {})), CGSize(width: 500, height: 200))
                }
            }
            let status = snapshotWindow(built.0, size: built.1, dark: dark, to: args[2], title: kind == "compare" ? L("compare.title") : "")
            if kind.hasPrefix("wheel") { UserDefaults.standard.removePersistentDomain(forName: snapshotSuite) }
            return status
        } catch {
            print("ERR \(error.localizedDescription)")
            return 1
        }
    }

    private static let snapshotSuite = "com.pickverter.snapshot"

    static func snapshotWindow(_ view: AnyView, size: CGSize, dark: Bool, to path: String, title: String = "") -> Int32 {
        let window = NSWindow(contentRect: CGRect(origin: .zero, size: size), styleMask: [.titled, .closable],
                              backing: .buffered, defer: false)
        window.title = title
        window.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
        let host = NSHostingView(rootView: view.environmentObject(Preferences.shared).environmentObject(Permissions.shared))
        host.frame = CGRect(origin: .zero, size: size)
        window.contentView = host
        pump(1.2)
        host.layoutSubtreeIfNeeded()
        // PICKVERTER_SNAPSHOT_CHROME=1: the whole window, title bar and toolbar included.
        let target: NSView = ProcessInfo.processInfo.environment["PICKVERTER_SNAPSHOT_CHROME"] == "1"
            ? (host.superview ?? host) : host
        guard let rep = target.bitmapImageRepForCachingDisplay(in: target.bounds) else { return 1 }
        target.cacheDisplay(in: target.bounds, to: rep)
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
            print("staged \(staged.path) team=\(CodeIdentity.team(of: staged) ?? "ad-hoc")")
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

    // MARK: Self-test: signatures and privacy permissions

    /// Which launches clear grants made to another signature, and the Team ID lookup behind it.
    static func signingTests() -> Int {
        var failures = 0
        let cases: [(current: String?, last: String?, returning: Bool)] = [
            (nil, nil, true),   // an ad-hoc build never clears or records
            ("T", "T", true),   // the same signer as last time
            ("T", nil, true),   // the first Developer ID launch after 0.3.0 and earlier
            ("T", nil, false),  // a first launch only records
            ("T", "U", true),   // another signer
        ]
        let decisions = cases.map { "\(CodeIdentity.change(current: $0.current, last: $0.last, returning: $0.returning))" }
        check("signer changes", decisions.joined(separator: " "), "keep keep clearGrants record clearGrants", &failures)
        check("a returning user", "\(CodeIdentity.isReturning(["onboarded": false])) "
              + "\(CodeIdentity.isReturning(["NSNavPanelExpandedSizeForOpenMode": "{880, 448}"]))", "true false", &failures)
        // The Security framework must read the same Team ID codesign prints ("not set" for ad-hoc builds).
        let app = Bundle.main.bundleURL
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/codesign")
        process.arguments = ["-dv", app.path]
        let pipe = Pipe()
        process.standardError = pipe
        process.standardOutput = FileHandle.nullDevice
        try? process.run()
        let output = String(decoding: pipe.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
        process.waitUntilExit()
        let printed = output.split(separator: "\n").first(where: { $0.hasPrefix("TeamIdentifier=") })?.dropFirst("TeamIdentifier=".count)
        let expected = printed.map(String.init).flatMap { $0 == "not set" ? nil : $0 } ?? "ad-hoc"
        let team = CodeIdentity.team(of: app)
        check("Team ID matches codesign", team ?? "ad-hoc", expected, &failures)
        check("grants carry over only between signed copies", "\(CodeIdentity.keepsPermissions(from: app, to: app))",
              "\(team != nil)", &failures)
        return failures
    }

    // MARK: Self-test: AI results

    /// Remove BG's results say the AI model made them (Store policy 11.16, as on Windows); the report form carries the
    /// version and the tool, nothing about the file.
    static func aiReportTests() -> Int {
        var failures = 0
        let ids: [String?] = ["tool.removebg", "tool.removebg.crop", "tool.removebgx", "tool.crop.square", nil]
        check("AI results", ids.map { "\(AIReport.covers($0))" }.joined(separator: " "), "true true false false false", &failures)
        func fields(_ id: String?) -> [String: String] {
            let items = URLComponents(url: AIReport.url(optionID: id), resolvingAgainstBaseURL: false)?.queryItems ?? []
            return Dictionary(items.map { ($0.name, $0.value ?? "") }, uniquingKeysWith: { first, _ in first })
        }
        let url = AIReport.url(optionID: "tool.removebg.crop")
        check("report form", "\(url.host ?? "")\(url.path)", "github.com/ivLis-Studio/pickverter/issues/new", &failures)
        let link = url.absoluteString
        check("encoded", link, link.allSatisfy { $0.isASCII && $0 != " " } ? link : "ASCII only, no spaces", &failures)
        let form = fields("tool.removebg.crop")
        check("form fields", form.keys.sorted().joined(separator: " ") + " · \(form["template"] ?? "") · \(form["labels"] ?? "")",
              "app labels template tool · ai-result.yml · ai-report", &failures)
        check("which Remove BG", "\(form["tool"] ?? "") | \(fields("tool.removebg")["tool"] ?? "")", "Remove BG (crop) | Remove BG", &failures)
        let app = form["app"] ?? ""
        let shape = #"^Pickverter [0-9][^ ]* · macOS [0-9]+\.[0-9]+\.[0-9]+ · (arm64|x86_64) · GitHub$"#
        check("app line", app, app.range(of: shape, options: .regularExpression) != nil ? app
              : "Pickverter <version> · macOS <x.y.z> · <arm64|x86_64> · GitHub", &failures)
        return failures
    }
}
