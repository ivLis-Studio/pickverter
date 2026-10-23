import AppKit
import Combine
import SwiftUI

/// Settings › Wheel: one ring for one file type as the wheel shows it, and everything that can go in or out.
@MainActor
final class WheelLayoutEditor: ObservableObject {
    /// The file types Settings offers, in menu order.
    static let kinds: [FileKind] = [.image, .video, .audio, .pdf, .document, .hwp, .office, .table, .json, .subtitle,
                                    .archive, .folder, .other]

    enum Origin: Equatable {
        case native
        /// An option of a slot in this ring, pinned as a slot of its own.
        case pinned(parent: String)
        /// Brought in from another ring.
        case added(WheelMode)
    }

    struct Row: Identifiable {
        let option: PickOption
        let title: String
        let origin: Origin
        var id: String { option.id }
    }

    /// One entry of Add Slot; children are a slot's options (a submenu).
    struct Candidate: Identifiable {
        let id: String
        let title: String
        let symbol: String
        var children: [Candidate] = []
    }

    struct RingCandidates: Identifiable {
        let mode: WheelMode
        let items: [Candidate]
        var id: Int { mode.rawValue }
    }

    @Published var mode: WheelMode = .convert {
        didSet { if mode != oldValue { selection = nil; reload() } }
    }
    @Published var kind: FileKind = .image {
        didSet { if kind != oldValue { selection = nil; reload() } }
    }
    @Published var selection: String? {
        didSet { preview.previewHighlight(rows.firstIndex { $0.id == selection }) }
    }
    @Published private(set) var rows: [Row] = []
    @Published private(set) var removedCandidates: [Candidate] = []
    @Published private(set) var optionCandidates: [Candidate] = []
    @Published private(set) var ringCandidates: [RingCandidates] = []
    /// The real wheel, showing this ring with the user's layout.
    let preview = WheelController(demo: true, showsLayout: true)
    private var native: [WheelMode: [PickOption]] = [:]
    private var observers: Set<AnyCancellable> = []

    var key: String { WheelLayout.key(mode: mode, kind: kind) }
    private var layout: WheelLayout { WheelLayout.shared }
    var canAdd: Bool { !removedCandidates.isEmpty || !optionCandidates.isEmpty || !ringCandidates.isEmpty }

    init() {
        // objectWillChange fires before the change lands; one pass of the run loop later it has, and a burst of
        // changes (Reset) rebuilds once.
        WheelLayout.shared.objectWillChange
            .debounce(for: .zero, scheduler: RunLoop.main)
            .sink { [weak self] _ in self?.reload() }
            .store(in: &observers)
        SettingsRouter.shared.$layoutRequest
            .compactMap { $0 }
            .sink { [weak self] request in self?.open(request) }
            .store(in: &observers)
        reload()
    }

    /// "Edit Wheel Layout…" in the wheel: the ring and file type it was opened on.
    private func open(_ request: SettingsRouter.LayoutRequest) {
        if let kind = request.kind, Self.kinds.contains(kind) { self.kind = kind }
        mode = request.mode
    }

    static func title(of kind: FileKind) -> String {
        switch kind {
        case .image: return L("kind.image")
        case .video: return L("kind.video")
        case .audio: return L("kind.audio")
        case .pdf: return L("kind.pdf")
        case .document: return L("kind.document")
        case .hwp: return L("kind.hwp")
        case .office: return L("kind.office")
        case .table: return L("kind.table")
        case .json: return L("kind.json")
        case .subtitle: return L("kind.subtitle")
        case .archive: return L("kind.archive")
        case .folder: return L("kind.folder")
        case .other: return L("kind.other")
        }
    }

