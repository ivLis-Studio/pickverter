using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Pickverter.UI.Editors;

/// An image fitted into the editor with room around it; converts between screen and normalized coordinates.
internal abstract class ImageStage : FrameworkElement
{
    private BitmapSource? image;

    public BitmapSource? Image
    {
        get => image;
        set
        {
            image = value;
            InvalidateVisual();
        }
    }

    /// Pixel size of the real file (the preview may be smaller).
    public double PixelWidth { get; set; } = 1;
    public double PixelHeight { get; set; } = 1;

    protected Rect ImageRect
    {
        get
        {
            const double pad = 28;
            var width = Math.Max(1, ActualWidth - pad * 2);
            var height = Math.Max(1, ActualHeight - pad * 2);
            var scale = Math.Min(width / PixelWidth, height / PixelHeight);
            var w = PixelWidth * scale;
            var h = PixelHeight * scale;
            return new Rect((ActualWidth - w) / 2, (ActualHeight - h) / 2, w, h);
        }
    }

    protected Point ToNormal(Point point)
    {
        var rect = ImageRect;
        return new Point(Math.Clamp((point.X - rect.X) / rect.Width, 0, 1), Math.Clamp((point.Y - rect.Y) / rect.Height, 0, 1));
    }

    protected Rect ToScreen(NormRect rect)
    {
        var area = ImageRect;
        return new Rect(area.X + rect.X * area.Width, area.Y + rect.Y * area.Height, rect.W * area.Width, rect.H * area.Height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Theme.IsDark ? new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)) : new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6)), null,
            new Rect(0, 0, ActualWidth, ActualHeight));
        if (image != null) dc.DrawImage(image, ImageRect);
        else dc.DrawRectangle(Theme.Tile, null, ImageRect);
        RenderOverlay(dc);
    }

    protected abstract void RenderOverlay(DrawingContext dc);

    protected static void Handle(DrawingContext dc, Point center, double size = 10)
    {
        var rect = new Rect(center.X - size / 2, center.Y - size / 2, size, size);
        dc.DrawRoundedRectangle(Brushes.White, new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)), 1), rect, 2, 2);
    }

    protected static (CanvasGeometry.TargetKind Kind, int Index)? HitHandles(Rect selection, Point point, bool edges)
    {
        Point[] corners = [selection.TopLeft, selection.TopRight, selection.BottomRight, selection.BottomLeft];
        for (var index = 0; index < 4; index++)
        {
            if ((corners[index] - point).Length <= 11) return (CanvasGeometry.TargetKind.Corner, index);
        }
        if (!edges) return null;
        const double band = 7;
        if (point.X > selection.Left && point.X < selection.Right)
        {
            if (Math.Abs(point.Y - selection.Top) <= band) return (CanvasGeometry.TargetKind.Edge, 0);
            if (Math.Abs(point.Y - selection.Bottom) <= band) return (CanvasGeometry.TargetKind.Edge, 2);
        }
        if (point.Y > selection.Top && point.Y < selection.Bottom)
        {
            if (Math.Abs(point.X - selection.Right) <= band) return (CanvasGeometry.TargetKind.Edge, 1);
            if (Math.Abs(point.X - selection.Left) <= band) return (CanvasGeometry.TargetKind.Edge, 3);
        }
        return null;
    }

    protected static Cursor CursorFor(CanvasGeometry.TargetKind kind, int index) => kind switch
    {
        CanvasGeometry.TargetKind.Corner => index is 0 or 2 ? Cursors.SizeNWSE : Cursors.SizeNESW,
        CanvasGeometry.TargetKind.Edge => index is 0 or 2 ? Cursors.SizeNS : Cursors.SizeWE,
        CanvasGeometry.TargetKind.Move => Cursors.SizeAll,
        _ => Cursors.Cross,
    };
}

/// Crop: one rectangle with corner and edge handles, an optional fixed ratio, rule-of-thirds guides.
internal sealed class CropStage : ImageStage
{
    private (CanvasGeometry.TargetKind Kind, int Index)? drag;
    private NormRect origin;
    private Point start;
    private (double X, double Y) anchor;

    public NormRect Selection { get; set; } = NormRect.Full;
    /// Normalized width / height, or null for free.
    public double? Ratio { get; set; }
    public event Action? Changed;

    public CropStage()
    {
        Focusable = true;
        FocusVisualStyle = null;
    }

