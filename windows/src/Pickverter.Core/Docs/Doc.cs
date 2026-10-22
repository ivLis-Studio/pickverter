namespace Pickverter;

[Flags]
public enum RunStyle { None = 0, Bold = 1, Italic = 2, Underline = 4, Strike = 8, Code = 16 }

/// A piece of text with one style. "\n" on its own is a line break.
public sealed record Run(string Text, RunStyle Style = RunStyle.None, string? Link = null)
{
    public static readonly Run Break = new("\n");
    public bool IsBreak => Text == "\n";
}

public abstract record Block;
public sealed record HeadingBlock(int Level, IReadOnlyList<Run> Runs) : Block;
public sealed record ParagraphBlock(IReadOnlyList<Run> Runs) : Block;
public sealed record ListEntry(IReadOnlyList<Run> Runs, int Level = 0);
public sealed record ListBlock(bool Ordered, IReadOnlyList<ListEntry> Entries) : Block;
public sealed record QuoteBlock(IReadOnlyList<Run> Runs) : Block;
public sealed record CodeBlock(string Text) : Block;
/// Rows of cells; the first row is the header.
public sealed record TableBlock(IReadOnlyList<IReadOnlyList<IReadOnlyList<Run>>> Rows) : Block;
public sealed record ImageBlock(byte[] Data, string Ext, string Alt, int Width, int Height) : Block;
public sealed record RuleBlock : Block;

/// A document reduced to what every format can carry: headings, paragraphs with bold/italic/links, lists,
/// quotes, code, tables and pictures. Readers fill it, writers turn it into DOCX, ODT, RTF, HTML, Markdown or text.
public sealed class Doc
{
    public string Title { get; set; } = "";
    public List<Block> Blocks { get; } = [];

    public static string Plain(IEnumerable<Run> runs) => string.Concat(runs.Select(run => run.Text));

    public string PlainText()
    {
        var parts = new List<string>();
        foreach (var block in Blocks)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    parts.Add(Plain(heading.Runs));
                    break;
                case ParagraphBlock paragraph:
                    parts.Add(Plain(paragraph.Runs));
                    break;
                case QuoteBlock quote:
                    parts.Add(Plain(quote.Runs));
                    break;
                case ListBlock list:
                    parts.Add(string.Join("\n", list.Entries.Select((entry, index) =>
                        new string(' ', entry.Level * 2) + (list.Ordered ? $"{index + 1}. " : "• ") + Plain(entry.Runs))));
                    break;
                case CodeBlock code:
                    parts.Add(code.Text);
                    break;
                case TableBlock table:
                    parts.Add(string.Join("\n", table.Rows.Select(row => string.Join("\t", row.Select(cell => Plain(cell).Replace("\n", " "))))));
                    break;
                case ImageBlock image:
                    if (image.Alt.Length > 0) parts.Add("[" + image.Alt + "]");
                    break;
            }
        }
        return string.Join("\n\n", parts.Where(part => part.Length > 0)).Trim() + "\n";
    }

    /// Adjacent runs with the same style and link become one; empty runs are dropped.
    public static List<Run> Merge(IEnumerable<Run> runs)
    {
        var result = new List<Run>();
        foreach (var run in runs)
        {
            if (run.Text.Length == 0) continue;
            if (result.Count > 0 && !run.IsBreak && !result[^1].IsBreak && result[^1].Style == run.Style && result[^1].Link == run.Link)
                result[^1] = result[^1] with { Text = result[^1].Text + run.Text };
            else
                result.Add(run);
        }
        return result;
    }

    /// Trims leading and trailing whitespace of a paragraph's runs.
    public static List<Run> Trim(IReadOnlyList<Run> runs)
    {
        var list = Merge(runs);
        while (list.Count > 0 && (list[0].IsBreak || list[0].Text.Trim().Length == 0)) list.RemoveAt(0);
        while (list.Count > 0 && (list[^1].IsBreak || list[^1].Text.Trim().Length == 0)) list.RemoveAt(list.Count - 1);
        if (list.Count > 0) list[0] = list[0] with { Text = list[0].Text.TrimStart() };
        if (list.Count > 0) list[^1] = list[^1] with { Text = list[^1].Text.TrimEnd() };
        return list;
    }

    public static Doc FromText(string text, string title)
    {
        var doc = new Doc { Title = title };
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (var chunk in normalized.Split("\n\n"))
        {
            var trimmed = chunk.Trim('\n');
            if (trimmed.Trim().Length == 0) continue;
            var runs = new List<Run>();
            var lines = trimmed.Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                if (index > 0) runs.Add(Run.Break);
                runs.Add(new Run(lines[index]));
            }
            doc.Blocks.Add(new ParagraphBlock(runs));
        }
        return doc;
    }

    public static string ImageMime(string ext) => ext switch
    {
        "jpg" or "jpeg" => "image/jpeg",
        "png" => "image/png",
        "gif" => "image/gif",
        "svg" => "image/svg+xml",
        "webp" => "image/webp",
        "bmp" => "image/bmp",
        _ => "application/octet-stream",
    };

    /// Pixel size of encoded image bytes (0×0 when unknown).
    public static (int Width, int Height) MeasureImage(byte[] data)
    {
        try
        {
            var info = new ImageMagick.MagickImageInfo(data);
            return ((int)info.Width, (int)info.Height);
        }
        catch (Exception)
        {
            return (0, 0);
        }
    }

    public static ImageBlock? LoadImage(string path, string alt)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var ext = FileNames.NormalizedExt(path);
            if (ext is not ("png" or "jpg" or "gif" or "bmp" or "webp" or "svg")) return null;
            var data = File.ReadAllBytes(path);
            var (w, h) = MeasureImage(data);
            return new ImageBlock(data, ext, alt, w, h);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
