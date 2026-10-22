import AppKit
import Combine
import QuickLookThumbnailing
import SwiftUI

@MainActor
final class Job: ObservableObject, Identifiable {
    enum State: Equatable { case running, succeeded, failed(String), cancelled }
    enum Tone { case info, warning }

    struct NoticeAction {
        let title: String
        let symbol: String
        let run: () -> Void
    }

    let id = UUID()
    let option: PickOption?
    let title: String
    let symbol: String
    let isFormat: Bool
    let mode: WheelMode
    let files: [URL]
    let destination: ResultDestination
    let anchor: CGPoint
    let created = Date()
    let tone: Tone
    let noticeAction: NoticeAction?
    /// Seconds before a notice fades on its own (nil: the usual timing).
    let lingers: Double?
    /// Interactive jobs stay invisible until the editor is confirmed.
    @Published var started = true
    /// Offered on a failed card when one click can fix the failure (blocked folder access, missing ffmpeg).
    @Published var fixAction: NoticeAction?

    @Published var state: State = .running
    @Published var progress: Double?
    @Published var note: String?
    @Published private(set) var outputs: [URL] = []
    @Published private(set) var message: String?
    @Published private(set) var undone = false
    @Published private(set) var preview: NSImage?
    /// Results currently sit in the private clipboard cache (not saved next to the original yet).
    @Published private(set) var inCache = false
    @Published private(set) var copied = false
    private(set) var undoable = false
    private(set) var silent = false
    private var sourceBytes: Int64 = 0
    private var outputBytes: Int64 = 0
    var task: Task<Void, Never>?

    init(option: PickOption, mode: WheelMode, files: [URL], destination: ResultDestination, anchor: CGPoint = .zero) {
        self.option = option
        title = option.title
        symbol = option.symbol
        isFormat = option.isFormat
        self.mode = mode
        self.files = files
        self.destination = destination
        self.anchor = anchor
        tone = .info
        noticeAction = nil
        lingers = nil
    }

    /// Informational card without a job behind it.
    init(notice: String, detail: String, symbol: String, tone: Tone = .warning, action: NoticeAction? = nil, lingers: Double? = nil) {
        option = nil
        title = notice
        self.symbol = symbol
        isFormat = false
        mode = .actions
        files = []
        destination = .file
        anchor = NSEvent.mouseLocation
        self.tone = tone
        noticeAction = action
        self.lingers = lingers
        message = detail
        state = tone == .warning ? .failed(detail) : .succeeded
    }

    var isRunning: Bool { state == .running }
    var isNotice: Bool { option == nil }
    var hasFiles: Bool { !outputs.isEmpty && !undone }

    func complete(with result: PickResult) {
        outputs = result.outputs
        message = result.message
        undoable = result.undoable && !result.outputs.isEmpty
        silent = result.silent
        inCache = outputs.contains { ClipboardStore.contains($0) }
        if !outputs.isEmpty {
            sourceBytes = files.reduce(0) { $0 + FileNames.size(of: $1) }
            outputBytes = outputs.reduce(0) { $0 + FileNames.size(of: $1) }
            if destination.copies, option?.producesFiles == true {
                ClipboardWriter.write(outputs)
                copied = true
            }
            loadPreview()
        }
        progress = 1
        state = .succeeded
    }

    private func loadPreview() {
        guard let first = outputs.first else { return }
        preview = NSWorkspace.shared.icon(forFile: first.path)
        let request = QLThumbnailGenerator.Request(fileAt: first, size: CGSize(width: 72, height: 72), scale: 2,
                                                   representationTypes: .thumbnail)
        QLThumbnailGenerator.shared.generateBestRepresentation(for: request) { [weak self] representation, _ in
            guard let image = representation?.nsImage else { return }
            Task { @MainActor in self?.preview = image }
        }
    }

    var label: String { isFormat ? L("job.convert_label", title) : title }

    var headline: String {
        if isNotice { return title }
        switch state {
        case .running: return L("job.running", label)
        case .succeeded: return outputs.isEmpty ? label : (undone ? L("job.undone", label) : L("job.done", label))
        case .failed: return L("job.failed", label)
        case .cancelled: return L("job.cancelled", label)
        }
    }

