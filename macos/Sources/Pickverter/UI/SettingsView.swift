import AppKit
import SwiftUI

enum SettingsTab: String, Hashable {
    case general, shortcuts, wheel, integrations, updates, about
}

@MainActor
final class SettingsRouter: ObservableObject {
    static let shared = SettingsRouter()
    @Published var tab: SettingsTab = .general
}

struct SettingsView: View {
    @EnvironmentObject var prefs: Preferences
    @ObservedObject var router = SettingsRouter.shared

    var body: some View {
        TabView(selection: $router.tab) {
            GeneralSettings().tabItem { Label(L("tab.general"), systemImage: "gearshape") }.tag(SettingsTab.general)
            TriggerSettings().tabItem { Label(L("tab.shortcuts"), systemImage: "keyboard") }.tag(SettingsTab.shortcuts)
            WheelSettings().tabItem { Label(L("tab.wheel"), systemImage: "circle.circle") }.tag(SettingsTab.wheel)
            IntegrationSettings().tabItem { Label(L("tab.integrations"), systemImage: "puzzlepiece.extension") }.tag(SettingsTab.integrations)
            UpdateSettings().tabItem { Label(L("tab.updates"), systemImage: "arrow.down.circle") }.tag(SettingsTab.updates)
            AboutSettings().tabItem { Label(L("tab.about"), systemImage: "info.circle") }.tag(SettingsTab.about)
        }
        .frame(width: 600, height: 540)
        .id(prefs.language)
    }
}

struct GeneralSettings: View {
    @EnvironmentObject var prefs: Preferences

