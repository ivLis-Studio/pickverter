using System.ComponentModel;
using System.Text.Json;

namespace Pickverter;

/// The user's own wheel: custom order, variants pinned to the main ring, slots taken out of a ring or brought in from
/// the other two, and which option a slot runs by default. Everything but the defaults is kept per ring and file type
/// ("tools|image"); slots hidden in every ring come from earlier versions and are still honoured.
public sealed class WheelLayout : INotifyPropertyChanged
{
    private sealed class Data
    {
        /// id -> title at the time it was hidden, in every ring (earlier versions; slots now leave one ring at a time).
        public Dictionary<string, string> Hidden { get; set; } = new();
        public Dictionary<string, List<string>> Orders { get; set; } = new();
        public Dictionary<string, List<string>> Pinned { get; set; } = new();
        public Dictionary<string, string> PinnedTitles { get; set; } = new();
        /// key -> slots taken out of that ring for that file type.
        public Dictionary<string, List<string>> Removed { get; set; } = new();
        /// key -> slots (or options of slots) brought in from the other two rings, in the order they were added.
        public Dictionary<string, List<string>> Added { get; set; } = new();
        public Dictionary<string, string> Defaults { get; set; } = new();
        public Dictionary<string, string> LastUsed { get; set; } = new();
        public bool RemembersLast { get; set; } = true;

        /// A file from an earlier version has no Removed or Added; one edited by hand may hold nulls or empty lists.
        public Data Normalized()
        {
            Hidden = OrEmpty(Hidden);
            Orders = Cleaned(Orders);
            Pinned = Cleaned(Pinned);
            PinnedTitles = OrEmpty(PinnedTitles);
            Removed = Cleaned(Removed);
            Added = Cleaned(Added);
            Defaults = OrEmpty(Defaults);
            LastUsed = OrEmpty(LastUsed);
            return this;
        }

        private static Dictionary<string, string> OrEmpty(Dictionary<string, string>? map) => map ?? new();

        private static Dictionary<string, List<string>> Cleaned(Dictionary<string, List<string>>? map)
        {
            var result = new Dictionary<string, List<string>>();
            if (map == null) return result;
            foreach (var (key, list) in map)
            {
                var ids = (list ?? []).Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();
                if (ids.Count > 0) result[key] = ids;
            }
            return result;
        }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string? path;
    private Data data = new();

    public static WheelLayout Shared { get; private set; } = new(null);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// The file types Settings › Wheel offers, in menu order. "Use for All File Types" writes the ring for each.
    public static readonly IReadOnlyList<FileKind> Kinds =
    [
        FileKind.Image, FileKind.Video, FileKind.Audio, FileKind.Pdf, FileKind.Document, FileKind.Hwp, FileKind.Office,
        FileKind.Table, FileKind.Json, FileKind.Subtitle, FileKind.Archive, FileKind.Folder, FileKind.Other,
    ];

