using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Pickverter.Input;
using Pickverter.Services;
using Pickverter.UI.Wheel;

namespace Pickverter.UI;

/// Welcome window: a live demo of the wheel, the few things to set up front, and the gestures.
internal sealed class OnboardingWindow : AppWindow
{
    private readonly WheelModel demo = new(demo: true);
    private readonly WheelCanvas canvas;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private readonly IStartup startup = Platform.Startup;
    private int tick;
    private bool startWithWindows = true;
    private static readonly (WheelMode Mode, int? Slot, int? Sub)[] Script =
    [
        (WheelMode.Convert, null, null), (WheelMode.Convert, 1, null), (WheelMode.Convert, 3, null), (WheelMode.Convert, 3, null),
        (WheelMode.Tools, null, null), (WheelMode.Tools, 4, null), (WheelMode.Tools, 4, 1), (WheelMode.Tools, 4, 2), (WheelMode.Tools, 4, 2),
        (WheelMode.Actions, null, null), (WheelMode.Actions, 2, null), (WheelMode.Actions, 2, 2), (WheelMode.Actions, 2, 2),
    ];

    public OnboardingWindow()
    {
        Width = 980;
        Height = 720;
        ResizeMode = ResizeMode.CanMinimize;
        canvas = new WheelCanvas(demo) { IsHitTestVisible = false };
        demo.Changed += _ => canvas.InvalidateVisual();
        PresentDemo();
        startWithWindows = Preferences.Shared.Onboarded ? Preferences.Shared.LaunchAtLogin : true;
        timer.Tick += (_, _) => Step();
        Loaded += (_, _) => timer.Start();
        Closed += (_, _) =>
        {
            timer.Stop();
            FFmpegSetup.Changed -= OnFFmpeg;
            startup.Changed -= Rebuild;
        };
        FFmpegSetup.Changed += OnFFmpeg;
        // Windows' answer for Start with Windows (Store edition: switched off in Task Manager, a policy).
        startup.Changed += Rebuild;
        Activated += (_, _) =>
        {
            ShowDemo(true);
            // Back from installing ffmpeg, Windows Security or Task Manager: the steps catch up, but only once the click
            // that brought the window forward has landed, and only if one of them changed (a rebuild scrolls to the top).
            AfterActivationClick(() =>
            {
                Tools.Refresh();
                _ = startup.RefreshAsync();
                if (StepsState() != shownSteps) Rebuild();
            });
        };
        Deactivated += (_, _) => ShowDemo(false);
        Rebuild();
    }

