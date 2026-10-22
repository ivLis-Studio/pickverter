namespace Pickverter;

/// Decides which options appear on each ring for the current selection. Order matters: the first item sits at
/// 12 o'clock and positions stay stable between uses.
public static class Catalog
{
    public static List<PickOption> Options(IReadOnlyList<string> files, WheelMode mode)
    {
        if (files.Count == 0) return [];
        var kinds = files.Select(FileKinds.Of).ToList();
        return mode switch
        {
            WheelMode.Convert => ConvertOptions(files, kinds),
            WheelMode.Tools => ToolOptions(files, kinds),
            _ => ActionCatalog.Options(files),
        };
    }

    /// Every option and every variant, for the command line.
    public static List<PickOption> AllOptions(IReadOnlyList<string> files) =>
        WheelModes.All.SelectMany(mode => Options(files, mode)).SelectMany(option => new[] { option }.Concat(option.Variants)).ToList();

    // Building blocks

    public static PickOption Variant(string parent, string key, string title, PickAction perform, string symbol = "", bool files = true,
                                     string? unavailable = null, bool interactive = false) => new()
    {
        Id = parent + "." + key,
        Title = title,
        Symbol = symbol,
        Unavailable = unavailable,
        ProducesFiles = files,
        Interactive = interactive,
        Perform = perform,
    };

    public static PickOption Tool(string key, string symbol, PickAction? perform = null, string? detail = null, bool files = true,
                                  string? unavailable = null, bool interactive = false, IReadOnlyList<PickOption>? variants = null)
    {
        variants ??= [];
        return new PickOption
        {
            Id = "tool." + key,
            Title = L("tool." + key),
            Detail = detail ?? L("tool." + key + ".d"),
            Symbol = symbol,
            Unavailable = unavailable,
            ProducesFiles = perform == null ? (variants.Count > 0 ? variants[0].ProducesFiles : files) : files,
            Variants = variants,
            Interactive = perform == null ? variants.Count > 0 && variants[0].Interactive : interactive,
            Perform = perform ?? variants[0].Perform,
        };
    }

    /// Asks once (an editor for the first file), then applies the answer to every selected file.
    public static PickAction Edited<T>(Func<PickContext, Task<T>> ask, Func<string, T, PickContext, Task<string>> apply) => async ctx =>
    {
        var answer = await ask(ctx);
        ctx.BeginWork();
        return await Batch.PerFile((src, slice) => apply(src, answer, slice))(ctx);
    };

    public sealed record Level(string Key, string Title, double Image, int Crf);

    public static IReadOnlyList<Level> Levels =>
    [
        new("balanced", L("q.balanced"), 0.68, 26),
        new("high", L("q.high"), 0.82, 22),
        new("small", L("q.small"), 0.5, 30),
    ];

    public static string SpeedTitle(double factor) =>
        (factor == Math.Round(factor) ? ((int)factor).ToString(CultureInfo.InvariantCulture) : factor.ToString(CultureInfo.InvariantCulture)) + "×";

    private static string SpeedKey(double factor) => factor.ToString("0.0", CultureInfo.InvariantCulture);

    // Convert

    public static IReadOnlyList<string> Targets(string path, FileKind kind)
    {
        var source = FileNames.NormalizedExt(path);
        return kind switch
        {
            FileKind.Image when source == "gif" => ["mp4", "webm", "png", "jpg", "webp", "tiff", "pdf"],
            FileKind.Image => ["jpg", "png", "webp", "avif", "heic", "tiff", "gif", "bmp", "pdf", "ico"],
            FileKind.Video => ["mp4", "mov", "webm", "mkv", "gif", "mp3", "m4a", "wav"],
            FileKind.Audio => ["mp3", "m4a", "wav", "flac", "aiff", "ogg", "opus"],
            FileKind.Pdf => ["png", "jpg", "txt", "md", "docx", "rtf", "html"],
            FileKind.Document => ["pdf", "docx", "rtf", "txt", "md", "html", "odt", "epub"],
            FileKind.Office => OfficeEngine.Targets(source),
            FileKind.Table => ["csv", "tsv", "json", "md", "html"],
            FileKind.Json => ["csv", "tsv", "md", "html"],
            FileKind.Subtitle => ["srt", "vtt", "txt"],
            FileKind.Archive => ["zip", "tar.gz"],
            _ => [],
        };
    }

