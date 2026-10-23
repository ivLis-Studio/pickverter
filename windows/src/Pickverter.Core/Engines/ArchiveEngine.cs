using System.Formats.Tar;
using System.IO.Compression;

namespace Pickverter;

public static class ArchiveEngine
{
    /// Folder litter from macOS (Finder's .DS_Store, AppleDouble "._" files, __MACOSX, custom folder icons "Icon\r") and
    /// Windows (Thumbs.db, desktop.ini): never packed, never unpacked.
    public static bool IsJunk(string name) =>
        name.StartsWith("._", StringComparison.Ordinal) || name == "Icon\r" ||
        name.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase) || name.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase);

    /// Tests: the OEM code page ZIP names are read in (null: this PC's).
    internal static int? OemCodePageOverride { get; set; }

    private static int OemCodePage => OemCodePageOverride ?? CultureInfo.CurrentCulture.TextInfo.OEMCodePage;

    /// Unpacks any supported archive into a fresh folder (zip and tar built in; 7z, rar, bz2, xz through tar.exe or
    /// 7-Zip). Whatever comes out has names in NFC and no junk.
    public static async Task UnpackAsync(string src, string folder, CancellationToken token)
    {
        var (stem, ext) = FileNames.Split(FileNames.Name(src));
        Directory.CreateDirectory(folder);
        switch (ext)
        {
            case "zip":
                try
                {
                    await UnzipAsync(src, folder, token);
                    return;
                }
                catch (InvalidDataException error)
                {
                    // A compression method .NET doesn't read (bzip2, LZMA…) or a damaged directory: tar.exe or 7-Zip may manage.
                    Debug.Log("unzip: " + error.Message);
                    Empty(folder);
                }
                break;
            case "tar":
                await TarFile.ExtractToDirectoryAsync(src, folder, overwriteFiles: true, token);
                Tidy(folder);
                return;
            case "tar.gz":
            case "tgz":
            {
                await using (var file = File.OpenRead(src))
                await using (var gzip = new GZipStream(file, CompressionMode.Decompress))
                {
                    await TarFile.ExtractToDirectoryAsync(gzip, folder, overwriteFiles: true, token);
                }
                Tidy(folder);
                return;
            }
            case "gz":
            {
                await using var file = File.OpenRead(src);
                await using var gzip = new GZipStream(file, CompressionMode.Decompress);
                await using var output = File.Create(Path.Combine(folder, FileNames.Sanitize(NameRepair.Nfc(stem))));
                await gzip.CopyToAsync(output, token);
                return;
            }
        }
        await UnpackWithToolAsync(src, folder, ext, token);
        Tidy(folder);
    }

    private static async Task UnpackWithToolAsync(string src, string folder, string ext, CancellationToken token)
    {
        if (Tools.Tar is { } tar)
        {
            try
            {
                await Shell.RunAsync(tar, ["-xf", src, "-C", folder], token: token);
                if (Directory.EnumerateFileSystemEntries(folder).Any()) return;
            }
            catch (ToolFailure error) when (Tools.SevenZip != null || ext is "bz2" or "xz" or "7z" or "rar" or "zip")
            {
                // Windows' tar reads archives, not a lone .bz2/.xz stream (and older builds no 7z or rar): 7-Zip does.
                Debug.Log("tar: " + error.Message);
            }
            catch (ToolFailure error)
            {
                Debug.Log("tar: " + error.Message);
                throw new PickException(L("err.archive_open"));
            }
        }
        if (Tools.SevenZip is { } sevenZip)
        {
            Empty(folder);
            await Shell.RunAsync(sevenZip, ["x", "-y", "-o" + folder, src], token: token);
            return;
        }
        throw new PickException(ext == "zip" ? L("err.archive_open") : L("err.needs_app", "7-Zip"));
    }

    /// Clears a scratch folder after an attempt that didn't work out.
    private static void Empty(string folder)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(folder).ToList()) FileSystem.TryDelete(entry);
    }

    /// .NET reads the data; the names come from the central directory as written (ZipNames.Decode picks their
    /// encoding), in NFC, without junk, and never outside the folder (no absolute paths, no "..").
    private static async Task UnzipAsync(string src, string folder, CancellationToken token)
    {
        await using var stream = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        var records = ZipNames.ReadCentralDirectory(stream);
        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true, new ZipNameEncoding(OemCodePage));
        var entries = zip.Entries;
        if (entries.Any(entry => entry.IsEncrypted)) throw new PickException(L("err.zip_encrypted"));
        var names = records != null && records.Count == entries.Count
            ? ZipNames.Decode(records, OemCodePage)
            : entries.Select(entry => NameRepair.Nfc(entry.FullName)).ToList();
        var root = Path.GetFullPath(folder);
        var inside = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < entries.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var name = names[index];
            if (SafeParts(name) is not { } parts) continue;
            var target = Path.GetFullPath(Path.Combine([root, .. parts]));
            if (!target.StartsWith(inside, StringComparison.Ordinal)) continue;
            if (name.EndsWith('/') || name.EndsWith('\\'))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!written.Add(target))
            {
                // Two entries for one name (the same name twice, or in NFD and NFC): keep both.
                target = FileNames.Unique(target);
                written.Add(target);
            }
            var entry = entries[index];
            await using (var input = entry.Open())
            await using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                await input.CopyToAsync(output, token);
            }
            try
            {
                File.SetLastWriteTime(target, entry.LastWriteTime.LocalDateTime);
            }
            catch (Exception)
            {
            }
        }
    }

    /// An entry's path as safe parts for this folder: NFC, Windows-safe names, "." dropped. Null for junk, for an
    /// absolute path and for any ".." (zip slip).
    internal static string[]? SafeParts(string entryName)
    {
        var name = NameRepair.Nfc(entryName.Replace('\\', '/'));
        if (name.StartsWith('/')) return null;
        if (name.Length >= 2 && char.IsAsciiLetter(name[0]) && name[1] == ':' && (name.Length == 2 || name[2] == '/')) return null;
        var parts = new List<string>();
        foreach (var part in name.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "..") return null;
            if (part == ".") continue;
            if (IsJunk(part)) return null;
            parts.Add(FileNames.Sanitize(part));
        }
        return parts.Count == 0 ? null : [.. parts];
    }

    /// After tar, gzip or 7-Zip: what a ZIP gets on the way out (junk gone, names in NFC).
    private static void Tidy(string folder) => NameRepair.ComposeInside(folder, IsJunk);

    /// Extracts next to the archive. A single top-level item is not wrapped in an extra folder.
    public static async Task<string> Extract(string src, PickContext ctx)
    {
        var stem = FileNames.Split(FileNames.Name(src)).Base;
        var staging = FileSystem.TempFolder("pickverter-x");
        try
        {
            ctx.Report(null, L("note.extracting"));
            await UnpackAsync(src, staging, ctx.Token);
            var items = Directory.EnumerateFileSystemEntries(staging).Where(item => !IsJunk(Path.GetFileName(item))).ToList();
            if (items.Count == 0) throw new PickException(L("err.archive_empty"));
            var directory = ctx.DirectoryFor(src);
            var source = items.Count == 1 ? items[0] : staging;
            var name = NameRepair.Nfc(items.Count == 1 ? Path.GetFileName(items[0]) : stem);
            var final = FileNames.Unique(Path.Combine(directory, name));
            foreach (var junk in Directory.EnumerateFileSystemEntries(staging).Where(item => IsJunk(Path.GetFileName(item))).ToList())
                FileSystem.TryDelete(junk);
            FileSystem.Move(source, final);
            return final;
        }
        finally
        {
            FileSystem.TryDelete(staging);
        }
    }

    private sealed record Item(string Path, string Name, bool Folder);

    /// What a ZIP or TAR gets: the selected items and folders with everything in them (empty ones too), names in NFC
    /// (a Mac's split letters joined), junk left out, and a second "name.ext" at the same level as "name 2.ext".
    private static List<Item> Entries(IReadOnlyList<string> files)
    {
        var items = new List<Item>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var name = FileNames.Name(file);
            if (IsJunk(name)) continue;
            var folder = Directory.Exists(file);
            var entry = Unused(NameRepair.Nfc(name), used, folder);
            if (folder) AddFolder(items, file, entry);
            else items.Add(new Item(file, entry, false));
        }
        return items;
    }

    private static void AddFolder(List<Item> items, string folder, string entry)
    {
        items.Add(new Item(folder, entry + "/", true));
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in new DirectoryInfo(folder).EnumerateFileSystemInfos())
        {
            if (IsJunk(child.Name)) continue;
            var isFolder = child is DirectoryInfo;
            var name = entry + "/" + Unused(NameRepair.Nfc(child.Name), used, isFolder);
            // Junctions and symbolic links lead elsewhere (sometimes back up the tree): they go in as empty folders.
            if (isFolder && !child.Attributes.HasFlag(FileAttributes.ReparsePoint)) AddFolder(items, child.FullName, name);
            else if (isFolder) items.Add(new Item(child.FullName, name + "/", true));
            else items.Add(new Item(child.FullName, name, false));
        }
    }

    /// "name.ext", then "name 2.ext", "name 3.ext"… (folders: "name 2").
    private static string Unused(string name, HashSet<string> used, bool folder)
    {
        if (used.Add(name)) return name;
        var ext = folder ? "" : FileNames.Split(name).Ext;
        var stem = ext.Length == 0 ? name : name[..^(ext.Length + 1)];
        var suffix = ext.Length == 0 ? "" : name[^(ext.Length + 1)..];
        for (var n = 2; ; n++)
        {
            var candidate = stem + " " + n.ToString(CultureInfo.InvariantCulture) + suffix;
            if (used.Add(candidate)) return candidate;
        }
    }

    public static async Task WriteZip(IReadOnlyList<string> files, string destination, PickContext? ctx = null)
    {
        var items = Entries(files);
        var total = items.Count(item => !item.Folder);
        var done = 0;
        await using var stream = new FileStream(destination, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16, useAsync: true);
        var zip = new ZipWriter(stream);
        foreach (var item in items)
        {
            ctx?.Check();
            if (item.Folder)
            {
                zip.AddFolder(item.Name, Directory.GetLastWriteTime(item.Path));
                continue;
            }
            await zip.AddFileAsync(item.Name, item.Path, ctx?.Token ?? default);
            done++;
            if (ctx != null && total > 1) ctx.Report((double)done / total);
        }
        zip.Finish();
    }

    public static async Task WriteTarGz(IReadOnlyList<string> files, string destination, PickContext? ctx = null)
    {
        await using var stream = File.Create(destination);
        await using var gzip = new GZipStream(stream, CompressionLevel.Optimal);
        await using var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false);
        foreach (var item in Entries(files))
        {
            ctx?.Check();
            if (item.Folder)
            {
                await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, item.Name));
                continue;
            }
            await tar.WriteEntryAsync(item.Path, item.Name, ctx?.Token ?? default);
        }
    }

    public static async Task<string> Compress(IReadOnlyList<string> files, string format, PickContext ctx)
    {
        if (files.Count == 0) throw new PickException(L("err.archive_empty"));
        var first = files[0];
        var stem = files.Count == 1 ? (Directory.Exists(first) ? FileNames.Name(first) : FileNames.Base(first)) : L("sfx.archive");
        var destination = FileNames.Unique(Path.Combine(ctx.DirectoryFor(first), FileNames.Sanitize(stem) + "." + format));
        ctx.Report(null, L("note.compressing"));
        return await ctx.Produce(destination, async tmp =>
        {
            if (format == "zip") await WriteZip(files, tmp, ctx);
            else await WriteTarGz(files, tmp, ctx);
        });
    }

    public static async Task<string> Convert(string src, string format, PickContext ctx)
    {
        var staging = FileSystem.TempFolder("pickverter-c");
        try
        {
            await UnpackAsync(src, staging, ctx.Token);
            var items = Directory.EnumerateFileSystemEntries(staging).Where(item => !IsJunk(Path.GetFileName(item))).ToList();
            if (items.Count == 0) throw new PickException(L("err.archive_empty"));
            return await ctx.Produce(ctx.DestinationFor(src, format), async tmp =>
            {
                if (format == "zip") await WriteZip(items, tmp, ctx);
                else await WriteTarGz(items, tmp, ctx);
            });
        }
        finally
        {
            FileSystem.TryDelete(staging);
        }
    }

    public static async Task<PickResult> CopyListing(IReadOnlyList<string> folders)
    {
        var lines = new List<string>();
        foreach (var folder in folders)
        {
            var names = Directory.EnumerateFileSystemEntries(folder)
                .Where(entry => (File.GetAttributes(entry) & FileAttributes.Hidden) == 0 && !Path.GetFileName(entry).StartsWith('.'))
                .Select(Path.GetFileName)
                .OfType<string>()
                .OrderBy(name => name, NaturalComparer.Instance);
            lines.AddRange(names);
        }
        await Platform.Clipboard.SetTextAsync(string.Join("\r\n", lines));
        return PickResult.Note(LP("msg.names_copied", lines.Count));
    }
}
