import AppKit
import QuickLookThumbnailing
import SwiftUI
import UniformTypeIdentifiers

struct WheelGeometry {
    let outerRadius: CGFloat = 150
    let innerRadius: CGFloat = 62
    let rim: CGFloat = 8
    let subGap: CGFloat = 6
    let subThickness: CGFloat = 54
    let margin: CGFloat = 22
    let tabWidth: CGFloat = 94
    let tabHeight: CGFloat = 28
    let tabSpacing: CGFloat = 2
    let tabPadding: CGFloat = 3
    let tabGap: CGFloat = 12

    var discRadius: CGFloat { outerRadius + rim }
    var subInner: CGFloat { discRadius + subGap }
    var subOuter: CGFloat { subInner + subThickness }
    var boxSide: CGFloat { (subOuter + margin) * 2 }
    /// Wheel center in view coordinates (top-left origin).
    var center: CGPoint { CGPoint(x: boxSide / 2, y: boxSide / 2) }
    var tabBarHeight: CGFloat { tabHeight + tabPadding * 2 }
    var tabBarWidth: CGFloat { tabWidth * 3 + tabSpacing * 2 + tabPadding * 2 }
    var tabBarOffset: CGFloat { discRadius + tabGap + tabBarHeight / 2 }
    var windowSize: CGSize { CGSize(width: boxSide, height: boxSide) }
    var deadZone: CGFloat { innerRadius * 0.62 }
    /// How far from the center the pointer still targets a slot. Highlight and click use the same distance, so a
    /// highlighted slot always runs when clicked and a click with nothing highlighted closes the wheel.
    var reach: CGFloat { subOuter + 14 }
    /// Everything the wheel can draw around its center (the outer ring and its edge), plus a little air.
    var envelope: CGFloat { subOuter + 4 + 8 }

    func mainSpan(_ count: Int) -> Double { 2 * .pi / Double(max(count, 1)) }

    /// Angular width (radians) of one option on the outer ring.
    func subSpan(_ count: Int) -> Double { max(22, min(32, 172 / Double(max(count, 1)))) * .pi / 180 }

    /// Where the wheel centers for a pointer at `point`: right on it, or nudged inward near the edges of `area` (a
    /// screen's visible frame, so the menu bar and the Dock never cover it) until the outer ring and the tabs fit.
    func center(for point: CGPoint, in area: CGRect) -> CGPoint {
        func clamp(_ value: CGFloat, _ low: CGFloat, _ high: CGFloat) -> CGFloat {
            low <= high ? min(max(value, low), high) : (low + high) / 2
        }
        return CGPoint(x: clamp(point.x, area.minX + envelope, area.maxX - envelope),
                       y: clamp(point.y, area.minY + envelope, area.maxY - envelope))
    }

    /// Tab rect in AppKit screen coordinates for a wheel centered at c.
    func tabRect(_ mode: WheelMode, wheelCenter c: CGPoint) -> CGRect {
        let barCenterY = c.y - tabBarOffset
        let x = c.x - tabBarWidth / 2 + tabPadding + CGFloat(mode.rawValue) * (tabWidth + tabSpacing)
        return CGRect(x: x, y: barCenterY - tabHeight / 2, width: tabWidth, height: tabHeight)
    }
}

/// Lightweight value used to draw one ring segment (Equatable so SwiftUI can skip redraws).
struct SegmentData: Equatable, Identifiable {
    let id: String
    let title: String
    let symbol: String
    var isFormat = false
    var isMore = false
    var unavailable = false
    /// Unavailable only until ffmpeg is downloaded, which a click on it does.
    var needsDownload = false
    var pageLabel: String? = nil
    var hasVariants = false
    /// On the outer ring: the option a plain click on the slot runs.
    var isDefault = false
}

/// The outer ring of options for one slot. Angles in radians, clockwise from 12 o'clock.
struct SubRingData: Equatable {
    let parentIndex: Int
    let centerAngle: Double
    let span: Double
    let items: [SegmentData]

    func angle(of index: Int) -> Double {
        centerAngle + (Double(index) - Double(items.count - 1) / 2) * span
    }

    var halfWidth: Double { span * Double(items.count) / 2 }
}

@MainActor
final class PointerModel: ObservableObject {
    @Published var angle: Double?
}

/// Target for NSMenuItem closures (menu items hold their target weakly, the menu owner keeps these alive).
@MainActor
final class MenuAction: NSObject {
    private let handler: () -> Void
    init(_ handler: @escaping () -> Void) { self.handler = handler }
    @objc func run() { handler() }
}

@MainActor
final class WheelController: ObservableObject {
    static let shared = WheelController()
    static let maxPerPage = 10

    enum Origin { case drag, selection }

