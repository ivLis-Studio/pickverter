namespace Pickverter;

/// Rich Text Format both ways (WordPad, Word, TextEdit files).
public static class Rtf
{
    public static Doc Read(string rtf, string fallbackTitle) => new Reader(rtf, fallbackTitle).Read();

    private enum Dest { Text, Skip, FontTable, Info, Title, Pict, FieldInstruction, FieldResult, ListText }

    private struct State
    {
        public bool Bold, Italic, Underline, Strike, Hidden;
        public int FontSize, Font, Uc;
        public Dest Dest;
        public bool Ignorable;
        public string? Link;
    }

    private sealed class Reader(string source, string fallbackTitle)
    {
        private readonly Doc doc = new() { Title = fallbackTitle };
        private readonly Stack<State> stack = new();
        private State state = new() { FontSize = 24, Uc = 1 };
        private int position;
        private int codePage = 1252;
        private readonly Dictionary<int, int> fontCodePages = new();
        private readonly Dictionary<int, string> fontNames = new();
        private int definingFont;
        private readonly StringBuilder fontName = new();
        private readonly List<byte> pendingBytes = [];
        private int skip;
        private readonly List<(Run Run, int Size)> runs = [];
        private int? outline;
        private int? listLevel;
        private int leftIndent;
        private bool inTable;
        private readonly StringBuilder listText = new();
        private readonly List<IReadOnlyList<Run>> row = [];
        private readonly List<IReadOnlyList<IReadOnlyList<Run>>> rows = [];
        private readonly List<Run> cell = [];
        private readonly List<ListEntry> listEntries = [];
        private bool listOrdered;
        private readonly StringBuilder title = new();
        private readonly StringBuilder instruction = new();
        private readonly StringBuilder pictHex = new();
        private string? pictType;
        private string? fieldLink;
        private readonly StringBuilder text = new();

        private static readonly HashSet<string> SkipDestinations =
        [
            "colortbl", "stylesheet", "header", "headerl", "headerr", "headerf", "footer", "footerl", "footerr", "footerf", "footnote",
            "annotation", "object", "nonshppict", "listtable", "listoverridetable", "rsidtbl", "generator", "xmlnstbl", "themedata",
            "colorschememapping", "latentstyles", "datastore", "mmathPr", "pgdsctbl", "defchp", "defpap", "revtbl", "protusertbl",
            "wgrffmtfilter", "fchars", "lchars", "bkmkstart", "bkmkend", "author", "operator", "company", "creatim", "revtim", "printim",
            "buptim", "doccomm", "keywords", "subject", "comment", "hlinkbase", "category", "manager", "userprops", "pntxta", "pntxtb",
            "pn", "xe", "tc", "txe", "atnid", "atnauthor", "falt", "panose", "fname", "passwordhash", "docvar", "objdata",
        ];

        private Encoding CurrentEncoding()
        {
            FileSystem.EnsureCodePages();
            var page = fontCodePages.TryGetValue(state.Font, out var fontPage) ? fontPage : codePage;
            try { return Encoding.GetEncoding(page); } catch (Exception) { return Encoding.Latin1; }
        }

        private void FlushBytes()
        {
            if (pendingBytes.Count == 0) return;
            var decoded = CurrentEncoding().GetString(pendingBytes.ToArray());
            pendingBytes.Clear();
            foreach (var c in decoded) Emit(c);
        }

        private void Emit(char c)
        {
            if (state.Hidden) return;
            switch (state.Dest)
            {
                case Dest.FontTable:
                    fontName.Append(c);
                    return;
                case Dest.Title:
                    title.Append(c);
                    return;
                case Dest.FieldInstruction:
                    instruction.Append(c);
                    return;
                case Dest.Pict:
                    pictHex.Append(c);
                    return;
                case Dest.ListText:
                    listText.Append(c);
                    return;
                case Dest.Text:
                case Dest.FieldResult:
                    text.Append(c);
                    return;
            }
        }

        private void EmitText(string value)
        {
            foreach (var c in value) Emit(c);
        }

