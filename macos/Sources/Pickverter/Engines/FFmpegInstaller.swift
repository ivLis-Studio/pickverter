import AppKit
import Foundation

/// Downloads ffmpeg and ffprobe into Application Support, so every video and audio format works without Homebrew.
///
/// The builds come from Martin Riedl's build server: static, signed with his Developer ID, and published with a
/// SHA-256 next to every zip. A download is used only when the zip matches that checksum and both binaries carry
/// the builder's signature (Apple-issued Developer ID, team `KU3N25YGLU`).
@MainActor
final class FFmpegInstaller: ObservableObject {
    static let shared = FFmpegInstaller()

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

    nonisolated static let binaries = ["ffmpeg", "ffprobe"]
    nonisolated static let signingTeam = "KU3N25YGLU"

    /// ~/Library/Application Support/Pickverter/ffmpeg (PICKVERTER_FFMPEG_DIR overrides it for tests).
    nonisolated static var directory: URL {
        if let custom = ProcessInfo.processInfo.environment["PICKVERTER_FFMPEG_DIR"], !custom.isEmpty {
            return URL(fileURLWithPath: custom, isDirectory: true)
        }
        let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent("Library/Application Support", isDirectory: true)
        return support.appendingPathComponent("Pickverter/ffmpeg", isDirectory: true)
    }

    nonisolated static var architecture: String {
        #if arch(arm64)
        return "arm64"
        #else
        return "amd64"
        #endif
    }

    /// Always names the newest release; the server redirects to the versioned file.
    nonisolated static func source(_ binary: String) -> URL {
        URL(string: "https://ffmpeg.martin-riedl.de/redirect/latest/macos/\(architecture)/release/\(binary).zip")!
    }

    nonisolated static var requirement: String {
        "anchor apple generic and certificate 1[field.1.2.840.113635.100.6.2.6] exists"
            + " and certificate leaf[field.1.2.840.113635.100.6.1.13] exists"
            + " and certificate leaf[subject.OU] = \"\(signingTeam)\""
    }

    /// The ffmpeg in use is the one Pickverter downloaded (not Homebrew's or a bundled copy).
    nonisolated static var isManaged: Bool {
        guard let path = Tools.ffmpeg else { return false }
        return path.hasPrefix(directory.path + "/")
    }

