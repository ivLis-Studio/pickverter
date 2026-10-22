using System.Reflection;
using System.Text.Json;

namespace Pickverter;

/// Runtime localization backed by the shared i18n.json plus the Windows overlay, so the language
/// can switch without a restart.
public static class Loc
{
    public sealed record UiLanguage(string Code, string Name);

    public static readonly IReadOnlyList<UiLanguage> Languages =
    [
        new("en", "English"),
        new("ko", "한국어"),
        new("ja", "日本語"),
        new("zh-Hans", "简体中文"),
        new("zh-Hant", "繁體中文"),
        new("es", "Español"),
        new("fr", "Français"),
        new("de", "Deutsch"),
        new("ru", "Русский"),
        new("pt-BR", "Português (Brasil)"),
    ];

    private sealed class Table
    {
        public Dictionary<string, int> Columns { get; } = new();
        public Dictionary<string, string[]> Strings { get; } = new();
    }

    private static readonly object Gate = new();
    private static string current = InitialLanguage();
    private static readonly Lazy<Table> table = new(LoadTable);

    public static event Action? Changed;

    public static string Language
    {
        get { lock (Gate) return current; }
    }

    public static CultureInfo Culture
    {
        get
        {
            try { return CultureInfo.GetCultureInfo(Language); } catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
        }
    }

    /// "system" follows Windows; anything else is a code from Languages.
    public static void Apply(string preference)
    {
        var resolved = preference == "system" ? SystemLanguage() : preference;
        bool changed;
        lock (Gate)
        {
            changed = current != resolved;
            current = resolved;
        }
        if (changed) Changed?.Invoke();
    }

    public static string SystemLanguage() =>
        Resolve([CultureInfo.CurrentUICulture.Name, CultureInfo.InstalledUICulture.Name]);

    public static string Resolve(IEnumerable<string> identifiers)
    {
        foreach (var raw in identifiers)
        {
            if (string.IsNullOrEmpty(raw)) continue;
            var id = raw.ToLowerInvariant().Replace('_', '-');
            if (id.StartsWith("zh", StringComparison.Ordinal))
            {
                var traditional = id.Contains("hant") || id.EndsWith("-tw") || id.EndsWith("-hk") || id.EndsWith("-mo");
                return traditional ? "zh-Hant" : "zh-Hans";
            }
            if (id.StartsWith("pt", StringComparison.Ordinal)) return "pt-BR";
            var code = id.Length >= 2 ? id[..2] : id;
            if (Languages.Any(language => language.Code == code)) return code;
        }
        return "en";
    }

    private static string InitialLanguage()
    {
        var forced = Environment.GetEnvironmentVariable("PICKVERTER_LANG");
        if (!string.IsNullOrEmpty(forced)) return forced;
        return SystemLanguage();
    }

    private static Table LoadTable()
    {
        var result = new Table();
        var assembly = typeof(Loc).Assembly;
        Merge(result, assembly, "i18n.json");
        Merge(result, assembly, "i18n.windows.json");
        return result;
    }

    private static void Merge(Table table, Assembly assembly, string resource)
    {
        using var stream = assembly.GetManifestResourceStream(resource);
        if (stream == null) return;
        using var json = JsonDocument.Parse(stream);
        var codes = json.RootElement.GetProperty("languages").EnumerateArray().Select(element => element.GetString() ?? "").ToArray();
        if (table.Columns.Count == 0)
        {
            for (var index = 0; index < codes.Length; index++) table.Columns[codes[index]] = index;
        }
        foreach (var entry in json.RootElement.GetProperty("strings").EnumerateObject())
        {
            var values = entry.Value.EnumerateArray().Select(element => element.GetString() ?? "").ToArray();
            if (!table.Strings.TryGetValue(entry.Name, out var row))
            {
                row = new string[table.Columns.Count];
                table.Strings[entry.Name] = row;
            }
            // The overlay may list languages in another order: map by code, keep base values where the overlay is empty.
            for (var index = 0; index < codes.Length && index < values.Length; index++)
            {
                if (values[index].Length == 0 || !table.Columns.TryGetValue(codes[index], out var column) || column >= row.Length) continue;
                row[column] = values[index];
            }
        }
    }

    public static string Template(string key)
    {
        var data = table.Value;
        if (!data.Strings.TryGetValue(key, out var row)) return key;
        if (data.Columns.TryGetValue(Language, out var index) && index < row.Length && !string.IsNullOrEmpty(row[index])) return row[index];
        if (data.Columns.TryGetValue("en", out var english) && english < row.Length && row[english] != null) return row[english];
        return key;
    }

    public static bool HasKey(string key) => table.Value.Strings.ContainsKey(key);

    public static IReadOnlyCollection<string> Keys => table.Value.Strings.Keys;

    /// Every language's value for a key (tests and the overlay checker).
    public static string? Value(string key, string language)
    {
        var data = table.Value;
        if (!data.Strings.TryGetValue(key, out var row) || !data.Columns.TryGetValue(language, out var index) || index >= row.Length) return null;
        return row[index];
    }

    public static int PluralIndex(int count, int forms)
    {
        if (forms <= 1) return 0;
        switch (Language)
        {
            case "ru":
                if (count % 10 == 1 && count % 100 != 11) return 0;
                if (count % 10 is >= 2 and <= 4 && count % 100 is not (>= 12 and <= 14)) return Math.Min(1, forms - 1);
                return Math.Min(2, forms - 1);
            case "fr":
            case "pt-BR":
                return count <= 1 ? 0 : 1;
            default:
                return count == 1 ? 0 : 1;
        }
    }

    /// %@ takes the next argument, %1$@ a numbered one, %% is a literal percent sign.
    public static string Format(string template, IReadOnlyList<string> args)
    {
        var builder = new StringBuilder(template.Length + 16);
        var sequential = 0;
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c == '%' && i + 1 < template.Length)
            {
                var next = template[i + 1];
                if (next == '%')
                {
                    builder.Append('%');
                    i++;
                    continue;
                }
                if (next == '@')
                {
                    builder.Append(sequential < args.Count ? args[sequential] : "");
                    sequential++;
                    i++;
                    continue;
                }
                if (char.IsAsciiDigit(next))
                {
                    var j = i + 1;
                    var number = 0;
                    while (j < template.Length && char.IsAsciiDigit(template[j]))
                    {
                        number = number * 10 + (template[j] - '0');
                        j++;
                    }
                    if (j + 1 < template.Length && template[j] == '$' && template[j + 1] == '@')
                    {
                        var index = number - 1;
                        builder.Append(index >= 0 && index < args.Count ? args[index] : "");
                        i = j + 1;
                        continue;
                    }
                }
            }
            builder.Append(c);
        }
        return builder.ToString();
    }

    public static string FormatNumber(long value) => value.ToString("N0", Culture);
}

public static class Strings
{
    /// Localized string. Placeholders are %@ (or %1$@, %2$@ for reordering).
    public static string L(string key, params string[] args)
    {
        var template = Loc.Template(key);
        return args.Length == 0 ? template : Loc.Format(template, args);
    }

    /// Pluralized string. Forms are separated by "|" in the table; %1$@ is the formatted count.
    public static string LP(string key, int count, params string[] args)
    {
        var forms = Loc.Template(key).Split('|');
        var chosen = forms[Math.Min(Loc.PluralIndex(count, forms.Length), forms.Length - 1)];
        var all = new string[args.Length + 1];
        all[0] = Loc.FormatNumber(count);
        Array.Copy(args, 0, all, 1, args.Length);
        return Loc.Format(chosen, all);
    }
}
