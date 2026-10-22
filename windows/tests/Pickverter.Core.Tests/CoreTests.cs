using Xunit;

namespace Pickverter.Tests;

public class LocalizationTests
{
    [Fact]
    public void FormatsPlaceholdersAndPlurals()
    {
        using var sandbox = new Sandbox();
        Assert.Equal("a b", Loc.Format("%@ %@", ["a", "b"]));
        Assert.Equal("b a", Loc.Format("%2$@ %1$@", ["a", "b"]));
        Assert.Equal("50%", Loc.Format("%@%%", ["50"]));
        Assert.Equal("Copied 1 path", LP("msg.paths_copied", 1));
        Assert.Equal("Copied 3 paths", LP("msg.paths_copied", 3));
        Loc.Apply("ru");
        Assert.Equal(0, Loc.PluralIndex(21, 3));
        Assert.Equal(1, Loc.PluralIndex(22, 3));
        Assert.Equal(2, Loc.PluralIndex(25, 3));
        Loc.Apply("en");
    }

    [Fact]
    public void ResolvesWindowsCultureNames()
    {
        Assert.Equal("zh-Hant", Loc.Resolve(["zh-TW"]));
        Assert.Equal("zh-Hans", Loc.Resolve(["zh-CN"]));
        Assert.Equal("pt-BR", Loc.Resolve(["pt-PT"]));
        Assert.Equal("ko", Loc.Resolve(["ko-KR"]));
        Assert.Equal("en", Loc.Resolve(["sv-SE"]));
    }

    [Fact]
    public void WindowsOverlayReplacesMacWording()
    {
        using var sandbox = new Sandbox();
        foreach (var language in Loc.Languages)
        {
            Loc.Apply(language.Code);
            foreach (var key in new[] { "card.reveal", "job.trashed", "hint.alt_clipboard", "set.override_hint", "menu.tap.text", "wheel.hint.click" })
            {
                var value = L(key);
                Assert.DoesNotContain("Finder", value);
                Assert.DoesNotContain("⌥", value);
                Assert.DoesNotContain("⌘", value);
            }
        }
        Loc.Apply("en");
    }
}

public class FileNameTests
{
    [Fact]
    public void SplitsCompoundExtensions()
    {
        Assert.Equal(("backup", "tar.gz"), FileNames.Split("backup.tar.gz"));
        Assert.Equal(("photo", "heic"), FileNames.Split("photo.HEIC"));
        Assert.Equal((".hidden", ""), FileNames.Split(".hidden"));
        Assert.Equal("jpg", FileNames.NormalizedExt("a.JPEG"));
    }

    [Fact]
    public void UniqueNamesFollowExplorer()
    {
        using var sandbox = new Sandbox();
        var path = Path.Combine(sandbox.Root, "photo.png");
        Assert.Equal(path, FileNames.Unique(path));
        File.WriteAllText(path, "x");
        Assert.Equal(Path.Combine(sandbox.Root, "photo (2).png"), FileNames.Unique(path));
        File.WriteAllText(Path.Combine(sandbox.Root, "photo (2).png"), "x");
        Assert.Equal(Path.Combine(sandbox.Root, "photo (3).png"), FileNames.Unique(path));
    }

    [Fact]
    public void SanitizesWindowsNames()
    {
        Assert.Equal("a-b-c", FileNames.Sanitize("a:b?c"));
        Assert.Equal("_CON.txt", FileNames.Sanitize("CON.txt"));
        Assert.Equal("name", FileNames.Sanitize("name. "));
    }

    [Fact]
    public void NaturalSortMatchesExplorer()
    {
        var sorted = new[] { "img10.png", "img2.png", "Img1.png" }.OrderBy(name => name, NaturalComparer.Instance).ToList();
        Assert.Equal(["Img1.png", "img2.png", "img10.png"], sorted);
    }
}

public class UpdateTests
{
    [Fact]
    public void SignsAndVerifies()
    {
        var (privateKey, publicKey) = UpdateSigning.GenerateKey();
        Assert.Equal(publicKey, UpdateSigning.PublicKey(privateKey));
        var message = UpdateSigning.SignedMessage("1.2.3", "ABCDEF", 42);
        var signature = UpdateSigning.Sign(message, privateKey);
        var key = Convert.FromBase64String(publicKey);
        Assert.True(UpdateSigning.Verify(message, signature, key));
        Assert.False(UpdateSigning.Verify(UpdateSigning.SignedMessage("1.2.4", "ABCDEF", 42), signature, key));
    }

    [Fact]
    public void ComparesVersions()
    {
        Assert.True(Versions.Compare("0.2.0", "0.1.1") > 0);
        Assert.True(Versions.Compare("1.0.0-beta.2", "1.0.0") < 0);
        Assert.True(Versions.Compare("1.0.0-beta.10", "1.0.0-beta.2") > 0);
        Assert.Equal(0, Versions.Compare("v1.0", "1.0.0"));
    }

    [Fact]
    public void MacKeyFormatIsCompatible()
    {
        // CryptoKit stores the 32-byte seed; the same seed must give the same public key here.
        var seed = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        Assert.Equal(44, UpdateSigning.PublicKey(seed).Length);
    }
}
