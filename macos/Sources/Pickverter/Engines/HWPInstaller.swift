import AppKit
import Foundation

/// Downloads rhwp, the open-source HWP engine (github.com/edwardkim/rhwp, MIT), into Application Support the first
/// time an HWP or HWPX file is converted.
///
/// The release is pinned: Pickverter knows the version and the SHA-256 of each archive, so a download is used only when
/// it is exactly the file this build was tested with. A newer Pickverter can move the pin; the old copy is replaced on
/// its next use.
@MainActor
final class HWPInstaller: ObservableObject {
    static let shared = HWPInstaller()

    enum Phase: Equatable {
        case idle
        case downloading(Double)
        case installing
        case failed(String)
    }

    @Published private(set) var phase: Phase = .idle
    private var current: Task<Void, Error>?
    private var listeners: [UUID: (Double) -> Void] = [:]
    private var lastPercent = -1

    nonisolated static let version = "0.8.7"

    /// The release archive for this Mac and its SHA-256 (from the release's SHA256SUMS.txt).
    nonisolated static var archive: (name: String, sha256: String) {
        #if arch(arm64)
        return ("rhwp-v0.8.7-macos-aarch64.tar.gz", "6ed46aa63f6d10c447842999f73abc59cf9586c5645fefc5e763bd6a6d43662d")
        #else
        return ("rhwp-v0.8.7-macos-x86_64.tar.gz", "07b1a5ea570e410d1b52b4d848a94f00c64674e84004883c62efad7daab61da4")
        #endif
    }

    nonisolated static var source: URL {
        URL(string: "https://github.com/edwardkim/rhwp/releases/download/v\(version)/\(archive.name)")!
    }

    /// ~/Library/Application Support/Pickverter/rhwp (PICKVERTER_RHWP_DIR overrides it for tests).
    nonisolated static var directory: URL {
        if let custom = ProcessInfo.processInfo.environment["PICKVERTER_RHWP_DIR"], !custom.isEmpty {
            return URL(fileURLWithPath: custom, isDirectory: true)
        }
        let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent("Library/Application Support", isDirectory: true)
        return support.appendingPathComponent("Pickverter/rhwp", isDirectory: true)
    }

    nonisolated static var installedVersion: String? {
        guard let text = try? String(contentsOf: directory.appendingPathComponent("version.txt"), encoding: .utf8) else { return nil }
        let value = text.trimmingCharacters(in: .whitespacesAndNewlines)
        return value.isEmpty ? nil : value
    }

    /// The rhwp to run: the pinned version Pickverter downloaded (PICKVERTER_RHWP names another binary, for tests).
    nonisolated static var executable: String? {
        let fm = FileManager.default
        if let custom = ProcessInfo.processInfo.environment["PICKVERTER_RHWP"], !custom.isEmpty {
            return fm.isExecutableFile(atPath: custom) ? custom : nil
        }
        let binary = directory.appendingPathComponent("rhwp").path
        guard installedVersion == version, fm.isExecutableFile(atPath: binary) else { return nil }
        return binary
    }

    var isWorking: Bool {
        switch phase {
        case .downloading, .installing: return true
        case .idle, .failed: return false
        }
    }

    var failure: String? {
        if case .failed(let message) = phase { return message }
        return nil
    }

    /// Status line for Settings while something is happening.
    var statusLine: String? {
        switch phase {
        case .downloading(let fraction): return L("hwp.downloading", "\(Int((fraction * 100).rounded(.down)))")
        case .installing: return L("hwp.installing")
        case .failed(let message): return message
        case .idle: return nil
        }
    }

    /// Downloads and installs, or joins the install already running. `progress` gets 0…1.
    func install(progress: ((Double) -> Void)? = nil) async throws {
        let id = UUID()
        if let progress { listeners[id] = progress }
        defer { listeners[id] = nil }
        let task: Task<Void, Error>
        if let current {
            task = current
        } else {
            task = Task { @MainActor in try await self.run() }
            current = task
        }
        try await withTaskCancellationHandler {
            try await task.value
        } onCancel: {
            task.cancel()
        }
    }

    /// For buttons: starts an install and reports through `phase`.
    func start() {
        guard !isWorking else { return }
        Task { try? await install() }
    }

    func cancel() { current?.cancel() }

    /// Snapshots only: show a state without downloading anything.
    func preview(_ phase: Phase) { self.phase = phase }

    /// Deletes the downloaded copy (Settings → Remove); the next HWP conversion downloads it again.
    func remove() {
        guard !isWorking else { return }
        try? FileManager.default.removeItem(at: Self.directory)
        phase = .idle
        objectWillChange.send()
    }

    private func report(_ fraction: Double) {
        guard isWorking else { return }
        let value = min(max(fraction, 0), 1)
        let percent = Int((value * 100).rounded(.down))
        guard percent != lastPercent else { return }
        lastPercent = percent
        phase = .downloading(value)
        listeners.values.forEach { $0(value) }
    }