    public static string FormatLabel(string ext) => ext switch
    {
        "webp" => "WebP",
        "opus" => "Opus",
        "webm" => "WebM",
        _ => ext.ToUpperInvariant(),
    };

    public static string FormatSymbol(string ext) => ext switch
    {
        "jpg" or "png" or "heic" or "webp" or "avif" or "tiff" or "bmp" or "ico" => "photo",
        "gif" or "mp4" or "mov" or "webm" or "mkv" => "film",
        "mp3" or "m4a" or "wav" or "flac" or "aiff" or "ogg" or "opus" => "waveform",
        "pdf" => "doc.richtext",
        "csv" or "tsv" or "xlsx" => "tablecells",
        "json" => "curlybraces",
        "srt" or "vtt" => "captions.bubble",
        "zip" or "tar.gz" => "archivebox",
        "epub" => "book",
        "pptx" => "rectangle.on.rectangle",
        _ => "doc.text",
    };

    public static List<PickOption> ConvertOptions(IReadOnlyList<string> files, IReadOnlyList<FileKind> kinds)
    {
        List<string>? common = null;
        for (var index = 0; index < files.Count; index++)
        {
            var list = Targets(files[index], kinds[index]);
            common = common == null ? [.. list] : common.Where(list.Contains).ToList();
        }
        var sources = files.Select(FileNames.NormalizedExt).Distinct().ToList();
        var kindSet = kinds.ToHashSet();
        var result = new List<PickOption>();
        foreach (var ext in common ?? [])
        {
            if (sources.Count == 1 && sources[0] == ext) continue;
            var unavailable = ConvertAvailability(ext, files, kindSet);
            PickAction Run(ConvertOptions options) => Batch.PerFile((src, ctx) => Converter.Convert(src, ext, options, ctx));
            var id = "convert." + ext;
            var option = new PickOption
            {
                Id = id,
                Title = FormatLabel(ext),
                Symbol = FormatSymbol(ext),
                IsFormat = true,
                OutputExt = ext,
                Unavailable = unavailable,
                Perform = Run(new ConvertOptions()),
            };
            if (ext == "gif" && kindSet.Contains(FileKind.Video))
            {
                option = option with
                {
                    Variants =
                    [
                        Variant(id, "720", L("gif.medium"), Run(new ConvertOptions(GifWidth: 720, GifFps: 15)), unavailable: unavailable),
                        Variant(id, "480", L("gif.small"), Run(new ConvertOptions(GifWidth: 480, GifFps: 12)), unavailable: unavailable),
                        Variant(id, "full", L("gif.large"), Run(new ConvertOptions(GifWidth: null, GifFps: 15)), unavailable: unavailable),
                    ],
                };
            }
            if (ext is "png" or "jpg" && kindSet.Count == 1 && kindSet.Contains(FileKind.Pdf))
            {
                option = option with
                {
                    Variants = new[] { 300, 150, 600 }.Select(dpi =>
                        Variant(id, dpi.ToString(CultureInfo.InvariantCulture), $"{dpi} dpi", Run(new ConvertOptions(Dpi: dpi)), unavailable: unavailable)).ToList(),
                };
            }
            result.Add(option);
        }
        return result;
    }

