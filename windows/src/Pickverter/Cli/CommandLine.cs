using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Pickverter.Input;
using Pickverter.Interop;
using Pickverter.Services;
using Pickverter.UI;

namespace Pickverter.Cli;

/// "Pickverter.exe cli <command>": developer and release tooling (also what CI runs on a Windows machine).
internal static class CommandLine
{
    private const string Usage = """
        Pickverter command line

          cli version
          cli list <files…>                         options on every ring for these files
          cli run <option-id> <files…> [--clipboard|--both]
          cli fixtures <folder>                     sample files for every engine
          cli selftest [folder]                     exercises the Windows services and engines
          cli glyphs                                every symbol used by the wheel has an icon
          cli snapshot <folder> [--lang en,ko|all] [--theme light,dark]
          cli screenshot <folder>                   real windows on the desktop (shadows, transparency)
          cli inspect                               File Explorer selection and window under the pointer
          cli update-keygen <private-key-file>
          cli update-pubkey [private-key-file]
          cli update-manifest --version V --zip-x64 Z --url-x64 U [--zip-arm64 Z --url-arm64 U]
                              [--notes file] [--notes-url U] [--build B] [--min-os 10.0.19041]
          cli update-verify <manifest> <zip-x64> [zip-arm64]
          cli update-check
        """;

    public static int Run(string[] args)
    {
        AttachOutput();
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Length == 0)
        {
            Console.WriteLine(Usage);
            return 64;
        }
        var command = args[0];
        var rest = args[1..];
        try
        {
            switch (command)
            {
                case "version":
                    Console.WriteLine(Updater.Shared.CurrentVersion);
                    return 0;
                case "list":
                    return Headless(() => List(rest));
                case "run":
                    return Headless(() => RunOption(rest).GetAwaiter().GetResult());
                case "fixtures":
                    if (rest.Length == 0) break;
                    Headless(() =>
                    {
                        foreach (var file in Fixtures.Make(rest[0])) Console.WriteLine(file);
                        return 0;
                    });
                    return 0;
                case "glyphs":
                    return Headless(Glyphs);
                case "selftest":
                    return WithApp(() => SelfTest.Run(rest.Length > 0 ? rest[0] : FileSystem.TempFolder("selftest")));
                case "snapshot":
                    if (rest.Length == 0) break;
                    return WithApp(() => Snapshots.Run(rest));
                case "screenshot":
                    if (rest.Length == 0) break;
                    return WithApp(() => Snapshots.Screens(rest[0]));
                case "e2e":
                    if (rest.Length == 0) break;
                    return WithApp(() => EndToEnd.Run(rest[0]));
                case "inspect":
                    return WithApp(Inspect);
                case "update-keygen":
                    if (rest.Length == 0) break;
                    return UpdateKeygen(rest[0]);
                case "update-pubkey":
                    Console.WriteLine(UpdateSigning.PublicKey(PrivateKey(rest.FirstOrDefault())));
                    return 0;
                case "update-manifest":
                    return UpdateManifestCommand(rest);
                case "update-verify":
                    return UpdateVerify(rest);
                case "update-check":
                    return Headless(() =>
                    {
                        Updater.Shared.CheckAsync(true).GetAwaiter().GetResult();
                        Console.WriteLine($"{Updater.Shared.Phase}: {Updater.Shared.StatusText}");
                        return Updater.Shared.Phase == Updater.UpdatePhase.Failed ? 1 : 0;
                    });
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("error: " + error);
            return 1;
        }
        Console.WriteLine(Usage);
        return 64;
    }

    /// A GUI executable has no console; borrow the caller's when output isn't redirected already.
    private static void AttachOutput()
    {
        var handle = Native.GetStdHandle(-11);
        if (handle != IntPtr.Zero && handle != new IntPtr(-1)) return;
        if (!Native.AttachConsole(Native.ATTACH_PARENT_PROCESS)) return;
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        Console.WriteLine();
    }

    private static void Prepare()
    {
        Preferences.UseTransient();
        WheelLayout.UseTransient();
        if (Environment.GetEnvironmentVariable("PICKVERTER_LANG") is { Length: > 0 } language) Loc.Apply(language);
    }

    /// Engines only (no windows): the core with Windows services that don't need a UI thread.
    private static int Headless(Func<int> body)
    {
        Prepare();
        Platform.Shell = new WinShell();
        Platform.Vision = new WinVision();
        Platform.Pdf = new WinPdf();
        Platform.Printer = new EdgePrinter();
        Platform.Office = new WinOffice();
        Platform.Media = new WinMedia();
        Platform.Codecs = new WinCodecs();
        Platform.Clipboard = new WinClipboard();
        return body();
    }

