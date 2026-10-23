using System.Net;
using System.Text.RegularExpressions;

namespace Pickverter;

public static partial class Html
{
    public static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    public static string Attribute(string text) => Escape(text).Replace("\"", "&quot;");

    public const string Css =
        "body { font-family: \"Segoe UI Variable Text\", \"Segoe UI\", \"Malgun Gothic\", \"Yu Gothic UI\", \"Microsoft YaHei UI\", system-ui, sans-serif; " +
        "font-size: 11pt; line-height: 1.6; color: #1b1b1f; max-width: 760px; margin: 2em auto; padding: 0 1em; }\n" +
        "h1 { font-size: 22pt; line-height: 1.25; } h2 { font-size: 17pt; } h3 { font-size: 14pt; } h4, h5, h6 { font-size: 12pt; }\n" +
        "code, pre { font-family: \"Cascadia Mono\", Consolas, monospace; font-size: 10pt; }\n" +
        "code { background: #f2f2f5; padding: 0 3px; border-radius: 3px; } pre { background: #f5f5f7; padding: 10px; white-space: pre-wrap; }\n" +
        "blockquote { color: #555; border-left: 3px solid #ccc; margin-left: 0; padding-left: 12px; }\n" +
        "table { border-collapse: collapse; margin: 0.8em 0; } th, td { border: 1px solid #ccc; padding: 4px 8px; vertical-align: top; } th { background: #f4f4f6; }\n" +
        "img { max-width: 100%; height: auto; }\n" +
        "@media print { body { margin: 0; max-width: none; } }\n";

    // Writing

    public static string Inline(IEnumerable<Run> runs)
    {
        var builder = new StringBuilder();
        foreach (var run in runs)
        {
            if (run.IsBreak)
            {
                builder.Append("<br>");
                continue;
            }
            var text = Escape(run.Text);
            if (run.Style.HasFlag(RunStyle.Code)) text = "<code>" + text + "</code>";
            if (run.Style.HasFlag(RunStyle.Bold)) text = "<strong>" + text + "</strong>";
            if (run.Style.HasFlag(RunStyle.Italic)) text = "<em>" + text + "</em>";
            if (run.Style.HasFlag(RunStyle.Underline)) text = "<u>" + text + "</u>";
            if (run.Style.HasFlag(RunStyle.Strike)) text = "<del>" + text + "</del>";
            if (run.Link != null) text = "<a href=\"" + Attribute(run.Link) + "\">" + text + "</a>";
            builder.Append(text);
        }
        return builder.ToString();
    }

    /// Block HTML. imageSource maps a picture to its src (a data: URL by default).
    public static string Body(Doc doc, Func<ImageBlock, string>? imageSource = null, bool xhtml = false)
    {
        imageSource ??= image => "data:" + Doc.ImageMime(image.Ext) + ";base64," + Convert.ToBase64String(image.Data);
        var br = xhtml ? "<br/>" : "<br>";
        var builder = new StringBuilder();
        foreach (var block in doc.Blocks)
        {
            switch (block)
            {
                case HeadingBlock heading:
                {
                    var level = Math.Clamp(heading.Level, 1, 6);
                    builder.Append($"<h{level}>").Append(Inline(heading.Runs).Replace("<br>", br)).Append($"</h{level}>\n");
                    break;
                }
                case ParagraphBlock paragraph:
                    builder.Append("<p>").Append(Inline(paragraph.Runs).Replace("<br>", br)).Append("</p>\n");
                    break;
                case QuoteBlock quote:
                    builder.Append("<blockquote><p>").Append(Inline(quote.Runs).Replace("<br>", br)).Append("</p></blockquote>\n");
                    break;
                case CodeBlock code:
                    builder.Append("<pre><code>").Append(Escape(code.Text)).Append("</code></pre>\n");
                    break;
                case RuleBlock:
                    builder.Append(xhtml ? "<hr/>\n" : "<hr>\n");
                    break;
                case ImageBlock image:
                    builder.Append("<p><img alt=\"").Append(Attribute(image.Alt)).Append("\" src=\"").Append(Attribute(imageSource(image)))
                        .Append(xhtml ? "\"/></p>\n" : "\"></p>\n");
                    break;
                case ListBlock list:
                {
                    var tag = list.Ordered ? "ol" : "ul";
                    var depth = -1;
                    foreach (var entry in list.Entries)
                    {
                        var level = Math.Max(0, entry.Level);
                        while (depth < level)
                        {
                            builder.Append('<').Append(tag).Append(">\n");
                            depth++;
                        }
                        while (depth > level)
                        {
                            builder.Append("</").Append(tag).Append(">\n");
                            depth--;
                        }
                        builder.Append("<li>").Append(Inline(entry.Runs).Replace("<br>", br)).Append("</li>\n");
                    }
                    while (depth >= 0)
                    {
                        builder.Append("</").Append(tag).Append(">\n");
                        depth--;
                    }
                    break;
                }
                case TableBlock table:
                {
                    builder.Append("<table>\n");
                    for (var row = 0; row < table.Rows.Count; row++)
                    {
                        var cell = row == 0 && table.HeaderRow ? "th" : "td";
                        builder.Append("<tr>");
                        foreach (var runs in table.Rows[row]) builder.Append('<').Append(cell).Append('>').Append(Inline(runs).Replace("<br>", br)).Append("</").Append(cell).Append('>');
                        builder.Append("</tr>\n");
                    }
                    builder.Append("</table>\n");
                    break;
                }
            }
        }
        return builder.ToString();
    }

