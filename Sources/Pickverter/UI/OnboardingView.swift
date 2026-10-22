import AppKit
import Combine
import SwiftUI

@MainActor
final class OnboardingDemo: ObservableObject {
    let wheel = WheelController(demo: true)
    private var timer: Timer?
    private var tick = 0
    /// mode, slot, outer-ring option
    private let script: [(WheelMode, Int?, Int?)] = [
        (.convert, nil, nil), (.convert, 1, nil), (.convert, 3, nil), (.convert, 3, nil),
        (.tools, nil, nil), (.tools, 4, nil), (.tools, 4, 1), (.tools, 4, 2), (.tools, 4, 2),
        (.actions, nil, nil), (.actions, 2, nil), (.actions, 2, 2), (.actions, 2, 2),
    ]

    init() {
        let sample = FileManager.default.temporaryDirectory.appendingPathComponent(L("demo.filename"))
        wheel.present(files: [sample], origin: .selection, mode: .convert, at: .zero)
    }

    func start() {
        timer?.invalidate()
        timer = Timer.scheduledTimer(withTimeInterval: 0.9, repeats: true) { _ in
            Task { @MainActor in self.step() }
        }
    }

    func stop() { timer?.invalidate() }

    private func step() {
        tick = (tick + 1) % script.count
        let (mode, slot, sub) = script[tick]
        if wheel.mode != mode { wheel.demoSetMode(mode) }
        withAnimation(.easeOut(duration: 0.15)) { wheel.demoHighlight(slot, sub: sub) }
    }
}

struct OnboardingView: View {
    @EnvironmentObject var permissions: Permissions
    @EnvironmentObject var prefs: Preferences
    @StateObject private var demo = OnboardingDemo()
    @ObservedObject private var updater = Updater.shared
    @State private var hasFFmpeg = Tools.ffmpeg != nil