    var body: some View {
        Form {
            Section {
                Toggle(L("set.enable"), isOn: $prefs.enabled)
                Toggle(L("set.login"), isOn: $prefs.launchAtLogin)
                Toggle(L("set.sounds"), isOn: $prefs.playSounds)
                Picker(L("set.language"), selection: $prefs.language) {
                    Text(L("set.language.system")).tag("system")
                    Divider()
                    ForEach(Loc.languages) { Text($0.name).tag($0.code) }
                }
            }
            Section {
                Picker(L("set.destination"), selection: $prefs.destination) {
                    ForEach(ResultDestination.allCases) { destination in
                        Label(destination.label, systemImage: destination.symbol).tag(destination)
                    }
                }
                Picker(L("set.location"), selection: $prefs.outputLocation) {
                    ForEach(OutputLocation.allCases) { Text($0.label).tag($0) }
                }
            } header: {
                Text(L("set.results"))
            } footer: {
                VStack(alignment: .leading, spacing: 4) {
                    Text(L("set.override_hint"))
                    Text(L("set.never_overwrite"))
                }
                .font(.caption)
                .foregroundStyle(.secondary)
            }
            Section {
                Picker(L("set.hud"), selection: $prefs.hudPosition) {
                    ForEach(HUDPosition.allCases) { Text($0.label).tag($0) }
                }
                Picker(L("set.jobs"), selection: $prefs.maxJobs) {
                    Text(L("set.jobs.auto", "\(WorkQueue.shared.limitForAuto)")).tag(0)
                    Divider()
                    ForEach(1...4, id: \.self) { Text("\($0)").tag($0) }
                }
            } header: {
                Text(L("set.cards"))
            } footer: {
                Text(L("set.jobs.d")).font(.caption).foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
    }
}

struct TriggerSettings: View {
    @EnvironmentObject var prefs: Preferences

    var body: some View {
        Form {
            Section(L("set.finder_section")) {
                Picker(L("set.trigger"), selection: $prefs.triggerKey) {
                    ForEach(TriggerKey.allCases) { Text($0.label).tag($0) }
                }
                Toggle(L("set.tap", prefs.triggerKey.glyph), isOn: $prefs.tapTrigger)
                Toggle(L("set.click", prefs.triggerKey.glyph), isOn: $prefs.clickTrigger)
                Toggle(L("set.double"), isOn: $prefs.doubleTap)
            }
            Section(L("set.drag_section")) {
                Toggle(L("set.drag"), isOn: $prefs.dragTrigger)
                LabeledContent("⇧ Shift") { Text(L("mode.convert")).foregroundStyle(.secondary) }
                LabeledContent("⌘ Command") { Text(L("mode.tools")).foregroundStyle(.secondary) }
                LabeledContent("⌃ Control") { Text(L("mode.actions")).foregroundStyle(.secondary) }
            }
            Section(L("set.inwheel")) {
                KeyRow(keys: "← →  ·  1–0", text: L("keys.arrows"))
                KeyRow(keys: "↑ ↓", text: L("keys.updown"))
                KeyRow(keys: "a–z", text: L("keys.type"))
                KeyRow(keys: "Tab  ·  ⇧ ⌘ ⌃", text: L("keys.tab"))
                KeyRow(keys: "⌘C  ·  ⌘S  ·  ⌥-click", text: L("keys.destination"))
                KeyRow(keys: "↩︎  ·  esc", text: L("keys.enter"))
            }
        }
        .formStyle(.grouped)
    }
}

struct KeyRow: View {
    let keys: String
    let text: String
    var body: some View {
        LabeledContent {
            Text(text).foregroundStyle(.secondary).multilineTextAlignment(.trailing)
        } label: {
            Text(keys).font(.system(.body, design: .monospaced))
        }
    }
}

struct IntegrationSettings: View {
    @EnvironmentObject var prefs: Preferences
    @EnvironmentObject var permissions: Permissions
    @State private var refresh = 0

    var body: some View {
        Form {
            Section(L("set.permissions")) {
                PermissionRow(title: L("perm.ax"), detail: L("perm.ax.d"), granted: permissions.accessibility) {
                    permissions.requestAccessibility()
                }
                PermissionRow(title: L("perm.finder"), detail: L("perm.finder.d"), granted: permissions.finder == .granted) {
                    permissions.requestFinder()
                }
                PermissionRow(title: L("perm.fda"), detail: L("perm.fda.d"), granted: permissions.fullDiskAccess) {
                    permissions.requestFullDiskAccess()
                }
                if !permissions.fullDiskAccess {
                    HStack {
                        Image(systemName: permissions.fileAccessReady ? "checkmark.circle.fill" : "circle.dashed")
                            .foregroundStyle(permissions.fileAccessReady ? Color.green : Color.secondary)
                        VStack(alignment: .leading, spacing: 3) {
                            Text(L("perm.folders"))
                            FolderStatusChips()
                        }
                        Spacer()
                        if FileAccess.requestedFolders.isEmpty {
                            Button(L("access.request")) { permissions.requestFolders() }.disabled(permissions.askingFolders)
                        } else {
                            Button(L("access.open_settings")) { FileAccess.openFolderSettings() }
                        }
                    }
                    if permissions.openedFullDiskAccess {
                        HStack(spacing: 8) {
                            AppDragChip()
                            Text(L("access.drag")).font(.caption).foregroundStyle(.secondary)
                        }
                    }
                }
            }
            Section(L("set.apps")) {
                Picker(L("set.terminal"), selection: $prefs.terminal) {
                    ForEach(TerminalChoice.allCases.filter { $0.isInstalled || $0 == .terminal }) { Text($0.label).tag($0) }
                }
                LabeledContent(L("set.editor")) {
                    Text(EditorChoice.installed.map(\.label).joined(separator: ", ")).foregroundStyle(.secondary)
                }
            }
            Section {
                EngineRow(name: "ffmpeg", purpose: L("engine.media"), formula: "ffmpeg", path: Tools.ffmpeg)
                EngineRow(name: "ImageMagick", purpose: L("engine.png"), formula: "imagemagick", path: Tools.magick)
                EngineRow(name: "cwebp", purpose: L("engine.webp"), formula: "webp", path: Tools.cwebp)
                HStack {
                    Button(L("engine.rescan")) {
                        Tools.refresh()
                        refresh += 1
                    }
                    if Tools.ffmpeg == nil {
                        Button(L("engine.install_ffmpeg")) { EngineInstaller.installFFmpeg() }
                    }
                }
            } header: {
                Text(L("set.engines"))
            } footer: {
                Text(L("engine.footer")).font(.caption).foregroundStyle(.secondary)
            }
            .id("engines-\(refresh)")
            Section {
                ForEach(OfficeEngine.IWork.allCases, id: \.self) { app in
                    AppRow(name: app.name, purpose: L("office.iwork." + app.rawValue), installed: app.isInstalled)
                }
                AppRow(name: "LibreOffice", purpose: L("office.libre"), installed: OfficeEngine.libreOffice != nil)
            } header: {
                Text(L("set.office"))
            } footer: {
                Text(L("office.footer")).font(.caption).foregroundStyle(.secondary)
            }
            .id("office-\(refresh)")
        }
        .formStyle(.grouped)
        .onAppear { permissions.refreshFolders() }
    }
}

struct AppRow: View {
    let name: String
    let purpose: String
    let installed: Bool

    var body: some View {
        HStack {
            Image(systemName: installed ? "checkmark.circle.fill" : "minus.circle")
                .foregroundStyle(installed ? Color.green : Color.secondary)
            VStack(alignment: .leading, spacing: 1) {
                Text(name)
                Text(installed ? L("office.installed") : L("office.missing")).font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            Text(purpose).font(.caption).foregroundStyle(.secondary).multilineTextAlignment(.trailing)
        }
    }
}

/// Opens Terminal with "brew install ffmpeg" ready to run (or the Homebrew site when brew is missing).
@MainActor
enum EngineInstaller {
    static var brew: String? {
        ["/opt/homebrew/bin/brew", "/usr/local/bin/brew"].first { FileManager.default.isExecutableFile(atPath: $0) }
    }

    static func installFFmpeg() {
        guard let brew else {
            if let url = URL(string: "https://brew.sh") { NSWorkspace.shared.open(url) }
            return
        }
        let script = FileManager.default.temporaryDirectory.appendingPathComponent("Install ffmpeg for Pickverter.command")
        let body = "#!/bin/sh\nclear\necho 'Pickverter: installing ffmpeg with Homebrew…'\n'\(brew)' install ffmpeg\necho\necho 'Done. You can close this window.'\n"
        do {
            try body.write(to: script, atomically: true, encoding: .utf8)
            try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: script.path)
            NSWorkspace.shared.open(script)
        } catch {
            NSSound.beep()
        }
    }
}

struct WheelSettings: View {
    @ObservedObject var layout = WheelLayout.shared

    var body: some View {
        Form {
            Section {
                Toggle(L("set.wheel.remember"), isOn: $layout.remembersLast)
            } footer: {
                Text(L("set.wheel.remember.d")).font(.caption).foregroundStyle(.secondary)
            }
            Section(L("set.wheel.hidden")) {
                if layout.hidden.isEmpty {
                    Text(L("set.wheel.none")).foregroundStyle(.secondary)
                }
                ForEach(layout.hidden.keys.sorted(), id: \.self) { id in
                    HStack {
                        Text(layout.title(for: id, fallback: layout.hidden[id]))
                        Spacer()
                        Button(L("set.wheel.show")) { layout.unhide(id) }
                    }
                }
            }
            Section(L("set.wheel.pinned")) {
                if layout.pinnedList.isEmpty {
                    Text(L("set.wheel.none")).foregroundStyle(.secondary)
                }
                ForEach(layout.pinnedList, id: \.id) { entry in
                    HStack {
                        Text(entry.title)
                        Spacer()
                        Button(L("ctx.unpin")) { layout.unpin(entry.id) }
                    }
                }
            }
            Section {
                LabeledContent(L("set.wheel.custom_order")) {
                    Text(LP("set.wheel.rings", layout.orders.count)).foregroundStyle(.secondary)
                }
                LabeledContent(L("set.wheel.defaults")) {
                    Text(LP("set.wheel.slots", layout.defaults.count)).foregroundStyle(.secondary)
                }
                Button(L("set.wheel.reset_all"), role: .destructive) { layout.resetAll() }
                    .disabled(!layout.isCustomized && layout.lastUsed.isEmpty)
            } footer: {
                Text(L("set.wheel.footer")).font(.caption).foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
    }
}

struct UpdateSettings: View {
    @EnvironmentObject var prefs: Preferences
    @ObservedObject var updater = Updater.shared

    var body: some View {
        Form {
            Section {
                LabeledContent(L("update.current")) {
                    Text("\(updater.currentVersion) (\(updater.currentBuild))").foregroundStyle(.secondary)
                }
                LabeledContent(L("update.status")) {
                    Text(UpdateText.status(updater)).foregroundStyle(.secondary).multilineTextAlignment(.trailing)
                }
                HStack {
                    Button(L("update.check_now")) { WindowManager.shared.showUpdates() }
                        .disabled(!updater.isEnabled || updater.isBusy)
                    if updater.phase == .ready {
                        Button(L("update.restart")) { updater.installAndRelaunch() }
                    }
                }
            }
            Section {
                Toggle(L("update.auto_check"), isOn: $prefs.autoCheckUpdates)
                Toggle(L("update.auto_download"), isOn: $prefs.autoInstallUpdates)
                    .disabled(!prefs.autoCheckUpdates)
            } footer: {
                Text(updater.isEnabled ? L("update.footer") : L("update.disabled"))
                    .font(.caption).foregroundStyle(.secondary)
            }
            if let releases = updater.releasesPage {
                Section {
                    Link(L("update.releases"), destination: releases)
                }
            }
        }
        .formStyle(.grouped)
    }
}

@MainActor
enum UpdateText {
    static func status(_ updater: Updater) -> String {
        switch updater.phase {
        case .idle, .upToDate:
            guard let date = updater.lastChecked else { return updater.phase == .upToDate ? L("update.latest") : L("update.never") }
            let when = date.formatted(Date.RelativeFormatStyle(presentation: .named).locale(Loc.locale))
            return updater.phase == .upToDate ? L("update.latest") + " · " + when : L("update.checked", when)
        case .checking: return L("update.checking")
        case .available: return L("update.available", updater.latest?.version ?? "")
        case .downloading(let fraction): return L("update.downloading", "\(Int(fraction * 100))")
        case .ready: return L("update.ready", updater.latest?.version ?? "")
        case .installing: return L("update.installing")
        case .failed(let message): return message
        }
    }
}

struct PermissionRow: View {
    let title: String
    let detail: String
    let granted: Bool
    let action: () -> Void

    var body: some View {
        HStack {
            Image(systemName: granted ? "checkmark.circle.fill" : "circle.dashed")
                .foregroundStyle(granted ? Color.green : Color.secondary)
            VStack(alignment: .leading, spacing: 1) {
                Text(title)
                Text(detail).font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            if !granted { Button(L("perm.allow"), action: action) }
        }
    }
}

struct EngineRow: View {
    let name: String
    let purpose: String
    let formula: String
    let path: String?

    var body: some View {
        HStack {
            Image(systemName: path != nil ? "checkmark.circle.fill" : "minus.circle")
                .foregroundStyle(path != nil ? Color.green : Color.secondary)
            VStack(alignment: .leading, spacing: 1) {
                Text(name)
                Text(path ?? L("engine.missing", formula))
                    .font(.caption).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
            }
            Spacer()
            Text(purpose).font(.caption).foregroundStyle(.secondary)
        }
    }
}

struct AboutSettings: View {
    @ObservedObject var updater = Updater.shared

    var body: some View {
        VStack(spacing: 12) {
            AppIconArt().frame(width: 104, height: 104)
            Text("Pickverter").font(.system(size: 22, weight: .semibold))
            Text(L("about.version", updater.currentVersion) + (updater.isReleaseBuild ? "" : " · " + L("about.dev_build")))
                .foregroundStyle(.secondary)
            Text(L("about.tagline"))
                .multilineTextAlignment(.center)
                .foregroundStyle(.secondary)
                .font(.callout)
            if let page = updater.repositoryPage {
                HStack(spacing: 16) {
                    Link(L("about.github"), destination: page)
                    if let issues = URL(string: page.absoluteString + "/issues") { Link(L("about.issues"), destination: issues) }
                }
                .font(.callout)
                .padding(.top, 4)
            }
            Text(L("about.license")).font(.caption).foregroundStyle(.tertiary)
        }
        .padding(30)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}
