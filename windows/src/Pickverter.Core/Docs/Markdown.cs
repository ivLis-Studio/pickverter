using System.Text.RegularExpressions;

namespace Pickverter;

/// Markdown both ways: a forgiving reader for the common syntax and a writer that keeps the structure.
public static partial class Markdown
{
    private const char Tick = '\u0060';
    private const string Fence = "\u0060\u0060\u0060";

    [GeneratedRegex(@"^\s{0,3}(\d+)[.)]\s+")]
    private static partial Regex OrderedItem();

    [GeneratedRegex(@"^\s{0,3}[-*+]\s+")]
    private static partial Regex BulletItem();

    [GeneratedRegex(@"^\s*!\[([^\]]*)\]\(([^)\s]+)(?:\s+""[^""]*"")?\)\s*$")]
    private static partial Regex ImageLine();

    public static Doc Parse(string markdown, string? baseDirectory, string fallbackTitle)
    {
        var doc = new Doc { Title = fallbackTitle };
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var paragraph = new List<string>();
        var list = new List<ListEntry>();
        var ordered = false;

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            var runs = new List<Run>();
            for (var index = 0; index < paragraph.Count; index++)
            {
                var line = paragraph[index];
                var hardBreak = line.EndsWith("  ", StringComparison.Ordinal) || line.EndsWith('\\');
                runs.AddRange(ParseInline(line.TrimEnd(' ', '\\')));
                if (index < paragraph.Count - 1) runs.Add(hardBreak ? Run.Break : new Run(" "));
            }
            doc.Blocks.Add(new ParagraphBlock(Doc.Merge(runs)));
            paragraph.Clear();
        }

        void FlushList()
        {
            if (list.Count == 0) return;
            doc.Blocks.Add(new ListBlock(ordered, list.ToList()));
            list.Clear();
        }

