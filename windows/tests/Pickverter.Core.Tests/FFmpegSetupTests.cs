using System.IO.Compression;
using System.Text.Json;
using Xunit;

namespace Pickverter.Tests;

/// Picking and unpacking the ffmpeg build that Pickverter downloads for itself.
public class FFmpegSetupTests
{
    [Fact]
    public void PicksTheNewestReleaseBranchForThisCpu()
    {
        const string json = """
            {"assets":[
              {"name":"checksums.sha256","size":10,"browser_download_url":"https://github.com/b/checksums.sha256"},
              {"name":"ffmpeg-master-latest-win64-gpl-shared.zip","size":1,"browser_download_url":"https://github.com/b/m.zip","digest":"sha256:AA"},
              {"name":"ffmpeg-n8.1-latest-win64-gpl-shared-8.1.zip","size":2,"browser_download_url":"https://github.com/b/81.zip","digest":"sha256:BB"},
              {"name":"ffmpeg-n9.0-latest-win64-gpl-shared-9.0.zip","size":3,"browser_download_url":"https://github.com/b/90.zip","digest":"sha256:CC"},
              {"name":"ffmpeg-n9.0-latest-win64-gpl-9.0.zip","size":4,"browser_download_url":"https://github.com/b/90-static.zip"},
              {"name":"ffmpeg-n9.0-latest-winarm64-gpl-shared-9.0.zip","size":5,"browser_download_url":"https://github.com/b/arm.zip","digest":"sha256:DD"}
            ]}
            """;
        using var release = JsonDocument.Parse(json);
        var x64 = FFmpegSetup.PickAsset(release.RootElement, "win64");
        Assert.Equal("ffmpeg-n9.0-latest-win64-gpl-shared-9.0.zip", x64?.Name);
        Assert.Equal("cc", x64?.Sha256);
        Assert.Equal("9.0", x64?.Version);
        Assert.Equal("https://github.com/b/arm.zip", FFmpegSetup.PickAsset(release.RootElement, "winarm64")?.Url);
        using var masterOnly = JsonDocument.Parse("""{"assets":[{"name":"ffmpeg-master-latest-win64-gpl-shared.zip","size":1,"browser_download_url":"https://github.com/b/m.zip"}]}""");
        var master = FFmpegSetup.PickAsset(masterOnly.RootElement, "win64");
        Assert.Equal("master", master?.Version);
        Assert.Null(master?.Sha256);
        using var empty = JsonDocument.Parse("""{"message":"API rate limit exceeded"}""");
        Assert.Null(FFmpegSetup.PickAsset(empty.RootElement, "win64"));
    }

    [Fact]
    public void UnpacksOnlyTheProgramsAndTheirLibraries()
    {
        using var sandbox = new Sandbox();
        var zip = Path.Combine(sandbox.Root, "build.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "b/bin/ffmpeg.exe", "b/bin/ffprobe.exe", "b/bin/ffplay.exe", "b/bin/avcodec-63.dll", "b/lib/avcodec.lib",
                                         "b/doc/ffmpeg.html", "b/include/libavcodec/avcodec.h" })
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(name);
            }
        }
        var output = Path.Combine(sandbox.Root, "bin");
        FFmpegSetup.Extract(zip, output);
        Assert.Equal(["avcodec-63.dll", "ffmpeg.exe", "ffprobe.exe"], Directory.GetFiles(output).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void PreparedOptionsNeverLookUnavailable()
    {
        var option = new PickOption { Id = "convert.ogg", Title = "OGG", Unavailable = Tools.FFmpegHint, Perform = _ => Task.FromResult(new PickResult()) };
        Assert.Null(FFmpegSetup.Prepare(option).Unavailable);
        Assert.True(FFmpegSetup.IsMissing(new PickException("tone.wav: " + Tools.FFmpegHint)) || Tools.FFmpeg != null);
    }

    /// The real download (≈90 MB from GitHub). Opt in with PICKVERTER_NET_TESTS=1.
    [Fact]
    public async Task DownloadsTheRealBuild()
    {
        if (Environment.GetEnvironmentVariable("PICKVERTER_NET_TESTS") != "1") return;
        using var sandbox = new Sandbox();
        var folder = Path.Combine(sandbox.Root, "ffmpeg");
        var seen = new List<double>();
        var installing = false;
        await FFmpegSetup.DownloadAsync(folder, seen.Add, () => installing = true, TestContext.Current.CancellationToken);
        Assert.True(installing);
        Assert.NotEmpty(seen);
        Assert.True(File.Exists(Path.Combine(folder, "ffmpeg.exe")));
        Assert.True(File.Exists(Path.Combine(folder, "ffprobe.exe")));
        Assert.NotEmpty(Directory.GetFiles(folder, "avcodec-*.dll"));
        Assert.False(File.Exists(Path.Combine(folder, "ffplay.exe")));
        Assert.Matches(@"^(\d+\.\d+|master)$", File.ReadAllText(Path.Combine(folder, "version.txt")));
        Assert.Empty(Directory.GetDirectories(folder));
    }
}

