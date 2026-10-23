using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Unicode;

namespace Pickverter;

/// CRC-32 (ISO-HDLC), as ZIP and Info-ZIP's extra fields use it.
internal static class Crc32
{
    private static readonly uint[] Table = Build();

    private static uint[] Build()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    /// The CRC of everything so far (start from 0) followed by data.
    public static uint Append(uint crc, ReadOnlySpan<byte> data)
    {
        var c = ~crc;
        foreach (var b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return ~c;
    }

    public static uint Compute(ReadOnlySpan<byte> data) => Append(0, data);
}

/// Writes ZIP files that read right on every system: names in UTF-8 with the language encoding flag (bit 11) when they
/// aren't plain ASCII, folders as entries (empty ones survive), formats that are compressed already and empty files
/// stored, everything else deflated unless that doesn't make it smaller, ZIP64 once a size, an offset or the number of
/// entries needs it. Entries are written one after the other with their sizes known up front (no data descriptors).
/// They are marked as made on Unix, with Unix permissions next to the FAT attributes, the way Info-ZIP, libarchive and
/// Macs write them: older unzip builds (macOS Terminal's among them) otherwise read the names as DOS code page text.
internal sealed class ZipWriter(Stream output, bool forceZip64 = false)
{
    /// Already compressed: deflating these again only costs time.
    public static readonly HashSet<string> Compressed = new(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "png", "gif", "heic", "heif", "webp", "avif", "mp4", "m4v", "mov", "mkv", "webm", "mp3", "m4a", "aac", "ogg", "opus",
        "flac", "zip", "gz", "tgz", "bz2", "xz", "7z", "rar", "docx", "xlsx", "pptx", "hwpx", "epub", "dmg",
    };

    private const uint Max32 = 0xFFFFFFFF;
    private const ushort Stored = 0, Deflated = 8;
    /// rwxr-xr-x folders, rw-r--r-- files, in the high word; the FAT directory and archive bits in the low one.
    private const uint FolderAttributes = (0x41EDu << 16) | 0x10, FileAttributes = (0x81A4u << 16) | 0x20;
    /// Files up to this size are deflated in memory, bigger ones through a scratch file.
    private const long InMemory = 16 << 20;

    private sealed record Central(byte[] Name, ushort Flags, ushort Method, ushort Time, ushort Date, uint Crc, long Compressed, long Size,
                                  long Offset, uint Attributes);

    private readonly List<Central> central = [];

    public void AddFolder(string name, DateTime modified)
    {
        var bytes = Name(name.EndsWith('/') ? name : name + "/");
        var (time, date) = DosTime(modified);
        var offset = output.Position;
        WriteLocal(bytes, Stored, time, date, 0, 0, 0);
        central.Add(new Central(bytes, Flags(bytes), Stored, time, date, 0, 0, 0, offset, FolderAttributes));
    }

