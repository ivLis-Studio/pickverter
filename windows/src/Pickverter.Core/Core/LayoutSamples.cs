namespace Pickverter;

/// What Settings › Wheel shows for a file type: files that stand for it and every slot each ring has for them.
public static class LayoutSamples
{
    /// One sample per Office format the Windows app opens (OfficeEngine.Exts), in a fixed order.
    public static readonly IReadOnlyList<string> OfficeExts = ["xlsx", "xlsm", "xls", "pptx", "ppsx", "ppt", "numbers", "pages", "key", "ods", "odp"];

    private static string Folder => Path.Combine(FileSystem.TempRoot, "Pickverter Layout Preview");

    public static string Title(FileKind kind) => kind switch
    {
        FileKind.Image => L("kind.image"),
        FileKind.Video => L("kind.video"),
        FileKind.Audio => L("kind.audio"),
        FileKind.Pdf => L("kind.pdf"),
        FileKind.Document => L("kind.document"),
        FileKind.Hwp => L("kind.hwp"),
        FileKind.Office => L("kind.office"),
        FileKind.Table => L("kind.table"),
        FileKind.Json => L("kind.json"),
        FileKind.Subtitle => L("kind.subtitle"),
        FileKind.Archive => L("kind.archive"),
        FileKind.Folder => L("kind.folder"),
        _ => L("kind.other"),
    };

    /// Files that stand for a file type. They needn't exist (the catalog goes by extension), except the folder.
    public static IReadOnlyList<string> Files(FileKind kind)
    {
        static string Sample(string name) => Path.Combine(Folder, name);
        return kind switch
        {
            FileKind.Image => [Sample("photo.jpg"), Sample("anim.gif")],
            FileKind.Video => [Sample("clip.mp4")],
            FileKind.Audio => [Sample("sound.mp3")],
            FileKind.Pdf => [Sample("document.pdf")],
            FileKind.Document => [Sample("notes.docx")],
            FileKind.Hwp => [Sample("report.hwp"), Sample("report.hwpx")],
            FileKind.Office => OfficeExts.Select(ext => Sample(OfficeName(ext) + "." + ext)).ToList(),
            FileKind.Table => [Sample("data.csv")],
            FileKind.Json => [Sample("data.json")],
            FileKind.Subtitle => [Sample("captions.srt")],
            FileKind.Archive => [Sample("files.zip")],
            FileKind.Folder => [RealFolder()],
            _ => [Sample("file.bin")],
        };
    }

    private static string OfficeName(string ext) => ext switch
    {
        "pptx" or "ppsx" or "ppt" or "key" or "odp" => "slides",
        "pages" => "document",
        _ => "sheet",
    };

    /// A folder is only a folder when it exists.
    private static string RealFolder() => RealFolder(Path.Combine(Folder, "folder"));

    private static string RealFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception error)
        {
            Debug.Log("layout sample folder: " + error.Message);
        }
        return folder;
    }

    /// "photo 2.jpg" next to "photo.jpg"; next to a folder, a second real folder.
    public static string Sibling(string sample)
    {
        var name = Path.GetFileNameWithoutExtension(sample) + " 2" + Path.GetExtension(sample);
        var sibling = Path.Combine(Path.GetDirectoryName(sample) ?? Folder, name);
        return Directory.Exists(sample) ? RealFolder(sibling) : sibling;
    }

    /// What a file type's slots are gathered from: each sample on its own, then with a second file of its kind beside
    /// it (Merge, Join, Collage and the like only come with several files).
    public static IReadOnlyList<IReadOnlyList<string>> Selections(FileKind kind) =>
        Files(kind).SelectMany(sample => new IReadOnlyList<string>[] { [sample], [sample, Sibling(sample)] }).ToList();

    /// Every slot the ring has for a file type: the catalog's options for each selection, by id, in wheel order. A slot
    /// a later selection brings in goes right after the one before it there, or last.
    public static List<PickOption> Native(FileKind kind, WheelMode mode)
    {
        var result = new List<PickOption>();
        foreach (var selection in Selections(kind))
        {
            string? previous = null;
            foreach (var option in Catalog.Options(selection, mode))
            {
                if (!result.Any(existing => existing.Id == option.Id))
                {
                    var after = previous == null ? -1 : result.FindIndex(existing => existing.Id == previous);
                    result.Insert(after >= 0 ? after + 1 : result.Count, option);
                }
                previous = option.Id;
            }
        }
        return result;
    }

    /// All three rings' own slots for a file type.
    public static Dictionary<WheelMode, List<PickOption>> Rings(FileKind kind) =>
        WheelModes.All.ToDictionary(mode => mode, mode => Native(kind, mode));

    /// The other two rings' slots, where a ring's borrowed slots come from.
    public static List<PickOption> Pool(IReadOnlyDictionary<WheelMode, List<PickOption>> rings, WheelMode mode) =>
        WheelModes.All.Where(other => other != mode).SelectMany(other => rings.TryGetValue(other, out var list) ? list : []).ToList();

    /// Each ring with the user's layout applied, as the wheel shows it for this file type.
    public static Dictionary<WheelMode, List<PickOption>> Arranged(IReadOnlyDictionary<WheelMode, List<PickOption>> rings, FileKind kind,
                                                                  WheelLayout layout) =>
        WheelModes.All.ToDictionary(mode => mode,
            mode => layout.Arrange(rings.TryGetValue(mode, out var list) ? list : [], WheelLayout.Key(mode, kind), Pool(rings, mode)));
}
