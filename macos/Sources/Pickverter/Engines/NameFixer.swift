import AppKit

/// File names in the form Windows and the web expect. macOS hands names to the disk decomposed (한 as ㅎ+ㅏ+ㄴ,
/// が as か+゛) and APFS keeps whatever it is given, so a name made on a Mac can show up split once it leaves: after an
/// upload, in a cloud folder, on Windows. Foundation decomposes every path again, so renames here go through POSIX
/// calls with the exact bytes.
enum NameFixer {
    typealias RawName = [UInt8]

    static let slash: UInt8 = 0x2F

    /// A path as the file system takes it (decomposed, which every Mac volume looks up correctly).
    static func pathBytes(_ url: URL) -> RawName {
        url.withUnsafeFileSystemRepresentation { pointer -> RawName in
            guard let pointer else { return RawName(url.path.utf8) }
            return RawName(UnsafeBufferPointer(start: UnsafeRawPointer(pointer).assumingMemoryBound(to: UInt8.self),
                                               count: strlen(pointer)))
        }
    }

    static func withCPath<T>(_ bytes: RawName, _ body: (UnsafePointer<CChar>) -> T) -> T {
        (bytes + [0]).withUnsafeBufferPointer { buffer in
            buffer.baseAddress!.withMemoryRebound(to: CChar.self, capacity: buffer.count) { body($0) }
        }
    }

    /// The names in a folder exactly as stored.
    static func rawNames(in directory: RawName, limit: Int = 100_000) -> [RawName] {
        withCPath(directory) { path in
            guard let dir = opendir(path) else { return [] }
            defer { closedir(dir) }
            var names: [RawName] = []
            while names.count < limit, let entry = readdir(dir) {
                let length = Int(entry.pointee.d_namlen)
                let name = withUnsafeBytes(of: entry.pointee.d_name) { RawName($0.prefix(length)) }
                if name == [0x2E] || name == [0x2E, 0x2E] { continue }
                names.append(name)
            }
            return names
        }
    }

    /// Hangul conjoining jamo (U+1100–U+11FF) or a combining kana voicing mark (U+3099, U+309A), read off the UTF-8 bytes.
    static func hasSplitLetters(_ raw: RawName) -> Bool {
        var index = 0
        while index + 2 < raw.count {
            if raw[index] == 0xE1, (0x84...0x87).contains(raw[index + 1]) { return true }
            if raw[index] == 0xE3, raw[index + 1] == 0x82, raw[index + 2] == 0x99 || raw[index + 2] == 0x9A { return true }
            index += 1
        }
        return false
    }

    /// The joined-up name when a stored name has letters that only show up right once composed, otherwise nil.
    static func composed(_ raw: RawName) -> RawName? {
        guard hasSplitLetters(raw), let name = String(bytes: raw, encoding: .utf8) else { return nil }
        let joined = RawName(name.precomposedStringWithCanonicalMapping.utf8)
        return joined == raw ? nil : joined
    }

    static func composedName(_ name: String) -> RawName {
        RawName(name.precomposedStringWithCanonicalMapping.utf8)
    }

    static func isLocal(_ url: URL) -> Bool {
        (try? url.resourceValues(forKeys: [.volumeIsLocalKey]))?.volumeIsLocal ?? true
    }

    /// A real folder (symbolic links are left alone).
    static func isFolder(_ path: RawName) -> Bool {
        var info = stat()
        return withCPath(path) { lstat($0, &info) } == 0 && (info.st_mode & S_IFMT) == S_IFDIR
    }

    /// HFS+ stores every name decomposed, whatever it is given.
    static func keepsComposedNames(_ path: RawName) -> Bool {
        var info = statfs()
        guard withCPath(path, { statfs($0, &info) }) == 0 else { return true }
        let type = withUnsafeBytes(of: info.f_fstypename) { bytes in
            String(decoding: bytes.prefix(while: { $0 != 0 }), as: UTF8.self)
        }
        return type != "hfs"
    }

    /// The stored name of a selected item (the URL may carry either form) among its folder's names.
    static func storedName(of url: URL, among names: [RawName]) -> RawName {
        let wanted = composedName(url.lastPathComponent)
        return names.first { $0 == wanted } ?? names.first { composed($0) == wanted } ?? RawName(url.lastPathComponent.utf8)
    }

    /// Folder listings for one pass over a selection (many selected files usually share a folder).
    private final class Listings {
        private var cache: [RawName: [RawName]] = [:]
        func names(in folder: RawName) -> [RawName] {
            if let known = cache[folder] { return known }
            let names = rawNames(in: folder)
            cache[folder] = names
            return names
        }
    }

    // MARK: Action

