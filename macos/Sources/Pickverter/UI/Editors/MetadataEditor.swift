import AppKit
import SwiftUI

@MainActor
enum MetadataEditor {
    static func edit(_ url: URL) async throws -> MetadataEdit {
        if let value: MetadataEdit = try EditorHost.injected("metadata") { return value }
        let snapshot = try await MetadataKit.read(url)
        guard snapshot.writable else { throw PickError(snapshot.reason ?? L("err.meta_readonly")) }
        let rows = CGFloat(snapshot.fields.count) * 36 + CGFloat(snapshot.info.count) * 26
        let height = min(720, 250 + rows + (snapshot.hasLocation ? 52 : 0))
        return try await EditorHost.run("metadata", title: L("editor.meta.title", url.lastPathComponent),
                                        size: CGSize(width: 540, height: height), minSize: CGSize(width: 460, height: 360)) { finish in
            MetadataEditorView(snapshot: snapshot, finish: finish)
        }
    }
}

struct MetadataEditorView: View {
    let snapshot: MetaSnapshot
    let finish: (MetadataEdit?) -> Void
    @State private var values: [String: String]
    @State private var removeLocation = false
    @State private var replace = false

    init(snapshot: MetaSnapshot, finish: @escaping (MetadataEdit?) -> Void) {
        self.snapshot = snapshot
        self.finish = finish
        _values = State(initialValue: Dictionary(uniqueKeysWithValues: snapshot.fields.map { ($0.key, $0.value) }))
    }

    private var changes: [String: String] {
        var changed: [String: String] = [:]
        for field in snapshot.fields where (values[field.key] ?? "") != field.value {
            changed[field.key] = values[field.key] ?? ""
        }
        return changed
    }

    var body: some View {
        VStack(spacing: 0) {
            Form {
                Section {
                    ForEach(snapshot.fields) { field in
                        TextField(MetadataKit.label(field.key, family: snapshot.family),
                                  text: Binding(get: { values[field.key] ?? "" }, set: { values[field.key] = $0 }),
                                  prompt: Text(MetadataKit.placeholder(field.key)))
                    }
                }
                if snapshot.hasLocation {
                    Section {
                        Toggle(L("meta.remove_location"), isOn: $removeLocation)
                    }
                }
                if !snapshot.info.isEmpty {
                    Section(L("meta.details")) {
                        ForEach(snapshot.info) { row in
                            LabeledContent(row.label) {
                                Text(row.value).foregroundStyle(.secondary).textSelection(.enabled).lineLimit(1).truncationMode(.middle)
                            }
                        }
                    }
                }
                Section {
                    Picker(L("meta.save_as"), selection: $replace) {
                        Text(L("meta.copy")).tag(false)
                        Text(L("meta.replace")).tag(true)
                    }
                    .pickerStyle(.radioGroup)
                }
            }
            .formStyle(.grouped)
            EditorFooter(primary: L("meta.save"), primaryEnabled: !changes.isEmpty || removeLocation,
                         onCancel: { finish(nil) },
                         onPrimary: { finish(MetadataEdit(fields: changes, removeLocation: removeLocation, replaceOriginal: replace)) })
        }
    }
}
