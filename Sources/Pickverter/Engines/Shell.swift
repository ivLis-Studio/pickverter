import Foundation

/// Locates optional command-line encoders. Bundled copies in Resources/bin win over Homebrew.
enum Tools {
    private static let lock = NSLock()
    private static var cache: [String: String?] = [:]
    private static var encoderCache: Set<String>?

    static let searchDirectories = ["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin", "/usr/sbin"]
    static var ffmpegHint: String { L("err.ffmpeg") }

    static func path(_ name: String) -> String? {
        lock.lock()
        defer { lock.unlock() }
        if let hit = cache[name] { return hit }
        var found: String?
        let fm = FileManager.default
        if let bundled = Bundle.main.resourceURL?.appendingPathComponent("bin/" + name).path,
           fm.isExecutableFile(atPath: bundled) {
            found = bundled
        } else {
            found = searchDirectories.map { $0 + "/" + name }.first { fm.isExecutableFile(atPath: $0) }
        }
        cache[name] = .some(found)
        return found
    }

    static func refresh() {
        lock.lock()
        cache.removeAll()
        encoderCache = nil
        lock.unlock()
    }

    static var ffmpeg: String? { path("ffmpeg") }
    static var ffprobe: String? { path("ffprobe") }
    static var magick: String? { path("magick") }
    static var cwebp: String? { path("cwebp") }

    static func ffmpegEncoders() -> Set<String> {
        lock.lock()
        if let cached = encoderCache { lock.unlock(); return cached }
        lock.unlock()
        guard let ffmpeg else { return [] }
        let process = Process()
        process.executableURL = URL(fileURLWithPath: ffmpeg)
        process.arguments = ["-hide_banner", "-encoders"]
        let pipe = Pipe()
        process.standardOutput = pipe
        process.standardError = FileHandle.nullDevice
        do { try process.run() } catch { return [] }
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        var set = Set<String>()
        for line in String(decoding: data, as: UTF8.self).split(separator: "\n") {
            let parts = line.split(separator: " ", omittingEmptySubsequences: true)
            if parts.count >= 2, parts[0].count == 6 { set.insert(String(parts[1])) }
        }
        lock.lock()
        encoderCache = set
        lock.unlock()
        return set
    }

    static func hasEncoder(_ name: String) -> Bool { ffmpegEncoders().contains(name) }
}

enum Shell {
    struct Failure: LocalizedError {
        let tool: String
        let status: Int32
        let stderr: String

        var errorDescription: String? {
            let tail = stderr.split(separator: "\n").suffix(2).joined(separator: " ")
                .trimmingCharacters(in: .whitespacesAndNewlines)
            return tail.isEmpty ? L("err.tool_failed", tool, "\(status)") : "\(tool): \(tail.prefix(220))"
        }
    }

    private final class Collector: @unchecked Sendable {
        private let lock = NSLock()
        private var out = Data()
        private var err = Data()
        private var pending = Data()
        private let onLine: ((String) -> Void)?

        init(onLine: ((String) -> Void)?) { self.onLine = onLine }

        func appendOut(_ data: Data) {
            var lines: [String] = []
            lock.lock()
            if out.count < 4_000_000 { out.append(data) }
            if onLine != nil {
                pending.append(data)
                while let newline = pending.firstIndex(of: 0x0A) {
                    let lineData = pending[pending.startIndex..<newline]
                    lines.append(String(decoding: lineData, as: UTF8.self))
                    pending.removeSubrange(pending.startIndex...newline)
                }
            }
            lock.unlock()
            lines.forEach { onLine?($0) }
        }

        func appendErr(_ data: Data) {
            lock.lock()
            err.append(data)
            if err.count > 16_000 { err = err.suffix(8_000) }
            lock.unlock()
        }

        var outString: String { lock.lock(); defer { lock.unlock() }; return String(decoding: out, as: UTF8.self) }
        var errString: String { lock.lock(); defer { lock.unlock() }; return String(decoding: err, as: UTF8.self) }
    }

    /// Runs a process without a shell (no quoting pitfalls). Cancelling the task terminates the process.
    @discardableResult
    static func run(_ executable: String, _ arguments: [String], cwd: URL? = nil, stdoutTo file: URL? = nil,
                    onLine: ((String) -> Void)? = nil) async throws -> String {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: executable)
        process.arguments = arguments
        if let cwd { process.currentDirectoryURL = cwd }
        process.standardInput = FileHandle.nullDevice
        let collector = Collector(onLine: onLine)
        let toolName = (executable as NSString).lastPathComponent

        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<String, Error>) in
                let group = DispatchGroup()
                let errPipe = Pipe()
                process.standardError = errPipe
                group.enter()
                errPipe.fileHandleForReading.readabilityHandler = { handle in
                    let data = handle.availableData
                    if data.isEmpty { handle.readabilityHandler = nil; group.leave() } else { collector.appendErr(data) }
                }
                var outHandle: FileHandle?
                if let file {
                    FileManager.default.createFile(atPath: file.path, contents: nil)
                    outHandle = try? FileHandle(forWritingTo: file)
                    process.standardOutput = outHandle ?? FileHandle.nullDevice
                } else {
                    let outPipe = Pipe()
                    process.standardOutput = outPipe
                    group.enter()
                    outPipe.fileHandleForReading.readabilityHandler = { handle in
                        let data = handle.availableData
                        if data.isEmpty { handle.readabilityHandler = nil; group.leave() } else { collector.appendOut(data) }
                    }
                }
                group.enter()
                process.terminationHandler = { _ in group.leave() }
                do {
                    try process.run()
                } catch {
                    errPipe.fileHandleForReading.readabilityHandler = nil
                    continuation.resume(throwing: PickError(L("err.run_tool", toolName) + " · " + error.localizedDescription))
                    return
                }
                group.notify(queue: .global()) {
                    try? outHandle?.close()
                    if process.terminationStatus == 0 {
                        continuation.resume(returning: collector.outString)
                    } else if process.terminationReason == .uncaughtSignal {
                        continuation.resume(throwing: CancellationError())
                    } else {
                        continuation.resume(throwing: Failure(tool: toolName, status: process.terminationStatus,
                                                              stderr: collector.errString))
                    }
                }
            }
        } onCancel: {
            if process.isRunning { process.terminate() }
        }
    }
}
