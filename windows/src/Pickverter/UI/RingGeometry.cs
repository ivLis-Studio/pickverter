using System.Windows;
using System.Windows.Media;

namespace Pickverter.UI;

/// Annular sectors with rounded corners and constant-width gaps (the wheel's slots and the app mark).
/// Degrees start at 3 o'clock and grow clockwise (screen coordinates, y down).
internal static class RingGeometry
{
    private static readonly Dictionary<(double, double, double, double, double, double), Geometry> Cache = new();

    public static Point Polar(Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Point(center.X + Math.Cos(radians) * radius, center.Y + Math.Sin(radians) * radius);
    }

    /// Model angles (radians clockwise from 12 o'clock) to degrees from 3 o'clock.
    public static double Degrees(double radians) => radians * 180 / Math.PI - 90;

    /// Segment centred on the origin; translate it into place.
    public static Geometry Segment(double startDegrees, double endDegrees, double inner, double outer, double gap = 3, double corner = 8)
    {
        var key = (Math.Round(startDegrees, 3), Math.Round(endDegrees, 3), inner, outer, gap, corner);
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var hit)) return hit;
        }
        var geometry = Build(startDegrees, endDegrees, inner, outer, gap, corner);
        lock (Cache)
        {
            if (Cache.Count > 600) Cache.Clear();
            Cache[key] = geometry;
        }
        return geometry;
    }

    private static Geometry Build(double startDegrees, double endDegrees, double inner, double outer, double gap, double corner)
    {
        var center = new Point(0, 0);
        double ri = inner + corner, ro = outer - corner;
        var half = gap / 2 + corner;
        var s = startDegrees * Math.PI / 180;
        var e = endDegrees * Math.PI / 180;
        double dso = half / ro, dsi = half / ri;
        if (ro <= ri || e - s <= 2 * dsi + 0.02) return Geometry.Empty;
        Point P(double radius, double angle) => new(center.X + Math.Cos(angle) * radius, center.Y + Math.Sin(angle) * radius);
        var figure = new PathFigure { StartPoint = P(ro, s + dso), IsClosed = true, IsFilled = true };
        var outerSweep = (e - dso) - (s + dso);
        figure.Segments.Add(new ArcSegment(P(ro, e - dso), new Size(ro, ro), 0, outerSweep > Math.PI, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(P(ri, e - dsi), true));
        var innerSweep = (e - dsi) - (s + dsi);
        figure.Segments.Add(new ArcSegment(P(ri, s + dsi), new Size(ri, ri), 0, innerSweep > Math.PI, SweepDirection.Counterclockwise, true));
        var core = new PathGeometry([figure]);
        Geometry result = core;
        if (corner > 0)
        {
            var pen = new Pen(Brushes.Black, corner * 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            var widened = core.GetWidenedPathGeometry(pen, 0.05, ToleranceType.Absolute);
            result = Geometry.Combine(core, widened, GeometryCombineMode.Union, null, 0.05, ToleranceType.Absolute);
        }
        result.Freeze();
        return result;
    }

    /// Rounded band behind an outer ring of options.
    public static Geometry Band(double startDegrees, double endDegrees, double inner, double outer) =>
        Segment(startDegrees, endDegrees, inner, outer, 0, 10);
}
