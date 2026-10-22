using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pickverter;

/// The key that opens the wheel for files selected in File Explorer.
public enum TriggerKey { Alt, Ctrl, RightCtrl }

public static class TriggerKeys
{
    public static readonly TriggerKey[] All = [TriggerKey.Alt, TriggerKey.Ctrl, TriggerKey.RightCtrl];

    public static string Raw(this TriggerKey key) => key switch
    {
        TriggerKey.Alt => "alt",
        TriggerKey.Ctrl => "ctrl",
        _ => "rightCtrl",
    };

    public static TriggerKey? Parse(string? raw) => raw switch
    {
        "alt" => TriggerKey.Alt,
        "ctrl" => TriggerKey.Ctrl,
        "rightCtrl" => TriggerKey.RightCtrl,
        _ => null,
    };

    public static string Label(this TriggerKey key) => key switch
    {
        TriggerKey.Alt => KeyNames.Alt,
        TriggerKey.Ctrl => KeyNames.Ctrl,
        _ => L("trigger.rctrl"),
    };

    /// Keycap text in hints ("Alt", "Ctrl", "Right Ctrl").
    public static string Glyph(this TriggerKey key) => key.Label();

    /// The wheel ring the same modifier picks while dragging (Alt -> Actions, Ctrl -> Tools).
    public static WheelMode Ring(this TriggerKey key) => key == TriggerKey.Alt ? WheelMode.Actions : WheelMode.Tools;
}

/// Corner of the screen where result cards stack up.
public enum HudPosition { TopRight, BottomRight, TopLeft, BottomLeft }

public static class HudPositions
{
    public static readonly HudPosition[] All = [HudPosition.BottomRight, HudPosition.TopRight, HudPosition.BottomLeft, HudPosition.TopLeft];

    public static string Raw(this HudPosition position) => position switch
    {
        HudPosition.TopRight => "topRight",
        HudPosition.BottomRight => "bottomRight",
        HudPosition.TopLeft => "topLeft",
        _ => "bottomLeft",
    };

    public static HudPosition? Parse(string? raw) => raw switch
    {
        "topRight" => HudPosition.TopRight,
        "bottomRight" => HudPosition.BottomRight,
        "topLeft" => HudPosition.TopLeft,
        "bottomLeft" => HudPosition.BottomLeft,
        _ => null,
    };

    public static bool IsTop(this HudPosition position) => position is HudPosition.TopRight or HudPosition.TopLeft;
    public static bool IsLeft(this HudPosition position) => position is HudPosition.TopLeft or HudPosition.BottomLeft;
    public static string Label(this HudPosition position) => L("hud." + position.Raw());
}

