using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Pickverter.UI.Editors;

/// The trim bar: yellow handles for the part to keep, a playhead, and (for video) a strip of frames.
internal sealed class TrimTimeline : FrameworkElement
{
    private static readonly SolidColorBrush Handle = Frozen(Color.FromRgb(0xFF, 0xC8, 0x3D));
    private enum Part { None, Start, End, Playhead }
    private Part dragging;

    public double Duration { get; set; } = 1;
    public double Start { get; set; }
    public double End { get; set; } = 1;
    public double Position { get; set; }
    public List<BitmapSource> Frames { get; } = [];

    public event Action? Changed;
    public event Action<double>? Seek;

    public TrimTimeline()
    {
        Height = 56;
        Focusable = true;
        FocusVisualStyle = Ui.FocusRing(6);
        Cursor = Cursors.Hand;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private Rect Track => new(12, 4, Math.Max(1, ActualWidth - 24), ActualHeight - 8);
    private double X(double time) => Track.X + Track.Width * Math.Clamp(time / Math.Max(Duration, 0.001), 0, 1);
    private double Time(double x) => Math.Clamp((x - Track.X) / Track.Width, 0, 1) * Duration;

    protected override void OnRender(DrawingContext dc)
    {
        var track = Track;
        dc.DrawRoundedRectangle(Theme.Tile, null, track, 6, 6);
        if (Frames.Count > 0)
        {
            dc.PushClip(new RectangleGeometry(track, 6, 6));
            var width = track.Width / Frames.Count;
            for (var index = 0; index < Frames.Count; index++)
            {
                var frame = Frames[index];
                var scale = Math.Max(width / frame.PixelWidth, track.Height / frame.PixelHeight);
                var w = frame.PixelWidth * scale;
                var h = frame.PixelHeight * scale;
                dc.PushClip(new RectangleGeometry(new Rect(track.X + width * index, track.Y, width, track.Height)));
                dc.DrawImage(frame, new Rect(track.X + width * index + (width - w) / 2, track.Y + (track.Height - h) / 2, w, h));
                dc.Pop();
            }
            dc.Pop();
        }
        var left = X(Start);
        var right = X(End);
        var dim = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0));
        if (left > track.X) dc.DrawRectangle(dim, null, new Rect(track.X, track.Y, left - track.X, track.Height));
        if (right < track.Right) dc.DrawRectangle(dim, null, new Rect(right, track.Y, track.Right - right, track.Height));
        var keep = new Rect(left - 10, track.Y - 2, right - left + 20, track.Height + 4);
        dc.DrawRoundedRectangle(null, new Pen(Handle, 3), new Rect(keep.X + 1.5, keep.Y + 1.5, keep.Width - 3, keep.Height - 3), 6, 6);
        dc.DrawRoundedRectangle(Handle, null, new Rect(keep.X, keep.Y, 12, keep.Height), 5, 5);
        dc.DrawRoundedRectangle(Handle, null, new Rect(keep.Right - 12, keep.Y, 12, keep.Height), 5, 5);
        var grip = new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)), 1.5);
        foreach (var x in new[] { keep.X + 6, keep.Right - 6 })
            dc.DrawLine(grip, new Point(x, keep.Y + keep.Height * 0.35), new Point(x, keep.Y + keep.Height * 0.65));
        var playhead = X(Position);
        dc.DrawRoundedRectangle(Brushes.White, new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0, 0, 0)), 1),
            new Rect(playhead - 1.5, track.Y - 4, 3, track.Height + 8), 1.5, 1.5);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var x = e.GetPosition(this).X;
        var left = X(Start) - 5;
        var right = X(End) + 5;
        dragging = Math.Abs(x - left) <= 12 ? Part.Start : Math.Abs(x - right) <= 12 ? Part.End : Part.Playhead;
        Update(x);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var x = e.GetPosition(this).X;
        if (dragging == Part.None)
        {
            Cursor = Math.Abs(x - (X(Start) - 5)) <= 12 || Math.Abs(x - (X(End) + 5)) <= 12 ? Cursors.SizeWE : Cursors.Hand;
            return;
        }
        Update(x);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        dragging = Part.None;
        ReleaseMouseCapture();
    }

    private void Update(double x)
    {
        var time = Time(x);
        var minimum = Math.Min(0.1, Duration / 20);
        switch (dragging)
        {
            case Part.Start:
                Start = Math.Clamp(time, 0, End - minimum);
                Seek?.Invoke(Start);
                break;
            case Part.End:
                End = Math.Clamp(time, Start + minimum, Duration);
                Seek?.Invoke(End);
                break;
            case Part.Playhead:
                Seek?.Invoke(time);
                break;
        }
        InvalidateVisual();
        Changed?.Invoke();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1.0 : 0.1;
        switch (e.Key)
        {
            case Key.I:
                Start = Math.Min(Position, End - 0.05);
                break;
            case Key.O:
                End = Math.Max(Position, Start + 0.05);
                break;
            case Key.Left:
                Seek?.Invoke(Math.Max(0, Position - step));
                break;
            case Key.Right:
                Seek?.Invoke(Math.Min(Duration, Position + step));
                break;
            default:
                return;
        }
        e.Handled = true;
        InvalidateVisual();
        Changed?.Invoke();
    }
}

