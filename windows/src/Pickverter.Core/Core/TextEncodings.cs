using System.Text.Unicode;

namespace Pickverter;

/// Text between Macs, Korean Windows and everything else: byte order marks, strict UTF-8, CP949 (EUC-KR, what Korean
/// Windows wrote for decades) and the legacy code page of the user's language.
public static class TextEncodings
{
    /// How much of a file the "is this EUC-KR?" check reads.
    public const int SniffBytes = 256 * 1024;

    public static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    /// Plain-text formats: Fix Encoding offers itself for these.
    public static readonly HashSet<string> TextLike = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt", "text", "md", "markdown", "csv", "tsv", "srt", "vtt", "smi", "sami", "json", "html", "htm",
    };

    public static bool IsTextLike(string path) => TextLike.Contains(FileNames.Split(FileNames.Name(path)).Ext);

    public static Encoding Strict(int codePage)
    {
        FileSystem.EnsureCodePages();
        return Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    public static Encoding Lossy(int codePage)
    {
        FileSystem.EnsureCodePages();
        return Encoding.GetEncoding(codePage, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
    }

    public static bool HasUnicodeBom(ReadOnlySpan<byte> data) =>
        data.StartsWith(Utf8Bom) || (data.Length >= 2 && ((data[0] == 0xFF && data[1] == 0xFE) || (data[0] == 0xFE && data[1] == 0xFF)));

    /// Valid UTF-8. truncated: the data stops mid-file, so a sequence the end cuts in two doesn't count against it.
    public static bool IsUtf8(ReadOnlySpan<byte> data, bool truncated = false) =>
        Utf8.IsValid(truncated ? data[..CompleteUtf8Length(data)] : data);

    /// The length without a multi-byte sequence that the end of the data cuts off.
    private static int CompleteUtf8Length(ReadOnlySpan<byte> data)
    {
        for (var back = 1; back <= Math.Min(3, data.Length); back++)
        {
            var b = data[^back];
            if ((b & 0xC0) == 0x80) continue;
            var needed = b >= 0xF0 ? 4 : b >= 0xE0 ? 3 : b >= 0xC0 ? 2 : 1;
            return needed > back ? data.Length - back : data.Length;
        }
        return data.Length;
    }

    /// Decodes in a code page without a single invalid byte. truncated: a lead byte cut off at the end is fine.
    public static bool Fits(byte[] data, int count, int codePage, bool truncated = false)
    {
        try
        {
            Strict(codePage).GetDecoder().GetCharCount(data, 0, count, flush: !truncated);
            return true;
        }
        catch (Exception error) when (error is DecoderFallbackException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public static bool Fits(byte[] data, int codePage) => Fits(data, data.Length, codePage);

    public static bool IsCp949(byte[] data, int count, bool truncated = false) => Fits(data, count, 949, truncated);

    private static string? TryDecode(ReadOnlySpan<byte> bytes, int codePage)
    {
        try
        {
            return Strict(codePage).GetString(bytes);
        }
        catch (Exception error) when (error is DecoderFallbackException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// The code page Windows used for "ANSI" text in an interface language.
    public static int LegacyCodePage(string language) => language switch
    {
        "ko" => 949,
        "ja" => 932,
        "zh-Hans" => 936,
        "zh-Hant" => 950,
        "ru" => 1251,
        _ => 1252,
    };

    /// Japanese, Chinese and Russian have legacy code pages of their own that CP949 would misread.
    private static int? OwnLegacyCodePage(string language) => LegacyCodePage(language) is var page && page is 932 or 936 or 950 or 1251 ? page : null;

    /// Text of any origin: a byte order mark (UTF-8, UTF-16 LE/BE), then strict UTF-8, then CP949, then a lossy read.
    /// With a Japanese, Chinese or Russian interface that language's own legacy code page goes before CP949 (Shift-JIS,
    /// GBK, Big5 and Windows-1251 text often passes as CP949 and would come out as nonsense); the lossy read uses the
    /// interface language's code page (Windows-1252 for the Latin ones).
    public static string Decode(byte[] bytes)
    {
        var data = bytes.AsSpan();
        if (data.StartsWith(Utf8Bom)) return Encoding.UTF8.GetString(data[3..]);
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) return Encoding.Unicode.GetString(data[2..]);
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(data[2..]);
        if (Utf8.IsValid(data)) return Encoding.UTF8.GetString(data);
        var legacy = LegacyCodePage(Loc.Language);
        if (OwnLegacyCodePage(Loc.Language) is { } own && TryDecode(data, own) is { } local) return local;
        if (TryDecode(data, 949) is { } korean) return korean;
        return Lossy(legacy).GetString(data);
    }

    /// A PC whose OEM code page is neither the US one (437) nor Korean (949): File Explorer there writes ZIP names in it.
    public static bool HasOwnZipCodePage(int oemCodePage) => oemCodePage is not (0 or 437 or 949 or 65001);

    /// The name of a ZIP entry written without the UTF-8 flag: UTF-8 when the bytes are valid UTF-8 (Macs write it
    /// that way), otherwise CP949 (Korean Windows), otherwise CP437 (the original DOS code page). On a PC whose OEM code
    /// page is another one (850, 866, 932, 936…) that code page comes first after UTF-8: File Explorer there writes
    /// names in it, and they would pass as CP949.
    public static string DecodeZipName(ReadOnlySpan<byte> bytes, int oemCodePage)
    {
        if (Utf8.IsValid(bytes)) return Encoding.UTF8.GetString(bytes);
        if (HasOwnZipCodePage(oemCodePage) && TryDecode(bytes, oemCodePage) is { } local) return local;
        if (TryDecode(bytes, 949) is { } korean) return korean;
        return Lossy(437).GetString(bytes);
    }

    private static readonly object CacheGate = new();
    private static readonly Dictionary<(string Path, long Length, long Modified, string Language), bool> FixCache = new();

    /// Plain text whose first 256 KB is not UTF-8 but reads as CP949 (EUC-KR): Fix Encoding rewrites it in UTF-8. Not
    /// when it reads in the interface language's own legacy code page (Shift-JIS, GBK, Big5, Windows-1251): that is
    /// the user's own encoding, which Pickverter reads anyway, and CP949 would garble it.
    public static bool NeedsUtf8Fix(string path)
    {
        if (!IsTextLike(path)) return false;
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists || info.Length == 0) return false;
        }
        catch (Exception)
        {
            return false;
        }
        var key = (info.FullName, info.Length, info.LastWriteTimeUtc.Ticks, Loc.Language);
        lock (CacheGate)
        {
            if (FixCache.TryGetValue(key, out var known)) return known;
        }
        var result = SniffCp949(info.FullName, info.Length);
        lock (CacheGate)
        {
            if (FixCache.Count > 256) FixCache.Clear();
            FixCache[key] = result;
        }
        return result;
    }

    private static bool SniffCp949(string path, long length)
    {
        try
        {
            var count = (int)Math.Min(length, SniffBytes);
            var buffer = new byte[count];
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                stream.ReadExactly(buffer, 0, count);
            }
            var truncated = length > count;
            if (HasUnicodeBom(buffer) || IsUtf8(buffer, truncated)) return false;
            if (OwnLegacyCodePage(Loc.Language) is { } own && Fits(buffer, count, own, truncated)) return false;
            return IsCp949(buffer, count, truncated);
        }
        catch (Exception error)
        {
            Debug.Log("encoding sniff: " + error.Message);
            return false;
        }
    }
}
