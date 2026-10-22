import AppKit
import PDFKit
import SwiftUI
import UniformTypeIdentifiers

@MainActor
final class PDFOrganizerModel: ObservableObject {
    struct Item: Identifiable, Equatable {
        let id = UUID()
        var file: Int
        var page: Int
        var rotation: Int
    }

    let documents: [PDFDocument]
    let names: [String]
    @Published var items: [Item]
    @Published var selection = Set<UUID>()
    @Published var dragging: UUID?
    private var anchor: UUID?
    private var cache: [String: NSImage] = [:]

    init(documents: [PDFDocument], names: [String]) {
        self.documents = documents
        self.names = names
        items = documents.enumerated().flatMap { file, doc in
            (0..<doc.pageCount).map { Item(file: file, page: $0, rotation: 0) }
        }
    }

    var multipleFiles: Bool { documents.count > 1 }

    func thumbnail(_ item: Item) -> NSImage {
        let key = "\(item.file)-\(item.page)"
        if let hit = cache[key] { return hit }
        let image = documents[item.file].page(at: item.page)?.thumbnail(of: CGSize(width: 260, height: 260), for: .cropBox) ?? NSImage()
        cache[key] = image
        return image
    }

    func label(_ item: Item, number: Int) -> String {
        guard multipleFiles else { return "\(number)" }
        return FileNames.split(names[item.file]).base + " · " + L("pages.page_short", "\(item.page + 1)")
    }

    private func index(_ id: UUID) -> Int? { items.firstIndex { $0.id == id } }

    func click(_ item: Item) {
        let flags = NSEvent.modifierFlags
        if flags.contains(.command) {
            if selection.contains(item.id) { selection.remove(item.id) } else { selection.insert(item.id) }
            anchor = item.id
        } else if flags.contains(.shift), let anchor, let a = index(anchor), let b = index(item.id) {
            selection = Set(items[min(a, b)...max(a, b)].map(\.id))
        } else {
            selection = [item.id]
            anchor = item.id
        }
    }

    func selectAll() { selection = Set(items.map(\.id)) }

    func rotate(_ degrees: Int) {
        for index in items.indices where selection.contains(items[index].id) {
            items[index].rotation = ((items[index].rotation + degrees) % 360 + 360) % 360
        }
    }

    func delete() {
        items.removeAll { selection.contains($0.id) }
        selection = []
    }

    func duplicate() {
        var copies = Set<UUID>()
        var index = 0
        while index < items.count {
            if selection.contains(items[index].id) {
                let copy = Item(file: items[index].file, page: items[index].page, rotation: items[index].rotation)
                items.insert(copy, at: index + 1)
                copies.insert(copy.id)
                index += 1
            }
            index += 1
        }
        selection = copies
    }

    /// Reverses the selected pages in place, or the whole document when one page or none is selected.
    func reverse() {
        let positions = selection.count > 1 ? items.indices.filter { selection.contains(items[$0].id) } : Array(items.indices)
        let reversed = positions.map { items[$0] }.reversed()
        for (position, item) in zip(positions, reversed) { items[position] = item }
    }

    func move(_ id: UUID, to target: UUID) {
        guard let from = index(id), let to = index(target), from != to else { return }
        let item = items.remove(at: from)
        items.insert(item, at: to)
    }

    func arrangement(selectedOnly: Bool) -> PDFArrangement {
        let chosen = selectedOnly ? items.filter { selection.contains($0.id) } : items
        return PDFArrangement(pages: chosen.map { PDFPageEntry(file: $0.file, page: $0.page, rotation: $0.rotation) })
    }
}

@MainActor
enum PDFOrganizer {
    static func arrange(_ files: [URL]) async throws -> PDFArrangement {
        if let value: PDFArrangement = try EditorHost.injected("pages") { return value }
        let docs = try files.map { try PDFEngine.open($0) }
        let title = files.count == 1 ? files[0].lastPathComponent : LP("wheel.items", files.count)
        return try await EditorHost.run("pages", title: L("editor.pages.title", title), size: CGSize(width: 880, height: 640),
                                        minSize: CGSize(width: 560, height: 420)) { finish in
            PDFOrganizerView(model: PDFOrganizerModel(documents: docs, names: files.map(\.lastPathComponent)), finish: finish)
        }
    }
}

struct PDFOrganizerView: View {
    @StateObject var model: PDFOrganizerModel
    let finish: (PDFArrangement?) -> Void

    init(model: PDFOrganizerModel, finish: @escaping (PDFArrangement?) -> Void) {
        _model = StateObject(wrappedValue: model)
        self.finish = finish
    }

