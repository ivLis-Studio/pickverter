namespace Pickverter;

public enum FileKind { Image, Video, Audio, Pdf, Document, Hwp, Office, Table, Json, Subtitle, Archive, Folder, Other }

public static class FileKinds
{
    public static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "jfif", "png", "heic", "heif", "webp", "avif", "tif", "tiff", "gif", "bmp", "ico", "icns", "svg", "jp2",
        "psd", "tga", "dng", "cr2", "cr3", "nef", "arw", "raf", "orf", "rw2", "exr", "jxl",
    };
    public static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "m4v", "mov", "mkv", "webm", "avi", "wmv", "flv", "mpg", "mpeg", "3gp", "ts", "mts", "m2ts", "ogv",
    };
    public static readonly HashSet<string> AudioExts = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp3", "m4a", "aac", "wav", "aif", "aiff", "aifc", "flac", "ogg", "oga", "opus", "wma", "caf", "amr", "m4b",
    };
    public static readonly HashSet<string> DocumentExts = new(StringComparer.OrdinalIgnoreCase)
    {
        "docx", "doc", "rtf", "txt", "text", "md", "markdown", "html", "htm", "odt",
    };
    /// Hangul Word Processor documents (Hancom Office), read and written by rhwp.
    public static readonly HashSet<string> HwpExts = new(StringComparer.OrdinalIgnoreCase) { "hwp", "hwpx" };
    public static readonly HashSet<string> TableExts = new(StringComparer.OrdinalIgnoreCase) { "csv", "tsv" };
    /// SubRip, WebVTT and SAMI (.smi, the format Korean subtitles long came in).
    public static readonly HashSet<string> SubtitleExts = new(StringComparer.OrdinalIgnoreCase) { "srt", "vtt", "smi", "sami" };
    public static readonly HashSet<string> ArchiveExts = new(StringComparer.OrdinalIgnoreCase)
    {
        "zip", "tar", "gz", "tgz", "bz2", "tbz", "tbz2", "xz", "txz", "rar", "7z",
    };

    public static FileKind Of(string path)
    {
        var ext = Path.GetExtension(path.TrimEnd('\\', '/')).TrimStart('.').ToLowerInvariant();
        if (OfficeEngine.Exts.Contains(ext)) return FileKind.Office;
        if (Directory.Exists(path)) return FileKind.Folder;
        if (ext == "pdf") return FileKind.Pdf;
        if (ext == "json") return FileKind.Json;
        if (HwpExts.Contains(ext)) return FileKind.Hwp;
        if (ImageExts.Contains(ext)) return FileKind.Image;
        if (VideoExts.Contains(ext)) return FileKind.Video;
        if (AudioExts.Contains(ext)) return FileKind.Audio;
        if (DocumentExts.Contains(ext)) return FileKind.Document;
        if (TableExts.Contains(ext)) return FileKind.Table;
        if (SubtitleExts.Contains(ext)) return FileKind.Subtitle;
        if (ArchiveExts.Contains(ext)) return FileKind.Archive;
        return Platform.Shell.PerceivedType(ext) switch
        {
            "image" => FileKind.Image,
            "video" => FileKind.Video,
            "audio" => FileKind.Audio,
            _ => FileKind.Other,
        };
    }

    public static string Raw(this FileKind kind) => kind.ToString().ToLowerInvariant();
}

public static class FileNames
{
    private static readonly string[] CompoundExtensions = ["tar.gz", "tar.bz2", "tar.xz"];

    /// The last path component, also for folders given with a trailing separator.
    public static string Name(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    /// Splits a file name into base and lowercased extension, aware of ".tar.gz" style names.
    public static (string Base, string Ext) Split(string name)
    {
        var lower = name.ToLowerInvariant();
        foreach (var compound in CompoundExtensions)
        {
            if (lower.EndsWith("." + compound, StringComparison.Ordinal) && name.Length > compound.Length + 1)
                return (name[..^(compound.Length + 1)], compound);
        }
        var dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1) return (name, "");
        return (name[..dot], name[(dot + 1)..].ToLowerInvariant());
    }

    public static string NormalizedExt(string path) => Split(Name(path)).Ext switch
    {
        "jpeg" or "jfif" => "jpg",
        "tif" => "tiff",
        "heif" => "heic",
        "htm" => "html",
        "aif" or "aifc" => "aiff",
        "markdown" => "md",
        "text" => "txt",
        "tgz" => "tar.gz",
        "oga" => "ogg",
        var other => other,
    };

    /// Explorer-style unique name: "photo.png" -> "photo (2).png" -> "photo (3).png".
    public static string Unique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? "";
        var (stem, ext) = Split(Name(path));
        for (var n = 2; ; n++)
        {
            var name = ext.Length == 0 ? $"{stem} ({n})" : $"{stem} ({n}).{ext}";
            var candidate = Path.Combine(dir, name);
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// A name Windows accepts: no reserved characters or device names, no trailing dots or spaces.
    public static string Sanitize(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name) builder.Append(c < 32 || "<>:\"/\\|?*".Contains(c) ? '-' : c);
        var clean = builder.ToString().TrimEnd('.', ' ').Trim();
        if (clean.Length == 0) clean = "untitled";
        var stem = clean.Split('.')[0];
        if (Reserved.Contains(stem)) clean = "_" + clean;
        return clean.Length > 200 ? clean[..200] : clean;
    }

    public static long Size(string path)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            if (!Directory.Exists(path)) return 0;
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; } catch (IOException) { }
            }
            return total;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    public static string FormatBytes(long bytes) => Platform.Shell.FormatBytes(bytes);

    /// "photo.heic" -> "photo"
    public static string Base(string path) => Split(Name(path)).Base;
}

public static class FileSystem
{
    public static bool CanWrite(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return false;
            var probe = Path.Combine(directory, ".pickverter-probe-" + Guid.NewGuid().ToString("N")[..8]);
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// File or folder, across volumes too.
    public static void Move(string source, string destination)
    {
        if (File.Exists(source))
        {
            File.Move(source, destination);
            return;
        }
        try
        {
            Directory.Move(source, destination);
        }
        catch (IOException) when (!Directory.Exists(destination) && !string.Equals(Path.GetPathRoot(source),
                                       Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase))
        {
            CopyDirectory(source, destination);
            TryDelete(source);
        }
    }

    public static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(source)) CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }

    public static void Copy(string source, string destination)
    {
        if (File.Exists(source)) File.Copy(source, destination);
        else CopyDirectory(source, destination);
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception)
        {
        }
    }

    public static string TempFolder(string prefix)
    {
        var folder = Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// Plain text, whatever its encoding (see TextEncodings.Decode).
    public static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return DecodeText(bytes);
    }

    public static string DecodeText(byte[] bytes) => TextEncodings.Decode(bytes);

    private static bool codePagesRegistered;

    public static void EnsureCodePages()
    {
        if (codePagesRegistered) return;
        codePagesRegistered = true;
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch (Exception) { }
    }

    /// UTF-8 text. A CSV starts with a byte order mark: without one Excel reads it in the PC's legacy code page and
    /// Korean (or any non-English) text comes out broken.
    public static void WriteText(string path, string text)
    {
        var csv = string.Equals(Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase);
        if (csv && text.StartsWith('\uFEFF')) text = text[1..];
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: csv));
    }

    /// A CSV another program wrote (Excel, LibreOffice): saved again as UTF-8 with a byte order mark when it has none.
    public static void EnsureCsvBom(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.AsSpan().StartsWith(TextEncodings.Utf8Bom)) return;
        WriteText(path, DecodeText(bytes));
    }
}