    public static string? ConvertAvailability(string ext, IReadOnlyList<string> files, IReadOnlySet<FileKind> kinds)
    {
        var hasFFmpeg = Tools.FFmpeg != null;
        foreach (var kind in kinds)
        {
            switch (kind)
            {
                case FileKind.Image:
                    if (ext is "mp4" or "webm" or "mov")
                    {
                        if (!hasFFmpeg) return Tools.FFmpegHint;
                    }
                    else if (ImageEngine.EncodeUnavailable(ext) is { } reason)
                    {
                        return reason;
                    }
                    break;
                case FileKind.Video:
                case FileKind.Audio:
                    if (!hasFFmpeg)
                    {
                        foreach (var file in files)
                        {
                            var kindOf = FileKinds.Of(file);
                            if (kindOf is not (FileKind.Video or FileKind.Audio)) continue;
                            if (!Platform.Media.CanTranscode(FileNames.NormalizedExt(file), ext)) return Tools.FFmpegHint;
                        }
                    }
                    break;
                case FileKind.Pdf:
                    if (ext is "png" or "jpg" && !Platform.Pdf.Available) return L("err.render_page");
                    break;
                case FileKind.Document:
                    foreach (var file in files)
                    {
                        if (FileKinds.Of(file) != FileKind.Document) continue;
                        if (DocumentEngine.Unavailable(FileNames.NormalizedExt(file), ext) is { } reason) return reason;
                    }
                    break;
                case FileKind.Office:
                    foreach (var file in files)
                    {
                        if (FileKinds.Of(file) != FileKind.Office) continue;
                        if (OfficeEngine.Unavailable(FileNames.NormalizedExt(file), ext) is { } reason) return reason;
                    }
                    break;
            }
        }
        return null;
    }

    // Tools

    public static List<PickOption> ToolOptions(IReadOnlyList<string> files, IReadOnlyList<FileKind> kinds)
    {
        var kindSet = kinds.ToHashSet();
        var list = new List<PickOption>();
        if (kindSet.Count == 1) list = KindTools(kindSet.First(), files);
        if (!(kindSet.Count == 1 && kindSet.Contains(FileKind.Archive)))
        {
            var many = files.Count > 1;
            list.Add(new PickOption
            {
                Id = "tool.zip",
                Title = L(many ? "tool.zip.many" : "tool.zip"),
                Detail = many ? LP("tool.zip.many.d", files.Count) : L("tool.zip.d"),
                Symbol = "archivebox",
                Perform = async ctx => PickResult.Of(await ArchiveEngine.Compress(ctx.Files, "zip", ctx)),
            });
        }
        if (kindSet.Count == 1 && kindSet.Contains(FileKind.Folder))
        {
            list.Add(new PickOption
            {
                Id = "tool.targz",
                Title = "TAR.GZ",
                Detail = L("tool.targz.d"),
                Symbol = "shippingbox",
                Perform = async ctx => PickResult.Of(await ArchiveEngine.Compress(ctx.Files, "tar.gz", ctx)),
            });
            list.Add(Tool("listing", "list.bullet", ctx => ArchiveEngine.CopyListing(ctx.Files), files: false));
        }
        return list;
    }

    public static List<PickOption> RotateVariants(IEnumerable<Turn> turns, Func<string, Turn, PickContext, Task<string>> body, string? unavailable = null) =>
        turns.Select(turn => Variant("tool.rotate", turn.Raw(), turn.Title(), Batch.PerFile((src, ctx) => body(src, turn, ctx)),
                                     symbol: turn.Symbol(), unavailable: unavailable)).ToList();

    public static List<PickOption> CropVariants(bool video, string? unavailable)
    {
        var custom = Variant("tool.crop", "custom", L("crop.custom"),
            Edited(ctx => Platform.Editors.CropAsync(ctx.Files[0], ctx.Files.Count, ctx.Token),
                   (src, answer, ctx) => video ? MediaEngine.CropVideo(src, answer.Rect, ctx) : ImageEngine.Crop(src, answer.Rect, ctx)),
            symbol: "crop", unavailable: unavailable, interactive: true);
        var presets = new (string Key, string Title, double Ratio)[] { ("square", "1:1", 1), ("wide", "16:9", 16.0 / 9), ("tall", "9:16", 9.0 / 16), ("portrait", "4:5", 0.8) };
        var list = new List<PickOption> { custom };
        list.AddRange(presets.Select(preset => Variant("tool.crop", preset.Key, L("crop.center", preset.Title),
            Batch.PerFile((src, ctx) => video ? MediaEngine.CenterCropVideo(src, preset.Ratio, ctx) : ImageEngine.CenterCrop(src, preset.Ratio, ctx)),
            unavailable: unavailable)));
        return list;
    }

