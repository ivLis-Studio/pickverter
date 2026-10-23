import AppKit
import CryptoKit

/// What a GitHub release publishes as `update.json` next to the app zip.
struct UpdateManifest: Codable, Equatable {
    var version: String
    var build: String?
    var published: String?
    var minimumSystemVersion: String?
    var url: String
    var size: Int64
    var sha256: String
    /// Ed25519 over `signedMessage`, base64.
    var signature: String
    var notes: String?
    var notesURL: String?

    static func signedMessage(version: String, sha256: String, size: Int64) -> Data {
        Data("pickverter-update:1\n\(version)\n\(sha256.lowercased())\n\(size)".utf8)
    }

    var message: Data { Self.signedMessage(version: version, sha256: sha256, size: size) }
}

enum Versions {
    /// Numeric dotted compare; a pre-release ("1.0.0-beta.2") sorts before its release.
    static func compare(_ a: String, _ b: String) -> ComparisonResult {
        func split(_ value: String) -> ([Int], String?) {
            let trimmed = value.hasPrefix("v") ? String(value.dropFirst()) : value
            let parts = trimmed.split(separator: "-", maxSplits: 1)
            let numbers = (parts.first ?? "").split(separator: ".").map { Int($0) ?? 0 }
            return (numbers, parts.count > 1 ? String(parts[1]) : nil)
        }
        let (na, pa) = split(a), (nb, pb) = split(b)
        for index in 0..<max(na.count, nb.count) {
            let x = index < na.count ? na[index] : 0, y = index < nb.count ? nb[index] : 0
            if x != y { return x < y ? .orderedAscending : .orderedDescending }
        }
        switch (pa, pb) {
        case (nil, nil): return .orderedSame
        case (nil, _): return .orderedDescending
        case (_, nil): return .orderedAscending
        case let (x?, y?): return x.compare(y, options: .numeric)
        }
    }
}

enum UpdateSigning {
    static func generateKey() -> (privateKey: String, publicKey: String) {
        let key = Curve25519.Signing.PrivateKey()
        return (key.rawRepresentation.base64EncodedString(), key.publicKey.rawRepresentation.base64EncodedString())
    }

    static func publicKey(forPrivate base64: String) throws -> String {
        guard let data = Data(base64Encoded: base64.trimmingCharacters(in: .whitespacesAndNewlines)) else { throw PickError("bad key") }
        return try Curve25519.Signing.PrivateKey(rawRepresentation: data).publicKey.rawRepresentation.base64EncodedString()
    }

    static func sign(_ message: Data, privateKey base64: String) throws -> String {
        guard let data = Data(base64Encoded: base64.trimmingCharacters(in: .whitespacesAndNewlines)) else { throw PickError("bad key") }
        return try Curve25519.Signing.PrivateKey(rawRepresentation: data).signature(for: message).base64EncodedString()
    }

    static func verify(_ manifest: UpdateManifest, publicKey: Data) -> Bool {
        guard let key = try? Curve25519.Signing.PublicKey(rawRepresentation: publicKey),
              let signature = Data(base64Encoded: manifest.signature) else { return false }
        return key.isValidSignature(signature, for: manifest.message)
    }

    static func sha256(of url: URL) throws -> String {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        var hasher = SHA256()
        while let chunk = try handle.read(upToCount: 4 << 20), !chunk.isEmpty { hasher.update(data: chunk) }
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }
}

/// URLSession download with progress, finishing exactly once.
final class UpdateDownload: NSObject, URLSessionDownloadDelegate, @unchecked Sendable {
    private let destination: URL
    private let progress: @Sendable (Double) -> Void
    private var continuation: CheckedContinuation<URL, Error>?
    private var session: URLSession?
    private let lock = NSLock()

    init(destination: URL, progress: @escaping @Sendable (Double) -> Void) {
        self.destination = destination
        self.progress = progress
    }

    func start(_ url: URL) async throws -> URL {
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<URL, Error>) in
                lock.withLock { self.continuation = continuation }
                let session = URLSession(configuration: .ephemeral, delegate: self, delegateQueue: nil)
                self.session = session
                var request = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 60)
                request.setValue("Pickverter-Updater", forHTTPHeaderField: "User-Agent")
                session.downloadTask(with: request).resume()
            }
        } onCancel: {
            self.session?.invalidateAndCancel()
            self.finish(.failure(CancellationError()))
        }
    }

    private func finish(_ result: Result<URL, Error>) {
        let pending: CheckedContinuation<URL, Error>? = lock.withLock {
            let current = continuation
            continuation = nil
            return current
        }
        pending?.resume(with: result)
        session?.finishTasksAndInvalidate()
    }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didFinishDownloadingTo location: URL) {
        if let http = downloadTask.response as? HTTPURLResponse, http.statusCode != 200 {
            finish(.failure(PickError(L("update.err.server", "\(http.statusCode)"))))
            return
        }
        do {
            try? FileManager.default.removeItem(at: destination)
            try FileManager.default.moveItem(at: location, to: destination)
            finish(.success(destination))
        } catch {
            finish(.failure(error))
        }
    }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didWriteData bytesWritten: Int64,
                    totalBytesWritten: Int64, totalBytesExpectedToWrite: Int64) {
        guard totalBytesExpectedToWrite > 0 else { return }
        progress(Double(totalBytesWritten) / Double(totalBytesExpectedToWrite))
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        if let error { finish(.failure(error)) }
    }
}

