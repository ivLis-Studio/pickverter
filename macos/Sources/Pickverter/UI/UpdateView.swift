import AppKit
import SwiftUI

/// "Software Update" window: what's new, download progress, install.
struct UpdateView: View {
    @ObservedObject var updater = Updater.shared
    let close: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack(alignment: .top, spacing: 14) {
                AppIconArt().frame(width: 58, height: 58)
                VStack(alignment: .leading, spacing: 4) {
                    Text(headline).font(.system(size: 15, weight: .semibold)).fixedSize(horizontal: false, vertical: true)
                    Text(subline).font(.system(size: 12)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }
                Spacer(minLength: 0)
            }
            if let notes = updater.latest?.notes, showsRelease, !notes.isEmpty {
                ScrollView {
                    Text(rendered(notes))
                        .font(.system(size: 12))
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .textSelection(.enabled)
                        .padding(12)
                }
                .frame(minHeight: 120, maxHeight: 240)
                .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(Color.primary.opacity(0.045)))
                .overlay(RoundedRectangle(cornerRadius: 8, style: .continuous).strokeBorder(Color.primary.opacity(0.08), lineWidth: 0.5))
            }
            if case .downloading(let fraction) = updater.phase {
                ProgressView(value: fraction)
            } else if updater.phase == .checking || updater.phase == .installing {
                ProgressView().progressViewStyle(.linear)
            }
            buttons
        }
        .padding(20)
        .frame(width: 500)
    }

    private var showsRelease: Bool {
        switch updater.phase {
        case .available, .downloading, .ready, .installing: return true
        default: return false
        }
    }

    private var headline: String {
        switch updater.phase {
        case .available, .downloading, .ready, .installing:
            return L("update.window.new", updater.latest?.version ?? "")
        case .upToDate: return L("update.window.latest")
        case .checking: return L("update.checking")
        case .failed: return L("update.window.failed")
        case .idle: return L("update.window.title")
        }
    }

    private var subline: String {
        switch updater.phase {
        case .available: return L("update.window.current", updater.currentVersion)
        case .downloading(let fraction): return L("update.downloading", "\(Int(fraction * 100))")
        case .ready: return updater.canInstallInPlace ? L("update.card.ready.d") : L("update.err.location")
        case .installing: return L("update.installing")
        case .upToDate: return L("update.window.current", updater.currentVersion)
        case .failed(let message): return message
        case .checking, .idle: return L("update.window.current", updater.currentVersion)
        }
    }

    private func rendered(_ markdown: String) -> AttributedString {
        let options = AttributedString.MarkdownParsingOptions(interpretedSyntax: .inlineOnlyPreservingWhitespace)
        return (try? AttributedString(markdown: markdown, options: options)) ?? AttributedString(markdown)
    }

    @ViewBuilder
    private var buttons: some View {
        HStack(spacing: 10) {
            if let notesURL = updater.latest?.notesURL.flatMap(URL.init(string:)), showsRelease {
                Link(L("update.window.on_github"), destination: notesURL).font(.system(size: 12))
            } else if let releases = updater.releasesPage {
                Link(L("update.releases"), destination: releases).font(.system(size: 12))
            }
            Spacer()
            switch updater.phase {
            case .available:
                Button(L("update.skip")) { updater.skip(); close() }
                Button(L("update.later")) { close() }.keyboardShortcut(.cancelAction)
                Button(L("update.install")) { updater.download() }.keyboardShortcut(.defaultAction)
            case .downloading:
                Button(L("common.cancel")) { updater.cancelDownload() }.keyboardShortcut(.cancelAction)
            case .ready:
                Button(L("update.later")) { close() }.keyboardShortcut(.cancelAction)
                Button(L("update.restart")) { updater.installAndRelaunch() }.keyboardShortcut(.defaultAction)
            case .failed:
                Button(L("update.close")) { close() }.keyboardShortcut(.cancelAction)
                Button(L("update.retry")) { Task { await updater.check(userInitiated: true) } }.keyboardShortcut(.defaultAction)
            case .checking, .installing:
                Button(L("update.close")) { close() }.keyboardShortcut(.cancelAction)
            case .upToDate, .idle:
                Button(L("update.close")) { close() }.keyboardShortcut(.defaultAction)
            }
        }
    }
}
