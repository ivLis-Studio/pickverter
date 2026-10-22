using ImageMagick;

namespace Pickverter;

public sealed record MetaField(string Key, string Value);

public sealed record MetaInfoRow(string Label, string Value);

public enum MetaFamily { Image, Pdf, Media }

public sealed class MetaSnapshot
{
    public required MetaFamily Family { get; init; }
    public List<MetaField> Fields { get; set; } = [];
    public List<MetaInfoRow> Info { get; set; } = [];
    public bool HasLocation { get; set; }
    public bool Writable { get; set; } = true;
    public string? Reason { get; set; }
}

/// Title, author, date and the like for photos (EXIF, plus the XP tags File Explorer shows), PDFs and media.
public static class MetadataKit
{
    public static MetaFamily? Family(string path) => FileKinds.Of(path) switch
    {
        FileKind.Image => MetaFamily.Image,
        FileKind.Pdf => MetaFamily.Pdf,
        FileKind.Video or FileKind.Audio => MetaFamily.Media,
        _ => null,
    };

    public static IReadOnlyList<string> Keys(MetaFamily family) => family switch
    {
        MetaFamily.Image => ["title", "author", "copyright", "date", "keywords"],
        MetaFamily.Pdf => ["title", "author", "subject", "keywords", "creator"],
        _ => MediaEngine.TagKeys,
    };

    public static string Label(string key, MetaFamily family) => family == MetaFamily.Media && key == "date" ? L("meta.year") : L("meta." + key);

    public static string Placeholder(string key) => key switch
    {
        "date" => "2026:10:22 14:30:00",
        "keywords" => L("meta.keywords.hint"),
        _ => "",
    };

    private static string? XpText(IExifProfile exif, ExifTag<byte[]> tag)
    {
        var bytes = exif.GetValue(tag)?.Value;
        if (bytes == null || bytes.Length < 2) return null;
        return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
    }

    private static double? Degrees(IExifProfile exif, ExifTag<Rational[]> tag, ExifTag<string> reference, string negative)
    {
        var parts = exif.GetValue(tag)?.Value;
        if (parts == null || parts.Length < 3) return null;
        var value = parts[0].ToDouble() + parts[1].ToDouble() / 60 + parts[2].ToDouble() / 3600;
        return exif.GetValue(reference)?.Value == negative ? -value : value;
    }

