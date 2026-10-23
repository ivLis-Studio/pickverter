using System.Xml;
using System.Xml.Linq;
using ImageMagick;

namespace Pickverter;

/// Reads DocLang v0.6, the XML that rhwp's export-doclang writes for HWP and HWPX documents, into the shared document
/// model: paragraphs with bold, italic, underline, strikethrough and links, headings, lists, OTSL tables, pictures,
/// formulas and footnotes. Running headers and footers, layout groups and custom payloads are left out.
///
/// The document model's tables hold text, so pictures inside cells follow their table. Footnotes come last, numbered,
/// after a rule. Damaged or truncated XML never throws: whatever was readable before the damage is kept.
public static class DocLang
{
    private static readonly HashSet<string> InlineNames = ["bold", "italic", "underline", "strikethrough", "superscript", "subscript", "href"];

    private static readonly HashSet<string> BlockNames =
        ["text", "heading", "list", "table", "picture", "formula", "footnote", "page_header", "page_footer", "group", "page_break"];

    /// Layout-only parts (repeating headers and footers, flow groups, positions, custom payloads) and stray OTSL tokens.
    private static readonly HashSet<string> Skipped =
    [
        "page_header", "page_footer", "group", "location", "custom", "hwp_prop", "thread", "page_break", "caption", "marker", "src",
        "ldiv", "ched", "fcel", "ecel", "lcel", "ucel", "xcel", "nl",
    ];