    enum Item: Identifiable {
        case option(PickOption)
        case more(page: Int, total: Int)
        case destination(ResultDestination)

        var id: String {
            switch self {
            case .option(let option): return option.id
            case .more(let page, _): return "more.\(page)"
            case .destination(let destination): return "dest.\(destination.rawValue)"
            }
        }

        var option: PickOption? {
            if case .option(let option) = self { return option }
            return nil
        }

        var variants: [PickOption] { option?.variants ?? [] }
    }

    /// An option waiting for "file or clipboard?" when the destination preference is "ask".
    struct PendingChoice {
        let option: PickOption
        let files: [URL]
        let mode: WheelMode
    }

    let geometry = WheelGeometry()
    let pointer = PointerModel()

    @Published private(set) var isVisible = false
    @Published private(set) var origin: Origin = .selection
    @Published private(set) var mode: WheelMode = .convert
    @Published private(set) var files: [URL] = []
    @Published private(set) var segments: [SegmentData] = []
    @Published private(set) var highlighted: Int?
    @Published private(set) var subHighlighted: Int?
    @Published private(set) var subRing: SubRingData?
    @Published private(set) var tabsHidden = false
    @Published private(set) var hoveredTab: WheelMode?
    @Published private(set) var thumbnail: NSImage?
    @Published private(set) var counts: [WheelMode: Int] = [:]
    @Published private(set) var flash: String?
    @Published private(set) var page = 0
    @Published private(set) var pageCount = 1
    @Published private(set) var choice: PendingChoice?
    @Published var appeared = false
    /// While the right-click menu tracks, the event tap must leave the mouse alone.
    private(set) var menuOpen = false

    private(set) var items: [Item] = []
    private var optionsByMode: [WheelMode: [PickOption]] = [:]
    private var panel: WheelPanel?
    private(set) var center: CGPoint = .zero
    private var lastPointer: CGPoint = .zero
    private var hideTask: Task<Void, Never>?
    private var springTask: Task<Void, Never>?
    private var springKey: String?
    private var flashTask: Task<Void, Never>?
    private var thumbToken = UUID()
    private var typeBuffer = ""
    private var typeResetTask: Task<Void, Never>?
    private var scrollAccumulator: Double = 0
    private let isDemo: Bool
    /// Tests: capture what would run instead of starting a job, and pretend a destination preference.
    var onLaunch: ((PickOption, ResultDestination) -> Void)?
    var destinationOverride: ResultDestination?

    init(demo: Bool = false) {
        isDemo = demo
    }

    // MARK: Presenting

    func present(files: [URL], origin: Origin, mode requested: WheelMode?, at point: CGPoint) {
        guard !files.isEmpty else { return }
        hideTask?.cancel()
        self.files = files
        self.origin = origin
        choice = nil
        loadOptions()
        let newCounts = counts
        let preferred = requested ?? .convert
        mode = (newCounts[preferred] ?? 0) > 0 ? preferred
            : (WheelMode.allCases.first { (newCounts[$0] ?? 0) > 0 } ?? .actions)
        page = 0
        rebuildItems()
        highlighted = nil
        subHighlighted = nil
        subRing = nil
        tabsHidden = false
        hoveredTab = nil
        pointer.angle = nil
        flash = nil
        typeBuffer = ""
        springKey = nil
        loadThumbnail()

        if isDemo {
            center = point
            appeared = true
            return
        }

        let panel = ensurePanel()
        let frame = frameFor(point: point)
        panel.setFrame(frame, display: false)
        Debug.log("present \(origin) mode=\(mode) at=\(point) items=\(items.count)")
        center = CGPoint(x: frame.minX + geometry.center.x, y: frame.maxY - geometry.center.y)
        panel.dropView.acceptsDrops = origin == .drag
        isVisible = true
        appeared = false
        panel.alphaValue = 1
        panel.orderFrontRegardless()
        withAnimation(.spring(response: 0.28, dampingFraction: 0.8)) { appeared = true }
        NSHapticFeedbackManager.defaultPerformer.perform(.levelChange, performanceTime: .now)
        lastPointer = point
        pointerMoved(appKit: NSEvent.mouseLocation)
        Speech.announce(L("a11y.wheel_open", mode.title, LP("a11y.slots", segments.count), filesTitle))
    }

    /// Catalog options with the user's wheel customization applied (demo wheels stay stock).
    private func loadOptions() {
        var newCounts: [WheelMode: Int] = [:]
        for mode in WheelMode.allCases {
            var options = Catalog.options(for: files, mode: mode)
            if !isDemo { options = WheelLayout.shared.arrange(options, key: WheelLayout.key(mode: mode, files: files)) }
            optionsByMode[mode] = options
            newCounts[mode] = options.count
        }
        counts = newCounts
    }

