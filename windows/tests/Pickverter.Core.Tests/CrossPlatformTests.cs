using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Pickverter.Tests;

/// ZIP files written byte by byte (flags, names and extra fields .NET wouldn't write) and read back the same way.
internal static class RawZip
{
    public sealed record Entry(byte[] Name, byte[] Data, ushort Flags = 0, byte[]? Extra = null);

    public sealed record Header(string Name, byte[] RawName, ushort CentralFlags, ushort LocalFlags, ushort Method, ushort Version);

    /// Stored entries, no ZIP64.
    public static byte[] Build(IEnumerable<Entry> entries)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var central = new List<(Entry Entry, uint Crc, uint Offset)>();
        foreach (var entry in entries)
        {
            var extra = entry.Extra ?? [];
            var crc = Crc32.Compute(entry.Data);
            central.Add((entry, crc, (uint)stream.Position));
            writer.Write(0x04034b50u);
            writer.Write((ushort)20);
            writer.Write(entry.Flags);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)0x21);
            writer.Write(crc);
            writer.Write((uint)entry.Data.Length);
            writer.Write((uint)entry.Data.Length);
            writer.Write((ushort)entry.Name.Length);
            writer.Write((ushort)extra.Length);
            writer.Write(entry.Name);
            writer.Write(extra);
            writer.Write(entry.Data);
        }
        var start = (uint)stream.Position;
        foreach (var (entry, crc, offset) in central)
        {
            var extra = entry.Extra ?? [];
            writer.Write(0x02014b50u);
            writer.Write((ushort)20);
            writer.Write((ushort)20);
            writer.Write(entry.Flags);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)0x21);
            writer.Write(crc);
            writer.Write((uint)entry.Data.Length);
            writer.Write((uint)entry.Data.Length);
            writer.Write((ushort)entry.Name.Length);
            writer.Write((ushort)extra.Length);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write(0u);
            writer.Write(offset);
            writer.Write(entry.Name);
            writer.Write(extra);
        }
        var size = (uint)stream.Position - start;
        writer.Write(0x06054b50u);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)central.Count);
        writer.Write((ushort)central.Count);
        writer.Write(size);
        writer.Write(start);
        writer.Write((ushort)0);
        writer.Flush();
        return stream.ToArray();
    }

    /// Info-ZIP's Unicode Path extra field for a raw name.
    public static byte[] UnicodePath(byte[] rawName, string name, uint? crc = null)
    {
        var utf8 = Encoding.UTF8.GetBytes(name);
        var field = new byte[4 + 5 + utf8.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(field, 0x7075);
        BinaryPrimitives.WriteUInt16LittleEndian(field.AsSpan(2), (ushort)(5 + utf8.Length));
        field[4] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(field.AsSpan(5), crc ?? Crc32.Compute(rawName));
        utf8.CopyTo(field, 9);
        return field;
    }

    /// Every entry's central and local header (an archive without ZIP64 end records).
    public static List<Header> Read(byte[] zip)
    {
        var end = zip.Length - 22;
        while (BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(end)) != 0x06054b50) end--;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(end + 10));
        var position = (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(end + 16));
        var headers = new List<Header>();
        for (var index = 0; index < count; index++)
        {
            Assert.Equal(0x02014b50u, BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(position)));
            var version = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(position + 6));
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(position + 8));
            var method = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(position + 10));
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(position + 28));
            int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(position + 30));
            int commentLength = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(position + 32));
            var local = (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(position + 42));
            var raw = zip.AsSpan(position + 46, nameLength).ToArray();
            Assert.Equal(0x04034b50u, BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(local)));
            var localFlags = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(local + 6));
            int localNameLength = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(local + 26));
            Assert.Equal(raw, zip.AsSpan(local + 30, localNameLength).ToArray());
            headers.Add(new Header(Encoding.UTF8.GetString(raw), raw, flags, localFlags, method, version));
            position += 46 + nameLength + extraLength + commentLength;
        }
        return headers;
    }
}

public class NameTests
{
    internal static string Nfd(string text) => text.Normalize(NormalizationForm.FormD);

    private static List<string> Names(string folder) =>
        Directory.GetFileSystemEntries(folder, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(folder, path)).ToList();

