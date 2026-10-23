using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pickverter.UI.Editors;

namespace Pickverter.UI;

/// Before and after in one frame: the result shows right of a divider that follows a drag (a click jumps it there;
/// ← and → move it, Shift for small steps). Esc closes.
internal sealed class CompareWindow : AppWindow
{
    private readonly string before;
    private readonly string after;
    private readonly CompareStage stage = new();
    private Picture? beforePicture;
    private Picture? afterPicture;

    private sealed record Picture(BitmapSource? Image, int Width, int Height, long Bytes);

    public CompareWindow(string before, string after)
    {
        this.before = before;
        this.after = after;
        Width = 980;
        Height = 720;
        MinWidth = 520;
        MinHeight = 400;
        Rebuild();
        Ready = Load();
    }

    /// Both pictures decoded and measured (snapshots wait for it).
    public Task Ready { get; }

    public static void Open(string before, string after)
    {
        var window = new CompareWindow(before, after);
        // Above the result cards while it's in use, like the editors; another app in front and it steps back.
        window.Topmost = true;
        window.Activated += (_, _) => window.Topmost = true;
        window.Deactivated += (_, _) => window.Topmost = false;
        window.Show();
        Windowing.Activate(window);
    }

    private static async Task<Picture> Read(string path)
    {
        var image = await Previews.Image(path, 2400);
        var size = await Task.Run(() => ImageEngine.OrientedSize(path));
        var (width, height) = size ?? (image?.PixelWidth ?? 0, image?.PixelHeight ?? 0);
        return new Picture(image, width, height, FileNames.Size(path));
    }

    private async Task Load()
    {
        var first = Read(before);
        var second = Read(after);
        beforePicture = await first;
        afterPicture = await second;
        Refresh();
    }

    private void Refresh()
    {
        stage.Before = beforePicture?.Image;
        stage.After = afterPicture?.Image;
        stage.BeforePixels = Pixels(beforePicture);
        stage.AfterPixels = Pixels(afterPicture);
        stage.BeforeLabel = Label(L("compare.before"), beforePicture);
        stage.AfterLabel = Label(L("compare.after"), afterPicture);
        stage.InvalidateVisual();
    }

    private static Size Pixels(Picture? picture) =>
        picture is { Width: > 0, Height: > 0 } ? new Size(picture.Width, picture.Height) : new Size(4, 3);

    /// "Before · 2.4 MB · 4032 × 3024"
    private static string Label(string title, Picture? picture)
    {
        if (picture == null) return title;
        var label = title + " · " + FileNames.FormatBytes(picture.Bytes);
        if (picture.Width > 0) label += " · " + picture.Width.ToString(Loc.Culture) + " × " + picture.Height.ToString(Loc.Culture);
        return label;
    }

    protected override void Rebuild()
    {
        Title = L("compare.title");
        AutomationProperties.SetName(stage, L("compare.title"));
        AutomationProperties.SetHelpText(stage, L("compare.hint"));
        var hint = Ui.Text(L("compare.hint"), 12, brush: Theme.TextSecondary, alignment: TextAlignment.Center);
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        var bar = new Border
        {
            Background = Theme.Surface,
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(20, 10, 20, 10),
            Child = hint,
        };
        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        dock.Children.Add(bar);
        dock.Children.Add(Ui.Detach(stage));
        Content = dock;
        Refresh();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.01 : 0.05;
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.Left:
                stage.Divider -= step;
                break;
            case Key.Right:
                stage.Divider += step;
                break;
            case Key.Home:
                stage.Divider = 0;
                break;
            case Key.End:
                stage.Divider = 1;
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}

/// Both pictures fitted into the same frame, the first left of the divider, the second right of it, a checkerboard
/// behind transparency and a label in each top corner.
internal sealed class CompareStage : FrameworkElement
{
    private const double Pad = 24;
    private double divider = 0.5;
    private bool dragging;

    public BitmapSource? Before { get; set; }
    public BitmapSource? After { get; set; }
    public Size BeforePixels { get; set; } = new(4, 3);
    public Size AfterPixels { get; set; } = new(4, 3);
    public string BeforeLabel { get; set; } = "";
    public string AfterLabel { get; set; } = "";

    /// Where the divider sits across the pictures: 0 shows only the result, 1 only the original.
    public double Divider
    {
        get => divider;
        set
        {
            divider = Math.Clamp(value, 0, 1);
            AutomationProperties.SetItemStatus(this, Math.Round(divider * 100).ToString(Loc.Culture) + "%");
            InvalidateVisual();
        }
    }

    public CompareStage()
    {
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.SizeWE;
        ClipToBounds = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
    }

    private Rect Frame => new(Pad, Pad, Math.Max(1, ActualWidth - Pad * 2), Math.Max(1, ActualHeight - Pad * 2));

