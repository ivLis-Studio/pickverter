import AppKit
import SwiftUI

/// Result cards float at status-bar level, above every app. A Pickverter dialog the user is answering (an editor, the
/// password or page-range prompt, the folder picker) goes one level higher, so a card never covers its buttons.
@MainActor
enum DialogLevel {
    static let above = NSWindow.Level(rawValue: NSWindow.Level.statusBar.rawValue + 1)

    /// Runs a modal alert or panel above the cards. AppKit puts the window back at modal-panel level when the session
    /// starts and again when the app activates, so the level is held for as long as the session runs.
    static func runModal(_ window: NSWindow, _ run: () -> NSApplication.ModalResponse) -> NSApplication.ModalResponse {
        window.level = above
        let holder = Timer(timeInterval: 0.05, repeats: true) { _ in
            MainActor.assumeIsolated {
                if window.level != above { window.level = above }
            }
        }
        RunLoop.main.add(holder, forMode: .modalPanel)
        defer { holder.invalidate() }
        return run()
    }
}

/// One regular window per editor. Finder gets focus back when the editor closes.
@MainActor
final class EditorWindowController: NSObject, NSWindowDelegate {
    private(set) var window: NSWindow?
    var onClose: (() -> Void)?
    private var previousApp: NSRunningApplication?
    private var dismissing = false

    func show(title: String, size: CGSize, minSize: CGSize, root: AnyView) {
        previousApp = NSWorkspace.shared.frontmostApplication
        let hosting = NSHostingController(rootView: root)
        hosting.sizingOptions = []
        let window = NSWindow(contentRect: CGRect(origin: .zero, size: size),
                              styleMask: [.titled, .closable, .resizable, .miniaturizable],
                              backing: .buffered, defer: false)
        window.contentViewController = hosting
        window.title = title
        window.isReleasedWhenClosed = false
        window.delegate = self
        window.tabbingMode = .disallowed
        let mouse = NSEvent.mouseLocation
        var fitted = size
        let visible = ScreenGeometry.screen(containing: mouse)?.visibleFrame
        if let visible {
            // Never bigger than the screen: the buttons along the bottom have to stay reachable.
            let chrome = window.frameRect(forContentRect: CGRect(origin: .zero, size: size)).size
            fitted.width = min(size.width, visible.width - 32 - (chrome.width - size.width))
            fitted.height = min(size.height, visible.height - 32 - (chrome.height - size.height))
        }
        window.setContentSize(fitted)
        window.contentMinSize = CGSize(width: min(minSize.width, fitted.width), height: min(minSize.height, fitted.height))
        if let visible {
            let frame = window.frame
            let y = min(visible.midY - frame.height / 2 + 20, visible.maxY - frame.height)
            window.setFrameOrigin(CGPoint(x: visible.midX - frame.width / 2, y: max(visible.minY, y)))
        }
        self.window = window
        NSApp.activate(ignoringOtherApps: true)
        // In front of the cards from the start, even when macOS keeps another app active for a moment.
        window.level = DialogLevel.above
        window.makeKeyAndOrderFront(nil)
    }

    /// Closes without reporting a cancel (the editor already produced its answer).
    func dismiss() {
        dismissing = true
        window?.close()
        window = nil
        if let previousApp, previousApp.bundleIdentifier != Bundle.main.bundleIdentifier, !previousApp.isTerminated {
            previousApp.activate()
        }
    }

    func windowWillClose(_ notification: Notification) {
        if !dismissing { onClose?() }
    }

    // Above the cards while in use; once the user switches to another app it steps back like any window.
    func windowDidBecomeKey(_ notification: Notification) {
        window?.level = DialogLevel.above
    }

    func windowDidResignKey(_ notification: Notification) {
        window?.level = .normal
    }
}

@MainActor
enum EditorHost {
    private static var live: [ObjectIdentifier: EditorWindowController] = [:]

    /// Tests skip the window: PICKVERTER_EDITOR_<NAME> holds the JSON answer (or "cancel").
    static func injected<Result: Decodable>(_ name: String) throws -> Result? {
        let key = "PICKVERTER_EDITOR_" + name.uppercased()
        guard let raw = ProcessInfo.processInfo.environment[key] else { return nil }
        if raw == "cancel" { throw CancellationError() }
        do {
            return try JSONDecoder().decode(Result.self, from: Data(raw.utf8))
        } catch {
            throw PickError("\(key): \(error)")
        }
    }

    /// Shows the editor and suspends until it answers. Closing the window counts as cancel.
    static func run<Result: Codable, Content: View>(_ name: String, title: String, size: CGSize, minSize: CGSize? = nil,
                                                   content: (_ finish: @escaping (Result?) -> Void) -> Content) async throws -> Result {
        if let value: Result = try injected(name) { return value }
        let controller = EditorWindowController()
        let key = ObjectIdentifier(controller)
        live[key] = controller
        defer { live[key] = nil }
        return try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Result, Error>) in
            let gate = Gate()
            let finish: (Result?) -> Void = { result in
                guard gate.open() else { return }
                controller.dismiss()
                if let result {
                    continuation.resume(returning: result)
                } else {
                    continuation.resume(throwing: CancellationError())
                }
            }
            controller.onClose = { finish(nil) }
            controller.show(title: title, size: size, minSize: minSize ?? CGSize(width: size.width * 0.7, height: size.height * 0.7),
                            root: AnyView(content(finish)))
        }
    }

    private final class Gate {
        private var used = false
        func open() -> Bool {
            if used { return false }
            used = true
            return true
        }
    }
}

/// Cancel / primary buttons with the standard esc and ↩︎ shortcuts, plus room for controls on the left.
struct EditorFooter<Leading: View>: View {
    let primary: String
    var primaryEnabled = true
    let onCancel: () -> Void
    let onPrimary: () -> Void
    @ViewBuilder var leading: () -> Leading

    var body: some View {
        VStack(spacing: 0) {
            EditorDivider()
            HStack(spacing: 10) {
                leading()
                Spacer(minLength: 12)
                Button(L("common.cancel"), action: onCancel)
                    .keyboardShortcut(.cancelAction)
                Button(primary, action: onPrimary)
                    .keyboardShortcut(.defaultAction)
                    .disabled(!primaryEnabled)
            }
            .controlSize(.regular)
            .padding(.horizontal, 16)
            .padding(.vertical, 12)
        }
        .background(Color(nsColor: .windowBackgroundColor))
    }
}

extension EditorFooter where Leading == EmptyView {
    init(primary: String, primaryEnabled: Bool = true, onCancel: @escaping () -> Void, onPrimary: @escaping () -> Void) {
        self.init(primary: primary, primaryEnabled: primaryEnabled, onCancel: onCancel, onPrimary: onPrimary) { EmptyView() }
    }
}

/// Hairline that follows the window's appearance (light and dark).
struct EditorDivider: View {
    var body: some View {
        Rectangle().fill(Color(nsColor: .separatorColor)).frame(height: 1)
    }
}

enum TimeText {
    /// 75.4 -> "1:15.4", 3725 -> "1:02:05.0"
    static func format(_ seconds: Double, precise: Bool = true) -> String {
        let value = max(0, seconds.isFinite ? seconds : 0)
        let tenths = Int((value * 10).rounded())
        let h = tenths / 36_000, m = (tenths / 600) % 60, s = (tenths / 10) % 60, t = tenths % 10
        let body = h > 0 ? String(format: "%d:%02d:%02d", h, m, s) : String(format: "%d:%02d", m, s)
        return precise ? body + ".\(t)" : body
    }
}
