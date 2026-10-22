import AppKit
import Foundation

/// The three rings of the wheel. Each one maps to a modifier key while dragging.
enum WheelMode: Int, CaseIterable, Identifiable {
    case convert, tools, actions

    var id: Int { rawValue }

    var title: String {
        switch self {
        case .convert: return L("mode.convert")
        case .tools: return L("mode.tools")
        case .actions: return L("mode.actions")
        }
    }

    var keyGlyph: String {
        switch self {
        case .convert: return "⇧"
        case .tools: return "⌘"
        case .actions: return "⌃"
        }
    }
}

enum OutputLocation: String, CaseIterable, Identifiable {
    case sameFolder, downloads, desktop

    var id: String { rawValue }

    var label: String {
        switch self {
        case .sameFolder: return L("set.location.same")
        case .downloads: return L("set.location.downloads")
        case .desktop: return L("set.location.desktop")
        }
    }

    func directory(for source: URL) -> URL {
        let fm = FileManager.default
        switch self {
        case .sameFolder:
            let dir = source.deletingLastPathComponent()
            return fm.isWritableFile(atPath: dir.path) ? dir : Self.downloadsFolder
        case .downloads:
            return Self.downloadsFolder
        case .desktop:
            return fm.urls(for: .desktopDirectory, in: .userDomainMask).first ?? Self.downloadsFolder
        }
    }

    static var downloadsFolder: URL {
        FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask).first
            ?? URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent("Downloads")
    }
}

/// Where a finished result goes. `ask` is resolved by the wheel before a job runs.
enum ResultDestination: String, CaseIterable, Identifiable {
    case file, clipboard, both, ask

    var id: String { rawValue }
    var label: String { L("dest." + rawValue) }

    var symbol: String {
        switch self {
        case .file: return "doc"
        case .clipboard: return "doc.on.clipboard"
        case .both: return "doc.on.doc"
        case .ask: return "questionmark.circle"
        }
    }

    var copies: Bool { self == .clipboard || self == .both }
}

struct PickResult {
    var outputs: [URL] = []
    var message: String? = nil
    /// New files that can be moved to the Trash from the result card.
    var undoable = true
    /// No result card at all (AirDrop, Terminal, ...).
    var silent = false

    static let quiet = PickResult(undoable: false, silent: true)

    static func note(_ message: String) -> PickResult {
        PickResult(message: message, undoable: false)
    }
}

struct PickError: LocalizedError {
    let message: String
    init(_ message: String) { self.message = message }
    var errorDescription: String? { message }
}

/// Thrown when a file in a batch needs no work (e.g. already the target format).
struct PickSkip: Error {}

struct PickOption: Identifiable {
    let id: String
    let title: String
    let detail: String
    let symbol: String
    var isFormat = false
    var outputExt: String? = nil
    var unavailable: String? = nil
    /// Creates new files, so the result can go to a file, the clipboard or both.
    var producesFiles = true
    /// Shown on the outer ring when the slot is hovered. `perform` runs the first one.
    var variants: [PickOption] = []
    /// Opens an editor window first; the result card only appears once the work itself starts.
    var interactive = false
    /// Set on a variant that the user pinned to the main ring: the id of the slot it belongs to.
    var pinnedFrom: String? = nil
    let perform: (PickContext) async throws -> PickResult
}

/// Fires once, the first time real work starts (shared by a context and all of its slices).
final class StartSignal: @unchecked Sendable {
    private let lock = NSLock()
    private var action: (() -> Void)?

    func set(_ action: @escaping () -> Void) { lock.withLock { self.action = action } }

    func fire() {
        let pending: (() -> Void)? = lock.withLock {
            let current = action
            action = nil
            return current
        }
        pending?()
    }
}

/// Everything an engine needs to run one option: inputs, output policy and progress reporting.
final class PickContext: @unchecked Sendable {
    let files: [URL]
    let location: OutputLocation
    let destination: ResultDestination
    let anchor: CGPoint
    let jobID: UUID
    let started: StartSignal
    private let sink: (Double?, String?) -> Void
    private let offset: Double
    private let scale: Double

    init(files: [URL], location: OutputLocation, destination: ResultDestination = .file, anchor: CGPoint = .zero,
         jobID: UUID = UUID(), offset: Double = 0, scale: Double = 1, started: StartSignal = StartSignal(),
         sink: @escaping (Double?, String?) -> Void) {
        self.files = files
        self.location = location
        self.destination = destination
        self.anchor = anchor
        self.jobID = jobID
        self.offset = offset
        self.scale = scale
        self.started = started
        self.sink = sink
    }

    /// A copy of this context working on other files (same job, progress and output policy).
    func with(files: [URL]) -> PickContext {
        PickContext(files: files, location: location, destination: destination, anchor: anchor, jobID: jobID,
                    offset: offset, scale: scale, started: started, sink: sink)
    }

    func report(_ fraction: Double?, _ note: String? = nil) {
        started.fire()
        sink(fraction.map { offset + max(0, min(1, $0)) * scale }, note)
    }

    /// Called by editors once the user confirmed: from here on the job is real work.
    func beginWork() { started.fire() }