    /// Files that stand for a file type. They needn't exist (the catalog goes by extension), except the folder.
    static func samples(for kind: FileKind) -> [URL] {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("Pickverter Layout Preview", isDirectory: true)
        func file(_ name: String) -> URL { folder.appendingPathComponent(name) }
        switch kind {
        case .image: return [file("Photo.jpg"), file("Animation.gif")]
        case .video: return [file("Clip.mp4")]
        case .audio: return [file("Sound.mp3")]
        case .pdf: return [file("Document.pdf")]
        case .document: return [file("Notes.docx")]
        case .hwp: return [file("Report.hwp"), file("Report.hwpx")]
        case .office: return ["xlsx", "pptx", "pages", "numbers", "key", "ods", "odp"].map { file("Office." + $0) }
        case .table: return [file("Data.csv")]
        case .json: return [file("Data.json")]
        case .subtitle: return [file("Captions.srt")]
        case .archive: return [file("Files.zip")]
        case .folder:
            let directory = folder.appendingPathComponent("Folder", isDirectory: true)
            try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            return [directory]
        case .other: return [file("File.bin")]
        }
    }

    /// Every slot the ring can have for this file type: the catalog's options for each sample, alone and with a
    /// second file (Collage, Merge and Join only come with several), by id, in wheel order. A slot only some
    /// selections have goes right after the one before it there, or last.
    static func nativeOptions(_ kind: FileKind, _ mode: WheelMode) -> [PickOption] {
        var result: [PickOption] = []
        for sample in samples(for: kind) {
            for selection in [[sample], [sample, sibling(of: sample)]] {
                var previous: String?
                for option in Catalog.options(for: selection, mode: mode) {
                    if !result.contains(where: { $0.id == option.id }) {
                        let after = previous.flatMap { id in result.firstIndex { $0.id == id } }
                        result.insert(option, at: after.map { $0 + 1 } ?? result.count)
                    }
                    previous = option.id
                }
            }
        }
        return result
    }

    /// "Photo 2.jpg" next to "Photo.jpg" (a real folder for a folder).
    private static func sibling(of url: URL) -> URL {
        let ext = url.pathExtension
        let name = url.deletingPathExtension().lastPathComponent + " 2" + (ext.isEmpty ? "" : "." + ext)
        let sibling = url.deletingLastPathComponent().appendingPathComponent(name, isDirectory: url.hasDirectoryPath)
        if url.hasDirectoryPath { try? FileManager.default.createDirectory(at: sibling, withIntermediateDirectories: true) }
        return sibling
    }

    func reload() {
        native = Dictionary(uniqueKeysWithValues: WheelMode.allCases.map { ($0, Self.nativeOptions(kind, $0)) })
        let ring = native[mode] ?? []
        let pool = WheelMode.allCases.filter { $0 != mode }.flatMap { native[$0] ?? [] }
        let arranged = layout.arrange(ring, key: key, pool: pool)
        rows = arranged.map(row)
        collectCandidates(ring: ring, shown: Set(arranged.map(\.id)))
        let source = native
        preview.optionSource = { source[$0] ?? [] }
        preview.preview(files: Array(Self.samples(for: kind).prefix(1)), mode: mode)
        if let selection, !rows.contains(where: { $0.id == selection }) { self.selection = nil }
        preview.previewHighlight(rows.firstIndex { $0.id == selection })
    }

    // MARK: Editing

    func move(from offsets: IndexSet, to destination: Int) {
        var ids = rows.map(\.id)
        ids.move(fromOffsets: offsets, toOffset: destination)
        layout.setOrder(ids, key: key)
    }

    func move(_ row: Row, by step: Int) {
        guard let from = rows.firstIndex(where: { $0.id == row.id }) else { return }
        let to = min(max(from + step, 0), rows.count - 1)
        guard to != from else { return }
        var ids = rows.map(\.id)
        ids.swapAt(from, to)
        layout.setOrder(ids, key: key)
    }

    func remove(_ row: Row) {
        if selection == row.id { selection = nil }
        layout.remove(row.id, key: key)
    }

    func restore(_ candidate: Candidate) { layout.restore(candidate.id, key: key) }

    func pin(_ candidate: Candidate) { layout.pin(candidate.id, title: candidate.title, key: key) }

    func addFromRing(_ candidate: Candidate) { layout.addFromRing(candidate.id, key: key) }

    func reset() { layout.reset(key: key) }

