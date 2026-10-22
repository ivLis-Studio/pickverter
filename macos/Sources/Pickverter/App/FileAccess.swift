import AppKit
import SwiftUI

/// Folders macOS guards with a one-time prompt (Files & Folders). Full Disk Access covers all of them,
/// plus external and network drives.
enum ProtectedFolder: String, CaseIterable, Identifiable {
    case desktop, documents, downloads, iCloud

    var id: String { rawValue }

    var url: URL? {
        let fm = FileManager.default
        switch self {
        case .desktop: return fm.urls(for: .desktopDirectory, in: .userDomainMask).first
        case .documents: return fm.urls(for: .documentDirectory, in: .userDomainMask).first
        case .downloads: return fm.urls(for: .downloadsDirectory, in: .userDomainMask).first
        case .iCloud:
            let drive = URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent("Library/Mobile Documents/com~apple~CloudDocs")
            return fm.fileExists(atPath: drive.path) ? drive : nil
        }
    }

    var label: String {
        switch self {
        case .desktop: return L("folder.desktop")
        case .documents: return L("folder.documents")
        case .downloads: return L("folder.downloads")
        case .iCloud: return L("folder.icloud")
        }
    }

    /// The ones that exist on this Mac.
    static var available: [ProtectedFolder] { allCases.filter { $0.url != nil } }
}

enum FolderAccess: Equatable {
    case unknown, granted, denied
}

enum FileAccess {
    /// Files only Full Disk Access can open. Opening them never shows a prompt.
    static var probes: [String] {
        [NSHomeDirectory() + "/Library/Application Support/com.apple.TCC/TCC.db",
         "/Library/Application Support/com.apple.TCC/TCC.db",
         NSHomeDirectory() + "/Library/Safari/Bookmarks.plist"]
    }

    static var hasFullDiskAccess: Bool {
        for path in probes {
            let descriptor = Darwin.open(path, O_RDONLY)
            if descriptor >= 0 {
                Darwin.close(descriptor)
                return true
            }
        }
        return false
    }

    /// Touching a guarded folder shows the macOS prompt the first time (the call waits for the answer),
    /// so this must run off the main thread.
    static func probe(_ folder: ProtectedFolder) -> FolderAccess {
        guard let url = folder.url else { return .unknown }
        do {
            _ = try FileManager.default.contentsOfDirectory(atPath: url.path)
            return .granted
        } catch {
            return isAccessDenied(error) ? .denied : .unknown
        }
    }

    private static let requestedKey = "fileAccess.requested"

    /// Folders the user has already answered for; re-checking those never prompts.
    static var requestedFolders: Set<ProtectedFolder> {
        get { Set((UserDefaults.standard.stringArray(forKey: requestedKey) ?? []).compactMap(ProtectedFolder.init(rawValue:))) }
        set { UserDefaults.standard.set(newValue.map(\.rawValue).sorted(), forKey: requestedKey) }
    }

    /// TCC refusals come back as EPERM ("Operation not permitted"), often wrapped in a Cocoa file error.
    /// EACCES is ordinary Unix permissions and isn't something System Settings can fix.
    static func isAccessDenied(_ error: Error) -> Bool {
        if error is Denied { return true }
        if let failure = error as? Shell.Failure { return failure.stderr.contains("Operation not permitted") }
        let ns = error as NSError
        if let underlying = ns.userInfo[NSUnderlyingErrorKey] as? NSError {
            if underlying.domain == NSPOSIXErrorDomain { return underlying.code == Int(EPERM) }
            return isAccessDenied(underlying)
        }
        if ns.domain == NSPOSIXErrorDomain { return ns.code == Int(EPERM) }
        return ns.domain == NSCocoaErrorDomain && [NSFileReadNoPermissionError, NSFileWriteNoPermissionError].contains(ns.code)
    }

    struct Denied: LocalizedError {
        let url: URL
        var errorDescription: String? { L("err.no_access", FileAccess.placeName(for: url)) }
    }

    /// "Desktop" for anything inside the Desktop, otherwise the enclosing folder or volume name.
    static func placeName(for url: URL) -> String {
        let path = url.standardizedFileURL.path
        for folder in ProtectedFolder.allCases {
            if let root = folder.url?.standardizedFileURL.path, path == root || path.hasPrefix(root + "/") { return folder.label }
        }
        if path.hasPrefix("/Volumes/") {
            return String(path.dropFirst("/Volumes/".count).split(separator: "/").first ?? "")
        }
        return url.deletingLastPathComponent().lastPathComponent
    }

    /// Opens every input once before a job starts: a pending macOS prompt appears now (not halfway through),
    /// and a refusal becomes a clear message instead of an engine error. Returns the first blocked file.
    static func firstBlocked(_ urls: [URL]) async -> URL? {
        await Task.detached(priority: .userInitiated) { () -> URL? in
            for url in urls {
                do {
                    var isDir: ObjCBool = false
                    guard FileManager.default.fileExists(atPath: url.path, isDirectory: &isDir) else { continue }
                    if isDir.boolValue {
                        _ = try FileManager.default.contentsOfDirectory(atPath: url.path)
                    } else {
                        let handle = try FileHandle(forReadingFrom: url)
                        try handle.close()
                    }
                } catch {
                    if isAccessDenied(error) { return url }
                }
            }
            return nil
        }.value
    }

