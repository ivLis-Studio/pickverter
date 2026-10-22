using System.Windows;
using System.Windows.Media.Imaging;

namespace Pickverter.UI;

/// A regular window (Settings, Welcome, Software Update, editors): theme colours, dark title bar in dark mode,
/// and a fresh build whenever the language or the theme changes.
internal abstract class AppWindow : Window
{
    protected AppWindow()
    {
        FontFamily = Theme.TextFont;
        Background = Theme.Window;
        Foreground = Theme.TextPrimary;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        try
        {
            using var stream = typeof(AppWindow).Assembly.GetManifestResourceStream("Pickverter.ico");
            if (stream != null) Icon = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        }
        catch (Exception)
        {
        }
        SourceInitialized += (_, _) => Windowing.DarkTitleBar(this);
        Theme.Changed += OnTheme;
        Loc.Changed += OnLanguage;
        Closed += (_, _) =>
        {
            Theme.Changed -= OnTheme;
            Loc.Changed -= OnLanguage;
        };
    }

    private void OnTheme()
    {
        Background = Theme.Window;
        Foreground = Theme.TextPrimary;
        Windowing.DarkTitleBar(this);
        Rebuild();
    }

    private void OnLanguage()
    {
        FontFamily = Theme.TextFont;
        Rebuild();
    }

    protected abstract void Rebuild();
}
