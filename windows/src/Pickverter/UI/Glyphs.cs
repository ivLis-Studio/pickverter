using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Pickverter.UI;

/// SF Symbol names from the shared core, drawn with Fluent UI System Icons (see scripts/icons.json).
internal static partial class Glyphs
{
    public static bool Has(string symbol) => Table.ContainsKey(symbol);

    public static (FontFamily Family, string Text) Get(string symbol)
    {
        if (string.IsNullOrEmpty(symbol) || !Table.TryGetValue(symbol, out var entry)) entry = Table["circle"];
        return (entry.Filled ? Theme.IconsFilled : Theme.IconsRegular, char.ConvertFromUtf32(entry.Code));
    }

    public static TextBlock Icon(string symbol, double size, Brush? brush = null)
    {
        var (family, text) = Get(symbol);
        var block = new TextBlock
        {
            Text = text,
            FontFamily = family,
            FontSize = size,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            LineHeight = size,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        if (brush != null) block.Foreground = brush;
        return block;
    }

    public static FormattedText Formatted(string symbol, double size, Brush brush, double pixelsPerDip)
    {
        var (family, text) = Get(symbol);
        return new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), size, brush, pixelsPerDip);
    }
}