    nonisolated static var installedVersion: String? {
        guard let text = try? String(contentsOf: directory.appendingPathComponent("version.txt"), encoding: .utf8) else { return nil }
        let value = text.trimmingCharacters(in: .whitespacesAndNewlines)
        return value.isEmpty ? nil : value
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

    /// Status line for Settings and the welcome window while something is happening.
    var statusLine: String? {
        switch phase {
        case .downloading(let fraction): return L("ffmpeg.downloading", "\(Int((fraction * 100).rounded(.down)))")
        case .installing: return L("ffmpeg.installing")
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

    /// Deletes the downloaded copy (Settings → Remove). Homebrew's ffmpeg, if any, takes over again.
    func remove() {
        guard !isWorking else { return }
        try? FileManager.default.removeItem(at: Self.directory)
        Tools.refresh()
        phase = .idle
        objectWillChange.send()
    }

    private func report(_ fraction: Double) {
        guard isWorking else { return }
        // Whole percents only: cards and Settings redraw a hundred times, not once per network packet.
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
            Tools.refresh()
            phase = .idle
            Task.detached(priority: .utility) { _ = Tools.ffmpegEncoders() }
        } catch {
            if error is CancellationError || (error as? URLError)?.code == .cancelled || Task.isCancelled {
                phase = .idle
                throw CancellationError()
            }
            let message = Self.describe(error)
            Debug.log("ffmpeg install failed: \(error)")
            phase = .failed(message)
            throw PickError(message)
        }
    }

    nonisolated static func describe(_ error: Error) -> String {
        if let pick = error as? PickError { return pick.message }
        return L("ffmpeg.err.download", error.localizedDescription)
    }

    // MARK: Steps (off the main actor)

    /// Download → checksum → unpack → signature → move into place → run once.
    nonisolated static func download(progress: @escaping @Sendable (Double) -> Void,
                                     installing: @escaping @Sendable () -> Void) async throws {
        let fm = FileManager.default
        let folder = directory
        try fm.createDirectory(at: folder, withIntermediateDirectories: true)
        // A download interrupted by quitting leaves its staging folder behind; clear those first.
        for leftover in (try? fm.contentsOfDirectory(at: folder, includingPropertiesForKeys: nil)) ?? []
        where leftover.lastPathComponent.hasPrefix(".download-") {
            try? fm.removeItem(at: leftover)
        }
        let staging = folder.appendingPathComponent(".download-" + UUID().uuidString.prefix(8), isDirectory: true)
        try fm.createDirectory(at: staging, withIntermediateDirectories: true)
        defer { try? fm.removeItem(at: staging) }

        var version: String?
        var ready: [(name: String, file: URL)] = []
        let share = 1 / Double(binaries.count)
        for (index, name) in binaries.enumerated() {
            let base = Double(index) * share
            let resolved = try await resolve(source(name))
            let expected = try await checksum(for: resolved)
            let zip = staging.appendingPathComponent(name + ".zip")
            let task = UpdateDownload(destination: zip) { fraction in progress(base + fraction * share) }
            _ = try await task.start(resolved)
            try Task.checkCancellation()
            guard try UpdateSigning.sha256(of: zip) == expected else { throw PickError(L("update.err.checksum")) }
            let unpacked = staging.appendingPathComponent(name, isDirectory: true)
            try fm.createDirectory(at: unpacked, withIntermediateDirectories: true)
            try await Shell.run("/usr/bin/ditto", ["-x", "-k", zip.path, unpacked.path])
            guard let binary = find(name, in: unpacked) else { throw PickError(L("ffmpeg.err.signature")) }
            try fm.setAttributes([.posixPermissions: 0o755], ofItemAtPath: binary.path)
            removexattr(binary.path, "com.apple.quarantine", 0)
            do {
                try await Shell.run("/usr/bin/codesign", ["--verify", "--strict", "-R", "=" + requirement, binary.path])
            } catch {
                throw PickError(L("ffmpeg.err.signature"))
            }
            ready.append((name, binary))
            version = version ?? release(from: resolved)
        }
        installing()
        for entry in ready {
            let target = folder.appendingPathComponent(entry.name)
            try? fm.removeItem(at: target)
            try fm.moveItem(at: entry.file, to: target)
        }
        // Prove it starts on this Mac before any job depends on it.
        try await Shell.run(folder.appendingPathComponent("ffmpeg").path, ["-hide_banner", "-version"])
        try (version ?? "").write(to: folder.appendingPathComponent("version.txt"), atomically: true, encoding: .utf8)
    }

    /// Follows the "latest" redirect with a one-byte request and returns the versioned URL.
    nonisolated static func resolve(_ url: URL) async throws -> URL {
        var request = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 30)
        request.setValue("bytes=0-0", forHTTPHeaderField: "Range")
        request.setValue("Pickverter", forHTTPHeaderField: "User-Agent")
        let (_, response) = try await URLSession.shared.data(for: request)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard (200..<300).contains(status), let final = response.url else { throw PickError(L("update.err.server", "\(status)")) }
        return final
    }

    /// The "<sha256>  <name>" file the server publishes next to every download.
    nonisolated static func checksum(for url: URL) async throws -> String {
        guard let address = URL(string: url.absoluteString + ".sha256") else { throw PickError(L("update.err.checksum")) }
        var request = URLRequest(url: address, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 30)
        request.setValue("Pickverter", forHTTPHeaderField: "User-Agent")
        let (data, response) = try await URLSession.shared.data(for: request)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard status == 200 else { throw PickError(L("update.err.server", "\(status)")) }
        let digest = String(decoding: data, as: UTF8.self).split(whereSeparator: \.isWhitespace).first.map(String.init) ?? ""
        guard digest.count == 64, digest.allSatisfy(\.isHexDigit) else { throw PickError(L("update.err.checksum")) }
        return digest.lowercased()
    }

    /// ".../1789931890_9.0.2/ffmpeg.zip" → "9.0.2"
    nonisolated static func release(from url: URL) -> String? {
        let folder = url.deletingLastPathComponent().lastPathComponent
        guard let separator = folder.firstIndex(of: "_") else { return nil }
        let value = String(folder[folder.index(after: separator)...])
        return value.isEmpty ? nil : value
    }

    nonisolated static func find(_ name: String, in folder: URL) -> URL? {
        let direct = folder.appendingPathComponent(name)
        if FileManager.default.fileExists(atPath: direct.path) { return direct }
        let walker = FileManager.default.enumerator(at: folder, includingPropertiesForKeys: nil)
        while let url = walker?.nextObject() as? URL {
            if url.lastPathComponent == name, !url.path.contains("__MACOSX") { return url }
        }
        return nil
    }

    // MARK: Jobs

    /// The same option, but it first installs ffmpeg when it's missing (progress on the job's own card).
    nonisolated static func preparing(_ option: PickOption) -> PickOption {
        var copy = PickOption(id: option.id, title: option.title, detail: option.detail, symbol: option.symbol,
                              isFormat: option.isFormat, outputExt: option.outputExt, unavailable: nil,
                              producesFiles: option.producesFiles, interactive: option.interactive) { ctx in
            guard Tools.ffmpeg == nil else { return try await option.perform(ctx) }
            let download = ctx.portion(0, 0.5)
            try await FFmpegInstaller.shared.install { fraction in
                download.report(fraction, L("ffmpeg.downloading", "\(Int((fraction * 100).rounded(.down)))"))
            }
            ctx.report(0.5, "")
            return try await option.perform(ctx.portion(0.5, 1))
        }
        copy.pinnedFrom = option.pinnedFrom
        return copy
    }
}

