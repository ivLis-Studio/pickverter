namespace Pickverter;

/// Sizes of the wheel in device-independent pixels. Angles are radians clockwise from 12 o'clock; offsets are
/// measured from the wheel's center with y pointing down (screen coordinates).
public sealed class WheelGeometry
{
    public double OuterRadius { get; } = 150;
    public double InnerRadius { get; } = 62;
    public double Rim { get; } = 8;
    public double SubGap { get; } = 6;
    public double SubThickness { get; } = 54;
    public double Margin { get; } = 22;
    /// Wide enough for a ring's key and name in the current language (the Windows wheel measures it).
    public double TabWidth { get; set; } = 96;
    public double TabHeight { get; } = 28;
    public double TabSpacing { get; } = 2;
    public double TabPadding { get; } = 3;
    public double TabGap { get; } = 12;

    public double DiscRadius => OuterRadius + Rim;
    public double SubInner => DiscRadius + SubGap;
    public double SubOuter => SubInner + SubThickness;
    public double BoxSide => (SubOuter + Margin) * 2;
    public double Center => BoxSide / 2;
    public double TabBarHeight => TabHeight + TabPadding * 2;
    public double TabBarWidth => TabWidth * 3 + TabSpacing * 2 + TabPadding * 2;
    /// Distance from the wheel center down to the middle of the tab bar.
    public double TabBarOffset => DiscRadius + TabGap + TabBarHeight / 2;
    public double DeadZone => InnerRadius * 0.62;

    public double MainSpan(int count) => 2 * Math.PI / Math.Max(count, 1);

    /// Angular width of one option on the outer ring.
    public double SubSpan(int count) => Math.Max(22, Math.Min(32, 172.0 / Math.Max(count, 1))) * Math.PI / 180;

    /// Tab rect relative to the wheel center (x, y = top-left corner).
    public (double X, double Y, double Width, double Height) TabRect(WheelMode mode)
    {
        var x = -TabBarWidth / 2 + TabPadding + (int)mode * (TabWidth + TabSpacing);
        return (x, TabBarOffset - TabHeight / 2, TabWidth, TabHeight);
    }

    /// Clockwise angle from 12 o'clock for an offset from the center (y down).
    public static double Angle(double dx, double dy)
    {
        var angle = Math.Atan2(dx, -dy);
        return angle < 0 ? angle + 2 * Math.PI : angle;
    }

    /// Point on a circle around the center for a clockwise-from-12 angle.
    public static (double X, double Y) Polar(double radius, double angle) => (Math.Sin(angle) * radius, -Math.Cos(angle) * radius);
}

/// Lightweight value used to draw one ring segment.
public sealed record SegmentData(string Id, string Title, string Symbol)
{
    public bool IsFormat { get; init; }
    public bool IsMore { get; init; }
    public bool Unavailable { get; init; }
    public string? PageLabel { get; init; }
    public bool HasVariants { get; init; }
    /// On the outer ring: the option a plain click on the slot runs.
    public bool IsDefault { get; init; }
}

/// The outer ring of options for one slot.
public sealed record SubRingData(int ParentIndex, double CenterAngle, double Span, IReadOnlyList<SegmentData> Items)
{
    public double AngleOf(int index) => CenterAngle + (index - (Items.Count - 1) / 2.0) * Span;
    public double HalfWidth => Span * Items.Count / 2;
}
