import AppKit

/// The user's own wheel: hidden slots, custom order, variants pinned to the main ring and which
/// option a slot runs by default. Order, pins, removed slots and slots brought in from the other rings are kept per
/// ring and file type (key "1|image": WheelMode raw value and FileKind).
@MainActor
final class WheelLayout: ObservableObject {
    private(set) static var shared = WheelLayout()
    private let store: UserDefaults

    /// Snapshots: a layout kept in its own defaults suite, so the user's wheel stays as it is.
    static func useScratch(_ store: UserDefaults) { shared = WheelLayout(store: store) }

    /// id -> title at the time it was hidden, in every ring (from earlier versions; slots now leave one ring at a time).
    @Published private(set) var hidden: [String: String]
    @Published private(set) var orders: [String: [String]]
    @Published private(set) var pinned: [String: [String]]
    @Published private(set) var pinnedTitles: [String: String]
    /// key -> slots taken out of that ring for that file type.
    @Published private(set) var removed: [String: [String]]
    /// key -> slots (or options of slots) brought in from the other two rings, in the order they were added.
    @Published private(set) var added: [String: [String]]
    /// slot id -> variant id chosen with "Use as default".
    @Published private(set) var defaults: [String: String]
    @Published private(set) var lastUsed: [String: String]
    @Published var remembersLast: Bool { didSet { store.set(remembersLast, forKey: "wheel.remember") } }

    init(store: UserDefaults = .standard) {
        self.store = store
        store.register(defaults: ["wheel.remember": true])
        hidden = store.dictionary(forKey: "wheel.hidden") as? [String: String] ?? [:]
        orders = store.dictionary(forKey: "wheel.orders") as? [String: [String]] ?? [:]
        pinned = store.dictionary(forKey: "wheel.pinned") as? [String: [String]] ?? [:]
        pinnedTitles = store.dictionary(forKey: "wheel.pinnedTitles") as? [String: String] ?? [:]
        removed = store.dictionary(forKey: "wheel.removed") as? [String: [String]] ?? [:]
        added = store.dictionary(forKey: "wheel.added") as? [String: [String]] ?? [:]
        defaults = store.dictionary(forKey: "wheel.defaults") as? [String: String] ?? [:]
        lastUsed = store.dictionary(forKey: "wheel.lastUsed") as? [String: String] ?? [:]
        remembersLast = store.bool(forKey: "wheel.remember")
    }

    private func save() {
        store.set(hidden, forKey: "wheel.hidden")
        store.set(orders, forKey: "wheel.orders")
        store.set(pinned, forKey: "wheel.pinned")
        store.set(pinnedTitles, forKey: "wheel.pinnedTitles")
        store.set(removed, forKey: "wheel.removed")
        store.set(added, forKey: "wheel.added")
        store.set(defaults, forKey: "wheel.defaults")
        store.set(lastUsed, forKey: "wheel.lastUsed")
    }

    static func key(mode: WheelMode, files: [URL]) -> String {
        let kinds = Set(files.map { FileKind.of($0).rawValue }).sorted().joined(separator: "+")
        return "\(mode.rawValue)|\(kinds)"
    }

    static func key(mode: WheelMode, kind: FileKind) -> String { "\(mode.rawValue)|\(kind.rawValue)" }

    var isCustomized: Bool {
        !hidden.isEmpty || !orders.isEmpty || !pinned.isEmpty || !defaults.isEmpty || !removed.isEmpty || !added.isEmpty
    }

    func isCustomized(_ key: String) -> Bool { orders[key] != nil || pinned[key] != nil || removed[key] != nil || added[key] != nil }

    /// Localized name for an id when one exists, so lists in Settings follow the UI language.
    func title(for id: String, fallback: String?) -> String {
        if id.hasPrefix("convert.") { return Catalog.formatLabel(String(id.dropFirst("convert.".count))) }
        if Loc.hasKey(id) { return L(id) }
        return fallback ?? id
    }

    // MARK: Applying

    /// The ring as the wheel shows it. pool: the other two rings' options for the same files, where slots brought in
    /// from them come from.
    func arrange(_ options: [PickOption], key: String, pool: [PickOption] = []) -> [PickOption] {
        let pins = pinned[key] ?? []
        var list: [PickOption] = []
        for option in options {
            list.append(option)
            for variant in option.variants where pins.contains(variant.id) {
                var promoted = WheelController.display(variant, parent: option)
                promoted.pinnedFrom = option.id
                list.append(promoted)
            }
        }
        for id in added[key] ?? [] where !list.contains(where: { $0.id == id }) {
            if let option = Self.find(id, in: pool) { list.append(option) }
        }
        let out = Set(removed[key] ?? [])
        list = list.filter { hidden[$0.id] == nil && !out.contains($0.id) }
        guard let order = orders[key], !order.isEmpty else { return list }
        var rank: [String: Int] = [:]
        for (index, id) in order.enumerated() where rank[id] == nil { rank[id] = index }
        var result = list.filter { rank[$0.id] != nil }.sorted { rank[$0.id]! < rank[$1.id]! }
        // Slots the saved order doesn't know yet (new pins, new app features) go right after their
        // natural neighbour instead of piling up at the end.
        for (index, option) in list.enumerated() where rank[option.id] == nil {
            let neighbour = list[..<index].last { candidate in result.contains { $0.id == candidate.id } }
            let position = neighbour.flatMap { n in result.firstIndex { $0.id == n.id } }.map { $0 + 1 } ?? 0
            result.insert(option, at: position)
        }
        return result
    }

