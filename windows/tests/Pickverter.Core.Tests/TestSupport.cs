using System.IO.Compression;
using ImageMagick;
using Xunit;



namespace Pickverter.Tests;

/// Fresh settings, a scratch folder and generated fixtures for every test class.
public sealed class Sandbox : IDisposable
{
    private static readonly Lazy<string> SharedFixtures = new(() =>
    {
        var folder = Path.Combine(Path.GetTempPath(), "pickverter-fixtures-" + Environment.ProcessId);
        if (!Directory.Exists(folder)) Fixtures.Make(folder);
        return folder;
    });

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "pickverter-test-" + Guid.NewGuid().ToString("N")[..10]);
    public MemoryClipboard Clipboard { get; } = new();

    public Sandbox()
    {
        Directory.CreateDirectory(Root);
        Platform.Paths = new AppPaths(Path.Combine(Root, "settings"), Path.Combine(Root, "cache"));
        Platform.Clipboard = Clipboard;
        Platform.Main = new InlineMainThread();
        Preferences.UseTransient();
        WheelLayout.UseTransient();
        Loc.Apply("en");
    }

    /// A private copy of a fixture (results land next to it).
    public string Fixture(string name)
    {
        var source = Path.Combine(SharedFixtures.Value, name);
        var folder = Path.Combine(Root, Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, name);
        File.Copy(source, target);
        if (name == "book.md") File.Copy(Path.Combine(SharedFixtures.Value, "logo.png"), Path.Combine(folder, "logo.png"));
        return target;
    }

    public bool HasFixture(string name) => File.Exists(Path.Combine(SharedFixtures.Value, name));

    public static async Task<PickResult> Run(string optionId, params string[] files) =>
        await Run(optionId, ResultDestination.File, files);

    public static async Task<PickResult> Run(string optionId, ResultDestination destination, params string[] files)
    {
        var option = Catalog.AllOptions(files).FirstOrDefault(candidate => candidate.Id == optionId)
                     ?? throw new Xunit.Sdk.XunitException($"no option {optionId} for {string.Join(", ", files.Select(Path.GetFileName))}");
        Assert.Null(option.Unavailable);
        return await JobCenter.RunDirect(option, files, destination);
    }

    public static string Output(PickResult result)
    {
        Assert.NotEmpty(result.Outputs);
        var output = result.Outputs[0];
        Assert.True(File.Exists(output) || Directory.Exists(output), "missing " + output);
        return output;
    }

    public static (uint Width, uint Height) Size(string image)
    {
        var info = new MagickImageInfo(image);
        return (info.Width, info.Height);
    }

    public static int Pages(string pdf)
    {
        using var doc = PdfSharp.Pdf.IO.PdfReader.Open(pdf, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        return doc.PageCount;
    }

    public static string ZipText(string archive, string entry)
    {
        using var zip = ZipFile.OpenRead(archive);
        using var reader = new StreamReader(zip.GetEntry(entry)!.Open());
        return reader.ReadToEnd();
    }

    public static IDisposable Editor(string name, string answer)
    {
        var key = "PICKVERTER_EDITOR_" + name.ToUpperInvariant();
        Environment.SetEnvironmentVariable(key, answer);
        return new Reset(() => Environment.SetEnvironmentVariable(key, null));
    }

    private sealed class Reset(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    public void Dispose()
    {
        FileSystem.TryDelete(Root);
    }
}