    public static async Task<MetaSnapshot> Read(string path)
    {
        var family = Family(path) ?? throw new PickException(L("err.meta_unsupported"));
        var values = new Dictionary<string, string>();
        var info = new List<MetaInfoRow>();
        var snapshot = new MetaSnapshot { Family = family };
        switch (family)
        {
            case MetaFamily.Image:
            {
                using var image = new MagickImage();
                try
                {
                    image.Ping(path);
                }
                catch (MagickException)
                {
                    throw new PickException(L("err.read_image"));
                }
                var exif = image.GetExifProfile();
                var iptc = image.GetIptcProfile();
                if (exif != null)
                {
                    values["title"] = exif.GetValue(ExifTag.ImageDescription)?.Value ?? XpText(exif, ExifTag.XPTitle) ?? "";
                    values["author"] = exif.GetValue(ExifTag.Artist)?.Value ?? XpText(exif, ExifTag.XPAuthor) ?? "";
                    values["copyright"] = exif.GetValue(ExifTag.Copyright)?.Value ?? "";
                    values["date"] = exif.GetValue(ExifTag.DateTimeOriginal)?.Value ?? exif.GetValue(ExifTag.DateTime)?.Value ?? "";
                    values["keywords"] = XpText(exif, ExifTag.XPKeywords)?.Replace(";", ", ") ?? "";
                    var make = exif.GetValue(ExifTag.Make)?.Value?.Trim();
                    var model = exif.GetValue(ExifTag.Model)?.Value?.Trim();
                    var camera = string.Join(" ", new[] { make, model }.Where(part => !string.IsNullOrEmpty(part)));
                    if (camera.Length > 0) info.Add(new MetaInfoRow(L("meta.camera"), camera));
                    if (exif.GetValue(ExifTag.LensModel)?.Value is { Length: > 0 } lens) info.Add(new MetaInfoRow(L("meta.lens"), lens));
                    var latitude = Degrees(exif, ExifTag.GPSLatitude, ExifTag.GPSLatitudeRef, "S");
                    var longitude = Degrees(exif, ExifTag.GPSLongitude, ExifTag.GPSLongitudeRef, "W");
                    if (latitude is { } lat && longitude is { } lon)
                    {
                        snapshot.HasLocation = true;
                        info.Add(new MetaInfoRow(L("meta.location"), string.Create(CultureInfo.InvariantCulture,
                            $"{Math.Abs(lat):0.00000}° {(lat < 0 ? "S" : "N")}, {Math.Abs(lon):0.00000}° {(lon < 0 ? "W" : "E")}")));
                    }
                }
                if (iptc != null)
                {
                    if (!values.TryGetValue("title", out var title) || title.Length == 0) values["title"] = iptc.GetValue(IptcTag.Caption)?.Value ?? "";
                    if (!values.TryGetValue("author", out var author) || author.Length == 0) values["author"] = iptc.GetValue(IptcTag.Byline)?.Value ?? "";
                    if (!values.TryGetValue("copyright", out var copyright) || copyright.Length == 0) values["copyright"] = iptc.GetValue(IptcTag.CopyrightNotice)?.Value ?? "";
                    if (!values.TryGetValue("keywords", out var keywords) || keywords.Length == 0)
                        values["keywords"] = string.Join(", ", iptc.GetAllValues(IptcTag.Keyword).Select(value => value.Value));
                }
                if (ImageEngine.OrientedSize(path) is { } size) info.Insert(0, new MetaInfoRow(L("meta.size"), $"{size.Width} × {size.Height}"));
                snapshot.Writable = ImageEngine.CanEncode(ImageEngine.OutputExt(path));
                break;
            }
            case MetaFamily.Pdf:
            {
                var fields = PdfEngine.Info(path);
                foreach (var key in Keys(family)) values[key] = fields.GetValueOrDefault(key) ?? "";
                info.Add(new MetaInfoRow(L("meta.pages"), fields["pages"]));
                if (fields["producer"].Length > 0) info.Add(new MetaInfoRow(L("meta.producer"), fields["producer"]));
                break;
            }
            case MetaFamily.Media:
            {
                values = await MediaEngine.ReadTags(path);
                var probe = await MediaEngine.ProbeAsync(path);
                if (probe.Duration > 0) info.Add(new MetaInfoRow(L("meta.duration"), TimeText.Format(probe.Duration, precise: false)));
                if (probe.DisplaySize is { } size) info.Add(new MetaInfoRow(L("meta.size"), $"{size.Width} × {size.Height}"));
                var codecs = new[] { probe.VideoCodec, probe.AudioCodec }.Where(codec => codec != null && codec != "native").ToList();
                if (codecs.Count > 0) info.Add(new MetaInfoRow(L("meta.codecs"), string.Join(" · ", codecs)));
                if (Tools.FFmpeg == null)
                {
                    snapshot.Writable = false;
                    snapshot.Reason = Tools.FFmpegHint;
                }
                break;
            }
        }
        info.Add(new MetaInfoRow(L("meta.file_size"), FileNames.FormatBytes(FileNames.Size(path))));
        snapshot.Fields = Keys(family).Select(key => new MetaField(key, values.GetValueOrDefault(key) ?? "")).ToList();
        snapshot.Info = info;
        return snapshot;
    }

