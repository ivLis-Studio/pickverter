import AppKit

enum ArchiveEngine {
    /// Extracts next to the archive. A single top-level item is not wrapped in an extra folder.
    static func extract(_ src: URL, ctx: PickContext) async throws -> URL {
        let fm = FileManager.default
        let (base, ext) = FileNames.split(src.lastPathComponent)
        let staging = fm.temporaryDirectory.appendingPathComponent("pickverter-x-" + UUID().uuidString, isDirectory: true)
        try fm.createDirectory(at: staging, withIntermediateDirectories: true)
        defer { try? fm.removeItem(at: staging) }
        ctx.report(nil, L("note.extracting"))
        if ext == "zip" {
            try await Shell.run("/usr/bin/ditto", ["-x", "-k", src.path, staging.path])
        } else if ext == "gz" || ext == "bz2" || ext == "xz", !base.lowercased().hasSuffix(".tar") {
            let tool = ext == "gz" ? "/usr/bin/gunzip" : ext == "bz2" ? "/usr/bin/bunzip2" : "/usr/bin/xz"
            let out = staging.appendingPathComponent(base)
            try await Shell.run(tool, ["-c", src.path], stdoutTo: out)
        } else {
            try await Shell.run("/usr/bin/tar", ["-xf", src.path, "-C", staging.path])
        }
        let items = try fm.contentsOfDirectory(at: staging, includingPropertiesForKeys: nil)
            .filter { !["__MACOSX", ".DS_Store"].contains($0.lastPathComponent) }
        guard !items.isEmpty else { throw PickError(L("err.archive_empty")) }
        let directory = ctx.directory(for: src)
        let source = items.count == 1 ? items[0] : staging
        let name = items.count == 1 ? items[0].lastPathComponent : base
        let final = FileNames.unique(directory.appendingPathComponent(name))
        try fm.moveItem(at: source, to: final)
        return final
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
            if format == "zip", files.count == 1 {
                try await Shell.run("/usr/bin/ditto", ["-c", "-k", "--sequesterRsrc", "--keepParent", first.path, tmp.path])
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
            if format == "zip" {
                try await Shell.run("/usr/bin/zip", ["-r", "-y", "-q", tmp.path] + names + ["-x", "*.DS_Store", "__MACOSX/*"], cwd: root)
            } else {
                try await Shell.run("/usr/bin/tar", ["-czf", tmp.path, "--exclude", ".DS_Store", "-C", root.path] + names)
            }
        }
    }

    static func convert(_ src: URL, to format: String, ctx: PickContext) async throws -> URL {
        let fm = FileManager.default
        let staging = fm.temporaryDirectory.appendingPathComponent("pickverter-c-" + UUID().uuidString, isDirectory: true)
        try fm.createDirectory(at: staging, withIntermediateDirectories: true)
        defer { try? fm.removeItem(at: staging) }
        if FileNames.split(src.lastPathComponent).ext == "zip" {
            try await Shell.run("/usr/bin/ditto", ["-x", "-k", src.path, staging.path])
        } else {
            try await Shell.run("/usr/bin/tar", ["-xf", src.path, "-C", staging.path])
        }
        let names = try fm.contentsOfDirectory(atPath: staging.path).filter { $0 != "__MACOSX" && $0 != ".DS_Store" }
        guard !names.isEmpty else { throw PickError(L("err.archive_empty")) }
        let dst = ctx.destination(for: src, ext: format)
        return try await ctx.produce(dst) { tmp in
            if format == "zip" {
                try await Shell.run("/usr/bin/zip", ["-r", "-y", "-q", tmp.path] + names, cwd: staging)
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
