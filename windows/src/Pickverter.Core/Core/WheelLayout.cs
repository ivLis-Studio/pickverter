using System.ComponentModel;
using System.Text.Json;

namespace Pickverter;

/// The user's own wheel: hidden slots, custom order, variants pinned to the main ring and which option a slot
/// runs by default. Order and pins are kept per ring and file type ("tools|image").
public sealed class WheelLayout : INotifyPropertyChanged
{
    private sealed class Data
    {
        public Dictionary<string, string> Hidden { get; set; } = new();
        public Dictionary<string, List<string>> Orders { get; set; } = new();
        public Dictionary<string, List<string>> Pinned { get; set; } = new();
        public Dictionary<string, string> PinnedTitles { get; set; } = new();
        public Dictionary<string, string> Defaults { get; set; } = new();
        public Dictionary<string, string> LastUsed { get; set; } = new();
        public bool RemembersLast { get; set; } = true;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string? path;
    private Data data = new();

    public static WheelLayout Shared { get; private set; } = new(null);

    public event PropertyChangedEventHandler? PropertyChanged;

    private WheelLayout(string? path)
    {
        this.path = path;
        if (path == null || !File.Exists(path)) return;
        try
        {
            data = JsonSerializer.Deserialize<Data>(File.ReadAllText(path), Json) ?? new Data();
        }
        catch (Exception)
        {
            data = new Data();
        }
    }

    public static WheelLayout Load(string? path = null)
    {
        Shared = new WheelLayout(path ?? Path.Combine(Platform.Paths.Settings, "wheel.json"));
        return Shared;
    }

    public static WheelLayout UseTransient()
    {
        Shared = new WheelLayout(null);
        return Shared;
    }

