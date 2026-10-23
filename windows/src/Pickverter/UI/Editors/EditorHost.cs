using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Pickverter.UI.Editors;

/// Opens the editors (crop, hide areas, trim, pages, info, password, page range, document password) on the UI thread
/// and hands the answer back to the job. Closing an editor cancels the job without leaving a card behind.
internal sealed class EditorHost : IEditors
{
    private static async Task<T> Open<T>(string name, Func<EditorWindow<T>> make, CancellationToken token)
    {
        if (EditorInjection.TryGet<T>(name, out var injected)) return injected;
        var answer = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Platform.Main.RunAsync(() =>
        {
            var window = make();
            window.Completed += value => answer.TrySetResult(value);
            window.Cancelled += () => answer.TrySetCanceled();
            token.Register(() => Platform.Main.Post(window.Close));
            // Result cards float above everything; an editor in use must not end up under one (a card covered the
            // password dialog's Lock button on a small screen). Switch to another app and it steps back like any window.
            window.Topmost = true;
            window.Activated += (_, _) => window.Topmost = true;
            window.Deactivated += (_, _) => window.Topmost = false;
            window.Show();
            Windowing.Activate(window);
        });
        return await answer.Task;
    }

    public Task<CropSelection> CropAsync(string file, int count, CancellationToken token) =>
        Open("crop", () => new CropEditor(file, count), token);

    public Task<RedactSelection> RedactAsync(string file, int count, CancellationToken token) =>
        Open("redact", () => new RedactEditor(file, count), token);

    public Task<TrimSelection> TrimAsync(string file, double duration, bool isVideo, CancellationToken token) =>
        Open("trim", () => new TrimEditor(file, duration, isVideo), token);

    public Task<PdfArrangement> PagesAsync(IReadOnlyList<string> files, CancellationToken token) =>
        Open("pages", () => new PagesEditor(files), token);

    public Task<MetadataEdit> MetadataAsync(string file, MetaSnapshot snapshot, CancellationToken token) =>
        Open("metadata", () => new MetadataEditor(file, snapshot), token);

    public Task<string> PasswordAsync(string file, CancellationToken token) =>
        Open("password", () => new PasswordDialog(file), token);

    public Task<string> RangeAsync(string file, int pageCount, CancellationToken token) =>
        Open("range", () => new RangeDialog(file, pageCount), token);

    public Task<string> UnlockAsync(string file, bool retry, CancellationToken token) =>
        Open("unlock", () => new PasswordDialog(file, unlock: true, retry: retry), token);

    public Task<double> SizeAsync(string file, long bytes, CancellationToken token) =>
        EditorInjection.TryGetSize(out var injected) ? Task.FromResult(injected) : Open("size", () => new SizeDialog(file, bytes), token);
}

/// An editor window that ends with an answer (Finish) or without one (closed, Esc, Cancel).
internal abstract class EditorWindow<T> : AppWindow
{
    private bool finished;

    public event Action<T>? Completed;
    public event Action? Cancelled;

    protected void Finish(T value)
    {
        if (finished) return;
        finished = true;
        Completed?.Invoke(value);
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (finished) return;
        finished = true;
        Cancelled?.Invoke();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    /// Bottom bar: a note on the left, buttons on the right (the last one is the default action).
    protected static Border Footer(UIElement? note, params FrameworkElement[] buttons)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (note != null) grid.Children.Add(note);
        var row = Ui.Row(8);
        foreach (var button in buttons) row.Children.Add(button);
        Grid.SetColumn(row, 1);
        grid.Children.Add(row);
        return new Border
        {
            Background = Theme.Surface,
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(20, 14, 20, 14),
            Child = grid,
        };
    }

    protected static FlatButton CancelButton(Window window) => Ui.Button(L("common.cancel"), window.Close);
}

/// Image previews for the editors, decoded off the UI thread.
internal static class Previews
{
    public static Task<BitmapSource?> Image(string path, int maxPixel) => Task.Run<BitmapSource?>(() =>
    {
        try
        {
            return Decode(ImageEngine.PreviewPng(path, maxPixel));
        }
        catch (Exception error)
        {
            Debug.Log("preview: " + error.Message);
            return null;
        }
    });

    public static async Task<BitmapSource?> Frame(string path, int maxPixel, double position)
    {
        try
        {
            var png = await MediaEngine.PreviewFrame(path, maxPixel, position, CancellationToken.None);
            try
            {
                return Decode(await File.ReadAllBytesAsync(png));
            }
            finally
            {
                FileSystem.TryDelete(png);
            }
        }
        catch (Exception error)
        {
            Debug.Log("frame: " + error.Message);
            return null;
        }
    }

    public static BitmapSource Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}

/// Segmented choice (ratio presets, redaction styles, trim modes).
internal sealed class Segmented<T> : Border
{
    private readonly SpacedStack row = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly List<(T Value, FlatButton Button)> buttons = [];

    public event Action<T>? Changed;
    public T Value { get; private set; }