    /// A child context whose progress maps into one slot of a batch.
    func slice(_ index: Int, of count: Int) -> PickContext {
        let width = scale / Double(max(count, 1))
        return PickContext(files: files, location: location, destination: destination, anchor: anchor, jobID: jobID,
                           offset: offset + width * Double(index), scale: width, started: started, sink: sink)
    }

    /// Clipboard-only results are written to a private cache folder instead of next to the original.
    func directory(for source: URL) -> URL {
        if destination == .clipboard { return ClipboardStore.folder(for: jobID) }
        return location.directory(for: source)
    }

    /// "photo.heic" + ext "png" -> "photo.png"; with suffix -> "photo compressed.png". Never overwrites.
    func destination(for source: URL, ext: String?, suffix: String? = nil) -> URL {
        var name = FileNames.split(source.lastPathComponent).base
        if let suffix, !suffix.isEmpty { name += " " + suffix }
        if let ext, !ext.isEmpty { name += "." + ext }
        return FileNames.unique(directory(for: source).appendingPathComponent(name))
    }

    /// Runs `body` against a hidden staging path, then moves the finished result into place.
    /// Finder never sees half-written files and failures leave nothing behind. Heavy work waits
    /// for a free slot in the shared work queue so several jobs don't fight over the CPU.
    func produce(_ destination: URL, _ body: (URL) async throws -> Void) async throws -> URL {
        beginWork()
        if WorkQueue.isHoldingSlot { return try await stage(destination, body) }
        let queue = WorkQueue.shared
        if queue.isFull { sink(nil, L("job.queued")) }
        try await queue.acquire()
        defer { queue.release() }
        sink(nil, "")
        return try await WorkQueue.$isHoldingSlot.withValue(true) {
            try await stage(destination, body)
        }
    }

    private func stage(_ destination: URL, _ body: (URL) async throws -> Void) async throws -> URL {
        let fm = FileManager.default
        let folder = destination.deletingLastPathComponent()
        try fm.createDirectory(at: folder, withIntermediateDirectories: true)
        // Staging inside the destination folder (hidden) keeps the final step a rename on the same volume,
        // so big results on external drives aren't copied twice. Read-only folders fall back to /tmp.
        var work = folder.appendingPathComponent(".pickverter-" + UUID().uuidString.prefix(8), isDirectory: true)
        if (try? fm.createDirectory(at: work, withIntermediateDirectories: false)) == nil {
            work = fm.temporaryDirectory.appendingPathComponent("pickverter-" + UUID().uuidString, isDirectory: true)
            try fm.createDirectory(at: work, withIntermediateDirectories: true)
        }
        defer { try? fm.removeItem(at: work) }
        let staged = work.appendingPathComponent(destination.lastPathComponent)
        try await body(staged)
        guard fm.fileExists(atPath: staged.path) else { throw PickError(L("err.no_result")) }
        var final = FileNames.unique(destination)
        do {
            try fm.moveItem(at: staged, to: final)
        } catch {
            // Something took the name in the meantime: pick the next free one.
            final = FileNames.unique(final)
            try fm.moveItem(at: staged, to: final)
        }
        return final
    }
}

enum Batch {
    /// Runs `body` for every selected file, keeps going past individual failures.
    static func perFile(_ body: @escaping (URL, PickContext) async throws -> URL) -> (PickContext) async throws -> PickResult {
        return { ctx in
            var outputs: [URL] = []
            var failures: [String] = []
            var skipped = 0
            let count = ctx.files.count
            for (index, file) in ctx.files.enumerated() {
                try Task.checkCancellation()
                if count > 1 { ctx.report(Double(index) / Double(count), "\(index + 1)/\(count) · \(file.lastPathComponent)") }
                do {
                    outputs.append(try await body(file, ctx.slice(index, of: count)))
                } catch is PickSkip {
                    skipped += 1
                } catch {
                    if Task.isCancelled { throw CancellationError() }
                    if count == 1 { throw error }
                    failures.append("\(file.lastPathComponent): \(error.localizedDescription)")
                }
            }
            if outputs.isEmpty {
                if let first = failures.first { throw PickError(first) }
                if skipped > 0 { throw PickError(L("err.same_format")) }
            }
            var message: String?
            if !failures.isEmpty { message = LP("job.failed_some", failures.count, failures[0]) }
            return PickResult(outputs: outputs, message: message)
        }
    }
}

enum ScreenGeometry {
    static var primaryHeight: CGFloat { NSScreen.screens.first?.frame.height ?? 0 }

    /// Quartz event coordinates (top-left origin) -> AppKit screen coordinates (bottom-left origin).
    static func appKit(fromQuartz point: CGPoint) -> CGPoint {
        CGPoint(x: point.x, y: primaryHeight - point.y)
    }
}

/// Set PICKVERTER_DEBUG=1 to trace gestures in the console.
enum Debug {
    static let enabled = ProcessInfo.processInfo.environment["PICKVERTER_DEBUG"] != nil
    static func log(_ message: @autoclosure () -> String) {
        if enabled { NSLog("Pickverter · %@", message()) }
    }
}
