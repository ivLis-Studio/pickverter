import Foundation

/// The hub's size preview: "Compress · Balanced  ≈ 420 KB · −71%" before anything is written next to the file. The
/// highlighted option runs for real on a private copy of the result (no card, no queue, no questions), its size is
/// read and the copy deleted at once.
@MainActor
final class SizeEstimator: ObservableObject {
    enum State: Equatable {
        case none
        case working
        case result(String)
        case small
    }

    @Published private(set) var state: State = .none

    static let imageLimit: Int64 = 80_000_000
    static let pdfLimit: Int64 = 30_000_000
    static let imageFormats: Set<String> = ["jpg", "png", "heic", "webp", "avif", "tiff", "gif", "bmp", "ico"]

    private var task: Task<Void, Never>?
    /// The estimate being worked out right now (it may outlive the highlight that asked for it).
    private var measuring: Task<State, Never>?
    private var key: String?
    private var cache: [String: State] = [:]
    private var order: [String] = []

    /// Image→image conversions, image Compress (every non-interactive option) and Resize, PDF Compress; one file.
    static func applies(_ option: PickOption, parent: PickOption?, files: [URL]) -> Bool {
        guard files.count == 1, let file = files.first, !option.interactive,
              option.unavailable == nil, parent?.unavailable == nil else { return false }
        let id = option.id
        switch FileKind.of(file) {
        case .image:
            guard FileNames.size(of: file) <= imageLimit else { return false }
            if id.hasPrefix("convert.") { return (parent ?? option).outputExt.map(imageFormats.contains) ?? false }
            return id.hasPrefix("tool.compress.") || id.hasPrefix("tool.resize.")
        case .pdf:
            return id == "tool.compress" && FileNames.size(of: file) <= pdfLimit
        default:
            return false
        }
    }

    /// Follows the highlight: starts about 250 ms after it settles, cancels when it moves on.
    func follow(_ option: PickOption?, parent: PickOption?, files: [URL]) {
        guard let option, files.count == 1, let file = files.first else {
            cancel()
            return
        }
        // "Under 10 MB" on a file that already is (videos too): nothing to work out.
        let under = TargetSize.target(of: option.id).map { FileNames.size(of: file) <= $0 } ?? false
        guard under || Self.applies(option, parent: parent, files: files) else {
            cancel()
            return
        }
        let values = try? file.resourceValues(forKeys: [.fileSizeKey, .contentModificationDateKey])
        let key = "\(file.path)|\(values?.fileSize ?? 0)|\(values?.contentModificationDate?.timeIntervalSince1970 ?? 0)|\(option.id)"
        guard key != self.key else { return }
        cancel()
        self.key = key
        if let known = cache[key] {
            state = known
            return
        }
        if under {
            remember(key, .small)
            state = .small
            return
        }
        task = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 250_000_000)
            guard !Task.isCancelled, let self else { return }
            // One at a time and below the interface's priority: sweeping across the ring shouldn't decode the same
            // big picture five times at once.
            _ = await self.measuring?.value
            guard !Task.isCancelled, self.key == key else { return }
            self.state = .working
            let job = Task.detached(priority: .utility) { await Self.measure(option, file: file) }
            self.measuring = job
            let result = await withTaskCancellationHandler { await job.value } onCancel: { job.cancel() }
            guard !Task.isCancelled, self.key == key else { return }
            self.remember(key, result)
            self.state = result
        }
    }

    func cancel() {
        task?.cancel()
        task = nil
        key = nil
        if state != .none { state = .none }
    }

    /// Snapshots and tests: a fixed line.
    func show(_ state: State) {
        cancel()
        self.state = state
    }

    private func remember(_ key: String, _ state: State) {
        if cache[key] == nil { order.append(key) }
        cache[key] = state
        while order.count > 64 { cache[order.removeFirst()] = nil }
    }

    /// Runs the option into a private folder and reports the result's size against the original.
    nonisolated static func measure(_ option: PickOption, file: URL) async -> State {
        let job = UUID()
        let folder = ClipboardStore.folder(for: job)
        defer { try? FileManager.default.removeItem(at: folder) }
        let context = PickContext(files: [file], location: .sameFolder, destination: .clipboard, jobID: job) { _, _ in }
        do {
            let result = try await WorkQueue.$isHoldingSlot.withValue(true) { try await option.perform(context) }
            let size = result.outputs.reduce(Int64(0)) { $0 + FileNames.size(of: $1) }
            guard size > 0 else { return .none }
            return .result(L("est.result", FileNames.formatBytes(size), change(from: FileNames.size(of: file), to: size)))
        } catch let error as PickError where error.message == L("err.already_small") {
            return .small
        } catch {
            return .none
        }
    }

    /// "−71%" smaller, "+12%" larger (U+2212 is the minus sign).
    nonisolated static func change(from original: Int64, to size: Int64) -> String {
        guard original > 0 else { return "" }
        let percent = Int((Double(size - original) / Double(original) * 100).rounded())
        if percent < 0 { return "\u{2212}\(-percent)%" }
        return percent > 0 ? "+\(percent)%" : "0%"
    }
}