    public Segmented(IEnumerable<(T Value, string Title)> items, T selected)
    {
        Value = selected;
        Background = Theme.Tile;
        CornerRadius = new CornerRadius(6);
        Padding = new Thickness(2);
        Child = row;
        foreach (var (value, title) in items)
        {
            var button = new FlatButton(title, compact: true, subtle: true) { Radius = new CornerRadius(4) };
            var target = value;
            button.Click += (_, _) => Select(target, notify: true);
            buttons.Add((value, button));
            row.Children.Add(button);
        }
        Refresh();
    }

    public void Select(T value, bool notify = false)
    {
        Value = value;
        Refresh();
        if (notify) Changed?.Invoke(value);
    }

    private void Refresh()
    {
        foreach (var (value, button) in buttons)
        {
            var on = EqualityComparer<T>.Default.Equals(value, Value);
            button.Background = on ? Theme.SelectedTab : Brushes.Transparent;
            button.BorderThickness = new Thickness(on ? 1 : 0);
            button.BorderBrush = Theme.Hairline;
            button.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            System.Windows.Automation.AutomationProperties.SetItemStatus(button, on ? "selected" : "");
        }
    }
}

// Small dialogs

/// Sets a PDF's password (typed twice), or asks for the one that opens a locked HWP document (unlock: one field;
/// retry: the last one didn't match).
internal sealed class PasswordDialog : EditorWindow<string>
{
    private readonly string file;
    private readonly bool unlock;
    private readonly bool retry;
    private readonly PasswordBox first = new() { FontSize = 14, Padding = new Thickness(8, 6, 8, 6) };
    private readonly PasswordBox second = new() { FontSize = 14, Padding = new Thickness(8, 6, 8, 6) };
    private readonly TextBlock error = Ui.Caption("");

    public PasswordDialog(string file, bool unlock = false, bool retry = false)
    {
        this.file = file;
        this.unlock = unlock;
        this.retry = retry;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        Rebuild();
        Loaded += (_, _) => first.Focus();
    }

    protected override void Rebuild()
    {
        var title = unlock ? L("hwp.password.title") : L("pdf.password.title");
        Title = title;
        error.Foreground = Theme.Critical;
        // Opening a document has no second field to mismatch: its only message is that the last password was wrong.
        if (unlock) error.Text = retry ? L("err.hwp_password") : "";
        var primary = Ui.Button(unlock ? L("hwp.password.open") : L("pdf.password.lock"), Submit, prominent: true);
        primary.IsDefault = true;
        var form = Ui.Column(10,
            Ui.Text(title, 20, FontWeights.SemiBold),
            Ui.Text(unlock ? L("hwp.password.info", FileNames.Name(file)) : L("pdf.password.info", FileNames.Name(file)), 13, brush: Theme.TextSecondary, wrap: true),
            Ui.Caption(L("pdf.password.placeholder")), Ui.Detach(first));
        if (!unlock)
        {
            form.Children.Add(Ui.Caption(L("pdf.password.confirm")));
            form.Children.Add(Ui.Detach(second));
        }
        form.Children.Add(Ui.Detach(error));
        form.Margin = new Thickness(24, 22, 24, 18);
        var dock = new DockPanel();
        var footer = Footer(null, CancelButton(this), primary);
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(footer);
        dock.Children.Add(form);
        Content = dock;
    }

    private void Submit()
    {
        if (first.Password.Length == 0) return;
        if (!unlock && first.Password != second.Password)
        {
            error.Text = L("pdf.password.mismatch");
            second.Focus();
            return;
        }
        Finish(first.Password);
    }
}

internal sealed class RangeDialog : EditorWindow<string>
{
    private readonly string file;
    private readonly int pages;
    private readonly TextBox input = new() { FontSize = 14, Padding = new Thickness(8, 6, 8, 6) };
    private readonly TextBlock error = Ui.Caption("");

    public RangeDialog(string file, int pages)
    {
        this.file = file;
        this.pages = pages;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        Rebuild();
        Loaded += (_, _) => input.Focus();
    }

    protected override void Rebuild()
    {
        Title = L("pdf.range.title");
        error.Foreground = Theme.Critical;
        var extract = Ui.Button(L("pdf.range.extract"), Submit, prominent: true);
        extract.IsDefault = true;
        var form = Ui.Column(10,
            Ui.Text(L("pdf.range.title"), 20, FontWeights.SemiBold),
            Ui.Text(L("pdf.range.info", FileNames.Name(file), pages.ToString(Loc.Culture)), 13, brush: Theme.TextSecondary, wrap: true),
            Ui.Detach(input), Ui.Detach(error));
        form.Margin = new Thickness(24, 22, 24, 18);
        var dock = new DockPanel();
        var footer = Footer(null, CancelButton(this), extract);
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(footer);
        dock.Children.Add(form);
        Content = dock;
    }

    private void Submit()
    {
        var text = input.Text.Trim();
        if (PdfEngine.ParseRanges(text, pages) == null)
        {
            error.Text = L("err.bad_range", text);
            input.Focus();
            input.SelectAll();
            return;
        }
        Finish(text);
    }
}

/// "Compress › Custom Size…": the size to get under, in MB. Suggests half the current size.
internal sealed class SizeDialog : EditorWindow<double>
{
    private readonly string file;
    private readonly long bytes;
    private readonly TextBox input = new() { FontSize = 14, Padding = new Thickness(8, 6, 8, 6) };