internal sealed class TrimEditor : EditorWindow<TrimSelection>
{
    private readonly string file;
    private readonly bool video;
    private readonly MediaElement player = new() { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, ScrubbingEnabled = true };
    private readonly TrimTimeline timeline = new();
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly TextBox start = Field(), end = Field();
    private readonly TextBlock length = Ui.Text("", 13, brush: Theme.TextSecondary);
    private bool playing;
    private bool previewFailed;
    private bool lossless;
    private readonly Border playHolder = new();

    public TrimEditor(string file, double duration, bool video)
    {
        this.file = file;
        this.video = video;
        lossless = Tools.FFmpeg != null;
        timeline.Duration = Math.Max(duration, 0.1);
        timeline.End = timeline.Duration;
        Width = video ? 980 : 720;
        Height = video ? 700 : 360;
        MinWidth = 600;
        MinHeight = video ? 480 : 320;
        player.Source = new Uri(Path.GetFullPath(file));
        player.MediaFailed += (_, _) =>
        {
            previewFailed = true;
            Rebuild();
        };
        player.MediaOpened += (_, _) =>
        {
            player.Pause();
            player.Position = TimeSpan.Zero;
        };
        player.Play();
        player.Pause();
        clock.Tick += (_, _) => Tick();
        timeline.Seek += SeekTo;
        timeline.Changed += UpdateFields;
        start.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter) Commit(start, true);
        };
        start.LostKeyboardFocus += (_, _) => Commit(start, true);
        end.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter) Commit(end, false);
        };
        end.LostKeyboardFocus += (_, _) => Commit(end, false);
        Closed += (_, _) =>
        {
            clock.Stop();
            player.Stop();
            player.Close();
        };
        Rebuild();
        if (video) _ = LoadFrames();
    }

    private static TextBox Field() => new()
    {
        Width = 96,
        FontSize = 13,
        Padding = new Thickness(6, 4, 6, 4),
        FontFamily = Theme.Mono,
        TextAlignment = TextAlignment.Center,
    };

    private async Task LoadFrames()
    {
        const int count = 10;
        for (var index = 0; index < count; index++)
        {
            var at = timeline.Duration * (index + 0.5) / count;
            var frame = await Previews.Frame(file, 240, at);
            if (frame == null) return;
            timeline.Frames.Add(frame);
            timeline.InvalidateVisual();
        }
    }

    private void SeekTo(double time)
    {
        timeline.Position = time;
        if (!previewFailed) player.Position = TimeSpan.FromSeconds(time);
        timeline.InvalidateVisual();
    }

    private void Tick()
    {
        if (!playing) return;
        var position = player.Position.TotalSeconds;
        if (position >= timeline.End - 0.02)
        {
            Toggle();
            SeekTo(timeline.Start);
            return;
        }
        timeline.Position = position;
        timeline.InvalidateVisual();
    }

    private void Toggle()
    {
        if (previewFailed) return;
        playing = !playing;
        if (playing)
        {
            if (timeline.Position < timeline.Start || timeline.Position >= timeline.End - 0.05) SeekTo(timeline.Start);
            player.Play();
            clock.Start();
        }
        else
        {
            player.Pause();
            clock.Stop();
        }
        UpdatePlayButton();
    }

    private void UpdatePlayButton()
    {
        var button = FlatButton.IconOnly(playing ? L("trim.pause") : L("trim.play"), playing ? "pause.fill" : "play.fill", Toggle, compact: false);
        button.IsEnabled = !previewFailed;
        playHolder.Child = button;
    }

    private void UpdateFields()
    {
        if (!start.IsKeyboardFocused) start.Text = TimeText.Format(timeline.Start);
        if (!end.IsKeyboardFocused) end.Text = TimeText.Format(timeline.End);
        length.Text = L("trim.length") + " " + TimeText.Format(timeline.End - timeline.Start);
    }

    private void Commit(TextBox box, bool isStart)
    {
        if (TimeText.Parse(box.Text) is { } value)
        {
            // Same minimum as dragging; never an inverted range on very short clips.
            var minimum = Math.Min(0.05, timeline.Duration / 20);
            if (isStart) timeline.Start = Math.Clamp(value, 0, Math.Max(0, timeline.End - minimum));
            else timeline.End = Math.Clamp(value, Math.Min(timeline.Start + minimum, timeline.Duration), timeline.Duration);
            SeekTo(isStart ? timeline.Start : timeline.End);
        }
        UpdateFields();
        timeline.InvalidateVisual();
    }

    protected override void Rebuild()
    {
        Title = L("editor.trim.title", FileNames.Name(file));
        var dock = new DockPanel();
        var controls = Ui.Column(12);
        controls.Margin = new Thickness(20, 14, 20, 14);
        if (previewFailed) controls.Children.Add(Ui.Caption(L("trim.fallback")));
        controls.Children.Add(Ui.Detach(timeline));
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        UpdatePlayButton();
        row.Children.Add(Ui.Detach(playHolder));
        var times = Ui.Row(10, Ui.Text(L("trim.start"), 13, brush: Theme.TextSecondary), Ui.Detach(start),
            Ui.Text(L("trim.end"), 13, brush: Theme.TextSecondary), Ui.Detach(end), Ui.Detach(length));
        times.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetColumn(times, 1);
        row.Children.Add(times);
        controls.Children.Add(row);
        if (Tools.FFmpeg != null)
        {
            var modes = new Segmented<bool>([(true, L("trim.fast")), (false, L("trim.precise"))], lossless);
            var hint = Ui.Caption(lossless ? L("trim.fast.d") : L("trim.precise.d"));
            modes.Changed += value =>
            {
                lossless = value;
                hint.Text = value ? L("trim.fast.d") : L("trim.precise.d");
            };
            controls.Children.Add(Ui.Row(12, modes, hint));
        }
        controls.Children.Add(Ui.Caption(L("trim.hint")));
        var apply = Ui.Button(L("trim.apply"), () => Finish(new TrimSelection(timeline.Start, timeline.End, lossless && Tools.FFmpeg != null)),
            prominent: true, icon: "scissors");
        apply.IsDefault = true;
        var footer = Footer(null, CancelButton(this), apply);
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(footer);
        DockPanel.SetDock(controls, Dock.Bottom);
        dock.Children.Add(controls);
        var stage = new Grid { Background = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10)) };
        if (player.Parent is Panel old) old.Children.Remove(player);
        if (video && !previewFailed) stage.Children.Add(player);
        else stage.Children.Add(Glyphs.Icon(video ? "film" : "waveform", 64, new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF))));
        if (!video && !previewFailed) stage.Children.Add(player);
        dock.Children.Add(stage);
        Content = dock;
        UpdateFields();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Space && !start.IsKeyboardFocused && !end.IsKeyboardFocused)
        {
            Toggle();
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }
}
