using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Pickverter.UI.Editors;

/// Organize PDF pages: drag to reorder, rotate, duplicate, delete, keep only some. Several PDFs become one.
internal sealed class PagesEditor : EditorWindow<PdfArrangement>
{
    private sealed class Page
    {
        public required int File { get; init; }
        public required int Index { get; init; }
        public int Rotation { get; set; }
        public Guid Id { get; } = Guid.NewGuid();
    }

    private readonly IReadOnlyList<string> files;
    private readonly List<Page> pages = [];
    private readonly HashSet<Guid> selected = [];
    private readonly Dictionary<(int, int), BitmapSource> thumbnails = new();
    private readonly WrapPanel grid = new() { Margin = new Thickness(16) };
    private readonly ScrollViewer scroller = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock status = Ui.Caption("", wrap: false);
    private Guid? anchor;
    private Point dragStart;
    private Page? dragging;
    private int? dropIndex;
    private readonly bool slides;

    public PagesEditor(IReadOnlyList<string> files)
    {
        this.files = files;
        slides = files.All(file => FileNames.NormalizedExt(file) is "pptx" or "key" or "odp");
        Width = 1000;
        Height = 720;
        MinWidth = 640;
        MinHeight = 440;
        scroller.Content = grid;
        grid.AllowDrop = true;
        grid.DragOver += OnDragOver;
        grid.Drop += OnDrop;
        grid.Background = Brushes.Transparent;
        grid.MouseLeftButtonDown += (_, args) =>
        {
            if (args.OriginalSource == grid)
            {
                selected.Clear();
                Refresh();
            }
        };
        Focusable = true;
        Rebuild();
        _ = Load();
    }

    private async Task Load()
    {
        for (var file = 0; file < files.Count; file++)
        {
            var count = await Task.Run(() =>
            {
                try
                {
                    return PdfEngine.PageCount(files[file]);
                }
                catch (Exception)
                {
                    return 0;
                }
            });
            for (var index = 0; index < count; index++) pages.Add(new Page { File = file, Index = index });
        }
        Refresh();
        var scratch = FileSystem.TempFolder("pages");
        try
        {
            foreach (var page in pages.ToList())
            {
                if (!IsLoaded && !IsVisible) return;
                var png = Path.Combine(scratch, $"{page.File}-{page.Index}.png");
                try
                {
                    await Platform.Pdf.RenderPageAsync(files[page.File], page.Index, 36, png, CancellationToken.None);
                    thumbnails[(page.File, page.Index)] = Previews.Decode(await File.ReadAllBytesAsync(png));
                    Refresh(page);
                }
                catch (Exception error)
                {
                    Debug.Log("page thumbnail: " + error.Message);
                }
            }
        }
        finally
        {
            FileSystem.TryDelete(scratch);
        }
    }

