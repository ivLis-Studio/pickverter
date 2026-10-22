import AppKit

/// Borderless, non-activating overlay: Finder keeps focus while the wheel is open.
final class WheelPanel: NSPanel {
    let dropView: WheelDropView

    init(size: CGSize) {
        dropView = WheelDropView(frame: CGRect(origin: .zero, size: size))
        super.init(contentRect: CGRect(origin: .zero, size: size), styleMask: [.borderless, .nonactivatingPanel],
                   backing: .buffered, defer: false)
        isOpaque = false
        backgroundColor = .clear
        hasShadow = false
        level = .popUpMenu
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .transient, .ignoresCycle]
        isReleasedWhenClosed = false
        hidesOnDeactivate = false
        isFloatingPanel = true
        becomesKeyOnlyIfNeeded = true
        animationBehavior = .none
        let container = NSView(frame: CGRect(origin: .zero, size: size))
        container.autoresizingMask = [.width, .height]
        contentView = container
    }

    func install(host: NSView) {
        guard let container = contentView else { return }
        host.frame = container.bounds
        host.autoresizingMask = [.width, .height]
        container.addSubview(host)
        dropView.frame = container.bounds
        dropView.autoresizingMask = [.width, .height]
        container.addSubview(dropView)
    }

    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

/// Transparent layer above the SwiftUI content that receives the file drop.
final class WheelDropView: NSView {
    weak var controller: WheelController?
    var acceptsDrops = false

    override init(frame: NSRect) {
        super.init(frame: frame)
        registerForDraggedTypes([.fileURL])
    }

    required init?(coder: NSCoder) {
        fatalError("init(coder:) is not supported")
    }

    private func operation(for info: NSDraggingInfo) -> NSDragOperation {
        let mask = info.draggingSourceOperationMask
        for candidate in [NSDragOperation.copy, .generic, .link, .move] where mask.contains(candidate) {
            return candidate
        }
        return []
    }

    private func update(_ info: NSDraggingInfo) -> NSDragOperation {
        guard acceptsDrops, let controller, let window else { return [] }
        controller.pointerMoved(appKit: window.convertPoint(toScreen: info.draggingLocation))
        return controller.canDropHere ? operation(for: info) : []
    }

    override func draggingEntered(_ sender: NSDraggingInfo) -> NSDragOperation { update(sender) }
    override func draggingUpdated(_ sender: NSDraggingInfo) -> NSDragOperation { update(sender) }
    override func prepareForDragOperation(_ sender: NSDraggingInfo) -> Bool { acceptsDrops }

    override func performDragOperation(_ sender: NSDraggingInfo) -> Bool {
        guard acceptsDrops, let controller, let window else { return false }
        let point = window.convertPoint(toScreen: sender.draggingLocation)
        let urls = (sender.draggingPasteboard.readObjects(forClasses: [NSURL.self],
                                                           options: [.urlReadingFileURLsOnly: true]) as? [URL]) ?? []
        return controller.performDrop(at: point, files: urls)
    }
}
