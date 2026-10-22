using System.IO.Compression;
using System.Xml.Linq;

namespace Pickverter;

/// OpenDocument text (LibreOffice Writer) both ways.
public static class Odt
{
    private static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private static readonly XNamespace Text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private static readonly XNamespace Table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private static readonly XNamespace Style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    private static readonly XNamespace Fo = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";
    private static readonly XNamespace Draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    private static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";
    private static readonly XNamespace DC = "http://purl.org/dc/elements/1.1/";

    public static Doc Read(string path, string fallbackTitle)
    {
        using var zip = ZipFile.OpenRead(path);
        XDocument? Load(string name)
        {
            var entry = zip.GetEntry(name);
            if (entry == null) return null;
            using var stream = entry.Open();
            return XDocument.Load(stream);
        }
        var content = Load("content.xml") ?? throw new PickException(L("err.office_damaged"));
        var stylesDoc = Load("styles.xml");
        var doc = new Doc { Title = fallbackTitle };
        var title = Load("meta.xml")?.Descendants(DC + "title").FirstOrDefault()?.Value;
        if (!string.IsNullOrWhiteSpace(title)) doc.Title = title.Trim();

        var textStyles = new Dictionary<string, RunStyle>();
        var paragraphParents = new Dictionary<string, string>();
        var orderedLists = new HashSet<string>();
        foreach (var root in new[] { content.Root, stylesDoc?.Root })
        {
            if (root == null) continue;
            foreach (var style in root.Descendants(Style + "style"))
            {
                var name = (string?)style.Attribute(Style + "name");
                if (name == null) continue;
                var parent = (string?)style.Attribute(Style + "parent-style-name");
                if (parent != null) paragraphParents[name] = parent;
                var props = style.Element(Style + "text-properties");
                if (props == null) continue;
                var flags = RunStyle.None;
                if ((string?)props.Attribute(Fo + "font-weight") is "bold" or "600" or "700" or "800" or "900") flags |= RunStyle.Bold;
                if ((string?)props.Attribute(Fo + "font-style") == "italic") flags |= RunStyle.Italic;
                if ((string?)props.Attribute(Style + "text-underline-style") is { } underline && underline != "none") flags |= RunStyle.Underline;
                if ((string?)props.Attribute(Style + "text-line-through-style") is { } strike && strike != "none") flags |= RunStyle.Strike;
                var font = (string?)props.Attribute(Style + "font-name") ?? "";
                if (font.Contains("Mono", StringComparison.OrdinalIgnoreCase) || font.Contains("Courier", StringComparison.OrdinalIgnoreCase) ||
                    font.Contains("Consolas", StringComparison.OrdinalIgnoreCase)) flags |= RunStyle.Code;
                textStyles[name] = flags;
            }
            foreach (var list in root.Descendants(Text + "list-style"))
            {
                var name = (string?)list.Attribute(Style + "name");
                var first = list.Elements().FirstOrDefault();
                if (name != null && first?.Name == Text + "list-level-style-number") orderedLists.Add(name);
            }
        }

        /// The first well-known style name on the way up the parent chain (automatic styles like P1 point at it).
        string Root(string? style)
        {
            var current = style ?? "";
            for (var guard = 0; guard < 10; guard++)
            {
                if (current is "Title" or "Quotations" || current.StartsWith("Heading_20_", StringComparison.Ordinal) ||
                    current.StartsWith("Preformatted", StringComparison.Ordinal)) return current;
                if (!paragraphParents.TryGetValue(current, out var parent)) break;
                current = parent;
            }
            return current;
        }

        List<Run> Runs(XElement element, RunStyle style, string? link)
        {
            var runs = new List<Run>();
            foreach (var node in element.Nodes())
            {
                if (node is XText text)
                {
                    runs.Add(new Run(System.Text.RegularExpressions.Regex.Replace(text.Value, @"\s+", " "), style, link));
                    continue;
                }
                if (node is not XElement child) continue;
                var name = child.Name;
                if (name == Text + "span")
                {
                    var flags = textStyles.GetValueOrDefault((string?)child.Attribute(Text + "style-name") ?? "");
                    runs.AddRange(Runs(child, style | flags, link));
                }
                else if (name == Text + "a")
                {
                    runs.AddRange(Runs(child, style, (string?)child.Attribute(XLink + "href") ?? link));
                }
                else if (name == Text + "line-break")
                {
                    runs.Add(Run.Break);
                }
                else if (name == Text + "tab")
                {
                    runs.Add(new Run("\t", style, link));
                }
                else if (name == Text + "s")
                {
                    runs.Add(new Run(new string(' ', (int?)child.Attribute(Text + "c") ?? 1), style, link));
                }
                else if (name.Namespace == Text && name.LocalName is "note" or "bookmark" or "soft-page-break" or "sequence-decls")
                {
                }
                else if (name != Draw + "frame")
                {
                    runs.AddRange(Runs(child, style, link));
                }
            }
            return runs;
        }

        IEnumerable<ImageBlock> Images(XElement element)
        {
            foreach (var image in element.Descendants(Draw + "image"))
            {
                var href = (string?)image.Attribute(XLink + "href");
                if (href == null || href.Contains("://")) continue;
                var entry = zip.GetEntry(href.TrimStart('.', '/'));
                if (entry == null) continue;
                using var stream = entry.Open();
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                var ext = FileNames.NormalizedExt(href);
                if (ext is not ("png" or "jpg" or "gif" or "bmp" or "svg")) continue;
                var data = memory.ToArray();
                var (w, h) = Doc.MeasureImage(data);
                yield return new ImageBlock(data, ext, (string?)image.Parent?.Attribute(Draw + "name") ?? "", w, h);
            }
        }

        var codeLines = new List<string>();
        void FlushCode()
        {
            if (codeLines.Count == 0) return;
            doc.Blocks.Add(new CodeBlock(string.Join("\n", codeLines)));
            codeLines.Clear();
        }

        void Paragraph(XElement element)
        {
            var styleName = (string?)element.Attribute(Text + "style-name");
            var root = Root(styleName);
            var runs = Doc.Trim(Runs(element, textStyles.GetValueOrDefault(styleName ?? ""), null));
            if (root.StartsWith("Preformatted", StringComparison.Ordinal))
            {
                codeLines.Add(Doc.Plain(runs));
                return;
            }
            FlushCode();
            if (element.Name == Text + "h")
            {
                var level = (int?)element.Attribute(Text + "outline-level") ?? 1;
                if (runs.Count > 0) doc.Blocks.Add(new HeadingBlock(Math.Clamp(level, 1, 6), runs));
            }
            else if (root == "Title")
            {
                if (runs.Count > 0) doc.Blocks.Add(new HeadingBlock(1, runs));
            }
            else if (root.StartsWith("Heading_20_", StringComparison.Ordinal) && int.TryParse(root["Heading_20_".Length..], out var level))
            {
                if (runs.Count > 0) doc.Blocks.Add(new HeadingBlock(Math.Clamp(level, 1, 6), runs));
            }
            else if (root == "Quotations")
            {
                if (runs.Count > 0) doc.Blocks.Add(new QuoteBlock(runs));
            }
            else if (runs.Count > 0)
            {
                doc.Blocks.Add(new ParagraphBlock(runs));
            }
            doc.Blocks.AddRange(Images(element));
        }

        void List(XElement list, int level, List<ListEntry> entries)
        {
            foreach (var item in list.Elements(Text + "list-item").Concat(list.Elements(Text + "list-header")))
            {
                foreach (var child in item.Elements())
                {
                    if (child.Name == Text + "list") List(child, level + 1, entries);
                    else if (child.Name == Text + "p" || child.Name == Text + "h")
                    {
                        var runs = Doc.Trim(Runs(child, RunStyle.None, null));
                        if (runs.Count > 0) entries.Add(new ListEntry(runs, level));
                    }
                }
            }
        }

        void Body(XElement container)
        {
            foreach (var element in container.Elements())
            {
                if (element.Name == Text + "p" || element.Name == Text + "h")
                {
                    Paragraph(element);
                }
                else if (element.Name == Text + "list")
                {
                    FlushCode();
                    var entries = new List<ListEntry>();
                    List(element, 0, entries);
                    var ordered = orderedLists.Contains((string?)element.Attribute(Text + "style-name") ?? "");
                    if (entries.Count > 0) doc.Blocks.Add(new ListBlock(ordered, entries));
                }
                else if (element.Name == Table + "table")
                {
                    FlushCode();
                    var rows = new List<IReadOnlyList<IReadOnlyList<Run>>>();
                    foreach (var row in element.Descendants(Table + "table-row"))
                    {
                        var cells = new List<IReadOnlyList<Run>>();
                        foreach (var cell in row.Elements(Table + "table-cell"))
                        {
                            var runs = new List<Run>();
                            foreach (var paragraph in cell.Elements(Text + "p"))
                            {
                                if (runs.Count > 0) runs.Add(Run.Break);
                                runs.AddRange(Runs(paragraph, RunStyle.None, null));
                            }
                            cells.Add(Doc.Trim(runs));
                        }
                        if (cells.Count > 0) rows.Add(cells);
                    }
                    if (rows.Count > 0) doc.Blocks.Add(new TableBlock(rows));
                }
                else if (element.Name == Text + "section" || element.Name == Text + "index-body")
                {
                    Body(element);
                }
            }
        }

        var bodyText = content.Root?.Element(Office + "body")?.Element(Office + "text") ?? throw new PickException(L("err.office_damaged"));
        Body(bodyText);
        FlushCode();
        return doc;
    }

