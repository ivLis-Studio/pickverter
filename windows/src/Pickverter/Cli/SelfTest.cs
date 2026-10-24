using System.Diagnostics;
using ImageMagick;
using ImageMagick.Drawing;
using Pickverter.Input;
using Pickverter.Services;

namespace Pickverter.Cli;

/// Exercises the Windows services and a broad set of engine runs on real files. Prints one line per check;
/// SKIP means the machine lacks an optional component (Office, an OCR language, HEVC…).
internal static class SelfTest
{
    private static int failures;

    private static async Task Check(string name, Func<Task<string?>> body)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var note = await body();
            if (note != null && note.StartsWith("SKIP", StringComparison.Ordinal))
                Console.WriteLine($"SKIP  {name} · {note[4..].Trim()}");
            else
                Console.WriteLine($"ok    {name} ({watch.ElapsedMilliseconds} ms){(string.IsNullOrEmpty(note) ? "" : " · " + note)}");
        }
        catch (Exception error)
        {
            failures++;
            Console.WriteLine($"FAIL  {name} · {Errors.Describe(error)}");
            if (Debug.Enabled) Console.WriteLine(error);
        }
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new PickException(message);
    }

    public static async Task<int> Run(string folder)
    {
        failures = 0;
        Console.WriteLine($"Pickverter {Updater.Shared.CurrentVersion} · {Environment.OSVersion.VersionString} · {Updater.Architecture}" +
                          (Distribution.IsStore ? " · Microsoft Store edition" : ""));
        var files = Fixtures.Make(folder);
        string F(string name) => files.First(file => Path.GetFileName(file) == name);
        Console.WriteLine($"fixtures: {files.Count} in {folder}");

        await Check("input hooks", () =>
        {
            InputMonitor.Shared.Start();
            var running = InputMonitor.Shared.IsRunning;
            InputMonitor.Shared.Stop();
            Expect(running, "hooks not installed");
            return Task.FromResult<string?>(null);
        });
        await Check("shell thumbnail", () =>
        {
            var image = Input.Explorer.Thumbnail(F("photo.jpg"), 96);
            Expect(image != null, "no thumbnail");
            return Task.FromResult<string?>($"{image!.PixelWidth}x{image.PixelHeight}");
        });
        await Check("clipboard text", async () =>
        {
            await Platform.Clipboard.SetTextAsync("Pickverter ✓");
            var formats = await Platform.Clipboard.DescribeAsync();
            Expect(formats.Contains("UnicodeText"), formats);
            return null;
        });
        await Check("clipboard image", async () =>
        {
            await Platform.Clipboard.WriteResultsAsync([F("photo.jpg")]);
            var formats = await Platform.Clipboard.DescribeAsync();
            Expect(formats.Contains("PNG") && formats.Contains("FileDrop"), formats);
            return null;
        });
        await Check("pdf render", async () =>
        {
            var png = Path.Combine(folder, "page-render.png");
            await Platform.Pdf.RenderPageAsync(F("report.pdf"), 0, 72, png, CancellationToken.None);
            var info = new MagickImageInfo(png);
            Expect(info.Width > 100, "too small");
            return $"{info.Width}x{info.Height}";
        });
        await Check("ocr", async () =>
        {
            if (!Platform.Vision.OcrAvailable) return "SKIP no OCR language installed";
            var sign = Path.Combine(folder, "ocr-sample.png");
            using (var image = new MagickImage(MagickColors.White, 1400, 300))
            {
                new Drawables().FontPointSize(96).FillColor(MagickColors.Black).Text(60, 190, "Pickverter 2026").Draw(image);
                image.Write(sign);
            }
            var lines = await Platform.Vision.RecognizeAsync(sign, CancellationToken.None);
            var text = string.Join(" ", lines.Select(line => line.Text));
            Expect(text.Contains("Pickverter", StringComparison.OrdinalIgnoreCase), "read: " + text);
            return text;
        });
        await Check("faces", async () =>
        {
            if (!Platform.Vision.FacesAvailable) return "SKIP face detection unavailable";
            var found = await Platform.Vision.DetectFacesAsync(F("photo.jpg"), CancellationToken.None);
            return $"{found.Count} face(s)";
        });
        await Check("edge print", async () =>
        {
            if (!Platform.Printer.Available) return "SKIP Microsoft Edge not found";
            var pdf = Path.Combine(folder, "page-print.pdf");
            await Platform.Printer.PrintAsync(F("page.html"), pdf, CancellationToken.None);
            Expect(PdfEngine.PageCount(pdf) >= 1, "no pages");
            return null;
        });
        await Check("media foundation", async () =>
        {
            if (!Platform.Media.Available) return "SKIP Media Foundation not installed";
            var duration = await Platform.Media.DurationAsync(F("tone.wav"));
            Expect(duration > 3, $"duration {duration}");
            return $"{duration:0.0} s";
        });
        await Check("heic encoder", () => Task.FromResult<string?>(Platform.Codecs.CanEncodeHeic ? null : "SKIP HEIF/HEVC extensions not installed"));
        await Check("office", () =>
        {
            var apps = new[] { OfficeApp.Word, OfficeApp.Excel, OfficeApp.PowerPoint }.Where(Platform.Office.Has).ToList();
            return Task.FromResult<string?>(apps.Count == 0 ? "SKIP Microsoft Office not installed" : string.Join(", ", apps));
        });
        await Check("recycle bin", async () =>
        {
            var scratch = Path.Combine(folder, "recycle-me.txt");
            await File.WriteAllTextAsync(scratch, "bye");
            await Platform.Shell.RecycleAsync([scratch]);
            Expect(!File.Exists(scratch), "still there");
            return null;
        });
        if (Platform.Startup is StoreStartup storeStartup)
        {
            // The package's startup task: read only (switching it would change what Windows does at the next sign-in).
            await Check("start with windows", async () =>
            {
                if (!StoreEdition.HasIdentity) return "SKIP Microsoft Store edition without package identity";
                await storeStartup.RefreshAsync();
                Expect(storeStartup.State != StartupState.Unavailable, "no startup task " + StoreStartup.TaskId);
                return "startup task " + storeStartup.State;
            });
        }
        else
        {
            await Check("start with windows", () =>
            {
                var startup = Platform.Startup;
                var before = startup.LaunchAtLogin;
                startup.LaunchAtLogin = true;
                var on = startup.LaunchAtLogin;
                startup.LaunchAtLogin = before;
                Expect(on, "Run key not written");
                return Task.FromResult<string?>(null);
            });
        }
        await Check("update signing", () =>
        {
            var (privateKey, publicKey) = UpdateSigning.GenerateKey();
            var message = UpdateSigning.SignedMessage("1.2.3", "ab", 42);
            var signature = UpdateSigning.Sign(message, privateKey);
            Expect(UpdateSigning.Verify(message, signature, Convert.FromBase64String(publicKey)), "signature");
            return Task.FromResult<string?>(null);
        });
        await Check("terminals", () => Task.FromResult<string?>(string.Join(", ", Terminals.Installed.Select(kind => kind.Label()))));
        await Check("folder protection", () => Task.FromResult<string?>(FolderProtection.State.ToString()));

        // Engines through the same options the wheel offers.
        var runs = new (string Option, string[] Inputs)[]
        {
            ("convert.png", ["photo.jpg"]),
            ("convert.webp", ["photo.jpg"]),
            ("convert.avif", ["logo.png"]),
            ("convert.ico", ["logo.png"]),
            ("convert.pdf", ["photo.jpg"]),
            ("tool.resize.50", ["photo.jpg"]),
            ("tool.rotate.left", ["photo.jpg"]),
            ("tool.compress", ["photo.jpg"]),
            ("convert.png", ["report.pdf"]),
            ("convert.txt", ["report.pdf"]),
            ("convert.docx", ["book.md"]),
            ("convert.pdf", ["book.md"]),
            ("convert.html", ["letter.docx"]),
            ("convert.csv", ["sheet.xlsx"]),
            ("convert.md", ["deck.pptx"]),
            ("convert.json", ["people.csv"]),
            ("convert.vtt", ["captions.srt"]),
            ("convert.tar.gz", ["bundle.zip"]),
            ("tool.zip", ["notes.txt"]),
            ("tool.extract", ["bundle.zip"]),
            ("tool.merge", ["report.pdf", "report.pdf"]),
            ("tool.split", ["report.pdf"]),
            ("convert.m4a", ["tone.wav"]),
            ("convert.mp3", ["tone.wav"]),
            // HWP through rhwp (skipped until it's downloaded): its own PDF, page images through Windows' renderer, DOCX via DocLang.
            ("convert.pdf", ["report.hwpx"]),
            ("convert.png", ["report.hwpx"]),
            ("convert.docx", ["report.hwpx"]),
            ("convert.hwp", ["report.hwpx"]),
        };
        foreach (var (id, inputs) in runs)
        {
            var paths = inputs.Select(F).ToList();
            await Check("run " + id + " · " + string.Join(" ", inputs), async () =>
            {
                var option = Catalog.AllOptions(paths).FirstOrDefault(candidate => candidate.Id == id);
                if (option == null) return "SKIP not offered";
                if (option.Unavailable != null) return "SKIP " + option.Unavailable;
                var result = await JobCenter.RunDirect(option, paths, ResultDestination.File);
                Expect(result.Outputs.Count > 0 && result.Outputs.All(output => File.Exists(output) || Directory.Exists(output)), "no output");
                return string.Join(", ", result.Outputs.Select(Path.GetFileName));
            });
        }
        Console.WriteLine(failures == 0 ? "self test passed" : $"{failures} check(s) failed");
        return failures == 0 ? 0 : 1;
    }
}