    /// "2026-10-22 14:30" / "2026.10.22" -> EXIF's "2026:10:22 14:30:00".
    public static string ExifDate(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0) return "";
        var parts = trimmed.Split(' ', 2);
        var date = parts[0].Replace('-', ':').Replace('.', ':').Replace('/', ':');
        var time = parts.Length > 1 ? parts[1] : "00:00:00";
        if (time.Split(':').Length == 2) time += ":00";
        return date + " " + time;
    }

    private static void ApplyFields(IExifProfile exif, MetadataEdit edit)
    {
        var fields = edit.Fields;
        static byte[] Xp(string text) => Encoding.Unicode.GetBytes(text + "\0");
        if (fields.TryGetValue("title", out var title))
        {
            exif.SetValue(ExifTag.ImageDescription, title);
            exif.SetValue(ExifTag.XPTitle, Xp(title));
        }
        if (fields.TryGetValue("author", out var author))
        {
            exif.SetValue(ExifTag.Artist, author);
            exif.SetValue(ExifTag.XPAuthor, Xp(author));
        }
        if (fields.TryGetValue("copyright", out var copyright)) exif.SetValue(ExifTag.Copyright, copyright);
        if (fields.TryGetValue("date", out var date) && ExifDate(date) is { Length: > 0 } stamp) exif.SetValue(ExifTag.DateTimeOriginal, stamp);
        if (fields.TryGetValue("keywords", out var keywords))
        {
            var list = keywords.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            exif.SetValue(ExifTag.XPKeywords, Xp(string.Join(";", list)));
        }
        if (edit.RemoveLocation) ImageEngine.RemoveGps(exif);
    }

    /// JPEG: only the EXIF segment is replaced, the picture itself is not re-encoded.
    private static bool TrySpliceJpeg(string src, MetadataEdit edit, string destination)
    {
        var jpeg = File.ReadAllBytes(src);
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return false;
        ExifProfile exif;
        using (var image = new MagickImage())
        {
            image.Ping(src);
            exif = image.GetExifProfile() as ExifProfile ?? new ExifProfile();
        }
        ApplyFields(exif, edit);
        exif.RemoveThumbnail();
        var payload = exif.ToByteArray() ?? [];
        if (payload.Length < 6 || payload[0] != (byte)'E' || payload[1] != (byte)'x') payload = [.. "Exif\0\0"u8.ToArray(), .. payload];
        if (payload.Length > 65_533) return false;
        using var output = new MemoryStream(jpeg.Length + payload.Length);
        output.Write(jpeg, 0, 2);
        var position = 2;
        var wroteExif = false;
        void WriteExif()
        {
            if (wroteExif) return;
            wroteExif = true;
            var length = payload.Length + 2;
            output.Write([0xFF, 0xE1, (byte)(length >> 8), (byte)(length & 0xFF)]);
            output.Write(payload);
        }
        while (position + 4 <= jpeg.Length && jpeg[position] == 0xFF)
        {
            var marker = jpeg[position + 1];
            if (marker == 0xDA) break;
            if (marker is 0x01 or >= 0xD0 and <= 0xD7)
            {
                output.Write(jpeg, position, 2);
                position += 2;
                continue;
            }
            var length = (jpeg[position + 2] << 8) | jpeg[position + 3];
            if (length < 2 || position + 2 + length > jpeg.Length) return false;
            var isExif = marker == 0xE1 && length >= 8 && jpeg[position + 4] == (byte)'E' && jpeg[position + 5] == (byte)'x' && jpeg[position + 6] == (byte)'i' && jpeg[position + 7] == (byte)'f';
            var isXmp = marker == 0xE1 && !isExif;
            if (marker != 0xE0 && !wroteExif) WriteExif();
            var dropXmp = isXmp && edit.RemoveLocation && Encoding.UTF8.GetString(jpeg, position + 4, length - 2).Contains("GPS", StringComparison.Ordinal);
            if (!isExif && !dropXmp) output.Write(jpeg, position, 2 + length);
            position += 2 + length;
        }
        WriteExif();
        output.Write(jpeg, position, jpeg.Length - position);
        File.WriteAllBytes(destination, output.ToArray());
        return true;
    }

    public static async Task WriteImage(string src, MetadataEdit edit, string destination)
    {
        if (FileNames.NormalizedExt(src) == "jpg" && FileNames.NormalizedExt(destination) == "jpg")
        {
            try
            {
                if (TrySpliceJpeg(src, edit, destination)) return;
            }
            catch (Exception error)
            {
                Debug.Log("jpeg splice: " + error.Message);
            }
            FileSystem.TryDelete(destination);
        }
        // Other formats: re-encode once at high quality with the new info.
        using var image = ImageEngine.Load(src);
        var exif = image.GetExifProfile() ?? new ExifProfile();
        ApplyFields(exif, edit);
        image.SetProfile(exif);
        var iptc = image.GetIptcProfile() as IptcProfile ?? new IptcProfile();
        if (edit.Fields.TryGetValue("title", out var title)) iptc.SetValue(IptcTag.Caption, title);
        if (edit.Fields.TryGetValue("author", out var author)) iptc.SetValue(IptcTag.Byline, author);
        if (edit.Fields.TryGetValue("copyright", out var copyright)) iptc.SetValue(IptcTag.CopyrightNotice, copyright);
        image.SetProfile(iptc);
        if (edit.RemoveLocation) ImageEngine.RemoveLocation(image);
        await ImageEngine.Encode(image, destination, FileNames.NormalizedExt(destination), 0.95);
    }

    public static async Task<PickResult> Apply(string src, MetadataEdit edit, PickContext ctx)
    {
        var family = Family(src) ?? throw new PickException(L("err.meta_unsupported"));
        var (stem, sourceExt) = FileNames.Split(FileNames.Name(src));
        var ext = sourceExt;
        if (family == MetaFamily.Image && !ImageEngine.CanEncode(FileNames.NormalizedExt(src)) && FileNames.NormalizedExt(src) != "jpg")
            ext = ImageEngine.OutputExt(src);
        var suffix = L("sfx.edited");
        var destination = edit.ReplaceOriginal
            ? Path.Combine(Path.GetDirectoryName(src)!, FileNames.Sanitize($"{stem} {suffix}.{ext}"))
            : ctx.DestinationFor(src, ext, suffix);
        var output = await ctx.Produce(destination, async tmp =>
        {
            switch (family)
            {
                case MetaFamily.Image:
                    await WriteImage(src, edit, tmp);
                    break;
                case MetaFamily.Pdf:
                    PdfEngine.WriteInfo(src, edit.Fields, tmp);
                    break;
                default:
                    await MediaEngine.WriteTags(src, edit.Fields, tmp, ctx);
                    break;
            }
        });
        if (!edit.ReplaceOriginal || ext != sourceExt) return PickResult.Of(output);
        await Platform.Shell.RecycleAsync([src]);
        File.Move(output, src);
        return new PickResult { Outputs = [src], Message = L("meta.replaced"), Undoable = false };
    }
}