    public static PickOption RedactTool(bool video, string? unavailable)
    {
        var custom = Variant("tool.redact", "custom", L("redact.custom"),
            Edited(ctx => Platform.Editors.RedactAsync(ctx.Files[0], ctx.Files.Count, ctx.Token),
                   (src, answer, ctx) => video ? MediaEngine.RedactVideo(src, answer.Regions, answer.Style, ctx)
                                               : ImageEngine.Redact(src, answer.Regions, answer.Style, ctx)),
            symbol: "hand.draw", unavailable: unavailable, interactive: true);
        if (video) return Tool("redact", "checkerboard.rectangle", custom.Perform, unavailable: unavailable, interactive: true);
        return Tool("redact", "checkerboard.rectangle", variants:
        [
            custom,
            Variant("tool.redact", "faces", L("redact.auto_faces"),
                    Batch.PerFile((src, ctx) => ImageEngine.AutoRedact(src, Detection.Faces, RedactStyle.Pixelate, ctx)),
                    symbol: "person.crop.square", unavailable: Platform.Vision.FacesAvailable ? null : L("err.faces_missing")),
            Variant("tool.redact", "text", L("redact.auto_text"),
                    Batch.PerFile((src, ctx) => ImageEngine.AutoRedact(src, Detection.Text, RedactStyle.Fill, ctx)),
                    symbol: "text.viewfinder", unavailable: Platform.Vision.OcrAvailable ? null : L("err.ocr_missing")),
        ]);
    }

    /// "Edit…" (single file) plus the quick removals for images.
    public static PickOption? MetaTool(IReadOnlyList<string> files, bool image, string? unavailable = null)
    {
        var variants = new List<PickOption>();
        if (files.Count == 1)
        {
            variants.Add(Variant("tool.meta", "edit", L("meta.edit"), async ctx =>
            {
                var snapshot = await MetadataKit.Read(ctx.Files[0]);
                if (!snapshot.Writable) throw new PickException(snapshot.Reason ?? L("err.meta_readonly"));
                var edit = await Platform.Editors.MetadataAsync(ctx.Files[0], snapshot, ctx.Token);
                ctx.BeginWork();
                return await MetadataKit.Apply(ctx.Files[0], edit, ctx);
            }, symbol: "pencil", files: false, unavailable: unavailable, interactive: true));
        }
        if (image)
        {
            variants.Add(Variant("tool.meta", "all", L("strip.all"), Batch.PerFile((src, ctx) => ImageEngine.StripMetadata(src, false, ctx)), symbol: "eye.slash"));
            variants.Add(Variant("tool.meta", "gps", L("strip.gps"), Batch.PerFile((src, ctx) => ImageEngine.StripMetadata(src, true, ctx)), symbol: "location.slash"));
        }
        if (variants.Count == 0) return null;
        if (variants.Count == 1)
        {
            return Tool("meta", "info.circle", variants[0].Perform, detail: L("tool.meta.d"), files: false, unavailable: unavailable, interactive: true);
        }
        return Tool("meta", "info.circle", detail: L("tool.meta.image.d"), variants: variants);
    }

    public static List<PickOption> SplitVariants(string? unavailable)
    {
        var plans = new (string Key, string Title, SplitPlan Plan)[]
        {
            ("2", L("split.halves"), new SplitPlan.Parts(2)),
            ("3", L("split.thirds"), new SplitPlan.Parts(3)),
            ("60", L("split.every", "1"), new SplitPlan.Every(60)),
            ("300", L("split.every", "5"), new SplitPlan.Every(300)),
            ("600", L("split.every", "10"), new SplitPlan.Every(600)),
        };
        return plans.Select(plan => Variant("tool.parts", plan.Key, plan.Title,
            Batch.PerFile((src, ctx) => MediaEngine.Split(src, plan.Plan, ctx)), unavailable: unavailable)).ToList();
    }

    public static PickOption CutTool(bool video, string? unavailable) => Tool("cut", "timeline.selection", async ctx =>
    {
        var first = ctx.Files[0];
        var duration = await MediaEngine.Duration(first);
        var range = await Platform.Editors.TrimAsync(first, duration, video, ctx.Token);
        ctx.BeginWork();
        return await Batch.PerFile((src, slice) => MediaEngine.Cut(src, range, slice))(ctx);
    }, unavailable: unavailable, interactive: true);