    var body: some View {
        HStack(spacing: 0) {
            ZStack {
                Color(nsColor: .underPageBackgroundColor)
                WheelView(model: demo.wheel)
                    .environment(\.glassWithinWindow, true)
                    .scaleEffect(0.78)
                    .allowsHitTesting(false)
            }
            .frame(width: 400)
            .clipped()

            Divider()

            VStack(alignment: .leading, spacing: 18) {
                if updater.updatedFrom != nil {
                    VStack(alignment: .leading, spacing: 6) {
                        Text(L("ob.updated", updater.currentVersion)).font(.system(size: 22, weight: .bold))
                        Text(L("ob.updated.d"))
                            .font(.system(size: 13)).foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                } else {
                    VStack(alignment: .leading, spacing: 6) {
                        Text("Pickverter").font(.system(size: 26, weight: .bold))
                        Text(L("ob.subtitle"))
                            .font(.system(size: 13)).foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }

                VStack(alignment: .leading, spacing: 10) {
                    StepRow(number: 1, title: L("perm.ax"), detail: L("ob.ax.d"), done: permissions.accessibility) {
                        permissions.requestAccessibility()
                    }
                    StepRow(number: 2, title: L("perm.finder"), detail: L("ob.finder.d"), done: permissions.finder == .granted) {
                        permissions.requestFinder()
                    }
                    StepRow(number: 3, title: L("ob.ffmpeg"), detail: L("ob.ffmpeg.d"), done: hasFFmpeg,
                            actionTitle: L("ob.install"), optional: true) {
                        EngineInstaller.installFFmpeg()
                    }
                    if !permissions.accessibility {
                        Text(L("ob.reset_hint"))
                            .font(.system(size: 11)).foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }

                Divider()

                VStack(alignment: .leading, spacing: 10) {
                    SectionLabel(L("ob.how"))
                    GuideRow(keys: [prefs.triggerKey.glyph], title: L("ob.tap.t"), detail: L("ob.tap.d", prefs.triggerKey.glyph))
                    GuideRow(keys: [prefs.triggerKey.glyph, L("key.click")], title: L("ob.click.t"), detail: L("ob.click.d", prefs.triggerKey.glyph))
                    GuideRow(keys: ["⇧", "⌘", "⌃"], title: L("ob.drag.t"), detail: L("ob.drag.d"))
                    GuideRow(keys: ["↑", "↓"], title: L("ob.options.t"), detail: L("ob.options.d"))
                }

                Spacer(minLength: 0)
                HStack {
                    Text(L("ob.local")).font(.system(size: 11)).foregroundStyle(.secondary)
                    Spacer()
                    Button(L("ob.start")) {
                        prefs.onboarded = true
                        updater.acknowledgeUpdate()
                        WindowManager.shared.closeOnboarding()
                    }
                    .buttonStyle(.borderedProminent)
                    .controlSize(.large)
                    .keyboardShortcut(.defaultAction)
                }
            }
            .padding(.horizontal, 28)
            .padding(.top, 36)
            .padding(.bottom, 22)
            .frame(width: 470)
        }
        .frame(height: 580)
        .onAppear { demo.start() }
        .onDisappear { demo.stop() }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            Tools.refresh()
            hasFFmpeg = Tools.ffmpeg != nil
        }
        .id(prefs.language)
    }
}

struct StepRow: View {
    let number: Int
    let title: String
    let detail: String
    let done: Bool
    var actionTitle: String = L("perm.allow")
    var optional = false
    let action: () -> Void

    var body: some View {
        HStack(spacing: 12) {
            ZStack {
                Circle().fill(done ? Color.green : Color.primary.opacity(0.08))
                if done {
                    Image(systemName: "checkmark").font(.system(size: 10, weight: .bold)).foregroundStyle(.white)
                } else {
                    Text("\(number)").font(.system(size: 11, weight: .semibold))
                }
            }
            .frame(width: 22, height: 22)
            VStack(alignment: .leading, spacing: 1) {
                HStack(spacing: 6) {
                    Text(title).font(.system(size: 13, weight: .medium))
                    if optional, !done {
                        Text(L("ob.optional")).font(.system(size: 10, weight: .medium)).foregroundStyle(.secondary)
                            .padding(.horizontal, 5).padding(.vertical, 1)
                            .background(Capsule().fill(Color.primary.opacity(0.07)))
                    }
                }
                Text(detail).font(.system(size: 11)).foregroundStyle(.secondary)
            }
            Spacer()
            if done {
                Text(L("ob.done")).font(.system(size: 11)).foregroundStyle(.secondary)
            } else {
                Button(actionTitle, action: action).controlSize(.small)
            }
        }
    }
}

struct GuideRow: View {
    let keys: [String]
    let title: String
    let detail: String

    var body: some View {
        HStack(spacing: 12) {
            HStack(spacing: 4) { ForEach(keys, id: \.self) { KeyCap(text: $0) } }
                .frame(width: 104, alignment: .leading)
            VStack(alignment: .leading, spacing: 1) {
                Text(title).font(.system(size: 12.5, weight: .medium))
                Text(detail).font(.system(size: 11)).foregroundStyle(.secondary)
            }
        }
    }
}

@MainActor
final class WindowManager {
    static let shared = WindowManager()
    private var settingsWindow: NSWindow?
    private var onboardingWindow: NSWindow?
    private var updateWindow: NSWindow?
    private var languageWatch: AnyCancellable?

    init() {
        languageWatch = Preferences.shared.$language.dropFirst().sink { [weak self] _ in
            Task { @MainActor in
                self?.settingsWindow?.title = L("settings.title")
                self?.onboardingWindow?.title = L("ob.window")
                self?.updateWindow?.title = L("update.window.title")
            }
        }
    }

    func showSettings(tab: SettingsTab? = nil) {
        if let tab { SettingsRouter.shared.tab = tab }
        if settingsWindow == nil {
            let root = SettingsView()
                .environmentObject(Preferences.shared)
                .environmentObject(Permissions.shared)
            let window = NSWindow(contentViewController: NSHostingController(rootView: root))
            window.styleMask = [.titled, .closable, .miniaturizable]
            window.isReleasedWhenClosed = false
            window.center()
            settingsWindow = window
        }
        settingsWindow?.title = L("settings.title")
        bringForward(settingsWindow)
    }

    /// Opens the update window and checks right away unless an update is already known.
    func showUpdates() {
        let updater = Updater.shared
        switch updater.phase {
        case .available, .downloading, .ready, .installing, .checking: break
        default: Task { await updater.check(userInitiated: true) }
        }
        if updateWindow == nil {
            let root = UpdateView(close: { [weak self] in self?.updateWindow?.close() })
                .environmentObject(Preferences.shared)
            let hosting = NSHostingController(rootView: root)
            let window = NSWindow(contentViewController: hosting)
            window.styleMask = [.titled, .closable]
            window.isReleasedWhenClosed = false
            window.center()
            updateWindow = window
        }
        updateWindow?.title = L("update.window.title")
        bringForward(updateWindow)
    }

    func showOnboarding() {
        if onboardingWindow == nil {
            let root = OnboardingView()
                .environmentObject(Preferences.shared)
                .environmentObject(Permissions.shared)
            let window = NSWindow(contentViewController: NSHostingController(rootView: root))
            window.styleMask = [.titled, .closable, .fullSizeContentView]
            window.titlebarAppearsTransparent = true
            window.titleVisibility = .hidden
            window.isMovableByWindowBackground = true
            window.isReleasedWhenClosed = false
            window.center()
            onboardingWindow = window
        }
        onboardingWindow?.title = L("ob.window")
        bringForward(onboardingWindow)
    }

    func closeOnboarding() {
        onboardingWindow?.close()
    }

    private func bringForward(_ window: NSWindow?) {
        guard let window else { return }
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
        window.orderFrontRegardless()
    }
}