    public async Task AddFileAsync(string name, string path, CancellationToken token)
    {
        var bytes = Name(name);
        var (time, date) = DosTime(LastWrite(path));
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        var length = source.Length;
        var buffer = new byte[1 << 16];
        if (length > 0 && !Compressed.Contains(Path.GetExtension(path).TrimStart('.')))
        {
            // Deflate first, then decide: data that doesn't get smaller is stored as it is.
            await using var scratch = length <= InMemory
                ? (Stream)new MemoryStream()
                : new FileStream(Path.Combine(Path.GetTempPath(), "pickverter-zip-" + Guid.NewGuid().ToString("N")[..10]), FileMode.CreateNew,
                    FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            uint crc = 0;
            long size = 0;
            await using (var deflate = new DeflateStream(scratch, CompressionLevel.Optimal, leaveOpen: true))
            {
                int read;
                while ((read = await source.ReadAsync(buffer, token)) > 0)
                {
                    crc = Crc32.Append(crc, buffer.AsSpan(0, read));
                    size += read;
                    await deflate.WriteAsync(buffer.AsMemory(0, read), token);
                }
            }
            if (scratch.Length < size)
            {
                var offset = output.Position;
                WriteLocal(bytes, Deflated, time, date, crc, scratch.Length, size);
                scratch.Position = 0;
                await scratch.CopyToAsync(output, token);
                central.Add(new Central(bytes, Flags(bytes), Deflated, time, date, crc, scratch.Length, size, offset, FileAttributes));
                return;
            }
            source.Position = 0;
        }
        await StoreAsync(bytes, time, date, source, length, buffer, token);
    }

    /// Copied as it is; the CRC goes into the header once the data has passed through.
    private async Task StoreAsync(byte[] name, ushort time, ushort date, Stream source, long length, byte[] buffer, CancellationToken token)
    {
        var offset = output.Position;
        WriteLocal(name, Stored, time, date, 0, length, length);
        uint crc = 0;
        long copied = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, token)) > 0)
        {
            crc = Crc32.Append(crc, buffer.AsSpan(0, read));
            copied += read;
            await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
        if (copied != length) throw new IOException("The file changed while it was being packed");
        var end = output.Position;
        output.Position = offset + 14;
        var field = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(field, crc);
        output.Write(field);
        output.Position = end;
        central.Add(new Central(name, Flags(name), Stored, time, date, crc, length, length, offset, FileAttributes));
    }

