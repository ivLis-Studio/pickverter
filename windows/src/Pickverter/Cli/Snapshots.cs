using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Pickverter.Input;
using Pickverter.Interop;
using Pickverter.UI;
using Pickverter.UI.Editors;
using Pickverter.UI.Wheel;

namespace Pickverter.Cli;

/// Renders every surface to PNG (2x) so the UI can be reviewed without a Windows desktop, in any language
/// and theme. "screenshot" shows the real windows instead and captures the screen (DWM shadows, layering).
internal static class Snapshots
{
    private static readonly string[] Everyone = Loc.Languages.Select(language => language.Code).ToArray();

    public static async Task<int> Run(string[] args)
    {
        var folder = Path.GetFullPath(args[0]);
        var languages = Option(args, "--lang") is { } list ? (list == "all" ? Everyone : list.Split(',')) : ["en"];
        var themes = Option(args, "--theme") is { } names ? names.Split(',') : ["light"];
        var fixtures = FileSystem.TempFolder("snapshot-fixtures");
        var files = Fixtures.Make(fixtures, media: false);
        string F(string name) => files.First(file => Path.GetFileName(file) == name);
        // The tray panel and Settings show the keyboard hook's state: run it like the app does.
        InputMonitor.Shared.Start();
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
                await Wheels(output, F("photo.jpg"), F("report.pdf"), files);
                await Cards(output, F("photo.jpg"));
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
    }

    private static async Task Cards(string output, string photo)
    {
        var presenter = new CardPresenter();
        var option = Catalog.Options([photo], WheelMode.Convert).First(candidate => candidate.Id == "convert.png");
        var result = Path.Combine(Path.GetDirectoryName(photo)!, "photo.png");
        if (!File.Exists(result)) await JobCenter.RunDirect(option, [photo], ResultDestination.File);

        var running = new Job(option, WheelMode.Convert, [photo], ResultDestination.File) { Progress = 0.42 };
        var done = new Job(option, WheelMode.Convert, [photo], ResultDestination.File);
        await done.Complete(new PickResult { Outputs = [result] });
        var failed = new Job(option, WheelMode.Convert, [photo], ResultDestination.File);
        failed.Fail(L("err.ffmpeg"));
        failed.FixAction = new NoticeAction(L("engine.install_ffmpeg"), "arrow.down.circle", () => { });
        var notice = new Job(L("update.card.available", "0.3.0"), L("update.card.available.d"), "arrow.down.circle", JobTone.Info,
            new NoticeAction(L("update.card.details"), "info.circle", () => { }));
        var clipboard = new Job(option, WheelMode.Convert, [photo], ResultDestination.Clipboard);
        await clipboard.Complete(new PickResult { Outputs = [result] });
        JobCenter.Shared.Jobs.Insert(0, done);
        JobCenter.Shared.Jobs.Insert(0, running);
        foreach (var (job, name) in new[] { (running, "card-running"), (done, "card-done"), (failed, "card-failed"), (notice, "card-notice"), (clipboard, "card-copied") })
        {
            var window = new CardWindow(job, presenter);
            await Task.Delay(job == done ? 500 : 30);
            window.Rebuild();
            if (window.Content is not FrameworkElement content) continue;
            window.Content = null;
            await Save(content, Path.Combine(output, name + ".png"), CardPresenter.CardWidth, null, Theme.Surface);
            window.Close();
        }
    }

    private static async Task Windows(string output, IReadOnlyList<string> files, Func<string, string> file)
    {
        var flyout = new TrayFlyout();
        flyout.Rebuild();
        await Take(flyout, Path.Combine(output, "flyout.png"), 336, null);

        var settings = new SettingsWindow();
        foreach (var page in Enum.GetValues<SettingsPage>())
        {
            settings.Select(page);
            await Take(settings, Path.Combine(output, "settings-" + page.ToString().ToLowerInvariant() + ".png"), 920, null, keep: true);
        }
        settings.Close();

        var onboarding = new OnboardingWindow();
        onboarding.Step(6);
        await Take(onboarding, Path.Combine(output, "onboarding.png"), 980, 720);

        var notes = "- **New** — Pickverter now runs on Windows: tap Alt in File Explorer.\n- Result cards stay in the corner you choose.\n- [Release notes](https://github.com)";
        Updater.Shared.Simulate(Updater.UpdatePhase.Available, new UpdateManifest { Version = "0.3.0", Notes = notes, NotesUrl = "https://github.com" });
        await Take(new UpdateWindow(), Path.Combine(output, "update-available.png"), 540, null);
        Updater.Shared.Simulate(Updater.UpdatePhase.UpToDate, null);
        await Take(new UpdateWindow(), Path.Combine(output, "update-latest.png"), 540, null);
        Updater.Shared.Simulate(Updater.UpdatePhase.Idle, null);

        var photo = file("photo.jpg");
        var crop = new CropEditor(photo, 1);
        await Task.Delay(1200);
        await Take(crop, Path.Combine(output, "editor-crop.png"), 980, 720);
        var redact = new RedactEditor(photo, 2);
        await Task.Delay(1200);
        redact.Stage.Regions.Add(new NormRect(0.62, 0.12, 0.2, 0.28));
        redact.Stage.Regions.Add(new NormRect(0.1, 0.62, 0.3, 0.14));
        redact.Stage.Selected = 1;
        redact.Stage.Prepare();
        await Take(redact, Path.Combine(output, "editor-redact.png"), 980, 720);
        await Take(new TrimEditor(file("tone.wav"), 4, false), Path.Combine(output, "editor-trim.png"), 720, 360);
        var pages = new PagesEditor([file("report.pdf")]);
        await Task.Delay(3000);
        if (pages.ThumbnailCount == 0) Console.WriteLine("  page thumbnails: none · " + (pages.LastError ?? "no error"));
        await Take(pages, Path.Combine(output, "editor-pages.png"), 1000, 720);
        var snapshot = await MetadataKit.Read(photo);
        await Take(new MetadataEditor(photo, snapshot), Path.Combine(output, "editor-metadata.png"), 600, 640);
        await Take(new PasswordDialog(file("report.pdf")), Path.Combine(output, "dialog-password.png"), 460, null);
        await Take(new RangeDialog(file("report.pdf"), 3), Path.Combine(output, "dialog-range.png"), 460, null);
    }

    private static async Task Take(Window window, string path, double width, double? height, bool keep = false)
    {
        if (window.Content is not FrameworkElement content) return;
        window.Content = null;
        await Save(content, path, width, height, window.Background);
        if (keep) window.Content = content;
        else window.Close();
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
            JobCenter.Shared.Notice(L("update.card.available", "0.3.0"), L("update.card.available.d"), "arrow.down.circle", JobTone.Info,
                new NoticeAction(L("update.card.details"), "info.circle", () => { }), 30);
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