    private static Rect Fit(Size pixels, Rect frame)
    {
        var width = Math.Max(1, pixels.Width);
        var height = Math.Max(1, pixels.Height);
        var scale = Math.Min(frame.Width / width, frame.Height / height);
        return new Rect(frame.X + (frame.Width - width * scale) / 2, frame.Y + (frame.Height - height * scale) / 2, width * scale, height * scale);
    }

    /// What the two pictures cover together: the divider and the labels belong to it.
    private Rect Area => Rect.Union(Fit(BeforePixels, Frame), Fit(AfterPixels, Frame));

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Theme.IsDark ? new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)) : new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6)), null,
            new Rect(0, 0, ActualWidth, ActualHeight));
        if (ActualWidth <= Pad * 2 || ActualHeight <= Pad * 2) return;
        var frame = Frame;
        var beforeRect = Fit(BeforePixels, frame);
        var afterRect = Fit(AfterPixels, frame);
        var area = Rect.Union(beforeRect, afterRect);
        var x = area.X + area.Width * divider;
        var checker = Checkerboard();
        dc.PushClip(new RectangleGeometry(new Rect(area.X, area.Y, Math.Max(0, x - area.X), area.Height)));
        Paint(dc, Before, beforeRect, checker);
        dc.Pop();
        dc.PushClip(new RectangleGeometry(new Rect(x, area.Y, Math.Max(0, area.Right - x), area.Height)));
        Paint(dc, After, afterRect, checker);
        dc.Pop();
        DrawDivider(dc, x, area);
        DrawLabel(dc, BeforeLabel, area, left: true);
        DrawLabel(dc, AfterLabel, area, left: false);
    }

    private static void Paint(DrawingContext dc, BitmapSource? image, Rect rect, Brush checker)
    {
        if (image == null)
        {
            dc.DrawRectangle(Theme.Tile, null, rect);
            return;
        }
        dc.DrawRectangle(checker, null, rect);
        dc.DrawImage(image, rect);
    }

    private static void DrawDivider(DrawingContext dc, double x, Rect area)
    {
        var top = new Point(x, area.Top);
        var bottom = new Point(x, area.Bottom);
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(0x59, 0, 0, 0)), 3.5), top, bottom);
        dc.DrawLine(new Pen(Brushes.White, 1.5), top, bottom);
        var center = new Point(x, area.Y + area.Height / 2);
        dc.DrawEllipse(Brushes.White, new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), 1), center, 15, 15);
        var arrows = new StreamGeometry();
        using (var figure = arrows.Open())
        {
            figure.BeginFigure(new Point(center.X - 3, center.Y - 5), true, true);
            figure.LineTo(new Point(center.X - 9, center.Y), true, false);
            figure.LineTo(new Point(center.X - 3, center.Y + 5), true, false);
            figure.BeginFigure(new Point(center.X + 3, center.Y - 5), true, true);
            figure.LineTo(new Point(center.X + 9, center.Y), true, false);
            figure.LineTo(new Point(center.X + 3, center.Y + 5), true, false);
        }
        arrows.Freeze();
        dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C)), null, arrows);
    }

    private void DrawLabel(DrawingContext dc, string text, Rect area, bool left)
    {
        if (text.Length == 0) return;
        var formatted = new FormattedText(text, Loc.Culture, FlowDirection.LeftToRight, Theme.Face(FontWeights.SemiBold), 12, Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = Math.Max(40, area.Width / 2 - 36),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        var width = formatted.WidthIncludingTrailingWhitespace;
        var box = new Rect(left ? area.X + 10 : area.Right - 10 - width - 16, area.Y + 10, width + 16, formatted.Height + 8);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)), null, box, 6, 6);
        dc.DrawText(formatted, new Point(box.X + 8, box.Y + 4));
    }

    private static Brush Checkerboard()
    {
        var light = Theme.IsDark ? Color.FromRgb(0x3A, 0x3A, 0x3A) : Color.FromRgb(0xFF, 0xFF, 0xFF);
        var dark = Theme.IsDark ? Color.FromRgb(0x2C, 0x2C, 0x2C) : Color.FromRgb(0xDF, 0xDF, 0xDF);
        var squares = new GeometryGroup();
        squares.Children.Add(new RectangleGeometry(new Rect(0, 0, 8, 8)));
        squares.Children.Add(new RectangleGeometry(new Rect(8, 8, 8, 8)));
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(light), null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(dark), null, squares));
        var brush = new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 16, 16),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 16, 16),
            ViewboxUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
        };
        brush.Freeze();
        return brush;
    }

    // The divider follows the pointer while the button is down; a click jumps it there.

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        dragging = CaptureMouse();
        Follow(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragging) Follow(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!dragging) return;
        dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        dragging = false;
    }

    private void Follow(Point point)
    {
        var area = Area;
        if (area.Width > 0) Divider = (point.X - area.X) / area.Width;
    }
}