    public static PickOption JoinTool(int count, bool audio, string? unavailable) =>
        Tool("join", "rectangle.stack.badge.plus", ctx => MediaEngine.Join(ctx),
             detail: LP(audio ? "tool.join.audio.d" : "tool.join.d", count), unavailable: unavailable);

    public static List<PickOption> KindTools(FileKind kind, IReadOnlyList<string> files)
    {
        var hasFFmpeg = Tools.FFmpeg != null;
        var noFFmpeg = hasFFmpeg ? null : Tools.FFmpegHint;
        // Trim, snapshots and splitting also work with Windows' own media stack.
        var basicMedia = hasFFmpeg || Platform.Media.Available ? null : Tools.FFmpegHint;
        var many = files.Count > 1;
        switch (kind)
        {
            case FileKind.Image:
            {
                var list = new List<PickOption>
                {
                    Tool("compress", "arrow.down.right.and.arrow.up.left", variants: Levels.Select(level =>
                        Variant("tool.compress", level.Key, level.Title, Batch.PerFile((src, ctx) => ImageEngine.Compress(src, level.Image, ctx)))).ToList()),
                    Tool("resize", "arrow.down.forward.and.arrow.up.backward", variants:
                        new[] { 0.5, 0.25, 0.75 }.Select(scale => Variant("tool.resize", ((int)(scale * 100)).ToString(CultureInfo.InvariantCulture),
                                $"{(int)(scale * 100)}%", Batch.PerFile((src, ctx) => ImageEngine.Resize(src, scale, null, ctx))))
                            .Concat(new[] { 1280, 1920, 3840 }.Select(side => Variant("tool.resize", side.ToString(CultureInfo.InvariantCulture),
                                $"{side}px", Batch.PerFile((src, ctx) => ImageEngine.Resize(src, null, side, ctx))))).ToList()),
                    Tool("crop", "crop", variants: CropVariants(false, null)),
                    Tool("removebg", "scissors", variants: CutoutStyles.All.Select(style =>
                        Variant("tool.removebg", style.Raw(), style.Title(), Batch.PerFile((src, ctx) => ImageEngine.RemoveBackground(src, style, ctx)))).ToList()),
                    RedactTool(false, null),
                    Tool("ocr", "text.viewfinder", ctx => ImageEngine.RecognizeText(ctx), files: false,
                         unavailable: Platform.Vision.OcrAvailable ? null : L("err.ocr_missing")),
                    Tool("rotate", "rotate.right", variants: RotateVariants(Turns.All, ImageEngine.Transform)),
                };
                if (MetaTool(files, true) is { } meta) list.Add(meta);
                if (many)
                {
                    list.Add(Tool("topdf", "doc.richtext", ctx => ImageEngine.CombineToPdf(ctx), detail: LP("tool.topdf.d", files.Count)));
                    list.Add(Tool("collage", "square.grid.2x2", detail: LP("tool.collage.d", files.Count), variants: CollageLayouts.All.Select(layout =>
                        Variant("tool.collage", layout.Raw(), layout.Title(), ctx => ImageEngine.Collage(ctx, layout), symbol: layout.Symbol())).ToList()));
                    list.Add(Tool("picturebook", "book", async ctx => PickResult.Of(await EpubBuilder.FromImages(ctx.Files, ctx)),
                                  detail: LP("tool.picturebook.d", files.Count)));
                }
                return list;
            }
            case FileKind.Video:
            {
                var list = new List<PickOption>
                {
                    Tool("compress", "arrow.down.right.and.arrow.up.left", unavailable: noFFmpeg, variants: Levels.Select(level =>
                        Variant("tool.compress", level.Key, level.Title, Batch.PerFile((src, ctx) => MediaEngine.CompressVideo(src, level.Crf, ctx)), unavailable: noFFmpeg)).ToList()),
                    CutTool(true, basicMedia),
                    Tool("resolution", "rectangle.compress.vertical", unavailable: noFFmpeg, variants: new[] { 720, 1080, 480 }.Select(height =>
                        Variant("tool.resolution", height.ToString(CultureInfo.InvariantCulture), $"{height}p",
                                Batch.PerFile((src, ctx) => MediaEngine.Downscale(src, height, ctx)), unavailable: noFFmpeg)).ToList()),
                    Tool("crop", "crop", unavailable: noFFmpeg, variants: CropVariants(true, noFFmpeg)),
                    Tool("speed", "hare", unavailable: noFFmpeg, variants: new[] { 2.0, 1.5, 4.0, 0.5 }.Select(factor =>
                        Variant("tool.speed", SpeedKey(factor), SpeedTitle(factor), Batch.PerFile((src, ctx) => MediaEngine.Speed(src, factor, ctx)), unavailable: noFFmpeg)).ToList()),
                    Tool("rotate", "rotate.right", unavailable: noFFmpeg,
                         variants: RotateVariants([Turn.Right, Turn.Left, Turn.Half, Turn.FlipH], MediaEngine.RotateVideo, noFFmpeg)),
                    Tool("mute", "speaker.slash", Batch.PerFile(MediaEngine.Mute), unavailable: noFFmpeg),
                    Tool("snapshot", "camera", unavailable: basicMedia, variants: new (string Key, double Position)[] { ("middle", 0.5), ("first", 0.0), ("last", 1.0) }
                        .Select(frame => Variant("tool.snapshot", frame.Key, L("frame." + frame.Key),
                                Batch.PerFile((src, ctx) => MediaEngine.Snapshot(src, frame.Position, ctx)), unavailable: basicMedia)).ToList()),
                    Tool("parts", "square.split.2x1", unavailable: basicMedia, variants: SplitVariants(basicMedia)),
                    RedactTool(true, noFFmpeg),
                };
                if (MetaTool(files, false, noFFmpeg) is { } meta) list.Add(meta);
                if (many) list.Insert(1, JoinTool(files.Count, false, noFFmpeg));
                return list;
            }
            case FileKind.Audio:
            {
                var list = new List<PickOption>
                {
                    CutTool(false, basicMedia),
                    Tool("normalize", "waveform", unavailable: noFFmpeg, variants: new (string Key, int Lufs)[] { ("podcast", -16), ("stream", -14), ("broadcast", -23) }
                        .Select(level => Variant("tool.normalize", level.Key, L("lufs." + level.Key),
                                Batch.PerFile((src, ctx) => MediaEngine.Normalize(src, level.Lufs, ctx)), unavailable: noFFmpeg)).ToList()),
                    Tool("silence", "waveform.path.badge.minus", Batch.PerFile(MediaEngine.TrimSilence), unavailable: noFFmpeg),
                    Tool("compress", "arrow.down.right.and.arrow.up.left", unavailable: noFFmpeg, variants: new[] { 128, 64, 192 }.Select(kbps =>
                        Variant("tool.compress", kbps.ToString(CultureInfo.InvariantCulture), $"{kbps} kbps",
                                Batch.PerFile((src, ctx) => MediaEngine.CompressAudio(src, kbps, ctx)), unavailable: noFFmpeg)).ToList()),
                    Tool("mono", "speaker.wave.1", Batch.PerFile(MediaEngine.Mono), unavailable: noFFmpeg),
                    Tool("speed", "hare", unavailable: noFFmpeg, variants: new[] { 2.0, 1.5, 0.5 }.Select(factor =>
                        Variant("tool.speed", SpeedKey(factor), SpeedTitle(factor), Batch.PerFile((src, ctx) => MediaEngine.Speed(src, factor, ctx)), unavailable: noFFmpeg)).ToList()),
                    Tool("parts", "square.split.2x1", unavailable: basicMedia, variants: SplitVariants(basicMedia)),
                    Tool("visualize", "waveform.circle", unavailable: noFFmpeg, variants: Visualizers.All.Select(style =>
                        Variant("tool.visualize", style.Raw(), style.Title(), Batch.PerFile((src, ctx) => MediaEngine.Visualize(src, style, ctx)), unavailable: noFFmpeg)).ToList()),
                };
                if (MetaTool(files, false, noFFmpeg) is { } meta) list.Add(meta);
                if (many) list.Insert(1, JoinTool(files.Count, true, noFFmpeg));
                return list;
            }
            case FileKind.Pdf:
            {
                var noRender = Platform.Pdf.Available ? null : L("err.render_page");
                var list = new List<PickOption>
                {
                    Tool("compress", "arrow.down.right.and.arrow.up.left", Batch.PerFile(PdfEngine.Compress)),
                    Tool("pages", "square.grid.3x3", variants:
                    [
                        Variant("tool.pages", "organize", L("pages.organize"), async ctx =>
                        {
                            var arrangement = await Platform.Editors.PagesAsync(ctx.Files, ctx.Token);
                            ctx.BeginWork();
                            return PickResult.Of(await PdfEngine.Arrange(ctx.Files, arrangement, null, ctx));
                        }, symbol: "square.grid.3x3", interactive: true),
                        Variant("tool.pages", "range", L("pages.range"), Batch.PerFile(PdfEngine.ExtractRange), symbol: "text.insert"),
                        Variant("tool.pages", "reverse", L("pages.reverse"), Batch.PerFile(PdfEngine.Reverse), symbol: "arrow.up.arrow.down"),
                        Variant("tool.pages", "blank", L("pages.blank"), Batch.PerFile(PdfEngine.RemoveBlank), symbol: "doc.badge.ellipsis", unavailable: noRender),
                        Variant("tool.pages", "odd", L("pages.odd"), Batch.PerFile((src, ctx) => PdfEngine.OddEven(src, true, ctx)), symbol: "1.square"),
                        Variant("tool.pages", "even", L("pages.even"), Batch.PerFile((src, ctx) => PdfEngine.OddEven(src, false, ctx)), symbol: "2.square"),
                    ]),
                    Tool("split", "square.split.2x1", Batch.PerFile(PdfEngine.Split)),
                    Tool("rotate", "rotate.right", variants: RotateVariants([Turn.Right, Turn.Left, Turn.Half], PdfEngine.Rotate)),
                    Tool("copytext", "doc.on.clipboard", ctx => PdfEngine.CopyText(ctx), files: false),
                    Tool("lock", "lock", Batch.PerFile(PdfEngine.Encrypt)),
                };
                if (MetaTool(files, false) is { } meta) list.Add(meta);
                if (many) list.Insert(1, Tool("merge", "doc.on.doc", ctx => PdfEngine.Merge(ctx), detail: LP("tool.merge.d", files.Count)));
                return list;
            }
            case FileKind.Document:
            {
                var list = new List<PickOption>
                {
                    Tool("copytext", "doc.on.clipboard", ctx => DocumentEngine.CopyText(ctx), files: false),
                    Tool("count", "number", ctx => DocumentEngine.Count(ctx), files: false),
                };
                if (many) list.Add(Tool("epub", "book", async ctx => PickResult.Of(await EpubBuilder.FromDocuments(ctx.Files, ctx)), detail: LP("tool.epub.d", files.Count)));
                if (files.All(file => FileNames.NormalizedExt(file) is "docx" or "odt"))
                    list.Add(Tool("media", "photo.on.rectangle", Batch.PerFile(OfficeEngine.ExtractMedia)));
                return list;
            }
            case FileKind.Office:
            {
                var list = new List<PickOption>();
                if (files.All(file => FileNames.NormalizedExt(file) is "pptx" or "ppsx" or "xlsx" or "xlsm"))
                    list.Add(Tool("copytext", "doc.on.clipboard", ctx => OfficeEngine.CopyText(ctx), files: false));
                if (files.All(file => OfficeEngine.IsZipBased(FileNames.NormalizedExt(file))))
                    list.Add(Tool("media", "photo.on.rectangle", Batch.PerFile(OfficeEngine.ExtractMedia)));
                return list;
            }
            case FileKind.Json:
                return
                [
                    Tool("pretty", "text.alignleft", Batch.PerFile((src, ctx) => DataEngine.ReformatJson(src, true, ctx))),
                    Tool("minify", "arrow.right.to.line", Batch.PerFile((src, ctx) => DataEngine.ReformatJson(src, false, ctx))),
                ];
            case FileKind.Archive:
                return [Tool("extract", "archivebox", Batch.PerFile(ArchiveEngine.Extract))];
            default:
                return [];
        }
    }
}