    /// After hiding, moving or pinning: rebuild in place and keep the same slot highlighted.
    func refreshLayout(keeping id: String? = nil) {
        loadOptions()
        rebuildItems()
        subRing = nil
        subHighlighted = nil
        tabsHidden = false
        if let id, let index = items.firstIndex(where: { $0.id == id }) {
            highlighted = index
            pointer.angle = Double(index) * geometry.mainSpan(items.count)
        } else {
            highlighted = nil
            pointer.angle = nil
        }
    }

    func dismiss() {
        guard isVisible else { return }
        Debug.log("dismiss")
        isVisible = false
        springTask?.cancel()
        springKey = nil
        withAnimation(.easeOut(duration: 0.12)) { appeared = false }
        hideTask = Task { @MainActor [weak self] in
            try? await Task.sleep(nanoseconds: 140_000_000)
            guard let self, !Task.isCancelled, !self.isVisible else { return }
            self.panel?.orderOut(nil)
            self.choice = nil
        }
    }

    func dragSessionEnded() {
        guard isVisible, origin == .drag else { return }
        Task { @MainActor [weak self] in
            try? await Task.sleep(nanoseconds: 300_000_000)
            guard let self, self.isVisible, self.origin == .drag, self.choice == nil else { return }
            self.dismiss()
        }
    }

    private func ensurePanel() -> WheelPanel {
        if let panel { return panel }
        let panel = WheelPanel(size: geometry.windowSize)
        let host = NSHostingView(rootView: WheelView(model: self))
        panel.install(host: host)
        panel.dropView.controller = self
        self.panel = panel
        return panel
    }

    private func frameFor(point: CGPoint) -> CGRect {
        let size = geometry.windowSize
        // Near an edge the whole wheel moves inward: every slot, outer ring and tab stays on screen and clickable.
        let middle = ScreenGeometry.screen(containing: point).map { geometry.center(for: point, in: $0.visibleFrame) } ?? point
        return CGRect(x: middle.x - geometry.center.x, y: middle.y - (size.height - geometry.center.y),
                      width: size.width, height: size.height)
    }

    private func loadThumbnail() {
        guard let first = files.first else { return }
        let token = UUID()
        thumbToken = token
        if FileManager.default.fileExists(atPath: first.path) {
            thumbnail = NSWorkspace.shared.icon(forFile: first.path)
        } else {
            thumbnail = NSWorkspace.shared.icon(for: UTType(filenameExtension: first.pathExtension) ?? .data)
        }
        let request = QLThumbnailGenerator.Request(fileAt: first, size: CGSize(width: 96, height: 96), scale: 2,
                                                   representationTypes: .thumbnail)
        QLThumbnailGenerator.shared.generateBestRepresentation(for: request) { [weak self] representation, _ in
            guard let image = representation?.nsImage else { return }
            Task { @MainActor in
                guard let self, self.thumbToken == token else { return }
                self.thumbnail = image
            }
        }
    }

    // MARK: Items

    private func rebuildItems() {
        if choice != nil {
            pageCount = 1
            page = 0
            items = [.destination(.file), .destination(.clipboard), .destination(.both)]
        } else {
            let options = optionsByMode[mode] ?? []
            if options.count <= Self.maxPerPage {
                pageCount = 1
                page = 0
                items = options.map { .option($0) }
            } else {
                let perPage = Self.maxPerPage - 1
                pageCount = Int((Double(options.count) / Double(perPage)).rounded(.up))
                page = min(page, pageCount - 1)
                let start = page * perPage
                items = options[start..<min(options.count, start + perPage)].map { .option($0) } + [.more(page: page, total: pageCount)]
            }
        }
        segments = items.map { item in
            switch item {
            case .option(let option):
                return SegmentData(id: option.id, title: option.title, symbol: option.symbol, isFormat: option.isFormat,
                                   unavailable: option.unavailable != nil, needsDownload: option.unavailable == Tools.ffmpegHint,
                                   hasVariants: !option.variants.isEmpty)
            case .more(let page, let total):
                return SegmentData(id: item.id, title: L("wheel.more"), symbol: "ellipsis", isMore: true,
                                   pageLabel: "\(page + 1)/\(total)")
            case .destination(let destination):
                return SegmentData(id: item.id, title: destination.label, symbol: destination.symbol)
            }
        }
    }

    var highlightedItem: Item? {
        guard let highlighted, items.indices.contains(highlighted) else { return nil }
        return items[highlighted]
    }