    var subline: String {
        switch state {
        case .running:
            return note ?? (files.count == 1 ? files[0].lastPathComponent : LP("wheel.items", files.count))
        case .failed(let reason):
            return reason
        case .cancelled:
            return L("job.stopped")
        case .succeeded:
            if isNotice { return message ?? "" }
            if undone { return L("job.trashed") }
            if outputs.isEmpty { return message ?? "" }
            var parts = [outputs.count == 1 ? outputs[0].lastPathComponent : LP("job.files_created", outputs.count)]
            var size = FileNames.formatBytes(outputBytes)
            let shrinks = ["tool.compress", "tool.resize", "tool.resolution"].contains { option?.id.hasPrefix($0) == true }
            if shrinks, sourceBytes > 0, outputBytes > 0, outputBytes < sourceBytes {
                let saved = Int((1 - Double(outputBytes) / Double(sourceBytes)) * 100)
                if saved >= 1 { size += " (−\(saved)%)" }
            }
            parts.append(size)
            var line = parts.joined(separator: " · ")
            if inCache { line = L("job.in_clipboard") + " · " + line } else if copied { line += " · " + L("job.also_copied") }
            if let message { line += "\n" + message }
            return line
        }
    }

    func cancel() { task?.cancel() }

    func reveal() { NSWorkspace.shared.activateFileViewerSelecting(outputs) }

    func open() {
        if let first = outputs.first { NSWorkspace.shared.open(first) }
    }

    func copyToClipboard() {
        ClipboardWriter.write(outputs)
        copied = true
    }

    /// Moves clipboard-only results to where files normally go.
    func saveFromCache() {
        let location = Preferences.shared.outputLocation
        var moved: [URL] = []
        for (index, url) in outputs.enumerated() {
            let source = files.indices.contains(index) ? files[index] : (files.first ?? url)
            let target = FileNames.unique(location.directory(for: source).appendingPathComponent(url.lastPathComponent))
            if (try? FileManager.default.moveItem(at: url, to: target)) != nil { moved.append(target) } else { moved.append(url) }
        }
        outputs = moved
        inCache = false
        undoable = true
        if copied { ClipboardWriter.write(outputs) }
    }

    func undo() {
        let existing = outputs.filter { FileManager.default.fileExists(atPath: $0.path) }
        NSWorkspace.shared.recycle(existing) { [weak self] _, _ in
            Task { @MainActor in self?.undone = true }
        }
    }
}

@MainActor
final class JobCenter: ObservableObject {
    static let shared = JobCenter()

    @Published private(set) var jobs: [Job] = []
    let hud = HUDController()

    func run(_ option: PickOption, mode: WheelMode, files: [URL], anchor: CGPoint, destination: ResultDestination = .file) {
        let job = Job(option: option, mode: mode, files: files, destination: destination, anchor: anchor)
        jobs.insert(job, at: 0)
        if jobs.count > 40 { jobs.removeLast(jobs.count - 40) }
        if destination == .clipboard { ClipboardStore.prune() }
        let signal = StartSignal()
        let context = PickContext(files: files, location: Preferences.shared.outputLocation, destination: destination,
                                  anchor: anchor, jobID: job.id, started: signal) { [weak job] fraction, note in
            Task { @MainActor in
                guard let job, job.isRunning else { return }
                if let fraction { job.progress = fraction }
                if let note { job.note = note.isEmpty ? nil : note }
            }
        }
        let delay: UInt64 = mode == .actions ? 450_000_000 : 120_000_000
        if option.interactive {
            job.started = false
            signal.set { [weak self, weak job] in
                Task { @MainActor in
                    guard let self, let job, job.isRunning else { return }
                    job.started = true
                    self.hud.show(job)
                }
            }
        }
        job.task = Task { @MainActor [weak self] in
            let reveal = Task { @MainActor in
                guard !option.interactive else { return }
                try? await Task.sleep(nanoseconds: delay)
                if !Task.isCancelled, job.isRunning { self?.hud.show(job) }
            }
            do {
                // Convert and tool jobs read their inputs; ask macOS about blocked folders up front.
                if mode != .actions, let blocked = await FileAccess.firstBlocked(files) {
                    throw FileAccess.Denied(url: blocked)
                }
                let result = try await option.perform(context)
                job.complete(with: result)
            } catch {
                if Task.isCancelled || error is CancellationError {
                    job.state = .cancelled
                } else if FileAccess.isAccessDenied(error) {
                    let message = (error as? FileAccess.Denied)?.errorDescription
                        ?? L("err.no_access", FileAccess.placeName(for: files.first ?? URL(fileURLWithPath: "/")))
                    job.fixAction = Job.NoticeAction(title: L("access.fix"), symbol: "lock.open") { FileAccess.openBestSettings() }
                    job.state = .failed(message)
                } else if Tools.isMissingFFmpeg(error) {
                    // Download ffmpeg on a fresh card, then run the same thing again.
                    job.fixAction = Job.NoticeAction(title: L("engine.install_ffmpeg"), symbol: "arrow.down.circle") { [weak self] in
                        self?.run(FFmpegInstaller.preparing(option), mode: mode, files: files, anchor: anchor, destination: destination)
                    }
                    job.state = .failed(error.localizedDescription)
                } else {
                    job.state = .failed(error.localizedDescription)
                }
            }
            reveal.cancel()
            self?.finish(job)
        }
    }

