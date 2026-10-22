import AppKit
import ServiceManagement

enum TriggerKey: String, CaseIterable, Identifiable {
    case option, control, function, rightCommand

    var id: String { rawValue }

    var label: String {
        switch self {
        case .option: return "⌥ Option"
        case .control: return "⌃ Control"
        case .function: return "fn / 🌐"
        case .rightCommand: return L("trigger.rcmd")
        }
    }

    var glyph: String {
        switch self {
        case .option: return "⌥"
        case .control: return "⌃"
        case .function: return "fn"
        case .rightCommand: return "⌘"
        }
    }

    var keyCodes: Set<Int64> {
        switch self {
        case .option: return [58, 61]
        case .control: return [59, 62]
        case .function: return [63]
        case .rightCommand: return [54]
        }
    }
}

enum TerminalChoice: String, CaseIterable, Identifiable {
    case terminal, iterm, ghostty, warp

    var id: String { rawValue }

    var label: String {
        switch self {
        case .terminal: return L("app.terminal")
        case .iterm: return "iTerm"
        case .ghostty: return "Ghostty"
        case .warp: return "Warp"
        }
    }

    var bundleID: String {
        switch self {
        case .terminal: return "com.apple.Terminal"
        case .iterm: return "com.googlecode.iterm2"
        case .ghostty: return "com.mitchellh.ghostty"
        case .warp: return "dev.warp.Warp-Stable"
        }
    }

    var isInstalled: Bool { NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID) != nil }

    static var current: TerminalChoice {
        TerminalChoice(rawValue: UserDefaults.standard.string(forKey: "terminal") ?? "") ?? .terminal
    }
}

/// Corner of the screen where result cards stack up.
enum HUDPosition: String, CaseIterable, Identifiable {
    case topRight, bottomRight, topLeft, bottomLeft

    var id: String { rawValue }
    var isTop: Bool { self == .topRight || self == .topLeft }
    var isLeft: Bool { self == .topLeft || self == .bottomLeft }
    var label: String { L("hud." + rawValue) }
}

@MainActor
final class Preferences: ObservableObject {
    static let shared = Preferences()
    private let store = UserDefaults.standard