    /// A slot as it is, or one of a slot's options shown as a slot of its own ("Rotate · Left").
    static func find(_ id: String, in options: [PickOption]) -> PickOption? {
        if let option = options.first(where: { $0.id == id }) { return option }
        for parent in options {
            if let variant = parent.variants.first(where: { $0.id == id }) { return WheelController.display(variant, parent: parent) }
        }
        return nil
    }

    /// Index of the variant a click on the slot runs.
    func preferredVariant(of option: PickOption) -> Int {
        guard !option.variants.isEmpty else { return 0 }
        if let id = defaults[option.id], let index = option.variants.firstIndex(where: { $0.id == id }) { return index }
        if remembersLast, let id = lastUsed[option.id], let index = option.variants.firstIndex(where: { $0.id == id }) { return index }
        return 0
    }

    func noteUsed(slot: String, variant: String) {
        guard remembersLast, lastUsed[slot] != variant else { return }
        lastUsed[slot] = variant
        save()
    }

    // MARK: Editing

    func hide(_ id: String, title: String) {
        hidden[id] = title
        save()
    }

    func unhide(_ id: String) {
        hidden[id] = nil
        save()
    }

    func isAdded(_ id: String, key: String) -> Bool { added[key]?.contains(id) == true }

    /// Takes a slot out of one ring for one file type: a slot brought in from another ring goes back, a pinned option
    /// is unpinned, and any other slot is set aside ("Add Slot" in Settings brings it back).
    func remove(_ id: String, key: String) {
        if var list = added[key], let index = list.firstIndex(of: id) {
            list.remove(at: index)
            added[key] = list.isEmpty ? nil : list
            save()
        } else if isPinned(id, key: key) {
            unpin(id, key: key)
        } else {
            var list = removed[key] ?? []
            guard !list.contains(id) else { return }
            list.append(id)
            removed[key] = list
            save()
        }
    }

    /// Puts a removed slot back.
    func restore(_ id: String, key: String) {
        guard var list = removed[key], let index = list.firstIndex(of: id) else { return }
        list.remove(at: index)
        removed[key] = list.isEmpty ? nil : list
        save()
    }

    /// Brings a slot, or one of its options, from another ring into this one (the other ring keeps it too).
    func addFromRing(_ id: String, key: String) {
        if var list = removed[key], let index = list.firstIndex(of: id) {
            list.remove(at: index)
            removed[key] = list.isEmpty ? nil : list
        }
        var list = added[key] ?? []
        if !list.contains(id) { list.append(id) }
        added[key] = list
        save()
    }

    /// The whole ring in a new order (ids as the wheel shows them).
    func setOrder(_ ids: [String], key: String) {
        orders[key] = ids
        save()
    }

    func setDefault(slot: String, variant: String?) {
        defaults[slot] = variant
        save()
    }

    func pin(_ variant: String, title: String, key: String) {
        var list = pinned[key] ?? []
        guard !list.contains(variant) else { return }
        list.append(variant)
        pinned[key] = list
        pinnedTitles[variant] = title
        save()
    }

    func unpin(_ variant: String, key: String? = nil) {
        for ring in pinned.keys where key == nil || ring == key {
            pinned[ring]?.removeAll { $0 == variant }
            if pinned[ring]?.isEmpty == true { pinned[ring] = nil }
        }
        if !pinned.values.contains(where: { $0.contains(variant) }) { pinnedTitles[variant] = nil }
        save()
    }

    func isPinned(_ variant: String, key: String) -> Bool { pinned[key]?.contains(variant) == true }

    /// Moves one slot left or right within the ring as it is currently shown.
    func move(_ id: String, by step: Int, visible: [String], key: String) {
        var ids = visible
        guard let from = ids.firstIndex(of: id) else { return }
        let to = min(max(from + step, 0), ids.count - 1)
        guard to != from else { return }
        ids.swapAt(from, to)
        orders[key] = ids
        save()
    }

    func reset(key: String) {
        orders[key] = nil
        for variant in pinned[key] ?? [] { unpin(variant, key: key) }
        pinned[key] = nil
        removed[key] = nil
        added[key] = nil
        save()
    }

    /// Gives the same ring of every other file type in kinds this ring's order, pins, removed and added slots. Ids a
    /// file type doesn't have are simply skipped when its wheel opens.
    func applyToAllKinds(from key: String, kinds: [FileKind]) {
        guard let ring = key.split(separator: "|").first.map(String.init) else { return }
        for kind in kinds {
            let other = "\(ring)|\(kind.rawValue)"
            guard other != key else { continue }
            orders[other] = orders[key]
            pinned[other] = pinned[key]
            removed[other] = removed[key]
            added[other] = added[key]
        }
        save()
    }

    func resetAll() {
        hidden = [:]
        orders = [:]
        pinned = [:]
        pinnedTitles = [:]
        removed = [:]
        added = [:]
        defaults = [:]
        lastUsed = [:]
        save()
    }
}

/// VoiceOver announcements for the wheel, which lives in a non-activating panel.
@MainActor
enum Speech {
    static var isActive: Bool { NSWorkspace.shared.isVoiceOverEnabled }

    static func announce(_ text: String) {
        guard isActive, !text.isEmpty else { return }
        NSAccessibility.post(element: NSApp as Any, notification: .announcementRequested,
                             userInfo: [.announcement: text, .priority: NSAccessibilityPriorityLevel.high.rawValue])
    }
}