    var body: some View {
        VStack(spacing: 0) {
            toolbar
            EditorDivider()
            ScrollView {
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 132, maximum: 172), spacing: 14)], spacing: 18) {
                    ForEach(Array(model.items.enumerated()), id: \.element.id) { index, item in
                        PageTile(model: model, item: item, number: index + 1)
                            .onTapGesture { model.click(item) }
                            .onDrag {
                                model.dragging = item.id
                                return NSItemProvider(object: item.id.uuidString as NSString)
                            }
                            .onDrop(of: [UTType.text], delegate: PageDropDelegate(target: item.id, model: model))
                    }
                }
                .padding(18)
                .animation(.easeOut(duration: 0.15), value: model.items)
            }
            .background(Color(nsColor: .underPageBackgroundColor))
            .onDrop(of: [UTType.text], isTargeted: nil) { _ in
                model.dragging = nil
                return true
            }
            EditorFooter(primary: L("pages.save"), primaryEnabled: !model.items.isEmpty,
                         onCancel: { finish(nil) }, onPrimary: { finish(model.arrangement(selectedOnly: false)) }) {
                Text(status).font(.system(size: 11)).foregroundStyle(.secondary).lineLimit(1)
                Button(L("pages.save_selected")) { finish(model.arrangement(selectedOnly: true)) }
                    .disabled(model.selection.isEmpty)
            }
        }
    }

    private var status: String {
        let pages = LP("pages.count", model.items.count)
        return model.selection.isEmpty ? pages : pages + " · " + LP("pages.selected", model.selection.count)
    }

    private var toolbar: some View {
        HStack(spacing: 6) {
            ToolbarButton(symbol: "rotate.left", title: L("rot.left")) { model.rotate(-90) }
                .keyboardShortcut("l", modifiers: .command)
            ToolbarButton(symbol: "rotate.right", title: L("rot.right")) { model.rotate(90) }
                .keyboardShortcut("r", modifiers: .command)
            ToolbarButton(symbol: "plus.square.on.square", title: L("pages.duplicate")) { model.duplicate() }
                .keyboardShortcut("d", modifiers: .command)
            ToolbarButton(symbol: "trash", title: L("pages.delete")) { model.delete() }
                .keyboardShortcut(.delete, modifiers: [])
            Divider().frame(height: 18).padding(.horizontal, 4)
            ToolbarButton(symbol: "arrow.up.arrow.down", title: L("pages.reverse"), enabled: true) { model.reverse() }
            Spacer()
            Button(L("pages.select_all")) { model.selectAll() }
                .keyboardShortcut("a", modifiers: .command)
            Text(L("pages.hint")).font(.system(size: 11)).foregroundStyle(.secondary).lineLimit(1)
        }
        .environment(\.selectionActive, !model.selection.isEmpty)
        .padding(.horizontal, 14)
        .padding(.vertical, 8)
        .background(Color(nsColor: .windowBackgroundColor))
    }
}

private struct SelectionActiveKey: EnvironmentKey { static let defaultValue = true }

extension EnvironmentValues {
    var selectionActive: Bool {
        get { self[SelectionActiveKey.self] }
        set { self[SelectionActiveKey.self] = newValue }
    }
}

struct ToolbarButton: View {
    let symbol: String
    let title: String
    var enabled: Bool? = nil
    let action: () -> Void
    @Environment(\.selectionActive) private var active

    var body: some View {
        Button(action: action) {
            Image(systemName: symbol).frame(width: 22, height: 18)
        }
        .help(title)
        .accessibilityLabel(title)
        .disabled(!(enabled ?? active))
    }
}

struct PageTile: View {
    @ObservedObject var model: PDFOrganizerModel
    let item: PDFOrganizerModel.Item
    let number: Int

    var body: some View {
        let selected = model.selection.contains(item.id)
        VStack(spacing: 6) {
            Image(nsImage: model.thumbnail(item))
                .resizable()
                .scaledToFit()
                .frame(maxWidth: 118, maxHeight: 118)
                .overlay(Rectangle().strokeBorder(Color.black.opacity(0.14), lineWidth: 0.5))
                .rotationEffect(.degrees(Double(item.rotation)))
                .frame(width: 118, height: 118)
                .padding(7)
                .background(RoundedRectangle(cornerRadius: 9, style: .continuous).fill(selected ? Theme.accent.opacity(0.16) : .clear))
                .overlay(RoundedRectangle(cornerRadius: 9, style: .continuous)
                    .strokeBorder(selected ? Theme.accent : Color.clear, lineWidth: 2))
            Text(model.label(item, number: number))
                .font(.system(size: 11, weight: selected ? .semibold : .regular).monospacedDigit())
                .foregroundStyle(selected ? Theme.accent : Color.secondary)
                .lineLimit(1)
                .truncationMode(.middle)
        }
        .opacity(model.dragging == item.id ? 0.35 : 1)
        .contentShape(Rectangle())
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(L("pages.page_short", "\(number)"))
        .accessibilityAddTraits(selected ? [.isSelected, .isButton] : .isButton)
    }
}

struct PageDropDelegate: DropDelegate {
    let target: UUID
    let model: PDFOrganizerModel

    func dropEntered(info: DropInfo) {
        guard let dragging = model.dragging, dragging != target else { return }
        model.move(dragging, to: target)
    }

    func dropUpdated(info: DropInfo) -> DropProposal? { DropProposal(operation: .move) }

    func performDrop(info: DropInfo) -> Bool {
        model.dragging = nil
        return true
    }
}