/// GitHub Releases updater: check -> download -> verify (Ed25519, size, SHA-256, bundle id, code signature)
/// -> swap the bundle after quitting -> relaunch.
@MainActor
final class Updater: ObservableObject {
    static let shared = Updater()

    enum Phase: Equatable {
        case idle, checking, upToDate, available, downloading(Double), ready, installing
        case failed(String)
    }

    @Published private(set) var phase: Phase = .idle
    @Published private(set) var latest: UpdateManifest?
    @Published private(set) var lastChecked: Date?
    /// Set after relaunching into a new version (shown once).
    @Published private(set) var updatedFrom: String?
    private(set) var stagedApp: URL?
    private var stagedVersion: String?
    private var timer: Timer?
    private var downloadTask: Task<Void, Never>?
    private var announced = Set<String>()
    private let defaults = UserDefaults.standard

    let currentVersion = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0"
    let currentBuild = Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "0"
    let repository = Bundle.main.object(forInfoDictionaryKey: "PVUpdateRepository") as? String ?? ""

    var publicKey: Data? {
        (Bundle.main.object(forInfoDictionaryKey: "PVUpdatePublicKey") as? String).flatMap { Data(base64Encoded: $0) }
    }

    /// Local builds (scripts/build-app.sh) don't update themselves; release builds carry PVReleaseBuild.
    var isReleaseBuild: Bool {
        (Bundle.main.object(forInfoDictionaryKey: "PVReleaseBuild") as? Bool) == true
            || ProcessInfo.processInfo.environment["PICKVERTER_UPDATE_TEST"] != nil
    }

    var isEnabled: Bool { isReleaseBuild && repository.contains("/") && publicKey?.count == 32 }

    var manifestURL: URL? {
        if let custom = ProcessInfo.processInfo.environment["PICKVERTER_UPDATE_URL"] { return URL(string: custom) }
        return URL(string: "https://github.com/\(repository)/releases/latest/download/update.json")
    }

    var releasesPage: URL? { repository.isEmpty ? nil : URL(string: "https://github.com/\(repository)/releases") }
    var repositoryPage: URL? { repository.isEmpty ? nil : URL(string: "https://github.com/\(repository)") }

