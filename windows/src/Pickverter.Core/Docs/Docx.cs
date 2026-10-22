using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Pickverter;

/// Word documents: a reader for the parts that matter and a writer that produces clean, standard DOCX.
public static partial class Docx
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace V = "urn:schemas-microsoft-com:vml";
    private static readonly XNamespace DC = "http://purl.org/dc/elements/1.1/";
    private static readonly HashSet<string> MonoFonts = new(StringComparer.OrdinalIgnoreCase)
    {
        "Consolas", "Courier New", "Courier", "Cascadia Mono", "Cascadia Code", "Lucida Console", "Menlo", "Monaco", "Source Code Pro",
    };

    [GeneratedRegex(@"HYPERLINK\s+""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex HyperlinkField();

    private sealed record StyleInfo(string Name, int? Outline, string? BasedOn, bool Numbered);

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
        var document = Load("word/document.xml") ?? throw new PickException(L("err.office_damaged"));
        var rels = new Dictionary<string, string>();
        foreach (var relation in Load("word/_rels/document.xml.rels")?.Root?.Elements(Rel + "Relationship") ?? [])
        {
            var id = (string?)relation.Attribute("Id");
            var target = (string?)relation.Attribute("Target");
            if (id != null && target != null) rels[id] = target;
        }
        var styles = new Dictionary<string, StyleInfo>();
        foreach (var style in Load("word/styles.xml")?.Root?.Elements(W + "style") ?? [])
        {
            var id = (string?)style.Attribute(W + "styleId");
            if (id == null) continue;
            var name = (string?)style.Element(W + "name")?.Attribute(W + "val") ?? id;
            var outline = (int?)style.Element(W + "pPr")?.Element(W + "outlineLvl")?.Attribute(W + "val");
            var basedOn = (string?)style.Element(W + "basedOn")?.Attribute(W + "val");
            var numbered = style.Element(W + "pPr")?.Element(W + "numPr") != null;
            styles[id] = new StyleInfo(name, outline, basedOn, numbered);
        }
        var numToAbstract = new Dictionary<string, string>();
        var formats = new Dictionary<(string Abstract, int Level), string>();
        var numbering = Load("word/numbering.xml")?.Root;
        if (numbering != null)
        {
            foreach (var abstractNum in numbering.Elements(W + "abstractNum"))
            {
                var id = (string?)abstractNum.Attribute(W + "abstractNumId") ?? "";
                foreach (var level in abstractNum.Elements(W + "lvl"))
                {
                    var ilvl = (int?)level.Attribute(W + "ilvl") ?? 0;
                    formats[(id, ilvl)] = (string?)level.Element(W + "numFmt")?.Attribute(W + "val") ?? "decimal";
                }
            }
            foreach (var num in numbering.Elements(W + "num"))
            {
                var id = (string?)num.Attribute(W + "numId");
                var abstractId = (string?)num.Element(W + "abstractNumId")?.Attribute(W + "val");
                if (id != null && abstractId != null) numToAbstract[id] = abstractId;
            }
        }
        var doc = new Doc { Title = fallbackTitle };
        var coreTitle = Load("docProps/core.xml")?.Root?.Element(DC + "title")?.Value;
        if (!string.IsNullOrWhiteSpace(coreTitle)) doc.Title = coreTitle.Trim();

        StyleInfo? Style(string? id)
        {
            var guard = 0;
            while (id != null && guard++ < 10)
            {
                if (!styles.TryGetValue(id, out var info)) return null;
                return info;
            }
            return null;
        }

        int? HeadingLevel(string? styleId, XElement? pPr)
        {
            var outline = (int?)pPr?.Element(W + "outlineLvl")?.Attribute(W + "val");
            var current = styleId;
            for (var guard = 0; current != null && guard < 10; guard++)
            {
                if (!styles.TryGetValue(current, out var info)) break;
                var name = info.Name.ToLowerInvariant();
                if (name == "title") return 1;
                if (name == "subtitle") return 2;
                if (name.StartsWith("heading ", StringComparison.Ordinal) && int.TryParse(name[8..], out var level)) return Math.Clamp(level, 1, 6);
                outline ??= info.Outline;
                current = info.BasedOn;
            }
            return outline is >= 0 and < 9 ? Math.Min(outline.Value + 1, 6) : null;
        }

        bool IsQuote(string? styleId) => Style(styleId)?.Name.Contains("quote", StringComparison.OrdinalIgnoreCase) == true;

        ImageBlock? Image(string? id, string alt)
        {
            if (id == null || !rels.TryGetValue(id, out var target) || target.Contains("://")) return null;
            var name = target.StartsWith('/') ? target[1..] : "word/" + target;
            var entry = zip.GetEntry(name.Replace("\\", "/"));
            if (entry == null) return null;
            using var stream = entry.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var data = memory.ToArray();
            var ext = FileNames.NormalizedExt(target);
            if (ext is not ("png" or "jpg" or "gif" or "bmp")) return null;
            var (w, h) = Doc.MeasureImage(data);
            return new ImageBlock(data, ext, alt, w, h);
        }

        (List<Run> Runs, List<ImageBlock> Images) ReadRuns(XElement paragraph)
        {
            var runs = new List<Run>();
            var images = new List<ImageBlock>();
            string? fieldLink = null;
            var instruction = new StringBuilder();
            var inInstruction = false;

            void ReadRun(XElement run, string? link)
            {
                var properties = run.Element(W + "rPr");
                bool On(string name)
                {
                    var element = properties?.Element(W + name);
                    if (element == null) return false;
                    var value = (string?)element.Attribute(W + "val");
                    return value is null or not ("0" or "false" or "off" or "none");
                }
                var style = RunStyle.None;
                if (On("b")) style |= RunStyle.Bold;
                if (On("i")) style |= RunStyle.Italic;
                if (On("u")) style |= RunStyle.Underline;
                if (On("strike") || On("dstrike")) style |= RunStyle.Strike;
                var font = (string?)properties?.Element(W + "rFonts")?.Attribute(W + "ascii");
                if (font != null && MonoFonts.Contains(font)) style |= RunStyle.Code;
                var effectiveLink = link ?? fieldLink;
                foreach (var child in run.Elements())
                {
                    var name = child.Name.LocalName;
                    switch (name)
                    {
                        case "t":
                            if (inInstruction) break;
                            runs.Add(new Run(child.Value, style, effectiveLink));
                            break;
                        case "tab":
                            runs.Add(new Run("\t", style, effectiveLink));
                            break;
                        case "br" or "cr":
                            if ((string?)child.Attribute(W + "type") != "page") runs.Add(Run.Break);
                            break;
                        case "noBreakHyphen":
                            runs.Add(new Run("-", style, effectiveLink));
                            break;
                        case "fldChar":
                        {
                            var type = (string?)child.Attribute(W + "fldCharType");
                            if (type == "begin")
                            {
                                inInstruction = true;
                                instruction.Clear();
                            }
                            else if (type == "separate")
                            {
                                inInstruction = false;
                                var match = HyperlinkField().Match(instruction.ToString());
                                fieldLink = match.Success ? match.Groups[1].Value : null;
                            }
                            else if (type == "end")
                            {
                                inInstruction = false;
                                fieldLink = null;
                            }
                            break;
                        }
                        case "instrText":
                            instruction.Append(child.Value);
                            break;
                        case "drawing":
                        {
                            var alt = (string?)child.Descendants().FirstOrDefault(element => element.Name.LocalName == "docPr")?.Attribute("descr") ?? "";
                            foreach (var blip in child.Descendants(A + "blip"))
                            {
                                if (Image((string?)blip.Attribute(R + "embed"), alt) is { } image) images.Add(image);
                            }
                            break;
                        }
                        case "pict" or "object":
                            foreach (var data in child.Descendants(V + "imagedata"))
                            {
                                if (Image((string?)data.Attribute(R + "id"), "") is { } image) images.Add(image);
                            }
                            break;
                    }
                }
            }

            void Walk(XElement parent, string? link)
            {
                foreach (var child in parent.Elements())
                {
                    switch (child.Name.LocalName)
                    {
                        case "r":
                            ReadRun(child, link);
                            break;
                        case "hyperlink":
                        {
                            var id = (string?)child.Attribute(R + "id");
                            var target = id != null && rels.TryGetValue(id, out var value) ? value : null;
                            Walk(child, target ?? link);
                            break;
                        }
                        case "fldSimple":
                        {
                            var match = HyperlinkField().Match((string?)child.Attribute(W + "instr") ?? "");
                            Walk(child, match.Success ? match.Groups[1].Value : link);
                            break;
                        }
                        case "smartTag" or "ins" or "customXml" or "sdt" or "sdtContent" or "bdo" or "dir":
                            Walk(child, link);
                            break;
                    }
                }
            }

            Walk(paragraph, null);
            return (runs, images);
        }

        var listEntries = new List<ListEntry>();
        var listOrdered = false;
        void FlushList()
        {
            if (listEntries.Count == 0) return;
            doc.Blocks.Add(new ListBlock(listOrdered, listEntries.ToList()));
            listEntries.Clear();
        }

        var codeLines = new List<string>();
        void FlushCode()
        {
            if (codeLines.Count == 0) return;
            doc.Blocks.Add(new CodeBlock(string.Join("\n", codeLines)));
            codeLines.Clear();
        }

        void Paragraph(XElement paragraph)
        {
            var pPr = paragraph.Element(W + "pPr");
            var styleId = (string?)pPr?.Element(W + "pStyle")?.Attribute(W + "val");
            var (runs, images) = ReadRuns(paragraph);
            var trimmed = Doc.Trim(runs);
            var numPr = pPr?.Element(W + "numPr");
            var heading = HeadingLevel(styleId, pPr);
            var allCode = trimmed.Count > 0 && trimmed.All(run => run.IsBreak || run.Style.HasFlag(RunStyle.Code));
            if (allCode && heading == null && numPr == null)
            {
                FlushList();
                codeLines.Add(Doc.Plain(runs));
                return;
            }
            FlushCode();
            if (numPr != null && heading == null && trimmed.Count > 0)
            {
                var numId = (string?)numPr.Element(W + "numId")?.Attribute(W + "val") ?? "";
                var level = (int?)numPr.Element(W + "ilvl")?.Attribute(W + "val") ?? 0;
                if (numId != "0")
                {
                    var format = numToAbstract.TryGetValue(numId, out var abstractId) && formats.TryGetValue((abstractId, level), out var value) ? value : "bullet";
                    var ordered = format != "bullet" && format != "none";
                    if (listEntries.Count == 0) listOrdered = ordered;
                    listEntries.Add(new ListEntry(trimmed, level));
                    foreach (var image in images) doc.Blocks.Add(image);
                    return;
                }
            }
            FlushList();
            if (trimmed.Count > 0)
            {
                if (heading is { } level) doc.Blocks.Add(new HeadingBlock(level, trimmed));
                else if (IsQuote(styleId)) doc.Blocks.Add(new QuoteBlock(trimmed));
                else doc.Blocks.Add(new ParagraphBlock(trimmed));
            }
            foreach (var image in images) doc.Blocks.Add(image);
        }

        void Table(XElement table)
        {
            var rows = new List<IReadOnlyList<IReadOnlyList<Run>>>();
            foreach (var row in table.Elements(W + "tr"))
            {
                var cells = new List<IReadOnlyList<Run>>();
                foreach (var cell in row.Elements(W + "tc"))
                {
                    var runs = new List<Run>();
                    foreach (var paragraph in cell.Descendants(W + "p"))
                    {
                        if (runs.Count > 0) runs.Add(Run.Break);
                        runs.AddRange(ReadRuns(paragraph).Runs);
                    }
                    cells.Add(Doc.Trim(runs));
                }
                if (cells.Count > 0) rows.Add(cells);
            }
            if (rows.Count > 0) doc.Blocks.Add(new TableBlock(rows));
        }

        void Body(XElement container)
        {
            foreach (var element in container.Elements())
            {
                switch (element.Name.LocalName)
                {
                    case "p":
                        Paragraph(element);
                        break;
                    case "tbl":
                        FlushList();
                        FlushCode();
                        Table(element);
                        break;
                    case "sdt":
                        if (element.Element(W + "sdtContent") is { } content) Body(content);
                        break;
                    case "customXml":
                        Body(element);
                        break;
                }
            }
        }

        var body = document.Root?.Element(W + "body") ?? throw new PickException(L("err.office_damaged"));
        Body(body);
        FlushList();
        FlushCode();
        return doc;
    }

    // Writing

    private const string MainNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    public static void Write(Doc doc, string path)
    {
        var relations = new List<string>();
        var media = new List<(string Name, byte[] Data)>();
        var orderedLists = 0;
        var body = new StringBuilder();
        var pictureId = 0;
        var metric = RegionInfo.CurrentRegion.IsMetric;
        var pageWidth = metric ? 11906 : 12240;
        var textWidth = pageWidth - 2 * 1440;

        string Rel(string type, string target, bool external = false)
        {
            var id = "rId" + (relations.Count + 10);
            relations.Add($"<Relationship Id=\"{id}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/{type}\" Target=\"{Xml.Escape(target)}\"{(external ? " TargetMode=\"External\"" : "")}/>");
            return id;
        }

        string RunXml(Run run, bool forceBold = false)
        {
            if (run.IsBreak) return "<w:r><w:br/></w:r>";
            var properties = new StringBuilder();
            if (run.Link != null) properties.Append("<w:rStyle w:val=\"Hyperlink\"/>");
            if (run.Style.HasFlag(RunStyle.Code)) properties.Append("<w:rFonts w:ascii=\"Consolas\" w:hAnsi=\"Consolas\" w:cs=\"Consolas\"/>");
            if (forceBold || run.Style.HasFlag(RunStyle.Bold)) properties.Append("<w:b/><w:bCs/>");
            if (run.Style.HasFlag(RunStyle.Italic)) properties.Append("<w:i/><w:iCs/>");
            if (run.Style.HasFlag(RunStyle.Strike)) properties.Append("<w:strike/>");
            if (run.Style.HasFlag(RunStyle.Underline) && run.Link == null) properties.Append("<w:u w:val=\"single\"/>");
            var rPr = properties.Length > 0 ? "<w:rPr>" + properties + "</w:rPr>" : "";
            var content = new StringBuilder();
            var pieces = run.Text.Split('\t');
            for (var index = 0; index < pieces.Length; index++)
            {
                if (index > 0) content.Append("<w:tab/>");
                if (pieces[index].Length > 0) content.Append("<w:t xml:space=\"preserve\">").Append(Xml.Escape(pieces[index])).Append("</w:t>");
            }
            return "<w:r>" + rPr + content + "</w:r>";
        }

        string Runs(IEnumerable<Run> runs, bool forceBold = false)
        {
            var builder = new StringBuilder();
            foreach (var run in runs)
            {
                if (run.Link != null && !run.IsBreak)
                {
                    var id = Rel("hyperlink", run.Link, external: true);
                    builder.Append("<w:hyperlink r:id=\"").Append(id).Append("\" w:history=\"1\">").Append(RunXml(run, forceBold)).Append("</w:hyperlink>");
                }
                else
                {
                    builder.Append(RunXml(run, forceBold));
                }
            }
            return builder.ToString();
        }

        void Paragraph(string? style, string content, string extra = "")
        {
            body.Append("<w:p>");
            if (style != null || extra.Length > 0)
            {
                body.Append("<w:pPr>");
                if (style != null) body.Append("<w:pStyle w:val=\"").Append(style).Append("\"/>");
                body.Append(extra).Append("</w:pPr>");
            }
            body.Append(content).Append("</w:p>");
        }

        foreach (var block in doc.Blocks)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    Paragraph("Heading" + Math.Clamp(heading.Level, 1, 6), Runs(heading.Runs));
                    break;
                case ParagraphBlock paragraph:
                    Paragraph(null, Runs(paragraph.Runs));
                    break;
                case QuoteBlock quote:
                    Paragraph("Quote", Runs(quote.Runs));
                    break;
                case CodeBlock code:
                    foreach (var line in code.Text.Split('\n')) Paragraph("Code", RunXml(new Run(line.TrimEnd('\r'))));
                    break;
                case RuleBlock:
                    Paragraph(null, "", "<w:pBdr><w:bottom w:val=\"single\" w:sz=\"6\" w:space=\"1\" w:color=\"A0A0A0\"/></w:pBdr>");
                    break;
                case ListBlock list:
                {
                    var numId = 1;
                    if (list.Ordered)
                    {
                        orderedLists++;
                        numId = 1 + orderedLists;
                    }
                    foreach (var entry in list.Entries)
                    {
                        Paragraph("ListParagraph", Runs(entry.Runs),
                            $"<w:numPr><w:ilvl w:val=\"{Math.Clamp(entry.Level, 0, 8)}\"/><w:numId w:val=\"{numId}\"/></w:numPr>");
                    }
                    break;
                }
                case ImageBlock image:
                {
                    var (data, ext) = WordImage(image);
                    pictureId++;
                    var name = $"image{pictureId}.{ext}";
                    media.Add((name, data));
                    var id = Rel("image", "media/" + name);
                    var width = image.Width > 0 ? image.Width : 600;
                    var height = image.Height > 0 ? image.Height : 400;
                    long cx = width * 9525L, cy = height * 9525L;
                    long maxCx = textWidth * 635L;
                    if (cx > maxCx)
                    {
                        cy = cy * maxCx / cx;
                        cx = maxCx;
                    }
                    var alt = Xml.Escape(image.Alt);
                    Paragraph(null,
                        "<w:r><w:drawing><wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\">" +
                        $"<wp:extent cx=\"{cx}\" cy=\"{cy}\"/><wp:docPr id=\"{pictureId}\" name=\"Picture {pictureId}\" descr=\"{alt}\"/>" +
                        "<wp:cNvGraphicFramePr><a:graphicFrameLocks noChangeAspect=\"1\"/></wp:cNvGraphicFramePr>" +
                        "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:pic>" +
                        $"<pic:nvPicPr><pic:cNvPr id=\"{pictureId}\" name=\"{name}\" descr=\"{alt}\"/><pic:cNvPicPr/></pic:nvPicPr>" +
                        $"<pic:blipFill><a:blip r:embed=\"{id}\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>" +
                        $"<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr>" +
                        "</pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r>");
                    break;
                }
                case TableBlock table:
                {
                    var columns = table.Rows.Max(row => row.Count);
                    if (columns == 0) break;
                    var cellWidth = textWidth / columns;
                    body.Append("<w:tbl><w:tblPr><w:tblStyle w:val=\"TableGrid\"/><w:tblW w:w=\"0\" w:type=\"auto\"/><w:tblLook w:val=\"04A0\" w:firstRow=\"1\" w:lastRow=\"0\" w:firstColumn=\"0\" w:lastColumn=\"0\" w:noHBand=\"0\" w:noVBand=\"1\"/></w:tblPr><w:tblGrid>");
                    for (var column = 0; column < columns; column++) body.Append($"<w:gridCol w:w=\"{cellWidth}\"/>");
                    body.Append("</w:tblGrid>");
                    for (var row = 0; row < table.Rows.Count; row++)
                    {
                        body.Append(row == 0 ? "<w:tr><w:trPr><w:tblHeader/></w:trPr>" : "<w:tr>");
                        for (var column = 0; column < columns; column++)
                        {
                            var cell = column < table.Rows[row].Count ? table.Rows[row][column] : [];
                            body.Append($"<w:tc><w:tcPr><w:tcW w:w=\"{cellWidth}\" w:type=\"dxa\"/></w:tcPr><w:p><w:pPr><w:spacing w:after=\"0\"/></w:pPr>")
                                .Append(Runs(cell, forceBold: row == 0)).Append("</w:p></w:tc>");
                        }
                        body.Append("</w:tr>");
                    }
                    body.Append("</w:tbl>");
                    Paragraph(null, "");
                    break;
                }
            }
        }
        var pageHeight = metric ? 16838 : 15840;
        body.Append($"<w:sectPr><w:pgSz w:w=\"{pageWidth}\" w:h=\"{pageHeight}\"/><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\" w:header=\"708\" w:footer=\"708\" w:gutter=\"0\"/></w:sectPr>");

        var documentXml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
            $"<w:document xmlns:w=\"{MainNs}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
            "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
            "xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><w:body>" + body + "</w:body></w:document>";

        var numbering = new StringBuilder();
        numbering.Append($"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<w:numbering xmlns:w=\"{MainNs}\">");
        string[] bullets = ["•", "◦", "▪"];
        numbering.Append("<w:abstractNum w:abstractNumId=\"0\"><w:multiLevelType w:val=\"hybridMultilevel\"/>");
        for (var level = 0; level < 9; level++)
            numbering.Append($"<w:lvl w:ilvl=\"{level}\"><w:start w:val=\"1\"/><w:numFmt w:val=\"bullet\"/><w:lvlText w:val=\"{bullets[level % 3]}\"/><w:lvlJc w:val=\"left\"/><w:pPr><w:ind w:left=\"{720 + level * 360}\" w:hanging=\"360\"/></w:pPr></w:lvl>");
        numbering.Append("</w:abstractNum><w:abstractNum w:abstractNumId=\"1\"><w:multiLevelType w:val=\"hybridMultilevel\"/>");
        string[] numberFormats = ["decimal", "lowerLetter", "lowerRoman"];
        for (var level = 0; level < 9; level++)
            numbering.Append($"<w:lvl w:ilvl=\"{level}\"><w:start w:val=\"1\"/><w:numFmt w:val=\"{numberFormats[level % 3]}\"/><w:lvlText w:val=\"%{level + 1}.\"/><w:lvlJc w:val=\"left\"/><w:pPr><w:ind w:left=\"{720 + level * 360}\" w:hanging=\"360\"/></w:pPr></w:lvl>");
        numbering.Append("</w:abstractNum><w:num w:numId=\"1\"><w:abstractNumId w:val=\"0\"/></w:num>");
        for (var list = 1; list <= orderedLists; list++)
            numbering.Append($"<w:num w:numId=\"{list + 1}\"><w:abstractNumId w:val=\"1\"/><w:lvlOverride w:ilvl=\"0\"><w:startOverride w:val=\"1\"/></w:lvlOverride></w:num>");
        numbering.Append("</w:numbering>");

        var eastAsia = Language.EastAsianFont(Language.Detect(doc.PlainText()));
        var styles = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<w:styles xmlns:w=\"{MainNs}\">" +
            "<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\" w:cs=\"Calibri\"" +
            (eastAsia != null ? $" w:eastAsia=\"{eastAsia}\"" : "") + "/><w:sz w:val=\"22\"/><w:szCs w:val=\"22\"/></w:rPr></w:rPrDefault>" +
            "<w:pPrDefault><w:pPr><w:spacing w:after=\"160\" w:line=\"264\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>" +
            "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:qFormat/></w:style>" +
            HeadingStyle(1, 36) + HeadingStyle(2, 30) + HeadingStyle(3, 26) + HeadingStyle(4, 24) + HeadingStyle(5, 22) + HeadingStyle(6, 22) +
            "<w:style w:type=\"paragraph\" w:styleId=\"Title\"><w:name w:val=\"Title\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:qFormat/><w:pPr><w:spacing w:after=\"240\"/></w:pPr><w:rPr><w:sz w:val=\"52\"/><w:szCs w:val=\"52\"/></w:rPr></w:style>" +
            "<w:style w:type=\"paragraph\" w:styleId=\"Quote\"><w:name w:val=\"Quote\"/><w:basedOn w:val=\"Normal\"/><w:qFormat/><w:pPr><w:ind w:left=\"567\"/><w:pBdr><w:left w:val=\"single\" w:sz=\"18\" w:space=\"8\" w:color=\"C8C8C8\"/></w:pBdr></w:pPr><w:rPr><w:i/><w:color w:val=\"595959\"/></w:rPr></w:style>" +
            "<w:style w:type=\"paragraph\" w:styleId=\"Code\"><w:name w:val=\"Code\"/><w:basedOn w:val=\"Normal\"/><w:qFormat/><w:pPr><w:spacing w:after=\"0\" w:line=\"240\" w:lineRule=\"auto\"/><w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"F3F3F5\"/></w:pPr><w:rPr><w:rFonts w:ascii=\"Consolas\" w:hAnsi=\"Consolas\" w:cs=\"Consolas\"/><w:sz w:val=\"19\"/><w:szCs w:val=\"19\"/></w:rPr></w:style>" +
            "<w:style w:type=\"paragraph\" w:styleId=\"ListParagraph\"><w:name w:val=\"List Paragraph\"/><w:basedOn w:val=\"Normal\"/><w:qFormat/><w:pPr><w:spacing w:after=\"60\"/><w:ind w:left=\"720\"/><w:contextualSpacing/></w:pPr></w:style>" +
            "<w:style w:type=\"character\" w:styleId=\"Hyperlink\"><w:name w:val=\"Hyperlink\"/><w:uiPriority w:val=\"99\"/><w:unhideWhenUsed/><w:rPr><w:color w:val=\"0563C1\"/><w:u w:val=\"single\"/></w:rPr></w:style>" +
            "<w:style w:type=\"table\" w:styleId=\"TableGrid\"><w:name w:val=\"Table Grid\"/><w:pPr><w:spacing w:after=\"0\" w:line=\"240\" w:lineRule=\"auto\"/></w:pPr><w:tblPr><w:tblBorders>" +
            "<w:top w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"BFBFBF\"/><w:left w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"BFBFBF\"/>" +
            "<w:bottom w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"BFBFBF\"/><w:right w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"BFBFBF\"/>" +
            "<w:insideH w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"BFBFBF\"/><w:insideV w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"BFBFBF\"/>" +
            "</w:tblBorders><w:tblCellMar><w:top w:w=\"60\" w:type=\"dxa\"/><w:left w:w=\"108\" w:type=\"dxa\"/><w:bottom w:w=\"60\" w:type=\"dxa\"/><w:right w:w=\"108\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr></w:style>" +
            "</w:styles>";

        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var core = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
            "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" " +
            "xmlns:dcterms=\"http://purl.org/dc/terms/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" +
            $"<dc:title>{Xml.Escape(doc.Title)}</dc:title><dc:creator>Pickverter</dc:creator>" +
            $"<dcterms:created xsi:type=\"dcterms:W3CDTF\">{now}</dcterms:created><dcterms:modified xsi:type=\"dcterms:W3CDTF\">{now}</dcterms:modified></cp:coreProperties>";
        var app = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\"><Application>Pickverter</Application></Properties>";
        var types = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Default Extension=\"png\" ContentType=\"image/png\"/><Default Extension=\"jpg\" ContentType=\"image/jpeg\"/><Default Extension=\"jpeg\" ContentType=\"image/jpeg\"/>" +
            "<Default Extension=\"gif\" ContentType=\"image/gif\"/><Default Extension=\"bmp\" ContentType=\"image/bmp\"/>" +
            "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
            "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>" +
            "<Override PartName=\"/word/numbering.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml\"/>" +
            "<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>" +
            "<Override PartName=\"/docProps/app.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.extended-properties+xml\"/></Types>";
        var rootRels = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>" +
            "<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties\" Target=\"docProps/app.xml\"/></Relationships>";
        var documentRels = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering\" Target=\"numbering.xml\"/>" +
            string.Concat(relations) + "</Relationships>";

        using var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        void Add(string name, string content)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
        Add("[Content_Types].xml", types);
        Add("_rels/.rels", rootRels);
        Add("word/document.xml", documentXml);
        Add("word/styles.xml", styles);
        Add("word/numbering.xml", numbering.ToString());
        Add("word/_rels/document.xml.rels", documentRels);
        Add("docProps/core.xml", core);
        Add("docProps/app.xml", app);
        foreach (var (name, data) in media)
        {
            var entry = zip.CreateEntry("word/media/" + name, CompressionLevel.NoCompression);
            using var output = entry.Open();
            output.Write(data);
        }
    }

    private static string HeadingStyle(int level, int size) =>
        $"<w:style w:type=\"paragraph\" w:styleId=\"Heading{level}\"><w:name w:val=\"heading {level}\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:uiPriority w:val=\"9\"/><w:qFormat/>" +
        $"<w:pPr><w:keepNext/><w:keepLines/><w:spacing w:before=\"{(level == 1 ? 360 : 240)}\" w:after=\"80\"/><w:outlineLvl w:val=\"{level - 1}\"/></w:pPr>" +
        $"<w:rPr><w:b/><w:bCs/><w:sz w:val=\"{size}\"/><w:szCs w:val=\"{size}\"/></w:rPr></w:style>";

    /// Word shows PNG, JPEG, GIF and BMP; anything else is turned into PNG.
    internal static (byte[] Data, string Ext) WordImage(ImageBlock image)
    {
        if (image.Ext is "png" or "jpg" or "gif" or "bmp") return (image.Data, image.Ext);
        try
        {
            using var magick = new ImageMagick.MagickImage(image.Data);
            return (magick.ToByteArray(ImageMagick.MagickFormat.Png), "png");
        }
        catch (Exception)
        {
            return (image.Data, image.Ext);
        }
    }
}