    private func finish(_ job: Job) {
        if !job.started {
            // The editor was cancelled before anything happened: leave no trace.
            if job.state == .cancelled {
                jobs.removeAll { $0.id == job.id }
                return
            }
            job.started = true
        }
        Speech.announce(job.headline)
        switch job.state {
        case .succeeded:
            if job.silent {
                hud.remove(job)
                jobs.removeAll { $0.id == job.id }
                return
            }
            hud.show(job)
            hud.scheduleDismiss(job)
            if Preferences.shared.playSounds, !job.outputs.isEmpty { Sounds.success() }
        case .failed:
            hud.show(job)
            hud.scheduleDismiss(job)
            if Preferences.shared.playSounds { Sounds.failure() }
        case .cancelled:
            hud.scheduleDismiss(job)
        case .running:
            break
        }
    }

    func notice(_ title: String, detail: String, symbol: String, tone: Job.Tone = .warning, action: Job.NoticeAction? = nil,
                lingers: Double? = nil) {
        let job = Job(notice: title, detail: detail, symbol: symbol, tone: tone, action: action, lingers: lingers)
        hud.show(job)
        hud.scheduleDismiss(job)
    }
}

enum Sounds {
    static func success() {
        let sound = NSSound(named: "Pop")
        sound?.volume = 0.35
        sound?.play()
    }

    static func failure() {
        let sound = NSSound(named: "Funk")
        sound?.volume = 0.3
        sound?.play()
    }
}

/// SwiftUI buttons in a non-activating panel should respond to the very first click.
final class FirstClickHostingView<Content: View>: NSHostingView<Content> {
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
}

/// One borderless window per card, so every card gets the real macOS window shadow.
final class CardPanel: NSPanel {
    private var host: FirstClickHostingView<AnyView>?
    private var observation: AnyCancellable?

    @MainActor
    init(job: Job, hud: HUDController) {
        super.init(contentRect: CGRect(x: 0, y: 0, width: HUDController.width, height: 80),
                   styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        isOpaque = false
        backgroundColor = .clear
        hasShadow = true
        level = .statusBar
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .transient, .ignoresCycle]
        hidesOnDeactivate = false
        isReleasedWhenClosed = false
        animationBehavior = .none
        let card = JobCard(job: job,
                           onClose: { [weak hud] in hud?.remove(job) },
                           onHover: { [weak hud] inside in hud?.setHovered(job.id, inside) })
        let host = FirstClickHostingView(rootView: AnyView(card))
        contentView = host
        self.host = host
        observation = job.objectWillChange.sink { [weak hud] _ in
            Task { @MainActor in hud?.layout() }
        }
    }

    @MainActor
    func fittingHeight() -> CGFloat {
        host?.layoutSubtreeIfNeeded()
        return max(56, ceil(host?.fittingSize.height ?? 80))
    }

    override var canBecomeKey: Bool { false }
}

@MainActor
final class HUDController {
    static let width: CGFloat = 352
    static let gap: CGFloat = 10
    static let inset: CGFloat = 12

    private var entries: [(job: Job, panel: CardPanel)] = []
    private var timers: [UUID: Task<Void, Never>] = [:]
    private var hovered = Set<UUID>()
    /// Cards stay on the screen where the first one appeared until all of them are gone.
    private var screen: NSScreen?

    func show(_ job: Job) {
        if entries.contains(where: { $0.job.id == job.id }) {
            layout()
            return
        }
        if entries.isEmpty {
            let point = job.anchor == .zero ? NSEvent.mouseLocation : job.anchor
            screen = ScreenGeometry.screen(containing: point)
        }
        let panel = CardPanel(job: job, hud: self)
        entries.insert((job, panel), at: 0)
        while entries.count > 4, let last = entries.last { remove(last.job) }
        layout(appearing: panel)
    }

    func remove(_ job: Job) {
        timers[job.id]?.cancel()
        timers[job.id] = nil
        hovered.remove(job.id)
        guard let index = entries.firstIndex(where: { $0.job.id == job.id }) else { return }
        let panel = entries.remove(at: index).panel
        NSAnimationContext.runAnimationGroup({ context in
            context.duration = 0.18
            panel.animator().alphaValue = 0
        }, completionHandler: {
            panel.orderOut(nil)
        })
        layout()
    }