    /// The central directory and the end records. The stream stays open.
    public void Finish()
    {
        var start = output.Position;
        foreach (var entry in central)
        {
            var bigSize = forceZip64 || entry.Size >= Max32 || entry.Compressed >= Max32;
            var bigOffset = forceZip64 || entry.Offset >= Max32;
            var extra = (bigSize ? 16 : 0) + (bigOffset ? 8 : 0);
            var header = new byte[46 + entry.Name.Length + (extra > 0 ? 4 + extra : 0)];
            var span = header.AsSpan();
            var version = (ushort)(bigSize || bigOffset ? 45 : 20);
            BinaryPrimitives.WriteUInt32LittleEndian(span, 0x02014b50);
            // Made by Unix (host 3): permissions in the external attributes' high word, FAT attributes in the low one.
            BinaryPrimitives.WriteUInt16LittleEndian(span[4..], (ushort)(0x0300 | version));
            BinaryPrimitives.WriteUInt16LittleEndian(span[6..], version);
            BinaryPrimitives.WriteUInt16LittleEndian(span[8..], entry.Flags);
            BinaryPrimitives.WriteUInt16LittleEndian(span[10..], entry.Method);
            BinaryPrimitives.WriteUInt16LittleEndian(span[12..], entry.Time);
            BinaryPrimitives.WriteUInt16LittleEndian(span[14..], entry.Date);
            BinaryPrimitives.WriteUInt32LittleEndian(span[16..], entry.Crc);
            BinaryPrimitives.WriteUInt32LittleEndian(span[20..], bigSize ? Max32 : (uint)entry.Compressed);
            BinaryPrimitives.WriteUInt32LittleEndian(span[24..], bigSize ? Max32 : (uint)entry.Size);
            BinaryPrimitives.WriteUInt16LittleEndian(span[28..], (ushort)entry.Name.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(span[30..], (ushort)(extra > 0 ? 4 + extra : 0));
            BinaryPrimitives.WriteUInt32LittleEndian(span[38..], entry.Attributes);
            BinaryPrimitives.WriteUInt32LittleEndian(span[42..], bigOffset ? Max32 : (uint)entry.Offset);
            entry.Name.CopyTo(span[46..]);
            if (extra > 0)
            {
                var field = span[(46 + entry.Name.Length)..];
                BinaryPrimitives.WriteUInt16LittleEndian(field, 0x0001);
                BinaryPrimitives.WriteUInt16LittleEndian(field[2..], (ushort)extra);
                var at = 4;
                if (bigSize)
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(field[at..], (ulong)entry.Size);
                    BinaryPrimitives.WriteUInt64LittleEndian(field[(at + 8)..], (ulong)entry.Compressed);
                    at += 16;
                }
                if (bigOffset) BinaryPrimitives.WriteUInt64LittleEndian(field[at..], (ulong)entry.Offset);
            }
            output.Write(header);
        }
        var end = output.Position;
        var size = end - start;
        var count = central.Count;
        var zip64 = forceZip64 || count >= 0xFFFF || size >= Max32 || start >= Max32;
        if (zip64)
        {
            var record = new byte[56 + 20];
            var span = record.AsSpan();
            BinaryPrimitives.WriteUInt32LittleEndian(span, 0x06064b50);
            BinaryPrimitives.WriteUInt64LittleEndian(span[4..], 44);
            BinaryPrimitives.WriteUInt16LittleEndian(span[12..], 45);
            BinaryPrimitives.WriteUInt16LittleEndian(span[14..], 45);
            BinaryPrimitives.WriteUInt64LittleEndian(span[24..], (ulong)count);
            BinaryPrimitives.WriteUInt64LittleEndian(span[32..], (ulong)count);
            BinaryPrimitives.WriteUInt64LittleEndian(span[40..], (ulong)size);
            BinaryPrimitives.WriteUInt64LittleEndian(span[48..], (ulong)start);
            // The locator: where the ZIP64 end record starts.
            BinaryPrimitives.WriteUInt32LittleEndian(span[56..], 0x07064b50);
            BinaryPrimitives.WriteUInt64LittleEndian(span[64..], (ulong)end);
            BinaryPrimitives.WriteUInt32LittleEndian(span[72..], 1);
            output.Write(record);
        }
        var tail = new byte[22];
        var last = tail.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(last, 0x06054b50);
        BinaryPrimitives.WriteUInt16LittleEndian(last[8..], zip64 ? (ushort)0xFFFF : (ushort)count);
        BinaryPrimitives.WriteUInt16LittleEndian(last[10..], zip64 ? (ushort)0xFFFF : (ushort)count);
        BinaryPrimitives.WriteUInt32LittleEndian(last[12..], zip64 ? Max32 : (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(last[16..], zip64 ? Max32 : (uint)start);
        output.Write(tail);
        output.Flush();
    }

    private void WriteLocal(byte[] name, ushort method, ushort time, ushort date, uint crc, long compressed, long size)
    {
        var zip64 = forceZip64 || compressed >= Max32 || size >= Max32;
        var header = new byte[30 + name.Length + (zip64 ? 20 : 0)];
        var span = header.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, 0x04034b50);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], zip64 ? (ushort)45 : (ushort)20);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], Flags(name));
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], method);
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], time);
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], date);
        BinaryPrimitives.WriteUInt32LittleEndian(span[14..], crc);
        BinaryPrimitives.WriteUInt32LittleEndian(span[18..], zip64 ? Max32 : (uint)compressed);
        BinaryPrimitives.WriteUInt32LittleEndian(span[22..], zip64 ? Max32 : (uint)size);
        BinaryPrimitives.WriteUInt16LittleEndian(span[26..], (ushort)name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(span[28..], zip64 ? (ushort)20 : (ushort)0);
        name.CopyTo(span[30..]);
        if (zip64)
        {
            var field = span[(30 + name.Length)..];
            BinaryPrimitives.WriteUInt16LittleEndian(field, 0x0001);
            BinaryPrimitives.WriteUInt16LittleEndian(field[2..], 16);
            BinaryPrimitives.WriteUInt64LittleEndian(field[4..], (ulong)size);
            BinaryPrimitives.WriteUInt64LittleEndian(field[12..], (ulong)compressed);
        }
        output.Write(header);
    }

    private static byte[] Name(string name)
    {
        var bytes = Encoding.UTF8.GetBytes(name);
        if (bytes.Length > ushort.MaxValue) throw new PathTooLongException(name);
        return bytes;
    }

    /// Bit 11, "language encoding": the name is UTF-8. Plain ASCII names go without it, as every tool writes them.
    private static ushort Flags(byte[] name) => name.Any(b => b >= 0x80) ? (ushort)0x0800 : (ushort)0;

    private static DateTime LastWrite(string path)
    {
        try
        {
            return File.GetLastWriteTime(path);
        }
        catch (Exception)
        {
            return DateTime.Now;
        }
    }

    /// MS-DOS date and time (local, two-second steps, 1980 to 2107).
    internal static (ushort Time, ushort Date) DosTime(DateTime local)
    {
        if (local.Year < 1980) local = new DateTime(1980, 1, 1, 0, 0, 0);
        if (local.Year > 2107) local = new DateTime(2107, 12, 31, 23, 59, 58);
        return ((ushort)((local.Hour << 11) | (local.Minute << 5) | (local.Second / 2)), (ushort)(((local.Year - 1980) << 9) | (local.Month << 5) | local.Day));
    }
}

