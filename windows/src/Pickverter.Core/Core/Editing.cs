using System.Text.Json.Serialization;

namespace Pickverter;

/// Rectangle in normalized image coordinates (0…1, origin top-left), independent of preview scale.
public readonly record struct NormRect(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("w")] double W,
    [property: JsonPropertyName("h")] double H)
{
    public static readonly NormRect Full = new(0, 0, 1, 1);

    [JsonIgnore] public double MaxX => X + W;
    [JsonIgnore] public double MaxY => Y + H;
    [JsonIgnore] public bool IsFull => X < 0.0005 && Y < 0.0005 && W > 0.999 && H > 0.999;

    public static NormRect Between(double ax, double ay, double bx, double by) =>
        new(Math.Min(ax, bx), Math.Min(ay, by), Math.Abs(ax - bx), Math.Abs(ay - by));

    /// Slid inside the unit square (size kept where possible).
    public NormRect Clamped()
    {
        var w = Math.Clamp(W, 0, 1);
        var h = Math.Clamp(H, 0, 1);
        return new NormRect(Math.Clamp(X, 0, 1 - w), Math.Clamp(Y, 0, 1 - h), w, h);
    }

    /// Intersected with the unit square.
    public NormRect Clipped()
    {
        double x0 = Math.Max(0, X), y0 = Math.Max(0, Y), x1 = Math.Min(1, MaxX), y1 = Math.Min(1, MaxY);
        return new NormRect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }

    public NormRect Expanded(double fraction) =>
        new NormRect(X - W * fraction, Y - H * fraction, W * (1 + 2 * fraction), H * (1 + 2 * fraction)).Clipped();

    /// Whole-pixel rect (top-left origin) inside an image of the given size; even keeps video encoders happy.
    public PixelRect Pixels(double width, double height, bool even = false)
    {
        var x = Math.Round(X * width);
        var y = Math.Round(Y * height);
        var w = Math.Round(W * width);
        var h = Math.Round(H * height);
        var x0 = Math.Max(0, x);
        var y0 = Math.Max(0, y);
        var x1 = Math.Min(width, x + w);
        var y1 = Math.Min(height, y + h);
        var rect = new PixelRect((int)x0, (int)y0, (int)Math.Max(0, x1 - x0), (int)Math.Max(0, y1 - y0));
        if (!even) return rect;
        return new PixelRect(rect.X / 2 * 2, rect.Y / 2 * 2, Math.Max(2, rect.Width / 2 * 2), Math.Max(2, rect.Height / 2 * 2));
    }

    public double Overlap(NormRect other)
    {
        var iw = Math.Max(0, Math.Min(MaxX, other.MaxX) - Math.Max(X, other.X));
        var ih = Math.Max(0, Math.Min(MaxY, other.MaxY) - Math.Max(Y, other.Y));
        var inter = iw * ih;
        var union = W * H + other.W * other.H - inter;
        return union > 0 ? inter / union : 0;
    }

    /// Largest centered rect with the given pixel aspect ratio (width / height).
    public static NormRect Centered(double ratio, double width, double height)
    {
        var normalized = ratio * (height / Math.Max(width, 1));
        double w = 1, h = 1 / normalized;
        if (h > 1)
        {
            h = 1;
            w = normalized;
        }
        return new NormRect((1 - w) / 2, (1 - h) / 2, w, h);
    }
}

public readonly record struct PixelRect(int X, int Y, int Width, int Height);

public sealed record CropSelection([property: JsonPropertyName("rect")] NormRect Rect);

public enum RedactStyle { Pixelate, Blur, Fill }

public static class RedactStyles
{
    public static readonly RedactStyle[] All = [RedactStyle.Pixelate, RedactStyle.Blur, RedactStyle.Fill];
    public static string Label(this RedactStyle style) => L("redact." + style.ToString().ToLowerInvariant());
}

public sealed record RedactSelection(
    [property: JsonPropertyName("regions")] IReadOnlyList<NormRect> Regions,
    [property: JsonPropertyName("style")] RedactStyle Style);

public sealed record TrimSelection(
    [property: JsonPropertyName("start")] double Start,
    [property: JsonPropertyName("end")] double End,
    /// Stream copy: instant and lossless, cuts land on the nearest keyframe.
    [property: JsonPropertyName("lossless")] bool Lossless);

public sealed record PdfPageEntry(
    /// Index into the selected files.
    [property: JsonPropertyName("file")] int File,
    [property: JsonPropertyName("page")] int Page,
    /// Extra clockwise rotation in degrees (multiples of 90).
    [property: JsonPropertyName("rotation")] int Rotation);

public sealed record PdfArrangement([property: JsonPropertyName("pages")] IReadOnlyList<PdfPageEntry> Pages);

public sealed record MetadataEdit(
    /// Only the fields the user changed.
    [property: JsonPropertyName("fields")] IReadOnlyDictionary<string, string> Fields,
    [property: JsonPropertyName("removeLocation")] bool RemoveLocation,
    /// Swap the edited file in for the original (the original goes to the Recycle Bin).
    [property: JsonPropertyName("replaceOriginal")] bool ReplaceOriginal);

public enum CropAspect { Free, Original, Square, R4x3, R3x2, R16x9, R9x16, R4x5 }

