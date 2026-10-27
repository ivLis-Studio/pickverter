using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Pickverter.Input;
using Pickverter.Interop;
using Pickverter.Services;
using Pickverter.UI;
using Pickverter.UI.Editors;
using Pickverter.UI.Wheel;

namespace Pickverter.Cli;

/// Renders every surface to PNG (2x) so the UI can be reviewed without a Windows desktop, in any language
/// and theme. "screenshot" shows the real windows instead and captures the screen (DWM shadows, layering).
/// With PICKVERTER_CHANNEL=store they show the Microsoft Store edition: no update window or update notices, the Store
/// line in Settings › Updates, and its own states for Start with Windows and folder protection.
internal static class Snapshots
{
    private static readonly string[] Everyone = Loc.Languages.Select(language => language.Code).ToArray();
    private static HashSet<string>? only;

    /// --only wheels,cards,flyout,settings,onboarding,update,crop,redact,trim,pages,metadata,dialogs,compare
    private static bool On(string section) => only == null || only.Contains(section);

    public static async Task<int> Run(string[] args)
    {
        var folder = Path.GetFullPath(args[0]);
        var languages = Option(args, "--lang") is { } list ? (list == "all" ? Everyone : list.Split(',')) : ["en"];
        var themes = Option(args, "--theme") is { } names ? names.Split(',') : ["light"];
        only = Option(args, "--only") is { } sections ? sections.Split(',').ToHashSet(StringComparer.Ordinal) : null;
        var fixtures = FileSystem.TempFolder("snapshot-fixtures");
        var files = Fixtures.Make(fixtures, media: false);
        string F(string name) => files.First(file => Path.GetFileName(file) == name);
        // The tray panel and Settings show the keyboard hook's state: run it like the app does.
        var hooks = Environment.GetEnvironmentVariable("PICKVERTER_NO_HOOKS") is not { Length: > 0 };
        if (hooks) InputMonitor.Shared.Start();
        foreach (var theme in themes)
        {
            var dark = theme == "dark";
            Application.Current.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;
            Theme.Force(dark);
            foreach (var language in languages)
            {
                Loc.Apply(language);
                var output = Path.Combine(folder, language + "-" + theme);
                Directory.CreateDirectory(output);
                Console.WriteLine("▸ " + output);
                if (On("wheels")) await Wheels(output, F("photo.jpg"), F("report.pdf"), files);
                if (On("cards")) await Cards(output, F("photo.jpg"), F("tone.wav"));
                await Windows(output, files, F);
            }
        }
        InputMonitor.Shared.Stop();
        Console.WriteLine("▸ snapshots done");
        return 0;
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static Brush Desk => Theme.IsDark ? new SolidColorBrush(Color.FromRgb(0x1F, 0x2A, 0x36)) : new SolidColorBrush(Color.FromRgb(0x8F, 0xA9, 0xC2));

    public static async Task Save(FrameworkElement element, string path, double width, double? height = null, Brush? background = null, double scale = 2)
    {
        var host = new Border { Background = background ?? Brushes.Transparent, Child = element, Width = width };
        if (height is { } fixedHeight) host.Height = fixedHeight;
        host.Measure(new Size(width, height ?? double.PositiveInfinity));
        host.Arrange(new Rect(new Size(width, height ?? host.DesiredSize.Height)));
        host.UpdateLayout();
        await Task.Delay(40);
        host.Measure(new Size(width, height ?? double.PositiveInfinity));
        var size = new Size(width, height ?? host.DesiredSize.Height);
        host.Arrange(new Rect(size));
        host.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale), 96 * scale, 96 * scale,
            PixelFormats.Pbgra32);
        bitmap.Render(host);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        await using (var stream = File.Create(path)) encoder.Save(stream);
        host.Child = null;
    }

    private static WheelCanvas Canvas(WheelModel model) => new(model)
    {
        Effect = new DropShadowEffect { BlurRadius = 26, ShadowDepth = 6, Direction = 270, Opacity = Theme.ShadowOpacity },
    };

    private static int IndexOf(WheelModel model, string id)
    {
        for (var index = 0; index < model.Items.Count; index++)
        {
            if (model.Items[index].Id == id) return index;
        }
        return 0;
    }

    private static async Task Wheels(string output, string photo, string pdf, IReadOnlyList<string> files)
    {
        var model = new WheelModel();
        model.Present([photo], WheelModel.WheelOrigin.Selection, WheelMode.Convert, ScreenPoint.Zero);
        model.ForceVisibleForTesting();
        var canvas = Canvas(model);
        canvas.Thumbnail = Explorer.Thumbnail(photo, 96);
        var side = model.Geometry.BoxSide;
        model.DemoHighlight(null);
        await Save(canvas, Path.Combine(output, "wheel-idle.png"), side, side, Desk);
        model.DemoHighlight(1);
        await Save(canvas, Path.Combine(output, "wheel-convert.png"), side, side, Desk);
        model.DemoSetMode(WheelMode.Tools);
        model.DemoHighlight(IndexOf(model, "tool.rotate"), 1);
        await Save(canvas, Path.Combine(output, "wheel-tools.png"), side, side, Desk);
        model.DemoSetMode(WheelMode.Actions);
        model.DemoHighlight(IndexOf(model, "action.copypath"), 0);
        await Save(canvas, Path.Combine(output, "wheel-actions.png"), side, side, Desk);
        model.DemoSetMode(WheelMode.Convert);
        model.DemoChoice();
        await Save(canvas, Path.Combine(output, "wheel-choice.png"), side, side, Desk);

        var many = new WheelModel();
        var images = files.Where(file => FileKinds.Of(file) == FileKind.Image).ToList();
        many.Present(images, WheelModel.WheelOrigin.Drag, WheelMode.Tools, ScreenPoint.Zero);
        many.ForceVisibleForTesting();
        var manyCanvas = Canvas(many);
        manyCanvas.Thumbnail = Explorer.Thumbnail(images[0], 96);
        many.DemoHighlight(null);
        await Save(manyCanvas, Path.Combine(output, "wheel-drag-many.png"), side, side, Desk);

        var document = new WheelModel();
        document.Present([pdf], WheelModel.WheelOrigin.Selection, WheelMode.Tools, ScreenPoint.Zero);
        document.ForceVisibleForTesting();
        var documentCanvas = Canvas(document);
        documentCanvas.Thumbnail = Explorer.Thumbnail(pdf, 96);
        document.DemoHighlight(IndexOf(document, "tool.pages"), 2);
        await Save(documentCanvas, Path.Combine(output, "wheel-pdf.png"), side, side, Desk);

        // An HWP document: until rhwp is downloaded, its options offer the download on click.
        if (files.FirstOrDefault(file => FileKinds.Of(file) == FileKind.Hwp) is { } hwp)
        {
            var hangul = new WheelModel();
            hangul.Present([hwp], WheelModel.WheelOrigin.Selection, WheelMode.Convert, ScreenPoint.Zero);
            hangul.ForceVisibleForTesting();
            var hangulCanvas = Canvas(hangul);
            hangulCanvas.Thumbnail = Explorer.Thumbnail(hwp, 96);
            hangul.DemoHighlight(IndexOf(hangul, "convert.pdf"));
            await Save(hangulCanvas, Path.Combine(output, "wheel-hwp.png"), side, side, Desk);
        }

        // The size preview: Compress (its preferred option, under the option's name), a size the photo is under
        // already, and a conversion (in place of the hint at the bottom).
        var sized = new WheelModel();
        sized.Present([photo], WheelModel.WheelOrigin.Selection, WheelMode.Tools, ScreenPoint.Zero);
        sized.ForceVisibleForTesting();
        var sizedCanvas = Canvas(sized);
        sizedCanvas.Thumbnail = Explorer.Thumbnail(photo, 96);
        var compress = IndexOf(sized, "tool.compress");
        sized.DemoHighlight(compress);
        await sized.EstimateNowAsync();
        await Save(sizedCanvas, Path.Combine(output, "wheel-estimate.png"), side, side, Desk);
        sized.DemoHighlight(compress, sized.Items[compress].Variants.ToList().FindIndex(variant => variant.Id == "tool.compress.max5m"));
        await sized.EstimateNowAsync();
        await Save(sizedCanvas, Path.Combine(output, "wheel-estimate-small.png"), side, side, Desk);
        sized.DemoSetMode(WheelMode.Convert);
        sized.DemoHighlight(IndexOf(sized, "convert.webp"));
        await sized.EstimateNowAsync();
        await Save(sizedCanvas, Path.Combine(output, "wheel-estimate-convert.png"), side, side, Desk);
    }

    private static readonly Dictionary<string, string> Results = new();

    /// A file an option makes from a fixture (made once per run).
    private static async Task<string> Result(string file, string optionId)
    {
        var key = file + "\n" + optionId;
        if (Results.TryGetValue(key, out var known) && File.Exists(known)) return known;
        var option = Catalog.AllOptions([file]).First(candidate => candidate.Id == optionId);
        var made = (await JobCenter.RunDirect(option, [file], ResultDestination.File)).Outputs[0];
        Results[key] = made;
        return made;
    }

    private static async Task Cards(string output, string photo, string tone)
    {
        var presenter = new CardPresenter();
        var option = Catalog.Options([photo], WheelMode.Convert).First(candidate => candidate.Id == "convert.png");
        var result = Path.Combine(Path.GetDirectoryName(photo)!, "photo.png");
        if (!File.Exists(result)) await JobCenter.RunDirect(option, [photo], ResultDestination.File);

        var running = new Job(option, WheelMode.Convert, [photo], ResultDestination.File) { Progress = 0.42 };
        var done = new Job(option, WheelMode.Convert, [photo], ResultDestination.File);
        await done.Complete(new PickResult { Outputs = [result] });
        // Audio needs ffmpeg, which is what the failed card offers to install.
        var mp3 = Catalog.Options([tone], WheelMode.Convert).FirstOrDefault(candidate => candidate.Id == "convert.mp3");
        var failed = mp3 != null ? new Job(mp3, WheelMode.Convert, [tone], ResultDestination.File) : new Job(option, WheelMode.Convert, [photo], ResultDestination.File);
        failed.Fail(L("err.ffmpeg"));
        failed.FixAction = new NoticeAction(L("engine.install_ffmpeg"), "arrow.down.circle", () => { });
        var notice = new Job(L("update.card.available", "0.3.0"), L("update.card.available.d"), "arrow.down.circle", JobTone.Info,
            new NoticeAction(L("update.card.details"), "info.circle", () => { }));
        var clipboard = new Job(option, WheelMode.Convert, [photo], ResultDestination.Clipboard);
        await clipboard.Complete(new PickResult { Outputs = [result] });
        // Remove BG's result says the AI model made it and links to the report form (the card needs no model).
        var cutout = Catalog.AllOptions([photo]).First(candidate => AiReport.Covers(candidate.Id) && candidate.Id != "tool.removebg");
        var ai = new Job(cutout, WheelMode.Tools, [photo], ResultDestination.File);
        await ai.Complete(new PickResult { Outputs = [result] });
        JobCenter.Shared.Jobs.Insert(0, done);
        JobCenter.Shared.Jobs.Insert(0, running);
        var cards = new List<(Job, string)> { (running, "card-running"), (done, "card-done"), (failed, "card-failed") };
        // The update notice is the GitHub edition's: the Store edition never shows one.
        if (!Distribution.IsStore) cards.Add((notice, "card-notice"));
        cards.Add((clipboard, "card-copied"));
        cards.Add((ai, "card-ai"));
        foreach (var (job, name) in cards)
        {
            var window = new CardWindow(job, presenter);
            await Task.Delay(job == done || job == ai ? 500 : 30);
            window.Rebuild();
            if (window.Content is not FrameworkElement content) continue;
            window.Content = null;
            await Save(content, Path.Combine(output, name + ".png"), CardPresenter.CardWidth, null, Theme.Surface);
            window.Close();
        }
    }

    private static async Task Windows(string output, IReadOnlyList<string> files, Func<string, string> file)
    {
        if (On("flyout"))
        {
            var flyout = new TrayFlyout();
            flyout.Rebuild();
            await Take(flyout, Path.Combine(output, "flyout.png"), 336, null);
            if (Distribution.IsStore)
            {
                // Folder protection blocking the Store edition: it sends people to Windows Security.
                FolderProtection.Simulated = FolderProtectionState.Blocking;
                var blocked = new TrayFlyout();
                blocked.Rebuild();
                await Take(blocked, Path.Combine(output, "flyout-cfa.png"), 336, null);
                FolderProtection.Simulated = null;
            }
        }

        if (On("settings"))
        {
            var settings = new SettingsWindow();
            foreach (var page in Enum.GetValues<SettingsPage>())
            {
                settings.Select(page);
                await Take(settings, Path.Combine(output, "settings-" + page.ToString().ToLowerInvariant() + ".png"), 920, null, keep: true);
            }
            await WheelLayouts(settings, output);
            // The ffmpeg row while Pickverter downloads it, and after a failed download.
            settings.Select(SettingsPage.Integrations);
            FFmpegSetup.Simulate(FFmpegSetup.SetupPhase.Downloading, 0.42);
            settings.Select(SettingsPage.Integrations);
            await Take(settings, Path.Combine(output, "settings-ffmpeg-downloading.png"), 920, null, keep: true);
            FFmpegSetup.Simulate(FFmpegSetup.SetupPhase.Failed, 0, L("ffmpeg.err.download", "No such host is known."));
            settings.Select(SettingsPage.Integrations);
            await Take(settings, Path.Combine(output, "settings-ffmpeg-failed.png"), 920, null, keep: true);
            FFmpegSetup.Simulate(FFmpegSetup.SetupPhase.Idle);
            // The rhwp row while Pickverter downloads it.
            HwpSetup.Simulate(HwpSetup.SetupPhase.Downloading, 0.42);
            settings.Select(SettingsPage.Integrations);
            await Take(settings, Path.Combine(output, "settings-rhwp-downloading.png"), 920, null, keep: true);
            HwpSetup.Simulate(HwpSetup.SetupPhase.Idle);
            if (Distribution.IsStore && Platform.Startup is StoreStartup startup)
            {
                // Start with Windows switched off in Task Manager, then by policy; folder protection blocking.
                foreach (var (state, name) in new[] { (StartupState.DisabledByUser, "settings-startup-off"), (StartupState.DisabledByPolicy, "settings-startup-policy") })
                {
                    startup.Simulate(state);
                    settings.Select(SettingsPage.General);
                    await Take(settings, Path.Combine(output, name + ".png"), 920, null, keep: true);
                }
                startup.Simulate(StartupState.Disabled);
                FolderProtection.Simulated = FolderProtectionState.Blocking;
                settings.Select(SettingsPage.Integrations);
                await Take(settings, Path.Combine(output, "settings-cfa.png"), 920, null, keep: true);
                FolderProtection.Simulated = null;
            }
            settings.Close();
        }

        if (On("onboarding"))
        {
            var onboarding = new OnboardingWindow();
            onboarding.Step(6);
            await Take(onboarding, Path.Combine(output, "onboarding.png"), 980, 720);
            if (Distribution.IsStore && Platform.Startup is StoreStartup startup)
            {
                startup.Simulate(StartupState.DisabledByUser);
                FolderProtection.Simulated = FolderProtectionState.Blocking;
                var locked = new OnboardingWindow();
                locked.Step(6);
                await Take(locked, Path.Combine(output, "onboarding-store-blocked.png"), 980, 720);
                FolderProtection.Simulated = null;
                startup.Simulate(StartupState.Disabled);
            }
        }

        // The Store edition has no Software Update window.
        if (On("update") && !Distribution.IsStore)
        {
            var notes = "- **New** — Pickverter now runs on Windows: tap Alt in File Explorer.\n- Result cards stay in the corner you choose.\n- [Release notes](https://github.com)";
            Updater.Shared.Simulate(Updater.UpdatePhase.Available, new UpdateManifest { Version = "0.3.0", Notes = notes, NotesUrl = "https://github.com" });
            await Take(new UpdateWindow(), Path.Combine(output, "update-available.png"), 540, null);
            Updater.Shared.Simulate(Updater.UpdatePhase.UpToDate, null);
            await Take(new UpdateWindow(), Path.Combine(output, "update-latest.png"), 540, null);
            Updater.Shared.Simulate(Updater.UpdatePhase.Idle, null);
        }

        var photo = file("photo.jpg");
        if (On("crop"))
        {
            var crop = new CropEditor(photo, 1);
            await Task.Delay(1200);
            await Take(crop, Path.Combine(output, "editor-crop.png"), 980, 720);
        }
        if (On("redact"))
        {
            var redact = new RedactEditor(photo, 2);
            await Task.Delay(1200);
            redact.Stage.Regions.Add(new NormRect(0.62, 0.12, 0.2, 0.28));
            redact.Stage.Regions.Add(new NormRect(0.1, 0.62, 0.3, 0.14));
            redact.Stage.Selected = 1;
            redact.Stage.Prepare();
            await Take(redact, Path.Combine(output, "editor-redact.png"), 980, 720);
        }
        if (On("trim")) await Take(new TrimEditor(file("tone.wav"), 4, false), Path.Combine(output, "editor-trim.png"), 720, 360);
        if (On("pages"))
        {
            var pages = new PagesEditor([file("report.pdf")]);
            for (var wait = 0; wait < 40 && pages.ThumbnailCount < 3; wait++) await Task.Delay(100);
            await Task.Delay(200);
            if (pages.ThumbnailCount < 3) Console.WriteLine($"  page thumbnails: {pages.ThumbnailCount} · " + (pages.LastError ?? "no error"));
            await Take(pages, Path.Combine(output, "editor-pages.png"), 1000, 720);
        }
        if (On("metadata"))
        {
            var snapshot = await MetadataKit.Read(photo);
            await Take(new MetadataEditor(photo, snapshot), Path.Combine(output, "editor-metadata.png"), 600, 640);
        }
        if (On("dialogs"))
        {
            await Take(new PasswordDialog(file("report.pdf")), Path.Combine(output, "dialog-password.png"), 460, null);
            await Take(new RangeDialog(file("report.pdf"), 3), Path.Combine(output, "dialog-range.png"), 460, null);
            // Opening a locked HWP document: the first question, and the one after a wrong password.
            if (files.FirstOrDefault(path => FileKinds.Of(path) == FileKind.Hwp) is { } hwp)
            {
                await Take(new PasswordDialog(hwp, unlock: true), Path.Combine(output, "dialog-unlock.png"), 460, null);
                await Take(new PasswordDialog(hwp, unlock: true, retry: true), Path.Combine(output, "dialog-unlock-retry.png"), 460, null);
            }
            await Take(new SizeDialog(photo, FileNames.Size(photo)), Path.Combine(output, "dialog-size.png"), 460, null);
        }
        if (On("compare"))
        {
            // A photo against its half-size copy, then a transparent picture against its WebP (the checkerboard).
            var resized = new CompareWindow(photo, await Result(photo, "tool.resize.50"));
            await resized.Ready;
            await Take(resized, Path.Combine(output, "compare.png"), 980, 720);
            var logo = file("logo.png");
            var transparent = new CompareWindow(logo, await Result(logo, "convert.webp"));
            await transparent.Ready;
            await Take(transparent, Path.Combine(output, "compare-transparent.png"), 980, 720);
        }
    }

    private static async Task Take(Window window, string path, double width, double? height, bool keep = false)
    {
        if (window.Content is not FrameworkElement content) return;
        window.Content = null;
        await Save(content, path, width, height, window.Background);
        if (keep) window.Content = content;
        else window.Close();
    }

    /// Settings › Wheel with one ring changed: image Tools without OCR, Rotate · Left 90° pinned, Copy Path brought in
    /// from Actions (selected, so the preview shows where it sits) and Resize fixed to 1920 px. Then the same at the
    /// window's narrowest, where the preview moves above the list.
    private static async Task WheelLayouts(SettingsWindow settings, string output)
    {
        var layout = WheelLayout.Shared;
        var key = WheelLayout.Key(WheelMode.Tools, FileKind.Image);
        layout.Remove("tool.ocr", key);
        layout.Pin("tool.rotate.left", L("tool.rotate") + " · " + L("rot.left"), key);
        layout.AddFromRing("action.copypath", key);
        layout.SetDefault("tool.resize", "tool.resize.1920");
        settings.ShowLayout(WheelMode.Tools, FileKind.Image, "action.copypath");
        await Task.Delay(50);
        await Take(settings, Path.Combine(output, "settings-wheel-custom.png"), 920, null, keep: true);
        await Take(settings, Path.Combine(output, "settings-wheel-narrow.png"), 720, null, keep: true);
        layout.ResetAll();
        settings.ShowLayout(WheelMode.Convert, FileKind.Image);
        await Task.Delay(50);
    }

    // Real windows on the desktop

    public static async Task<int> Screens(string folder)
    {
        folder = Path.GetFullPath(folder);
        Directory.CreateDirectory(folder);
        var files = Fixtures.Make(FileSystem.TempFolder("screen-fixtures"), media: false);
        var photo = files.First(path => Path.GetFileName(path) == "photo.jpg");
        JobCenter.Shared.Presenter = new CardPresenter();
        InputMonitor.Shared.Start();
        foreach (var dark in new[] { false, true })
        {
            Application.Current.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;
            Theme.Force(dark);
            var suffix = dark ? "dark" : "light";
            var (bounds, work, _) = Native.MonitorAt(0, 0);
            var backdrop = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = Desk,
                Title = "backdrop",
            };
            backdrop.Show();
            Native.SetWindowPos(new WindowInteropHelper(backdrop).Handle, IntPtr.Zero, work.Left, work.Top, work.Width, work.Height, Native.SWP_NOACTIVATE);
            await Task.Delay(300);

            var center = new Native.POINT(work.Left + work.Width / 2, work.Top + work.Height / 2);
            WheelController.Shared.Present([photo], WheelModel.WheelOrigin.Selection, null, center);
            WheelController.Shared.Model.DemoHighlight(1);
            await Task.Delay(900);
            Capture(Path.Combine(folder, $"screen-wheel-{suffix}.png"));
            WheelController.Shared.Dismiss();

            var option = Catalog.Options([photo], WheelMode.Convert).First(candidate => candidate.Id == "convert.webp");
            var job = JobCenter.Shared.Run(option, WheelMode.Convert, [photo], new ScreenPoint(center.X, center.Y));
            if (!Distribution.IsStore)
            {
                JobCenter.Shared.Notice(L("update.card.available", "0.3.0"), L("update.card.available.d"), "arrow.down.circle", JobTone.Info,
                    new NoticeAction(L("update.card.details"), "info.circle", () => { }), 30);
            }
            for (var wait = 0; wait < 60 && job.IsRunning; wait++) await Task.Delay(100);
            await Task.Delay(900);
            Capture(Path.Combine(folder, $"screen-cards-{suffix}.png"));
            if (JobCenter.Shared.Presenter is CardPresenter cards)
            {
                cards.Remove(job);
            }

            var flyout = new TrayFlyout();
            flyout.Toggle(null);
            await Task.Delay(700);
            Capture(Path.Combine(folder, $"screen-flyout-{suffix}.png"));
            flyout.Hide();

            WindowManager.ShowSettings();
            await Task.Delay(900);
            Capture(Path.Combine(folder, $"screen-settings-{suffix}.png"));
            foreach (Window window in Application.Current.Windows) if (window is SettingsWindow) window.Close();
            backdrop.Close();
            await Task.Delay(300);
        }
        Console.WriteLine("▸ " + folder);
        return 0;
    }

    public static void CaptureScreen(string path) => Capture(path);

    private static void Capture(string path)
    {
        int x = Native.GetSystemMetrics(76), y = Native.GetSystemMetrics(77), width = Native.GetSystemMetrics(78), height = Native.GetSystemMetrics(79);
        var screen = Native.GetDC(IntPtr.Zero);
        var memory = Native.CreateCompatibleDC(screen);
        var bitmap = Native.CreateCompatibleBitmap(screen, width, height);
        var previous = Native.SelectObject(memory, bitmap);
        try
        {
            Native.BitBlt(memory, 0, 0, width, height, screen, x, y, Native.SRCCOPY | Native.CAPTUREBLT);
            Native.SelectObject(memory, previous);
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = File.Create(path);
            encoder.Save(stream);
            Console.WriteLine("  " + path);
        }
        finally
        {
            Native.DeleteObject(bitmap);
            Native.DeleteDC(memory);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }
}