    /// Everything, with a WPF application running the dispatcher (snapshots, self test, screenshots).
    private static int WithApp(Func<Task<int>> body)
    {
        Prepare();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, ThemeMode = ThemeMode.Light };
        app.DispatcherUnhandledException += (_, error) =>
        {
            Console.Error.WriteLine("dispatcher: " + error.Exception);
            error.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, error) => Console.Error.WriteLine("unhandled: " + error.ExceptionObject);
        WindowsPlatform.Install(app.Dispatcher);
        Platform.Editors = new InjectedEditors();
        var code = -1;
        app.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                code = await body();
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("error: " + error);
                code = 1;
            }
            finally
            {
                app.Shutdown();
            }
        });
        var result = app.Run();
        Console.WriteLine($"▸ exit {code} (application {result})");
        if (code < 0) code = 70;
        Console.Out.Flush();
        return code;
    }

    private static int WithApp(Func<int> body) => WithApp(() => Task.FromResult(body()));

    private static int List(string[] files)
    {
        var paths = files.Select(Path.GetFullPath).ToList();
        foreach (var mode in WheelModes.All)
        {
            Console.WriteLine($"[{mode.Raw()}]");
            foreach (var option in Catalog.Options(paths, mode))
            {
                Console.WriteLine($"  {option.Id,-34} {option.Title}{(option.Unavailable != null ? "  (" + option.Unavailable + ")" : "")}");
                foreach (var variant in option.Variants)
                    Console.WriteLine($"    {variant.Id,-32} {variant.Title}{(variant.Unavailable != null ? "  (" + variant.Unavailable + ")" : "")}");
            }
        }
        return 0;
    }

    private static async Task<int> RunOption(string[] args)
    {
        var flags = args.Where(arg => arg.StartsWith("--", StringComparison.Ordinal)).ToHashSet();
        var positional = args.Where(arg => !arg.StartsWith("--", StringComparison.Ordinal)).ToList();
        if (positional.Count < 2)
        {
            Console.WriteLine(Usage);
            return 64;
        }
        var id = positional[0];
        var files = positional.Skip(1).Select(Path.GetFullPath).ToList();
        var option = Catalog.AllOptions(files).FirstOrDefault(candidate => candidate.Id == id);
        if (option == null)
        {
            Console.Error.WriteLine($"no option {id} for these files");
            return 2;
        }
        if (option.Unavailable != null)
        {
            Console.Error.WriteLine($"{id} unavailable: {option.Unavailable}");
            return 3;
        }
        var destination = flags.Contains("--clipboard") ? ResultDestination.Clipboard : flags.Contains("--both") ? ResultDestination.Both : ResultDestination.File;
        var watch = Stopwatch.StartNew();
        var result = await JobCenter.RunDirect(option, files, destination);
        foreach (var output in result.Outputs) Console.WriteLine(output);
        if (result.Message != null) Console.WriteLine(result.Message);
        Console.Error.WriteLine($"{id}: {watch.ElapsedMilliseconds} ms");
        return 0;
    }

    private static int Glyphs()
    {
        var folder = FileSystem.TempFolder("glyphs");
        try
        {
            var files = Fixtures.Make(folder, media: false);
            var symbols = new SortedSet<string>(StringComparer.Ordinal);
            void Add(PickOption option)
            {
                if (option.Symbol.Length > 0) symbols.Add(option.Symbol);
                foreach (var variant in option.Variants) Add(variant);
            }
            var groups = files.Select(file => (IReadOnlyList<string>)[file]).ToList();
            groups.Add(files);
            groups.Add(files.Where(file => FileKinds.Of(file) == FileKind.Image).ToList());
            foreach (var group in groups)
            foreach (var mode in WheelModes.All)
            foreach (var option in Catalog.Options(group, mode))
                Add(option);
            foreach (var destination in ResultDestinations.All) symbols.Add(destination.Symbol());
            var missing = symbols.Where(symbol => !UI.Glyphs.Has(symbol)).ToList();
            foreach (var symbol in missing) Console.WriteLine("missing glyph: " + symbol);
            Console.WriteLine($"{symbols.Count} symbols · {missing.Count} missing");
            return missing.Count == 0 ? 0 : 1;
        }
        finally
        {
            FileSystem.TryDelete(folder);
        }
    }

    private static async Task<int> Inspect()
    {
        Native.GetCursorPos(out var cursor);
        var foreground = Native.GetForegroundWindow();
        var root = Native.GetAncestor(foreground, Native.GA_ROOT);
        Console.WriteLine($"cursor {cursor.X},{cursor.Y}");
        Console.WriteLine($"foreground {Native.ClassName(foreground)} root {Native.ClassName(root)} explorer={Explorer.ForegroundExplorer() != IntPtr.Zero} editing={Explorer.IsEditingText(root)}");
        var under = Native.WindowFromPoint(cursor);
        Console.WriteLine($"under pointer {Native.ClassName(under)} file view={Explorer.FileViewAt(cursor.X, cursor.Y) != IntPtr.Zero}");
        var (bounds, work, scale) = Native.MonitorAt(cursor.X, cursor.Y);
        Console.WriteLine($"monitor {bounds.Left},{bounds.Top} {bounds.Width}x{bounds.Height} work {work.Width}x{work.Height} scale {scale:0.##}");
        var explorer = Explorer.ForegroundExplorer();
        if (explorer != IntPtr.Zero)
        {
            foreach (var path in await Explorer.SelectionAsync(explorer)) Console.WriteLine("  selected " + path);
        }
        InputMonitor.Shared.Start();
        Console.WriteLine($"hooks {(InputMonitor.Shared.IsRunning ? "installed" : "unavailable")}");
        InputMonitor.Shared.Stop();
        Console.WriteLine($"theme {(Theme.AppsUseDarkTheme() ? "dark" : "light")} taskbar {(Theme.TaskbarIsDark() ? "dark" : "light")} accent {Theme.SystemAccent}");
        Console.WriteLine($"folder protection {FolderProtection.State}");
        return 0;
    }

    // Updates

    private static string KeyFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "pickverter", "update_ed25519.key");

    private static string PrivateKey(string? file)
    {
        if (file == null && Environment.GetEnvironmentVariable("PICKVERTER_UPDATE_KEY") is { Length: > 0 } key) return key.Trim();
        var path = file is null or "-" ? KeyFile : file;
        return File.ReadAllText(path).Trim();
    }

    private static int UpdateKeygen(string path)
    {
        if (File.Exists(path))
        {
            Console.Error.WriteLine(path + " exists");
            return 1;
        }
        var (privateKey, publicKey) = UpdateSigning.GenerateKey();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, privateKey + "\n");
        Console.WriteLine(publicKey);
        return 0;
    }

    private static Dictionary<string, string> Options(string[] args)
    {
        var options = new Dictionary<string, string>();
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (args[index].StartsWith("--", StringComparison.Ordinal)) options[args[index][2..]] = args[++index];
        }
        return options;
    }

    private static int UpdateManifestCommand(string[] args)
    {
        var options = Options(args);
        if (!options.TryGetValue("version", out var version))
        {
            Console.Error.WriteLine("--version is required");
            return 64;
        }
        var key = PrivateKey(options.GetValueOrDefault("key"));
        var manifest = new UpdateManifest
        {
            Version = version.TrimStart('v'),
            Build = options.GetValueOrDefault("build"),
            Published = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            MinimumSystemVersion = options.GetValueOrDefault("min-os") ?? "10.0.19041",
            Notes = options.TryGetValue("notes", out var notes) ? File.ReadAllText(notes).Trim() : null,
            NotesUrl = options.GetValueOrDefault("notes-url"),
        };
        foreach (var architecture in new[] { "x64", "arm64" })
        {
            if (!options.TryGetValue("zip-" + architecture, out var zip)) continue;
            if (!options.TryGetValue("url-" + architecture, out var url))
            {
                Console.Error.WriteLine($"--url-{architecture} is required with --zip-{architecture}");
                return 64;
            }
            var sha = UpdateSigning.Sha256(zip);
            var size = new FileInfo(zip).Length;
            manifest.Assets[architecture] = new UpdateAsset
            {
                Url = url,
                Size = size,
                Sha256 = sha,
                Signature = UpdateSigning.Sign(UpdateSigning.SignedMessage(manifest.Version, sha, size), key),
            };
        }
        if (manifest.Assets.Count == 0)
        {
            Console.Error.WriteLine("no --zip-x64 or --zip-arm64");
            return 64;
        }
        var json = JsonSerializer.Serialize(manifest, UpdateManifest.Json);
        if (options.TryGetValue("out", out var target)) File.WriteAllText(target, json + "\n", new UTF8Encoding(false));
        else Console.WriteLine(json);
        return 0;
    }

    private static int UpdateVerify(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine(Usage);
            return 64;
        }
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(File.ReadAllText(args[0]), UpdateManifest.Json)
            ?? throw new InvalidOperationException("empty manifest");
        var key = Updater.Shared.PublicKey;
        if (Environment.GetEnvironmentVariable("PICKVERTER_UPDATE_KEY") is { Length: > 0 } privateKey)
            key = Convert.FromBase64String(UpdateSigning.PublicKey(privateKey));
        var failures = 0;
        foreach (var zip in args[1..])
        {
            var name = Path.GetFileName(zip);
            var entry = manifest.Assets.FirstOrDefault(pair => pair.Value.Url.EndsWith("/" + name, StringComparison.Ordinal));
            if (entry.Value == null)
            {
                Console.Error.WriteLine($"{name}: not in the manifest");
                failures++;
                continue;
            }
            var asset = entry.Value;
            var ok = UpdateSigning.Sha256(zip) == asset.Sha256 && new FileInfo(zip).Length == asset.Size && UpdateSigning.Verify(manifest, asset, key);
            Console.WriteLine($"{entry.Key} {name}: {(ok ? "ok" : "MISMATCH")}");
            if (!ok) failures++;
        }
        return failures == 0 ? 0 : 1;
    }
}
