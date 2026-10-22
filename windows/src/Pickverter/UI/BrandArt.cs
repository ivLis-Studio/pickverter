using System.Windows;
using System.Windows.Media;

namespace Pickverter.UI;

/// The Pickverter mark, drawn as vectors: a graphite plate, eight slots with the upper-right one picked and lifted,
/// a document in the hub and the Windows arrow that picked it. Flat colours only; scripts/make-icons.mjs draws the
/// same numbers for the .ico files. Units: a 1024 grid.
internal static class BrandArt
{
    public const int Slots = 8;
    public const int Picked = 1;
    public static readonly Color Plate = Color.FromRgb(0x16, 0x17, 0x1A);
    public static readonly Color Slot = Color.FromRgb(0x2C, 0x2E, 0x34);
    public static readonly Color Pick = Color.FromRgb(0x0A, 0x84, 0xFF);
    public static readonly Color Hub = Color.FromRgb(0xF4, 0xF4, 0xF6);

    /// Windows arrow pointer, tip at (0,0), height 1.
    public static readonly Point[] Arrow =
    [
        new(0, 0), new(0, 0.842), new(0.21, 0.632), new(0.368, 1.0), new(0.474, 0.947), new(0.316, 0.579), new(0.579, 0.579),
    ];

    private static Brush Solid(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public static DrawingImage AppIcon()
    {
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            const double side = 864;
            var plate = new Rect((1024 - side) / 2, (1024 - side) / 2, side, side);
            dc.DrawRoundedRectangle(Solid(Plate), new Pen(Solid(Color.FromArgb(0x17, 0xFF, 0xFF, 0xFF)), 4), plate, 150, 150);
            var center = new Point(512, 512);
            var span = 360.0 / Slots;
            for (var index = 0; index < Slots; index++)
            {
                var mid = -90 + index * span;
                var lift = index == Picked ? 24 : 0;
                var offset = RingGeometry.Polar(new Point(0, 0), lift, mid);
                var segment = RingGeometry.Segment(mid - span / 2, mid + span / 2, 150, 312, 22, 21);
                dc.PushTransform(new TranslateTransform(center.X + offset.X, center.Y + offset.Y));
                dc.DrawGeometry(Solid(index == Picked ? Pick : Slot), null, segment);
                dc.Pop();
            }
            dc.DrawEllipse(Solid(Hub), null, center, 102, 102);
            dc.DrawGeometry(Solid(Plate), null, Document(new Rect(512 - 49, 512 - 63, 98, 126)));
            var pickMid = -90 + span * Picked;
            var tip = RingGeometry.Polar(center, (150 + 312) / 2.0 + 26, pickMid);
            var arrow = ArrowGeometry(new Point(tip.X - 4, tip.Y - 4), 190);
            dc.DrawGeometry(Solid(Colors.White), new Pen(Solid(Plate), 15) { LineJoin = PenLineJoin.Round }, arrow);
        }
        group.Freeze();
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    /// Small in-app mark (tray flyout header): plate and ring only.
    public static DrawingImage Glyph()
    {
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            dc.DrawRoundedRectangle(Solid(Plate), null, new Rect(0, 0, 100, 100), 22, 22);
            for (var index = 0; index < Slots; index++)
            {
                var mid = -90 + index * 45;
                var segment = RingGeometry.Segment(mid - 22.5, mid + 22.5, 15, 37, 4.5, 2);
                dc.PushTransform(new TranslateTransform(50, 50));
                dc.DrawGeometry(Solid(index == Picked ? Pick : Color.FromArgb(0x48, 0xFF, 0xFF, 0xFF)), null, segment);
                dc.Pop();
            }
        }
        group.Freeze();
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    public static Geometry ArrowGeometry(Point tip, double height)
    {
        var figure = new PathFigure { StartPoint = tip, IsClosed = true, IsFilled = true };
        foreach (var point in Arrow.Skip(1)) figure.Segments.Add(new LineSegment(new Point(tip.X + point.X * height, tip.Y + point.Y * height), true));
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }

    /// Page with a folded corner.
    public static Geometry Document(Rect rect)
    {
        var fold = rect.Width * 0.34;
        var r = rect.Width * 0.12;
        var figure = new PathFigure { StartPoint = new Point(rect.Left + r, rect.Top), IsClosed = true, IsFilled = true };
        figure.Segments.Add(new LineSegment(new Point(rect.Right - fold, rect.Top), true));
        figure.Segments.Add(new LineSegment(new Point(rect.Right, rect.Top + fold), true));
        figure.Segments.Add(new LineSegment(new Point(rect.Right, rect.Bottom - r), true));
        figure.Segments.Add(new QuadraticBezierSegment(new Point(rect.Right, rect.Bottom), new Point(rect.Right - r, rect.Bottom), true));
        figure.Segments.Add(new LineSegment(new Point(rect.Left + r, rect.Bottom), true));
        figure.Segments.Add(new QuadraticBezierSegment(new Point(rect.Left, rect.Bottom), new Point(rect.Left, rect.Bottom - r), true));
        figure.Segments.Add(new LineSegment(new Point(rect.Left, rect.Top + r), true));
        figure.Segments.Add(new QuadraticBezierSegment(new Point(rect.Left, rect.Top), new Point(rect.Left + r, rect.Top), true));
        var page = new PathGeometry([figure]);
        var cut = new PathFigure { StartPoint = new Point(rect.Right - fold, rect.Top), IsClosed = false };
        cut.Segments.Add(new LineSegment(new Point(rect.Right - fold, rect.Top + fold), true));
        cut.Segments.Add(new LineSegment(new Point(rect.Right, rect.Top + fold), true));
        var stroke = new PathGeometry([cut]).GetWidenedPathGeometry(new Pen(Brushes.Black, rect.Width * 0.07) { LineJoin = PenLineJoin.Round });
        var result = Geometry.Combine(page, stroke, GeometryCombineMode.Exclude, null);
        result.Freeze();
        return result;
    }
}