    @Published var enabled: Bool { didSet { store.set(enabled, forKey: "enabled") } }
    @Published var triggerKey: TriggerKey { didSet { store.set(triggerKey.rawValue, forKey: "triggerKey") } }
    @Published var tapTrigger: Bool { didSet { store.set(tapTrigger, forKey: "tapTrigger") } }
    @Published var clickTrigger: Bool { didSet { store.set(clickTrigger, forKey: "clickTrigger") } }
    @Published var doubleTap: Bool { didSet { store.set(doubleTap, forKey: "doubleTap") } }
    @Published var dragTrigger: Bool { didSet { store.set(dragTrigger, forKey: "dragTrigger") } }
    @Published var outputLocation: OutputLocation { didSet { store.set(outputLocation.rawValue, forKey: "outputLocation") } }
    @Published var destination: ResultDestination { didSet { store.set(destination.rawValue, forKey: "destination") } }
    /// "system" or a code from Loc.languages.
    @Published var language: String {
        didSet {
            store.set(language, forKey: "language")
            Loc.apply(preference: language)
            // Lets AppKit-provided UI (open panels, alerts) follow on the next launch.
            if language == "system" {
                store.removeObject(forKey: "AppleLanguages")
            } else {
                store.set([language], forKey: "AppleLanguages")
            }
        }
    }
    @Published var terminal: TerminalChoice { didSet { store.set(terminal.rawValue, forKey: "terminal") } }
    @Published var playSounds: Bool { didSet { store.set(playSounds, forKey: "playSounds") } }
    @Published var onboarded: Bool { didSet { store.set(onboarded, forKey: "onboarded") } }
    @Published var hudPosition: HUDPosition { didSet { store.set(hudPosition.rawValue, forKey: "hudPosition") } }
    /// 0 = automatic (read by WorkQueue straight from the defaults).
    @Published var maxJobs: Int { didSet { store.set(maxJobs, forKey: "maxJobs") } }
    @Published var autoCheckUpdates: Bool { didSet { store.set(autoCheckUpdates, forKey: "autoCheckUpdates") } }
    /// Download in the background and offer a one-click restart.
    @Published var autoInstallUpdates: Bool { didSet { store.set(autoInstallUpdates, forKey: "autoInstallUpdates") } }
    @Published var launchAtLogin: Bool {
        didSet {
            guard launchAtLogin != (SMAppService.mainApp.status == .enabled) else { return }
            do {
                if launchAtLogin { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
            } catch {
                NSLog("Pickverter login item: \(error)")
            }
        }
    }

    private init() {
        store.register(defaults: [
            "enabled": true, "tapTrigger": true, "clickTrigger": true, "doubleTap": false,
            "dragTrigger": true, "playSounds": true, "onboarded": false,
            "autoCheckUpdates": true, "autoInstallUpdates": true, "maxJobs": 0,
        ])
        enabled = store.bool(forKey: "enabled")
        triggerKey = TriggerKey(rawValue: store.string(forKey: "triggerKey") ?? "") ?? .option
        tapTrigger = store.bool(forKey: "tapTrigger")
        clickTrigger = store.bool(forKey: "clickTrigger")
        doubleTap = store.bool(forKey: "doubleTap")
        dragTrigger = store.bool(forKey: "dragTrigger")
        outputLocation = OutputLocation(rawValue: store.string(forKey: "outputLocation") ?? "") ?? .sameFolder
        destination = ResultDestination(rawValue: store.string(forKey: "destination") ?? "") ?? .file
        language = store.string(forKey: "language") ?? "system"
        terminal = TerminalChoice.current
        playSounds = store.bool(forKey: "playSounds")
        onboarded = store.bool(forKey: "onboarded")
        hudPosition = HUDPosition(rawValue: store.string(forKey: "hudPosition") ?? "") ?? .topRight
        maxJobs = store.integer(forKey: "maxJobs")
        autoCheckUpdates = store.bool(forKey: "autoCheckUpdates")
        autoInstallUpdates = store.bool(forKey: "autoInstallUpdates")
        launchAtLogin = SMAppService.mainApp.status == .enabled
    }
}

@MainActor
final class Permissions: ObservableObject {
    static let shared = Permissions()

    enum Automation { case unknown, granted, denied }

    @Published private(set) var accessibility = AXIsProcessTrusted()
    @Published private(set) var finder: Automation = .unknown
    var onAccessibilityChange: ((Bool) -> Void)?
    private var timer: Timer?

    func startMonitoring() {
        timer?.invalidate()
        timer = Timer.scheduledTimer(withTimeInterval: 1.0, repeats: true) { _ in
            Task { @MainActor in Permissions.shared.refresh() }
        }
        refreshFinder(ask: false)
    }

    func refresh() {
        let trusted = AXIsProcessTrusted()
        if trusted != accessibility {
            accessibility = trusted
            onAccessibilityChange?(trusted)
        }
    }

    func requestAccessibility() {
        let options = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
        _ = AXIsProcessTrustedWithOptions(options)
        openPrivacyPane("Privacy_Accessibility")
    }

    func requestFinder() {
        refreshFinder(ask: true)
    }

    func refreshFinder(ask: Bool) {
        Task.detached(priority: .userInitiated) {
            let state = FinderAutomation.status(ask: ask)
            await MainActor.run {
                Permissions.shared.finder = state
                if ask, state == .denied { Permissions.shared.openPrivacyPane("Privacy_Automation") }
            }
        }
    }

    func noteFinderDenied() { finder = .denied }

    func openPrivacyPane(_ anchor: String) {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?\(anchor)") {
            NSWorkspace.shared.open(url)
        }
    }
}

enum FinderAutomation {
    static func status(ask: Bool) -> Permissions.Automation {
        let target = NSAppleEventDescriptor(bundleIdentifier: "com.apple.finder")
        guard let desc = target.aeDesc else { return .unknown }
        let wildcard: UInt32 = 0x2A2A_2A2A
        let status = AEDeterminePermissionToAutomateTarget(desc, wildcard, wildcard, ask)
        switch status {
        case 0: return .granted
        case -1743: return .denied
        default: return .unknown
        }
    }
}
