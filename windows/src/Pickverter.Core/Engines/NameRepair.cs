namespace Pickverter;

/// Names a Mac wrote in decomposed form (NFD): Hangul as separate jamo (ㅎㅏㄴ), kana with a separate voicing mark
/// (か + ゛). File Explorer and most web apps show those apart; joining them (NFC) makes them read right.
public static class NameRepair
{
    /// How much the wheel looks at before it stops looking: the first selected items, and the first names directly
    /// inside each selected folder.
    private const int ItemLimit = 20;
    private const int ScanLimit = 5_000;

    /// Hangul conjoining jamo and the combining kana voicing marks: what NFC joins in such names.
    private static bool Joinable(char c) => c is >= '\u1100' and <= '\u11FF' or '\u3099' or '\u309A';

    private static int CountJoinable(string text)
    {
        var count = 0;
        foreach (var c in text)
        {
            if (Joinable(c)) count++;
        }
        return count;
    }

    /// The NFC form of a name whose split letters join, or null when nothing in it joins.
    public static string? Joined(string name)
    {
        var count = CountJoinable(name);
        if (count == 0) return null;
        string nfc;
        try
        {
            nfc = name.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return null;
        }
        return nfc != name && CountJoinable(nfc) < count ? nfc : null;
    }

    /// NFC, or the name as it is when it can't be normalized (a broken surrogate pair).
    public static string Nfc(string name)
    {
        try
        {
            return name.IsNormalized(NormalizationForm.FormC) ? name : name.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return name;
        }
    }

    /// The Actions ring offers Join Split Letters when a selected name, or a name directly inside a selected folder,
    /// has letters to join.
    public static bool Offered(IReadOnlyList<string> files)
    {
        foreach (var file in files.Take(ItemLimit))
        {
            if (Joined(FileNames.Name(file)) != null) return true;
            if (!Directory.Exists(file)) continue;
            try
            {
                var seen = 0;
                foreach (var entry in new DirectoryInfo(file).EnumerateFileSystemInfos())
                {
                    if (Joined(entry.Name) != null) return true;
                    if (++seen >= ScanLimit) break;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                Debug.Log("names: " + error.Message);
            }
        }
        return false;
    }

    /// Renames every name with split letters to its joined form, folders all the way down (what is inside a folder
    /// before the folder itself, so every path stays valid until its turn). A name another item already has is left
    /// alone. Not undoable: the old names are the broken ones.
    public static async Task<PickResult> Join(PickContext ctx)
    {
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var renamed = await Task.Run(() =>
        {
            var count = 0;
            foreach (var group in ctx.Files.GroupBy(file => Path.GetDirectoryName(Path.GetFullPath(file).TrimEnd('\\', '/')) ?? "",
                         StringComparer.OrdinalIgnoreCase))
            {
                var parent = group.Key;
                var listing = parent.Length > 0 ? Listing.Of(parent) : null;
                foreach (var file in group)
                {
                    ctx.Check();
                    var path = Path.GetFullPath(file).TrimEnd('\\', '/');
                    if (IsRealFolder(path)) count += JoinInside(path, ctx, touched);
                    if (listing != null && TryRename(path, parent, listing))
                    {
                        count++;
                        touched.Add(parent);
                    }
                }
            }
            return count;
        }, ctx.Token);
        foreach (var folder in touched) Platform.Shell.NotifyFolderChanged(folder);
        return PickResult.Note(renamed > 0 ? LP("msg.renamed", renamed) : L("err.names_ok"));
    }

    /// A folder to walk into: not a junction or symbolic link (those lead somewhere else, sometimes back up the tree).
    private static bool IsRealFolder(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.Directory) && !attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static int JoinInside(string folder, PickContext ctx, HashSet<string> touched)
    {
        FileSystemInfo[] entries;
        try
        {
            entries = new DirectoryInfo(folder).GetFileSystemInfos();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Debug.Log("names: " + error.Message);
            return 0;
        }
        var listing = new Listing(entries.Select(entry => entry.Name));
        var count = 0;
        foreach (var entry in entries)
        {
            ctx.Check();
            if (entry is DirectoryInfo directory && !directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                count += JoinInside(directory.FullName, ctx, touched);
            if (TryRename(entry.FullName, folder, listing))
            {
                count++;
                touched.Add(folder);
            }
        }
        return count;
    }

    private static bool TryRename(string path, string parent, Listing listing)
    {
        var name = FileNames.Name(path);
        if (Target(name, listing.Names) is not { } target) return false;
        try
        {
            MoveName(path, Path.Combine(parent, target), parent);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Debug.Log("names: " + error.Message);
            return false;
        }
        listing.Renamed(name, target);
        return true;
    }

    /// The joined name an item gets, or null: nothing to join, or another item in the folder already answers to that
    /// name (names: everything in the folder, compared the way File Explorer does, ignoring case).
    internal static string? Target(string name, IReadOnlySet<string> names) =>
        Joined(name) is { } target && !names.Contains(target) ? target : null;

    /// Renames a file or folder within its folder. A file system that matches names regardless of normalization
    /// (APFS on a Mac, an SMB share from one) finds the target is "taken" by the item itself: a temporary name in
    /// between works everywhere.
    internal static void MoveName(string source, string destination, string parent)
    {
        var folder = File.GetAttributes(source).HasFlag(FileAttributes.Directory);
        if (!File.Exists(destination) && !Directory.Exists(destination))
        {
            Move(source, destination, folder);
            return;
        }
        var temporary = Path.Combine(parent, ".pickverter-" + Guid.NewGuid().ToString("N")[..8]);
        Move(source, temporary, folder);
        try
        {
            Move(temporary, destination, folder);
        }
        catch (Exception)
        {
            Move(temporary, source, folder);
            throw;
        }
    }

    private static void Move(string source, string destination, bool folder)
    {
        if (folder) Directory.Move(source, destination);
        else File.Move(source, destination);
    }

    /// Every name inside a folder Pickverter just made (a result, an unpacked archive) in NFC, all the way down.
    /// junk: names to delete instead. A name that is taken stays as it is.
    internal static void ComposeInside(string folder, Func<string, bool>? junk = null)
    {
        FileSystemInfo[] entries;
        try
        {
            entries = new DirectoryInfo(folder).GetFileSystemInfos();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Debug.Log("names: " + error.Message);
            return;
        }
        var names = new HashSet<string>(entries.Select(entry => entry.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (junk?.Invoke(entry.Name) == true)
            {
                FileSystem.TryDelete(entry.FullName);
                continue;
            }
            if (entry is DirectoryInfo directory && !directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) ComposeInside(directory.FullName, junk);
            var nfc = Nfc(entry.Name);
            if (nfc == entry.Name || names.Contains(nfc)) continue;
            try
            {
                MoveName(entry.FullName, Path.Combine(folder, nfc), folder);
                names.Add(nfc);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Debug.Log("names: " + error.Message);
            }
        }
    }

    /// The names in one folder as renames change them.
    private sealed class Listing(IEnumerable<string> names)
    {
        private readonly HashSet<string> names = new(names, StringComparer.OrdinalIgnoreCase);

        public IReadOnlySet<string> Names => names;

        public static Listing? Of(string folder)
        {
            try
            {
                return new Listing(new DirectoryInfo(folder).EnumerateFileSystemInfos().Select(entry => entry.Name));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Debug.Log("names: " + error.Message);
                return null;
            }
        }

        public void Renamed(string from, string to)
        {
            names.Remove(from);
            names.Add(to);
        }
    }
}