public static class CropAspects
{
    public static readonly CropAspect[] All =
        [CropAspect.Free, CropAspect.Original, CropAspect.Square, CropAspect.R4x3, CropAspect.R3x2, CropAspect.R16x9, CropAspect.R9x16, CropAspect.R4x5];

    public static string Label(this CropAspect aspect) => aspect switch
    {
        CropAspect.Free => L("crop.free"),
        CropAspect.Original => L("crop.original"),
        CropAspect.Square => "1:1",
        CropAspect.R4x3 => "4:3",
        CropAspect.R3x2 => "3:2",
        CropAspect.R16x9 => "16:9",
        CropAspect.R9x16 => "9:16",
        _ => "4:5",
    };

    /// Width / height in pixels.
    public static double? Ratio(this CropAspect aspect, double width, double height) => aspect switch
    {
        CropAspect.Free => null,
        CropAspect.Original => height > 0 ? width / height : null,
        CropAspect.Square => 1,
        CropAspect.R4x3 => 4.0 / 3,
        CropAspect.R3x2 => 1.5,
        CropAspect.R16x9 => 16.0 / 9,
        CropAspect.R9x16 => 9.0 / 16,
        _ => 0.8,
    };
}

/// Drag targets and rect math shared by the crop and redact editors (normalized coordinates).
public static class CanvasGeometry
{
    public enum TargetKind { Move, Create, Corner, Edge }

    public readonly record struct Target(TargetKind Kind, int Index = 0);

    /// 0 top-left, 1 top-right, 2 bottom-right, 3 bottom-left.
    public static (double X, double Y) Corner(NormRect r, int index) => index switch
    {
        0 => (r.X, r.Y),
        1 => (r.MaxX, r.Y),
        2 => (r.MaxX, r.MaxY),
        _ => (r.X, r.MaxY),
    };

    /// Rect spanned from a fixed anchor to the pointer; ratio is normalized width / height.
    public static NormRect Span(double ax, double ay, double px, double py, double? ratio, double minimum = 0.01)
    {
        double dx = px - ax, dy = py - ay;
        double sx = dx < 0 ? -1 : 1, sy = dy < 0 ? -1 : 1;
        double roomX = sx > 0 ? 1 - ax : ax, roomY = sy > 0 ? 1 - ay : ay;
        var w = Math.Min(Math.Max(Math.Abs(dx), minimum), roomX);
        var h = Math.Min(Math.Max(Math.Abs(dy), minimum), roomY);
        if (ratio is > 0)
        {
            var side = Math.Max(w, h * ratio.Value);
            w = side;
            h = side / ratio.Value;
            if (w > roomX)
            {
                w = roomX;
                h = w / ratio.Value;
            }
            if (h > roomY)
            {
                h = roomY;
                w = h * ratio.Value;
            }
        }
        return new NormRect(sx > 0 ? ax : ax - w, sy > 0 ? ay : ay - h, w, h);
    }

    public static NormRect Edge(int side, NormRect origin, double px, double py, double? ratio, double minimum = 0.01)
    {
        double x = origin.X, y = origin.Y, w = origin.W, h = origin.H;
        switch (side)
        {
            case 0:
                y = Math.Min(py, origin.MaxY - minimum);
                h = origin.MaxY - y;
                break;
            case 1:
                w = Math.Max(px - origin.X, minimum);
                break;
            case 2:
                h = Math.Max(py - origin.Y, minimum);
                break;
            default:
                x = Math.Min(px, origin.MaxX - minimum);
                w = origin.MaxX - x;
                break;
        }
        if (ratio is not > 0) return new NormRect(x, y, w, h).Clipped();
        var r = ratio.Value;
        if (side % 2 == 0)
        {
            var nw = h * r;
            if (nw > 1)
            {
                nw = 1;
                h = nw / r;
                if (side == 0) y = origin.MaxY - h;
            }
            x = origin.X + origin.W / 2 - nw / 2;
            w = nw;
        }
        else
        {
            var nh = w / r;
            if (nh > 1)
            {
                nh = 1;
                w = nh * r;
                if (side == 3) x = origin.MaxX - w;
            }
            y = origin.Y + origin.H / 2 - nh / 2;
            h = nh;
        }
        return new NormRect(x, y, w, h).Clamped();
    }

    public static NormRect Moved(NormRect origin, double sx, double sy, double px, double py) =>
        new NormRect(origin.X + (px - sx), origin.Y + (py - sy), origin.W, origin.H).Clamped();
}

public static class TimeText
{
    /// 75.4 -> "1:15.4", 3725 -> "1:02:05.0"
    public static string Format(double seconds, bool precise = true)
    {
        var value = double.IsFinite(seconds) ? Math.Max(0, seconds) : 0;
        var tenths = (long)Math.Round(value * 10);
        long h = tenths / 36_000, m = tenths / 600 % 60, s = tenths / 10 % 60, t = tenths % 10;
        var body = h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
        return precise ? body + "." + t : body;
    }

    /// "1:02.5", "62.5", "01:00:02,5" -> seconds.
    public static double? Parse(string raw)
    {
        var cleaned = raw.Trim().Replace(',', '.');
        if (cleaned.Length == 0) return null;
        var parts = cleaned.Split(':');
        if (parts.Length > 3) return null;
        double seconds = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return null;
            seconds = seconds * 60 + value;
        }
        return seconds;
    }
}
