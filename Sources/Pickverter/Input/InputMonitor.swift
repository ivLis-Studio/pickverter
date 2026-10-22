import AppKit
import CoreGraphics

/// One session-wide event tap drives every gesture:
///  • tap a modifier while dragging files (⇧ convert, ⌘ tools, ⌃ actions)
///  • tap the trigger key with files selected in Finder
///  • hold the trigger key and click a file in Finder
/// While the wheel is open it also routes keys, clicks and scrolls to it.
@MainActor
final class InputMonitor {
    private var tap: CFMachPort?
    private var source: CFRunLoopSource?

    private var pendingKey: Int64?
    private var pendingDown: TimeInterval = 0
    private var pendingInterrupted = false
    private var lastTap: (key: Int64, time: TimeInterval)?

    private var mouseIsDown = false
    private var mouseDownPoint = CGPoint.zero
    private var clickCandidate = false
    private var dragChangeCount = 0
    private var dragChecked = false
    private var dragActive = false
    private var dragFiles: [URL] = []
    private var lastDragCheck: TimeInterval = 0
    private var swallowMouseUp = false
    private var swallowRightUp = false
    private var swallowedKeys = Set<Int64>()

    var isRunning: Bool { tap != nil }

    func start() {
        guard tap == nil else { return }
        let types: [CGEventType] = [.flagsChanged, .keyDown, .keyUp, .leftMouseDown, .leftMouseUp, .leftMouseDragged,
                                    .rightMouseDown, .rightMouseUp, .otherMouseDown, .mouseMoved, .scrollWheel]
        let mask = types.reduce(CGEventMask(0)) { $0 | (CGEventMask(1) << CGEventMask($1.rawValue)) }
        let refcon = Unmanaged.passUnretained(self).toOpaque()
        guard let tap = CGEvent.tapCreate(tap: .cgSessionEventTap, place: .headInsertEventTap, options: .defaultTap,
                                          eventsOfInterest: mask, callback: inputTapCallback, userInfo: refcon) else {
            NSLog("Pickverter: event tap unavailable (Accessibility permission missing?)")
            return
        }
        self.tap = tap
        let source = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, tap, 0)
        self.source = source
        CFRunLoopAddSource(CFRunLoopGetMain(), source, .commonModes)
        CGEvent.tapEnable(tap: tap, enable: true)
        NSLog("Pickverter: input monitor running")
    }

    func stop() {
        if let tap { CGEvent.tapEnable(tap: tap, enable: false) }
        if let source { CFRunLoopRemoveSource(CFRunLoopGetMain(), source, .commonModes) }
        tap = nil
        source = nil
    }

    private static let allModifiers: CGEventFlags = [.maskShift, .maskControl, .maskAlternate, .maskCommand, .maskSecondaryFn]

    private static func modifierMask(for code: Int64) -> CGEventFlags? {
        switch code {
        case 56, 60: return .maskShift
        case 59, 62: return .maskControl
        case 58, 61: return .maskAlternate
        case 54, 55: return .maskCommand
        case 63: return .maskSecondaryFn
        default: return nil
        }
    }

    private static func wheelMode(for code: Int64) -> WheelMode? {
        switch code {
        case 56, 60: return .convert
        case 54, 55: return .tools
        case 59, 62: return .actions
        default: return nil
        }
    }

    private var now: TimeInterval { ProcessInfo.processInfo.systemUptime }

    fileprivate func handle(_ type: CGEventType, _ event: CGEvent) -> Unmanaged<CGEvent>? {
        let pass = Unmanaged.passUnretained(event)
        let wheel = WheelController.shared
        // The right-click menu runs its own tracking loop; leave every event to it.
        if wheel.menuOpen, type != .tapDisabledByTimeout, type != .tapDisabledByUserInput { return pass }
        switch type {
        case .tapDisabledByTimeout, .tapDisabledByUserInput:
            if let tap { CGEvent.tapEnable(tap: tap, enable: true) }
            return pass

        case .flagsChanged:
            handleFlags(event)
            return pass

        case .keyDown:
            pendingInterrupted = true
            let code = event.getIntegerValueField(.keyboardEventKeycode)
            if wheel.isVisible, wheel.handleKey(code: code, event: event) {
                swallowedKeys.insert(code)
                return nil
            }
            return pass

        case .keyUp:
            let code = event.getIntegerValueField(.keyboardEventKeycode)
            if swallowedKeys.remove(code) != nil { return nil }
            return pass

        case .leftMouseDown:
            pendingInterrupted = true
            if wheel.isVisible, wheel.origin == .selection {
                if wheel.handleClick(quartz: event.location, flags: event.flags) {
                    swallowMouseUp = true
                    return nil
                }
            }
            mouseIsDown = true
            mouseDownPoint = event.location
            dragActive = false
            dragChecked = false
            dragFiles = []
            dragChangeCount = NSPasteboard(name: .drag).changeCount
            let prefs = Preferences.shared
            let triggerMask = Self.modifierMask(for: prefs.triggerKey.keyCodes.first ?? 58) ?? .maskAlternate
            let held = event.flags.intersection(Self.allModifiers)
            clickCandidate = prefs.enabled && prefs.clickTrigger && held == triggerMask
            return pass

        case .leftMouseDragged:
            let location = event.location
            if clickCandidate, hypot(location.x - mouseDownPoint.x, location.y - mouseDownPoint.y) > 4 {
                clickCandidate = false
            }
            if !dragActive { detectFileDrag() }
            if wheel.isVisible, wheel.origin == .drag { wheel.pointerMoved(quartz: location) }
            return pass

        case .leftMouseUp:
            if swallowMouseUp {
                swallowMouseUp = false
                return nil
            }
            mouseIsDown = false
            if dragActive {
                dragActive = false
                wheel.dragSessionEnded()
            }
            if clickCandidate {
                clickCandidate = false
                let point = event.location
                Task { @MainActor [weak self] in
                    try? await Task.sleep(nanoseconds: 140_000_000)
                    self?.openFromClick(at: point)
                }
            }
            return pass

        case .mouseMoved:
            if wheel.isVisible, wheel.origin == .selection { wheel.pointerMoved(quartz: event.location) }
            return pass

        case .rightMouseDown:
            guard wheel.isVisible else { return pass }
            if wheel.origin == .selection, wheel.contains(quartz: event.location) {
                // Never run a menu inside the tap callback: input would stall until it closes.
                let location = event.location
                swallowRightUp = true
                DispatchQueue.main.async {
                    if !WheelController.shared.showContextMenu(quartz: location) { WheelController.shared.dismiss() }
                }
                return nil
            }
            wheel.dismiss()
            return pass

        case .rightMouseUp:
            if swallowRightUp {
                swallowRightUp = false
                return nil
            }
            return pass

        case .otherMouseDown:
            if wheel.isVisible { wheel.dismiss() }
            return pass

        case .scrollWheel:
            if wheel.isVisible, wheel.contains(quartz: event.location) {
                let delta = event.getDoubleValueField(.scrollWheelEventPointDeltaAxis1)
                wheel.scroll(delta)
                return nil
            }
            return pass

        default:
            return pass
        }
    }

    private func detectFileDrag() {
        guard mouseIsDown, !dragChecked, now - lastDragCheck > 0.04 else { return }
        lastDragCheck = now
        let board = NSPasteboard(name: .drag)
        guard board.changeCount != dragChangeCount else { return }
        dragChecked = true
        let urls = (board.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL]) ?? []
        Debug.log("drag pasteboard changed · \(urls.count) file(s)")
        if !urls.isEmpty {
            dragActive = true
            dragFiles = urls
            clickCandidate = false
        }
    }

    private func handleFlags(_ event: CGEvent) {
        let code = event.getIntegerValueField(.keyboardEventKeycode)
        guard let mask = Self.modifierMask(for: code) else { return }
        let flags = event.flags
        Debug.log("flags code=\(code) flags=\(flags.rawValue) pending=\(String(describing: pendingKey)) interrupted=\(pendingInterrupted)")
        if flags.contains(mask) {
            let others = flags.intersection(Self.allModifiers).subtracting(mask)
            if others.isEmpty {
                pendingKey = code
                pendingDown = now
                pendingInterrupted = false
            } else {
                pendingKey = nil
            }
        } else {
            defer { pendingKey = nil }
            guard pendingKey == code, !pendingInterrupted, now - pendingDown < 0.4 else { return }
            modifierTapped(code)
        }
    }

    private func modifierTapped(_ code: Int64) {
        let prefs = Preferences.shared
        Debug.log("tap code=\(code) drag=\(dragActive) mouseDown=\(mouseIsDown) finderFront=\(FinderBridge.isFinderFrontmost)")
        guard prefs.enabled else { return }
        let wheel = WheelController.shared
        let isTrigger = prefs.triggerKey.keyCodes.contains(code)
        let mapped = Self.wheelMode(for: code)

        if dragActive, mouseIsDown {
            guard prefs.dragTrigger else { return }
            if wheel.isVisible {
                if let mapped { wheel.setMode(mapped) } else if isTrigger { wheel.cycleMode() }
            } else if mapped != nil || isTrigger {
                wheel.present(files: dragFiles, origin: .drag, mode: mapped, at: NSEvent.mouseLocation)
            }
            return
        }

        if wheel.isVisible {
            if isTrigger { wheel.dismiss() } else if let mapped { wheel.setMode(mapped) }
            return
        }

        guard isTrigger, prefs.tapTrigger else { return }
        if prefs.doubleTap {
            if let last = lastTap, last.key == code, now - last.time < 0.45 {
                lastTap = nil
            } else {
                lastTap = (code, now)
                return
            }
        }
        guard FinderBridge.isFinderFrontmost else { return }
        // Renaming a file, typing in the search field or path bar: ⌥ belongs to the text.
        guard !FinderBridge.isEditingText else { return }
        let point = NSEvent.mouseLocation
        Task { @MainActor in
            let files = FinderBridge.selection()
            guard !files.isEmpty else { return }
            WheelController.shared.present(files: files, origin: .selection, mode: nil, at: point)
        }
    }

    private func openFromClick(at point: CGPoint) {
        guard !WheelController.shared.isVisible, FinderBridge.isFinderContent(at: point), !FinderBridge.isEditingText else { return }
        let files = FinderBridge.selection()
        guard !files.isEmpty else { return }
        WheelController.shared.present(files: files, origin: .selection, mode: nil,
                                       at: ScreenGeometry.appKit(fromQuartz: point))
    }
}

private func inputTapCallback(proxy: CGEventTapProxy, type: CGEventType, event: CGEvent,
                              refcon: UnsafeMutableRawPointer?) -> Unmanaged<CGEvent>? {
    guard let refcon else { return Unmanaged.passUnretained(event) }
    let monitor = Unmanaged<InputMonitor>.fromOpaque(refcon).takeUnretainedValue()
    return MainActor.assumeIsolated { monitor.handle(type, event) }
}