    /// The option that would run right now: a variant when one is highlighted on the outer ring, otherwise the
    /// slot's default variant (`implied`), otherwise the slot itself.
    var activeOption: (option: PickOption, parent: PickOption?, implied: Bool)? {
        guard let parent = highlightedItem?.option else { return nil }
        if let sub = subHighlighted, parent.variants.indices.contains(sub) { return (parent.variants[sub], parent, false) }
        if parent.variants.count > 1 {
            let preferred = isDemo ? 0 : WheelLayout.shared.preferredVariant(of: parent)
            return (parent.variants[preferred], parent, true)
        }
        return (parent, nil, false)
    }

    var canDropHere: Bool {
        guard let active = activeOption else { return false }
        let reason = active.option.unavailable ?? active.parent?.unavailable
        return reason == nil || reason == Tools.ffmpegHint
    }

    func setMode(_ newMode: WheelMode) {
        guard newMode != mode, choice == nil else { return }
        setExpanded(nil)
        withAnimation(.spring(response: 0.3, dampingFraction: 0.86)) {
            mode = newMode
            page = 0
            highlighted = nil
            subHighlighted = nil
            rebuildItems()
        }
        typeBuffer = ""
        NSHapticFeedbackManager.defaultPerformer.perform(.levelChange, performanceTime: .now)
        Speech.announce(newMode.title)
        if isVisible { pointerMoved(appKit: lastPointer) }
    }

    func cycleMode(_ step: Int = 1) {
        let modes = WheelMode.allCases
        var index = mode.rawValue
        for _ in 0..<modes.count {
            index = (index + step + modes.count) % modes.count
            if (counts[modes[index]] ?? 0) > 0 {
                setMode(modes[index])
                return
            }
        }
    }

    func nextPage(_ step: Int = 1) {
        guard pageCount > 1 else { return }
        setExpanded(nil)
        withAnimation(.spring(response: 0.3, dampingFraction: 0.86)) {
            page = (page + step + pageCount) % pageCount
            highlighted = nil
            subHighlighted = nil
            rebuildItems()
        }
    }

    func scroll(_ delta: Double) {
        guard pageCount > 1 else { return }
        scrollAccumulator += delta
        if abs(scrollAccumulator) > 18 {
            nextPage(scrollAccumulator > 0 ? -1 : 1)
            scrollAccumulator = 0
        }
    }

    // MARK: Outer ring

    private func setExpanded(_ index: Int?) {
        guard let index, items.indices.contains(index), !items[index].variants.isEmpty else {
            if subRing != nil {
                withAnimation(.easeOut(duration: 0.12)) {
                    subRing = nil
                    tabsHidden = false
                }
            }
            return
        }
        if subRing?.parentIndex == index { return }
        guard let parent = items[index].option else { return }
        let variants = parent.variants
        let preferred = isDemo ? 0 : WheelLayout.shared.preferredVariant(of: parent)
        let ring = SubRingData(parentIndex: index, centerAngle: Double(index) * geometry.mainSpan(items.count),
                               span: geometry.subSpan(variants.count),
                               items: variants.enumerated().map { position, variant in
                                   SegmentData(id: variant.id, title: variant.title, symbol: variant.symbol,
                                               unavailable: variant.unavailable != nil,
                                               needsDownload: variant.unavailable == Tools.ffmpegHint,
                                               isDefault: position == preferred && variants.count > 1)
                               })
        // The tab bar sits under 6 o'clock; fade it when the ring opens over it.
        let hidesTabs = abs(ring.centerAngle - .pi) < ring.halfWidth + 0.68
        withAnimation(.spring(response: 0.24, dampingFraction: 0.86)) {
            subRing = ring
            tabsHidden = hidesTabs
        }
    }

    private func subIndex(at angle: Double) -> Int? {
        guard let ring = subRing else { return nil }
        var delta = angle - ring.centerAngle
        while delta > .pi { delta -= 2 * .pi }
        while delta < -.pi { delta += 2 * .pi }
        guard abs(delta) <= ring.halfWidth + ring.span * 0.35 else { return nil }
        let raw = Int(((delta + ring.halfWidth) / ring.span).rounded(.down))
        return min(max(raw, 0), ring.items.count - 1)
    }

    private func setSub(_ index: Int?) {
        guard subHighlighted != index else { return }
        subHighlighted = index
        if let index, let ring = subRing, ring.items.indices.contains(index) {
            NSHapticFeedbackManager.defaultPerformer.perform(.alignment, performanceTime: .now)
            Speech.announce(ring.items[index].title)
        }
    }

    // MARK: Pointer

    func contains(quartz point: CGPoint) -> Bool {
        panel?.frame.contains(ScreenGeometry.appKit(fromQuartz: point)) ?? false
    }

    func pointerMoved(quartz point: CGPoint) {
        pointerMoved(appKit: ScreenGeometry.appKit(fromQuartz: point))
    }

