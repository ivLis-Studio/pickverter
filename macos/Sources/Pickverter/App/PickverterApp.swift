import AppKit
import SwiftUI

@main
enum Entry {
    @MainActor
    static func main() {
        let arguments = Array(CommandLine.arguments.dropFirst())
        if arguments.first == "cli" {
            exit(CLI.run(Array(arguments.dropFirst())))
        }
        PickverterApp.main()
    }
}

struct PickverterApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate
    @StateObject private var prefs = Preferences.shared
    @StateObject private var permissions = Permissions.shared
    @StateObject private var jobs = JobCenter.shared

    var body: some Scene {
        MenuBarExtra {
            MenuBarView()
                .environmentObject(prefs)
                .environmentObject(permissions)
                .environmentObject(jobs)
        } label: {
            Image(nsImage: BrandArt.menuBarIcon())
        }
        .menuBarExtraStyle(.window)
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)
        AppController.shared.start()
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        WindowManager.shared.showSettings()
        return false
    }
}

@MainActor
final class AppController {
    static let shared = AppController()
    let input = InputMonitor()

    func start() {
        // Before anything looks at a permission: grants made to a differently signed copy are cleared once.
        CodeIdentity.clearGrantsAfterSignerChange()
        ClipboardStore.prune()
        Task.detached(priority: .utility) { _ = Tools.ffmpegEncoders() }
        Task.detached(priority: .background) {
            // Vision loads its text models lazily (can take many seconds once); do it before the first OCR.
            try? await Task.sleep(nanoseconds: 4_000_000_000)
            if let blank = CGContext(data: nil, width: 64, height: 32, bitsPerComponent: 8, bytesPerRow: 0,
                                     space: CGColorSpaceCreateDeviceRGB(),
                                     bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue)?.makeImage() {
                _ = try? ImageEngine.recognizeText(in: blank)
            }
        }
        let permissions = Permissions.shared
        permissions.onAccessibilityChange = { [weak self] granted in
            if granted { self?.input.start() } else { self?.input.stop() }
        }
        permissions.startMonitoring()
        if permissions.accessibility { input.start() }
        let updater = Updater.shared
        updater.start()
        AppMover.offerIfNeeded()
        permissions.refreshFolders()
        // Existing users see the new file-access step once.
        let fileAccessNews = !UserDefaults.standard.bool(forKey: "fileAccess.offered") && !permissions.fileAccessReady
        if !Preferences.shared.onboarded || !permissions.accessibility || fileAccessNews {
            WindowManager.shared.showOnboarding()
        } else if updater.updatedFrom != nil {
            JobCenter.shared.notice(L("update.card.done", updater.currentVersion), detail: L("update.card.done.d"),
                                    symbol: "checkmark.circle", tone: .info,
                                    action: updater.releasesPage.map { page in
                                        Job.NoticeAction(title: L("update.whats_new"), symbol: "list.bullet") { NSWorkspace.shared.open(page) }
                                    }, lingers: 8)
            updater.acknowledgeUpdate()
        }
        runDeveloperDemo()
    }

    /// Developer hook (no effect in normal launches):
    ///   PICKVERTER_DEMO_FILES=a:b  PICKVERTER_DEMO_MODE=tools  PICKVERTER_DEMO_SLOT=4,1  PICKVERTER_DEMO_RUN=convert.png,tool.rotate.left
    private func runDeveloperDemo() {
        let env = ProcessInfo.processInfo.environment
        guard let demo = env["PICKVERTER_DEMO_FILES"] else { return }
        let files = demo.split(separator: ":").map { URL(fileURLWithPath: String($0)) }
        Task { @MainActor in
            try? await Task.sleep(nanoseconds: 800_000_000)
            let screen = NSScreen.main?.frame ?? .zero
            let mode: WheelMode? = ["tools": .tools, "actions": .actions, "convert": .convert][env["PICKVERTER_DEMO_MODE"] ?? ""]
            let wheel = WheelController.shared
            wheel.present(files: files, origin: .selection, mode: mode, at: CGPoint(x: screen.midX, y: screen.midY))
            if let slot = env["PICKVERTER_DEMO_SLOT"]?.split(separator: ",").compactMap({ Int($0) }), let first = slot.first {
                wheel.demoHighlight(first, sub: slot.count > 1 ? slot[1] : nil)
            }
            let ids = env["PICKVERTER_DEMO_RUN"]?.split(separator: ",").map(String.init) ?? []
            let options = Catalog.allOptions(for: files)
            for (index, id) in ids.enumerated() {
                guard let option = options.first(where: { $0.id == id }) else { continue }
                try? await Task.sleep(nanoseconds: 300_000_000)
                JobCenter.shared.run(option, mode: .convert, files: files, anchor: .zero,
                                     destination: index == 1 ? .clipboard : .file)
            }
        }
    }
}