    /// The XML rhwp wrote at path. baseDirectory: the folder it ran in (file pictures must be inside it).
    public static Doc Read(string path, string? baseDirectory, string title)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Parse(stream, baseDirectory, title);
        }
        catch (IOException error)
        {
            Debug.Log("doclang read: " + error.Message);
            return new Doc { Title = title };
        }
        catch (UnauthorizedAccessException error)
        {
            Debug.Log("doclang read: " + error.Message);
            return new Doc { Title = title };
        }
    }

    public static Doc Parse(string xml, string? baseDirectory, string title)
    {
        using var reader = new StringReader(xml);
        return Read(Load(reader), baseDirectory, title);
    }

    public static Doc Parse(Stream stream, string? baseDirectory, string title) => Read(Load(stream), baseDirectory, title);

    private static Doc Read(XElement holder, string? baseDirectory, string title)
    {
        var reader = new Reader(baseDirectory);
        var doc = new Doc { Title = title };
        var roots = holder.Elements().ToList();
        var root = roots.Count == 1 && roots[0].Name.LocalName == "doclang" ? roots[0] : holder;
        reader.Blocks(root, doc.Blocks);
        reader.AppendFootnotes(doc.Blocks);
        return doc;
    }

    // Loading

    private static XmlReaderSettings Settings => new()
    {
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        CheckCharacters = false,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = false,
    };

    private static XElement Load(TextReader text)
    {
        try
        {
            using var reader = XmlReader.Create(text, Settings);
            return Load(reader);
        }
        catch (Exception error) when (error is XmlException or ArgumentException or InvalidOperationException)
        {
            Debug.Log("doclang: " + error.Message);
            return new XElement("holder");
        }
    }

    private static XElement Load(Stream stream)
    {
        try
        {
            using var reader = XmlReader.Create(stream, Settings);
            return Load(reader);
        }
        catch (Exception error) when (error is XmlException or ArgumentException or InvalidOperationException)
        {
            Debug.Log("doclang: " + error.Message);
            return new XElement("holder");
        }
    }

    /// Builds the tree node by node, keeping whitespace (a space between two bold words is content). Damage stops the
    /// walk, and everything read before it stays.
    private static XElement Load(XmlReader reader)
    {
        var holder = new XElement("holder");
        var open = new Stack<XElement>();
        open.Push(holder);
        try
        {
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                    {
                        var element = new XElement(reader.LocalName);
                        var empty = reader.IsEmptyElement;
                        if (reader.HasAttributes)
                        {
                            while (reader.MoveToNextAttribute())
                            {
                                if (reader.Prefix == "xmlns" || reader.LocalName == "xmlns") continue;
                                element.SetAttributeValue(reader.LocalName, reader.Value);
                            }
                            reader.MoveToElement();
                        }
                        open.Peek().Add(element);
                        if (!empty) open.Push(element);
                        break;
                    }
                    case XmlNodeType.EndElement:
                        if (open.Count > 1) open.Pop();
                        break;
                    case XmlNodeType.Text:
                    case XmlNodeType.CDATA:
                    case XmlNodeType.Whitespace:
                    case XmlNodeType.SignificantWhitespace:
                        open.Peek().Add(new XText(reader.Value));
                        break;
                }
            }
        }
        catch (Exception error) when (error is XmlException or ArgumentException or InvalidOperationException or DecoderFallbackException)
        {
            Debug.Log("doclang: " + error.Message);
        }
        return holder;
    }

    // Reading

    private sealed class Reader(string? baseDirectory)
    {
        private readonly List<List<Block>> footnotes = [];

        public void Blocks(XElement parent, List<Block> output)
        {
            // Inline content outside a <text> (not what rhwp writes) still becomes a paragraph.
            var loose = new List<Run>();
            foreach (var node in parent.Nodes())
            {
                switch (node)
                {
                    case XText text:
                        loose.AddRange(TextRuns(text.Value, RunStyle.None, null));
                        break;
                    case XElement element when InlineNames.Contains(element.Name.LocalName):
                        Inline(element, RunStyle.None, null, loose);
                        break;
                    case XElement element:
                        Paragraph(output, loose);
                        loose.Clear();
                        Block(element, output);
                        break;
                }
            }
            Paragraph(output, loose);
        }

        private void Block(XElement element, List<Block> output)
        {
            var name = element.Name.LocalName;
            switch (name)
            {
                case "text":
                    Paragraph(output, Inlines(element));
                    break;
                case "heading":
                {
                    var runs = Doc.Trim(Inlines(element));
                    var level = int.TryParse((string?)element.Attribute("level"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 1;
                    if (runs.Count > 0) output.Add(new HeadingBlock(Math.Clamp(level, 1, 6), runs));
                    break;
                }
                case "list":
                    List(element, output);
                    break;
                case "table":
                    Table(element, output);
                    break;
                case "picture":
                    if (Picture(element) is { } image) output.Add(image);
                    break;
                case "formula":
                {
                    // LaTeX (or rhwp's equation script when it has none), shown as code.
                    var formula = element.Value.Trim();
                    if (formula.Length > 0) output.Add(new ParagraphBlock(TextRuns(formula, RunStyle.Code, null).ToList()));
                    break;
                }
                case "footnote":
                {
                    var note = new List<Block>();
                    Blocks(element, note);
                    if (note.Count > 0) footnotes.Add(note);
                    break;
                }
                default:
                    if (Skipped.Contains(name)) break;
                    if (element.Descendants().Any(child => BlockNames.Contains(child.Name.LocalName))) Blocks(element, output);
                    else Paragraph(output, Inlines(element));
                    break;
            }
        }

        /// Notes come last, numbered in document order (DocLang numbers them implicitly), after a rule.
        public void AppendFootnotes(List<Block> output)
        {
            var entries = new List<ListEntry>();
            foreach (var note in footnotes)
            {
                var runs = Flatten(note, [], new Run(" "));
                if (runs.Count > 0) entries.Add(new ListEntry(runs));
            }
            if (entries.Count == 0) return;
            output.Add(new RuleBlock());
            output.Add(new ListBlock(true, entries));
        }

        /// Items start at each <ldiv/> (its optional <marker> is the bullet, which the list draws itself); the blocks after
        /// it, up to the next one, are the item. Nested lists indent; tables and pictures inside items follow the list.
        private void List(XElement list, List<Block> output)
        {
            var entries = new List<ListEntry>();
            var after = new List<Block>();
            Items(list, 0, entries, after);
            if (entries.Count > 0) output.Add(new ListBlock((string?)list.Attribute("class") == "ordered", entries));
            output.AddRange(after);
        }

        private void Items(XElement list, int level, List<ListEntry> entries, List<Block> after)
        {
            List<Run>? item = null;
            void Close()
            {
                if (item == null) return;
                var runs = Doc.Trim(item);
                if (runs.Count > 0) entries.Add(new ListEntry(runs, level));
                item = null;
            }
            void Line(IReadOnlyList<Run> runs)
            {
                var trimmed = Doc.Trim(runs);
                if (trimmed.Count == 0) return;
                item ??= [];
                if (item.Count > 0) item.Add(Run.Break);
                item.AddRange(trimmed);
            }
            foreach (var node in list.Nodes())
            {
                if (node is XText text)
                {
                    Line(TextRuns(text.Value, RunStyle.None, null).ToList());
                    continue;
                }
                if (node is not XElement element) continue;
                switch (element.Name.LocalName)
                {
                    case "ldiv":
                        Close();
                        item = [];
                        break;
                    case "list":
                        Close();
                        Items(element, Math.Min(level + 1, 7), entries, after);
                        break;
                    default:
                    {
                        var blocks = new List<Block>();
                        if (InlineNames.Contains(element.Name.LocalName)) blocks.Add(new ParagraphBlock(Styled(element)));
                        else Block(element, blocks);
                        foreach (var block in blocks)
                        {
                            switch (block)
                            {
                                case ParagraphBlock paragraph:
                                    Line(paragraph.Runs);
                                    break;
                                case HeadingBlock heading:
                                    Line(heading.Runs);
                                    break;
                                case QuoteBlock quote:
                                    Line(quote.Runs);
                                    break;
                                case ListBlock nested:
                                    Close();
                                    entries.AddRange(nested.Entries.Select(entry => entry with { Level = Math.Min(level + 1 + entry.Level, 7) }));
                                    break;
                                default:
                                    after.Add(block);
                                    break;
                            }
                        }
                        break;
                    }
                }
            }
            Close();
        }

        /// OTSL: ched/fcel/ecel start a cell (its blocks follow, up to the next token), lcel/ucel/xcel continue a merged
        /// one (an empty cell here: the model has no spans), nl ends a row. The caption goes above the table, in italics.
        /// Only header cells (ched) in the first row make it a header row: most HWP tables have none.
        private void Table(XElement table, List<Block> output)
        {
            var rows = new List<IReadOnlyList<IReadOnlyList<Run>>>();
            var row = new List<IReadOnlyList<Run>>();
            List<Block>? cell = null;
            var caption = new List<Run>();
            var pictures = new List<Block>();
            var headerRow = false;
            void CloseCell()
            {
                if (cell == null) return;
                row.Add(Flatten(cell, pictures, Run.Break));
                cell = null;
            }
            void CloseRow()
            {
                CloseCell();
                if (row.Count > 0) rows.Add(row);
                row = [];
            }
            foreach (var node in table.Nodes())
            {
                if (node is XText text)
                {
                    if (cell != null && text.Value.Trim().Length > 0) cell.Add(new ParagraphBlock(TextRuns(text.Value, RunStyle.None, null).ToList()));
                    continue;
                }
                if (node is not XElement element) continue;
                switch (element.Name.LocalName)
                {
                    case "caption":
                        caption.AddRange(Inlines(element));
                        break;
                    case "ched":
                    case "fcel":
                    case "ecel":
                        CloseCell();
                        if (rows.Count == 0 && element.Name.LocalName == "ched") headerRow = true;
                        cell = [];
                        break;
                    case "lcel":
                    case "ucel":
                    case "xcel":
                        CloseCell();
                        row.Add([]);
                        break;
                    case "nl":
                        CloseRow();
                        break;
                    case "location":
                    case "custom":
                    case "hwp_prop":
                    case "thread":
                        break;
                    default:
                        if (cell == null) break;
                        if (InlineNames.Contains(element.Name.LocalName)) cell.Add(new ParagraphBlock(Styled(element)));
                        else Block(element, cell);
                        break;
                }
            }
            CloseRow();
            if (rows.Count > 0)
            {
                var title = Doc.Trim(caption);
                if (title.Count > 0) output.Add(new ParagraphBlock(title.Select(run => run.IsBreak ? run : run with { Style = run.Style | RunStyle.Italic }).ToList()));
                output.Add(new TableBlock(rows, headerRow));
            }
            output.AddRange(pictures);
        }

        /// A cell's (or a footnote's) blocks as one run list: paragraphs joined by separator, lists as "• item" lines,
        /// nested tables as lines of tab-separated cells. Pictures go to pictures.
        private static List<Run> Flatten(IEnumerable<Block> blocks, List<Block> pictures, Run separator)
        {
            var runs = new List<Run>();
            void Line(IReadOnlyList<Run> line)
            {
                var trimmed = Doc.Trim(line);
                if (trimmed.Count == 0) return;
                if (runs.Count > 0) runs.Add(separator);
                runs.AddRange(trimmed);
            }
            foreach (var block in blocks)
            {
                switch (block)
                {
                    case ParagraphBlock paragraph:
                        Line(paragraph.Runs);
                        break;
                    case HeadingBlock heading:
                        Line(heading.Runs);
                        break;
                    case QuoteBlock quote:
                        Line(quote.Runs);
                        break;
                    case CodeBlock code:
                        Line(TextRuns(code.Text, RunStyle.Code, null).ToList());
                        break;
                    case ListBlock list:
                    {
                        var counters = new int[8];
                        foreach (var entry in list.Entries)
                        {
                            var level = Math.Clamp(entry.Level, 0, 7);
                            counters[level]++;
                            for (var deeper = level + 1; deeper < counters.Length; deeper++) counters[deeper] = 0;
                            var marker = list.Ordered ? counters[level].ToString(CultureInfo.InvariantCulture) + ". " : "• ";
                            Line([new Run(marker), .. entry.Runs]);
                        }
                        break;
                    }
                    case TableBlock table:
                        foreach (var cells in table.Rows)
                        {
                            var line = new List<Run>();
                            for (var index = 0; index < cells.Count; index++)
                            {
                                if (index > 0) line.Add(new Run("\t"));
                                line.AddRange(cells[index].Select(run => run.IsBreak ? new Run(" ") : run));
                            }
                            Line(line);
                        }
                        break;
                    case ImageBlock image:
                        pictures.Add(image);
                        break;
                }
            }
            return Doc.Merge(runs);
        }

        /// The picture's bytes: a data: URI (what rhwp writes without --assets-dir), or a file inside the folder rhwp ran
        /// in. Nothing from the network, nothing outside that folder.
        private ImageBlock? Picture(XElement picture)
        {
            var uri = picture.Descendants().FirstOrDefault(child => child.Name.LocalName == "src")?.Attribute("uri")?.Value.Trim();
            if (string.IsNullOrEmpty(uri)) return null;
            var (data, ext) = Bytes(uri);
            return data is { Length: > 0 } ? Image(data, ext) : null;
        }

        private (byte[]? Data, string Ext) Bytes(string uri)
        {
            if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = uri.IndexOf(',');
                if (comma < 0) return (null, "");
                var header = uri[5..comma];
                if (!header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)) return (null, "");
                try
                {
                    return (Convert.FromBase64String(uri[(comma + 1)..]), MimeExt(header.Split(';')[0].Trim().ToLowerInvariant()));
                }
                catch (FormatException)
                {
                    return (null, "");
                }
            }
            if (baseDirectory == null || uri.Contains("://", StringComparison.Ordinal)) return (null, "");
            try
            {
                var root = Path.GetFullPath(baseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var path = Path.GetFullPath(Path.Combine(root, Uri.UnescapeDataString(uri).Replace('/', Path.DirectorySeparatorChar)));
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return (null, "");
                return (File.ReadAllBytes(path), FileNames.NormalizedExt(path));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return (null, "");
            }
        }

        private static string MimeExt(string mime) => mime switch
        {
            "image/png" => "png",
            "image/jpeg" or "image/jpg" => "jpg",
            "image/gif" => "gif",
            _ => "",
        };

        /// PNG, JPEG and GIF go in as they are; anything else (BMP, TIFF, WMF…) becomes PNG when ImageMagick reads it.
        private static ImageBlock? Image(byte[] data, string ext)
        {
            if (ext is "png" or "jpg" or "gif")
            {
                var (width, height) = Doc.MeasureImage(data);
                if (width > 0) return new ImageBlock(data, ext, "", width, height);
            }
            try
            {
                using var image = new MagickImage(data);
                return new ImageBlock(image.ToByteArray(MagickFormat.Png), "png", "", (int)image.Width, (int)image.Height);
            }
            catch (Exception error) when (error is MagickException or ArgumentException)
            {
                return null;
            }
        }
    }

    // Inline content

    private static void Paragraph(List<Block> output, IReadOnlyList<Run> runs)
    {
        var trimmed = Doc.Trim(runs);
        if (trimmed.Count > 0) output.Add(new ParagraphBlock(trimmed));
    }

    private static List<Run> Inlines(XElement element)
    {
        var runs = new List<Run>();
        foreach (var node in element.Nodes())
        {
            if (node is XText text) runs.AddRange(TextRuns(text.Value, RunStyle.None, null));
            else if (node is XElement child) Inline(child, RunStyle.None, null, runs);
        }
        return runs;
    }

    /// An inline element met where blocks belong (bold straight inside a list or a cell): its own style counts too.
    private static List<Run> Styled(XElement element)
    {
        var runs = new List<Run>();
        Inline(element, RunStyle.None, null, runs);
        return runs;
    }

    private static void Inline(XElement element, RunStyle style, string? link, List<Run> runs)
    {
        switch (element.Name.LocalName)
        {
            case "bold": style |= RunStyle.Bold; break;
            case "italic": style |= RunStyle.Italic; break;
            case "underline": style |= RunStyle.Underline; break;
            case "strikethrough": style |= RunStyle.Strike; break;
            case "href": link = SafeLink((string?)element.Attribute("uri")) ?? link; break;
            // Element heads carry positions and custom payloads, not text.
            case "location":
            case "custom":
            case "hwp_prop":
            case "thread":
            case "marker":
                return;
            // superscript, subscript and anything unknown: the text stays, the style can't.
        }
        foreach (var node in element.Nodes())
        {
            if (node is XText text) runs.AddRange(TextRuns(text.Value, style, link));
            else if (node is XElement child) Inline(child, style, link, runs);
        }
    }

    /// A newline inside text is a line break (DocLang has no <br/>).
    private static IEnumerable<Run> TextRuns(string text, RunStyle style, string? link)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (index > 0) yield return Run.Break;
            if (lines[index].Length > 0) yield return new Run(lines[index], style, link);
        }
    }

    /// Links stay links unless they would run script.
    private static string? SafeLink(string? uri)
    {
        var value = uri?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        foreach (var scheme in new[] { "javascript:", "vbscript:", "data:" })
        {
            if (value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return null;
        }
        return value;
    }
}