    func pointerMoved(appKit point: CGPoint) {
        guard isVisible else { return }
        lastPointer = point
        if let tab = tabHit(point) {
            if hoveredTab != tab { hoveredTab = tab }
            setHighlight(nil)
            setSub(nil)
            setExpanded(nil)
            pointer.angle = nil
            if origin == .drag {
                spring("tab.\(tab.rawValue)", after: 0.26) { [weak self] in self?.setMode(tab) }
            }
            return
        }
        if hoveredTab != nil { hoveredTab = nil }
        let dx = point.x - center.x, dy = point.y - center.y
        let distance = hypot(dx, dy)
        let inside = panel?.frame.contains(point) ?? true
        guard inside, distance >= geometry.deadZone, distance <= geometry.reach, !items.isEmpty else {
            setHighlight(nil)
            setSub(nil)
            if distance < geometry.deadZone { setExpanded(nil) }
            pointer.angle = nil
            spring(nil, after: 0) {}
            return
        }
        var angle = atan2(dx, dy)
        if angle < 0 { angle += 2 * .pi }
        if distance > geometry.discRadius + 1, let ring = subRing, let sub = subIndex(at: angle) {
            setHighlight(ring.parentIndex)
            setSub(sub)
            pointer.angle = ring.angle(of: sub)
            spring(nil, after: 0) {}
            return
        }
        let span = geometry.mainSpan(items.count)
        let index = Int(((angle + span / 2) / span).rounded(.down)) % items.count
        pointer.angle = angle
        setHighlight(index)
        setSub(nil)
        setExpanded(index)
        if origin == .drag, case .more = items[index] {
            spring("more.\(page)", after: 0.5) { [weak self] in self?.nextPage() }
        } else {
            spring(nil, after: 0) {}
        }
    }

    private func tabHit(_ point: CGPoint) -> WheelMode? {
        guard choice == nil, !tabsHidden else { return nil }
        return WheelMode.allCases.first { geometry.tabRect($0, wheelCenter: center).insetBy(dx: -1, dy: -6).contains(point) }
    }

    private func setHighlight(_ index: Int?) {
        guard highlighted != index else { return }
        highlighted = index
        if let index, segments.indices.contains(index) {
            NSHapticFeedbackManager.defaultPerformer.perform(.alignment, performanceTime: .now)
            if Speech.isActive {
                let segment = segments[index]
                var line = segment.title
                if segment.unavailable { line += ", " + L("a11y.unavailable") }
                if segment.hasVariants, let option = items[index].option { line += ", " + LP("hint.options", option.variants.count) }
                Speech.announce(line)
            }
        }
    }

    private func spring(_ key: String?, after delay: Double, _ action: @escaping @MainActor () -> Void) {
        guard key != springKey else { return }
        springKey = key
        springTask?.cancel()
        guard let key else { return }
        springTask = Task { @MainActor [weak self] in
            try? await Task.sleep(nanoseconds: UInt64(delay * 1_000_000_000))
            guard let self, !Task.isCancelled, self.springKey == key else { return }
            action()
        }
    }

    // MARK: Committing

    /// flip: ⌥ was held (send the result the other way). forced: ⌘C / ⌘S.
    func commit(index: Int? = nil, files override: [URL]? = nil, flip: Bool = false, forced: ResultDestination? = nil) {
        guard let index = index ?? highlighted, items.indices.contains(index) else { return }
        switch items[index] {
        case .more:
            nextPage()
        case .destination(let destination):
            guard let pending = choice else { return }
            launch(pending.option, mode: pending.mode, files: pending.files, destination: forced ?? destination)
        case .option(let parent):
            let sub = index == highlighted ? subHighlighted : nil
            var option = sub.flatMap { parent.variants.indices.contains($0) ? parent.variants[$0] : nil } ?? parent
            if sub == nil, parent.variants.count > 1, !isDemo {
                option = parent.variants[WheelLayout.shared.preferredVariant(of: parent)]
            }
            let reason = option.unavailable ?? parent.unavailable
            // Missing ffmpeg isn't a dead end: the job downloads it first, right on its card.
            let needsFFmpeg = reason == Tools.ffmpegHint
            if let reason, !needsFFmpeg {
                showFlash(reason)
                NSSound.beep()
                return
            }
            if sub != nil, option.id != parent.id, !isDemo { WheelLayout.shared.noteUsed(slot: parent.id, variant: option.id) }
            let targets = (override?.isEmpty == false) ? override! : files
            var display = Self.display(option, parent: option.id == parent.id ? nil : parent)
            if needsFFmpeg { display = FFmpegInstaller.preparing(display) }
            guard let destination = resolveDestination(for: display, flip: flip, forced: forced) else {
                enterChoice(PendingChoice(option: display, files: targets, mode: mode))
                return
            }
            launch(display, mode: mode, files: targets, destination: destination)
        }
    }

