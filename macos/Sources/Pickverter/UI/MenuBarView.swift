import AppKit
import SwiftUI

struct MenuBarView: View {
    @EnvironmentObject var prefs: Preferences
    @EnvironmentObject var permissions: Permissions
    @EnvironmentObject var jobs: JobCenter
    @ObservedObject private var updater = Updater.shared

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(spacing: 10) {
                WheelGlyph(size: 28)
                VStack(alignment: .leading, spacing: 1) {
                    Text("Pickverter").font(.system(size: 13, weight: .semibold))
                    Text(status).font(.system(size: 11)).foregroundStyle(.secondary)
                }
                Spacer()
                Toggle("", isOn: $prefs.enabled).toggleStyle(.switch).labelsHidden().controlSize(.small)
            }

            if !permissions.accessibility {
                PermissionBanner()
            }

            VStack(alignment: .leading, spacing: 8) {
                SectionLabel(L("menu.how"))
                GestureLine(keys: [prefs.triggerKey.glyph], suffix: prefs.doubleTap ? L("menu.double") : L("menu.tap"),
                            text: L("menu.tap.text"))
                GestureLine(keys: [prefs.triggerKey.glyph], suffix: L("menu.click"), text: L("menu.click.text"))
                GestureLine(keys: ["⇧", "⌘", "⌃"], prefix: L("menu.drag"), text: L("menu.drag.text"))
            }

            HStack {
                SectionLabel(L("set.destination"))
                Spacer()
                Picker("", selection: $prefs.destination) {
                    ForEach(ResultDestination.allCases) { Text($0.label).tag($0) }
                }
                .labelsHidden()
                .pickerStyle(.menu)
                .fixedSize()
                .controlSize(.small)
            }

            if !jobs.jobs.isEmpty {
                VStack(alignment: .leading, spacing: 6) {
                    SectionLabel(L("menu.recent"))
                    ForEach(jobs.jobs.prefix(4)) { job in RecentJobRow(job: job) }
                }
            }

            if let version = updater.latest?.version, [.available, .ready].contains(updater.phase) {
                HStack(spacing: 8) {
                    Image(systemName: "arrow.down.circle.fill").foregroundStyle(Theme.accent)
                    Text(L("menu.update", version)).font(.system(size: 12)).lineLimit(1)
                    Spacer(minLength: 6)
                    Button(updater.phase == .ready ? L("update.restart") : L("update.card.details")) {
                        if updater.phase == .ready { updater.installAndRelaunch() } else { WindowManager.shared.showUpdates() }
                    }
                    .controlSize(.small)
                }
                .padding(10)
                .background(RoundedRectangle(cornerRadius: 9, style: .continuous).fill(Color.primary.opacity(0.05)))
            }

            Divider()
            HStack(spacing: 14) {
                Button(L("menu.settings")) { WindowManager.shared.showSettings() }
                Menu {
                    Button(L("menu.guide")) { WindowManager.shared.showOnboarding() }
                    Button(L("menu.check_updates")) { WindowManager.shared.showUpdates() }
                        .disabled(!updater.isEnabled)
                    if let page = updater.repositoryPage {
                        Divider()
                        Button(L("about.github")) { NSWorkspace.shared.open(page) }
                    }
                } label: {
                    Image(systemName: "ellipsis.circle")
                }
                .menuStyle(.borderlessButton)
                .menuIndicator(.hidden)
                .fixedSize()
                Spacer()
                Button(L("menu.quit")) { NSApp.terminate(nil) }
            }
            .buttonStyle(.plain)
            .font(.system(size: 12))
            .foregroundStyle(.secondary)
        }
        .padding(16)
        .frame(width: 320)
    }

    private var status: String {
        if !permissions.accessibility { return L("menu.status.permission") }
        return prefs.enabled ? L("menu.status.ready") : L("menu.status.paused")
    }
}

struct SectionLabel: View {
    let text: String
    init(_ text: String) { self.text = text }
    var body: some View {
        Text(text).font(.system(size: 11, weight: .medium)).foregroundStyle(.secondary)
    }
}

struct GestureLine: View {
    let keys: [String]
    var prefix: String? = nil
    var suffix: String? = nil
    let text: String

    var body: some View {
        HStack(spacing: 6) {
            if let prefix { Text(prefix).font(.system(size: 11.5)).foregroundStyle(.secondary) }
            ForEach(keys, id: \.self) { KeyCap(text: $0) }
            if let suffix { Text(suffix).font(.system(size: 11.5)).foregroundStyle(.secondary) }
            Spacer(minLength: 8)
            Text(text).font(.system(size: 12)).lineLimit(1).minimumScaleFactor(0.8)
        }
    }
}

struct PermissionBanner: View {
    @EnvironmentObject var permissions: Permissions

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Label(L("perm.banner.title"), systemImage: "hand.raised")
                .font(.system(size: 12, weight: .semibold))
            Text(L("perm.banner.detail"))
                .font(.system(size: 11)).foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Button(L("perm.banner.button")) { permissions.requestAccessibility() }
                .controlSize(.small)
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Color.primary.opacity(0.05)))
    }
}

struct RecentJobRow: View {
    @ObservedObject var job: Job

    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: job.symbol)
                .font(.system(size: 11))
                .foregroundStyle(.secondary)
                .frame(width: 22, height: 22)
                .background(Circle().fill(Color.primary.opacity(0.07)))
            VStack(alignment: .leading, spacing: 0) {
                Text(job.headline).font(.system(size: 12)).lineLimit(1)
                Text(job.created.formatted(Date.RelativeFormatStyle(presentation: .named).locale(Loc.locale)))
                    .font(.system(size: 10)).foregroundStyle(.secondary)
            }
            Spacer()
            if job.hasFiles, !job.inCache {
                Button { job.reveal() } label: { Image(systemName: "folder") }
                    .buttonStyle(.plain)
                    .foregroundStyle(.secondary)
                    .help(L("card.reveal"))
            }
        }
    }
}
