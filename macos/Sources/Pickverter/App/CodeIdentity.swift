import CoreServices
import Foundation
import os
import Security

/// Who signed a bundle, and what that means for privacy permissions.
///
/// macOS ties Accessibility, Finder control and file access to the code signature. Developer ID builds from one
/// team keep those grants across updates. An ad-hoc build is a new app to macOS every time, and so is a build from
/// another signer: the old grants still look "on" in System Settings but no longer apply, so Pickverter clears them
/// and asks again instead of failing quietly.
enum CodeIdentity {
    /// The privacy services Pickverter asks for (System Settings › Privacy & Security).
    static let privacyServices = ["Accessibility", "AppleEvents", "SystemPolicyAllFiles", "SystemPolicyDesktopFolder",
                                  "SystemPolicyDocumentsFolder", "SystemPolicyDownloadsFolder",
                                  "SystemPolicyRemovableVolumes", "SystemPolicyNetworkVolumes"]

    /// The Team ID a bundle is signed with; nil for ad-hoc and unsigned builds.
    static func team(of bundle: URL) -> String? {
        var code: SecStaticCode?
        guard SecStaticCodeCreateWithPath(bundle as CFURL, [], &code) == errSecSuccess, let code else { return nil }
        var info: CFDictionary?
        guard SecCodeCopySigningInformation(code, SecCSFlags(rawValue: kSecCSSigningInformation), &info) == errSecSuccess,
              let values = info as? [String: Any] else { return nil }
        return values[kSecCodeInfoTeamIdentifier as String] as? String
    }

    /// Whether grants made to `current` also apply to `next`: only between Developer ID builds of one team.
    static func keepsPermissions(from current: URL, to next: URL) -> Bool {
        guard let nextTeam = team(of: next) else { return false }
        return team(of: current) == nextTeam
    }

    enum Change: Equatable { case keep, record, clearGrants }

    /// What a launch does: nothing for ad-hoc builds or the signer that ran last time; a first launch records the
    /// signer; a returning user's grants from another signer (or from the ad-hoc builds up to 0.3.0) are cleared.
    static func change(current: String?, last: String?, returning: Bool) -> Change {
        guard let current, current != last else { return .keep }
        return returning ? .clearGrants : .record
    }

    /// Preferences only an earlier launch leaves behind.
    static let returningKeys = ["onboarded", "fileAccess.offered", "fileAccess.requested", "update.from", "update.lastCheck"]

    static func isReturning(_ stored: [String: Any]) -> Bool {
        returningKeys.contains { stored[$0] != nil }
    }

    private static let lastTeamKey = "signing.team"
    private static let log = Logger(subsystem: "com.pickverter.app", category: "permissions")

    /// Set when this launch cleared grants made to a differently signed copy; the welcome window explains why.
    @MainActor private(set) static var clearedStaleGrants = false

    /// Runs first at launch, before anything checks a permission. Pickverter up to 0.3.0 was ad-hoc signed and its
    /// updater cleared grants only when the new copy was ad-hoc too, so the first Developer ID launch does it here,
    /// once per change of signer.
    @MainActor static func clearGrantsAfterSignerChange() {
        let defaults = UserDefaults.standard
        let bundleID = Bundle.main.bundleIdentifier ?? "com.pickverter.app"
        let stored = defaults.persistentDomain(forName: bundleID) ?? [:]
        let current = team(of: Bundle.main.bundleURL)
        let decision = change(current: current, last: defaults.string(forKey: lastTeamKey), returning: isReturning(stored))
        guard decision != .keep, let current else { return }
        if decision == .clearGrants {
            log.notice("Clearing privacy grants made to a differently signed copy (now team \(current, privacy: .public))")
            // tccutil finds the bundle ID through Launch Services; make sure it knows this copy wherever it runs from.
            _ = LSRegisterURL(Bundle.main.bundleURL as CFURL, true)
            FileAccess.requestedFolders = []
            clearedStaleGrants = true
            // Launch Services skips apps run from a disk image or a translocated download, and tccutil then can't
            // find this one. Try again next launch (usually from Applications, where AppMover puts it).
            guard resetPrivacy(bundleID: bundleID) else { return }
        }
        defaults.set(current, forKey: lastTeamKey)
    }

    /// `tccutil reset` for each service, so macOS asks again instead of keeping a grant that no longer matches.
    /// False when tccutil couldn't find the app at all (exit 64), so nothing was reset.
    @discardableResult
    static func resetPrivacy(bundleID: String) -> Bool {
        var found = true
        for service in privacyServices {
            let process = Process()
            process.executableURL = URL(fileURLWithPath: "/usr/bin/tccutil")
            process.arguments = ["reset", service, bundleID]
            process.standardOutput = FileHandle.nullDevice
            process.standardError = FileHandle.nullDevice
            guard (try? process.run()) != nil else { continue }
            process.waitUntilExit()
            log.notice("tccutil reset \(service, privacy: .public): \(process.terminationStatus)")
            if process.terminationStatus == 64 { found = false }
        }
        return found
    }

    /// Snapshots of the welcome window right after the switch (`cli editor-snapshot onboarding-signed …`).
    @MainActor static func previewClearedGrants() { clearedStaleGrants = true }
}