    /// A variant runs under its parent's name ("Rotate · Left 90°") so result cards stay readable.
    static func display(_ option: PickOption, parent: PickOption?) -> PickOption {
        guard let parent else { return option }
        var copy = PickOption(id: option.id,
                              title: parent.isFormat ? parent.title : "\(parent.title) · \(option.title)",
                              detail: option.detail, symbol: option.symbol.isEmpty ? parent.symbol : option.symbol,
                              isFormat: parent.isFormat, outputExt: parent.outputExt, unavailable: option.unavailable,
                              producesFiles: option.producesFiles, interactive: option.interactive, perform: option.perform)
        copy.variants = []
        return copy
    }

    private func resolveDestination(for option: PickOption, flip: Bool, forced: ResultDestination?) -> ResultDestination? {
        guard option.producesFiles else { return .file }
        if let forced { return forced }
        switch destinationOverride ?? Preferences.shared.destination {
        case .ask: return flip ? .clipboard : nil
        case .file: return flip ? .clipboard : .file
        case .clipboard: return flip ? .file : .clipboard
        case .both: return flip ? .file : .both
        }
    }

    private func launch(_ option: PickOption, mode: WheelMode, files: [URL], destination: ResultDestination) {
        let anchor = center
        dismiss()
        if let onLaunch {
            onLaunch(option, destination)
            return
        }
        JobCenter.shared.run(option, mode: mode, files: files, anchor: anchor, destination: destination)
    }

    private func enterChoice(_ pending: PendingChoice) {
        origin = .selection
        panel?.dropView.acceptsDrops = false
        setExpanded(nil)
        withAnimation(.spring(response: 0.3, dampingFraction: 0.86)) {
            choice = pending
            rebuildItems()
            highlighted = 0
            subHighlighted = nil
        }
        pointer.angle = 0
    }

    func performDrop(at point: CGPoint, files dropped: [URL]) -> Bool {
        pointerMoved(appKit: point)
        guard canDropHere, let index = highlighted else {
            dismiss()
            return false
        }
        commit(index: index, files: dropped, flip: NSEvent.modifierFlags.contains(.option))
        return true
    }

    /// Returns true when the click was consumed by the wheel.
    func handleClick(quartz: CGPoint, flags: CGEventFlags) -> Bool {
        guard isVisible else { return false }
        let point = ScreenGeometry.appKit(fromQuartz: quartz)
        if let tab = tabHit(point) {
            setMode(tab)
            return true
        }
        let distance = hypot(point.x - center.x, point.y - center.y)
        if distance > geometry.reach {
            dismiss()
            return false
        }
        if distance < geometry.deadZone {
            dismiss()
            return true
        }
        pointerMoved(appKit: point)
        commit(flip: flags.contains(.maskAlternate))
        return true
    }

    private static let digitKeys: [Int64: Int] = [18: 1, 19: 2, 20: 3, 21: 4, 23: 5, 22: 6, 26: 7, 28: 8, 25: 9, 29: 10]

    // MARK: Customizing (right-click)