    public static void Write(Doc doc, string path)
    {
        var automatic = new StringBuilder();
        var usedStyles = new HashSet<RunStyle>();
        var body = new StringBuilder();
        var pictures = new List<(string Name, byte[] Data, string Mime)>();
        var tableIndex = 0;

        string TextStyle(RunStyle style)
        {
            if (style == RunStyle.None) return "";
            usedStyles.Add(style);
            return "T" + (int)style;
        }

        string Inline(IEnumerable<Run> runs)
        {
            var builder = new StringBuilder();
            foreach (var run in runs)
            {
                if (run.IsBreak)
                {
                    builder.Append("<text:line-break/>");
                    continue;
                }
                var escaped = Xml.Escape(run.Text).Replace("\t", "<text:tab/>");
                var name = TextStyle(run.Style);
                var span = name.Length > 0 ? $"<text:span text:style-name=\"{name}\">{escaped}</text:span>" : escaped;
                if (run.Link != null) span = $"<text:a xlink:type=\"simple\" xlink:href=\"{Xml.Escape(run.Link)}\">{span}</text:a>";
                builder.Append(span);
            }
            return builder.ToString();
        }

        foreach (var block in doc.Blocks)
        {
            switch (block)
            {
                case HeadingBlock heading:
                {
                    var level = Math.Clamp(heading.Level, 1, 6);
                    body.Append($"<text:h text:style-name=\"Heading_20_{level}\" text:outline-level=\"{level}\">").Append(Inline(heading.Runs)).Append("</text:h>");
                    break;
                }
                case ParagraphBlock paragraph:
                    body.Append("<text:p text:style-name=\"Text_20_body\">").Append(Inline(paragraph.Runs)).Append("</text:p>");
                    break;
                case QuoteBlock quote:
                    body.Append("<text:p text:style-name=\"Quotations\">").Append(Inline(quote.Runs)).Append("</text:p>");
                    break;
                case CodeBlock code:
                    foreach (var line in code.Text.Split('\n'))
                        body.Append("<text:p text:style-name=\"Preformatted_20_Text\">").Append(Xml.Escape(line.TrimEnd('\r')).Replace("  ", " <text:s/>")).Append("</text:p>");
                    break;
                case RuleBlock:
                    body.Append("<text:p text:style-name=\"Horizontal_20_Line\"/>");
                    break;
                case ListBlock list:
                {
                    var style = list.Ordered ? "L2" : "L1";
                    var depth = -1;
                    var open = new Stack<bool>();
                    foreach (var entry in list.Entries)
                    {
                        var level = Math.Clamp(entry.Level, 0, 9);
                        if (depth < 0)
                        {
                            body.Append($"<text:list text:style-name=\"{style}\">");
                            depth = 0;
                        }
                        while (depth < level)
                        {
                            body.Append("<text:list-item><text:list>");
                            depth++;
                        }
                        while (depth > level)
                        {
                            body.Append("</text:list></text:list-item>");
                            depth--;
                        }
                        body.Append("<text:list-item><text:p text:style-name=\"List_20_Contents\">").Append(Inline(entry.Runs)).Append("</text:p></text:list-item>");
                    }
                    while (depth > 0)
                    {
                        body.Append("</text:list></text:list-item>");
                        depth--;
                    }
                    if (depth == 0) body.Append("</text:list>");
                    break;
                }
                case TableBlock table:
                {
                    tableIndex++;
                    var columns = table.Rows.Max(row => row.Count);
                    body.Append($"<table:table table:name=\"Table{tableIndex}\" table:style-name=\"Table\"><table:table-column table:number-columns-repeated=\"{columns}\"/>");
                    for (var row = 0; row < table.Rows.Count; row++)
                    {
                        if (row == 0) body.Append("<table:table-header-rows>");
                        body.Append("<table:table-row>");
                        for (var column = 0; column < columns; column++)
                        {
                            var cell = column < table.Rows[row].Count ? table.Rows[row][column] : [];
                            var runs = row == 0 ? cell.Select(run => run with { Style = run.Style | RunStyle.Bold }) : cell;
                            body.Append("<table:table-cell table:style-name=\"Cell\" office:value-type=\"string\"><text:p text:style-name=\"Table_20_Contents\">")
                                .Append(Inline(runs)).Append("</text:p></table:table-cell>");
                        }
                        body.Append("</table:table-row>");
                        if (row == 0) body.Append("</table:table-header-rows>");
                    }
                    body.Append("</table:table>");
                    break;
                }
                case ImageBlock image:
                {
                    var (data, ext) = Docx.WordImage(image);
                    var name = $"Pictures/image{pictures.Count + 1}.{ext}";
                    pictures.Add((name, data, Doc.ImageMime(ext)));
                    var width = image.Width > 0 ? image.Width : 600;
                    var height = image.Height > 0 ? image.Height : 400;
                    var cmWidth = width / 96.0 * 2.54;
                    var cmHeight = height / 96.0 * 2.54;
                    if (cmWidth > 16)
                    {
                        cmHeight = cmHeight * 16 / cmWidth;
                        cmWidth = 16;
                    }
                    body.Append("<text:p text:style-name=\"Text_20_body\"><draw:frame draw:name=\"").Append(Xml.Escape(image.Alt.Length > 0 ? image.Alt : "Image" + pictures.Count))
                        .Append(string.Create(CultureInfo.InvariantCulture, $"\" text:anchor-type=\"as-char\" svg:width=\"{cmWidth:0.###}cm\" svg:height=\"{cmHeight:0.###}cm\">"))
                        .Append($"<draw:image xlink:href=\"{name}\" xlink:type=\"simple\" xlink:show=\"embed\" xlink:actuate=\"onLoad\"/></draw:frame></text:p>");
                    break;
                }
            }
        }

        foreach (var style in usedStyles)
        {
            automatic.Append($"<style:style style:name=\"T{(int)style}\" style:family=\"text\"><style:text-properties");
            if (style.HasFlag(RunStyle.Bold)) automatic.Append(" fo:font-weight=\"bold\" style:font-weight-asian=\"bold\"");
            if (style.HasFlag(RunStyle.Italic)) automatic.Append(" fo:font-style=\"italic\" style:font-style-asian=\"italic\"");
            if (style.HasFlag(RunStyle.Underline)) automatic.Append(" style:text-underline-style=\"solid\" style:text-underline-width=\"auto\" style:text-underline-color=\"font-color\"");
            if (style.HasFlag(RunStyle.Strike)) automatic.Append(" style:text-line-through-style=\"solid\"");
            if (style.HasFlag(RunStyle.Code)) automatic.Append(" style:font-name=\"Consolas\"");
            automatic.Append("/></style:style>");
        }
        automatic.Append("<style:style style:name=\"Table\" style:family=\"table\"><style:table-properties style:width=\"16cm\" table:align=\"left\"/></style:style>");
        automatic.Append("<style:style style:name=\"Cell\" style:family=\"table-cell\"><style:table-cell-properties fo:padding=\"0.1cm\" fo:border=\"0.5pt solid #bfbfbf\"/></style:style>");
        automatic.Append("<text:list-style style:name=\"L1\">");
        string[] bullets = ["•", "◦", "▪"];
        for (var level = 1; level <= 10; level++)
            automatic.Append($"<text:list-level-style-bullet text:level=\"{level}\" text:bullet-char=\"{bullets[(level - 1) % 3]}\"><style:list-level-properties text:list-level-position-and-space-mode=\"label-alignment\"><style:list-level-label-alignment text:label-followed-by=\"listtab\" fo:text-indent=\"-0.635cm\" fo:margin-left=\"{(level * 0.635 + 0.635).ToString("0.###", CultureInfo.InvariantCulture)}cm\"/></style:list-level-properties></text:list-level-style-bullet>");
        automatic.Append("</text:list-style><text:list-style style:name=\"L2\">");
        for (var level = 1; level <= 10; level++)
            automatic.Append($"<text:list-level-style-number text:level=\"{level}\" style:num-suffix=\".\" style:num-format=\"1\"><style:list-level-properties text:list-level-position-and-space-mode=\"label-alignment\"><style:list-level-label-alignment text:label-followed-by=\"listtab\" fo:text-indent=\"-0.635cm\" fo:margin-left=\"{(level * 0.635 + 0.635).ToString("0.###", CultureInfo.InvariantCulture)}cm\"/></style:list-level-properties></text:list-level-style-number>");
        automatic.Append("</text:list-style>");

        const string namespaces = "xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\" " +
            "xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" " +
            "xmlns:draw=\"urn:oasis:names:tc:opendocument:xmlns:drawing:1.0\" xmlns:fo=\"urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0\" " +
            "xmlns:xlink=\"http://www.w3.org/1999/xlink\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:meta=\"urn:oasis:names:tc:opendocument:xmlns:meta:1.0\" " +
            "xmlns:svg=\"urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0\" office:version=\"1.3\"";
        var contentXml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<office:document-content " + namespaces + ">" +
            "<office:font-face-decls><style:font-face style:name=\"Consolas\" svg:font-family=\"Consolas\" style:font-pitch=\"fixed\"/></office:font-face-decls>" +
            "<office:automatic-styles>" + automatic + "</office:automatic-styles><office:body><office:text>" + body + "</office:text></office:body></office:document-content>";

        string Heading(int level, string size) =>
            $"<style:style style:name=\"Heading_20_{level}\" style:display-name=\"Heading {level}\" style:family=\"paragraph\" style:parent-style-name=\"Heading\" style:next-style-name=\"Text_20_body\" style:default-outline-level=\"{level}\" style:class=\"text\">" +
            $"<style:text-properties fo:font-size=\"{size}\" fo:font-weight=\"bold\" style:font-size-asian=\"{size}\" style:font-weight-asian=\"bold\"/></style:style>";
        var stylesXml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<office:document-styles " + namespaces + ">" +
            "<office:font-face-decls><style:font-face style:name=\"Consolas\" svg:font-family=\"Consolas\" style:font-pitch=\"fixed\"/></office:font-face-decls><office:styles>" +
            "<style:default-style style:family=\"paragraph\"><style:text-properties fo:font-size=\"11pt\" style:font-size-asian=\"11pt\"/></style:default-style>" +
            "<style:style style:name=\"Standard\" style:family=\"paragraph\" style:class=\"text\"/>" +
            "<style:style style:name=\"Text_20_body\" style:display-name=\"Text body\" style:family=\"paragraph\" style:parent-style-name=\"Standard\" style:class=\"text\"><style:paragraph-properties fo:margin-top=\"0cm\" fo:margin-bottom=\"0.25cm\" fo:line-height=\"115%\"/></style:style>" +
            "<style:style style:name=\"Heading\" style:family=\"paragraph\" style:parent-style-name=\"Standard\" style:next-style-name=\"Text_20_body\" style:class=\"text\"><style:paragraph-properties fo:margin-top=\"0.42cm\" fo:margin-bottom=\"0.21cm\" fo:keep-with-next=\"always\"/></style:style>" +
            Heading(1, "18pt") + Heading(2, "15pt") + Heading(3, "13pt") + Heading(4, "12pt") + Heading(5, "11pt") + Heading(6, "11pt") +
            "<style:style style:name=\"Quotations\" style:family=\"paragraph\" style:parent-style-name=\"Standard\" style:class=\"html\"><style:paragraph-properties fo:margin-left=\"1cm\" fo:margin-bottom=\"0.25cm\"/><style:text-properties fo:font-style=\"italic\" fo:color=\"#595959\"/></style:style>" +
            "<style:style style:name=\"Preformatted_20_Text\" style:display-name=\"Preformatted Text\" style:family=\"paragraph\" style:parent-style-name=\"Standard\" style:class=\"html\"><style:paragraph-properties fo:margin-top=\"0cm\" fo:margin-bottom=\"0cm\" fo:background-color=\"#f3f3f5\"/><style:text-properties style:font-name=\"Consolas\" fo:font-size=\"9.5pt\"/></style:style>" +
            "<style:style style:name=\"List_20_Contents\" style:display-name=\"List Contents\" style:family=\"paragraph\" style:parent-style-name=\"Standard\" style:class=\"list\"><style:paragraph-properties fo:margin-bottom=\"0.1cm\"/></style:style>" +
            "<style:style style:name=\"Table_20_Contents\" style:display-name=\"Table Contents\" style:family=\"paragraph\" style:parent-style-name=\"Standard\" style:class=\"extra\"/>" +
            "<style:style style:name=\"Horizontal_20_Line\" style:display-name=\"Horizontal Line\" style:family=\"paragraph\" style:parent-style-name=\"Standard\" style:class=\"html\"><style:paragraph-properties fo:margin-bottom=\"0.25cm\" fo:border-bottom=\"0.5pt solid #a0a0a0\" fo:padding=\"0cm\"/></style:style>" +
            "</office:styles></office:document-styles>";
        var metaXml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<office:document-meta " + namespaces + "><office:meta><meta:generator>Pickverter</meta:generator>" +
            $"<dc:title>{Xml.Escape(doc.Title)}</dc:title></office:meta></office:document-meta>";
        var manifest = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<manifest:manifest xmlns:manifest=\"urn:oasis:names:tc:opendocument:xmlns:manifest:1.0\" manifest:version=\"1.3\">" +
            "<manifest:file-entry manifest:full-path=\"/\" manifest:version=\"1.3\" manifest:media-type=\"application/vnd.oasis.opendocument.text\"/>" +
            "<manifest:file-entry manifest:full-path=\"content.xml\" manifest:media-type=\"text/xml\"/><manifest:file-entry manifest:full-path=\"styles.xml\" manifest:media-type=\"text/xml\"/>" +
            "<manifest:file-entry manifest:full-path=\"meta.xml\" manifest:media-type=\"text/xml\"/>");
        foreach (var picture in pictures) manifest.Append($"<manifest:file-entry manifest:full-path=\"{picture.Name}\" manifest:media-type=\"{picture.Mime}\"/>");
        manifest.Append("</manifest:manifest>");

        using var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        void Add(string name, string text, CompressionLevel level = CompressionLevel.Optimal)
        {
            var entry = zip.CreateEntry(name, level);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(text);
        }
        Add("mimetype", "application/vnd.oasis.opendocument.text", CompressionLevel.NoCompression);
        Add("content.xml", contentXml);
        Add("styles.xml", stylesXml);
        Add("meta.xml", metaXml);
        Add("META-INF/manifest.xml", manifest.ToString());
        foreach (var picture in pictures)
        {
            var entry = zip.CreateEntry(picture.Name, CompressionLevel.NoCompression);
            using var output = entry.Open();
            output.Write(picture.Data);
        }
    }
}
