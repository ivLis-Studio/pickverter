namespace Pickverter;

public enum Turn { Right, Left, Half, FlipH, FlipV }

public static class Turns
{
    public static readonly Turn[] All = [Turn.Right, Turn.Left, Turn.Half, Turn.FlipH, Turn.FlipV];

    public static string Raw(this Turn turn) => turn switch
    {
        Turn.Right => "right",
        Turn.Left => "left",
        Turn.Half => "half",
        Turn.FlipH => "flipH",
        _ => "flipV",
    };

    public static string Title(this Turn turn) => L("rot." + turn.Raw());

    public static string Symbol(this Turn turn) => turn switch
    {
        Turn.Right => "rotate.right",
        Turn.Left => "rotate.left",
        Turn.Half => "arrow.triangle.2.circlepath",
        Turn.FlipH => "flip.horizontal",
        _ => "flip.vertical",
    };

    public static bool IsFlip(this Turn turn) => turn is Turn.FlipH or Turn.FlipV;
}

public enum CutoutStyle { Transparent, Crop, White }

public static class CutoutStyles
{
    public static readonly CutoutStyle[] All = [CutoutStyle.Transparent, CutoutStyle.Crop, CutoutStyle.White];
    public static string Raw(this CutoutStyle style) => style.ToString().ToLowerInvariant();
    public static string Title(this CutoutStyle style) => L("bg." + style.Raw());
}

public sealed record ConvertOptions(double Dpi = 300, int? GifWidth = 720, int GifFps = 15);

/// Routes a "convert to X" request to the engine that understands the source file.
public static class Converter
{
    public static Task<string> Convert(string src, string ext, ConvertOptions options, PickContext ctx)
    {
        if (FileNames.NormalizedExt(src) == ext) throw new PickSkipException();
        switch (FileKinds.Of(src))
        {
            case FileKind.Image:
                if (ext is "mp4" or "webm" or "mov") return MediaEngine.Convert(src, ext, options, ctx);
                return ImageEngine.Convert(src, ext, ctx);
            case FileKind.Video:
            case FileKind.Audio:
                return MediaEngine.Convert(src, ext, options, ctx);
            case FileKind.Pdf:
                return PdfEngine.Convert(src, ext, options.Dpi, ctx);
            case FileKind.Document:
                if (ext == "epub") return EpubBuilder.FromDocuments([src], ctx);
                return DocumentEngine.Convert(src, ext, ctx);
            case FileKind.Office:
                return OfficeEngine.Convert(src, ext, ctx);
            case FileKind.Table:
            case FileKind.Json:
                return DataEngine.ConvertTable(src, ext, ctx);
            case FileKind.Subtitle:
                return DataEngine.ConvertSubtitles(src, ext, ctx);
            case FileKind.Archive:
                return ArchiveEngine.Convert(src, ext, ctx);
            default:
                throw new PickException(L("err.not_convertible"));
        }
    }
}

public enum PathStyle { Full, Quoted, Name, Unix }

public static class PathStyles
{
    public static readonly PathStyle[] All = [PathStyle.Full, PathStyle.Quoted, PathStyle.Name, PathStyle.Unix];
    public static string Raw(this PathStyle style) => style.ToString().ToLowerInvariant();
    public static string Title(this PathStyle style) => L("path." + style.Raw());

    public static string Format(this PathStyle style, string path) => style switch
    {
        PathStyle.Full => path,
        PathStyle.Quoted => "\"" + path + "\"",
        PathStyle.Name => FileNames.Name(path),
        _ => path.Replace('\\', '/'),
    };
}

public enum HashAlgorithm { Sha256, Sha1, Md5 }

public static class HashAlgorithms
{
    public static readonly HashAlgorithm[] All = [HashAlgorithm.Sha256, HashAlgorithm.Sha1, HashAlgorithm.Md5];

    public static string Name(this HashAlgorithm algorithm) => algorithm switch
    {
        HashAlgorithm.Sha256 => "SHA-256",
        HashAlgorithm.Sha1 => "SHA-1",
        _ => "MD5",
    };

    public static string Raw(this HashAlgorithm algorithm) => algorithm.Name().ToLowerInvariant();
}

public enum CollageLayout { Grid, Row, Column }

public static class CollageLayouts
{
    public static readonly CollageLayout[] All = [CollageLayout.Grid, CollageLayout.Row, CollageLayout.Column];
    public static string Raw(this CollageLayout layout) => layout.ToString().ToLowerInvariant();
    public static string Title(this CollageLayout layout) => L("collage." + layout.Raw());

    public static string Symbol(this CollageLayout layout) => layout switch
    {
        CollageLayout.Grid => "square.grid.2x2",
        CollageLayout.Row => "rectangle.split.3x1",
        _ => "rectangle.split.1x2",
    };
}

public abstract record SplitPlan
{
    public sealed record Parts(int Count) : SplitPlan;
    public sealed record Every(double Seconds) : SplitPlan;
}

