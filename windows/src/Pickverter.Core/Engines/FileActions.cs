using System.Security.Cryptography;

namespace Pickverter;

public sealed record QuickFolder(string Key, string Title, string Symbol, string? Path);

public static class FileActions
{
    public static async Task<PickResult> CopyPaths(IReadOnlyList<string> files, PathStyle style)
    {
        var text = string.Join("\r\n", files.Select(file => style.Format(file)));
        await Platform.Clipboard.SetTextAsync(text);
        return PickResult.Note(files.Count > 1 ? LP("msg.paths_copied", files.Count) : L("msg.path_copied", style.Format(files[0])));
    }

    public static async Task<PickResult> CopyFiles(IReadOnlyList<string> files)
    {
        await Platform.Clipboard.SetFilesAsync(files);
        return PickResult.Note(LP("msg.files_copied", files.Count));
    }

    private static void Remember(string folder)
    {
        var list = Preferences.Shared.RecentFolders.Where(path => !string.Equals(path, folder, StringComparison.OrdinalIgnoreCase)).ToList();
        list.Insert(0, folder);
        Preferences.Shared.RecentFolders = list.Take(6).ToList();
    }

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');

    /// "Choose…", Desktop, Downloads, Documents and recently used folders (minus the one the files are in).
    public static IReadOnlyList<QuickFolder> QuickFolders(IReadOnlyList<string> files)
    {
        var current = files.Select(file => Path.GetDirectoryName(Path.GetFullPath(file)) ?? "").Select(dir => dir.TrimEnd('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = new List<QuickFolder> { new("choose", L("folder.choose"), "ellipsis.circle", null) };
        var fixedFolders = new (string Key, string Title, string Symbol, string Path)[]
        {
            ("desktop", L("folder.desktop"), "menubar.dock.rectangle", Platform.Shell.DesktopFolder),
            ("downloads", L("folder.downloads"), "arrow.down.circle", Platform.Shell.DownloadsFolder),
            ("documents", L("folder.documents"), "doc.text", Platform.Shell.DocumentsFolder),
        };
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in fixedFolders)
        {
            if (string.IsNullOrEmpty(folder.Path) || !Directory.Exists(folder.Path)) continue;
            var normalized = Normalize(folder.Path);
            known.Add(normalized);
            if (!current.Contains(normalized)) list.Add(new QuickFolder(folder.Key, folder.Title, folder.Symbol, folder.Path));
        }
        var index = 0;
        foreach (var path in Preferences.Shared.RecentFolders)
        {
            if (index >= 4) break;
            if (!Directory.Exists(path)) continue;
            var normalized = Normalize(path);
            if (known.Contains(normalized) || current.Contains(normalized)) continue;
            list.Add(new QuickFolder("recent" + index, FileNames.Name(path), "folder", path));
            index++;
        }
        return list;
    }

    public static async Task<PickResult> Relocate(IReadOnlyList<string> files, bool move, string? target)
    {
        var destination = target ?? await Platform.Actions.PickFolderAsync(move, files.Count, Preferences.Shared.RecentFolders.FirstOrDefault());
        if (destination == null) return PickResult.Quiet;
        Remember(destination);
        var results = new List<string>();
        foreach (var file in files)
        {
            var item = FileNames.Unique(Path.Combine(destination, FileNames.Name(file)));
            if (move) FileSystem.Move(file, item);
            else FileSystem.Copy(file, item);
            results.Add(item);
        }
        var place = FileNames.Name(destination);
        return new PickResult
        {
            Outputs = results,
            Message = move ? L("msg.moved_to", place) : L("msg.copied_to", place),
            Undoable = !move,
        };
    }

    public static string Digest(string path, HashAlgorithm algorithm, CancellationToken token = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        using System.Security.Cryptography.HashAlgorithm hasher = algorithm switch
        {
            HashAlgorithm.Sha256 => SHA256.Create(),
            HashAlgorithm.Sha1 => SHA1.Create(),
            _ => MD5.Create(),
        };
        var buffer = new byte[1 << 20];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();
            hasher.TransformBlock(buffer, 0, read, null, 0);
        }
        hasher.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(hasher.Hash!).ToLowerInvariant();
    }

    public static async Task<PickResult> CopyChecksums(PickContext ctx, HashAlgorithm algorithm)
    {
        var lines = new List<string>();
        for (var index = 0; index < ctx.Files.Count; index++)
        {
            ctx.Check();
            var file = ctx.Files[index];
            ctx.Report((double)index / ctx.Files.Count, FileNames.Name(file));
            if (Directory.Exists(file)) continue;
            var value = await Task.Run(() => Digest(file, algorithm, ctx.Token), ctx.Token);
            lines.Add(ctx.Files.Count > 1 ? $"{value}  {FileNames.Name(file)}" : value);
        }
        if (lines.Count == 0) throw new PickException(L("err.folder_hash"));
        await Platform.Clipboard.SetTextAsync(string.Join("\r\n", lines));
        return PickResult.Note(lines.Count > 1 ? LP("msg.hashes_copied", lines.Count, algorithm.Name()) : L("msg.hash_copied", algorithm.Name(), lines[0][..16]));
    }
}
