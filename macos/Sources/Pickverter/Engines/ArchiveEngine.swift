import AppKit

enum ArchiveEngine {
    /// Unpacks into a folder: ZIP through ZipReader (names from Windows read right; ditto for exotic methods), a lone
    /// .gz or .bz2 file through gunzip/bunzip2, a lone .xz through Apple's own LZMA decoder (macOS has no xz command),
    /// and every other archive through tar.
    static func unpack(_ src: URL, into folder: URL, ctx: PickContext? = nil) async throws {
        let (base, ext) = FileNames.split(src.lastPathComponent)
        if ext == "zip" {
            do {
                try ZipReader.extract(src, into: folder) { ctx?.report($0) }
            } catch is ZipKit.Unsupported {
                try await Shell.run("/usr/bin/ditto", ["-x", "-k", src.path, folder.path])
                removeClutter(in: folder)
            }
        } else if ["gz", "bz2", "xz"].contains(ext), !base.lowercased().hasSuffix(".tar") {
            let out = folder.appendingPathComponent(base)
            if ext == "xz" {
                do {
                    let plain = try (Data(contentsOf: src) as NSData).decompressed(using: .lzma)
                    try (plain as Data).write(to: out)
                } catch {
                    throw PickError(L("err.archive_open"))
                }
            } else {
                try await Shell.run(ext == "gz" ? "/usr/bin/gunzip" : "/usr/bin/bunzip2", ["-c", src.path], stdoutTo: out)
            }
        } else {
            try await Shell.run("/usr/bin/tar", ["-xf", src.path, "-C", folder.path])
            removeClutter(in: folder)
        }
    }

    /// .DS_Store, ._ files, __MACOSX, Thumbs.db and desktop.ini anywhere inside.
    static func removeClutter(in folder: URL) {
        let fm = FileManager.default
        guard let walker = fm.enumerator(at: folder, includingPropertiesForKeys: nil) else { return }
        var junk: [URL] = []
        for case let item as URL in walker where ZipKit.isJunk(item.lastPathComponent) {
            junk.append(item)
            walker.skipDescendants()
        }
        for item in junk { try? fm.removeItem(at: item) }
    }

    /// Extracts next to the archive. A single top-level item is not wrapped in an extra folder.
    static func extract(_ src: URL, ctx: PickContext) async throws -> URL {
        let fm = FileManager.default
        let base = FileNames.split(src.lastPathComponent).base
        let staging = fm.temporaryDirectory.appendingPathComponent("pickverter-x-" + UUID().uuidString, isDirectory: true)
        try fm.createDirectory(at: staging, withIntermediateDirectories: true)
        defer { try? fm.removeItem(at: staging) }
        ctx.report(nil, L("note.extracting"))
        try await unpack(src, into: staging, ctx: ctx)
        let items = try fm.contentsOfDirectory(at: staging, includingPropertiesForKeys: nil)
            .filter { !ZipKit.isJunk($0.lastPathComponent) }
        guard !items.isEmpty else { throw PickError(L("err.archive_empty")) }
        let directory = ctx.directory(for: src)
        let source = items.count == 1 ? items[0] : staging
        let name = items.count == 1 ? items[0].lastPathComponent : base
        let final = FileNames.unique(directory.appendingPathComponent(name))
        do {
            return try NameFixer.place(source, at: final)
        } catch CocoaError.fileWriteFileExists {
            return try NameFixer.place(source, at: FileNames.unique(final))
        }
    }

    static func compress(_ files: [URL], format: String, ctx: PickContext) async throws -> URL {
        guard let first = files.first else { throw PickError(L("err.archive_empty")) }
        let parent = first.deletingLastPathComponent()
        let sameParent = files.allSatisfy { $0.deletingLastPathComponent().standardizedFileURL == parent.standardizedFileURL }
        let base = files.count == 1 ? FileNames.split(first.lastPathComponent).base : L("sfx.archive")
        let name = files.count == 1 && FileKind.of(first) == .folder ? first.lastPathComponent : base
        let dst = FileNames.unique(ctx.directory(for: first).appendingPathComponent(name + "." + format))
        ctx.report(nil, L("note.compressing"))
        return try await ctx.produce(dst) { tmp in
            if format == "zip" {
                try writeZip(files.map { ($0, $0.lastPathComponent) }, to: tmp, ctx: ctx)
                return
            }
            var root = parent
            var names = files.map(\.lastPathComponent)
            var stagingDir: URL?
            if !sameParent {
                let staging = FileManager.default.temporaryDirectory.appendingPathComponent("pickverter-z-" + UUID().uuidString)
                try FileManager.default.createDirectory(at: staging, withIntermediateDirectories: true)
                names = []
                for file in files {
                    let target = FileNames.unique(staging.appendingPathComponent(file.lastPathComponent))
                    try FileManager.default.copyItem(at: file, to: target)
                    names.append(target.lastPathComponent)
                }
                root = staging
                stagingDir = staging
            }
            defer { if let stagingDir { try? FileManager.default.removeItem(at: stagingDir) } }
            try await Shell.run("/usr/bin/tar", ["-czf", tmp.path, "--exclude", ".DS_Store", "-C", root.path] + names)
        }
    }

    /// Every item under its own name (two items with the same name from different folders: "name 2").
    static func writeZip(_ items: [(url: URL, name: String)], to url: URL, ctx: PickContext) throws {
        let total = Double(max(1, items.reduce(0) { $0 + FileNames.size(of: $1.url) }))
        let writer = try ZipWriter(url: url) { read in ctx.report(min(0.99, Double(read) / total)) }
        var used = Set<String>()
        for item in items {
            var name = item.name.precomposedStringWithCanonicalMapping
            let (stem, ext) = FileNames.split(name)
            var n = 2
            while used.contains(name.lowercased()) {
                name = ext.isEmpty ? "\(stem) \(n)" : "\(stem) \(n).\(ext)"
                n += 1
            }
            used.insert(name.lowercased())
            try writer.add(item.url, as: name)
        }
        try writer.finish()
    }

    static func convert(_ src: URL, to format: String, ctx: PickContext) async throws -> URL {
        let fm = FileManager.default
        let staging = fm.temporaryDirectory.appendingPathComponent("pickverter-c-" + UUID().uuidString, isDirectory: true)
        try fm.createDirectory(at: staging, withIntermediateDirectories: true)
        defer { try? fm.removeItem(at: staging) }
        try await unpack(src, into: staging, ctx: ctx.portion(0, 0.4))
        let names = try fm.contentsOfDirectory(atPath: staging.path).filter { !ZipKit.isJunk($0) }
        guard !names.isEmpty else { throw PickError(L("err.archive_empty")) }
        let dst = ctx.destination(for: src, ext: format)
        return try await ctx.produce(dst) { tmp in
            if format == "zip" {
                try writeZip(names.map { (staging.appendingPathComponent($0), $0) }, to: tmp, ctx: ctx.portion(0.4, 1))
            } else {
                try await Shell.run("/usr/bin/tar", ["-czf", tmp.path, "-C", staging.path] + names)
            }
        }
    }

    static func copyListing(_ folders: [URL]) async throws -> PickResult {
        var lines: [String] = []
        for folder in folders {
            let names = try FileManager.default.contentsOfDirectory(atPath: folder.path)
                .filter { !$0.hasPrefix(".") }
                .sorted { $0.localizedStandardCompare($1) == .orderedAscending }
            lines += names
        }
        let text = lines.joined(separator: "\n")
        await MainActor.run {
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(text, forType: .string)
        }
        return .note(LP("msg.names_copied", lines.count))
    }
}
