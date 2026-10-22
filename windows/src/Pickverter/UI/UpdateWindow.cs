using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using TextRun = System.Windows.Documents.Run;

namespace Pickverter.UI;

/// "Software Update": what's new, download progress, install.
internal sealed partial class UpdateWindow : AppWindow
{
    private readonly Updater updater = Updater.Shared;

    public UpdateWindow()
    {
        Width = 540;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        updater.PropertyChanged += OnChanged;
        Closed += (_, _) => updater.PropertyChanged -= OnChanged;
        Rebuild();
    }

    private void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args) => Rebuild();

    private bool ShowsRelease => updater.Phase is Updater.UpdatePhase.Available or Updater.UpdatePhase.Downloading or Updater.UpdatePhase.Ready
        or Updater.UpdatePhase.Installing;

    private string Headline => updater.Phase switch
    {
        Updater.UpdatePhase.Available or Updater.UpdatePhase.Downloading or Updater.UpdatePhase.Ready or Updater.UpdatePhase.Installing =>
            L("update.window.new", updater.Latest?.Version ?? ""),
        Updater.UpdatePhase.UpToDate => L("update.window.latest"),
        Updater.UpdatePhase.Checking => L("update.checking"),
        Updater.UpdatePhase.Failed => L("update.window.failed"),
        _ => L("update.window.title"),
    };

    private string Subline => updater.Phase switch
    {
        Updater.UpdatePhase.Downloading => L("update.downloading", ((int)(updater.Fraction * 100)).ToString(CultureInfo.InvariantCulture)),
        Updater.UpdatePhase.Ready => updater.Installer?.CanInstallInPlace == false ? L("update.err.location") : L("update.card.ready.d"),
        Updater.UpdatePhase.Installing => L("update.installing"),
        Updater.UpdatePhase.Failed => updater.Failure ?? "",
        _ => L("update.window.current", updater.CurrentVersion),
    };

    protected override void Rebuild()
    {
        Title = L("update.window.title");
        var stack = Ui.Column(16);
        stack.Margin = new Thickness(24, 22, 24, 20);
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(new Image { Source = BrandArt.AppIcon(), Width = 60, Height = 60, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top });
        var texts = Ui.Column(4, Ui.Text(Headline, 18, FontWeights.SemiBold, wrap: true), Ui.Text(Subline, 13, brush: Theme.TextSecondary, wrap: true));
        Grid.SetColumn(texts, 1);
        header.Children.Add(texts);
        stack.Children.Add(header);
        if (ShowsRelease && updater.Latest?.Notes is { Length: > 0 } notes)
        {
            var box = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = Theme.TextPrimary, LineHeight = 19 };
            Render(box, notes);
            stack.Children.Add(new Border
            {
                Background = Theme.Card,
                BorderBrush = Theme.CardStroke,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 10, 14, 10),
                MaxHeight = 260,
                Child = new ScrollViewer { Content = box, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            });
        }
        if (updater.Phase == Updater.UpdatePhase.Downloading) stack.Children.Add(new LinearProgress { Value = updater.Fraction, Height = 4 });
        else if (updater.Phase is Updater.UpdatePhase.Checking or Updater.UpdatePhase.Installing) stack.Children.Add(new LinearProgress { Value = null, Height = 4 });
        stack.Children.Add(Buttons());
        Content = stack;
    }

    private FrameworkElement Buttons()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (ShowsRelease && updater.Latest?.NotesUrl is { Length: > 0 } url)
            grid.Children.Add(Ui.Link(L("update.window.on_github"), () => Platform.Shell.OpenUrl(url)));
        else
            grid.Children.Add(Ui.Link(L("update.releases"), () => Platform.Shell.OpenUrl(updater.ReleasesPage)));
        var row = Ui.Row(8);
        row.HorizontalAlignment = HorizontalAlignment.Right;
        void Add(string title, Action run, bool prominent = false, bool isDefault = false, bool isCancel = false)
        {
            var button = Ui.Button(title, run, prominent);
            button.IsDefault = isDefault;
            button.IsCancel = isCancel;
            row.Children.Add(button);
        }
        switch (updater.Phase)
        {
            case Updater.UpdatePhase.Available:
                Add(L("update.skip"), () =>
                {
                    updater.Skip();
                    Close();
                });
                Add(L("update.later"), Close, isCancel: true);
                Add(L("update.install"), updater.Download, prominent: true, isDefault: true);
                break;
            case Updater.UpdatePhase.Downloading:
                Add(L("common.cancel"), updater.CancelDownload, isCancel: true);
                break;
            case Updater.UpdatePhase.Ready:
                Add(L("update.later"), Close, isCancel: true);
                Add(L("update.restart"), updater.InstallAndRelaunch, prominent: true, isDefault: true);
                break;
            case Updater.UpdatePhase.Failed:
                Add(L("update.close"), Close, isCancel: true);
                Add(L("update.retry"), () => _ = updater.CheckAsync(true), prominent: true, isDefault: true);
                break;
            case Updater.UpdatePhase.Checking or Updater.UpdatePhase.Installing:
                Add(L("update.close"), Close, isCancel: true);
                break;
            default:
                Add(L("update.close"), Close, prominent: true, isDefault: true, isCancel: true);
                break;
        }
        Grid.SetColumn(row, 1);
        grid.Children.Add(row);
        return grid;
    }

    [GeneratedRegex(@"\*\*(.+?)\*\*|\[(.+?)\]\((.+?)\)")]
    private static partial Regex Inline();

    /// The release notes are Markdown: bullets, **bold** and [links](…) are enough for a changelog.
    private static void Render(TextBlock box, string markdown)
    {
        var first = true;
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            if (!first) box.Inlines.Add(new LineBreak());
            first = false;
            if (line.StartsWith("## ", StringComparison.Ordinal) || line.StartsWith("### ", StringComparison.Ordinal))
            {
                box.Inlines.Add(new Bold(new TextRun(line.TrimStart('#', ' '))));
                continue;
            }
            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                box.Inlines.Add(new TextRun("•  "));
                line = line[2..];
            }
            var at = 0;
            foreach (Match match in Inline().Matches(line))
            {
                if (match.Index > at) box.Inlines.Add(new TextRun(line[at..match.Index]));
                if (match.Groups[1].Success) box.Inlines.Add(new Bold(new TextRun(match.Groups[1].Value)));
                else box.Inlines.Add(new TextRun(match.Groups[2].Value) { Foreground = Theme.AccentText });
                at = match.Index + match.Length;
            }
            if (at < line.Length) box.Inlines.Add(new TextRun(line[at..]));
        }
    }
}