    private void Save()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        if (path == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(data, Json));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception error)
        {
            Debug.Log("wheel layout not saved: " + error.Message);
        }
    }

    public IReadOnlyDictionary<string, string> Hidden => data.Hidden;
    public IReadOnlyDictionary<string, List<string>> Orders => data.Orders;
    public IReadOnlyDictionary<string, List<string>> Pinned => data.Pinned;
    public IReadOnlyDictionary<string, string> Defaults => data.Defaults;
    public IReadOnlyDictionary<string, string> LastUsed => data.LastUsed;

    public bool RemembersLast
    {
        get => data.RemembersLast;
        set
        {
            if (data.RemembersLast == value) return;
            data.RemembersLast = value;
            Save();
        }
    }

    public static string Key(WheelMode mode, IEnumerable<string> files)
    {
        var kinds = string.Join("+", files.Select(file => FileKinds.Of(file).Raw()).Distinct().OrderBy(raw => raw, StringComparer.Ordinal));
        return mode.Raw() + "|" + kinds;
    }

    public bool IsCustomized => data.Hidden.Count > 0 || data.Orders.Count > 0 || data.Pinned.Count > 0 || data.Defaults.Count > 0;

    public bool IsCustomizedKey(string key) => data.Orders.ContainsKey(key) || data.Pinned.ContainsKey(key);

    /// Localized name for an id when one exists, so lists in Settings follow the UI language.
    public string Title(string id, string? fallback)
    {
        if (id.StartsWith("convert.", StringComparison.Ordinal)) return Catalog.FormatLabel(id["convert.".Length..]);
        if (Loc.HasKey(id)) return L(id);
        return fallback ?? id;
    }

    public List<PickOption> Arrange(IReadOnlyList<PickOption> options, string key)
    {
        var pins = data.Pinned.TryGetValue(key, out var pinnedList) ? pinnedList : [];
        var list = new List<PickOption>();
        foreach (var option in options)
        {
            list.Add(option);
            foreach (var variant in option.Variants)
            {
                if (pins.Contains(variant.Id)) list.Add(WheelModel.Display(variant, option) with { PinnedFrom = option.Id });
            }
        }
        list = list.Where(option => !data.Hidden.ContainsKey(option.Id)).ToList();
        if (!data.Orders.TryGetValue(key, out var order) || order.Count == 0) return list;
        var rank = new Dictionary<string, int>();
        for (var index = 0; index < order.Count; index++) rank.TryAdd(order[index], index);
        var result = list.Where(option => rank.ContainsKey(option.Id)).OrderBy(option => rank[option.Id]).ToList();
        // Slots the saved order doesn't know yet (new pins, new features) go right after their natural neighbour.
        for (var index = 0; index < list.Count; index++)
        {
            var option = list[index];
            if (rank.ContainsKey(option.Id)) continue;
            var neighbour = list.Take(index).LastOrDefault(candidate => result.Any(entry => entry.Id == candidate.Id));
            var position = neighbour == null ? 0 : result.FindIndex(entry => entry.Id == neighbour.Id) + 1;
            result.Insert(position, option);
        }
        return result;
    }

    /// Index of the variant a click on the slot runs.
    public int PreferredVariant(PickOption option)
    {
        if (option.Variants.Count == 0) return 0;
        if (data.Defaults.TryGetValue(option.Id, out var id))
        {
            var index = IndexOf(option.Variants, id);
            if (index >= 0) return index;
        }
        if (data.RemembersLast && data.LastUsed.TryGetValue(option.Id, out var last))
        {
            var index = IndexOf(option.Variants, last);
            if (index >= 0) return index;
        }
        return 0;
    }

    private static int IndexOf(IReadOnlyList<PickOption> options, string id)
    {
        for (var index = 0; index < options.Count; index++)
        {
            if (options[index].Id == id) return index;
        }
        return -1;
    }

    public void NoteUsed(string slot, string variant)
    {
        if (!data.RemembersLast || (data.LastUsed.TryGetValue(slot, out var current) && current == variant)) return;
        data.LastUsed[slot] = variant;
        Save();
    }

    public void Hide(string id, string title)
    {
        data.Hidden[id] = title;
        Save();
    }

    public void Unhide(string id)
    {
        data.Hidden.Remove(id);
        Save();
    }

    public void SetDefault(string slot, string? variant)
    {
        if (variant == null) data.Defaults.Remove(slot); else data.Defaults[slot] = variant;
        Save();
    }

    public void Pin(string variant, string title, string key)
    {
        if (!data.Pinned.TryGetValue(key, out var list))
        {
            list = [];
            data.Pinned[key] = list;
        }
        if (list.Contains(variant)) return;
        list.Add(variant);
        data.PinnedTitles[variant] = title;
        Save();
    }

    public void Unpin(string variant, string? key = null)
    {
        foreach (var ring in data.Pinned.Keys.ToList())
        {
            if (key != null && ring != key) continue;
            data.Pinned[ring].Remove(variant);
            if (data.Pinned[ring].Count == 0) data.Pinned.Remove(ring);
        }
        if (!data.Pinned.Values.Any(list => list.Contains(variant))) data.PinnedTitles.Remove(variant);
        Save();
    }

    public bool IsPinned(string variant, string key) => data.Pinned.TryGetValue(key, out var list) && list.Contains(variant);

    /// Moves one slot earlier or later within the ring as it is currently shown.
    public void Move(string id, int step, IReadOnlyList<string> visible, string key)
    {
        var ids = visible.ToList();
        var from = ids.IndexOf(id);
        if (from < 0) return;
        var to = Math.Clamp(from + step, 0, ids.Count - 1);
        if (to == from) return;
        (ids[from], ids[to]) = (ids[to], ids[from]);
        data.Orders[key] = ids;
        Save();
    }

    public void Reset(string key)
    {
        data.Orders.Remove(key);
        if (data.Pinned.TryGetValue(key, out var pins))
        {
            foreach (var variant in pins.ToList()) Unpin(variant, key);
        }
        data.Pinned.Remove(key);
        Save();
    }

    public void ResetAll()
    {
        var remember = data.RemembersLast;
        data = new Data { RemembersLast = remember };
        Save();
    }

    /// Every pinned variant with its title, for Settings.
    public IReadOnlyList<(string Id, string Title)> PinnedList =>
        data.Pinned.Values.SelectMany(list => list).Distinct().OrderBy(id => id, StringComparer.Ordinal)
            .Select(id => (id, data.PinnedTitles.TryGetValue(id, out var title) ? title : id)).ToList();
}