    protected override void Rebuild()
    {
        Title = L("editor.pages.title", files.Count == 1 ? FileNames.Name(files[0]) : LP("wheel.items", files.Count));
        var toolbar = Ui.Row(8,
            FlatButton.IconOnly(L("rot.left"), "rotate.left", () => Rotate(-90), compact: false),
            FlatButton.IconOnly(L("rot.right"), "rotate.right", () => Rotate(90), compact: false),
            FlatButton.IconOnly(L("pages.duplicate"), "plus.square.on.square", Duplicate, compact: false),
            FlatButton.IconOnly(L("pages.delete"), "trash", Delete, compact: false),
            FlatButton.IconOnly(L("pages.reverse"), "arrow.up.arrow.down", Reverse, compact: false),
            Ui.Button(L("pages.select_all"), SelectAll, subtle: true));
        toolbar.Margin = new Thickness(20, 12, 20, 8);
        var hint = Ui.Caption(L("pages.hint"), wrap: false);
        hint.Margin = new Thickness(20, 0, 20, 4);
        var saveSelected = Ui.Button(L("pages.save_selected"), () => Save(onlySelected: true));
        saveSelected.IsEnabled = selected.Count > 0;
        var save = Ui.Button(L("pages.save"), () => Save(onlySelected: false), prominent: true);
        save.IsDefault = true;
        var footer = Footer(Ui.Detach(status), CancelButton(this), saveSelected, save);
        var dock = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(hint, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(toolbar);
        dock.Children.Add(hint);
        dock.Children.Add(footer);
        if (scroller.Parent is Panel old) old.Children.Remove(scroller);
        dock.Children.Add(scroller);
        Content = dock;
        Refresh();
    }

    private void UpdateStatus()
    {
        var text = LP("pages.count", pages.Count);
        if (selected.Count > 0) text += " · " + LP("pages.selected", selected.Count);
        status.Text = text;
    }

    private void Refresh(Page? only = null)
    {
        if (only != null)
        {
            var at = pages.IndexOf(only);
            if (at >= 0 && at < grid.Children.Count)
            {
                grid.Children[at] = Tile(only, at);
                return;
            }
        }
        grid.Children.Clear();
        for (var index = 0; index < pages.Count; index++) grid.Children.Add(Tile(pages[index], index));
        UpdateStatus();
        if (Content is DockPanel dock)
        {
            foreach (var child in dock.Children)
            {
                if (child is Border { Child: Grid footerGrid })
                {
                    foreach (var element in footerGrid.Children)
                    {
                        if (element is SpacedStack row && row.Children.Count >= 2 && row.Children[1] is FlatButton saveSelected)
                            saveSelected.IsEnabled = selected.Count > 0;
                    }
                }
            }
        }
    }

    private FrameworkElement Tile(Page page, int position)
    {
        var isSelected = selected.Contains(page.Id);
        var image = new Grid { Width = 132, Height = 168 };
        if (thumbnails.TryGetValue((page.File, page.Index), out var thumbnail))
        {
            image.Children.Add(new Image
            {
                Source = thumbnail,
                Stretch = Stretch.Uniform,
                LayoutTransform = new RotateTransform(page.Rotation),
                Margin = new Thickness(6),
            });
        }
        else
        {
            image.Children.Add(new Border { Background = Theme.Tile, Margin = new Thickness(18, 10, 18, 10), CornerRadius = new CornerRadius(2) });
        }
        var label = slides ? L("pages.slide", (page.Index + 1).ToString(Loc.Culture)) : L("pages.page_short", (page.Index + 1).ToString(Loc.Culture));
        if (files.Count > 1) label = (page.File + 1).ToString(Loc.Culture) + " · " + label;
        var caption = Ui.Text(label, 12, isSelected ? FontWeights.SemiBold : FontWeights.Normal, isSelected ? Theme.AccentText : Theme.TextSecondary,
            alignment: TextAlignment.Center);
        caption.HorizontalAlignment = HorizontalAlignment.Center;
        var content = Ui.Column(4, image, caption);
        var tile = new Border
        {
            Child = content,
            Padding = new Thickness(6),
            Margin = new Thickness(6),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(2),
            BorderBrush = dropIndex == position ? Theme.Accent : isSelected ? Theme.Accent : Brushes.Transparent,
            Background = isSelected ? Theme.AccentSubtle : Brushes.Transparent,
            Cursor = Cursors.Hand,
        };
        tile.MouseLeftButtonDown += (_, args) =>
        {
            Choose(page, Keyboard.Modifiers);
            dragStart = args.GetPosition(this);
            dragging = page;
            args.Handled = true;
        };
        tile.MouseMove += (_, args) =>
        {
            if (dragging != page || args.LeftButton != MouseButtonState.Pressed) return;
            var now = args.GetPosition(this);
            if (Math.Abs(now.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(now.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            if (!selected.Contains(page.Id))
            {
                selected.Clear();
                selected.Add(page.Id);
            }
            DragDrop.DoDragDrop(tile, new DataObject("PickverterPages", page.Id.ToString()), DragDropEffects.Move);
            dragging = null;
            dropIndex = null;
            Refresh();
        };
        tile.MouseLeftButtonUp += (_, _) => dragging = null;
        System.Windows.Automation.AutomationProperties.SetName(tile, label);
        return tile;
    }

    private void Choose(Page page, ModifierKeys modifiers)
    {
        if (modifiers.HasFlag(ModifierKeys.Shift) && anchor is { } first && pages.FirstOrDefault(p => p.Id == first) is { } start)
        {
            var a = pages.IndexOf(start);
            var b = pages.IndexOf(page);
            selected.Clear();
            for (var index = Math.Min(a, b); index <= Math.Max(a, b); index++) selected.Add(pages[index].Id);
        }
        else if (modifiers.HasFlag(ModifierKeys.Control))
        {
            if (!selected.Remove(page.Id)) selected.Add(page.Id);
            anchor = page.Id;
        }
        else
        {
            selected.Clear();
            selected.Add(page.Id);
            anchor = page.Id;
        }
        Refresh();
    }

    private int InsertionIndex(Point point)
    {
        for (var index = 0; index < grid.Children.Count; index++)
        {
            if (grid.Children[index] is not FrameworkElement tile) continue;
            var origin = tile.TranslatePoint(new Point(0, 0), grid);
            var bounds = new Rect(origin, new Size(tile.ActualWidth, tile.ActualHeight));
            if (point.Y < bounds.Top) return index;
            if (point.Y <= bounds.Bottom && point.X < bounds.X + bounds.Width / 2) return index;
        }
        return grid.Children.Count;
    }

    private void OnDragOver(object sender, DragEventArgs args)
    {
        if (!args.Data.GetDataPresent("PickverterPages"))
        {
            args.Effects = DragDropEffects.None;
            return;
        }
        args.Effects = DragDropEffects.Move;
        var index = InsertionIndex(args.GetPosition(grid));
        if (dropIndex != index)
        {
            dropIndex = index;
            for (var position = 0; position < grid.Children.Count; position++)
            {
                if (grid.Children[position] is Border tile)
                    tile.BorderBrush = position == index ? Theme.Accent : selected.Contains(pages[position].Id) ? Theme.Accent : Brushes.Transparent;
            }
        }
        args.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs args)
    {
        var index = InsertionIndex(args.GetPosition(grid));
        var moving = pages.Where(page => selected.Contains(page.Id)).ToList();
        var before = pages.Take(index).Count(page => !selected.Contains(page.Id));
        pages.RemoveAll(page => selected.Contains(page.Id));
        pages.InsertRange(Math.Clamp(before, 0, pages.Count), moving);
        dropIndex = null;
        Refresh();
        args.Handled = true;
    }

    private IEnumerable<Page> Targets => selected.Count > 0 ? pages.Where(page => selected.Contains(page.Id)) : pages;

    private void Rotate(int degrees)
    {
        foreach (var page in Targets) page.Rotation = ((page.Rotation + degrees) % 360 + 360) % 360;
        Refresh();
    }

    private void Duplicate()
    {
        if (selected.Count == 0) return;
        var copies = new List<Page>();
        for (var index = pages.Count - 1; index >= 0; index--)
        {
            var page = pages[index];
            if (!selected.Contains(page.Id)) continue;
            var copy = new Page { File = page.File, Index = page.Index, Rotation = page.Rotation };
            pages.Insert(index + 1, copy);
            copies.Add(copy);
        }
        selected.Clear();
        foreach (var copy in copies) selected.Add(copy.Id);
        Refresh();
    }

    private void Delete()
    {
        if (selected.Count == 0 || selected.Count == pages.Count) return;
        pages.RemoveAll(page => selected.Contains(page.Id));
        selected.Clear();
        Refresh();
    }

    private void Reverse()
    {
        pages.Reverse();
        Refresh();
    }

    private void SelectAll()
    {
        selected.Clear();
        foreach (var page in pages) selected.Add(page.Id);
        Refresh();
    }

    private void Save(bool onlySelected)
    {
        var chosen = onlySelected ? pages.Where(page => selected.Contains(page.Id)).ToList() : pages;
        if (chosen.Count == 0) return;
        Finish(new PdfArrangement(chosen.Select(page => new PdfPageEntry(page.File, page.Index, page.Rotation)).ToList()));
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Delete or Key.Back)
        {
            Delete();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SelectAll();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.D && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Duplicate();
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }
}