    protected override void RenderOverlay(DrawingContext dc)
    {
        var area = ImageRect;
        var selection = ToScreen(Selection);
        var mask = Geometry.Combine(new RectangleGeometry(area), new RectangleGeometry(selection), GeometryCombineMode.Exclude, null);
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)), null, mask);
        var guide = new Pen(new SolidColorBrush(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF)), 1);
        for (var third = 1; third <= 2; third++)
        {
            var x = selection.X + selection.Width * third / 3;
            var y = selection.Y + selection.Height * third / 3;
            dc.DrawLine(guide, new Point(x, selection.Top), new Point(x, selection.Bottom));
            dc.DrawLine(guide, new Point(selection.Left, y), new Point(selection.Right, y));
        }
        dc.DrawRectangle(null, new Pen(Brushes.White, 1.5), selection);
        foreach (var corner in new[] { selection.TopLeft, selection.TopRight, selection.BottomRight, selection.BottomLeft }) Handle(dc, corner);
        foreach (var edge in new[]
        {
            new Point(selection.X + selection.Width / 2, selection.Top), new Point(selection.Right, selection.Y + selection.Height / 2),
            new Point(selection.X + selection.Width / 2, selection.Bottom), new Point(selection.Left, selection.Y + selection.Height / 2),
        }) Handle(dc, edge, 8);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var point = e.GetPosition(this);
        var selection = ToScreen(Selection);
        origin = Selection;
        start = point;
        var hit = HitHandles(selection, point, edges: true);
        if (hit is { Kind: CanvasGeometry.TargetKind.Corner } corner)
        {
            anchor = CanvasGeometry.Corner(Selection, (corner.Index + 2) % 4);
            drag = corner;
        }
        else if (hit != null)
        {
            drag = hit;
        }
        else if (selection.Contains(point))
        {
            drag = (CanvasGeometry.TargetKind.Move, 0);
        }
        else
        {
            var normal = ToNormal(point);
            anchor = (normal.X, normal.Y);
            drag = (CanvasGeometry.TargetKind.Create, 0);
        }
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        if (drag is not { } active)
        {
            var hit = HitHandles(ToScreen(Selection), point, edges: true);
            Cursor = hit is { } target ? CursorFor(target.Kind, target.Index) : ToScreen(Selection).Contains(point) ? Cursors.SizeAll : Cursors.Cross;
            return;
        }
        var normal = ToNormal(point);
        switch (active.Kind)
        {
            case CanvasGeometry.TargetKind.Move:
                var begin = ToNormal(start);
                Selection = CanvasGeometry.Moved(origin, begin.X, begin.Y, normal.X, normal.Y);
                break;
            case CanvasGeometry.TargetKind.Edge:
                Selection = CanvasGeometry.Edge(active.Index, origin, normal.X, normal.Y, Ratio);
                break;
            default:
                Selection = CanvasGeometry.Span(anchor.X, anchor.Y, normal.X, normal.Y, Ratio);
                break;
        }
        InvalidateVisual();
        Changed?.Invoke();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        drag = null;
        ReleaseMouseCapture();
        if (Selection.W < 0.005 || Selection.H < 0.005) Selection = origin;
        InvalidateVisual();
        Changed?.Invoke();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.02 : 0.004;
        var (dx, dy) = e.Key switch
        {
            Key.Left => (-step, 0.0),
            Key.Right => (step, 0.0),
            Key.Up => (0.0, -step),
            Key.Down => (0.0, step),
            _ => (0.0, 0.0),
        };
        if (dx == 0 && dy == 0) return;
        Selection = new NormRect(Selection.X + dx, Selection.Y + dy, Selection.W, Selection.H).Clamped();
        InvalidateVisual();
        Changed?.Invoke();
        e.Handled = true;
    }
}

internal sealed class CropEditor : EditorWindow<CropSelection>
{
    private readonly string file;
    private readonly int count;
    private readonly CropStage stage = new();
    private CropAspect aspect = CropAspect.Free;
    private readonly TextBlock size = Ui.Text("", 12, brush: Theme.TextSecondary);

    public CropEditor(string file, int count)
    {
        this.file = file;
        this.count = count;
        Width = 980;
        Height = 720;
        MinWidth = 640;
        MinHeight = 480;
        stage.Changed += UpdateSize;
        Rebuild();
        _ = Load();
    }