        private RunStyle CurrentStyle()
        {
            var style = RunStyle.None;
            if (state.Bold) style |= RunStyle.Bold;
            if (state.Italic) style |= RunStyle.Italic;
            if (state.Underline && state.Link == null) style |= RunStyle.Underline;
            if (state.Strike) style |= RunStyle.Strike;
            if (fontNames.TryGetValue(state.Font, out var name) &&
                (name.Contains("Courier", StringComparison.OrdinalIgnoreCase) || name.Contains("Consolas", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("Mono", StringComparison.OrdinalIgnoreCase) || name.Contains("Menlo", StringComparison.OrdinalIgnoreCase)))
                style |= RunStyle.Code;
            return style;
        }

        /// Text collected under the current formatting becomes a run (called before formatting changes).
        private void FlushText()
        {
            FlushBytes();
            if (text.Length == 0) return;
            runs.Add((new Run(text.ToString(), CurrentStyle(), state.Link), state.FontSize));
            text.Clear();
        }

        private void EndParagraph()
        {
            FlushText();
            var paragraph = Doc.Trim(runs.Select(entry => entry.Run).ToList());
            var sizes = runs.Where(entry => !entry.Run.IsBreak && entry.Run.Text.Trim().Length > 0).Select(entry => entry.Size).ToList();
            runs.Clear();
            if (inTable)
            {
                if (cell.Count > 0 && paragraph.Count > 0) cell.Add(Run.Break);
                cell.AddRange(paragraph);
                return;
            }
            if (rows.Count > 0) EndTable();
            // WordPad-style lists ({\pntext}) and plain "•<tab>" / "1.<tab>" paragraphs are lists too.
            if (listLevel == null && paragraph.Count > 0 && !paragraph[0].IsBreak)
            {
                var marker = System.Text.RegularExpressions.Regex.Match(paragraph[0].Text, @"^([•◦▪·]|\d+[.)])\t");
                if (marker.Success || listText.Length > 0)
                {
                    var ordered = marker.Success ? char.IsAsciiDigit(marker.Groups[1].Value[0]) : listText.ToString().Any(char.IsAsciiDigit);
                    if (marker.Success) paragraph[0] = paragraph[0] with { Text = paragraph[0].Text[marker.Length..] };
                    FlushListIfKindChanges(ordered);
                    if (listEntries.Count == 0) listOrdered = ordered;
                    listEntries.Add(new ListEntry(Doc.Trim(paragraph), Math.Max(0, (leftIndent - 720) / 360)));
                    listText.Clear();
                    return;
                }
            }
            if (listLevel is { } level)
            {
                if (paragraph.Count > 0)
                {
                    var marker = listText.ToString();
                    if (listEntries.Count == 0) listOrdered = marker.Any(char.IsAsciiDigit);
                    listEntries.Add(new ListEntry(paragraph, level));
                }
                listText.Clear();
                return;
            }
            FlushList();
            if (paragraph.Count == 0) return;
            var smallest = sizes.Count > 0 ? sizes.Min() : 24;
            int? heading = outline is { } value and < 6 ? value + 1 : smallest >= 42 ? 1 : smallest >= 33 ? 2 : smallest >= 29 ? 3 : null;
            if (heading is { } headingLevel)
                doc.Blocks.Add(new HeadingBlock(headingLevel, paragraph.Select(run => run with { Style = run.Style & ~RunStyle.Bold }).ToList()));
            else if (paragraph.All(run => run.IsBreak || run.Style.HasFlag(RunStyle.Code)))
                doc.Blocks.Add(new CodeBlock(Doc.Plain(paragraph)));
            else
                doc.Blocks.Add(new ParagraphBlock(paragraph));
        }

        private void FlushList()
        {
            if (listEntries.Count == 0) return;
            doc.Blocks.Add(new ListBlock(listOrdered, listEntries.ToList()));
            listEntries.Clear();
        }

        private void FlushListIfKindChanges(bool ordered)
        {
            if (listEntries.Count > 0 && listOrdered != ordered) FlushList();
        }

        private void EndRow()
        {
            if (row.Count > 0) rows.Add(row.ToList());
            row.Clear();
        }

        private void EndTable()
        {
            if (rows.Count > 0)
            {
                FlushList();
                doc.Blocks.Add(new TableBlock(rows.ToList()));
            }
            rows.Clear();
        }

        public Doc Read()
        {
            var s = source;
            while (position < s.Length)
            {
                var c = s[position];
                if (c == '{')
                {
                    FlushText();
                    stack.Push(state);
                    state.Ignorable = false;
                    position++;
                    continue;
                }
                if (c == '}')
                {
                    FlushText();
                    LeaveGroup();
                    position++;
                    continue;
                }
                if (c == '\\')
                {
                    ControlWord();
                    continue;
                }
                position++;
                if (c is '\r' or '\n') continue;
                if (skip > 0)
                {
                    skip--;
                    continue;
                }
                FlushBytes();
                if (state.Dest == Dest.FontTable && c == ';')
                {
                    fontNames[definingFont] = fontName.ToString().Trim();
                    fontName.Clear();
                    continue;
                }
                Emit(c);
            }
            EndParagraph();
            if (inTable) EndTable();
            FlushList();
            if (rows.Count > 0) EndTable();
            if (title.ToString().Trim() is { Length: > 0 } heading) doc.Title = heading;
            return doc;
        }

        private void LeaveGroup()
        {
            var leaving = state.Dest;
            if (leaving == Dest.Pict) FinishPicture();
            if (leaving == Dest.FieldInstruction)
            {
                var match = System.Text.RegularExpressions.Regex.Match(instruction.ToString(), "HYPERLINK\\s+\"([^\"]+)\"");
                fieldLink = match.Success ? match.Groups[1].Value : null;
                instruction.Clear();
            }
            state = stack.Count > 0 ? stack.Pop() : state;
            if (leaving == Dest.FieldResult && state.Dest != Dest.FieldResult) fieldLink = null;
        }

        private void FinishPicture()
        {
            var hex = pictHex.ToString();
            pictHex.Clear();
            if (pictType == null || hex.Length < 16) return;
            try
            {
                var clean = new string(hex.Where(Uri.IsHexDigit).ToArray());
                var data = Convert.FromHexString(clean.Length % 2 == 0 ? clean : clean[..^1]);
                var (w, h) = Doc.MeasureImage(data);
                EndParagraph();
                FlushList();
                doc.Blocks.Add(new ImageBlock(data, pictType, "", w, h));
            }
            catch (Exception)
            {
            }
            pictType = null;
        }

        private void ControlWord()
        {
            var s = source;
            position++;
            if (position >= s.Length) return;
            var c = s[position];
            if (!char.IsAsciiLetter(c))
            {
                position++;
                switch (c)
                {
                    case '\\' or '{' or '}':
                        if (skip > 0) skip--;
                        else
                        {
                            FlushBytes();
                            Emit(c);
                        }
                        break;
                    case '\'':
                        if (position + 2 <= s.Length && byte.TryParse(s.AsSpan(position, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                        {
                            position += 2;
                            if (skip > 0) skip--;
                            else pendingBytes.Add(value);
                        }
                        break;
                    case '*':
                        state.Ignorable = true;
                        break;
                    case '~':
                        FlushBytes();
                        Emit('\u00A0');
                        break;
                    case '_':
                        FlushBytes();
                        Emit('-');
                        break;
                    case '\r' or '\n':
                        Word("par", null);
                        break;
                }
                return;
            }
            var start = position;
            while (position < s.Length && char.IsAsciiLetter(s[position])) position++;
            var word = s[start..position];
            int? parameter = null;
            if (position < s.Length && (s[position] == '-' || char.IsAsciiDigit(s[position])))
            {
                var numberStart = position;
                position++;
                while (position < s.Length && char.IsAsciiDigit(s[position])) position++;
                if (int.TryParse(s.AsSpan(numberStart, position - numberStart), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
                    parameter = number;
            }
            if (position < s.Length && s[position] == ' ') position++;
            Word(word, parameter);
        }

        private void Word(string word, int? parameter)
        {
            var on = parameter is null or not 0;
            switch (word)
            {
                case "ansicpg":
                    codePage = parameter ?? 1252;
                    return;
                case "fonttbl":
                    state.Dest = Dest.FontTable;
                    return;
                case "info":
                    state.Dest = Dest.Info;
                    return;
                case "title":
                    state.Dest = state.Dest == Dest.Info ? Dest.Title : Dest.Skip;
                    return;
                case "pict":
                    state.Dest = Dest.Pict;
                    pictType = null;
                    pictHex.Clear();
                    return;
                case "pngblip":
                    pictType = "png";
                    return;
                case "jpegblip":
                    pictType = "jpg";
                    return;
                case "fldinst":
                    state.Dest = Dest.FieldInstruction;
                    return;
                case "fldrslt":
                    FlushText();
                    state.Dest = Dest.FieldResult;
                    state.Link = fieldLink;
                    return;
                case "listtext":
                case "pntext":
                    state.Dest = Dest.ListText;
                    listText.Clear();
                    return;
                case "field" or "shppict" or "shp" or "shpinst" or "sp" or "sn" or "sv":
                    return;
            }
            if (SkipDestinations.Contains(word) || (state.Ignorable && state.Dest != Dest.Skip && !IsFormatting(word)))
            {
                if (state.Ignorable || SkipDestinations.Contains(word)) state.Dest = Dest.Skip;
                return;
            }
            if (state.Dest == Dest.FontTable)
            {
                if (word == "f") definingFont = parameter ?? 0;
                else if (word == "fcharset") fontCodePages[definingFont] = Charset(parameter ?? 0);
                return;
            }
            if (state.Dest is Dest.Skip or Dest.Pict) return;
            switch (word)
            {
                case "par":
                case "sect":
                    EndParagraph();
                    return;
                case "line":
                    FlushText();
                    runs.Add((Run.Break, state.FontSize));
                    return;
                case "tab":
                    FlushBytes();
                    Emit('\t');
                    return;
                case "pard":
                    FlushText();
                    outline = null;
                    listLevel = null;
                    leftIndent = 0;
                    inTable = false;
                    return;
                case "li":
                    leftIndent = parameter ?? 0;
                    return;
                case "intbl":
                    inTable = true;
                    return;
                case "cell":
                    EndParagraph();
                    row.Add(Doc.Trim(cell));
                    cell.Clear();
                    return;
                case "row":
                    EndRow();
                    return;
                case "trowd":
                    return;
                case "outlinelevel":
                    outline = parameter;
                    return;
                case "ls":
                    listLevel ??= 0;
                    return;
                case "ilvl":
                    listLevel = parameter ?? 0;
                    return;
                case "u":
                    if (parameter is { } code)
                    {
                        FlushBytes();
                        Emit((char)(code < 0 ? code + 65536 : code));
                        skip = state.Uc;
                    }
                    return;
                case "uc":
                    state.Uc = parameter ?? 1;
                    return;
                case "emdash": FlushBytes(); Emit('—'); return;
                case "endash": FlushBytes(); Emit('–'); return;
                case "bullet": FlushBytes(); Emit('•'); return;
                case "lquote": FlushBytes(); Emit('‘'); return;
                case "rquote": FlushBytes(); Emit('’'); return;
                case "ldblquote": FlushBytes(); Emit('“'); return;
                case "rdblquote": FlushBytes(); Emit('”'); return;
            }
            if (!IsFormatting(word)) return;
            FlushText();
            switch (word)
            {
                case "b": state.Bold = on; break;
                case "i": state.Italic = on; break;
                case "ul": state.Underline = on; break;
                case "ulnone": state.Underline = false; break;
                case "strike" or "striked": state.Strike = on; break;
                case "v": state.Hidden = on; break;
                case "fs": state.FontSize = parameter ?? 24; break;
                case "f": state.Font = parameter ?? 0; break;
                case "plain":
                    state.Bold = state.Italic = state.Underline = state.Strike = state.Hidden = false;
                    state.FontSize = 24;
                    break;
            }
        }

        private static bool IsFormatting(string word) =>
            word is "b" or "i" or "ul" or "ulnone" or "strike" or "striked" or "v" or "fs" or "f" or "plain";

        private static int Charset(int charset) => charset switch
        {
            128 => 932,
            129 => 949,
            130 => 1361,
            134 => 936,
            136 => 950,
            161 => 1253,
            162 => 1254,
            177 => 1255,
            178 => 1256,
            186 => 1257,
            204 => 1251,
            222 => 874,
            238 => 1250,
            _ => 1252,
        };
    }

    // Writing

    private static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\': builder.Append("\\\\"); break;
                case '{': builder.Append("\\{"); break;
                case '}': builder.Append("\\}"); break;
                case '\t': builder.Append("\\tab "); break;
                case '\n': builder.Append("\\line "); break;
                case '\r': break;
                default:
                    if (c > 127) builder.Append("\\u").Append(((short)c).ToString(CultureInfo.InvariantCulture)).Append('?');
                    else builder.Append(c);
                    break;
            }
        }
        return builder.ToString();
    }

    private static string Runs(IEnumerable<Run> runs, bool forceBold = false)
    {
        var builder = new StringBuilder();
        foreach (var run in runs)
        {
            if (run.IsBreak)
            {
                builder.Append("\\line ");
                continue;
            }
            var open = new StringBuilder();
            if (forceBold || run.Style.HasFlag(RunStyle.Bold)) open.Append("\\b");
            if (run.Style.HasFlag(RunStyle.Italic)) open.Append("\\i");
            if (run.Style.HasFlag(RunStyle.Underline)) open.Append("\\ul");
            if (run.Style.HasFlag(RunStyle.Strike)) open.Append("\\strike");
            if (run.Style.HasFlag(RunStyle.Code)) open.Append("\\f1\\fs20");
            var body = open.Length > 0 ? "{" + open + " " + Escape(run.Text) + "}" : Escape(run.Text);
            if (run.Link != null)
                body = "{\\field{\\*\\fldinst{HYPERLINK \"" + run.Link.Replace("\"", "%22").Replace("\\", "\\\\") + "\"}}{\\fldrslt{\\ul\\cf1 " + Escape(run.Text) + "}}}";
            builder.Append(body);
        }
        return builder.ToString();
    }

    public static string Write(Doc doc)
    {
        var b = new StringBuilder();
        b.Append("{\\rtf1\\ansi\\ansicpg1252\\deff0\\uc1{\\fonttbl{\\f0\\fswiss\\fcharset0 Segoe UI;}{\\f1\\fmodern\\fcharset0 Consolas;}}");
        b.Append("{\\colortbl ;\\red5\\green99\\blue193;\\red89\\green89\\blue89;}");
        b.Append("{\\info{\\title ").Append(Escape(doc.Title)).Append("}}");
        b.Append("\\viewkind4\\f0\\fs22\n");
        foreach (var block in doc.Blocks)
        {
            switch (block)
            {
                case HeadingBlock heading:
                {
                    var level = Math.Clamp(heading.Level, 1, 6);
                    var size = level switch { 1 => 36, 2 => 30, 3 => 26, 4 => 24, _ => 22 };
                    b.Append($"\\pard\\sb240\\sa80\\keepn\\outlinelevel{level - 1}\\b\\fs{size} ").Append(Runs(heading.Runs)).Append("\\b0\\fs22\\par\n");
                    break;
                }
                case ParagraphBlock paragraph:
                    b.Append("\\pard\\sa160\\sl276\\slmult1 ").Append(Runs(paragraph.Runs)).Append("\\par\n");
                    break;
                case QuoteBlock quote:
                    b.Append("\\pard\\li567\\sa160\\i\\cf2 ").Append(Runs(quote.Runs)).Append("\\cf0\\i0\\par\n");
                    break;
                case CodeBlock code:
                    b.Append("\\pard\\sa160\\f1\\fs19 ").Append(Escape(code.Text.Replace("\r", ""))).Append("\\f0\\fs22\\par\n");
                    break;
                case RuleBlock:
                    b.Append("\\pard\\brdrb\\brdrs\\brdrw10\\brsp20\\sa160\\par\n");
                    break;
                case ListBlock list:
                {
                    var counters = new int[10];
                    foreach (var entry in list.Entries)
                    {
                        var level = Math.Clamp(entry.Level, 0, 9);
                        counters[level]++;
                        for (var deeper = level + 1; deeper < counters.Length; deeper++) counters[deeper] = 0;
                        var marker = list.Ordered ? counters[level] + "." : "\\bullet";
                        b.Append($"\\pard\\fi-360\\li{720 + level * 360}\\sa60 {marker}\\tab ").Append(Runs(entry.Runs)).Append("\\par\n");
                    }
                    break;
                }
                case TableBlock table:
                {
                    var columns = table.Rows.Max(row => row.Count);
                    if (columns == 0) break;
                    var width = 9000 / columns;
                    for (var row = 0; row < table.Rows.Count; row++)
                    {
                        b.Append("\\trowd\\trgaph108");
                        for (var column = 0; column < columns; column++)
                            b.Append("\\clbrdrt\\brdrs\\brdrw10\\clbrdrl\\brdrs\\brdrw10\\clbrdrb\\brdrs\\brdrw10\\clbrdrr\\brdrs\\brdrw10\\cellx").Append(width * (column + 1));
                        b.Append('\n');
                        for (var column = 0; column < columns; column++)
                        {
                            var cell = column < table.Rows[row].Count ? table.Rows[row][column] : [];
                            b.Append("\\pard\\intbl ").Append(Runs(cell, forceBold: row == 0)).Append("\\cell ");
                        }
                        b.Append("\\row\n");
                    }
                    b.Append("\\pard\\par\n");
                    break;
                }
                case ImageBlock image:
                {
                    var (data, ext) = Docx.WordImage(image);
                    if (ext is not ("png" or "jpg")) break;
                    var w = image.Width > 0 ? image.Width : 600;
                    var h = image.Height > 0 ? image.Height : 400;
                    long tw = w * 15L, th = h * 15L;
                    if (tw > 9000)
                    {
                        th = th * 9000 / tw;
                        tw = 9000;
                    }
                    b.Append("\\pard\\sa160{\\pict").Append(ext == "png" ? "\\pngblip" : "\\jpegblip")
                        .Append($"\\picw{w}\\pich{h}\\picwgoal{tw}\\pichgoal{th}\n");
                    var hex = Convert.ToHexString(data);
                    for (var index = 0; index < hex.Length; index += 128) b.Append(hex, index, Math.Min(128, hex.Length - index)).Append('\n');
                    b.Append("}\\par\n");
                    break;
                }
            }
        }
        b.Append('}');
        return b.ToString();
    }
}
