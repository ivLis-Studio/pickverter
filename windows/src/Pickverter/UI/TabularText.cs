using System.Windows;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;

namespace Pickverter.UI;

/// One line of text whose digits all take the same width (OpenType tabular figures), so a number that changes in place
/// doesn't shift its neighbours. FormattedText can't ask for OpenType features; TextFormatter can.
internal static class TabularText
{
    private static readonly TextFormatter Formatter = TextFormatter.Create(TextFormattingMode.Ideal);

    /// The caller draws the line (TextLine.Draw) and disposes it.
    public static TextLine Format(string text, Typeface face, double size, Brush brush, double pixelsPerDip)
    {
        var properties = new RunProperties(face, size, brush, pixelsPerDip);
        var source = new Source(text, properties) { PixelsPerDip = pixelsPerDip };
        return Formatter.FormatLine(source, 0, 100_000, new Paragraph(properties), null);
    }

    private sealed class Source(string text, TextRunProperties properties) : TextSource
    {
        public override TextRun GetTextRun(int textSourceCharacterIndex) =>
            textSourceCharacterIndex < text.Length
                ? new TextCharacters(text, textSourceCharacterIndex, text.Length - textSourceCharacterIndex, properties)
                : new TextEndOfParagraph(1);

        public override TextSpan<CultureSpecificCharacterBufferRange> GetPrecedingText(int textSourceCharacterIndexLimit)
        {
            var limit = Math.Clamp(textSourceCharacterIndexLimit, 0, text.Length);
            return new TextSpan<CultureSpecificCharacterBufferRange>(limit,
                new CultureSpecificCharacterBufferRange(properties.CultureInfo, new CharacterBufferRange(text, 0, limit)));
        }

        public override int GetTextEffectCharacterIndexFromTextSourceCharacterIndex(int textSourceCharacterIndex) => textSourceCharacterIndex;
    }

    private sealed class RunProperties : TextRunProperties
    {
        private readonly Typeface face;
        private readonly double size;
        private readonly Brush brush;

        public RunProperties(Typeface face, double size, Brush brush, double pixelsPerDip)
        {
            this.face = face;
            this.size = size;
            this.brush = brush;
            PixelsPerDip = pixelsPerDip;
        }

        public override Typeface Typeface => face;
        public override double FontRenderingEmSize => size;
        public override double FontHintingEmSize => size;
        public override TextDecorationCollection TextDecorations => null!;
        public override Brush ForegroundBrush => brush;
        public override Brush BackgroundBrush => null!;
        public override CultureInfo CultureInfo => Loc.Culture;
        public override TextEffectCollection TextEffects => null!;
        public override TextRunTypographyProperties TypographyProperties => TabularFigures.Instance;
    }

    private sealed class Paragraph(TextRunProperties properties) : TextParagraphProperties
    {
        public override FlowDirection FlowDirection => FlowDirection.LeftToRight;
        public override TextAlignment TextAlignment => TextAlignment.Left;
        public override double LineHeight => 0;
        public override bool FirstLineInParagraph => true;
        public override TextRunProperties DefaultTextRunProperties => properties;
        public override TextWrapping TextWrapping => TextWrapping.NoWrap;
        public override TextMarkerProperties TextMarkerProperties => null!;
        public override double Indent => 0;
    }

    /// WPF's default typography, with tabular figures.
    private sealed class TabularFigures : TextRunTypographyProperties
    {
        public static readonly TabularFigures Instance = new();

        public override FontNumeralAlignment NumeralAlignment => FontNumeralAlignment.Tabular;
        public override FontNumeralStyle NumeralStyle => FontNumeralStyle.Normal;
        public override bool StandardLigatures => true;
        public override bool ContextualLigatures => true;
        public override bool ContextualAlternates => true;
        public override bool Kerning => true;
        public override bool DiscretionaryLigatures => false;
        public override bool HistoricalLigatures => false;
        public override bool HistoricalForms => false;
        public override bool CaseSensitiveForms => false;
        public override bool CapitalSpacing => false;
        public override bool SlashedZero => false;
        public override bool MathematicalGreek => false;
        public override bool EastAsianExpertForms => false;
        public override int AnnotationAlternates => 0;
        public override int ContextualSwashes => 0;
        public override int StandardSwashes => 0;
        public override int StylisticAlternates => 0;
        public override FontCapitals Capitals => FontCapitals.Normal;
        public override FontEastAsianLanguage EastAsianLanguage => FontEastAsianLanguage.Normal;
        public override FontEastAsianWidths EastAsianWidths => FontEastAsianWidths.Normal;
        public override FontFraction Fraction => FontFraction.Normal;
        public override FontVariants Variants => FontVariants.Normal;
        public override bool StylisticSet1 => false;
        public override bool StylisticSet2 => false;
        public override bool StylisticSet3 => false;
        public override bool StylisticSet4 => false;
        public override bool StylisticSet5 => false;
        public override bool StylisticSet6 => false;
        public override bool StylisticSet7 => false;
        public override bool StylisticSet8 => false;
        public override bool StylisticSet9 => false;
        public override bool StylisticSet10 => false;
        public override bool StylisticSet11 => false;
        public override bool StylisticSet12 => false;
        public override bool StylisticSet13 => false;
        public override bool StylisticSet14 => false;
        public override bool StylisticSet15 => false;
        public override bool StylisticSet16 => false;
        public override bool StylisticSet17 => false;
        public override bool StylisticSet18 => false;
        public override bool StylisticSet19 => false;
        public override bool StylisticSet20 => false;
    }
}