    /// Shown on the Actions ring when a selected name, or a name right inside a selected folder, has split letters.
    static func needsFix(_ files: [URL]) -> Bool {
        let listings = Listings()
        for file in files.prefix(20) where isLocal(file) {
            let parent = pathBytes(file.deletingLastPathComponent())
            let names = listings.names(in: parent)
            let wanted = composedName(file.lastPathComponent)
            if names.contains(where: { composed($0) == wanted }) { return true }
            let path = parent + [slash] + storedName(of: file, among: names)
            if isFolder(path), rawNames(in: path, limit: 5000).contains(where: { composed($0) != nil }) { return true }
        }
        return false
    }

    /// Joins split letters in every selected name and everything inside selected folders (children first, so the
    /// paths above stay valid). Nothing is renamed over another file.
    static func fix(_ ctx: PickContext) async throws -> PickResult {
        var fixed = 0
        var touched = Set<RawName>()
        var visited = 0
        let listings = Listings()
        for file in ctx.files {
            try Task.checkCancellation()
            let parent = pathBytes(file.deletingLastPathComponent())
            guard keepsComposedNames(parent) else { continue }
            let stored = storedName(of: file, among: listings.names(in: parent))
            let path = parent + [slash] + stored
            if isFolder(path) { fixed += try walk(path, touched: &touched, visited: &visited, ctx: ctx) }
            if let joined = composed(stored), rename(in: parent, from: stored, to: joined) {
                fixed += 1
                touched.insert(parent)
            }
        }
        let folders = touched.map { String(decoding: $0, as: UTF8.self) }
        await MainActor.run {
            for folder in folders { NSWorkspace.shared.noteFileSystemChanged(folder) }
        }
        return .note(fixed > 0 ? LP("msg.renamed", fixed) : L("err.names_ok"))
    }

    private static func walk(_ folder: RawName, touched: inout Set<RawName>, visited: inout Int, ctx: PickContext?) throws -> Int {
        var fixed = 0
        for name in rawNames(in: folder) {
            visited += 1
            if visited % 2000 == 0 {
                try Task.checkCancellation()
                ctx?.report(nil, String(decoding: name, as: UTF8.self))
            }
            guard visited < 1_000_000 else { break }
            let path = folder + [slash] + name
            if isFolder(path) { fixed += try walk(path, touched: &touched, visited: &visited, ctx: ctx) }
            if let joined = composed(name), rename(in: folder, from: name, to: joined) {
                fixed += 1
                touched.insert(folder)
            }
        }
        return fixed
    }

    /// Renames unless another file already has the new name (on APFS both forms name the same file, which is fine).
    static func rename(in folder: RawName, from old: RawName, to new: RawName) -> Bool {
        let from = folder + [slash] + old
        let to = folder + [slash] + new
        var source = stat(), target = stat()
        guard withCPath(from, { lstat($0, &source) }) == 0 else { return false }
        if withCPath(to, { lstat($0, &target) }) == 0, source.st_ino != target.st_ino || source.st_dev != target.st_dev {
            return false
        }
        return posixRename(from, to) == 0
    }

    /// 0 or the errno of the failure. exclusive: fail with EEXIST instead of replacing a file.
    static func posixRename(_ from: RawName, _ to: RawName, exclusive: Bool = false) -> Int32 {
        withCPath(from) { f in
            withCPath(to) { t in
                let status = exclusive ? renamex_np(f, t, UInt32(RENAME_EXCL)) : Darwin.rename(f, t)
                return status == 0 ? 0 : errno
            }
        }
    }

    // MARK: Results

    /// Moves a finished result into place under a composed name, so it reads right after an upload or on Windows.
    /// Never replaces an existing file (throws fileWriteFileExists instead). Folders get composed names inside too.
    @discardableResult
    static func place(_ source: URL, at destination: URL) throws -> URL {
        let name = destination.lastPathComponent.precomposedStringWithCanonicalMapping
        let folder = destination.deletingLastPathComponent()
        let final = folder.appendingPathComponent(name)
        let to = pathBytes(folder) + [slash] + RawName(name.utf8)
        let from = pathBytes(source)
        var failure = posixRename(from, to, exclusive: true)
        if failure == ENOTSUP || failure == EINVAL {
            // A volume that can't refuse an existing name in the same call.
            if FileManager.default.fileExists(atPath: final.path) { throw CocoaError(.fileWriteFileExists) }
            failure = posixRename(from, to)
        }
        if failure == EEXIST { throw CocoaError(.fileWriteFileExists) }
        if failure != 0 {
            // Another volume: move across, then compose the name where it landed.
            try FileManager.default.moveItem(at: source, to: final)
            _ = posixRename(pathBytes(final), to)
        }
        if isFolder(to) {
            var touched = Set<RawName>()
            var visited = 0
            _ = try? walk(to, touched: &touched, visited: &visited, ctx: nil)
        }
        return final
    }
}