    func setHovered(_ id: UUID, _ inside: Bool) {
        if inside { hovered.insert(id) } else { hovered.remove(id) }
    }

    func scheduleDismiss(_ job: Job) {
        let seconds: Double
        if let lingers = job.lingers {
            seconds = lingers
        } else {
            switch job.state {
            case .failed: seconds = 8
            case .succeeded: seconds = job.outputs.isEmpty ? (job.isNotice ? 6 : 2.6) : 6
            default: seconds = 1.6
            }
        }
        timers[job.id]?.cancel()
        timers[job.id] = Task { @MainActor [weak self] in
            try? await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
            while let self, !self.hovered.isEmpty, !Task.isCancelled {
                try? await Task.sleep(nanoseconds: 400_000_000)
            }
            guard !Task.isCancelled else { return }
            self?.remove(job)
        }
    }

    func layout(appearing: CardPanel? = nil) {
        if let current = screen, !NSScreen.screens.contains(current) { screen = nil }
        guard let screen = screen ?? NSScreen.main else { return }
        let visible = screen.visibleFrame
        let corner = Preferences.shared.hudPosition
        var edge = corner.isTop ? visible.maxY - Self.inset : visible.minY + Self.inset
        let x = corner.isLeft ? visible.minX + Self.inset : visible.maxX - Self.width - Self.inset
        for entry in entries {
            let panel = entry.panel
            let height = panel.fittingHeight()
            let frame = CGRect(x: x, y: corner.isTop ? edge - height : edge, width: Self.width, height: height)
            if panel === appearing {
                panel.setFrame(frame.offsetBy(dx: 0, dy: corner.isTop ? 8 : -8), display: true)
                panel.alphaValue = 0
                panel.orderFrontRegardless()
                NSAnimationContext.runAnimationGroup { context in
                    context.duration = 0.22
                    context.timingFunction = CAMediaTimingFunction(name: .easeOut)
                    panel.animator().setFrame(frame, display: true)
                    panel.animator().alphaValue = 1
                }
            } else if panel.frame != frame {
                NSAnimationContext.runAnimationGroup { context in
                    context.duration = 0.2
                    panel.animator().setFrame(frame, display: true)
                }
            }
            edge = corner.isTop ? edge - height - Self.gap : edge + height + Self.gap
        }
        if entries.isEmpty { self.screen = nil }
        // The window shadow follows the rendered shape; refresh it once SwiftUI has drawn.
        Task { @MainActor [entries] in
            try? await Task.sleep(nanoseconds: 60_000_000)
            for entry in entries { entry.panel.invalidateShadow() }
            try? await Task.sleep(nanoseconds: 260_000_000)
            for entry in entries { entry.panel.invalidateShadow() }
        }
    }
}

struct JobCard: View {
    @ObservedObject var job: Job
    let onClose: () -> Void
    let onHover: (Bool) -> Void
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .top, spacing: 11) {
                JobBadge(job: job).frame(width: 34, height: 34)
                VStack(alignment: .leading, spacing: 3) {
                    Text(job.headline)
                        .font(.system(size: 13, weight: .semibold))
                        .lineLimit(1)
                    Text(job.subline)
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                        .lineLimit(3)
                        .truncationMode(.middle)
                        .fixedSize(horizontal: false, vertical: true)
                    if job.isRunning {
                        ProgressBar(value: job.progress)
                            .frame(height: 3)
                            .padding(.top, 5)
                    }
                }
                Spacer(minLength: 4)
                Button {
                    if job.isRunning { job.cancel() } else { onClose() }
                } label: {
                    Image(systemName: job.isRunning ? "stop.fill" : "xmark")
                        .font(.system(size: 9, weight: .semibold))
                        .foregroundStyle(.secondary)
                        .frame(width: 20, height: 20)
                        .background(Circle().fill(Color.primary.opacity(0.07)))
                }
                .buttonStyle(.plain)
                .help(job.isRunning ? L("card.stop") : L("card.close"))
            }
            if job.state == .succeeded, job.hasFiles {
                ViewThatFits(in: .horizontal) {
                    actionRow(compact: false)
                    actionRow(compact: true)
                }
            }
            if let action = job.noticeAction ?? (job.state == .running ? nil : job.fixAction) {
                HStack {
                    CardButton(title: action.title, symbol: action.symbol, prominent: true) {
                        action.run()
                        onClose()
                    }
                    Spacer(minLength: 0)
                }
            }
        }
        .padding(12)
        .frame(width: HUDController.width, alignment: .leading)
        .background(GlassSurface(shape: .rounded(14), material: .popover))
        .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous).strokeBorder(Theme.hairline(scheme), lineWidth: 0.5))
        .onHover(perform: onHover)
        .accessibilityElement(children: .contain)
        .accessibilityLabel(job.headline)
    }

    private func actionRow(compact: Bool) -> some View {
        HStack(spacing: 6) {
            if job.inCache {
                CardButton(title: L("card.save"), symbol: "square.and.arrow.down", prominent: true) { job.saveFromCache() }
            } else {
                CardButton(title: L("card.reveal"), symbol: "folder", prominent: true) { job.reveal() }
            }
            CardButton(title: L("card.open"), symbol: "arrow.up.forward.app", iconOnly: compact) { job.open() }
            if !job.copied {
                CardButton(title: L("card.copy"), symbol: "doc.on.clipboard", iconOnly: compact) { job.copyToClipboard() }
            }
            if job.undoable, !job.inCache {
                CardButton(title: L("card.undo"), symbol: "arrow.uturn.backward", iconOnly: compact) { job.undo() }
            }
            Spacer(minLength: 0)
        }
    }
}

