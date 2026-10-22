using System.Xml;

namespace Pickverter;

public static class Xml
{
    /// Text safe inside XML elements and attributes (invalid control characters dropped).
    public static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case '&': builder.Append("&amp;"); break;
                case '<': builder.Append("&lt;"); break;
                case '>': builder.Append("&gt;"); break;
                case '"': builder.Append("&quot;"); break;
                case '\'': builder.Append("&apos;"); break;
                default:
                    if (c >= 0x20 || c is '\t' or '\n' or '\r') builder.Append(c);
                    break;
            }
        }
        return builder.ToString();
    }

    /// Tiny SAX-style walk. Element names are reported without their namespace prefix ("a:t" -> "t").
    public static void Walk(Stream stream, Action<string, IReadOnlyDictionary<string, string>>? start = null, Action<string>? end = null,
                            Action<string>? text = null)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreComments = true, XmlResolver = null };
        using var reader = XmlReader.Create(stream, settings);
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                {
                    var name = reader.LocalName;
                    var attributes = new Dictionary<string, string>();
                    if (reader.HasAttributes)
                    {
                        while (reader.MoveToNextAttribute())
                        {
                            attributes[reader.Name] = reader.Value;
                            attributes.TryAdd(reader.LocalName, reader.Value);
                        }
                        reader.MoveToElement();
                    }
                    var empty = reader.IsEmptyElement;
                    start?.Invoke(name, attributes);
                    if (empty) end?.Invoke(name);
                    break;
                }
                case XmlNodeType.EndElement:
                    end?.Invoke(reader.LocalName);
                    break;
                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                case XmlNodeType.SignificantWhitespace:
                case XmlNodeType.Whitespace:
                    text?.Invoke(reader.Value);
                    break;
            }
        }
    }

    public static void Walk(string path, Action<string, IReadOnlyDictionary<string, string>>? start = null, Action<string>? end = null,
                            Action<string>? text = null)
    {
        if (!File.Exists(path)) return;
        using var stream = File.OpenRead(path);
        try
        {
            Walk(stream, start, end, text);
        }
        catch (XmlException)
        {
        }
    }
}