    @MainActor
    static func openFullDiskAccessSettings() {
        open("x-apple.systempreferences:com.apple.preference.security?Privacy_AllFiles")
    }

    @MainActor
    static func openFolderSettings() {
        open("x-apple.systempreferences:com.apple.preference.security?Privacy_FilesAndFolders")
    }

    /// Where a "Grant Access" button should lead: the folder list if the user went that way, else Full Disk Access.
    @MainActor
    static func openBestSettings() {
        if requestedFolders.isEmpty { openFullDiskAccessSettings() } else { openFolderSettings() }
    }

    @MainActor
    private static func open(_ link: String) {
        if let url = URL(string: link) { NSWorkspace.shared.open(url) }
    }
}

/// The app itself as a drag source: dropping it on the Full Disk Access list adds Pickverter
/// when macOS hasn't listed it on its own.
struct AppDragChip: View {
    var body: some View {
        HStack(spacing: 6) {
            Image(nsImage: NSWorkspace.shared.icon(forFile: Bundle.main.bundlePath))
                .resizable()
                .frame(width: 18, height: 18)
            Text("Pickverter").font(.system(size: 11, weight: .medium))
            Image(systemName: "hand.draw").font(.system(size: 10)).foregroundStyle(.secondary)
        }
        .padding(.horizontal, 8)
        .padding(.vertical, 4)
        .background(Capsule().fill(Color.primary.opacity(0.06)))
        .overlay(Capsule().strokeBorder(Color.primary.opacity(0.12), lineWidth: 0.5))
        .onDrag { NSItemProvider(contentsOf: Bundle.main.bundleURL) ?? NSItemProvider() }
        .help(L("access.drag.help"))
    }
}

/// Small "✓ Desktop  ✓ Documents  ✕ Downloads" row that wraps instead of truncating long names.
struct FolderStatusChips<Leading: View>: View {
    @EnvironmentObject var permissions: Permissions
    private let leading: Leading

    init(@ViewBuilder leading: () -> Leading) { self.leading = leading() }

    var body: some View {
        FlowLayout(spacing: 8, lineSpacing: 3) {
            leading
            ForEach(ProtectedFolder.available) { folder in
                let state = permissions.fullDiskAccess ? .granted : (permissions.folders[folder] ?? .unknown)
                HStack(spacing: 3) {
                    Image(systemName: state == .granted ? "checkmark.circle.fill" : state == .denied ? "xmark.circle.fill" : "circle.dashed")
                        .foregroundStyle(state == .granted ? Color.green : state == .denied ? Color.orange : Color.secondary)
                    Text(folder.label).foregroundStyle(.secondary)
                }
                .font(.system(size: 10.5))
                .lineLimit(1)
                .accessibilityElement(children: .combine)
                .accessibilityValue(L("access.state." + (state == .granted ? "granted" : state == .denied ? "denied" : "unknown")))
            }
        }
    }
}

extension FolderStatusChips where Leading == EmptyView {
    init() { self.leading = EmptyView() }
}

/// Places children left to right and starts a new line when the next one wouldn't fit.
struct FlowLayout: Layout {
    var spacing: CGFloat = 8
    var lineSpacing: CGFloat = 4

    private struct Line {
        var items: [Int] = []
        var width: CGFloat = 0
        var height: CGFloat = 0
    }

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let lines = arrange(subviews, width: proposal.width ?? .infinity)
        let width = lines.map(\.width).max() ?? 0
        let height = lines.reduce(0) { $0 + $1.height } + lineSpacing * CGFloat(max(lines.count - 1, 0))
        return CGSize(width: width, height: height)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        var y = bounds.minY
        for line in arrange(subviews, width: bounds.width) {
            var x = bounds.minX
            for index in line.items {
                let size = subviews[index].sizeThatFits(.unspecified)
                subviews[index].place(at: CGPoint(x: x, y: y + (line.height - size.height) / 2), proposal: ProposedViewSize(size))
                x += size.width + spacing
            }
            y += line.height + lineSpacing
        }
    }

    private func arrange(_ subviews: Subviews, width: CGFloat) -> [Line] {
        var lines = [Line()]
        for index in subviews.indices {
            let size = subviews[index].sizeThatFits(.unspecified)
            if !lines[lines.count - 1].items.isEmpty, lines[lines.count - 1].width + spacing + size.width > width {
                lines.append(Line())
            }
            var line = lines.removeLast()
            line.width += (line.items.isEmpty ? 0 : spacing) + size.width
            line.height = max(line.height, size.height)
            line.items.append(index)
            lines.append(line)
        }
        return lines
    }
}
