import AppKit

/// Remove BG's cut-outs come from an AI model (Apple's Vision foreground mask, run on this Mac). Microsoft Store policy
/// 11.16 asks the Windows edition to say so where the result appears and to let people report a bad result; the Mac
/// edition does the same for parity: the result card and Settings › Integrations link to a short GitHub issue form.
/// Nothing is sent from the app. The form opens in the browser, filled in with the version and the tool, never a file
/// name, a path or a picture.
enum AIReport {
    static let repository = "ivLis-Studio/pickverter"

    /// .github/ISSUE_TEMPLATE/ai-result.yml; its "app" and "tool" fields are filled in from here.
    static let template = "ai-result.yml"

    static let label = "ai-report"

    private static let tool = "tool.removebg"

    /// Options whose results the model makes: Remove BG and its variants (Transparent, Crop to Subject, White Background).
    static func covers(_ optionID: String?) -> Bool {
        guard let optionID else { return false }
        return optionID == tool || optionID.hasPrefix(tool + ".")
    }

    /// The report form, filled in with what helps (version, macOS, architecture, which Remove BG) and nothing personal.
    @MainActor
    static func url(optionID: String?) -> URL {
        let system = ProcessInfo.processInfo.operatingSystemVersion
        let macOS = "\(system.majorVersion).\(system.minorVersion).\(system.patchVersion)"
        // The Mac edition only ships from GitHub Releases.
        let app = "Pickverter \(Updater.shared.currentVersion) · macOS \(macOS) · \(architecture) · GitHub"
        var name = "Remove BG"
        if let optionID, optionID.hasPrefix(tool + ".") { name += " (\(optionID.dropFirst(tool.count + 1)))" }
        var components = URLComponents(string: "https://github.com/\(repository)/issues/new")!
        components.queryItems = [
            URLQueryItem(name: "template", value: template),
            URLQueryItem(name: "labels", value: label),
            URLQueryItem(name: "app", value: app),
            URLQueryItem(name: "tool", value: name),
        ]
        // URLComponents leaves "+" as it is, and GitHub would read it as a space.
        components.percentEncodedQuery = components.percentEncodedQuery?.replacingOccurrences(of: "+", with: "%2B")
        return components.url!
    }

    @MainActor
    static func open(optionID: String?) {
        NSWorkspace.shared.open(url(optionID: optionID))
    }

    private static var architecture: String {
        #if arch(arm64)
        return "arm64"
        #else
        return "x86_64"
        #endif
    }
}