    func applyToAllKinds() { layout.applyToAllKinds(from: key, kinds: Self.kinds) }

    // MARK: Building

    /// "PNG · 150 dpi" for an option of a slot, the slot's own title otherwise.
    private func title(of id: String, in options: [PickOption]) -> String {
        if let option = options.first(where: { $0.id == id }) { return option.title }
        for parent in options {
            if let variant = parent.variants.first(where: { $0.id == id }) { return "\(parent.title) · \(variant.title)" }
        }
        return id
    }

    private func ring(holding id: String) -> WheelMode? {
        WheelMode.allCases.first { mode in
            (native[mode] ?? []).contains { $0.id == id || $0.variants.contains { $0.id == id } }
        }
    }

    private func row(_ option: PickOption) -> Row {
        let all = WheelMode.allCases.flatMap { native[$0] ?? [] }
        if layout.isAdded(option.id, key: key), let source = ring(holding: option.id) {
            return Row(option: option, title: title(of: option.id, in: all), origin: .added(source))
        }
        if let parentID = option.pinnedFrom, let parent = (native[mode] ?? []).first(where: { $0.id == parentID }) {
            return Row(option: option, title: title(of: option.id, in: [parent]), origin: .pinned(parent: parent.title))
        }
        return Row(option: option, title: option.title, origin: .native)
    }

    private static func symbol(_ option: PickOption, parent: PickOption? = nil) -> String {
        if !option.symbol.isEmpty { return option.symbol }
        if let parent, !parent.symbol.isEmpty { return parent.symbol }
        return "circle.dashed"
    }

    private func collectCandidates(ring: [PickOption], shown: Set<String>) {
        let all = WheelMode.allCases.flatMap { native[$0] ?? [] }
        let removed = (layout.removed[key] ?? []).filter { !shown.contains($0) }
        removedCandidates = removed.compactMap { id in
            WheelLayout.find(id, in: all).map { Candidate(id: id, title: title(of: id, in: all), symbol: Self.symbol($0)) }
        }
        optionCandidates = ring.filter { $0.variants.count > 1 && !removed.contains($0.id) }.compactMap { parent in
            let variants = parent.variants.filter { !shown.contains($0.id) }
            guard !variants.isEmpty else { return nil }
            return Candidate(id: parent.id, title: parent.title, symbol: Self.symbol(parent),
                             children: variants.map { Candidate(id: $0.id, title: "\(parent.title) · \($0.title)",
                                                                symbol: Self.symbol($0, parent: parent)) })
        }
        let skip = shown.union(removed)
        ringCandidates = WheelMode.allCases.filter { $0 != mode }.compactMap { other in
            let items: [Candidate] = (native[other] ?? []).compactMap { option in
                let variants = option.variants.count > 1 ? option.variants.filter { !skip.contains($0.id) } : []
                guard !skip.contains(option.id) || !variants.isEmpty else { return nil }
                var candidate = Candidate(id: option.id, title: option.title, symbol: Self.symbol(option))
                if !variants.isEmpty {
                    let own = skip.contains(option.id) ? [] : [candidate]
                    candidate.children = own + variants.map {
                        Candidate(id: $0.id, title: "\(option.title) · \($0.title)", symbol: Self.symbol($0, parent: option))
                    }
                }
                return candidate
            }
            return items.isEmpty ? nil : RingCandidates(mode: other, items: items)
        }
    }
}

// MARK: - Views