    private async Task Load()
    {
        var video = FileKinds.Of(file) == FileKind.Video;
        BitmapSource? image;
        if (video)
        {
            image = await Previews.Frame(file, 1600, 0.3);
            var display = await MediaEngine.DisplaySize(file);
            if (display is { } pixels)
            {
                stage.PixelWidth = pixels.Width;
                stage.PixelHeight = pixels.Height;
            }
            else if (image != null)
            {
                stage.PixelWidth = image.PixelWidth;
                stage.PixelHeight = image.PixelHeight;
            }
        }
        else
        {
            var oriented = await Task.Run(() => ImageEngine.OrientedSize(file));
            image = await Previews.Image(file, 1800);
            if (oriented is { } pixels)
            {
                stage.PixelWidth = pixels.Width;
                stage.PixelHeight = pixels.Height;
            }
            else if (image != null)
            {
                stage.PixelWidth = image.PixelWidth;
                stage.PixelHeight = image.PixelHeight;
            }
        }
        stage.Image = image;
        ApplyAspect(aspect);
        UpdateSize();
    }

    private void ApplyAspect(CropAspect value)
    {
        aspect = value;
        var ratio = value.Ratio(stage.PixelWidth, stage.PixelHeight);
        stage.Ratio = ratio.HasValue ? ratio.Value * (stage.PixelHeight / stage.PixelWidth) : null;
        if (ratio is { } pixels) stage.Selection = NormRect.Centered(pixels, stage.PixelWidth, stage.PixelHeight);
        stage.InvalidateVisual();
        UpdateSize();
    }

    private void UpdateSize()
    {
        var rect = stage.Selection.Pixels(stage.PixelWidth, stage.PixelHeight);
        size.Text = $"{rect.Width.ToString(Loc.Culture)} × {rect.Height.ToString(Loc.Culture)}";
    }

    protected override void Rebuild()
    {
        Title = L("editor.crop.title", FileNames.Name(file));
        var aspects = new Segmented<CropAspect>(CropAspects.All.Select(value => (value, value.Label())), aspect);
        aspects.Changed += ApplyAspect;
        var toolbar = new Grid { Margin = new Thickness(20, 12, 20, 12) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.Children.Add(Ui.Row(10, Ui.Text(L("crop.aspect"), 13, brush: Theme.TextSecondary), aspects));
        Grid.SetColumn(Ui.Detach(size), 2);
        size.VerticalAlignment = VerticalAlignment.Center;
        toolbar.Children.Add(size);
        var reset = Ui.Button(L("crop.reset"), () =>
        {
            stage.Selection = NormRect.Full;
            ApplyAspect(CropAspect.Free);
            aspects.Select(CropAspect.Free);
        });
        var apply = Ui.Button(L("crop.apply"), () => Finish(new CropSelection(stage.Selection.Clamped())), prominent: true, icon: "crop");
        apply.IsDefault = true;
        var note = count > 1 ? Ui.Caption(LP("editor.applies_to", count)) : null;
        var footer = Footer(note, reset, CancelButton(this), apply);
        var dock = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(toolbar);
        dock.Children.Add(footer);
        if (stage.Parent is Panel old) old.Children.Remove(stage);
        dock.Children.Add(stage);
        Content = dock;
        Loaded += (_, _) => stage.Focus();
    }
}

/// Hide areas: any number of rectangles, previewed with the chosen style (mosaic, blur or fill).
internal sealed class RedactStage : ImageStage
{
    private (CanvasGeometry.TargetKind Kind, int Index)? drag;
    private NormRect origin;
    private Point start;
    private (double X, double Y) anchor;
    private BitmapSource? mosaic, blurred;

    public List<NormRect> Regions { get; } = [];
    public int? Selected { get; set; }
    public RedactStyle Look { get; set; } = RedactStyle.Pixelate;
    public event Action? Changed;

    public RedactStage()
    {
        Focusable = true;
        FocusVisualStyle = null;
    }

    public void Prepare()
    {
        if (Image is not { } source) return;
        mosaic = new TransformedBitmap(source, new ScaleTransform(1.0 / 22, 1.0 / 22));
        mosaic.Freeze();
        blurred = new TransformedBitmap(source, new ScaleTransform(1.0 / 28, 1.0 / 28));
        blurred.Freeze();
    }

