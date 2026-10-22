import AppKit

/// Offers to move a downloaded copy into Applications. Self-updates need a writable, stable location
/// (a copy launched from Downloads runs from a randomized read-only path).
@MainActor
enum AppMover {
    static var isInApplications: Bool {
        let path = Bundle.main.bundleURL.resolvingSymlinksInPath().path
        return path.hasPrefix("/Applications/") || path.hasPrefix(NSHomeDirectory() + "/Applications/")
    }

    static func offerIfNeeded() {
        guard Updater.shared.isReleaseBuild, !isInApplications,
              !UserDefaults.standard.bool(forKey: "move.declined"),
              ProcessInfo.processInfo.environment["PICKVERTER_UPDATE_TEST"] == nil else { return }
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = L("move.title")
        alert.informativeText = L("move.info")
        alert.addButton(withTitle: L("move.move"))
        alert.addButton(withTitle: L("move.keep"))
        alert.showsSuppressionButton = true
        alert.suppressionButton?.title = L("move.dont_ask")
        let response = alert.runModal()
        if alert.suppressionButton?.state == .on { UserDefaults.standard.set(true, forKey: "move.declined") }
        guard response == .alertFirstButtonReturn else { return }
        do {
            try move()
        } catch {
            let failure = NSAlert()
            failure.messageText = L("move.failed")
            failure.informativeText = error.localizedDescription
            failure.runModal()
        }
    }

    static func move() throws {
        let fm = FileManager.default
        let source = Bundle.main.bundleURL
        let system = URL(fileURLWithPath: "/Applications", isDirectory: true)
        let folder = fm.isWritableFile(atPath: system.path)
            ? system : URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent("Applications", isDirectory: true)
        try fm.createDirectory(at: folder, withIntermediateDirectories: true)
        let destination = folder.appendingPathComponent(source.lastPathComponent)
        if fm.fileExists(atPath: destination.path) {
            try fm.trashItem(at: destination, resultingItemURL: nil)
        }
        try run("/usr/bin/ditto", [source.path, destination.path])
        try? run("/usr/bin/xattr", ["-dr", "com.apple.quarantine", destination.path])
        // A translocated copy is read-only; the original download stays where the user put it.
        if !source.path.contains("/AppTranslocation/") { try? fm.trashItem(at: source, resultingItemURL: nil) }
        let relaunch = Process()
        relaunch.executableURL = URL(fileURLWithPath: "/bin/sh")
        relaunch.arguments = ["-c", "sleep 0.8; /usr/bin/open \"$0\"", destination.path]
        try relaunch.run()
        NSApp.terminate(nil)
    }

    private static func run(_ tool: String, _ arguments: [String]) throws {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: tool)
        process.arguments = arguments
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        try process.run()
        process.waitUntilExit()
        guard process.terminationStatus == 0 else { throw PickError(L("err.tool_failed", tool, "\(process.terminationStatus)")) }
    }
}