    private func run() async throws {
        defer { current = nil }
        lastPercent = -1
        phase = .downloading(0)
        do {
            try await Self.download { [weak self] fraction in
                Task { @MainActor in self?.report(fraction) }
            } installing: { [weak self] in
                Task { @MainActor in self?.phase = .installing }
            }
            phase = .idle
            objectWillChange.send()
        } catch {
            if error is CancellationError || (error as? URLError)?.code == .cancelled || Task.isCancelled {
                phase = .idle
                throw CancellationError()
            }
            let message = (error as? PickError)?.message ?? L("hwp.err.download", error.localizedDescription)
            Debug.log("rhwp install failed: \(error)")
            phase = .failed(message)
            throw PickError(message)
        }
    }

    // MARK: Steps (off the main actor)

    /// Download → pinned SHA-256 → unpack → move into place → run once.
    nonisolated static func download(progress: @escaping @Sendable (Double) -> Void,
                                     installing: @escaping @Sendable () -> Void) async throws {
        let fm = FileManager.default
        let folder = directory
        try fm.createDirectory(at: folder, withIntermediateDirectories: true)
        for leftover in (try? fm.contentsOfDirectory(at: folder, includingPropertiesForKeys: nil)) ?? []
        where leftover.lastPathComponent.hasPrefix(".download-") {
            try? fm.removeItem(at: leftover)
        }
        let staging = folder.appendingPathComponent(".download-" + UUID().uuidString.prefix(8), isDirectory: true)
        try fm.createDirectory(at: staging, withIntermediateDirectories: true)
        defer { try? fm.removeItem(at: staging) }

        let tarball = staging.appendingPathComponent(archive.name)
        _ = try await UpdateDownload(destination: tarball, progress: progress).start(source)
        try Task.checkCancellation()
        guard try UpdateSigning.sha256(of: tarball) == archive.sha256 else { throw PickError(L("update.err.checksum")) }
        installing()
        let unpacked = staging.appendingPathComponent("unpacked", isDirectory: true)
        try fm.createDirectory(at: unpacked, withIntermediateDirectories: true)
        try await Shell.run("/usr/bin/tar", ["-xzf", tarball.path, "-C", unpacked.path])
        guard let binary = file(named: "rhwp", in: unpacked) else { throw PickError(L("update.err.checksum")) }
        try fm.setAttributes([.posixPermissions: 0o755], ofItemAtPath: binary.path)
        removexattr(binary.path, "com.apple.quarantine", 0)
        try? fm.removeItem(at: folder.appendingPathComponent("version.txt"))
        for name in ["rhwp", "LICENSE"] {
            guard let file = name == "rhwp" ? binary : file(named: name, in: unpacked) else { continue }
            let target = folder.appendingPathComponent(name)
            try? fm.removeItem(at: target)
            try fm.moveItem(at: file, to: target)
        }
        // Prove it starts on this Mac before any job depends on it.
        let banner = try await Shell.run(folder.appendingPathComponent("rhwp").path, ["--version"])
        guard banner.contains(version) else { throw PickError(L("hwp.err.download", banner.trimmingCharacters(in: .whitespacesAndNewlines))) }
        try version.write(to: folder.appendingPathComponent("version.txt"), atomically: true, encoding: .utf8)
    }

    /// A regular file with that name anywhere in the unpacked archive (the release keeps it in an "rhwp" folder).
    nonisolated static func file(named name: String, in folder: URL) -> URL? {
        let walker = FileManager.default.enumerator(at: folder, includingPropertiesForKeys: [.isRegularFileKey])
        while let url = walker?.nextObject() as? URL {
            guard url.lastPathComponent == name, (try? url.resourceValues(forKeys: [.isRegularFileKey]).isRegularFile) == true else { continue }
            return url
        }
        return nil
    }

    // MARK: Jobs

    /// The same option, but it first downloads rhwp when it's missing (progress on the job's own card).
    nonisolated static func preparing(_ option: PickOption) -> PickOption {
        var copy = PickOption(id: option.id, title: option.title, detail: option.detail, symbol: option.symbol,
                              isFormat: option.isFormat, outputExt: option.outputExt, unavailable: nil,
                              producesFiles: option.producesFiles, interactive: option.interactive) { ctx in
            guard executable == nil else { return try await option.perform(ctx) }
            let download = ctx.portion(0, 0.4)
            try await HWPInstaller.shared.install { fraction in
                download.report(fraction, L("hwp.downloading", "\(Int((fraction * 100).rounded(.down)))"))
            }
            ctx.report(0.4, "")
            return try await option.perform(ctx.portion(0.4, 1))
        }
        copy.pinnedFrom = option.pinnedFrom
        return copy
    }
}