    static var updatesFolder: URL {
        let caches = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask).first ?? FileManager.default.temporaryDirectory
        return caches.appendingPathComponent("com.pickverter.app/Updates", isDirectory: true)
    }

    var isBusy: Bool {
        switch phase {
        case .checking, .downloading, .installing: return true
        default: return false
        }
    }

    // MARK: Lifecycle

    func start() {
        lastChecked = defaults.object(forKey: "update.lastCheck") as? Date
        if let from = defaults.string(forKey: "update.from") {
            defaults.removeObject(forKey: "update.from")
            if from != currentVersion { updatedFrom = from }
        }
        // Leftovers: the previous bundle kept for rollback and any unfinished download.
        try? FileManager.default.removeItem(at: Self.updatesFolder)
        guard isEnabled else { return }
        timer = Timer.scheduledTimer(withTimeInterval: 3600, repeats: true) { _ in
            Task { @MainActor in Updater.shared.checkIfDue() }
        }
        Task { @MainActor in
            try? await Task.sleep(nanoseconds: 20_000_000_000)
            Updater.shared.checkIfDue()
        }
    }

    func checkIfDue() {
        guard isEnabled, Preferences.shared.autoCheckUpdates, !isBusy, phase != .ready else { return }
        if let lastChecked, Date().timeIntervalSince(lastChecked) < 20 * 3600 { return }
        Task { await check(userInitiated: false) }
    }

    // MARK: Checking

    func check(userInitiated: Bool) async {
        guard isEnabled, let url = manifestURL else {
            if userInitiated { phase = .failed(L("update.disabled")) }
            return
        }
        guard !isBusy else { return }
        phase = .checking
        do {
            var request = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 20)
            request.setValue("Pickverter/\(currentVersion)", forHTTPHeaderField: "User-Agent")
            let (data, response) = try await URLSession.shared.data(for: request)
            let status = (response as? HTTPURLResponse)?.statusCode ?? 0
            lastChecked = Date()
            defaults.set(lastChecked, forKey: "update.lastCheck")
            if status == 404 {
                latest = nil
                phase = .upToDate
                return
            }
            guard status == 200 else { throw PickError(L("update.err.server", "\(status)")) }
            let manifest = try JSONDecoder().decode(UpdateManifest.self, from: data)
            guard let key = publicKey, UpdateSigning.verify(manifest, publicKey: key) else {
                throw PickError(L("update.err.signature"))
            }
            guard Versions.compare(manifest.version, currentVersion) == .orderedDescending, systemSupports(manifest) else {
                latest = nil
                phase = .upToDate
                return
            }
            latest = manifest
            phase = stagedVersion == manifest.version && stagedApp != nil ? .ready : .available
            guard !userInitiated, phase == .available, defaults.string(forKey: "update.skipped") != manifest.version else { return }
            if Preferences.shared.autoInstallUpdates {
                download()
            } else {
                announceAvailable(manifest)
            }
        } catch {
            phase = userInitiated ? .failed(error.localizedDescription) : .idle
            Debug.log("update check failed: \(error)")
        }
    }

    private func systemSupports(_ manifest: UpdateManifest) -> Bool {
        guard let minimum = manifest.minimumSystemVersion else { return true }
        let parts = minimum.split(separator: ".").map { Int($0) ?? 0 }
        let version = OperatingSystemVersion(majorVersion: parts.first ?? 0, minorVersion: parts.count > 1 ? parts[1] : 0,
                                             patchVersion: parts.count > 2 ? parts[2] : 0)
        return ProcessInfo.processInfo.isOperatingSystemAtLeast(version)
    }

    func skip() {
        guard let latest else { return }
        defaults.set(latest.version, forKey: "update.skipped")
        phase = .idle
    }

    // MARK: Downloading

    func download() {
        guard let manifest = latest, !isBusy || phase == .available else { return }
        if phase == .ready { return }
        downloadTask = Task { @MainActor in
            do {
                let app = try await stage(manifest)
                stagedApp = app
                stagedVersion = manifest.version
                phase = .ready
                announceReady(manifest)
            } catch is CancellationError {
                phase = .available
            } catch {
                phase = .failed(error.localizedDescription)
            }
        }
    }

    func cancelDownload() {
        downloadTask?.cancel()
        downloadTask = nil
    }

    /// Downloads, verifies and unpacks a release. Returns the ready-to-install app bundle.
    func stage(_ manifest: UpdateManifest) async throws -> URL {
        guard let url = URL(string: manifest.url), url.scheme == "https" else { throw PickError(L("update.err.bundle")) }
        phase = .downloading(0)
        let fm = FileManager.default
        let folder = Self.updatesFolder.appendingPathComponent(manifest.version, isDirectory: true)
        try? fm.removeItem(at: folder)
        try fm.createDirectory(at: folder, withIntermediateDirectories: true)
        let zip = folder.appendingPathComponent("Pickverter-\(manifest.version).zip")
        let loader = UpdateDownload(destination: zip) { fraction in
            Task { @MainActor in
                if case .downloading = Updater.shared.phase { Updater.shared.phase = .downloading(fraction) }
            }
        }
        _ = try await loader.start(url)
        guard FileNames.size(of: zip) == manifest.size else { throw PickError(L("update.err.checksum")) }
        let digest = try await Task.detached(priority: .userInitiated) { try UpdateSigning.sha256(of: zip) }.value
        guard digest == manifest.sha256.lowercased() else { throw PickError(L("update.err.checksum")) }
        let unpacked = folder.appendingPathComponent("unpacked", isDirectory: true)
        try await Shell.run("/usr/bin/ditto", ["-x", "-k", zip.path, unpacked.path])
        guard let app = try fm.contentsOfDirectory(at: unpacked, includingPropertiesForKeys: nil).first(where: { $0.pathExtension == "app" }),
              let info = NSDictionary(contentsOf: app.appendingPathComponent("Contents/Info.plist")),
              info["CFBundleIdentifier"] as? String == Bundle.main.bundleIdentifier,
              info["CFBundleShortVersionString"] as? String == manifest.version
        else { throw PickError(L("update.err.bundle")) }
        do {
            try await Shell.run("/usr/bin/codesign", ["--verify", "--deep", "--strict", app.path])
        } catch {
            throw PickError(L("update.err.codesign"))
        }
        _ = try? await Shell.run("/usr/bin/xattr", ["-dr", "com.apple.quarantine", app.path])
        try? fm.removeItem(at: zip)
        return app
    }

    // MARK: Installing

    /// Swaps the bundle once the app has quit. With RESET=1 it first clears the grants macOS keeps for the old
    /// signature (see CodeIdentity), so the new copy asks again instead of failing quietly.
    static let installScript = """
    #!/bin/sh
    # Pickverter updater: wait for the app to quit, swap the bundle, relaunch.
    PID="$1"; NEW="$2"; TARGET="$3"; BACKUP="$4"; RESET="$5"; BUNDLE_ID="$6"; OPEN="$7"
    i=0
    while kill -0 "$PID" 2>/dev/null && [ $i -lt 150 ]; do sleep 0.1; i=$((i + 1)); done
    /bin/rm -rf "$BACKUP"
    if /bin/mv "$TARGET" "$BACKUP"; then
      if /bin/mv "$NEW" "$TARGET"; then
        if [ "$RESET" = "1" ]; then
          for SERVICE in \(CodeIdentity.privacyServices.joined(separator: " ")); do
            /usr/bin/tccutil reset "$SERVICE" "$BUNDLE_ID" >/dev/null 2>&1
          done
        fi
        [ "$OPEN" = "open" ] && /usr/bin/open "$TARGET"
        exit 0
      fi
      /bin/mv "$BACKUP" "$TARGET"
    fi
    [ "$OPEN" = "open" ] && /usr/bin/open "$TARGET"
    exit 1
    """

    /// Can this copy replace itself? Not when translocated (run from Downloads) or in a folder we can't write.
    var canInstallInPlace: Bool {
        let target = Bundle.main.bundleURL
        return !target.path.contains("/AppTranslocation/")
            && FileManager.default.isWritableFile(atPath: target.deletingLastPathComponent().path)
    }

    @discardableResult
    func launchInstaller(staged: URL, target: URL, pid: Int32, relaunch: Bool, resetPermissions: Bool? = nil) throws -> Process {
        let fm = FileManager.default
        try fm.createDirectory(at: Self.updatesFolder, withIntermediateDirectories: true)
        let script = Self.updatesFolder.appendingPathComponent("install.sh")
        try Self.installScript.write(to: script, atomically: true, encoding: .utf8)
        try fm.setAttributes([.posixPermissions: 0o755], ofItemAtPath: script.path)
        let backup = Self.updatesFolder.appendingPathComponent("previous.app")
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/sh")
        let reset = resetPermissions ?? !CodeIdentity.keepsPermissions(from: target, to: staged)
        process.arguments = [script.path, "\(pid)", staged.path, target.path, backup.path, reset ? "1" : "0",
                             Bundle.main.bundleIdentifier ?? "com.pickverter.app", relaunch ? "open" : "no"]
        try process.run()
        return process
    }

    func installAndRelaunch() {
        guard let staged = stagedApp else {
            download()
            return
        }
        guard canInstallInPlace else {
            NSWorkspace.shared.activateFileViewerSelecting([staged])
            phase = .failed(L("update.err.location"))
            return
        }
        phase = .installing
        do {
            // Developer ID builds of one team keep their grants; anything else starts over.
            let reset = !CodeIdentity.keepsPermissions(from: Bundle.main.bundleURL, to: staged)
            try launchInstaller(staged: staged, target: Bundle.main.bundleURL, pid: ProcessInfo.processInfo.processIdentifier,
                                relaunch: true, resetPermissions: reset)
            // Cleared folder grants must not be probed (and prompted for) the moment the new version starts.
            if reset { FileAccess.requestedFolders = [] }
            defaults.set(currentVersion, forKey: "update.from")
            NSApp.terminate(nil)
        } catch {
            phase = .failed(error.localizedDescription)
        }
    }

    // MARK: Telling the user

    private func announceAvailable(_ manifest: UpdateManifest) {
        guard announced.insert("available-" + manifest.version).inserted else { return }
        JobCenter.shared.notice(L("update.card.available", manifest.version), detail: L("update.card.available.d"),
                                symbol: "arrow.down.circle", tone: .info,
                                action: Job.NoticeAction(title: L("update.card.details"), symbol: "info.circle") {
                                    WindowManager.shared.showUpdates()
                                }, lingers: 12)
    }

    private func announceReady(_ manifest: UpdateManifest) {
        guard announced.insert("ready-" + manifest.version).inserted else { return }
        JobCenter.shared.notice(L("update.card.ready", manifest.version), detail: L("update.card.ready.d"),
                                symbol: "arrow.down.circle", tone: .info,
                                action: Job.NoticeAction(title: L("update.restart"), symbol: "arrow.clockwise") {
                                    Updater.shared.installAndRelaunch()
                                }, lingers: 20)
    }

    func acknowledgeUpdate() { updatedFrom = nil }
}