/// The editor at the top of Settings › Wheel.
struct WheelLayoutSection: View {
    @ObservedObject var editor: WheelLayoutEditor
    @ObservedObject private var layout = WheelLayout.shared
    @State private var confirmApply = false

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(L("set.layout")).font(.headline)
            HStack(spacing: 10) {
                Picker(L("set.layout.ring"), selection: $editor.mode) {
                    ForEach(WheelMode.allCases) { mode in Text("\(mode.keyGlyph) \(mode.title)").tag(mode) }
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .fixedSize()
                Spacer(minLength: 8)
                Picker(L("set.layout.kind"), selection: $editor.kind) {
                    ForEach(WheelLayoutEditor.kinds, id: \.self) { kind in
                        Text(WheelLayoutEditor.title(of: kind)).tag(kind)
                    }
                }
                .labelsHidden()
                .fixedSize()
                .help(L("set.layout.kind"))
            }
            HStack(alignment: .top, spacing: 16) {
                VStack(spacing: 6) {
                    LayoutPreview(model: editor.preview)
                    Text(L("set.layout.hint"))
                    Text(L("set.layout.note")).foregroundStyle(.tertiary)
                }
                .font(.caption)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
                .frame(width: 214)
                VStack(alignment: .leading, spacing: 8) {
                    slots
                    HStack(spacing: 8) {
                        addMenu
                        Spacer()
                        moreMenu
                    }
                }
            }
        }
        .alert(L("set.layout.apply_all.q", editor.mode.title), isPresented: $confirmApply) {
            Button(L("set.layout.apply")) { editor.applyToAllKinds() }
            Button(L("common.cancel"), role: .cancel) {}
        } message: {
            Text(L("set.layout.apply_all.d", editor.mode.title))
        }
    }

    private var slots: some View {
        List(selection: $editor.selection) {
            ForEach(Array(editor.rows.enumerated()), id: \.element.id) { index, row in
                SlotRow(row: row, index: index, count: editor.rows.count, editor: editor)
                    .tag(row.id)
            }
            .onMove { editor.move(from: $0, to: $1) }
        }
        .listStyle(.bordered(alternatesRowBackgrounds: true))
        .frame(minHeight: 230, maxHeight: .infinity)
        .overlay {
            if editor.rows.isEmpty {
                Text(L("set.layout.empty"))
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                    .padding(24)
            }
        }
    }

    private var addMenu: some View {
        Menu {
            if !editor.removedCandidates.isEmpty {
                Section(L("set.layout.add.removed")) {
                    ForEach(editor.removedCandidates) { candidate in
                        Button { editor.restore(candidate) } label: { Label(candidate.title, systemImage: candidate.symbol) }
                    }
                }
            }
            if !editor.optionCandidates.isEmpty {
                Section(L("set.layout.add.options")) {
                    ForEach(editor.optionCandidates) { parent in
                        Menu {
                            ForEach(parent.children) { option in Button(option.title) { editor.pin(option) } }
                        } label: {
                            Label(parent.title, systemImage: parent.symbol)
                        }
                    }
                }
            }
            ForEach(editor.ringCandidates) { ring in
                Section(L("set.layout.add.from", ring.mode.title)) {
                    ForEach(ring.items) { candidate in
                        if candidate.children.isEmpty {
                            Button { editor.addFromRing(candidate) } label: { Label(candidate.title, systemImage: candidate.symbol) }
                        } else {
                            Menu {
                                ForEach(candidate.children) { option in Button(option.title) { editor.addFromRing(option) } }
                            } label: {
                                Label(candidate.title, systemImage: candidate.symbol)
                            }
                        }
                    }
                }
            }
            if !editor.canAdd {
                Text(L("set.layout.add.none"))
            }
        } label: {
            Label(L("set.layout.add"), systemImage: "plus")
        }
        .fixedSize()
    }

    private var moreMenu: some View {
        Menu {
            Button(L("ctx.reset_ring")) { editor.reset() }
                .disabled(!layout.isCustomized(editor.key))
            Button(L("set.layout.apply_all")) { confirmApply = true }
        } label: {
            Image(systemName: "ellipsis.circle")
        }
        .menuStyle(.borderlessButton)
        .menuIndicator(.hidden)
        .fixedSize()
        .accessibilityLabel(L("set.layout"))
    }
}

/// One slot in the list: what it is, which option a click runs, and a way out.
private struct SlotRow: View {
    let row: WheelLayoutEditor.Row
    let index: Int
    let count: Int
    @ObservedObject var editor: WheelLayoutEditor
    @ObservedObject private var layout = WheelLayout.shared