public sealed class Preferences : INotifyPropertyChanged
{
    private sealed class Data
    {
        public bool Enabled { get; set; } = true;
        public string TriggerKey { get; set; } = "alt";
        public bool TapTrigger { get; set; } = true;
        public bool ClickTrigger { get; set; } = true;
        public bool DoubleTap { get; set; }
        public bool DragTrigger { get; set; } = true;
        public string OutputLocation { get; set; } = "sameFolder";
        public string Destination { get; set; } = "file";
        public string Language { get; set; } = "system";
        public string Terminal { get; set; } = "wt";
        public bool PlaySounds { get; set; } = true;
        public bool Onboarded { get; set; }
        public string HudPosition { get; set; } = "bottomRight";
        public int MaxJobs { get; set; }
        public bool AutoCheckUpdates { get; set; } = true;
        public bool AutoInstallUpdates { get; set; } = true;
        public List<string> RecentFolders { get; set; } = [];
        public DateTimeOffset? LastUpdateCheck { get; set; }
        public string? SkippedVersion { get; set; }
        public string? UpdatedFrom { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string? path;
    private readonly Data data;
    private readonly object gate = new();

    public static Preferences Shared { get; private set; } = new(null);

    public event PropertyChangedEventHandler? PropertyChanged;

    private Preferences(string? path)
    {
        this.path = path;
        data = new Data();
        if (path != null && File.Exists(path))
        {
            try
            {
                data = JsonSerializer.Deserialize<Data>(File.ReadAllText(path), Json) ?? new Data();
            }
            catch (Exception)
            {
                data = new Data();
            }
        }
    }

    /// Reads settings.json from the app's settings folder (or the given file) and makes it the shared instance.
    public static Preferences Load(string? path = null)
    {
        path ??= Path.Combine(Platform.Paths.Settings, "settings.json");
        Shared = new Preferences(path);
        Loc.Apply(Shared.Language);
        return Shared;
    }

    /// In-memory settings for tests and snapshots.
    public static Preferences UseTransient()
    {
        Shared = new Preferences(null);
        return Shared;
    }

    private void Save()
    {
        if (path == null) return;
        try
        {
            string json;
            lock (gate) json = JsonSerializer.Serialize(data, Json);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception error)
        {
            Debug.Log("settings not saved: " + error.Message);
        }
    }

    private void Set<T>(T current, T value, Action<T> assign, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        lock (gate) assign(value);
        Save();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public bool Enabled { get => data.Enabled; set => Set(data.Enabled, value, v => data.Enabled = v); }

    public TriggerKey TriggerKey
    {
        get => TriggerKeys.Parse(data.TriggerKey) ?? TriggerKey.Alt;
        set => Set(data.TriggerKey, value.Raw(), v => data.TriggerKey = v);
    }

    public bool TapTrigger { get => data.TapTrigger; set => Set(data.TapTrigger, value, v => data.TapTrigger = v); }
    public bool ClickTrigger { get => data.ClickTrigger; set => Set(data.ClickTrigger, value, v => data.ClickTrigger = v); }
    public bool DoubleTap { get => data.DoubleTap; set => Set(data.DoubleTap, value, v => data.DoubleTap = v); }
    public bool DragTrigger { get => data.DragTrigger; set => Set(data.DragTrigger, value, v => data.DragTrigger = v); }

    public OutputLocation OutputLocation
    {
        get => OutputLocations.Parse(data.OutputLocation) ?? OutputLocation.SameFolder;
        set => Set(data.OutputLocation, value.Raw(), v => data.OutputLocation = v);
    }

    public ResultDestination Destination
    {
        get => ResultDestinations.Parse(data.Destination) ?? ResultDestination.File;
        set => Set(data.Destination, value.Raw(), v => data.Destination = v);
    }

    /// "system" or a code from Loc.Languages.
    public string Language
    {
        get => data.Language;
        set
        {
            if (data.Language == value) return;
            Loc.Apply(value);
            Set(data.Language, value, v => data.Language = v);
        }
    }

    public TerminalKind Terminal
    {
        get => Terminals.Parse(data.Terminal) ?? TerminalKind.WindowsTerminal;
        set => Set(data.Terminal, value.Raw(), v => data.Terminal = v);
    }

    public bool PlaySounds { get => data.PlaySounds; set => Set(data.PlaySounds, value, v => data.PlaySounds = v); }
    public bool Onboarded { get => data.Onboarded; set => Set(data.Onboarded, value, v => data.Onboarded = v); }

    public HudPosition HudPosition
    {
        get => HudPositions.Parse(data.HudPosition) ?? HudPosition.BottomRight;
        set => Set(data.HudPosition, value.Raw(), v => data.HudPosition = v);
    }

    /// 0 = automatic.
    public int MaxJobs { get => data.MaxJobs; set => Set(data.MaxJobs, value, v => data.MaxJobs = v); }
    public bool AutoCheckUpdates { get => data.AutoCheckUpdates; set => Set(data.AutoCheckUpdates, value, v => data.AutoCheckUpdates = v); }
    /// Download in the background and offer a one-click restart.
    public bool AutoInstallUpdates { get => data.AutoInstallUpdates; set => Set(data.AutoInstallUpdates, value, v => data.AutoInstallUpdates = v); }

    public bool LaunchAtLogin
    {
        get => Platform.Startup.LaunchAtLogin;
        set
        {
            if (Platform.Startup.LaunchAtLogin == value) return;
            Platform.Startup.LaunchAtLogin = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LaunchAtLogin)));
        }
    }

    public IReadOnlyList<string> RecentFolders
    {
        get { lock (gate) return [.. data.RecentFolders]; }
        set => Set(string.Join("\n", data.RecentFolders), string.Join("\n", value), _ => data.RecentFolders = [.. value]);
    }

    public DateTimeOffset? LastUpdateCheck { get => data.LastUpdateCheck; set => Set(data.LastUpdateCheck, value, v => data.LastUpdateCheck = v); }
    public string? SkippedVersion { get => data.SkippedVersion; set => Set(data.SkippedVersion, value, v => data.SkippedVersion = v); }
    public string? UpdatedFrom { get => data.UpdatedFrom; set => Set(data.UpdatedFrom, value, v => data.UpdatedFrom = v); }

    /// Re-announces every property (after the language changed, so bound labels refresh).
    public void RefreshAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}
