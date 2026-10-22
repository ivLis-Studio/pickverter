import AppKit
import ApplicationServices

@MainActor
enum FinderBridge {
    static let finderID = "com.apple.finder"

    static var isFinderFrontmost: Bool {
        NSWorkspace.shared.frontmostApplication?.bundleIdentifier == finderID
    }

    /// True while Finder's keyboard focus is in a text control (rename, search, Go to Folder…).
    static var isEditingText: Bool {
        guard let app = NSWorkspace.shared.frontmostApplication, app.bundleIdentifier == finderID else { return false }
        let element = AXUIElementCreateApplication(app.processIdentifier)
        AXUIElementSetMessagingTimeout(element, 0.15)
        var focused: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, kAXFocusedUIElementAttribute as CFString, &focused) == .success,
              let focused, CFGetTypeID(focused) == AXUIElementGetTypeID() else { return false }
        let target = focused as! AXUIElement
        var role: CFTypeRef?
        AXUIElementCopyAttributeValue(target, kAXRoleAttribute as CFString, &role)
        let editing: Set<String> = ["AXTextField", "AXTextArea", "AXComboBox", "AXSearchField"]
        if editing.contains(role as? String ?? "") { return true }
        var editable: CFTypeRef?
        if AXUIElementCopyAttributeValue(target, "AXEditable" as CFString, &editable) == .success, (editable as? Bool) == true {
            return true
        }
        return false
    }

    private static let selectionScript: NSAppleScript? = {
        let source = """
        tell application id "com.apple.finder"
            set out to ""
            try
                set picked to selection as alias list
                repeat with anItem in picked
                    set out to out & POSIX path of anItem & linefeed
                end repeat
            end try
            return out
        end tell
        """
        let script = NSAppleScript(source: source)
        script?.compileAndReturnError(nil)
        return script
    }()

    /// Current Finder selection (desktop included). Returns [] when nothing is selected or access is denied.
    static func selection() -> [URL] {
        var error: NSDictionary?
        let result = selectionScript?.executeAndReturnError(&error)
        Debug.log("finder selection error=\(String(describing: error)) result=\(result?.stringValue ?? "nil")")
        if let error, (error[NSAppleScript.errorNumber] as? Int) == -1743 {
            Permissions.shared.noteFinderDenied()
            JobCenter.shared.notice(L("notice.finder.title"), detail: L("notice.finder.detail"), symbol: "lock.shield")
            return []
        }
        guard let text = result?.stringValue else { return [] }
        return text.split(separator: "\n").map { URL(fileURLWithPath: String($0)) }
    }

    /// True when the point (Quartz coordinates) is over a Finder file area rather than chrome like the toolbar.
    static func isFinderContent(at point: CGPoint) -> Bool {
        let system = AXUIElementCreateSystemWide()
        var element: AXUIElement?
        guard AXUIElementCopyElementAtPosition(system, Float(point.x), Float(point.y), &element) == .success,
              let element else { return isFinderFrontmost }
        var pid: pid_t = 0
        AXUIElementGetPid(element, &pid)
        guard NSRunningApplication(processIdentifier: pid)?.bundleIdentifier == finderID else { return false }
        var role: CFTypeRef?
        AXUIElementCopyAttributeValue(element, kAXRoleAttribute as CFString, &role)
        let blocked: Set<String> = ["AXButton", "AXToolbar", "AXMenuBar", "AXMenuBarItem", "AXMenu", "AXMenuItem",
                                    "AXScrollBar", "AXSplitter", "AXPopUpButton", "AXSearchField", "AXTabGroup",
                                    "AXRadioButton", "AXCheckBox", "AXSlider"]
        return !blocked.contains(role as? String ?? "")
    }
}