    protected override void RenderOverlay(DrawingContext dc)
    {
        var area = ImageRect;
        for (var index = 0; index < Regions.Count; index++)
        {
            var rect = ToScreen(Regions[index]);
            switch (Look)
            {
                case RedactStyle.Fill:
                    dc.DrawRectangle(Brushes.Black, null, rect);
                    break;
                default:
                    var source = Look == RedactStyle.Pixelate ? mosaic : blurred;
                    if (source == null)
                    {
                        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xC0, 0x40, 0x40, 0x40)), null, rect);
                        break;
                    }
                    var group = new DrawingGroup();
                    RenderOptions.SetBitmapScalingMode(group, Look == RedactStyle.Pixelate ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
                    group.Children.Add(new ImageDrawing(source, area));
                    dc.PushClip(new RectangleGeometry(rect));
                    dc.DrawDrawing(group);
                    dc.Pop();
                    break;
            }
            var selected = Selected == index;
            dc.DrawRectangle(null, new Pen(selected ? Theme.Accent : new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)), selected ? 2 : 1), rect);
            if (selected)
            {
                foreach (var corner in new[] { rect.TopLeft, rect.TopRight, rect.BottomRight, rect.BottomLeft }) Handle(dc, corner);
            }
        }
        if (Regions.Count == 0)
        {
            var hint = new FormattedText(L("redact.empty"), Loc.Culture, FlowDirection.LeftToRight, Theme.Face(FontWeights.Medium), 14, Brushes.White,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            var box = new Rect(area.X + (area.Width - hint.Width) / 2 - 14, area.Y + area.Height / 2 - hint.Height / 2 - 8, hint.Width + 28, hint.Height + 16);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0xA0, 0, 0, 0)), null, box, 8, 8);
            dc.DrawText(hint, new Point(box.X + 14, box.Y + 8));
        }
    }

    private int? RegionAt(Point point)
    {
        for (var index = Regions.Count - 1; index >= 0; index--)
        {
            if (ToScreen(Regions[index]).Contains(point)) return index;
        }
        return null;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var point = e.GetPosition(this);
        start = point;
        if (Selected is { } current && current < Regions.Count && HitHandles(ToScreen(Regions[current]), point, edges: false) is { } corner)
        {
            origin = Regions[current];
            anchor = CanvasGeometry.Corner(origin, (corner.Index + 2) % 4);
            drag = corner;
        }
        else if (RegionAt(point) is { } hit)
        {
            Selected = hit;
            origin = Regions[hit];
            drag = (CanvasGeometry.TargetKind.Move, hit);
        }
        else
        {
            var normal = ToNormal(point);
            anchor = (normal.X, normal.Y);
            Regions.Add(new NormRect(normal.X, normal.Y, 0, 0));
            Selected = Regions.Count - 1;
            origin = Regions[^1];
            drag = (CanvasGeometry.TargetKind.Create, Regions.Count - 1);
        }
        CaptureMouse();
        InvalidateVisual();
        Changed?.Invoke();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        if (drag is not { } active || Selected is not { } index || index >= Regions.Count)
        {
            if (Selected is { } current && current < Regions.Count && HitHandles(ToScreen(Regions[current]), point, edges: false) is { } corner)
                Cursor = CursorFor(corner.Kind, corner.Index);
            else Cursor = RegionAt(point) != null ? Cursors.SizeAll : Cursors.Cross;
            return;
        }
        var normal = ToNormal(point);
        if (active.Kind == CanvasGeometry.TargetKind.Move)
        {
            var begin = ToNormal(start);
            Regions[index] = CanvasGeometry.Moved(origin, begin.X, begin.Y, normal.X, normal.Y);
        }
        else
        {
            Regions[index] = CanvasGeometry.Span(anchor.X, anchor.Y, normal.X, normal.Y, null, 0.002);
        }
        InvalidateVisual();
        Changed?.Invoke();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (drag is { Kind: CanvasGeometry.TargetKind.Create } && Selected is { } index && index < Regions.Count &&
            (Regions[index].W < 0.01 || Regions[index].H < 0.01))
        {
            Regions.RemoveAt(index);
            Selected = null;
        }
        drag = null;
        ReleaseMouseCapture();
        InvalidateVisual();
        Changed?.Invoke();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Delete or Key.Back) DeleteSelected();
    }

    public void DeleteSelected()
    {
        if (Selected is not { } index || index >= Regions.Count) return;
        Regions.RemoveAt(index);
        Selected = Regions.Count > 0 ? Regions.Count - 1 : null;
        InvalidateVisual();
        Changed?.Invoke();
    }
}

internal sealed class RedactEditor : EditorWindow<RedactSelection>
{
    private readonly string file;
    private readonly int count;
    private readonly RedactStage stage = new();
    internal RedactStage Stage => stage;
    private readonly TextBlock status = Ui.Caption("", wrap: false);
    private bool busy;

    public RedactEditor(string file, int count)
    {
        this.file = file;
        this.count = count;
        Width = 980;
        Height = 720;
        MinWidth = 640;
        MinHeight = 480;
        stage.Changed += UpdateStatus;
        Rebuild();
        _ = Load();
    }

