using System.Formats.Tar;
using System.IO.Compression;

namespace Pickverter;

public static class ArchiveEngine
{
    private static readonly HashSet<string> Junk = new(StringComparer.OrdinalIgnoreCase) { "__MACOSX", ".DS_Store", "Thumbs.db", "desktop.ini" };

    private static Encoding LegacyZipEncoding()
    {
        FileSystem.EnsureCodePages();
        try
        {
            var page = CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
            return Encoding.GetEncoding(page);
        }
        catch (Exception)
        {
            return Encoding.UTF8;
        }
    }

    /// Unpacks any supported archive into a folder (zip and tar built in; 7z, rar, bz2, xz through tar.exe or 7-Zip).
    public static async Task UnpackAsync(string src, string folder, CancellationToken token)
    {
        var (stem, ext) = FileNames.Split(FileNames.Name(src));
        Directory.CreateDirectory(folder);
        switch (ext)
        {
            case "zip":
                ZipFile.ExtractToDirectory(src, folder, LegacyZipEncoding(), overwriteFiles: true);
                return;
            case "tar":
                await TarFile.ExtractToDirectoryAsync(src, folder, overwriteFiles: true, token);
                return;
            case "tar.gz":
            case "tgz":
            {
                await using var file = File.OpenRead(src);
                await using var gzip = new GZipStream(file, CompressionMode.Decompress);
                await TarFile.ExtractToDirectoryAsync(gzip, folder, overwriteFiles: true, token);
                return;
            }
            case "gz":
            {
                await using var file = File.OpenRead(src);
                await using var gzip = new GZipStream(file, CompressionMode.Decompress);
                await using var output = File.Create(Path.Combine(folder, FileNames.Sanitize(stem)));
                await gzip.CopyToAsync(output, token);
                return;
            }
        }
        if (Tools.Tar is { } tar)
        {
            try
            {
                await Shell.RunAsync(tar, ["-xf", src, "-C", folder], token: token);
                if (Directory.EnumerateFileSystemEntries(folder).Any()) return;
            }
            catch (ToolFailure error) when (Tools.SevenZip != null || ext is "bz2" or "xz" or "7z" or "rar")
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
            await Shell.RunAsync(sevenZip, ["x", "-y", "-o" + folder, src], token: token);
            return;
        }
        throw new PickException(L("err.needs_app", "7-Zip"));
    }

    /// Extracts next to the archive. A single top-level item is not wrapped in an extra folder.
    public static async Task<string> Extract(string src, PickContext ctx)
    {
        var stem = FileNames.Split(FileNames.Name(src)).Base;
        var staging = FileSystem.TempFolder("pickverter-x");
        try
        {
            ctx.Report(null, L("note.extracting"));
            await UnpackAsync(src, staging, ctx.Token);
            var items = Directory.EnumerateFileSystemEntries(staging).Where(item => !Junk.Contains(Path.GetFileName(item))).ToList();
            if (items.Count == 0) throw new PickException(L("err.archive_empty"));
            var directory = ctx.DirectoryFor(src);
            var source = items.Count == 1 ? items[0] : staging;
            var name = items.Count == 1 ? Path.GetFileName(items[0]) : stem;
            var final = FileNames.Unique(Path.Combine(directory, name));
            foreach (var junk in Directory.EnumerateFileSystemEntries(staging).Where(item => Junk.Contains(Path.GetFileName(item))).ToList())
                FileSystem.TryDelete(junk);
            FileSystem.Move(source, final);
            return final;
        }
        finally
        {
            FileSystem.TryDelete(staging);
        }
    }

    private static IEnumerable<(string Path, string Entry)> Entries(IReadOnlyList<string> files, string? root = null)
    {
        foreach (var file in files)
        {
            var name = FileNames.Name(file);
            if (Junk.Contains(name)) continue;
            if (Directory.Exists(file))
            {
                var prefix = root == null ? name : name;
                yield return (file, prefix + "/");
                foreach (var entry in Directory.EnumerateFileSystemEntries(file, "*", SearchOption.AllDirectories))
                {
                    if (Junk.Contains(Path.GetFileName(entry))) continue;
                    var relative = Path.GetRelativePath(file, entry).Replace('\\', '/');
                    yield return (entry, prefix + "/" + relative + (Directory.Exists(entry) ? "/" : ""));
                }
            }
            else
            {
                yield return (file, name);
            }
        }
    }

    public static async Task WriteZip(IReadOnlyList<string> files, string destination, PickContext? ctx = null)
    {
        var entries = Entries(files).ToList();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var stream = new FileStream(destination, FileMode.Create, FileAccess.ReadWrite);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8);
        var index = 0;
        foreach (var (path, entryName) in entries)
        {
            ctx?.Check();
            var name = entryName;
            if (!used.Add(name)) continue;
            if (name.EndsWith('/'))
            {
                zip.CreateEntry(name);
                continue;
            }
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            try { entry.LastWriteTime = File.GetLastWriteTime(path); } catch (Exception) { }
            await using (var source = File.OpenRead(path))
            await using (var target = entry.Open())
            {
                await source.CopyToAsync(target, ctx?.Token ?? default);
            }
            index++;
            if (ctx != null && entries.Count > 1) ctx.Report((double)index / entries.Count);
        }
    }

    public static async Task WriteTarGz(IReadOnlyList<string> files, string destination, PickContext? ctx = null)
    {
        await using var stream = File.Create(destination);
        await using var gzip = new GZipStream(stream, CompressionLevel.Optimal);
        await using var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false);
        foreach (var (path, entryName) in Entries(files))
        {
            ctx?.Check();
            if (entryName.EndsWith('/'))
            {
                await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, entryName));
                continue;
            }
            await tar.WriteEntryAsync(path, entryName, ctx?.Token ?? default);
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
            var items = Directory.EnumerateFileSystemEntries(staging).Where(item => !Junk.Contains(Path.GetFileName(item))).ToList();
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