public static class Language
{
    /// A rough guess from the script of the text: ko, ja, zh, ru or the UI language for Latin text.
    public static string Detect(string text)
    {
        int hangul = 0, kana = 0, han = 0, cyrillic = 0, latin = 0;
        foreach (var c in text.Length > 6000 ? text[..6000] : text)
        {
            if (c is >= '\uAC00' and <= '\uD7A3' || c is >= '\u1100' and <= '\u11FF') hangul++;
            else if (c is >= '\u3040' and <= '\u30FF') kana++;
            else if (c is >= '\u4E00' and <= '\u9FFF') han++;
            else if (c is >= '\u0400' and <= '\u04FF') cyrillic++;
            else if (char.IsAsciiLetter(c)) latin++;
        }
        if (hangul > 0 && hangul >= kana) return "ko";
        if (kana > 0) return "ja";
        if (han > latin / 4 && han > 0) return Loc.Language == "zh-Hant" ? "zh-Hant" : "zh-Hans";
        if (cyrillic > latin) return "ru";
        var ui = Loc.Language;
        return ui is "ko" or "ja" or "zh-Hans" or "zh-Hant" or "ru" ? "en" : ui;
    }

    public static string? EastAsianFont(string language) => language switch
    {
        "ko" => "Malgun Gothic",
        "ja" => "Yu Gothic",
        "zh-Hans" => "Microsoft YaHei",
        "zh-Hant" => "Microsoft JhengHei",
        _ => null,
    };
}