    /// Hide, reorder, pin and pick defaults right where the slot is. Returns true when a menu was shown.
    func showContextMenu(quartz: CGPoint) -> Bool {
        guard isVisible, choice == nil else { return false }
        let point = ScreenGeometry.appKit(fromQuartz: quartz)
        let distance = hypot(point.x - center.x, point.y - center.y)
        guard distance <= geometry.reach else { return false }
        pointerMoved(appKit: point)
        guard let index = highlighted, case .option(let parent) = items[index] else { return false }
        let variant = subHighlighted.flatMap { parent.variants.indices.contains($0) ? parent.variants[$0] : nil }
        let layout = WheelLayout.shared
        let key = WheelLayout.key(mode: mode, files: files)
        let visible = (optionsByMode[mode] ?? []).map(\.id)
        let menu = NSMenu()
        menu.autoenablesItems = false
        var actions: [MenuAction] = []
        func add(_ title: String, symbol: String? = nil, checked: Bool = false, enabled: Bool = true, to target: NSMenu? = nil,
                 _ handler: @escaping () -> Void) {
            let action = MenuAction(handler)
            actions.append(action)
            let item = NSMenuItem(title: title, action: #selector(MenuAction.run), keyEquivalent: "")
            item.target = action
            item.state = checked ? .on : .off
            item.isEnabled = enabled
            if let symbol { item.image = NSImage(systemSymbolName: symbol, accessibilityDescription: nil) }
            (target ?? menu).addItem(item)
        }
        let header = NSMenuItem(title: variant.map { "\(parent.title) · \($0.title)" } ?? parent.title, action: nil, keyEquivalent: "")
        header.isEnabled = false
        menu.addItem(header)
        menu.addItem(.separator())
        var keep: String? = parent.id
        if let variant {
            let pinned = layout.isPinned(variant.id, key: key)
            add(pinned ? L("ctx.unpin") : L("ctx.pin"), symbol: pinned ? "pin.slash" : "pin") {
                if pinned { layout.unpin(variant.id, key: key) } else {
                    layout.pin(variant.id, title: WheelController.display(variant, parent: parent).title, key: key)
                }
            }
            let isDefault = layout.preferredVariant(of: parent) == parent.variants.firstIndex(where: { $0.id == variant.id })
                && layout.defaults[parent.id] != nil
            add(L("ctx.make_default"), symbol: "checkmark.circle", checked: isDefault) {
                layout.setDefault(slot: parent.id, variant: isDefault ? nil : variant.id)
            }
        } else {
            if let source = parent.pinnedFrom {
                add(L("ctx.unpin"), symbol: "pin.slash") { layout.unpin(parent.id, key: key) }
                keep = source
            } else {
                add(L("ctx.hide"), symbol: "eye.slash") { layout.hide(parent.id, title: parent.title) }
                keep = nil
            }
            let position = visible.firstIndex(of: parent.id) ?? 0
            add(L("ctx.move_earlier"), symbol: "arrow.counterclockwise", enabled: position > 0) {
                layout.move(parent.id, by: -1, visible: visible, key: key)
            }
            add(L("ctx.move_later"), symbol: "arrow.clockwise", enabled: position < visible.count - 1) {
                layout.move(parent.id, by: 1, visible: visible, key: key)
            }
            if parent.variants.count > 1 {
                let submenu = NSMenu()
                submenu.autoenablesItems = false
                let explicit = layout.defaults[parent.id]
                add(L("ctx.default_auto"), checked: explicit == nil, to: submenu) { layout.setDefault(slot: parent.id, variant: nil) }
                submenu.addItem(.separator())
                for option in parent.variants {
                    add(option.title, checked: explicit == option.id, to: submenu) {
                        layout.setDefault(slot: parent.id, variant: option.id)
                    }
                }
                let holder = NSMenuItem(title: L("ctx.default"), action: nil, keyEquivalent: "")
                holder.image = NSImage(systemSymbolName: "checkmark.circle", accessibilityDescription: nil)
                holder.submenu = submenu
                menu.addItem(holder)
            }
        }
        menu.addItem(.separator())
        if layout.isCustomized(key) {
            add(L("ctx.reset_ring"), symbol: "arrow.uturn.backward") { layout.reset(key: key) }
        }
        add(L("ctx.settings"), symbol: "gearshape") { [weak self] in
            self?.dismiss()
            WindowManager.shared.showSettings(tab: .wheel)
        }
        menuOpen = true
        _ = menu.popUp(positioning: nil, at: NSEvent.mouseLocation, in: nil)
        menuOpen = false
        _ = actions
        if isVisible {
            refreshLayout(keeping: keep)
            if let keep, let slot = items.firstIndex(where: { $0.id == keep }) { setExpanded(slot) }
        }
        return true
    }
    private static let letterKeys: [Int64: Character] = [
        0: "a", 11: "b", 8: "c", 2: "d", 14: "e", 3: "f", 5: "g", 4: "h", 34: "i", 38: "j", 40: "k", 37: "l", 46: "m",
        45: "n", 31: "o", 35: "p", 12: "q", 15: "r", 1: "s", 17: "t", 32: "u", 9: "v", 13: "w", 7: "x", 16: "y", 6: "z",
    ]

    /// Returns true when the key should be swallowed.
    func handleKey(code: Int64, event: CGEvent) -> Bool {
        guard isVisible else { return false }
        Debug.log("key code=\(code) flags=\(event.flags.rawValue) origin=\(origin)")
        if origin == .drag {
            if code == 53 { dismiss() }
            return false
        }
        let flags = event.flags
        if flags.contains(.maskCommand) {
            if code == 8 || code == 1 {
                commit(forced: code == 8 ? .clipboard : .file)
                return true
            }
            dismiss()
            return false
        }
        if flags.contains(.maskControl) {
            dismiss()
            return false
        }
        switch code {
        case 53:
            dismiss()
        case 36, 76, 49:
            commit()
        case 123:
            moveHighlight(-1)
        case 124:
            moveHighlight(1)
        case 126:
            moveVariant(-1)
        case 125:
            moveVariant(1)
        case 48:
            if choice == nil { cycleMode(flags.contains(.maskShift) ? -1 : 1) }
        case 116:
            nextPage(-1)
        case 121:
            nextPage(1)
        default:
            if let number = Self.digitKeys[code] {
                if items.indices.contains(number - 1) {
                    setHighlight(number - 1)
                    setSub(nil)
                    commit()
                }
            } else if let letter = Self.letterKeys[code], choice == nil {
                typeahead(letter)
            }
        }
        return true
    }

    private func moveHighlight(_ step: Int) {
        guard !items.isEmpty else { return }
        let count = items.count
        let next = highlighted.map { ($0 + step + count) % count } ?? (step > 0 ? 0 : count - 1)
        setHighlight(next)
        setSub(nil)
        setExpanded(next)
        pointer.angle = Double(next) * geometry.mainSpan(count)
    }

    /// ↑ ↓ walk the outer ring of the highlighted slot (or the main ring when it has none).
    private func moveVariant(_ step: Int) {
        guard let current = highlighted, items.indices.contains(current), !items[current].variants.isEmpty else {
            moveHighlight(step)
            return
        }
        setExpanded(current)
        let count = items[current].variants.count
        let next = subHighlighted.map { ($0 + step + count) % count } ?? (step > 0 ? 0 : count - 1)
        setSub(next)
        if let ring = subRing { pointer.angle = ring.angle(of: next) }
    }

    private func typeahead(_ letter: Character) {
        typeBuffer.append(letter)
        typeResetTask?.cancel()
        typeResetTask = Task { @MainActor [weak self] in
            try? await Task.sleep(nanoseconds: 1_100_000_000)
            if !Task.isCancelled { self?.typeBuffer = "" }
        }
        let options = optionsByMode[mode] ?? []
        let query = typeBuffer
        guard let match = options.firstIndex(where: { $0.title.lowercased().hasPrefix(query) })
                ?? options.firstIndex(where: { $0.id.lowercased().contains(query) }) else { return }
        var slot = match
        if pageCount > 1 {
            let perPage = Self.maxPerPage - 1
            let targetPage = match / perPage
            if targetPage != page {
                withAnimation(.spring(response: 0.3, dampingFraction: 0.86)) { page = targetPage; rebuildItems() }
            }
            slot = match % perPage
        }
        setHighlight(slot)
        setSub(nil)
        setExpanded(slot)
        pointer.angle = Double(slot) * geometry.mainSpan(items.count)
    }

    private func showFlash(_ message: String) {
        flash = message
        flashTask?.cancel()
        flashTask = Task { @MainActor [weak self] in
            try? await Task.sleep(nanoseconds: 1_800_000_000)
            if !Task.isCancelled { self?.flash = nil }
        }
    }

    // MARK: Hub text

    var filesTitle: String {
        guard let first = files.first else { return "" }
        return files.count == 1 ? first.lastPathComponent : LP("wheel.items", files.count)
    }

    var hint: String {
        origin == .drag ? L("wheel.hint.drag") : L("wheel.hint.click")
    }

    func previewLine(for option: PickOption, parent: PickOption?) -> String {
        let format = parent?.isFormat == true ? parent : (option.isFormat ? option : nil)
        if let format, let ext = format.outputExt {
            if files.count == 1, let first = files.first {
                return FileNames.split(first.lastPathComponent).base + "." + ext
            }
            return LP("wheel.files_to", files.count, format.title)
        }
        if let parent { return parent.title }
        return option.detail
    }

    /// Small reminder of the ⌥ override, shown only for options that create files.
    func destinationHint(for option: PickOption) -> String? {
        guard option.producesFiles, choice == nil else { return nil }
        switch Preferences.shared.destination {
        case .file, .ask: return L("hint.alt_clipboard")
        case .clipboard, .both: return L("hint.alt_file")
        }
    }

    // MARK: Demo / snapshots / tests

    func demoHighlight(_ index: Int?, sub: Int? = nil) {
        guard let index, items.indices.contains(index) else {
            highlighted = nil
            subHighlighted = nil
            subRing = nil
            pointer.angle = nil
            return
        }
        highlighted = index
        setExpanded(index)
        subHighlighted = sub
        if let sub, let ring = subRing { pointer.angle = ring.angle(of: sub) } else {
            pointer.angle = Double(index) * geometry.mainSpan(items.count)
        }
    }

    func demoSetMode(_ newMode: WheelMode) {
        subRing = nil
        tabsHidden = false
        withAnimation(.spring(response: 0.36, dampingFraction: 0.86)) {
            mode = newMode
            page = 0
            highlighted = nil
            subHighlighted = nil
            rebuildItems()
        }
    }

    func demoChoice() {
        guard let option = optionsByMode[mode]?.first else { return }
        choice = PendingChoice(option: option, files: files, mode: mode)
        rebuildItems()
        highlighted = 0
        pointer.angle = 0
    }

    func forceVisibleForTesting() {
        isVisible = true
    }
}