public enum Visualizer { Wave, Square, Spectrum }

public static class Visualizers
{
    public static readonly Visualizer[] All = [Visualizer.Wave, Visualizer.Square, Visualizer.Spectrum];
    public static string Raw(this Visualizer style) => style.ToString().ToLowerInvariant();
    public static string Title(this Visualizer style) => L("viz." + style.Raw());
}

public enum Detection { Faces, Text }

public static class ActionCatalog
{
    private static PickOption Action(string id, string title, string detail, string symbol, IReadOnlyList<PickOption>? variants = null,
                                     PickAction? perform = null) => new()
    {
        Id = "action." + id,
        Title = title,
        Detail = detail,
        Symbol = symbol,
        ProducesFiles = false,
        Variants = variants ?? [],
        Interactive = perform == null && variants is { Count: > 0 } && variants[0].Interactive,
        Perform = perform ?? variants![0].Perform,
    };

    private static PickOption Sub(string parent, string key, string title, PickAction perform, string symbol = "",
                                  bool interactive = false) => new()
    {
        Id = "action." + parent + "." + key,
        Title = title,
        Symbol = symbol,
        ProducesFiles = false,
        Interactive = interactive,
        Perform = perform,
    };

    public static List<PickOption> Options(IReadOnlyList<string> files)
    {
        var count = files.Count;
        var preferred = Terminals.Preferred;
        var terminals = new List<TerminalKind> { preferred };
        terminals.AddRange(Terminals.Installed.Where(kind => kind != preferred));
        var editors = Editors.Installed;
        var folders = FileActions.QuickFolders(files);

        // "Choose…" opens the folder picker first: its card waits until there is something to move.
        List<PickOption> Relocate(bool move) => folders.Select(folder =>
            Sub(move ? "move" : "duplicate", folder.Key, folder.Title,
                ctx => FileActions.Relocate(ctx.Files, move, folder.Path, ctx.BeginWork), folder.Symbol, interactive: folder.Path == null)).ToList();

        PickAction OpenTerminal(TerminalKind kind) => ctx =>
        {
            Terminals.Open(kind, Terminals.FolderFor(ctx.Files[0]));
            return Task.FromResult(PickResult.Quiet);
        };

        PickAction OpenEditor(EditorChoice editor) => ctx =>
        {
            Editors.Open(ctx.Files, editor);
            return Task.FromResult(PickResult.Quiet);
        };

        return
        [
            Action("share", L("action.share"), L("action.share.d"), "square.and.arrow.up", perform: async ctx =>
            {
                await Platform.Actions.ShareAsync(ctx.Files, ctx.Anchor);
                return PickResult.Quiet;
            }),
            Action("terminal", L("action.terminal"), L("action.terminal.d", preferred.Label()), "terminal",
                   variants: terminals.Count > 1 ? terminals.Select(kind => Sub("terminal", kind.Raw(), kind.Label(), OpenTerminal(kind))).ToList() : [],
                   perform: OpenTerminal(preferred)),
            Action("copypath", L("action.copypath"), count > 1 ? LP("action.copypath.many.d", count) : L("action.copypath.d"), "link",
                   variants: PathStyles.All.Select(style => Sub("copypath", style.Raw(), style.Title(), ctx => FileActions.CopyPaths(ctx.Files, style))).ToList()),
            Action("copyfile", L("action.copyfile"), L("action.copyfile.d"), "doc.on.doc", perform: ctx => FileActions.CopyFiles(ctx.Files)),
            Action("move", L("action.move"), L("action.move.d"), "folder", variants: Relocate(true)),
            Action("duplicate", L("action.duplicate"), L("action.duplicate.d"), "plus.square.on.square", variants: Relocate(false)),
            Action("float", L("action.float"), L("action.float.d"), "pin", perform: ctx =>
            {
                Platform.Actions.Float(ctx.Files, ctx.Anchor);
                return Task.FromResult(PickResult.Quiet);
            }),
            Action("openwith", L("action.openwith"), L("action.openwith.d"), "apps.list", perform: ctx =>
            {
                Platform.Actions.OpenWith(ctx.Files[0]);
                return Task.FromResult(PickResult.Quiet);
            }),
            Action("editor", editors[0].Label, L("action.editor.d", editors[0].Label), "chevron.left.forwardslash.chevron.right",
                   variants: editors.Count > 1 ? editors.Select(editor => Sub("editor", FileNames.Sanitize(editor.Label).ToLowerInvariant().Replace(' ', '-'), editor.Label, OpenEditor(editor))).ToList() : [],
                   perform: OpenEditor(editors[0])),
            Action("hash", L("action.hash"), L("action.hash.d"), "number",
                   variants: HashAlgorithms.All.Select(algorithm => Sub("hash", algorithm.Raw(), algorithm.Name(), ctx => FileActions.CopyChecksums(ctx, algorithm))).ToList()),
        ];
    }
}