/// Reads entry names from a ZIP's central directory, raw, so the encoding can be decided for the whole archive.
internal static class ZipNames
{
    public sealed record Record(ushort Flags, byte[] Name, byte[] Extra);

    /// The central directory's records in order (ZipArchive.Entries lists them the same way), or null when it can't be
    /// read (then ZipArchive's own reading, with ZipNameEncoding, names the entries).
    public static List<Record>? ReadCentralDirectory(Stream stream)
    {
        try
        {
            var length = stream.Length;
            if (length < 22) return null;
            // The end record: the last 22 bytes plus a comment of up to 65,535 bytes.
            var tailLength = (int)Math.Min(length, 22 + 65_535);
            var tail = new byte[tailLength];
            stream.Position = length - tailLength;
            stream.ReadExactly(tail);
            var end = -1;
            for (var index = tailLength - 22; index >= 0; index--)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index)) == 0x06054b50)
                {
                    end = index;
                    break;
                }
            }
            if (end < 0) return null;
            long count = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 10));
            long size = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 12));
            long offset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 16));
            var endPosition = length - tailLength + end;
            if (count == 0xFFFF || size == 0xFFFFFFFF || offset == 0xFFFFFFFF)
            {
                if (endPosition < 20) return null;
                var locator = new byte[20];
                stream.Position = endPosition - 20;
                stream.ReadExactly(locator);
                if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != 0x07064b50) return null;
                var recordPosition = (long)BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8));
                if (recordPosition < 0 || recordPosition + 56 > length) return null;
                var record = new byte[56];
                stream.Position = recordPosition;
                stream.ReadExactly(record);
                if (BinaryPrimitives.ReadUInt32LittleEndian(record) != 0x06064b50) return null;
                count = (long)BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(32));
                size = (long)BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(40));
                offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(48));
            }
            if (offset < 0 || size < 0 || count < 0 || offset + size > length || size > 1L << 30) return null;
            var directory = new byte[size];
            stream.Position = offset;
            stream.ReadExactly(directory);
            var records = new List<Record>();
            var position = 0;
            while (records.Count < count)
            {
                if (position + 46 > directory.Length || BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(position)) != 0x02014b50) return null;
                var flags = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(position + 8));
                int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(position + 28));
                int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(position + 30));
                int commentLength = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(position + 32));
                var start = position + 46;
                if (start + nameLength + extraLength + commentLength > directory.Length) return null;
                records.Add(new Record(flags, directory[start..(start + nameLength)], directory[(start + nameLength)..(start + nameLength + extraLength)]));
                position = start + nameLength + extraLength + commentLength;
            }
            return records;
        }
        catch (Exception error) when (error is IOException or ArgumentException or OverflowException or OutOfMemoryException)
        {
            Debug.Log("zip directory: " + error.Message);
            return null;
        }
    }

    /// Every entry's name, in NFC. An Info-ZIP Unicode Path field (0x7075) whose CRC matches wins; names flagged as
    /// UTF-8 (and valid) are UTF-8; the rest share one encoding when one fits them all (UTF-8, as Macs write them
    /// without the flag; the PC's own OEM code page, see TextEncodings.DecodeZipName; CP949, Korean Windows), and are
    /// read name by name otherwise.
    public static List<string> Decode(IReadOnlyList<Record> records, int oemCodePage)
    {
        var names = new string[records.Count];
        var legacy = new List<int>();
        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            if (UnicodePath(record) is { } unicode) names[index] = unicode;
            else if (record.Name.All(b => b < 0x80)) names[index] = Encoding.ASCII.GetString(record.Name);
            else if ((record.Flags & 0x0800) != 0 && Utf8.IsValid(record.Name)) names[index] = Encoding.UTF8.GetString(record.Name);
            else legacy.Add(index);
        }
        if (legacy.Count > 0)
        {
            var raw = legacy.Select(index => records[index].Name).ToList();
            int? shared = raw.All(name => Utf8.IsValid(name)) ? 65001
                : TextEncodings.HasOwnZipCodePage(oemCodePage) && raw.All(name => TextEncodings.Fits(name, oemCodePage)) ? oemCodePage
                : raw.All(name => TextEncodings.Fits(name, 949)) ? 949
                : null;
            foreach (var index in legacy)
            {
                var name = records[index].Name;
                names[index] = shared switch
                {
                    65001 => Encoding.UTF8.GetString(name),
                    { } page => TextEncodings.Strict(page).GetString(name),
                    _ => TextEncodings.DecodeZipName(name, oemCodePage),
                };
            }
        }
        return names.Select(NameRepair.Nfc).ToList();
    }

    /// Info-ZIP's Unicode Path extra field: version 1, the CRC-32 of the plain name, then the name in UTF-8.
    private static string? UnicodePath(Record record)
    {
        var extra = record.Extra.AsSpan();
        while (extra.Length >= 4)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(extra);
            int size = BinaryPrimitives.ReadUInt16LittleEndian(extra[2..]);
            if (4 + size > extra.Length) break;
            var data = extra.Slice(4, size);
            if (id == 0x7075 && size > 5 && data[0] == 1 && BinaryPrimitives.ReadUInt32LittleEndian(data[1..]) == Crc32.Compute(record.Name) &&
                Utf8.IsValid(data[5..]))
                return Encoding.UTF8.GetString(data[5..]);
            extra = extra[(4 + size)..];
        }
        return null;
    }
}

/// What ZipArchive decodes the names of entries without the UTF-8 flag with, when the central directory couldn't be
/// read separately: name by name, as TextEncodings.DecodeZipName reads them.
internal sealed class ZipNameEncoding(int oemCodePage) : Encoding
{
    private string Decode(byte[] bytes, int index, int count) => TextEncodings.DecodeZipName(bytes.AsSpan(index, count), oemCodePage);

    public override string GetString(byte[] bytes, int index, int count) => Decode(bytes, index, count);
    public override int GetCharCount(byte[] bytes, int index, int count) => Decode(bytes, index, count).Length;

    public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
    {
        var text = Decode(bytes, byteIndex, byteCount);
        text.CopyTo(0, chars, charIndex, text.Length);
        return text.Length;
    }

    public override int GetMaxCharCount(int byteCount) => byteCount;
    public override int GetByteCount(char[] chars, int index, int count) => UTF8.GetByteCount(chars, index, count);
    public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex) => UTF8.GetBytes(chars, charIndex, charCount, bytes, byteIndex);
    public override int GetMaxByteCount(int charCount) => UTF8.GetMaxByteCount(charCount);
}