    public SizeDialog(string file, long bytes)
    {
        this.file = file;
        this.bytes = bytes;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        input.Text = SizeTargets.Suggested(bytes).ToString("0.##", Loc.Culture);
        Rebuild();
        Loaded += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };
    }

    protected override void Rebuild()
    {
        Title = L("size.custom.title");
        System.Windows.Automation.AutomationProperties.SetName(input, L("size.custom.field"));
        var compress = Ui.Button(L("tool.compress"), Submit, prominent: true);
        compress.IsDefault = true;
        var form = Ui.Column(10,
            Ui.Text(L("size.custom.title"), 20, FontWeights.SemiBold),
            Ui.Text(L("size.custom.info", FileNames.Name(file), SizeTargets.Label(bytes)), 13, brush: Theme.TextSecondary, wrap: true),
            Ui.Caption(L("size.custom.field")), Ui.Detach(input));
        form.Margin = new Thickness(24, 22, 24, 18);
        var dock = new DockPanel();
        var footer = Footer(null, CancelButton(this), compress);
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(footer);
        dock.Children.Add(form);
        Content = dock;
    }

    private void Submit()
    {
        if (SizeTargets.ParseMegabytes(input.Text) is { } megabytes)
        {
            Finish(megabytes);
            return;
        }
        Platform.Feedback.Beep();
        input.Focus();
        input.SelectAll();
    }
}

internal sealed class MetadataEditor : EditorWindow<MetadataEdit>
{
    private readonly string file;
    private readonly MetaSnapshot snapshot;
    private readonly Dictionary<string, TextBox> boxes = new();
    private bool removeLocation;
    private bool replace;

    public MetadataEditor(string file, MetaSnapshot snapshot)
    {
        this.file = file;
        this.snapshot = snapshot;
        Width = 600;
        Height = 640;
        MinHeight = 420;
        Rebuild();
    }

    protected override void Rebuild()
    {
        Title = L("editor.meta.title", FileNames.Name(file));
        var values = snapshot.Fields.ToDictionary(field => field.Key, field => field.Value);
        var form = Ui.Column(12);
        form.Margin = new Thickness(24, 20, 24, 20);
        form.Children.Add(Ui.Text(FileNames.Name(file), 20, FontWeights.SemiBold));
        foreach (var key in MetadataKit.Keys(snapshot.Family))
        {
            if (!boxes.TryGetValue(key, out var box))
            {
                box = new TextBox { FontSize = 14, Padding = new Thickness(8, 6, 8, 6), Text = values.GetValueOrDefault(key, "") };
                boxes[key] = box;
            }
            box.Tag = MetadataKit.Placeholder(key);
            var label = Ui.Caption(MetadataKit.Label(key, snapshot.Family));
            var placeholder = MetadataKit.Placeholder(key);
            form.Children.Add(Ui.Column(4, label, Ui.Detach(box)));
            if (placeholder.Length > 0) box.ToolTip = placeholder;
        }
        if (snapshot.HasLocation)
        {
            var location = new CheckBox { Content = L("meta.remove_location"), IsChecked = removeLocation, FontSize = 14, Margin = new Thickness(0, 4, 0, 0) };
            location.Checked += (_, _) => removeLocation = true;
            location.Unchecked += (_, _) => removeLocation = false;
            form.Children.Add(location);
        }
        var output = new Segmented<bool>([(false, L("meta.copy")), (true, L("meta.replace"))], replace);
        output.Changed += value => replace = value;
        form.Children.Add(output);
        if (snapshot.Info.Count > 0)
        {
            form.Children.Add(Ui.SectionHeader(L("meta.details")));
            var info = new Grid();
            info.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            info.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var index = 0; index < snapshot.Info.Count; index++)
            {
                info.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var row = snapshot.Info[index];
                var label = Ui.Text(row.Label, 13, brush: Theme.TextSecondary);
                label.Margin = new Thickness(0, 3, 16, 3);
                Grid.SetRow(label, index);
                info.Children.Add(label);
                var value = Ui.Text(row.Value, 13, wrap: true);
                value.Margin = new Thickness(0, 3, 0, 3);
                Grid.SetRow(value, index);
                Grid.SetColumn(value, 1);
                info.Children.Add(value);
            }
            form.Children.Add(info);
        }
        var scroller = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var save = Ui.Button(L("meta.save"), Submit, prominent: true);
        save.IsDefault = true;
        var dock = new DockPanel();
        var footer = Footer(null, CancelButton(this), save);
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(footer);
        dock.Children.Add(scroller);
        Content = dock;
    }

    private void Submit()
    {
        var original = snapshot.Fields.ToDictionary(field => field.Key, field => field.Value);
        var changed = new Dictionary<string, string>();
        foreach (var (key, box) in boxes)
        {
            var before = original.GetValueOrDefault(key, "");
            if (box.Text != before) changed[key] = box.Text.Trim();
        }
        Finish(new MetadataEdit(changed, removeLocation, replace));
    }
}