    [Fact]
    public void JoinsHangulJamoAndKanaMarksOnly()
    {
        Assert.NotEqual("한글.txt", Nfd("한글.txt"));
        Assert.Equal("한글.txt", NameRepair.Joined(Nfd("한글.txt")));
        Assert.Equal("がぎ.txt", NameRepair.Joined(Nfd("がぎ.txt")));
        // Accents a Mac also splits (e + ◌́) are not what this fixes; nor are names that are joined already.
        Assert.Null(NameRepair.Joined(Nfd("café.txt")));
        Assert.Null(NameRepair.Joined("한글.txt"));
        Assert.Null(NameRepair.Joined("plain.txt"));
        // Another item already answers to the joined name (File Explorer ignores case): left alone.
        Assert.Null(NameRepair.Target(Nfd("한글.txt"), new HashSet<string>([Nfd("한글.txt"), "한글.TXT"], StringComparer.OrdinalIgnoreCase)));
        Assert.Equal("한글.txt", NameRepair.Target(Nfd("한글.txt"), new HashSet<string>([Nfd("한글.txt"), "other.txt"], StringComparer.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task RenamesSplitNamesAllTheWayDown()
    {
        using var sandbox = new Sandbox();
        var home = Path.Combine(sandbox.Root, "names");
        var root = Path.Combine(home, Nfd("자료"));
        Directory.CreateDirectory(Path.Combine(root, Nfd("사진")));
        File.WriteAllText(Path.Combine(root, Nfd("한글 문서.txt")), "a");
        File.WriteAllText(Path.Combine(root, Nfd("がぎ.txt")), "b");
        File.WriteAllText(Path.Combine(root, Nfd("사진"), Nfd("여름.jpg")), "c");
        File.WriteAllText(Path.Combine(root, "plain.txt"), "d");
        Assert.Equal("action.nfc", ActionCatalog.Options([root]).Last().Id);
        Assert.DoesNotContain(ActionCatalog.Options([sandbox.Fixture("notes.txt")]), option => option.Id == "action.nfc");

        var result = await Sandbox.Run("action.nfc", root);
        Assert.Equal(LP("msg.renamed", 5), result.Message);
        Assert.Empty(result.Outputs);
        Assert.Equal(["자료"], Directory.GetFileSystemEntries(home).Select(Path.GetFileName));
        var fixedRoot = Path.Combine(home, "자료");
        var names = Names(fixedRoot);
        Assert.All(names, name => Assert.True(name.IsNormalized(NormalizationForm.FormC), name));
        Assert.Equal(["plain.txt", "がぎ.txt", "사진", Path.Combine("사진", "여름.jpg"), "한글 문서.txt"], names.Order(StringComparer.Ordinal));
        Assert.Equal("c", File.ReadAllText(Path.Combine(fixedRoot, "사진", "여름.jpg")));

        // Nothing left to join: not offered, and a run says so.
        Assert.DoesNotContain(ActionCatalog.Options([fixedRoot]), option => option.Id == "action.nfc");
        var again = await NameRepair.Join(new PickContext([fixedRoot], OutputLocation.SameFolder, token: TestContext.Current.CancellationToken));
        Assert.Equal(L("err.names_ok"), again.Message);
    }

    [Fact]
    public async Task SkipsANameAnotherFileHas()
    {
        using var sandbox = new Sandbox();
        var folder = Path.Combine(sandbox.Root, "clash");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, Nfd("한글.txt")), "split");
        // APFS matches names regardless of normalization: both spellings can't exist at once there. NTFS keeps them apart.
        Assert.SkipWhen(File.Exists(Path.Combine(folder, "한글.txt")), "this file system treats NFD and NFC names as one");
        File.WriteAllText(Path.Combine(folder, "한글.txt"), "joined");
        File.WriteAllText(Path.Combine(folder, Nfd("가방.txt")), "bag");
        var result = await Sandbox.Run("action.nfc", Path.Combine(folder, Nfd("한글.txt")), Path.Combine(folder, Nfd("가방.txt")));
        Assert.Equal(LP("msg.renamed", 1), result.Message);
        var names = Directory.GetFiles(folder).Select(Path.GetFileName).ToList();
        Assert.Contains(Nfd("한글.txt"), names);
        Assert.Contains("한글.txt", names);
        Assert.Contains("가방.txt", names);
        Assert.Equal("joined", File.ReadAllText(Path.Combine(folder, "한글.txt")));
    }

    [Fact]
    public async Task ResultsGetJoinedNames()
    {
        using var sandbox = new Sandbox();
        var folder = Path.Combine(sandbox.Root, "results");
        Directory.CreateDirectory(folder);
        var photo = Path.Combine(folder, Nfd("사진.jpg"));
        File.Copy(sandbox.Fixture("photo.jpg"), photo);
        var output = Sandbox.Output(await Sandbox.Run("convert.png", photo));
        Assert.Equal("사진.png", Path.GetFileName(output));
        Assert.Contains("사진.png", Directory.GetFiles(folder).Select(Path.GetFileName));
    }
}

public class ZipTests
{
    private static string Nfd(string text) => NameTests.Nfd(text);

    private static string Write(Sandbox sandbox, string name, byte[] data)
    {
        var folder = Path.Combine(sandbox.Root, Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    [Fact]
    public async Task WritesUtf8FlaggedNfcNamesWithoutJunk()
    {
        using var sandbox = new Sandbox();
        var home = Path.Combine(sandbox.Root, "zip");
        var root = Path.Combine(home, Nfd("자료"));
        Directory.CreateDirectory(Path.Combine(root, Nfd("빈 폴더")));
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        var text = "안녕하세요 " + new string('a', 4000);
        File.WriteAllText(Path.Combine(root, Nfd("한글 문서.txt")), text);
        File.WriteAllText(Path.Combine(root, "plain.txt"), "plain");
        File.WriteAllBytes(Path.Combine(root, "empty.txt"), []);
        var noise = RandomNumberGenerator.GetBytes(6000);
        File.WriteAllBytes(Path.Combine(root, "photo.jpg"), noise);
        File.WriteAllBytes(Path.Combine(root, "noise.bin"), noise);
        File.WriteAllText(Path.Combine(root, "sub", Nfd("がぎ.txt")), "kana");
        foreach (var junk in new[] { ".DS_Store", "._" + Nfd("한글 문서.txt"), "Thumbs.db", "desktop.ini" }) File.WriteAllText(Path.Combine(root, junk), "x");
        Directory.CreateDirectory(Path.Combine(root, "__MACOSX", "inner"));
        File.WriteAllText(Path.Combine(root, "__MACOSX", "inner", "x.txt"), "x");
        try
        {
            File.WriteAllText(Path.Combine(root, "Icon\r"), "");
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // Windows doesn't allow a carriage return in a name.
        }

        var zip = Sandbox.Output(await Sandbox.Run("tool.zip", root));
        Assert.Equal("자료.zip", Path.GetFileName(zip));
        var headers = RawZip.Read(File.ReadAllBytes(zip));
        Assert.Equal(new[] { "자료/", "자료/empty.txt", "자료/noise.bin", "자료/photo.jpg", "자료/plain.txt", "자료/sub/", "자료/sub/がぎ.txt", "자료/빈 폴더/", "자료/한글 문서.txt" },
                     headers.Select(header => header.Name).Order(StringComparer.Ordinal));
        foreach (var header in headers)
        {
            Assert.True(header.Name.IsNormalized(NormalizationForm.FormC), header.Name);
            var unicode = header.RawName.Any(b => b >= 0x80);
            Assert.Equal(unicode, (header.CentralFlags & 0x0800) != 0);
            Assert.Equal(unicode, (header.LocalFlags & 0x0800) != 0);
        }
        ushort Method(string name) => headers.Single(header => header.Name == name).Method;
        Assert.Equal(8, Method("자료/한글 문서.txt"));
        Assert.Equal(0, Method("자료/photo.jpg"));
        Assert.Equal(0, Method("자료/empty.txt"));
        Assert.Equal(0, Method("자료/noise.bin"));

        // Any reader gets the same files back.
        using var archive = ZipFile.OpenRead(zip);
        Assert.Equal(text, Sandbox.ZipText(zip, "자료/한글 문서.txt"));
        byte[] Bytes(string name)
        {
            using var stream = archive.GetEntry(name)!.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }
        Assert.Equal(noise, Bytes("자료/photo.jpg"));
        Assert.Equal(noise, Bytes("자료/noise.bin"));
        Assert.Empty(Bytes("자료/empty.txt"));
    }

    [Fact]
    public async Task NumbersTheSecondOfTwoEqualNames()
    {
        using var sandbox = new Sandbox();
        var first = Path.Combine(sandbox.Root, "a", "report.txt");
        var second = Path.Combine(sandbox.Root, "b", "report.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(first)!);
        Directory.CreateDirectory(Path.GetDirectoryName(second)!);
        File.WriteAllText(first, "one");
        File.WriteAllText(second, "two");
        var zip = Sandbox.Output(await Sandbox.Run("tool.zip", first, second));
        using var archive = ZipFile.OpenRead(zip);
        Assert.Equal(["report.txt", "report 2.txt"], archive.Entries.Select(entry => entry.FullName));
        Assert.Equal("two", Sandbox.ZipText(zip, "report 2.txt"));
    }

    [Fact]
    public async Task WritesZip64RecordsThatReadersFollow()
    {
        using var sandbox = new Sandbox();
        var text = Path.Combine(sandbox.Root, "text.txt");
        File.WriteAllText(text, string.Concat(Enumerable.Repeat("Pickverter ", 500)));
        var path = Path.Combine(sandbox.Root, "zip64.zip");
        await using (var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite))
        {
            var writer = new ZipWriter(stream, forceZip64: true);
            writer.AddFolder("폴더", DateTime.Now);
            await writer.AddFileAsync("폴더/text.txt", text, TestContext.Current.CancellationToken);
            await writer.AddFileAsync("폴더/logo.png", sandbox.Fixture("logo.png"), TestContext.Current.CancellationToken);
            writer.Finish();
        }
        using (var archive = ZipFile.OpenRead(path))
        {
            Assert.Equal(["폴더/", "폴더/text.txt", "폴더/logo.png"], archive.Entries.Select(entry => entry.FullName));
        }
        Assert.Equal(File.ReadAllText(text), Sandbox.ZipText(path, "폴더/text.txt"));
        using var file = File.OpenRead(path);
        Assert.Equal(3, ZipNames.ReadCentralDirectory(file)?.Count);
    }

    [Fact]
    public async Task UnzipsKoreanWindowsAndMacNames()
    {
        using var sandbox = new Sandbox();
        ArchiveEngine.OemCodePageOverride = 437;
        try
        {
            var cp949 = TextEncodings.Strict(949);
            // Korean Windows: CP949 names without the UTF-8 flag.
            var korean = Write(sandbox, "korean.zip", RawZip.Build(
            [
                new(cp949.GetBytes("한글/"), []),
                new(cp949.GetBytes("한글/문서.txt"), "hi"u8.ToArray()),
            ]));
            var folder = Sandbox.Output(await Sandbox.Run("tool.extract", korean));
            Assert.Equal("한글", Path.GetFileName(folder));
            Assert.Equal(["문서.txt"], Directory.GetFileSystemEntries(folder).Select(Path.GetFileName));
            Assert.Equal("hi", File.ReadAllText(Path.Combine(folder, "문서.txt")));

            // A Mac: NFD names in UTF-8 without the flag, plus its litter.
            var mac = Write(sandbox, "mac.zip", RawZip.Build(
            [
                new(Encoding.UTF8.GetBytes(Nfd("자료") + "/"), []),
                new(Encoding.UTF8.GetBytes(Nfd("자료") + "/" + Nfd("사진.txt")), "photo"u8.ToArray()),
                new(Encoding.UTF8.GetBytes(Nfd("자료") + "/.DS_Store"), [0]),
                new(Encoding.UTF8.GetBytes(Nfd("자료") + "/Icon\r"), [0]),
                new(Encoding.UTF8.GetBytes("__MACOSX/" + Nfd("자료") + "/._" + Nfd("사진.txt")), [0]),
            ]));
            var unpacked = Sandbox.Output(await Sandbox.Run("tool.extract", mac));
            Assert.Equal("자료", Path.GetFileName(unpacked));
            Assert.Equal(["사진.txt"], Directory.GetFileSystemEntries(unpacked).Select(Path.GetFileName));
            Assert.Equal(["mac.zip", "자료"], Directory.GetFileSystemEntries(Path.GetDirectoryName(mac)!).Select(Path.GetFileName).Order(StringComparer.Ordinal));

            // One encoding for the whole archive: bytes that happen to be valid UTF-8 too are read as CP949 when every
            // other name is CP949 (name by name, the first would come out as a no-break space).
            var both = new byte[] { 0xC2, 0xA0 };
            var mixed = Write(sandbox, "mixed.zip", RawZip.Build(
            [
                new([.. both, .. ".txt"u8], "1"u8.ToArray()),
                new(cp949.GetBytes("가.txt"), "2"u8.ToArray()),
            ]));
            var staging = Path.Combine(sandbox.Root, "mixed-out");
            await ArchiveEngine.UnpackAsync(mixed, staging, TestContext.Current.CancellationToken);
            Assert.Equal(new[] { cp949.GetString(both) + ".txt", "가.txt" }.Order(StringComparer.Ordinal),
                         Directory.GetFiles(staging).Select(Path.GetFileName).Order(StringComparer.Ordinal));

            // Info-ZIP's Unicode Path field wins over the raw name, while its CRC matches.
            var raw = "?.txt"u8.ToArray();
            var unicode = Write(sandbox, "unicode.zip", RawZip.Build(
            [
                new(raw, "u"u8.ToArray(), Extra: RawZip.UnicodePath(raw, "유니코드.txt")),
                new("wrong.txt"u8.ToArray(), "w"u8.ToArray(), Extra: RawZip.UnicodePath("wrong.txt"u8.ToArray(), "무시.txt", crc: 1)),
            ]));
            var named = Path.Combine(sandbox.Root, "unicode-out");
            await ArchiveEngine.UnpackAsync(unicode, named, TestContext.Current.CancellationToken);
            Assert.Equal(new[] { "wrong.txt", "유니코드.txt" }, Directory.GetFiles(named).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        }
        finally
        {
            ArchiveEngine.OemCodePageOverride = null;
        }
    }

    [Fact]
    public async Task NeverWritesOutsideTheFolderOrOpensLockedZips()
    {
        using var sandbox = new Sandbox();
        var evil = "evil-" + Guid.NewGuid().ToString("N")[..8] + ".txt";
        var slip = Write(sandbox, "slip.zip", RawZip.Build(
        [
            new(Encoding.ASCII.GetBytes("../" + evil), "x"u8.ToArray()),
            new(Encoding.ASCII.GetBytes("/" + evil), "x"u8.ToArray()),
            new(Encoding.ASCII.GetBytes("safe/../../" + evil), "x"u8.ToArray()),
            new(Encoding.ASCII.GetBytes("C:/" + evil), "x"u8.ToArray()),
            new(Encoding.ASCII.GetBytes("..\\" + evil), "x"u8.ToArray()),
            new("good.txt"u8.ToArray(), "good"u8.ToArray()),
        ]));
        var staging = Path.Combine(sandbox.Root, "slip-out");
        await ArchiveEngine.UnpackAsync(slip, staging, TestContext.Current.CancellationToken);
        Assert.Equal(["good.txt"], Directory.GetFileSystemEntries(staging).Select(Path.GetFileName));
        Assert.False(File.Exists(Path.Combine(sandbox.Root, evil)));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(slip)!, evil)));

        var locked = Write(sandbox, "locked.zip", RawZip.Build([new("secret.txt"u8.ToArray(), "xxxxxxxxxxxx"u8.ToArray(), Flags: 1)]));
        var error = await Assert.ThrowsAsync<PickException>(() => Sandbox.Run("tool.extract", locked));
        Assert.Equal(L("err.zip_encrypted"), error.Message);
    }

    [Fact]
    public void ReadsOldZipNamesInThePcsOwnCodePage()
    {
        var german = TextEncodings.Strict(850).GetBytes("Bücher.txt");
        Assert.Equal("Bücher.txt", TextEncodings.DecodeZipName(german, 850));
        Assert.Equal("한글.txt", TextEncodings.DecodeZipName(TextEncodings.Strict(949).GetBytes("한글.txt"), 437));
        Assert.Equal("é.txt", TextEncodings.DecodeZipName([0x82, .. ".txt"u8], 437));
    }
}

public class TextTests
{
    private const string Notes = "안녕하세요, 픽버터입니다.\n둘째 줄입니다.";

    [Fact]
    public async Task RewritesEucKrAsUtf8()
    {
        using var sandbox = new Sandbox();
        var folder = Path.Combine(sandbox.Root, "text");
        Directory.CreateDirectory(folder);
        var cp949 = TextEncodings.Strict(949);
        var memo = Path.Combine(folder, "메모.txt");
        File.WriteAllBytes(memo, cp949.GetBytes(Notes));
        Assert.Equal(Notes, FileSystem.ReadText(memo));
        Assert.Equal("tool.utf8", Catalog.Options([memo], WheelMode.Tools)[0].Id);

        var output = Sandbox.Output(await Sandbox.Run("tool.utf8", memo));
        Assert.Equal("메모 UTF-8.txt", Path.GetFileName(output));
        var bytes = File.ReadAllBytes(output);
        Assert.False(bytes.AsSpan().StartsWith(TextEncodings.Utf8Bom));
        Assert.Equal(Notes, new UTF8Encoding(false, true).GetString(bytes));

        // UTF-8 already: not offered, and skipped in a batch.
        var plain = Path.Combine(folder, "plain.txt");
        File.WriteAllText(plain, Notes);
        Assert.DoesNotContain(Catalog.Options([plain], WheelMode.Tools), option => option.Id == "tool.utf8");
        Assert.Single((await Sandbox.Run("tool.utf8", memo, plain)).Outputs);

        // A CSV gets the byte order mark Excel needs; an HTML page says it is UTF-8 now.
        var table = Path.Combine(folder, "표.csv");
        File.WriteAllBytes(table, cp949.GetBytes("이름,점수\n가나,1\n"));
        var csv = File.ReadAllBytes(Sandbox.Output(await Sandbox.Run("tool.utf8", table)));
        Assert.True(csv.AsSpan().StartsWith(TextEncodings.Utf8Bom));
        Assert.Equal("이름,점수\n가나,1\n", Encoding.UTF8.GetString(csv, 3, csv.Length - 3));
        var page = Path.Combine(folder, "page.html");
        File.WriteAllBytes(page, cp949.GetBytes("<html><head><meta http-equiv=\"Content-Type\" content=\"text/html; charset=euc-kr\"><meta charset=\"EUC-KR\"></head><body>한글</body></html>"));
        var html = File.ReadAllText(Sandbox.Output(await Sandbox.Run("tool.utf8", page)));
        Assert.Contains("charset=utf-8\"", html);
        Assert.Contains("<meta charset=\"utf-8\">", html);
        Assert.DoesNotContain("euc-kr", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("한글", html);
    }

    [Fact]
    public void ChecksTheFirst256KbAndForgivesACutCharacter()
    {
        using var sandbox = new Sandbox();
        var folder = Path.Combine(sandbox.Root, "sniff");
        Directory.CreateDirectory(folder);
        var padding = Enumerable.Repeat((byte)'a', TextEncodings.SniffBytes - 1).ToArray();
        // CP949 "가" (B0 A1) cut in two by the 256 KB edge.
        var korean = Path.Combine(folder, "korean.txt");
        File.WriteAllBytes(korean, [.. padding, 0xB0, 0xA1, .. "\n"u8]);
        Assert.True(TextEncodings.NeedsUtf8Fix(korean));
        // UTF-8 "한" (ED 95 9C) cut the same way: UTF-8, not offered.
        var utf8 = Path.Combine(folder, "utf8.txt");
        File.WriteAllBytes(utf8, [.. padding, .. "한"u8, .. "\n"u8]);
        Assert.False(TextEncodings.NeedsUtf8Fix(utf8));
        // Past the edge nothing is read: CP949 there doesn't make a UTF-8 head need fixing.
        var later = Path.Combine(folder, "later.txt");
        File.WriteAllBytes(later, [.. padding, .. "a"u8, .. TextEncodings.Strict(949).GetBytes("가")]);
        Assert.False(TextEncodings.NeedsUtf8Fix(later));
        // Not text, not offered.
        var binary = Path.Combine(folder, "data.bin");
        File.WriteAllBytes(binary, TextEncodings.Strict(949).GetBytes("가나다"));
        Assert.False(TextEncodings.NeedsUtf8Fix(binary));
    }

    [Fact]
    public void LeavesTheInterfaceLanguagesOwnEncodingAlone()
    {
        using var sandbox = new Sandbox();
        var folder = Path.Combine(sandbox.Root, "own");
        Directory.CreateDirectory(folder);
        var japanese = Path.Combine(folder, "japanese.txt");
        File.WriteAllBytes(japanese, TextEncodings.Strict(932).GetBytes("こんにちは"));
        try
        {
            // Shift-JIS that also passes as CP949: offered with an English interface, not with a Japanese one.
            Assert.True(TextEncodings.NeedsUtf8Fix(japanese));
            Loc.Apply("ja");
            Assert.False(TextEncodings.NeedsUtf8Fix(japanese));
            Assert.Equal("こんにちは", FileSystem.ReadText(japanese));
        }
        finally
        {
            Loc.Apply("en");
        }
    }

    [Fact]
    public async Task CsvStartsWithAByteOrderMark()
    {
        using var sandbox = new Sandbox();
        var csv = Sandbox.Output(await Sandbox.Run("convert.csv", sandbox.Fixture("data.json")));
        Assert.True(File.ReadAllBytes(csv).AsSpan().StartsWith(TextEncodings.Utf8Bom));
        var sheets = Sandbox.Output(await Sandbox.Run("convert.csv", sandbox.Fixture("sheet.xlsx")));
        Assert.All(Directory.GetFiles(sheets), file => Assert.True(File.ReadAllBytes(file).AsSpan().StartsWith(TextEncodings.Utf8Bom), file));
        foreach (var ext in new[] { "tsv", "json", "md" })
        {
            var output = Sandbox.Output(await Sandbox.Run("convert." + ext, sandbox.Fixture("people.csv")));
            Assert.False(File.ReadAllBytes(output).AsSpan().StartsWith(TextEncodings.Utf8Bom), ext);
        }
        // Its own CSV reads back as before.
        Assert.Contains("\"name\": \"Alice\"", File.ReadAllText(Sandbox.Output(await Sandbox.Run("convert.json", csv))));
    }
}

public class SamiTests
{
    private const string Sample = """
        <SAMI>
        <HEAD><TITLE>Sample</TITLE>
        <STYLE TYPE="text/css"><!--
        P { margin-left:8pt; }
        .ENCC { Name:English; lang:en-US; }
        .KRCC { Name:Korean; lang:ko-KR; }
        --></STYLE></HEAD>
        <BODY>
        <SYNC Start=1000><P Class=ENCC>Hello<br>World
        <P Class=KRCC>안녕<br>세상
        <SYNC Start=3000><P Class=ENCC>&nbsp;
        <P Class=KRCC>&nbsp;
        <SYNC Start=5000><P Class=ENCC>Second
        <P Class=KRCC><font color="#ffff00">둘째</font> &amp; &lt;줄&gt;
        <SYNC start="7500"><p class="krcc">마지막 &#54620;&#xAE00;</p>
        </BODY>
        </SAMI>
        """;

    [Fact]
    public async Task ConvertsTheKoreanTrackOfASamiFile()
    {
        using var sandbox = new Sandbox();
        var folder = Path.Combine(sandbox.Root, "sami");
        Directory.CreateDirectory(folder);
        var smi = Path.Combine(folder, "movie.smi");
        // SAMI files mostly come in EUC-KR.
        File.WriteAllBytes(smi, TextEncodings.Strict(949).GetBytes(Sample));
        Assert.Equal(FileKind.Subtitle, FileKinds.Of(smi));
        Assert.Equal(FileKind.Subtitle, FileKinds.Of(Path.Combine(folder, "movie.sami")));
        var srt = Sandbox.Output(await Sandbox.Run("convert.srt", smi));
        Assert.Equal("""
            1
            00:00:01,000 --> 00:00:03,000
            안녕
            세상

            2
            00:00:05,000 --> 00:00:07,500
            둘째 & <줄>

            3
            00:00:07,500 --> 00:00:11,500
            마지막 한글

            """.Replace("\r\n", "\n"), File.ReadAllText(srt).Replace("\r\n", "\n"));
        Assert.Contains("00:00:05.000 --> 00:00:07.500", File.ReadAllText(Sandbox.Output(await Sandbox.Run("convert.vtt", smi))));
        Assert.Equal("안녕\n세상\n둘째 & <줄>\n마지막 한글\n", File.ReadAllText(Sandbox.Output(await Sandbox.Run("convert.txt", smi))));
    }

    [Fact]
    public void UsesTheFirstClassWithoutAKoreanOne()
    {
        var cues = DataEngine.ParseSami("<SAMI><BODY><SYNC Start=0><P Class=ENCC>Only English<SYNC Start=2000><P Class=JPCC>日本語</BODY></SAMI>");
        var cue = Assert.Single(cues);
        Assert.Equal(("Only English", 0.0, 4.0), (cue.Text, cue.Start, cue.End));
    }
}