struct JobBadge: View {
    @ObservedObject var job: Job

    var body: some View {
        ZStack {
            switch job.state {
            case .running:
                Circle().stroke(Color.primary.opacity(0.1), lineWidth: 2.5)
                Circle()
                    .trim(from: 0, to: max(0.04, job.progress ?? 0.25))
                    .stroke(Theme.accent, style: StrokeStyle(lineWidth: 2.5, lineCap: .round))
                    .rotationEffect(.degrees(-90))
                    .animation(.easeOut(duration: 0.25), value: job.progress)
                Image(systemName: job.symbol).font(.system(size: 12)).foregroundStyle(.secondary)
            case .succeeded:
                if let preview = job.preview, !job.undone {
                    Image(nsImage: preview).resizable().aspectRatio(contentMode: .fit)
                } else {
                    Circle().fill(Color.primary.opacity(0.07))
                    Image(systemName: job.isNotice ? job.symbol : (job.outputs.isEmpty ? "checkmark" : job.symbol))
                        .font(.system(size: 14, weight: .medium)).foregroundStyle(Theme.accent)
                }
            case .failed:
                Circle().fill(Color.orange.opacity(0.15))
                Image(systemName: job.isNotice ? job.symbol : "exclamationmark")
                    .font(.system(size: 14, weight: .semibold)).foregroundStyle(.orange)
            case .cancelled:
                Circle().fill(Color.primary.opacity(0.07))
                Image(systemName: "stop.fill").font(.system(size: 11)).foregroundStyle(.secondary)
            }
        }
    }
}

struct ProgressBar: View {
    let value: Double?
    @State private var phase: CGFloat = -0.4

    var body: some View {
        GeometryReader { proxy in
            ZStack(alignment: .leading) {
                Capsule().fill(Color.primary.opacity(0.1))
                if let value {
                    Capsule().fill(Theme.accent)
                        .frame(width: max(5, proxy.size.width * CGFloat(min(max(value, 0), 1))))
                        .animation(.easeOut(duration: 0.25), value: value)
                } else {
                    Capsule().fill(Theme.accent)
                        .frame(width: proxy.size.width * 0.3)
                        .offset(x: proxy.size.width * phase)
                        .onAppear {
                            withAnimation(.easeInOut(duration: 0.9).repeatForever(autoreverses: true)) { phase = 0.7 }
                        }
                }
            }
            .clipShape(Capsule())
        }
    }
}

struct CardButton: View {
    let title: String
    let symbol: String
    var prominent = false
    var iconOnly = false
    let action: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            HStack(spacing: 4) {
                Image(systemName: symbol).font(.system(size: 10, weight: .medium))
                if !iconOnly { Text(title).font(.system(size: 11, weight: .medium)) }
            }
            .lineLimit(1)
            .fixedSize()
            .foregroundStyle(prominent ? Color.white : Color.primary)
            .padding(.horizontal, iconOnly ? 7 : 9)
            .padding(.vertical, 4)
            .background(Capsule().fill(prominent ? Theme.accent.opacity(hovering ? 0.85 : 1)
                                                 : Color.primary.opacity(hovering ? 0.12 : 0.07)))
        }
        .buttonStyle(.plain)
        .help(title)
        .onHover { hovering = $0 }
    }
}