        var firstHeading = true;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var trimmed = line.Trim();
            if (trimmed.StartsWith(Fence, StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                FlushParagraph();
                FlushList();
                var fence = trimmed[..3];
                var code = new List<string>();
                index++;
                while (index < lines.Length && !lines[index].Trim().StartsWith(fence, StringComparison.Ordinal))
                {
                    code.Add(lines[index]);
                    index++;
                }
                doc.Blocks.Add(new CodeBlock(string.Join("\n", code)));
                continue;
            }
            if (trimmed.Length == 0)
            {
                FlushParagraph();
                FlushList();
                continue;
            }
            if (trimmed.StartsWith('#'))
            {
                var level = trimmed.TakeWhile(c => c == '#').Count();
                if (level <= 6 && trimmed.Length > level && trimmed[level] == ' ')
                {
                    FlushParagraph();
                    FlushList();
                    var text = trimmed[(level + 1)..].TrimEnd('#', ' ');
                    if (firstHeading && level == 1 && doc.Title == fallbackTitle) doc.Title = text;
                    firstHeading = false;
                    doc.Blocks.Add(new HeadingBlock(level, ParseInline(text)));
                    continue;
                }
            }
            if (trimmed is "---" or "***" or "___" && paragraph.Count == 0)
            {
                FlushList();
                doc.Blocks.Add(new RuleBlock());
                continue;
            }
            if (trimmed.StartsWith('>'))
            {
                FlushParagraph();
                FlushList();
                var quoted = new List<string> { trimmed[1..].Trim() };
                while (index + 1 < lines.Length && lines[index + 1].Trim().StartsWith('>'))
                {
                    index++;
                    quoted.Add(lines[index].Trim()[1..].Trim());
                }
                doc.Blocks.Add(new QuoteBlock(ParseInline(string.Join(" ", quoted))));
                continue;
            }
            if (trimmed.StartsWith('|') && index + 1 < lines.Length && lines[index + 1].Contains("---") && lines[index + 1].Contains('|'))
            {
                FlushParagraph();
                FlushList();
                var rows = new List<IReadOnlyList<IReadOnlyList<Run>>> { Cells(trimmed).Select(ParseInlineBold).ToList() };
                index += 2;
                while (index < lines.Length && lines[index].Trim().StartsWith('|'))
                {
                    rows.Add(Cells(lines[index]).Select(cell => (IReadOnlyList<Run>)ParseInline(cell)).ToList());
                    index++;
                }
                index--;
                doc.Blocks.Add(new TableBlock(rows));
                continue;
            }
            var image = ImageLine().Match(line);
            if (image.Success)
            {
                FlushParagraph();
                FlushList();
                var source = image.Groups[2].Value;
                var block = !source.Contains("://") && baseDirectory != null
                    ? Doc.LoadImage(Path.GetFullPath(Path.Combine(baseDirectory, Uri.UnescapeDataString(source).Replace('/', Path.DirectorySeparatorChar))), image.Groups[1].Value)
                    : null;
                if (block != null) doc.Blocks.Add(block);
                else if (image.Groups[1].Value.Length > 0) doc.Blocks.Add(new ParagraphBlock([new Run("[" + image.Groups[1].Value + "]")]));
                continue;
            }
            var bullet = BulletItem().Match(line);
            var number = OrderedItem().Match(line);
            if (bullet.Success || number.Success)
            {
                FlushParagraph();
                var isOrdered = !bullet.Success;
                if (list.Count > 0 && isOrdered != ordered && LevelOf(line) == 0) FlushList();
                if (list.Count == 0) ordered = isOrdered;
                var text = line[(bullet.Success ? bullet.Length : number.Length)..];
                if (text.StartsWith("[ ] ", StringComparison.Ordinal)) text = "☐ " + text[4..];
                else if (text.StartsWith("[x] ", StringComparison.OrdinalIgnoreCase)) text = "☑ " + text[4..];
                list.Add(new ListEntry(ParseInline(text), LevelOf(line)));
                continue;
            }
            if (list.Count > 0 && line.StartsWith("  ", StringComparison.Ordinal))
            {
                var last = list[^1];
                list[^1] = last with { Runs = Doc.Merge(last.Runs.Append(new Run(" ")).Concat(ParseInline(trimmed))) };
                continue;
            }
            FlushList();
            paragraph.Add(trimmed);
        }
        FlushParagraph();
        FlushList();
        return doc;
    }

    private static int LevelOf(string line)
    {
        var spaces = 0;
        foreach (var c in line)
        {
            if (c == ' ') spaces++;
            else if (c == '\t') spaces += 4;
            else break;
        }
        return Math.Min(spaces / 2, 6);
    }

    private static List<string> Cells(string row) =>
        row.Trim().Trim('|').Split('|').Select(cell => cell.Trim().Replace("\\|", "|")).ToList();

    private static IReadOnlyList<Run> ParseInlineBold(string text) =>
        ParseInline(text).Select(run => run with { Style = run.Style | RunStyle.Bold }).ToList();

    /// Code spans, links, images (as alt text), bold, italic and strikethrough.
    public static List<Run> ParseInline(string text, RunStyle style = RunStyle.None, string? link = null)
    {
        var runs = new List<Run>();
        var buffer = new StringBuilder();
        void Flush()
        {
            if (buffer.Length == 0) return;
            runs.Add(new Run(buffer.ToString(), style, link));
            buffer.Clear();
        }
        const string escapable = "\\\u0060*_[]()#+-.!~|<>";
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (c == '\\' && next != '\0' && escapable.Contains(next))
            {
                buffer.Append(next);
                i++;
                continue;
            }
            if (c == Tick)
            {
                var end = text.IndexOf(Tick, i + 1);
                if (end > i)
                {
                    Flush();
                    runs.Add(new Run(text[(i + 1)..end], style | RunStyle.Code, link));
                    i = end;
                    continue;
                }
            }
            if ((c == '!' && next == '[') || c == '[')
            {
                var open = c == '!' ? i + 1 : i;
                var close = MatchingBracket(text, open);
                if (close > 0 && close + 1 < text.Length && text[close + 1] == '(')
                {
                    var paren = text.IndexOf(')', close + 2);
                    if (paren > 0)
                    {
                        var label = text[(open + 1)..close];
                        var target = text[(close + 2)..paren].Trim().Split(' ')[0];
                        Flush();
                        if (c == '!') runs.Add(new Run(label.Length > 0 ? "[" + label + "]" : "", style, link));
                        else runs.AddRange(ParseInline(label, style, target));
                        i = paren;
                        continue;
                    }
                }
            }
            if (c == '~' && next == '~')
            {
                var end = text.IndexOf("~~", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    Flush();
                    runs.AddRange(ParseInline(text[(i + 2)..end], style | RunStyle.Strike, link));
                    i = end + 1;
                    continue;
                }
            }
            if (c is '*' or '_')
            {
                var marker = next == c ? new string(c, 2) : c.ToString();
                var start = i + marker.Length;
                var wordStart = c == '_' && i > 0 && char.IsLetterOrDigit(text[i - 1]);
                if (!wordStart && start < text.Length && !char.IsWhiteSpace(text[start]))
                {
                    var end = FindClosing(text, marker, start);
                    if (end > start)
                    {
                        Flush();
                        var added = marker.Length == 2 ? RunStyle.Bold : RunStyle.Italic;
                        runs.AddRange(ParseInline(text[start..end], style | added, link));
                        i = end + marker.Length - 1;
                        continue;
                    }
                }
            }
            buffer.Append(c);
        }
        Flush();
        return Doc.Merge(runs);
    }

    private static int MatchingBracket(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '[') depth++;
            else if (text[i] == ']' && --depth == 0) return i;
        }
        return -1;
    }

    private static int FindClosing(string text, string marker, int start)
    {
        var position = start;
        while (true)
        {
            var end = text.IndexOf(marker, position, StringComparison.Ordinal);
            if (end < 0) return -1;
            var beforeOk = end > 0 && !char.IsWhiteSpace(text[end - 1]);
            var afterIndex = end + marker.Length;
            var afterOk = marker[0] != '_' || afterIndex >= text.Length || !char.IsLetterOrDigit(text[afterIndex]);
            var notLonger = marker.Length == 2 || afterIndex >= text.Length || text[afterIndex] != marker[0];
            if (beforeOk && afterOk && notLonger) return end;
            position = end + 1;
        }
    }

    // Writing

    private static string Escape(string text)
    {
        const string special = "\\\u0060*_[]";
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (special.Contains(c)) builder.Append('\\');
            builder.Append(c);
        }
        return builder.ToString();
    }

    public static string Inline(IEnumerable<Run> runs)
    {
        var builder = new StringBuilder();
        foreach (var run in runs)
        {
            if (run.IsBreak)
            {
                builder.Append("  \n");
                continue;
            }
            var core = run.Text.Trim();
            if (core.Length == 0)
            {
                builder.Append(run.Text);
                continue;
            }
            var leading = run.Text[..(run.Text.Length - run.Text.TrimStart().Length)];
            var trailing = run.Text[run.Text.TrimEnd().Length..];
            var styled = run.Style.HasFlag(RunStyle.Code) ? Tick + core + Tick : Escape(core);
            if (run.Style.HasFlag(RunStyle.Bold)) styled = "**" + styled + "**";
            if (run.Style.HasFlag(RunStyle.Italic)) styled = "*" + styled + "*";
            if (run.Style.HasFlag(RunStyle.Strike)) styled = "~~" + styled + "~~";
            if (run.Link != null) styled = "[" + styled + "](" + run.Link.Replace(" ", "%20") + ")";
            builder.Append(leading).Append(styled).Append(trailing);
        }
        return builder.ToString();
    }

    public static string Write(Doc doc)
    {
        var parts = new List<string>();
        foreach (var block in doc.Blocks)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    parts.Add(new string('#', Math.Clamp(heading.Level, 1, 6)) + " " + Inline(heading.Runs.Select(run => run with { Style = run.Style & ~RunStyle.Bold })).Replace("  \n", " "));
                    break;
                case ParagraphBlock paragraph:
                    parts.Add(Inline(paragraph.Runs));
                    break;
                case QuoteBlock quote:
                    parts.Add("> " + Inline(quote.Runs).Replace("\n", "\n> "));
                    break;
                case CodeBlock code:
                    parts.Add(Fence + "\n" + code.Text + "\n" + Fence);
                    break;
                case RuleBlock:
                    parts.Add("---");
                    break;
                case ImageBlock image:
                    if (image.Alt.Length > 0) parts.Add("*[" + Escape(image.Alt) + "]*");
                    break;
                case ListBlock list:
                {
                    var lines = new List<string>();
                    var counters = new int[8];
                    foreach (var entry in list.Entries)
                    {
                        var level = Math.Clamp(entry.Level, 0, 7);
                        counters[level]++;
                        for (var deeper = level + 1; deeper < counters.Length; deeper++) counters[deeper] = 0;
                        var marker = list.Ordered ? counters[level] + "." : "-";
                        lines.Add(new string(' ', level * (list.Ordered ? 3 : 2)) + marker + " " + Inline(entry.Runs).Replace("  \n", " "));
                    }
                    parts.Add(string.Join("\n", lines));
                    break;
                }
                case TableBlock table:
                {
                    var rows = table.Rows.Select(row => row.Select(cell => Inline(cell.Select(run => run with { Style = run.Style & ~RunStyle.Bold }))
                        .Replace("  \n", " ").Replace("|", "\\|")).ToList()).ToList();
                    var width = rows.Max(row => row.Count);
                    string Line(List<string> cells) => "| " + string.Join(" | ", cells.Concat(Enumerable.Repeat("", width - cells.Count))) + " |";
                    var lines = new List<string> { Line(rows[0]), "|" + string.Concat(Enumerable.Repeat(" --- |", width)) };
                    lines.AddRange(rows.Skip(1).Select(Line));
                    parts.Add(string.Join("\n", lines));
                    break;
                }
            }
        }
        return string.Join("\n\n", parts.Where(part => part.Length > 0)).Trim() + "\n";
    }
}