    private WheelLayout(string? path)
    {
        this.path = path;
        if (path == null || !File.Exists(path)) return;
        try
        {
            data = (JsonSerializer.Deserialize<Data>(File.ReadAllText(path), Json) ?? new Data()).Normalized();
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
    public IReadOnlyDictionary<string, List<string>> Removed => data.Removed;
    public IReadOnlyDictionary<string, List<string>> Added => data.Added;
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

    /// One ring for one file type, as Settings › Wheel edits it ("tools|image").
    public static string Key(WheelMode mode, FileKind kind) => mode.Raw() + "|" + kind.Raw();

    public bool IsCustomized => data.Hidden.Count > 0 || data.Orders.Count > 0 || data.Pinned.Count > 0 || data.Defaults.Count > 0 ||
                                data.Removed.Count > 0 || data.Added.Count > 0;

    public bool IsCustomizedKey(string key) =>
        data.Orders.ContainsKey(key) || data.Pinned.ContainsKey(key) || data.Removed.ContainsKey(key) || data.Added.ContainsKey(key);

    /// Localized name for an id when one exists, so lists in Settings follow the UI language.
    public string Title(string id, string? fallback)
    {
        if (id.StartsWith("convert.", StringComparison.Ordinal)) return Catalog.FormatLabel(id["convert.".Length..]);
        if (Loc.HasKey(id)) return L(id);
        return fallback ?? id;
    }

    /// The ring as the wheel shows it. options: the ring's own slots for the files; pool: the other two rings' slots for
    /// the same files, where slots brought in from them come from.
    public List<PickOption> Arrange(IReadOnlyList<PickOption> options, string key, IReadOnlyList<PickOption>? pool = null)
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
        if (pool != null && data.Added.TryGetValue(key, out var added))
        {
            foreach (var id in added)
            {
                if (list.Any(option => option.Id == id)) continue;
                if (Find(id, pool) is { } found) list.Add(found);
            }
        }
        var removed = data.Removed.TryGetValue(key, out var removedList) ? removedList : [];
        list = list.Where(option => !data.Hidden.ContainsKey(option.Id) && !removed.Contains(option.Id)).ToList();
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

    /// A slot as it is, or one of a slot's options shown as a slot of its own ("Rotate · Left 90°").
    public static PickOption? Find(string id, IReadOnlyList<PickOption> options)
    {
        foreach (var option in options)
        {
            if (option.Id == id) return option;
        }
        foreach (var parent in options)
        {
            foreach (var variant in parent.Variants)
            {
                if (variant.Id == id) return WheelModel.Display(variant, parent);
            }
        }
        return null;
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
        Unpinned(variant, key);
        Save();
    }

    private void Unpinned(string variant, string? key)
    {
        foreach (var ring in data.Pinned.Keys.ToList())
        {
            if (key != null && ring != key) continue;
            data.Pinned[ring].Remove(variant);
            if (data.Pinned[ring].Count == 0) data.Pinned.Remove(ring);
        }
        if (!data.Pinned.Values.Any(list => list.Contains(variant))) data.PinnedTitles.Remove(variant);
    }

    public bool IsPinned(string variant, string key) => data.Pinned.TryGetValue(key, out var list) && list.Contains(variant);

    public bool IsAdded(string id, string key) => data.Added.TryGetValue(key, out var list) && list.Contains(id);

    /// Takes a slot out of one ring for one file type: a slot brought in from another ring goes back, a pinned option is
    /// unpinned, and any other slot is set aside (Add Slot in Settings › Wheel brings it back).
    public void Remove(string id, string key)
    {
        if (data.Added.TryGetValue(key, out var added) && added.Remove(id))
        {
            if (added.Count == 0) data.Added.Remove(key);
            Save();
            return;
        }
        if (IsPinned(id, key))
        {
            Unpin(id, key);
            return;
        }
        if (!data.Removed.TryGetValue(key, out var list))
        {
            list = [];
            data.Removed[key] = list;
        }
        if (list.Contains(id)) return;
        list.Add(id);
        Save();
    }

    /// Puts a removed slot back.
    public void Restore(string id, string key)
    {
        if (!data.Removed.TryGetValue(key, out var list) || !list.Remove(id)) return;
        if (list.Count == 0) data.Removed.Remove(key);
        Save();
    }

    /// Brings a slot, or one option of a slot, from another ring into this one (the other ring keeps it too).
    public void AddFromRing(string id, string key)
    {
        if (data.Removed.TryGetValue(key, out var removed) && removed.Remove(id) && removed.Count == 0) data.Removed.Remove(key);
        if (!data.Added.TryGetValue(key, out var list))
        {
            list = [];
            data.Added[key] = list;
        }
        if (!list.Contains(id)) list.Add(id);
        Save();
    }

    /// The whole ring in a new order (ids as the wheel shows them).
    public void SetOrder(IEnumerable<string> ids, string key)
    {
        var list = ids.ToList();
        if (list.Count == 0) data.Orders.Remove(key);
        else data.Orders[key] = list;
        Save();
    }

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
            foreach (var variant in pins.ToList()) Unpinned(variant, key);
        }
        data.Pinned.Remove(key);
        data.Removed.Remove(key);
        data.Added.Remove(key);
        Save();
    }

    /// Gives the same ring of every other file type Settings offers (Kinds) this ring's order, pins, removed and added
    /// slots, replacing theirs. Ids a file type doesn't have are simply skipped when its wheel opens.
    public void ApplyToAllKinds(string key)
    {
        var bar = key.IndexOf('|');
        if (bar <= 0) return;
        var ring = key[..bar];
        foreach (var kind in Kinds)
        {
            var other = ring + "|" + kind.Raw();
            if (other == key) continue;
            Copy(data.Orders, key, other);
            Copy(data.Pinned, key, other);
            Copy(data.Removed, key, other);
            Copy(data.Added, key, other);
        }
        // Pinned titles belong to the variant ids (the copied pins have theirs); drop the ones nothing pins any more.
        foreach (var variant in data.PinnedTitles.Keys.ToList())
        {
            if (!data.Pinned.Values.Any(list => list.Contains(variant))) data.PinnedTitles.Remove(variant);
        }
        Save();
    }

    private static void Copy(Dictionary<string, List<string>> map, string from, string to)
    {
        if (map.TryGetValue(from, out var list) && list.Count > 0) map[to] = [.. list];
        else map.Remove(to);
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