    private async Task Load()
    {
        var video = FileKinds.Of(file) == FileKind.Video;
        var image = video ? await Previews.Frame(file, 1600, 0.3) : await Previews.Image(file, 1800);
        if (video && await MediaEngine.DisplaySize(file) is { } display)
        {
            stage.PixelWidth = display.Width;
            stage.PixelHeight = display.Height;
        }
        else if (!video && await Task.Run(() => ImageEngine.OrientedSize(file)) is { } oriented)
        {
            stage.PixelWidth = oriented.Width;
            stage.PixelHeight = oriented.Height;
        }
        else if (image != null)
        {
            stage.PixelWidth = image.PixelWidth;
            stage.PixelHeight = image.PixelHeight;
        }
        stage.Image = image;
        stage.Prepare();
        stage.InvalidateVisual();
    }

    private void UpdateStatus() => status.Text = stage.Regions.Count == 0 ? "" : LP("redact.count", stage.Regions.Count);

    private async Task Detect(Detection kind)
    {
        if (busy) return;
        busy = true;
        status.Text = kind == Detection.Faces ? L("note.finding_faces") : L("note.finding_text");
        try
        {
            var source = file;
            string? frame = null;
            if (FileKinds.Of(file) == FileKind.Video)
            {
                frame = await MediaEngine.PreviewFrame(file, 2000, 0.3, CancellationToken.None);
                source = frame;
            }
            var found = await ImageEngine.Detect(kind, source, CancellationToken.None);
            if (frame != null) FileSystem.TryDelete(frame);
            foreach (var box in found)
            {
                if (stage.Regions.All(existing => existing.Overlap(box) < 0.6)) stage.Regions.Add(box);
            }
            stage.InvalidateVisual();
            status.Text = found.Count == 0
                ? (kind == Detection.Faces ? L("redact.no_faces") : L("redact.no_text"))
                : LP("redact.found", found.Count);
        }
        catch (Exception error)
        {
            status.Text = Errors.Describe(error);
        }
        finally
        {
            busy = false;
        }
    }

    protected override void Rebuild()
    {
        Title = L("editor.redact.title", FileNames.Name(file));
        var styles = new Segmented<RedactStyle>(RedactStyles.All.Select(style => (style, style.Label())), stage.Look);
        styles.Changed += style =>
        {
            stage.Look = style;
            stage.InvalidateVisual();
        };
        var tools = Ui.Row(8);
        var faces = Ui.Button(L("redact.faces"), () => _ = Detect(Detection.Faces), icon: "person.crop.square", compact: true);
        faces.IsEnabled = Platform.Vision.FacesAvailable;
        if (!faces.IsEnabled) faces.ToolTip = L("err.faces_missing");
        var text = Ui.Button(L("redact.text"), () => _ = Detect(Detection.Text), icon: "text.viewfinder", compact: true);
        text.IsEnabled = Platform.Vision.OcrAvailable;
        if (!text.IsEnabled) text.ToolTip = L("err.ocr_missing");
        tools.Children.Add(faces);
        tools.Children.Add(text);
        tools.Children.Add(FlatButton.IconOnly(L("redact.delete"), "trash", stage.DeleteSelected));
        tools.Children.Add(Ui.Button(L("redact.clear"), () =>
        {
            stage.Regions.Clear();
            stage.Selected = null;
            stage.InvalidateVisual();
            UpdateStatus();
        }, compact: true, subtle: true));
        var toolbar = new Grid { Margin = new Thickness(20, 12, 20, 12) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.Children.Add(styles);
        tools.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(tools, 1);
        toolbar.Children.Add(tools);
        var apply = Ui.Button(L("redact.apply"), () =>
        {
            if (stage.Regions.Count == 0) return;
            Finish(new RedactSelection(stage.Regions.Select(region => region.Clipped()).ToList(), stage.Look));
        }, prominent: true);
        apply.IsDefault = true;
        var note = Ui.Column(2, Ui.Detach(status));
        if (count > 1) note.Children.Add(Ui.Caption(LP("editor.applies_to", count)));
        var footer = Footer(note, CancelButton(this), apply);
        var dock = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(toolbar);
        dock.Children.Add(footer);
        if (stage.Parent is Panel old) old.Children.Remove(stage);
        dock.Children.Add(stage);
        Content = dock;
        UpdateStatus();
        Loaded += (_, _) => stage.Focus();
    }
}
