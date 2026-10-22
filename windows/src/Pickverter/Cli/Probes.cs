using Pickverter.Services;
using Pickverter.UI.Editors;

namespace Pickverter.Cli;

/// One small thing per process, to pin down problems that only show when the process exits (CI runs each in turn).
internal static class Probes
{
    public static readonly string[] Names = ["none", "pdf-1", "pdf-3", "pdf-3-pool", "count-ui", "count-pool", "decode", "editor"];

    public static async Task<int> Run(string name)
    {
        var folder = FileSystem.TempFolder("probe");
        var files = Fixtures.Make(folder, media: false);
        string F(string file) => files.First(path => Path.GetFileName(path) == file);
        var report = F("report.pdf");
        switch (name)
        {
            case "none":
                break;
            case "pdf-1":
                await Platform.Pdf.RenderPageAsync(report, 0, 72, Path.Combine(folder, "page.png"), CancellationToken.None);
                break;
            case "pdf-3":
                await RenderThree(report, folder);
                break;
            case "pdf-3-pool":
                await Task.Run(() => RenderThree(report, folder));
                break;
            case "count-ui":
                Console.WriteLine("pages " + PdfEngine.PageCount(report));
                break;
            case "count-pool":
                Console.WriteLine("pages " + await Task.Run(() => PdfEngine.PageCount(report)));
                break;
            case "decode":
                Console.WriteLine("width " + Previews.Decode(await File.ReadAllBytesAsync(F("logo.png"))).PixelWidth);
                break;
            case "editor":
            {
                var editor = new PagesEditor([report]);
                for (var wait = 0; wait < 40 && editor.ThumbnailCount < 3; wait++) await Task.Delay(100);
                Console.WriteLine($"thumbnails {editor.ThumbnailCount} {editor.LastError}");
                editor.Close();
                break;
            }
            default:
                Console.Error.WriteLine("probes: " + string.Join(", ", Names));
                return 64;
        }
        Console.WriteLine("probe " + name + " done");
        return 0;
    }

    private static async Task RenderThree(string pdf, string folder)
    {
        await using var session = await PdfSession.OpenAsync(pdf);
        for (var index = 0; index < 3; index++) await session.RenderAsync(index, 36, Path.Combine(folder, $"page-{index}.png"), CancellationToken.None);
    }
}

