import Foundation

/// Runtime localization backed by Resources/i18n.json, so the language can switch without a relaunch.
enum Loc {
    struct Language: Identifiable, Hashable {
        let code: String
        let name: String
        var id: String { code }
    }

    static let languages: [Language] = [
        Language(code: "en", name: "English"),
        Language(code: "ko", name: "한국어"),
        Language(code: "ja", name: "日本語"),
        Language(code: "zh-Hans", name: "简体中文"),
        Language(code: "zh-Hant", name: "繁體中文"),
        Language(code: "es", name: "Español"),
        Language(code: "fr", name: "Français"),
        Language(code: "de", name: "Deutsch"),
        Language(code: "ru", name: "Русский"),
        Language(code: "pt-BR", name: "Português (Brasil)"),
    ]

    private struct Table {
        var columns: [String: Int] = [:]
        var strings: [String: [String]] = [:]
    }

    private static let lock = NSLock()
    private static var current = initialLanguage()
    private static let table = loadTable()

    static var language: String {
        lock.lock()
        defer { lock.unlock() }
        return current
    }

    static var locale: Locale { Locale(identifier: language) }

    /// "system" follows macOS; anything else is a language code from `languages`.
    static func apply(preference: String) {
        let resolved = preference == "system" ? systemLanguage() : preference
        lock.lock()
        current = resolved
        lock.unlock()
    }

    static func systemLanguage() -> String { resolve(Locale.preferredLanguages) }

    static func resolve(_ identifiers: [String]) -> String {
        for raw in identifiers {
            let id = raw.lowercased()
            if id.hasPrefix("zh") {
                let traditional = id.contains("hant") || id.hasSuffix("-tw") || id.hasSuffix("-hk") || id.hasSuffix("-mo")
                return traditional ? "zh-Hant" : "zh-Hans"
            }
            if id.hasPrefix("pt") { return "pt-BR" }
            let base = String(id.prefix(2))
            if languages.contains(where: { $0.code == base }) { return base }
        }
        return "en"
    }

    private static func initialLanguage() -> String {
        if let forced = ProcessInfo.processInfo.environment["PICKVERTER_LANG"] { return forced }
        let preference = UserDefaults.standard.string(forKey: "language") ?? "system"
        return preference == "system" ? systemLanguage() : preference
    }

    private static func loadTable() -> Table {
        var candidates: [URL] = []
        if let bundled = Bundle.main.url(forResource: "i18n", withExtension: "json") { candidates.append(bundled) }
        let executable = URL(fileURLWithPath: CommandLine.arguments[0]).resolvingSymlinksInPath().deletingLastPathComponent()
        candidates.append(executable.appendingPathComponent("../Resources/i18n.json"))
        candidates.append(URL(fileURLWithPath: FileManager.default.currentDirectoryPath).appendingPathComponent("Resources/i18n.json"))
        for url in candidates {
            guard let data = try? Data(contentsOf: url),
                  let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let codes = json["languages"] as? [String],
                  let strings = json["strings"] as? [String: [String]] else { continue }
            var columns: [String: Int] = [:]
            for (index, code) in codes.enumerated() { columns[code] = index }
            return Table(columns: columns, strings: strings)
        }
        return Table()
    }

    static func template(_ key: String) -> String {
        guard let row = table.strings[key] else { return key }
        if let index = table.columns[language], index < row.count, !row[index].isEmpty { return row[index] }
        if let index = table.columns["en"], index < row.count { return row[index] }
        return key
    }

    static func hasKey(_ key: String) -> Bool { table.strings[key] != nil }

    static func pluralIndex(_ count: Int, forms: Int) -> Int {
        guard forms > 1 else { return 0 }
        switch language {
        case "ru":
            if count % 10 == 1 && count % 100 != 11 { return 0 }
            if (2...4).contains(count % 10) && !(12...14).contains(count % 100) { return min(1, forms - 1) }
            return min(2, forms - 1)
        case "fr", "pt-BR":
            return count <= 1 ? 0 : 1
        default:
            return count == 1 ? 0 : 1
        }
    }
}

/// Localized string. Placeholders are %@ (or %1$@, %2$@ for reordering).
func L(_ key: String, _ args: String...) -> String {
    let template = Loc.template(key)
    guard !args.isEmpty else { return template }
    return String(format: template, arguments: args.map { $0 as CVarArg })
}

/// Pluralized string. Forms are separated by "|" in the table; %1$@ is the formatted count.
func LP(_ key: String, _ count: Int, _ args: String...) -> String {
    let forms = Loc.template(key).components(separatedBy: "|")
    let chosen = forms[min(Loc.pluralIndex(count, forms: forms.count), forms.count - 1)]
    let number = count.formatted(.number.locale(Loc.locale))
    return String(format: chosen, arguments: ([number] + args).map { $0 as CVarArg })
}