    /// The demo turns only while this window is in front. Behind File Explorer a turning wheel looks just like the real
    /// one opening under Explorer, so it fades out and waits for the window to come back.
    private void ShowDemo(bool show)
    {
        if (show) timer.Start();
        else timer.Stop();
        var target = show ? 1.0 : 0.0;
        if (!SystemParameters.ClientAreaAnimation)
        {
            canvas.BeginAnimation(UIElement.OpacityProperty, null);
            canvas.Opacity = target;
            return;
        }
        // From wherever a running fade has got to.
        canvas.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(show ? 160 : 120)));
    }

    private string shownSteps = "";

    /// What the steps show, to tell whether coming back to the window changed any of them.
    private string StepsState() => string.Join("|", InputMonitor.Shared.IsRunning, FolderProtection.State, Tools.FFmpeg != null,
        FFmpegSetup.Phase, startup.State);

    private DateTime lastFFmpegRebuild = DateTime.MinValue;
    private FFmpegSetup.SetupPhase shownPhase = FFmpegSetup.Phase;

    /// The ffmpeg step shows its download as it happens (every phase change, progress a few times a second).
    private void OnFFmpeg()
    {
        var phaseChanged = FFmpegSetup.Phase != shownPhase;
        if (!phaseChanged && (DateTime.UtcNow - lastFFmpegRebuild).TotalMilliseconds < 300) return;
        shownPhase = FFmpegSetup.Phase;
        lastFFmpegRebuild = DateTime.UtcNow;
        Rebuild();
    }

    private void PresentDemo()
    {
        var sample = Path.Combine(FileSystem.TempRoot, L("demo.filename"));
        demo.Present([sample], WheelModel.WheelOrigin.Selection, WheelMode.Convert, ScreenPoint.Zero);
        tick = 0;
    }

    /// Advances the scripted demo (snapshots call it to pose the wheel).
    public void Step(int? to = null)
    {
        tick = to ?? (tick + 1) % Script.Length;
        var (mode, slot, sub) = Script[tick];
        if (demo.Mode != mode) demo.DemoSetMode(mode);
        demo.DemoHighlight(slot, sub);
    }

    protected override void Rebuild()
    {
        Title = L("ob.window");
        if (demo.Files.Count == 0 || !demo.Files[0].EndsWith(L("demo.filename"), StringComparison.Ordinal)) PresentDemo();
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(430) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var stage = new Grid { Background = Theme.IsDark ? new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1B)) : new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xEC)), ClipToBounds = true };
        if (canvas.Parent is Panel old) old.Children.Remove(canvas);
        canvas.LayoutTransform = new ScaleTransform(0.82, 0.82);
        canvas.HorizontalAlignment = HorizontalAlignment.Center;
        canvas.VerticalAlignment = VerticalAlignment.Center;
        canvas.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 26, ShadowDepth = 6, Direction = 270, Opacity = Theme.ShadowOpacity };
        stage.Children.Add(canvas);
        root.Children.Add(stage);

        var content = new DockPanel { Margin = new Thickness(32, 34, 32, 24) };
        var footer = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var local = Ui.Caption(L("ob.local"), wrap: true);
        local.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(local);
        var start = Ui.Button(L("ob.start"), Finish, prominent: true);
        start.IsDefault = true;
        start.MinWidth = 140;
        Grid.SetColumn(start, 1);
        footer.Children.Add(start);
        DockPanel.SetDock(footer, Dock.Bottom);
        content.Children.Add(footer);

        var stack = Ui.Column(14);
        stack.Children.Add(Ui.Column(6,
            Ui.Text("Pickverter", 30, FontWeights.SemiBold),
            Ui.Text(L("ob.subtitle"), 14, brush: Theme.TextSecondary, wrap: true)));

        var steps = Ui.Column(8);
        var input = InputMonitor.Shared.IsRunning;
        steps.Children.Add(Step(1, L("perm.input"), input ? L("ob.input.d") : L("perm.input.failed"), input, null, null));
        var protection = FolderProtection.State;
        var blocked = protection == FolderProtectionState.Blocking;
        if (Distribution.IsStore)
        {
            // No elevation from the Store edition: Windows Security, where the user allows Pickverter themselves.
            steps.Children.Add(Step(2, L("perm.cfa.title"), blocked ? L("perm.cfa.store") : L("ob.cfa.d"), !blocked,
                blocked ? L("perm.cfa.open") : null, blocked ? FolderProtection.OpenSettings : null));
        }
        else
        {
            steps.Children.Add(Step(2, L("perm.cfa.title"), blocked ? L("perm.cfa.blocking") : L("ob.cfa.d"), !blocked,
                blocked ? L("perm.cfa.allow") : null, blocked ? () => _ = Allow() : null));
        }
        var ffmpeg = Tools.FFmpeg != null;
        var failed = FFmpegSetup.Phase == FFmpegSetup.SetupPhase.Failed;
        string? ffmpegAction = ffmpeg ? null : FFmpegSetup.IsWorking ? L("common.cancel") : failed ? L("update.retry") : L("ob.install");
        Action? ffmpegRun = ffmpeg ? null : FFmpegSetup.IsWorking ? FFmpegSetup.Cancel : FFmpegSetup.Start;
        steps.Children.Add(Step(3, L("ob.ffmpeg"), (ffmpeg ? null : FFmpegSetup.StatusLine) ?? L("ob.ffmpeg.d"), ffmpeg, ffmpegAction, ffmpegRun,
            optional: true, warning: failed && !ffmpeg));
        // Switched off in Task Manager or by policy (Store edition): shown as it is, with where to change it.
        var startupState = startup.State;
        var locked = startupState.IsLocked();
        var login = Ui.Toggle(locked ? startupState.IsOn() : startWithWindows, value => startWithWindows = value, L("set.login"));
        login.IsEnabled = !locked;
        steps.Children.Add(Row(Badge(4, false), L("set.login"), startupState.Hint() ?? L("ob.startup.d"), login));
        stack.Children.Add(steps);

        stack.Children.Add(Ui.Divider());
        var guide = Ui.Column(9, Ui.Text(L("ob.how"), 12, FontWeights.SemiBold, Theme.TextSecondary));
        var trigger = Preferences.Shared.TriggerKey.Glyph();
        guide.Children.Add(Guide([trigger], L("ob.tap.t"), L("ob.tap.d", trigger)));
        guide.Children.Add(Guide([trigger, L("key.click")], L("ob.click.t"), L("ob.click.d", trigger)));
        guide.Children.Add(Guide([KeyNames.Shift, KeyNames.Ctrl, KeyNames.Alt], L("ob.drag.t"), L("ob.drag.d")));
        guide.Children.Add(Guide(["↑", "↓"], L("ob.options.t"), L("ob.options.d")));
        stack.Children.Add(guide);
        content.Children.Add(new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Grid.SetColumn(content, 1);
        root.Children.Add(content);
        Content = root;
        canvas.InvalidateVisual();
        shownSteps = StepsState();
    }

    private async Task Allow()
    {
        await FolderProtection.AllowAsync();
        Rebuild();
    }

    private void Finish()
    {
        var prefs = Preferences.Shared;
        if (!startup.State.IsLocked()) prefs.LaunchAtLogin = startWithWindows;
        prefs.Onboarded = true;
        Updater.Shared.AcknowledgeUpdate();
        Close();
    }

    private static FrameworkElement Badge(int number, bool done)
    {
        var badge = new Grid { Width = 26, Height = 26, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 12, 0) };
        badge.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(13),
            Background = done ? Theme.Success : Theme.Tile,
        });
        badge.Children.Add(done
            ? Glyphs.Icon("checkmark", 14, Theme.IsDark ? Brushes.Black : Brushes.White)
            : Ui.Text(number.ToString(Loc.Culture), 12, FontWeights.SemiBold, alignment: TextAlignment.Center));
        if (!done && badge.Children[^1] is TextBlock label) label.HorizontalAlignment = HorizontalAlignment.Center;
        return badge;
    }

    private static Border Step(int number, string title, string detail, bool done, string? action, Action? run, bool optional = false,
                               bool warning = false)
    {
        UIElement? control = null;
        if (action != null && run != null) control = Ui.Button(action, run, compact: true);
        var heading = optional ? title + " · " + L("ob.optional") : title;
        return Row(Badge(number, done), heading, detail, control, warning);
    }

    private static Border Row(FrameworkElement badge, string title, string detail, UIElement? control, bool warning = false)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(badge);
        var caption = Ui.Caption(detail);
        if (warning) caption.Foreground = Theme.Caution;
        var texts = Ui.Column(2, Ui.Text(title, 14, FontWeights.SemiBold, wrap: true), caption);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        if (control is FrameworkElement element)
        {
            element.Margin = new Thickness(12, 0, 0, 0);
            element.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(element, 2);
            grid.Children.Add(element);
        }
        return Ui.Card(grid, new Thickness(14, 10, 14, 10));
    }

    private static FrameworkElement Guide(IReadOnlyList<string> keys, string title, string detail)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var caps = Ui.Row(4);
        foreach (var key in keys) caps.Children.Add(KeyCap.Make(key));
        caps.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(caps);
        var texts = Ui.Column(1, Ui.Text(title, 13, FontWeights.SemiBold), Ui.Caption(detail));
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        return grid;
    }
}
