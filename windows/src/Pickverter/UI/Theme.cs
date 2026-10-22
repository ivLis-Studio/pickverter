using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.UI.ViewManagement;

namespace Pickverter.UI;

/// Flat, native styling: the Windows accent colour for selection, Windows 11's neutral tokens everywhere else.
/// No gradients, no glass. Values follow WinUI's light and dark theme resources.
internal static class Theme
{
    private static UISettings? settings;
    private static Color accentDark1 = Color.FromRgb(0x00, 0x5F, 0xB8);
    private static Color accentLight2 = Color.FromRgb(0x99, 0xEB, 0xFF);
    private static bool? forced;

    public static event Action? Changed;

    public static bool IsDark { get; private set; }
    public static bool HighContrast => SystemParameters.HighContrast;
    public static Color SystemAccent { get; private set; } = Color.FromRgb(0x00, 0x78, 0xD4);

    static Theme()
    {
        Read();
        Rebuild();
    }

    public static void Start(Dispatcher dispatcher)
    {
        try
        {
            settings = new UISettings();
            settings.ColorValuesChanged += (_, _) => dispatcher.BeginInvoke(() =>
            {
                if (Read()) Notify();
            });
        }
        catch (Exception error)
        {
            Debug.Log("theme: " + error.Message);
        }
        SystemParameters.StaticPropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SystemParameters.HighContrast)) Notify();
        };
    }

    private static void Notify()
    {
        Rebuild();
        Changed?.Invoke();
    }

    /// Snapshots render both themes regardless of the machine's setting.
    public static void Force(bool? dark)
    {
        forced = dark;
        Read();
        Notify();
    }

    private static bool Read()
    {
        var dark = forced ?? AppsUseDarkTheme();
        Color accent = SystemAccent, dark1 = accentDark1, light2 = accentLight2;
        try
        {
            var ui = settings ?? new UISettings();
            accent = Convert(ui.GetColorValue(UIColorType.Accent));
            dark1 = Convert(ui.GetColorValue(UIColorType.AccentDark1));
            light2 = Convert(ui.GetColorValue(UIColorType.AccentLight2));
        }
        catch (Exception)
        {
        }
        var changed = dark != IsDark || accent != SystemAccent || dark1 != accentDark1 || light2 != accentLight2;
        IsDark = dark;
        SystemAccent = accent;
        accentDark1 = dark1;
        accentLight2 = light2;
        return changed;
    }

    private static Color Convert(Windows.UI.Color color) => Color.FromArgb(color.A, color.R, color.G, color.B);

    private static int? Personalize(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue(name) as int?;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool AppsUseDarkTheme() => Personalize("AppsUseLightTheme") == 0;
    public static bool TaskbarIsDark() => Personalize("SystemUsesLightTheme") != 1;

    // Brushes (rebuilt on every theme change; windows rebuild their content on Changed)

    public static SolidColorBrush Accent { get; private set; } = null!;
    public static SolidColorBrush AccentHover { get; private set; } = null!;
    public static SolidColorBrush AccentSubtle { get; private set; } = null!;
    public static SolidColorBrush AccentText { get; private set; } = null!;
    public static SolidColorBrush TextOnAccent { get; private set; } = null!;
    public static SolidColorBrush TextPrimary { get; private set; } = null!;
    public static SolidColorBrush TextSecondary { get; private set; } = null!;
    public static SolidColorBrush TextTertiary { get; private set; } = null!;
    public static SolidColorBrush TextDisabled { get; private set; } = null!;
    /// Flyouts, result cards and the wheel.
    public static SolidColorBrush Surface { get; private set; } = null!;
    /// Window background (Settings, Welcome, editors).
    public static SolidColorBrush Window { get; private set; } = null!;
    /// Rows in Settings.
    public static SolidColorBrush Card { get; private set; } = null!;
    public static SolidColorBrush CardStroke { get; private set; } = null!;
    public static SolidColorBrush Hairline { get; private set; } = null!;
    public static SolidColorBrush Tile { get; private set; } = null!;
    public static SolidColorBrush SubtleHover { get; private set; } = null!;
    public static SolidColorBrush SubtlePressed { get; private set; } = null!;
    public static SolidColorBrush Control { get; private set; } = null!;
    public static SolidColorBrush ControlHover { get; private set; } = null!;
    public static SolidColorBrush ControlStroke { get; private set; } = null!;
    public static SolidColorBrush Divider { get; private set; } = null!;
    public static SolidColorBrush Caution { get; private set; } = null!;
    public static SolidColorBrush CautionSubtle { get; private set; } = null!;
    public static SolidColorBrush Success { get; private set; } = null!;
    public static SolidColorBrush Critical { get; private set; } = null!;
    public static SolidColorBrush SelectedTab { get; private set; } = null!;
    public static SolidColorBrush HubFill { get; private set; } = null!;
    public static Color ShadowColor { get; private set; }
    public static double ShadowOpacity { get; private set; }

    private static SolidColorBrush B(uint argb)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush B(Color color, double opacity = 1)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(color.A * opacity), color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    private static void Rebuild()
    {
        if (HighContrast)
        {
            var window = SystemColors.WindowColor;
            var text = SystemColors.WindowTextColor;
            var highlight = SystemColors.HighlightColor;
            Accent = AccentHover = AccentText = B(highlight);
            AccentSubtle = B(highlight, 0.35);
            TextOnAccent = B(SystemColors.HighlightTextColor);
            TextPrimary = B(text);
            TextSecondary = TextTertiary = B(text);
            TextDisabled = B(SystemColors.GrayTextColor);
            Surface = Window = Card = Control = ControlHover = SelectedTab = HubFill = B(window);
            CardStroke = Hairline = ControlStroke = Divider = B(text);
            Tile = SubtleHover = SubtlePressed = B(text, 0.12);
            Caution = Success = Critical = B(text);
            CautionSubtle = B(text, 0.12);
            ShadowColor = Colors.Black;
            ShadowOpacity = 0;
            return;
        }
        var accent = IsDark ? accentLight2 : accentDark1;
        Accent = B(accent);
        AccentHover = B(accent, 0.9);
        AccentSubtle = B(accent, 0.22);
        AccentText = B(IsDark ? accentLight2 : accentDark1);
        TextOnAccent = IsDark ? B(0xFF000000) : B(0xFFFFFFFF);
        if (IsDark)
        {
            TextPrimary = B(0xFFFFFFFF);
            TextSecondary = B(0xC5FFFFFF);
            TextTertiary = B(0x87FFFFFF);
            TextDisabled = B(0x5DFFFFFF);
            Surface = B(0xFF2C2C2C);
            Window = B(0xFF202020);
            Card = B(0xFF2B2B2B);
            CardStroke = B(0xFF1D1D1D);
            Hairline = B(0x24FFFFFF);
            Tile = B(0x12FFFFFF);
            SubtleHover = B(0x0FFFFFFF);
            SubtlePressed = B(0x0AFFFFFF);
            Control = B(0x0FFFFFFF);
            ControlHover = B(0x15FFFFFF);
            ControlStroke = B(0x18FFFFFF);
            Divider = B(0x15FFFFFF);
            Caution = B(0xFFFCE100);
            CautionSubtle = B(0x26FCE100);
            Success = B(0xFF6CCB5F);
            Critical = B(0xFFFF99A4);
            SelectedTab = B(0x1FFFFFFF);
            HubFill = B(0xFF262626);
            ShadowColor = Colors.Black;
            ShadowOpacity = 0.5;
        }
        else
        {
            TextPrimary = B(0xE4000000);
            TextSecondary = B(0x9E000000);
            TextTertiary = B(0x72000000);
            TextDisabled = B(0x5C000000);
            Surface = B(0xFFF9F9F9);
            Window = B(0xFFF3F3F3);
            Card = B(0xFFFBFBFB);
            CardStroke = B(0xFFE5E5E5);
            Hairline = B(0x17000000);
            Tile = B(0x0B000000);
            SubtleHover = B(0x09000000);
            SubtlePressed = B(0x06000000);
            Control = B(0xFFFDFDFD);
            ControlHover = B(0xFFF6F6F6);
            ControlStroke = B(0x24000000);
            Divider = B(0x14000000);
            Caution = B(0xFF9D5D00);
            CautionSubtle = B(0x1F9D5D00);
            Success = B(0xFF0F7B0F);
            Critical = B(0xFFC42B1C);
            SelectedTab = B(0xFFFFFFFF);
            HubFill = B(0xFFFFFFFF);
            ShadowColor = Colors.Black;
            ShadowOpacity = 0.22;
        }
    }

    // Typography

    private static readonly Dictionary<string, string> CjkFonts = new()
    {
        ["ko"] = "Malgun Gothic",
        ["ja"] = "Yu Gothic UI",
        ["zh-Hans"] = "Microsoft YaHei UI",
        ["zh-Hant"] = "Microsoft JhengHei UI",
    };

    private static string FallbackFor(string language) => CjkFonts.TryGetValue(language, out var font) ? ", " + font : "";

    public static FontFamily TextFont => new("Segoe UI Variable Text, Segoe UI" + FallbackFor(Loc.Language));
    public static FontFamily DisplayFont => new("Segoe UI Variable Display, Segoe UI" + FallbackFor(Loc.Language));
    public static FontFamily SmallFont => new("Segoe UI Variable Small, Segoe UI" + FallbackFor(Loc.Language));
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    private static FontFamily? iconsRegular, iconsFilled;

    public static FontFamily IconsRegular => iconsRegular ??= new FontFamily(new Uri("pack://application:,,,/"), "./Resources/Fonts/#FluentSystemIcons-Regular");
    public static FontFamily IconsFilled => iconsFilled ??= new FontFamily(new Uri("pack://application:,,,/"), "./Resources/Fonts/#FluentSystemIcons-Filled");

    public static Typeface Face(FontWeight weight, FontFamily? family = null) => new(family ?? TextFont, FontStyles.Normal, weight, FontStretches.Normal);
}
