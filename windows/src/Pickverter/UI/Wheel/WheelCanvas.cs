using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.TextFormatting;

namespace Pickverter.UI.Wheel;

/// Draws a WheelModel: the disc of slots, the outer ring of options, the hub and the ring tabs. Flat fills only.
internal sealed class WheelCanvas : FrameworkElement
{
    private readonly WheelModel model;
    private BitmapSource? thumbnail;

    public WheelCanvas(WheelModel model)
    {
        this.model = model;
        var side = model.Geometry.BoxSide;
        Width = side;
        Height = side;
        SnapsToDevicePixels = true;
        UseLayoutRounding = false;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        model.Changed += _ => InvalidateVisual();
    }

    /// Nearly invisible but hit-testable: the drop target that catches a drag before its files are known.
    public bool Probing { get; set; }

    public BitmapSource? Thumbnail
    {
        get => thumbnail;
        set
        {
            thumbnail = value;
            InvalidateVisual();
        }
    }

    private WheelGeometry G => model.Geometry;
    private Point Center => new(G.Center, G.Center);
    private double Dip => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    protected override void OnRender(DrawingContext dc)
    {
        if (Probing)
        {
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(1, 128, 128, 128)), null, Center, G.SubOuter, G.SubOuter);
            return;
        }
        if (model.Items.Count == 0 && model.Files.Count == 0) return;
        if (model.SubRing is { } ring) DrawSubRing(dc, ring);
        DrawDisc(dc);
        if (model.Choice != null) DrawChoiceCaption(dc);
        else if (!model.TabsHidden) DrawTabs(dc);
    }

    // Disc and slots

    private void DrawDisc(DrawingContext dc)
    {
        dc.DrawEllipse(Theme.Surface, new Pen(Theme.Hairline, 1), Center, G.DiscRadius, G.DiscRadius);
        var count = model.Segments.Count;
        if (count == 0)
        {
            var dashed = new Pen(Theme.Hairline, 1) { DashStyle = new DashStyle([4, 5], 0) };
            dc.DrawEllipse(null, dashed, Center, G.OuterRadius - 12, G.OuterRadius - 12);
        }
        var span = 360.0 / Math.Max(count, 1);
        for (var index = 0; index < count; index++)
        {
            var segment = model.Segments[index];
            var mid = -90 + span * index;
            var highlighted = model.Highlighted == index;
            var dimmed = highlighted && model.SubHighlighted != null;
            var selected = highlighted && !dimmed;
            var shape = RingGeometry.Segment(mid - span / 2, mid + span / 2, G.InnerRadius, G.OuterRadius);
            if (segment.Unavailable) dc.PushOpacity(segment.NeedsDownload ? 0.6 : 0.35);
            dc.PushTransform(new TranslateTransform(Center.X, Center.Y));
            dc.DrawGeometry(selected ? Theme.Accent : highlighted ? Theme.AccentSubtle : Theme.Tile, null, shape);
            dc.Pop();
            if (segment.HasVariants)
            {
                var notch = RingGeometry.Polar(Center, G.OuterRadius - 7, mid);
                dc.PushTransform(new RotateTransform(mid + 90, notch.X, notch.Y));
                var notchBrush = selected ? WithOpacity(Theme.TextOnAccent, 0.6) : WithOpacity(Theme.TextPrimary, 0.18);
                dc.DrawRoundedRectangle(notchBrush, null, new Rect(notch.X - 5, notch.Y - 1, 10, 2), 1, 1);
                dc.Pop();
            }
            var labelRadius = (G.InnerRadius + G.OuterRadius) / 2 + 2;
            var arc = 2 * Math.PI * labelRadius / Math.Max(count, 1);
            var width = Math.Max(40, Math.Min(arc - 12, G.OuterRadius - G.InnerRadius - 8));
            var position = RingGeometry.Polar(Center, labelRadius, mid);
            DrawLabel(dc, segment, position, width, selected, compact: false);
            if (segment.Unavailable) dc.Pop();
        }
        DrawHub(dc);
    }

    private void DrawLabel(DrawingContext dc, SegmentData segment, Point center, double width, bool selected, bool compact)
    {
        var brush = selected ? Theme.TextOnAccent : WithOpacity(Theme.TextPrimary, 0.88);
        if (segment.IsMore)
        {
            var lines = new List<FormattedText>
            {
                Glyphs.Formatted("ellipsis", 14, brush, Dip),
                Fit(segment.Title, width, 11, FontWeights.Medium, brush),
            };
            if (segment.PageLabel is { } page) lines.Add(Fit(page, width, 9, FontWeights.Normal, WithOpacity(brush, 0.6)));
            DrawStack(dc, lines, center, 3);
            return;
        }
        if (segment.IsFormat)
        {
            var size = segment.Title.Length > 4 ? 12.5 : 14;
            DrawStack(dc, [Fit(segment.Title, width, size, FontWeights.SemiBold, brush, 0.7)], center, 0);
            return;
        }
        var items = new List<FormattedText>();
        if (!string.IsNullOrEmpty(segment.Symbol)) items.Add(Glyphs.Formatted(segment.Symbol, compact ? 13 : 16, brush, Dip));
        // A single long word ("Ausgewogen", "Redimensionner") shrinks on one line instead of losing its end.
        items.Add(OneWord(segment.Title)
            ? Fit(segment.Title, width, compact ? 10 : 11, FontWeights.Medium, brush, 0.55)
            : Fit(segment.Title, width, compact ? 10 : 11, FontWeights.Medium, brush, 0.72, lines: 2));
        DrawStack(dc, items, center, compact ? 2 : 4);
    }

    /// No space to break at. Chinese and Japanese may break between any two characters, so they don't count.
    private static bool OneWord(string title) =>
        !title.Contains(' ') && !title.Any(c => c is >= '\u3040' and <= '\u30FF' or >= '\u3400' and <= '\u9FFF' or >= '\uF900' and <= '\uFAFF');

    // Outer ring

    private void DrawSubRing(DrawingContext dc, SubRingData ring)
    {
        var spanDegrees = ring.Span * 180 / Math.PI;
        var first = RingGeometry.Degrees(ring.AngleOf(0)) - spanDegrees / 2;
        var last = first + spanDegrees * ring.Items.Count;
        dc.PushTransform(new TranslateTransform(Center.X, Center.Y));
        var band = RingGeometry.Band(first - 1.2, last + 1.2, G.SubInner - 4, G.SubOuter + 4);
        dc.DrawGeometry(Theme.Surface, new Pen(Theme.Hairline, 1), band);
        dc.Pop();
        var labelRadius = (G.SubInner + G.SubOuter) / 2;
        var arc = ring.Span * labelRadius;
        for (var index = 0; index < ring.Items.Count; index++)
        {
            var item = ring.Items[index];
            var start = first + spanDegrees * index;
            var selected = model.SubHighlighted == index;
            var tile = RingGeometry.Segment(start, start + spanDegrees, G.SubInner, G.SubOuter, 3, 7);
            dc.PushTransform(new TranslateTransform(Center.X, Center.Y));
            dc.DrawGeometry(selected ? Theme.Accent : Theme.Tile, null, tile);
            if (item.IsDefault && !selected) dc.DrawGeometry(null, new Pen(WithOpacity(Theme.Accent, 0.7), 1.3), tile);
            dc.Pop();
            var mid = start + spanDegrees / 2;
            var radians = mid * Math.PI / 180;
            var width = LabelWidth(radians, arc, G.SubOuter - G.SubInner);
            if (item.Unavailable) dc.PushOpacity(item.NeedsDownload ? 0.6 : 0.35);
            DrawLabel(dc, item, RingGeometry.Polar(Center, labelRadius, mid), width, selected, compact: true);
            if (item.Unavailable) dc.Pop();
        }
    }

    /// Labels are horizontal, so near 3 and 9 o'clock their width runs across the band.
    private static double LabelWidth(double angle, double arc, double thickness)
    {
        var across = Math.Abs(Math.Cos(angle));
        var along = Math.Abs(Math.Sin(angle));
        var byBand = across > 0.05 ? (thickness - 6) / across : double.MaxValue;
        var byArc = along > 0.05 ? (arc - 8) / along : double.MaxValue;
        return Math.Max(40, Math.Min(Math.Min(byBand, byArc), arc + 20));
    }

    // Hub

    private void DrawHub(DrawingContext dc)
    {
        var radius = G.InnerRadius - 5;
        dc.DrawEllipse(Theme.HubFill, new Pen(Theme.Hairline, 1), Center, radius, radius);
        if (model.PointerAngle is { } angle && model.Highlighted != null)
        {
            var pen = new Pen(Theme.Accent, 2.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            var turn = 0.04 * 2 * Math.PI;
            dc.DrawGeometry(null, pen, ProgressRing.Arc(Center, radius - 3.25, angle - turn, angle + turn));
        }
        var width = radius * 2 - 22;
        var lines = new List<HubLine>();
        var spacing = 3.0;
        if (model.Flash is { } flash)
        {
            lines.Add(Glyphs.Formatted("exclamationmark.triangle", 16, Theme.Caution, Dip));
            lines.Add(Fit(flash, width, 9.5, FontWeights.Normal, Theme.TextPrimary, 0.9, lines: 4));
        }
        else if (model.Choice is { } choice)
        {
            var destination = model.HighlightedItem is WheelModel.Item.Destination target ? target.Value : ResultDestination.File;
            lines.Add(Glyphs.Formatted(destination.Symbol(), 18, Theme.TextPrimary, Dip));
            lines.Add(Fit(destination.Label(), width, 11.5, FontWeights.SemiBold, Theme.TextPrimary, 0.8, lines: 2));
            lines.Add(Fit(choice.Option.Title, width, 9.5, FontWeights.Normal, Theme.TextSecondary));
        }
        else if (model.Active is { } active)
        {
            var option = active.Option;
            var parent = active.Parent;
            var format = parent ?? option;
            spacing = 2;
            if (format.IsFormat) lines.Add(Fit(format.Title, width, 18, FontWeights.SemiBold, Theme.TextPrimary, 0.6));
            else lines.Add(Glyphs.Formatted(option.Symbol.Length == 0 ? parent?.Symbol ?? "circle" : option.Symbol, 18, Theme.TextPrimary, Dip));
            if (parent != null) lines.Add(Fit(option.Title, width, 11, FontWeights.SemiBold, Theme.AccentText, 0.7));
            var unavailable = option.Unavailable ?? parent?.Unavailable;
            // A missing ffmpeg or rhwp is an offer, not a dead end: a click downloads it and carries on.
            var offersDownload = Tools.IsDownload(unavailable);
            var estimate = unavailable == null ? EstimateLine(width) : null;
            // The size preview takes a line's place: for an option of a slot, the one under its name that repeats the
            // slot's title; otherwise the hint at the bottom.
            if (estimate is { } replacing && parent != null)
            {
                lines.Add(replacing);
            }
            else
            {
                lines.Add(Fit(offersDownload ? Tools.DownloadPrompt(unavailable) : unavailable ?? model.PreviewLine(option, parent), width, 9.5,
                    FontWeights.Normal, offersDownload ? Theme.AccentText : unavailable != null ? Theme.Caution : Theme.TextSecondary, 0.85, lines: 2,
                    middle: unavailable == null));
            }
            string? hint = null;
            if (parent != null && active.Implied) hint = "↑↓ " + LP("hint.options", parent.Variants.Count);
            else if (parent == null && option.Variants.Count > 0) hint = LP("hint.options", option.Variants.Count);
            else hint = model.DestinationHint(option);
            if (estimate is { } bottom && parent == null) lines.Add(bottom);
            else if (hint != null) lines.Add(Fit(hint, width, 8.5, FontWeights.Normal, Theme.TextTertiary, 0.7));
        }
        else if (model.HighlightedItem is WheelModel.Item.More)
        {
            lines.Add(Glyphs.Formatted("arrow.forward.circle", 18, Theme.TextSecondary, Dip));
            lines.Add(Fit(model.Origin == WheelModel.WheelOrigin.Drag ? L("wheel.dwell_next") : L("wheel.next_page"), width, 9.5,
                FontWeights.Normal, Theme.TextSecondary, 0.8, lines: 2));
        }
        else
        {
            DrawIdleHub(dc, width);
            return;
        }
        DrawStack(dc, lines, Center, spacing);
    }

    /// The size preview: "Checking the size…" quietly, then the result (tabular digits) or "Already small".
    private HubLine? EstimateLine(double width)
    {
        if (model.EstimateLine is not { } text) return null;
        const double size = 9.5;
        if (model.EstimatePending) return Fit(text, width, size, FontWeights.Normal, Theme.TextTertiary, 0.8);
        if (model.Estimate?.Kind != EstimateKind.Size) return Fit(text, width, size, FontWeights.Normal, Theme.TextSecondary, 0.8);
        var face = Theme.Face(FontWeights.Normal);
        var line = TabularText.Format(text, face, size, Theme.TextSecondary, Dip);
        if (line.WidthIncludingTrailingWhitespace > width)
        {
            var scaled = Math.Max(size * 0.8, size * width / line.WidthIncludingTrailingWhitespace);
            line.Dispose();
            line = TabularText.Format(text, face, scaled, Theme.TextSecondary, Dip);
        }
        return new HubLine(null, line);
    }

    /// One line of the hub: formatted text, or a line laid out with tabular digits (the size estimate).
    private readonly record struct HubLine(FormattedText? Text, TextLine? Tabular)
    {
        public static implicit operator HubLine(FormattedText text) => new(text, null);

        public double Height => Text?.Height ?? Tabular?.Height ?? 0;

        public double Width => Text is { } text
            ? text.TextAlignment == TextAlignment.Center && text.MaxTextWidth > 0 ? text.MaxTextWidth : text.WidthIncludingTrailingWhitespace
            : Tabular?.WidthIncludingTrailingWhitespace ?? 0;

        public void Draw(DrawingContext dc, Point origin)
        {
            if (Text != null) dc.DrawText(Text, origin);
            else Tabular?.Draw(dc, origin, InvertAxes.None);
        }
    }

    private void DrawIdleHub(DrawingContext dc, double width)
    {
        var title = Fit(model.FilesTitle, width, 10.5, FontWeights.Medium, Theme.TextPrimary, 0.85, middle: true);
        var hint = Fit(model.Segments.Count == 0 ? L("wheel.empty") : model.Hint, width, 8.5, FontWeights.Normal, Theme.TextSecondary, 0.75);
        const double thumb = 44;
        var total = thumb + 4 + title.Height + 2 + hint.Height;
        var top = Center.Y - total / 2;
        var box = new Rect(Center.X - thumb / 2, top, thumb, thumb);
        var count = model.Files.Count;
        if (count > 1)
        {
            dc.PushTransform(new RotateTransform(-8, box.X + box.Width / 2 - 5, box.Y + box.Height / 2 + 1));
            dc.DrawRoundedRectangle(Theme.Tile, null, new Rect(box.X + 4 - 5, box.Y + 4 + 1, 36, 36), 6, 6);
            dc.Pop();
        }
        if (thumbnail != null)
        {
            var scale = Math.Min(40 / thumbnail.Width, 40 / thumbnail.Height);
            var w = thumbnail.Width * scale;
            var h = thumbnail.Height * scale;
            dc.DrawImage(thumbnail, new Rect(Center.X - w / 2, box.Y + (thumb - h) / 2, w, h));
        }
        else
        {
            var glyph = Glyphs.Formatted(Kind(model.Files.FirstOrDefault()), 26, Theme.TextSecondary, Dip);
            dc.DrawText(glyph, new Point(Center.X - glyph.Width / 2, box.Y + (thumb - glyph.Height) / 2));
        }
        if (count > 1)
        {
            var badge = Fit(count.ToString(Loc.Culture), 30, 10, FontWeights.SemiBold, Theme.TextOnAccent);
            var bw = Math.Max(18, badge.Width + 10);
            var bc = new Point(Center.X + 18, box.Y + thumb / 2 - 17);
            dc.DrawRoundedRectangle(Theme.Accent, null, new Rect(bc.X - bw / 2, bc.Y - 9, bw, 18), 9, 9);
            dc.DrawText(badge, new Point(bc.X - badge.Width / 2, bc.Y - badge.Height / 2));
        }
        var y = top + thumb + 4;
        dc.DrawText(title, new Point(Center.X - TextWidth(title) / 2, y));
        y += title.Height + 2;
        dc.DrawText(hint, new Point(Center.X - TextWidth(hint) / 2, y));
    }

    private static string Kind(string? path) => path == null ? "doc" : FileKinds.Of(path) switch
    {
        FileKind.Image => "photo",
        FileKind.Video => "film",
        FileKind.Audio => "waveform",
        FileKind.Pdf => "doc.richtext",
        FileKind.Folder => "folder",
        FileKind.Archive => "archivebox",
        FileKind.Table => "tablecells",
        _ => "doc.text",
    };

    // Ring tabs

    private void DrawTabs(DrawingContext dc)
    {
        G.TabWidth = TabWidthFor(Dip);
        var bar = new Rect(Center.X - G.TabBarWidth / 2, Center.Y + G.TabBarOffset - G.TabBarHeight / 2, G.TabBarWidth, G.TabBarHeight);
        dc.DrawRoundedRectangle(Theme.Surface, new Pen(Theme.Hairline, 1), bar, bar.Height / 2, bar.Height / 2);
        foreach (var mode in WheelModes.All)
        {
            var (x, y, w, h) = G.TabRect(mode);
            var rect = new Rect(Center.X + x, Center.Y + y, w, h);
            var active = model.Mode == mode;
            var empty = model.Counts.GetValueOrDefault(mode) == 0;
            if (active)
            {
                dc.DrawRoundedRectangle(Theme.SelectedTab, new Pen(Theme.Hairline, 1), rect, h / 2, h / 2);
            }
            else if (model.HoveredTab == mode)
            {
                dc.DrawRoundedRectangle(Theme.SubtleHover, null, rect, h / 2, h / 2);
            }
            if (empty) dc.PushOpacity(0.3);
            var key = Fit(mode.KeyName(), 64, 10.5, FontWeights.Medium, Theme.TextSecondary);
            var title = Fit(mode.Title(), w - 14 - TextWidth(key), 12, active ? FontWeights.SemiBold : FontWeights.Normal, Theme.TextPrimary, 0.7);
            var total = TextWidth(key) + 5 + TextWidth(title);
            var left = rect.X + (w - total) / 2;
            dc.DrawText(key, new Point(left, rect.Y + (h - key.Height) / 2));
            dc.DrawText(title, new Point(left + TextWidth(key) + 5, rect.Y + (h - title.Height) / 2));
            if (empty) dc.Pop();
        }
    }

    /// Each ring tab fits its key and name in the current language: "Umschalt Umwandeln" needs more room than "Shift Convert".
    public static double TabWidthFor(double pixelsPerDip)
    {
        var widest = 0.0;
        foreach (var mode in WheelModes.All)
        {
            var key = new FormattedText(mode.KeyName(), Loc.Culture, FlowDirection.LeftToRight, Theme.Face(FontWeights.Medium), 10.5, Brushes.Black, pixelsPerDip);
            var title = new FormattedText(mode.Title(), Loc.Culture, FlowDirection.LeftToRight, Theme.Face(FontWeights.SemiBold), 12, Brushes.Black,
                pixelsPerDip);
            widest = Math.Max(widest, key.WidthIncludingTrailingWhitespace + 5 + title.WidthIncludingTrailingWhitespace + 24);
        }
        return Math.Clamp(Math.Ceiling(widest), 96, 136);
    }

    private void DrawChoiceCaption(DrawingContext dc)
    {
        var title = Fit(L("dest.title"), 260, 12, FontWeights.SemiBold, Theme.TextPrimary);
        var keys = string.Join(" · ", KeyNames.Enter, KeyNames.Combo(KeyNames.Ctrl, "C"), KeyNames.Combo(KeyNames.Ctrl, "S"), KeyNames.Esc);
        var hint = Fit(keys, 220, 10.5, FontWeights.Normal, Theme.TextSecondary);
        var width = TextWidth(title) + 8 + TextWidth(hint) + 28;
        var rect = new Rect(Center.X - width / 2, Center.Y + G.TabBarOffset - 15, width, 30);
        dc.DrawRoundedRectangle(Theme.Surface, new Pen(Theme.Hairline, 1), rect, 15, 15);
        dc.DrawText(title, new Point(rect.X + 14, rect.Y + (30 - title.Height) / 2));
        dc.DrawText(hint, new Point(rect.X + 14 + TextWidth(title) + 8, rect.Y + (30 - hint.Height) / 2));
    }

    // Text

    private static double TextWidth(FormattedText text) => text.MaxTextWidth > 0 && text.TextAlignment == TextAlignment.Center ? text.MaxTextWidth : text.WidthIncludingTrailingWhitespace;

    /// Text that fits a width: shrinks down to minScale first, then wraps (up to lines) and ends with an ellipsis.
    private FormattedText Fit(string text, double width, double size, FontWeight weight, Brush brush, double minScale = 0.8, int lines = 1, bool middle = false)
    {
        if (middle && lines == 1) text = MiddleTruncate(text, width, size, weight);
        var face = Theme.Face(weight);
        FormattedText Make(double fontSize)
        {
            var shown = lines > 1 ? KeepWords(text, face, fontSize, width) : text;
            var formatted = new FormattedText(shown, Loc.Culture, FlowDirection.LeftToRight, face, fontSize, brush, Dip);
            formatted.Trimming = TextTrimming.CharacterEllipsis;
            return formatted;
        }
        var result = Make(size);
        if (lines <= 1)
        {
            // Text doesn't shrink exactly in proportion (hinting), so aim a little under and try again if needed.
            var fontSize = size;
            for (var attempt = 0; attempt < 3 && result.WidthIncludingTrailingWhitespace > width && fontSize > size * minScale; attempt++)
            {
                fontSize = Math.Max(size * minScale, fontSize * width / result.WidthIncludingTrailingWhitespace * 0.97);
                result = Make(fontSize);
            }
            if (result.WidthIncludingTrailingWhitespace > width)
            {
                result.MaxTextWidth = width;
                result.MaxLineCount = 1;
            }
            return result;
        }
        result.MaxTextWidth = width;
        result.TextAlignment = TextAlignment.Center;
        var lineHeight = result.Height / Math.Max(1, LinesOf(result, size));
        for (var scale = 1.0; scale >= minScale; scale -= 0.07)
        {
            var candidate = Make(size * scale);
            candidate.MaxTextWidth = width;
            candidate.TextAlignment = TextAlignment.Center;
            if (candidate.Height <= size * scale * 1.34 * lines + 0.5 && !Overflows(candidate, width))
            {
                candidate.MaxLineCount = lines;
                return candidate;
            }
        }
        var last = Make(size * minScale);
        last.MaxTextWidth = width;
        last.TextAlignment = TextAlignment.Center;
        last.MaxLineCount = lines;
        _ = lineHeight;
        return last;
    }

    private static int LinesOf(FormattedText text, double size) => Math.Max(1, (int)Math.Round(text.Height / (size * 1.33)));

    /// Korean puts spaces between words, but WPF breaks Hangul between any two syllables ("받/고"). Such text is
    /// wrapped at its spaces first, as on the Mac; a single word wider than the line still breaks where WPF puts it.
    private string KeepWords(string text, Typeface face, double size, double width)
    {
        if (width <= 0 || !text.Any(IsHangul)) return text;
        double Measure(string value) => new FormattedText(value, Loc.Culture, FlowDirection.LeftToRight, face, size, Brushes.Black, Dip).WidthIncludingTrailingWhitespace;
        var wrapped = new List<string>();
        foreach (var paragraph in text.Split('\n'))
        {
            var line = "";
            foreach (var word in paragraph.Split(' '))
            {
                var longer = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && Measure(longer) > width)
                {
                    wrapped.Add(line);
                    line = word;
                }
                else
                {
                    line = longer;
                }
            }
            wrapped.Add(line);
        }
        return string.Join("\n", wrapped);
    }

    private static bool IsHangul(char c) => c is >= '\uAC00' and <= '\uD7A3' or >= '\u1100' and <= '\u11FF' or >= '\u3130' and <= '\u318F';

    /// A single word longer than the width would be broken mid-word: treat it as overflow and shrink instead.
    private static bool Overflows(FormattedText text, double width) => text.WidthIncludingTrailingWhitespace > width + 0.5 && text.MinWidth > width;

    private string MiddleTruncate(string text, double width, double size, FontWeight weight)
    {
        var face = Theme.Face(weight);
        double Measure(string value) => new FormattedText(value, Loc.Culture, FlowDirection.LeftToRight, face, size, Brushes.Black, Dip).WidthIncludingTrailingWhitespace;
        if (Measure(text) <= width / 0.85) return text;
        var keep = text.Length;
        while (keep > 4)
        {
            keep -= 1;
            var head = (keep + 1) / 2;
            var tail = keep - head;
            var candidate = text[..head] + "…" + text[^tail..];
            if (Measure(candidate) <= width / 0.85) return candidate;
        }
        return text;
    }

    private static void DrawStack(DrawingContext dc, IReadOnlyList<FormattedText> lines, Point center, double spacing) =>
        DrawStack(dc, lines.Select(line => (HubLine)line).ToList(), center, spacing);

    private static void DrawStack(DrawingContext dc, IReadOnlyList<HubLine> lines, Point center, double spacing)
    {
        var total = lines.Sum(line => line.Height) + spacing * Math.Max(0, lines.Count - 1);
        var y = center.Y - total / 2;
        foreach (var line in lines)
        {
            line.Draw(dc, new Point(center.X - line.Width / 2, y));
            y += line.Height + spacing;
            line.Tabular?.Dispose();
        }
    }

    private static Brush WithOpacity(Brush brush, double opacity)
    {
        var copy = brush.CloneCurrentValue();
        copy.Opacity = brush.Opacity * opacity;
        copy.Freeze();
        return copy;
    }
}
