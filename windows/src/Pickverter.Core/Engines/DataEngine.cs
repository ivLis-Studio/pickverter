using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pickverter;

/// Tables (CSV/TSV/JSON), subtitles and JSON formatting — plain text transforms, no dependencies.
public static partial class DataEngine
{
    // Tables

    public static List<List<string>> ParseDelimited(string text, char delimiter)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var start = text.Length > 0 && text[0] == '\uFEFF' ? 1 : 0;
        for (var index = start; index < text.Length; index++)
        {
            var c = text[index];
            if (quoted)
            {
                if (c == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"' && field.Length == 0)
            {
                quoted = true;
            }
            else if (c == delimiter)
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                row.Add(field.ToString());
                rows.Add(row);
                row = [];
                field.Clear();
            }
            else
            {
                field.Append(c);
            }
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows.Where(r => !(r.Count == 1 && r[0].Length == 0)).ToList();
    }

    public static List<List<string>> ReadTable(string path)
    {
        var ext = FileNames.NormalizedExt(path);
        var text = FileSystem.ReadText(path);
        if (ext == "json")
        {
            JsonDocument json;
            try
            {
                json = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            }
            catch (JsonException)
            {
                throw new PickException(L("err.json_invalid"));
            }
            using (json)
            {
                var root = json.RootElement;
                JsonElement? array = null;
                if (root.ValueKind == JsonValueKind.Array) array = root;
                else if (root.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in root.EnumerateObject())
                    {
                        if (property.Value.ValueKind == JsonValueKind.Array && property.Value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.Object))
                        {
                            array = property.Value;
                            break;
                        }
                    }
                }
                if (array is not { } records || records.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.Object))
                    throw new PickException(L("err.json_table"));
                var keys = new List<string>();
                foreach (var record in records.EnumerateArray())
                {
                    foreach (var property in record.EnumerateObject())
                    {
                        if (!keys.Contains(property.Name)) keys.Add(property.Name);
                    }
                }
                var rows = new List<List<string>> { keys };
                foreach (var record in records.EnumerateArray())
                {
                    rows.Add(keys.Select(key => record.TryGetProperty(key, out var value) ? Stringify(value) : "").ToList());
                }
                return rows;
            }
        }
        var sample = text.Length > 2000 ? text[..2000] : text;
        var delimiter = ext == "tsv" ? '\t' : sample.Count(c => c == ';') > sample.Count(c => c == ',') ? ';' : ',';
        return ParseDelimited(text, delimiter);
    }

    public static string Stringify(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => value.GetRawText(),
    };

    private static readonly JsonWriterOptions PrettyWriter = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string WriteTable(IReadOnlyList<IReadOnlyList<string>> rows, string ext)
    {
        var width = rows.Count == 0 ? 0 : rows.Max(row => row.Count);
        var padded = rows.Select(row => row.Concat(Enumerable.Repeat("", width - row.Count)).ToList()).ToList();
        switch (ext)
        {
            case "csv":
            case "tsv":
            {
                var separator = ext == "csv" ? "," : "\t";
                return string.Join("\n", padded.Select(row => string.Join(separator, row.Select(cell =>
                {
                    var needsQuotes = cell.Contains(separator) || cell.Contains('"') || cell.Contains('\n') || cell.Contains('\r');
                    return needsQuotes ? "\"" + cell.Replace("\"", "\"\"") + "\"" : cell;
                })))) + "\n";
            }
            case "json":
            {
                if (padded.Count == 0) return "[]\n";
                var keys = Uniqued(padded[0]);
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream, PrettyWriter))
                {
                    writer.WriteStartArray();
                    foreach (var row in padded.Skip(1))
                    {
                        writer.WriteStartObject();
                        for (var index = 0; index < keys.Count; index++) writer.WriteString(keys[index], row[index]);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
            }
            case "md":
            {
                if (padded.Count == 0) return "";
                string Line(IEnumerable<string> cells) =>
                    "| " + string.Join(" | ", cells.Select(cell => cell.Replace("|", "\\|").Replace("\r", "").Replace("\n", " "))) + " |";
                var lines = new List<string> { Line(padded[0]), "|" + string.Concat(Enumerable.Repeat(" --- |", padded[0].Count)) };
                lines.AddRange(padded.Skip(1).Select(Line));
                return string.Join("\n", lines) + "\n";
            }
            case "html":
            {
                if (padded.Count == 0) return "";
                var head = string.Concat(padded[0].Select(cell => "<th>" + Html.Escape(cell) + "</th>"));
                var body = string.Join("\n", padded.Skip(1).Select(row => "<tr>" + string.Concat(row.Select(cell => "<td>" + Html.Escape(cell) + "</td>")) + "</tr>"));
                return "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>\n"
                    + "body { font-family: \"Segoe UI Variable Text\", \"Segoe UI\", system-ui, sans-serif; font-size: 13px; }\n"
                    + "table { border-collapse: collapse; } th, td { border: 1px solid #d0d0d5; padding: 5px 9px; text-align: left; }\n"
                    + "th { background: #f2f2f5; } tr:nth-child(even) td { background: #fafafc; }\n"
                    + "</style></head><body><table><tr>" + head + "</tr>\n" + body + "\n</table></body></html>\n";
            }
            default:
                throw new PickException(L("err.cant_save_as", ext.ToUpperInvariant()));
        }
    }

    public static List<string> Uniqued(IReadOnlyList<string> header)
    {
        var seen = new Dictionary<string, int>();
        var result = new List<string>();
        for (var index = 0; index < header.Count; index++)
        {
            var name = header[index].Length == 0 ? "column" + (index + 1) : header[index];
            var n = seen.GetValueOrDefault(name);
            seen[name] = n + 1;
            result.Add(n == 0 ? name : name + "_" + (n + 1));
        }
        return result;
    }

    public static async Task<string> ConvertTable(string src, string ext, PickContext ctx)
    {
        var rows = ReadTable(src);
        if (rows.Count == 0) throw new PickException(L("err.table_empty"));
        var text = WriteTable(rows, ext);
        return await ctx.Produce(ctx.DestinationFor(src, ext), tmp => FileSystem.WriteText(tmp, text));
    }

    // JSON formatting (keeps key order, no re-serialization)

    public static string Reformat(string text, bool pretty)
    {
        var output = new StringBuilder(text.Length);
        var depth = 0;
        var inString = false;
        var escaped = false;
        char? previous = null;
        void Newline() => output.Append('\n').Append(' ', depth * 2);
        foreach (var c in text)
        {
            if (inString)
            {
                output.Append(c);
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                previous = c;
                continue;
            }
            if (char.IsWhiteSpace(c) || c == '\uFEFF') continue;
            switch (c)
            {
                case '"':
                    inString = true;
                    output.Append(c);
                    break;
                case '{':
                case '[':
                    output.Append(c);
                    depth++;
                    if (pretty) Newline();
                    break;
                case '}':
                case ']':
                    depth--;
                    if (pretty)
                    {
                        if (previous is '{' or '[')
                        {
                            while (output.Length > 0 && char.IsWhiteSpace(output[^1])) output.Length--;
                        }
                        else
                        {
                            Newline();
                        }
                    }
                    output.Append(c);
                    break;
                case ',':
                    output.Append(c);
                    if (pretty) Newline();
                    break;
                case ':':
                    output.Append(pretty ? ": " : ":");
                    break;
                default:
                    output.Append(c);
                    break;
            }
            previous = c;
        }
        if (pretty) output.Append('\n');
        return output.ToString();
    }

    public static async Task<string> ReformatJson(string src, bool pretty, PickContext ctx)
    {
        var text = FileSystem.ReadText(src);
        try
        {
            using var _ = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            throw new PickException(L("err.json_invalid"));
        }
        var output = Reformat(text, pretty);
        return await ctx.Produce(ctx.DestinationFor(src, "json", pretty ? L("sfx.pretty") : L("sfx.min")), tmp => FileSystem.WriteText(tmp, output));
    }

    // Subtitles

    public sealed record Cue(double Start, double End, string Text);

    public static double? ParseTime(string raw)
    {
        var parts = raw.Trim().Replace(',', '.').Split(':');
        if (parts.Length is < 2 or > 3) return null;
        double seconds = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return null;
            seconds = seconds * 60 + value;
        }
        return seconds;
    }

    public static List<Cue> ParseSubtitles(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var cues = new List<Cue>();
        foreach (var block in normalized.Split("\n\n"))
        {
            var lines = block.Split('\n').Where(line => line.Trim().Length > 0).ToList();
            var timing = lines.FindIndex(line => line.Contains("-->"));
            if (timing < 0) continue;
            var sides = lines[timing].Split("-->");
            if (sides.Length != 2) continue;
            var startTime = ParseTime(sides[0]);
            var endTime = ParseTime(sides[1].Trim().Split(' ')[0]);
            if (startTime is not { } start || endTime is not { } end) continue;
            cues.Add(new Cue(start, end, string.Join("\n", lines.Skip(timing + 1))));
        }
        return cues;
    }

    public static string Stamp(double seconds, string separator)
    {
        var total = (long)Math.Round(seconds * 1000);
        return $"{total / 3_600_000:00}:{total / 60_000 % 60:00}:{total / 1000 % 60:00}{separator}{total % 1000:000}";
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagPattern();

    public static async Task<string> ConvertSubtitles(string src, string ext, PickContext ctx)
    {
        var cues = ParseSubtitles(FileSystem.ReadText(src));
        if (cues.Count == 0) throw new PickException(L("err.no_cues"));
        var output = ext switch
        {
            "srt" => string.Join("\n\n", cues.Select((cue, i) => $"{i + 1}\n{Stamp(cue.Start, ",")} --> {Stamp(cue.End, ",")}\n{cue.Text}")) + "\n",
            "vtt" => "WEBVTT\n\n" + string.Join("\n\n", cues.Select(cue => $"{Stamp(cue.Start, ".")} --> {Stamp(cue.End, ".")}\n{cue.Text}")) + "\n",
            _ => string.Join("\n", cues.Select(cue => TagPattern().Replace(cue.Text, ""))) + "\n",
        };
        return await ctx.Produce(ctx.DestinationFor(src, ext), tmp => FileSystem.WriteText(tmp, output));
    }
}

/// Explorer-style ordering: "img2" before "img10", case-insensitive.
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? a, string? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a == null) return -1;
        if (b == null) return 1;
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                var na = a[si..i].TrimStart('0');
                var nb = b[sj..j].TrimStart('0');
                if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                var cmp = string.CompareOrdinal(na, nb);
                if (cmp != 0) return cmp;
                continue;
            }
            var ca = a[i];
            var cb = b[j];
            var text = string.Compare(ca.ToString(), cb.ToString(), CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);
            if (text != 0) return text;
            i++;
            j++;
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }

    public static List<string> SortByName(IEnumerable<string> paths) =>
        paths.OrderBy(FileNames.Name, Instance).ToList();
}