    /// The wheel shows ten slots; past that each page holds nine and a More slot.
    private var pageStart: Int? {
        let perPage = WheelController.maxPerPage - 1
        guard count > WheelController.maxPerPage, index > 0, index % perPage == 0 else { return nil }
        return index / perPage + 1
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 3) {
            if let page = pageStart {
                Text(L("set.layout.page", "\(page)"))
                    .font(.caption2.weight(.semibold))
                    .foregroundStyle(.secondary)
            }
            HStack(spacing: 8) {
                Image(systemName: row.option.symbol.isEmpty ? "circle.dashed" : row.option.symbol)
                    .foregroundStyle(.secondary)
                    .frame(width: 18)
                Text(row.title)
                    .lineLimit(1)
                    .truncationMode(.middle)
                badge
                Spacer(minLength: 4)
                if row.option.variants.count > 1 {
                    // Which option a plain click runs: quiet text until it's wanted.
                    Menu {
                        Picker(L("ctx.default"), selection: defaultBinding) {
                            Text(L("ctx.default_auto")).tag(String?.none)
                            ForEach(row.option.variants) { variant in Text(variant.title).tag(String?.some(variant.id)) }
                        }
                        .pickerStyle(.inline)
                        .labelsHidden()
                    } label: {
                        Text(defaultTitle)
                    }
                    .menuStyle(.borderlessButton)
                    .fixedSize()
                    .foregroundStyle(.secondary)
                    .help(L("ctx.default"))
                }
                Button { editor.remove(row) } label: {
                    Image(systemName: "minus.circle.fill").foregroundStyle(.secondary)
                }
                .buttonStyle(.borderless)
                .help(L("set.layout.remove"))
                .accessibilityLabel(L("set.layout.remove"))
            }
        }
        .padding(.vertical, 1)
        .contextMenu {
            Button(L("ctx.move_earlier")) { editor.move(row, by: -1) }.disabled(index == 0)
            Button(L("ctx.move_later")) { editor.move(row, by: 1) }.disabled(index == count - 1)
            Divider()
            Button(L("set.layout.remove")) { editor.remove(row) }
        }
    }

    @ViewBuilder private var badge: some View {
        switch row.origin {
        case .native:
            EmptyView()
        case .pinned(let parent):
            Image(systemName: "pin.fill")
                .font(.caption2)
                .foregroundStyle(.tertiary)
                .help(L("set.layout.pinned_from", parent))
        case .added(let source):
            Text("\(source.keyGlyph) \(source.title)")
                .font(.caption2)
                .foregroundStyle(.secondary)
                .padding(.horizontal, 5)
                .padding(.vertical, 1)
                .background(Capsule().fill(.quaternary))
                .help(L("set.layout.add.from", source.title))
        }
    }

    private var defaultBinding: Binding<String?> {
        Binding(get: { layout.defaults[row.id] }, set: { layout.setDefault(slot: row.id, variant: $0) })
    }

    private var defaultTitle: String {
        guard let id = layout.defaults[row.id], let variant = row.option.variants.first(where: { $0.id == id }) else {
            return L("set.layout.auto")
        }
        return variant.title
    }
}

/// The wheel at 60 %: the disc and its ring tabs, cut out of the full window-sized view.
private struct LayoutPreview: View {
    @ObservedObject var model: WheelController
    private let scale: CGFloat = 0.6

    var body: some View {
        let g = model.geometry
        let side = g.windowSize.width
        let top = g.discRadius + 8
        let bottom = g.tabBarOffset + g.tabBarHeight / 2 + 8
        let width = top * 2 * scale
        let height = (top + bottom) * scale
        WheelView(model: model)
            .environment(\.glassWithinWindow, true)
            .scaleEffect(scale)
            .frame(width: side * scale, height: side * scale)
            .offset(y: top * scale - height / 2)
            .frame(width: width, height: height)
            .clipped()
            .allowsHitTesting(false)
            .padding(.vertical, 6)
            .frame(maxWidth: .infinity)
            .background(RoundedRectangle(cornerRadius: 10).fill(Color(nsColor: .underPageBackgroundColor)))
            .accessibilityHidden(true)
    }
}