    public static string Document(Doc doc) =>
        "<!DOCTYPE html>\n<html><head><meta charset=\"utf-8\"><title>" + Escape(doc.Title) + "</title>\n<style>\n" + Css +
        "</style></head><body>\n" + Body(doc) + "</body></html>\n";

    // Reading

    [GeneratedRegex(@"<!--.*?-->|<!\[CDATA\[.*?\]\]>|<![^>]*>|<\?[^>]*\?>|<(/?)([a-zA-Z][a-zA-Z0-9:-]*)((?:[^>""']|""[^""]*""|'[^']*')*)/?>", RegexOptions.Singleline)]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"([a-zA-Z_:][-a-zA-Z0-9_:.]*)\s*(?:=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+)))?")]
    private static partial Regex AttributePattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private static Dictionary<string, string> Attributes(string raw)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in AttributePattern().Matches(raw))
        {
            var value = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Success ? match.Groups[3].Value : match.Groups[4].Value;
            result[match.Groups[1].Value] = WebUtility.HtmlDecode(value);
        }
        return result;
    }

    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "section", "article", "header", "footer", "main", "aside", "nav", "figure", "figcaption", "address",
        "h1", "h2", "h3", "h4", "h5", "h6", "li", "dt", "dd", "blockquote", "pre", "table", "tr", "ul", "ol", "dl", "hr", "body", "center",
    };

    public static Doc Parse(string html, string? baseDirectory, string fallbackTitle)
    {
        var doc = new Doc { Title = fallbackTitle };
        var runs = new List<Run>();
        int bold = 0, italic = 0, underline = 0, strike = 0, code = 0, skip = 0, quote = 0, pre = 0;
        int? heading = null;
        var links = new Stack<string?>();
        var lists = new Stack<bool>();
        var listEntries = new List<ListEntry>();
        var listOrdered = false;
        var inItem = false;
        List<List<IReadOnlyList<Run>>>? tableRows = null;
        List<IReadOnlyList<Run>>? tableRow = null;
        List<Run>? cell = null;
        var preText = new StringBuilder();
        var title = new StringBuilder();
        var inTitle = false;

        RunStyle Style() =>
            (bold > 0 ? RunStyle.Bold : 0) | (italic > 0 ? RunStyle.Italic : 0) | (underline > 0 ? RunStyle.Underline : 0) |
            (strike > 0 ? RunStyle.Strike : 0) | (code > 0 ? RunStyle.Code : 0);

        void FlushList()
        {
            if (listEntries.Count == 0) return;
            doc.Blocks.Add(new ListBlock(listOrdered, listEntries.ToList()));
            listEntries.Clear();
        }

        void Flush()
        {
            var trimmed = Doc.Trim(runs);
            runs.Clear();
            if (inItem)
            {
                if (trimmed.Count > 0) listEntries.Add(new ListEntry(trimmed, Math.Max(0, lists.Count - 1)));
                return;
            }
            if (trimmed.Count == 0) return;
            FlushList();
            if (heading is { } level) doc.Blocks.Add(new HeadingBlock(level, trimmed));
            else if (quote > 0) doc.Blocks.Add(new QuoteBlock(trimmed));
            else doc.Blocks.Add(new ParagraphBlock(trimmed));
        }

        void AddText(string text)
        {
            if (skip > 0) return;
            if (inTitle)
            {
                title.Append(text);
                return;
            }
            if (pre > 0)
            {
                preText.Append(WebUtility.HtmlDecode(text));
                return;
            }
            var decoded = WebUtility.HtmlDecode(Whitespace().Replace(text, " "));
            if (decoded.Length == 0) return;
            var run = new Run(decoded, Style(), links.Count > 0 ? links.Peek() : null);
            if (cell != null) cell.Add(run); else runs.Add(run);
        }

        var position = 0;
        foreach (Match match in TagPattern().Matches(html))
        {
            if (match.Index > position) AddText(html[position..match.Index]);
            position = match.Index + match.Length;
            if (!match.Groups[2].Success) continue;
            var closing = match.Groups[1].Value == "/";
            var name = match.Groups[2].Value.ToLowerInvariant();
            if (name is "script" or "style" or "head" or "noscript" or "template" or "svg")
            {
                if (name == "head" && !closing) continue;
                if (name == "head") continue;
                skip += closing ? -1 : 1;
                skip = Math.Max(0, skip);
                continue;
            }
            if (name == "title")
            {
                inTitle = !closing;
                continue;
            }
            if (skip > 0) continue;
            var attributes = closing ? null : Attributes(match.Groups[3].Value);
            switch (name)
            {
                case "b" or "strong": bold += closing ? -1 : 1; continue;
                case "i" or "em" or "cite" or "var": italic += closing ? -1 : 1; continue;
                case "u" or "ins": underline += closing ? -1 : 1; continue;
                case "s" or "del" or "strike": strike += closing ? -1 : 1; continue;
                case "code" or "kbd" or "samp" or "tt": if (pre == 0) code += closing ? -1 : 1; continue;
                case "a":
                    if (closing)
                    {
                        if (links.Count > 0) links.Pop();
                    }
                    else
                    {
                        var href = attributes!.GetValueOrDefault("href");
                        links.Push(href is { Length: > 0 } && !href.StartsWith('#') ? href : links.Count > 0 ? links.Peek() : null);
                    }
                    continue;
                case "br":
                    if (pre > 0) preText.Append('\n');
                    else if (cell != null) cell.Add(Run.Break);
                    else runs.Add(Run.Break);
                    continue;
                case "img":
                {
                    if (closing) continue;
                    var alt = attributes!.GetValueOrDefault("alt") ?? "";
                    var src = attributes!.GetValueOrDefault("src") ?? "";
                    var image = LoadSource(src, baseDirectory, alt);
                    if (image != null && cell == null)
                    {
                        Flush();
                        FlushList();
                        doc.Blocks.Add(image);
                    }
                    else if (alt.Length > 0)
                    {
                        AddText("[" + alt + "]");
                    }
                    continue;
                }
                case "hr":
                    Flush();
                    FlushList();
                    doc.Blocks.Add(new RuleBlock());
                    continue;
                case "pre":
                    if (!closing)
                    {
                        Flush();
                        pre++;
                        preText.Clear();
                    }
                    else if (pre > 0)
                    {
                        pre--;
                        FlushList();
                        var text = preText.ToString().Trim('\n');
                        if (text.Length > 0) doc.Blocks.Add(new CodeBlock(text));
                        preText.Clear();
                    }
                    continue;
                case "table":
                    if (!closing)
                    {
                        Flush();
                        FlushList();
                        tableRows = [];
                    }
                    else if (tableRows != null)
                    {
                        if (tableRows.Count > 0) doc.Blocks.Add(new TableBlock(tableRows.Select(row => (IReadOnlyList<IReadOnlyList<Run>>)row).ToList()));
                        tableRows = null;
                    }
                    continue;
                case "tr":
                    if (tableRows == null) continue;
                    if (!closing) tableRow = [];
                    else if (tableRow != null)
                    {
                        if (tableRow.Count > 0) tableRows.Add(tableRow);
                        tableRow = null;
                    }
                    continue;
                case "td" or "th":
                    if (tableRow == null) continue;
                    if (!closing)
                    {
                        cell = [];
                        if (name == "th") bold++;
                    }
                    else if (cell != null)
                    {
                        tableRow.Add(Doc.Trim(cell));
                        cell = null;
                        if (name == "th") bold = Math.Max(0, bold - 1);
                    }
                    continue;
                case "ul" or "ol":
                    Flush();
                    if (!closing)
                    {
                        if (lists.Count == 0)
                        {
                            FlushList();
                            listOrdered = name == "ol";
                        }
                        lists.Push(name == "ol");
                        inItem = false;
                    }
                    else if (lists.Count > 0)
                    {
                        lists.Pop();
                        inItem = lists.Count > 0;
                        if (lists.Count == 0) FlushList();
                    }
                    continue;
                case "li":
                    Flush();
                    inItem = lists.Count > 0;
                    continue;
                case "blockquote":
                    Flush();
                    quote += closing ? -1 : 1;
                    quote = Math.Max(0, quote);
                    continue;
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    Flush();
                    heading = closing ? null : name[1] - '0';
                    continue;
            }
            if (BlockTags.Contains(name) && cell == null) Flush();
        }
        if (position < html.Length) AddText(html[position..]);
        Flush();
        FlushList();
        var titleText = WebUtility.HtmlDecode(title.ToString()).Trim();
        if (titleText.Length > 0) doc.Title = titleText;
        return doc;
    }

    private static ImageBlock? LoadSource(string src, string? baseDirectory, string alt)
    {
        try
        {
            if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = src.IndexOf(',');
                if (comma < 0 || !src[..comma].Contains("base64", StringComparison.OrdinalIgnoreCase)) return null;
                var data = Convert.FromBase64String(src[(comma + 1)..]);
                var mime = src[5..comma].Split(';')[0].ToLowerInvariant();
                var ext = mime switch
                {
                    "image/png" => "png",
                    "image/jpeg" => "jpg",
                    "image/gif" => "gif",
                    "image/webp" => "webp",
                    "image/svg+xml" => "svg",
                    _ => null,
                };
                if (ext == null) return null;
                var (w, h) = Doc.MeasureImage(data);
                return new ImageBlock(data, ext, alt, w, h);
            }
            if (src.Contains("://") || baseDirectory == null) return null;
            var path = Path.GetFullPath(Path.Combine(baseDirectory, Uri.UnescapeDataString(src).Replace('/', Path.DirectorySeparatorChar)));
            return Doc.LoadImage(path, alt);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
