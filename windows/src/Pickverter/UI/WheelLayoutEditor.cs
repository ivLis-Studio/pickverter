using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Pickverter.UI.Editors;
using Pickverter.UI.Wheel;

namespace Pickverter.UI;

/// Settings › Wheel: one ring for one file type as the wheel shows it (a live preview and the list of slots), and
/// everything that can go in or out of it. The window rebuilds the page after every change; the editor keeps its ring,
/// file type and selected slot across rebuilds.
internal sealed class WheelLayoutEditor
{
    private enum Origin { Native, Pinned, Added }

    /// One slot of the list. Parent: the slot a pinned option belongs to; Source: the ring a borrowed slot comes from.
    private sealed record Row(PickOption Option, string Title, Origin Origin, string? Parent, WheelMode? Source)
    {
        public string Id => Option.Id;
    }

    /// One entry of Add Slot; children make it a submenu.
    private sealed record Candidate(string Id, string Title, string Symbol, IReadOnlyList<Candidate> Children);

    /// One entry of a slot's default-option box: closed it shows Short ("Auto", or the option's own title), the open
    /// list shows Long ("Automatic (last used)"). Id "" is automatic.
    private sealed record DefaultChoice(string Id, string Short, string Long)
    {
        public override string ToString() => Short;
    }

    /// About this wide, the wheel stays recognisable next to the list.
    private const double PreviewWidth = 220;

    private readonly FocusRegistry focus;
    private readonly Action rebuild;
    private string? selected;
    private string? reveal;
    private Dictionary<WheelMode, List<PickOption>> native = new();
    private Dictionary<WheelMode, List<PickOption>> arranged = new();
    private List<Row> rows = [];
    private readonly List<SlotRowView> views = [];
    private WheelModel? preview;
    private ReorderStack? list;

    /// rebuild: asks the window for a new page (the ring or the file type changed).
    public WheelLayoutEditor(FocusRegistry focus, Action rebuild)
    {
        this.focus = focus;
        this.rebuild = rebuild;
    }

    public WheelMode Ring { get; private set; } = WheelMode.Convert;
    public FileKind Kind { get; private set; } = FileKind.Image;

    private static WheelLayout Layout => WheelLayout.Shared;
    private string RingKey => WheelLayout.Key(Ring, Kind);

    /// "Edit Wheel Layout…" in the wheel: its ring and, when the files were all of one type, that file type.
    public void Open(WheelMode ring, FileKind? kind, string? slot = null)
    {
        Ring = ring;
        if (kind is { } type && WheelLayout.Kinds.Contains(type)) Kind = type;
        selected = slot;
        reveal = slot;
    }

    public FrameworkElement Build()
    {
        list = null;
        views.Clear();
        native = LayoutSamples.Rings(Kind);
        arranged = LayoutSamples.Arranged(native, Kind, Layout);
        rows = arranged[Ring].Select(RowFor).ToList();
        if (selected != null && rows.All(row => row.Id != selected)) selected = null;
        var content = Ui.Column(0, Toolbar(), Rule(), Body(), Rule(), Actions());
        return Ui.Card(content, new Thickness(0));
    }

    private static Border Rule() => new() { Height = 1, Background = Theme.Divider };

    // Rows

    private Row RowFor(PickOption option)
    {
        if (Layout.IsAdded(option.Id, RingKey) && RingHolding(option.Id) is { } source && source != Ring)
            return new Row(option, TitleOf(option.Id, native[source]), Origin.Added, null, source);
        if (option.PinnedFrom is { } parentId && native[Ring].FirstOrDefault(candidate => candidate.Id == parentId) is { } parent)
            return new Row(option, TitleOf(option.Id, [parent]), Origin.Pinned, parent.Title, null);
        return new Row(option, option.Title, Origin.Native, null, null);
    }

    /// A slot's own title, or "Parent · Option" for an option shown as a slot of its own.
    private static string TitleOf(string id, IReadOnlyList<PickOption> options)
    {
        foreach (var option in options)
        {
            if (option.Id == id) return option.Title;
        }
        foreach (var parent in options)
        {
            foreach (var variant in parent.Variants)
            {
                if (variant.Id == id) return parent.Title + " · " + variant.Title;
            }
        }
        return Layout.Title(id, null);
    }

    private WheelMode? RingHolding(string id)
    {
        foreach (var mode in WheelModes.All)
        {
            if (native[mode].Any(option => option.Id == id || option.Variants.Any(variant => variant.Id == id))) return mode;
        }
        return null;
    }

    private static string SymbolOf(PickOption option, PickOption? parent = null)
    {
        if (option.Symbol.Length > 0) return option.Symbol;
        if (parent is { Symbol.Length: > 0 }) return parent.Symbol;
        return "circle.dashed";
    }

    // Ring and file type

    private FrameworkElement Toolbar()
    {
        var rings = new SegmentPicker<WheelMode>(WheelModes.All.Select(mode => (mode, (string?)mode.KeyName(), mode.Title())), Ring, L("set.layout.ring"));
        foreach (var (mode, segment) in rings.Segments) focus.Register(segment, "layout.ring." + mode.Raw());
        rings.Changed += mode =>
        {
            if (mode == Ring) return;
            Ring = mode;
            selected = null;
            focus.Prefer("layout.ring." + mode.Raw());
            rebuild();
        };
        var kinds = Ui.Combo(WheelLayout.Kinds.Select(kind => (kind, LayoutSamples.Title(kind))), Kind, kind =>
        {
            if (kind == Kind) return;
            Kind = kind;
            selected = null;
            focus.Prefer("layout.kind");
            rebuild();
        }, L("set.layout.kind"), 150);
        focus.Register(kinds, "layout.kind");
        var kindRow = Ui.Row(10, Ui.Text(L("set.layout.kind"), 14), kinds);
        kindRow.VerticalAlignment = VerticalAlignment.Center;
        return new Adaptive { Gap = 12, Margin = new Thickness(16, 12, 16, 12), Children = { rings, kindRow } };
    }

    // Preview and slots

    private FrameworkElement Body()
    {
        var stage = Preview();
        var side = Ui.Column(6, stage,
            Ui.Paragraph(L("set.layout.hint"), 12, Theme.TextSecondary, TextAlignment.Center),
            Ui.Paragraph(L("set.layout.note"), 12, Theme.TextTertiary, TextAlignment.Center));
        side.Width = stage.Width;
        var main = Ui.Column(10, rows.Count == 0 ? Empty() : SlotList(), AddButton());
        return new Adaptive
        {
            Gap = 18,
            FillSecond = true,
            MinSecond = 260,
            CenterFirstWhenStacked = true,
            Margin = new Thickness(16, 14, 16, 16),
            Children = { side, main },
        };
    }

    /// The real wheel at about 220 px: the disc and the ring tabs under it, cut out of the full drawing (no outer ring
    /// opens here). Not interactive; the selected slot is highlighted, on its page.
    private Border Preview()
    {
        preview = new WheelModel();
        var g = preview.Geometry;
        g.TabWidth = WheelCanvas.TabWidthFor(1);
        preview.ShowLayout(LayoutSamples.Files(Kind).Take(1).ToList(), Ring, arranged);
        if (selected != null) preview.PreviewHighlight(rows.FindIndex(row => row.Id == selected));
        var canvas = new WheelCanvas(preview)
        {
            IsHitTestVisible = false,
            Focusable = false,
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Direction = 270, Opacity = Theme.ShadowOpacity * 0.8, Color = Theme.ShadowColor },
        };
        const double air = 14;
        var half = Math.Max(g.DiscRadius, g.TabBarWidth / 2) + air;
        var top = g.DiscRadius + air;
        var bottom = g.TabBarOffset + g.TabBarHeight / 2 + air;
        var scale = Math.Min(0.66, PreviewWidth / (half * 2));
        canvas.LayoutTransform = new ScaleTransform(scale, scale);
        var window = new Canvas { Width = Math.Round(half * 2 * scale), Height = Math.Round((top + bottom) * scale), ClipToBounds = true };
        Canvas.SetLeft(canvas, -(g.Center - half) * scale);
        Canvas.SetTop(canvas, -(g.Center - top) * scale);
        window.Children.Add(canvas);
        return new Border
        {
            Background = Theme.Window,
            BorderBrush = Theme.CardStroke,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6),
            Width = window.Width + 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = window,
        };
    }

    private FrameworkElement Empty()
    {
        var note = Ui.Paragraph(L("set.layout.empty"), 13, Theme.TextSecondary, TextAlignment.Center);
        note.VerticalAlignment = VerticalAlignment.Center;
        return new Border
        {
            BorderBrush = Theme.Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(20, 28, 20, 28),
            Child = note,
        };
    }

    private FrameworkElement SlotList()
    {
        list = new ReorderStack { Spacing = 2 };
        AutomationProperties.SetName(list, L("set.layout"));
        views.Clear();
        for (var index = 0; index < rows.Count; index++)
        {
            var view = RowView(rows[index], index);
            views.Add(view);
            list.AddRow(view);
            if (rows[index].Id == reveal) view.Loaded += (_, _) => view.BringIntoView();
        }
        reveal = null;
        // Past ten slots the wheel turns pages: nine slots and More on each.
        var perPage = WheelModel.MaxPerPage - 1;
        if (rows.Count > WheelModel.MaxPerPage)
        {
            for (var page = 2; (page - 1) * perPage < rows.Count; page++) list.AddDivider(PageDivider(page));
        }
        list.Moving += ShowOrder;
        list.Dropped += Drop;
        list.Cancelled += () => ShowOrder(null, -1);
        return list;
    }

    private static FrameworkElement PageDivider(int page)
    {
        var grid = new Grid { Margin = new Thickness(8, 10, 4, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(Ui.Text(L("set.layout.page", page.ToString(Loc.Culture)), 12, FontWeights.SemiBold, Theme.TextSecondary));
        var line = new Border { Height = 1, Background = Theme.Divider, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 1, 0, 0) };
        Grid.SetColumn(line, 1);
        grid.Children.Add(line);
        return grid;
    }

    private SlotRowView RowView(Row row, int index)
    {
        var option = row.Option;
        var choosesDefault = option.Variants.Count > 1;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 48 });
        if (choosesDefault) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new Gripper { Margin = new Thickness(0, 0, 9, 0) });
        var icon = Glyphs.Icon(SymbolOf(option), 16, Theme.TextPrimary);
        icon.Margin = new Thickness(0, 0, 10, 0);
        Grid.SetColumn(icon, 1);
        grid.Children.Add(icon);

        var title = Ui.Text(row.Title, 14);
        FrameworkElement label = title;
        string? note = null;
        if (row.Origin == Origin.Pinned)
        {
            note = L("set.layout.pinned_from", row.Parent ?? "");
            label = new TrimRow { Gap = 6, Children = { title, Glyphs.Icon("pin", 12, Theme.TextTertiary) } };
        }
        else if (row is { Origin: Origin.Added, Source: { } source })
        {
            note = L("set.layout.add.from", source.Title());
            label = new TrimRow { Gap = 8, Children = { title, Pill(source.KeyName() + " " + source.Title()) } };
        }
        label.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(label, 2);
        grid.Children.Add(label);

        if (choosesDefault)
        {
            var combo = DefaultCombo(row);
            combo.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(combo, 3);
            grid.Children.Add(combo);
        }

        var remove = FlatButton.IconOnly(L("set.layout.remove"), "minus.circle", () => Remove(row, index), compact: true, subtle: true);
        AutomationProperties.SetName(remove, L("set.layout.remove") + ", " + row.Title);
        remove.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(remove, choosesDefault ? 4 : 3);
        grid.Children.Add(remove);
        focus.Register(remove, "layout.remove." + row.Id);

        var view = new SlotRowView(row.Title, note) { Child = grid, IsSelected = row.Id == selected };
        AutomationProperties.SetPositionInSet(view, index + 1);
        AutomationProperties.SetSizeOfSet(view, rows.Count);
        focus.Register(view, "layout.row." + row.Id);
        view.ContextMenu = RowMenu(row, index);
        view.IsKeyboardFocusWithinChanged += (_, args) =>
        {
            if (args.NewValue is true) Select(row.Id);
        };
        view.PreviewMouseLeftButtonDown += (_, args) =>
        {
            if (InControl(args.OriginalSource as DependencyObject, view)) return;
            view.Focus();
            list?.Press(view, args);
            args.Handled = true;
        };
        view.PreviewMouseRightButtonDown += (_, args) =>
        {
            if (!InControl(args.OriginalSource as DependencyObject, view)) view.Focus();
        };
        view.KeyDown += (_, args) => RowKey(view, row, index, args);
        return view;
    }

    /// Presses on the default-option menu or the remove button belong to them, not to dragging.
    private static bool InControl(DependencyObject? node, DependencyObject row)
    {
        for (; node != null && node != row; node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is ButtonBase or ComboBox) return true;
        }
        return false;
    }

    private static Border Pill(string text) => new()
    {
        Background = Theme.Tile,
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(7, 1, 7, 2),
        VerticalAlignment = VerticalAlignment.Center,
        Child = Ui.Text(text, 11, brush: Theme.TextSecondary),
    };

    private static DataTemplate? closedChoice;
    private static DataTemplate? openChoice;

    /// How a DefaultChoice reads: Short in the closed box (ending in "…" only past the box's widest), Long in the list.
    private static DataTemplate ChoiceText(bool open)
    {
        if ((open ? openChoice : closedChoice) is { } cached) return cached;
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(open ? nameof(DefaultChoice.Long) : nameof(DefaultChoice.Short)));
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap);
        if (!open) text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        var template = new DataTemplate(typeof(DefaultChoice)) { VisualTree = text };
        template.Seal();
        if (open) openChoice = template;
        else closedChoice = template;
        return template;
    }

    /// Which option a plain click on the slot runs: automatic (the last one used) or a fixed one. The box is as wide as
    /// what it shows ("Auto", or the chosen option), so the slot's title keeps the room. The closed box and the open list
    /// read the same item differently: the box finds the short text in its own resources (the item's ContentTemplate
    /// stays empty, or the box would copy it), while each list entry finds the long text in its own resources first.
    private ComboBox DefaultCombo(Row row)
    {
        var option = row.Option;
        var chosen = Layout.Defaults.TryGetValue(option.Id, out var id) && option.Variants.Any(variant => variant.Id == id) ? id : "";
        var box = new ComboBox
        {
            FontFamily = Theme.TextFont,
            FontSize = 13,
            MinWidth = 0,
            MaxWidth = 220,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            ToolTip = L("ctx.default"),
        };
        box.Resources.Add(new DataTemplateKey(typeof(DefaultChoice)), ChoiceText(open: false));
        var choices = new List<DefaultChoice> { new("", L("set.layout.auto"), L("ctx.default_auto")) };
        choices.AddRange(option.Variants.Select(variant => new DefaultChoice(variant.Id, variant.Title, variant.Title)));
        foreach (var choice in choices)
        {
            var item = new ComboBoxItem { Content = choice, FontFamily = Theme.TextFont };
            item.Resources.Add(new DataTemplateKey(typeof(DefaultChoice)), ChoiceText(open: true));
            AutomationProperties.SetName(item, choice.Long);
            box.Items.Add(item);
            if (choice.Id == chosen) box.SelectedIndex = box.Items.Count - 1;
        }
        AutomationProperties.SetName(box, row.Title + ", " + L("ctx.default"));
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is ComboBoxItem { Content: DefaultChoice choice }) Layout.SetDefault(option.Id, choice.Id.Length == 0 ? null : choice.Id);
        };
        return focus.Register(box, "layout.default." + row.Id);
    }

    private ContextMenu RowMenu(Row row, int index)
    {
        var menu = new ContextMenu { FontFamily = Theme.TextFont };
        var earlier = TrayFlyout.MenuItem(L("ctx.move_earlier"), "arrow.counterclockwise", () => Later(() => Move(index, -1)));
        earlier.InputGestureText = KeyNames.Combo(KeyNames.Alt, "↑");
        earlier.IsEnabled = index > 0;
        var later = TrayFlyout.MenuItem(L("ctx.move_later"), "arrow.clockwise", () => Later(() => Move(index, 1)));
        later.InputGestureText = KeyNames.Combo(KeyNames.Alt, "↓");
        later.IsEnabled = index < rows.Count - 1;
        var remove = TrayFlyout.MenuItem(L("set.layout.remove"), "minus.circle", () => Later(() => Remove(row, index)));
        remove.InputGestureText = KeyNames.Del;
        menu.Items.Add(earlier);
        menu.Items.Add(later);
        menu.Items.Add(new Separator());
        menu.Items.Add(remove);
        return menu;
    }

    /// ↑ ↓ Home End walk the list, Alt+↑ Alt+↓ move the slot, Delete removes it.
    private void RowKey(SlotRowView view, Row row, int index, KeyEventArgs args)
    {
        if (!ReferenceEquals(args.OriginalSource, view)) return;
        var key = args.Key == Key.System ? args.SystemKey : args.Key;
        var alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        switch (key)
        {
            case Key.Escape when list?.IsDragging == true:
                list.Cancel();
                break;
            case Key.Up when alt:
                Move(index, -1);
                break;
            case Key.Down when alt:
                Move(index, 1);
                break;
            case Key.Up when !alt:
                FocusRow(index - 1);
                break;
            case Key.Down when !alt:
                FocusRow(index + 1);
                break;
            case Key.Home:
                FocusRow(0);
                break;
            case Key.End:
                FocusRow(rows.Count - 1);
                break;
            case Key.Delete:
                Remove(row, index);
                break;
            default:
                return;
        }
        args.Handled = true;
    }

    private void FocusRow(int index)
    {
        if (index < 0 || index >= views.Count) return;
        views[index].Focus();
        views[index].BringIntoView();
    }

    private void Select(string id)
    {
        if (selected == id) return;
        selected = id;
        for (var index = 0; index < views.Count && index < rows.Count; index++) views[index].IsSelected = rows[index].Id == id;
        preview?.PreviewHighlight(rows.FindIndex(row => row.Id == id));
    }

    /// While a row is dragged the wheel preview shows the order so far (null: back to the saved order).
    private void ShowOrder(IReadOnlyList<int>? order, int dragged)
    {
        if (preview == null) return;
        var rings = new Dictionary<WheelMode, List<PickOption>>(arranged);
        if (order != null) rings[Ring] = order.Select(index => rows[index].Option).ToList();
        preview.ShowLayout(preview.Files, Ring, rings);
        var position = order != null && dragged >= 0 ? order.ToList().IndexOf(dragged) : rows.FindIndex(row => row.Id == selected);
        preview.PreviewHighlight(position >= 0 ? position : null);
    }

    // Changes (each one saves; the window then builds the page again)

    /// A menu's command runs once the menu has closed and handed the keyboard back, so the new page can take it over.
    private static void Later(Action action) => Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, action);

    private void Move(int index, int step)
    {
        var target = index + step;
        if (index < 0 || index >= rows.Count || target < 0 || target >= rows.Count) return;
        var ids = rows.Select(row => row.Id).ToList();
        (ids[index], ids[target]) = (ids[target], ids[index]);
        selected = rows[index].Id;
        focus.Prefer("layout.row." + selected);
        Layout.SetOrder(ids, RingKey);
    }

    private void Drop(IReadOnlyList<int> order, int dragged)
    {
        var ids = order.Select(index => rows[index].Id).ToList();
        if (ids.SequenceEqual(rows.Select(row => row.Id)))
        {
            ShowOrder(null, -1);
            return;
        }
        selected = rows[dragged].Id;
        focus.Prefer("layout.row." + selected);
        Layout.SetOrder(ids, RingKey);
    }

    private void Remove(Row row, int index)
    {
        // From the keyboard, the slot that takes its place (or the one before it, or Add Slot) keeps the focus.
        var keyboard = InputManager.Current.MostRecentInputDevice is KeyboardDevice;
        var next = index + 1 < rows.Count ? rows[index + 1].Id : index > 0 ? rows[index - 1].Id : null;
        if (selected == row.Id) selected = keyboard ? next : null;
        if (keyboard) focus.Prefer(next != null ? "layout.row." + next : "layout.add");
        Layout.Remove(row.Id, RingKey);
    }

    /// A slot that comes back or in: shown selected (and scrolled to) once the list is rebuilt.
    private void Bring(string id, Action change)
    {
        Later(() =>
        {
            selected = id;
            reveal = id;
            change();
        });
    }

    private FrameworkElement AddButton()
    {
        var button = Ui.DropDown(L("set.layout.add"), "plus", AddMenu);
        return focus.Register(button, "layout.add");
    }

    /// Removed slots, options of this ring's slots as slots of their own, and the other two rings' slots.
    private ContextMenu AddMenu()
    {
        var menu = new ContextMenu();
        void Section(string title)
        {
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = title, IsEnabled = false, FontWeight = FontWeights.SemiBold });
        }
        var shown = rows.Select(row => row.Id).ToHashSet();
        var all = WheelModes.All.SelectMany(mode => native[mode]).ToList();
        var hidden = Layout.Hidden;

        var removed = (Layout.Removed.TryGetValue(RingKey, out var ids) ? ids : []).Where(id => !shown.Contains(id)).ToList();
        if (removed.Count > 0)
        {
            Section(L("set.layout.add.removed"));
            foreach (var id in removed)
            {
                var found = WheelLayout.Find(id, all);
                var title = found != null ? TitleOf(id, all) : Layout.Title(id, null);
                menu.Items.Add(TrayFlyout.MenuItem(title, found != null ? SymbolOf(found) : "circle.dashed", () => Bring(id, () => Layout.Restore(id, RingKey))));
            }
        }

        var parents = native[Ring].Where(parent => parent.Variants.Count > 1 && !removed.Contains(parent.Id))
            .Select(parent => (Parent: parent, Variants: parent.Variants.Where(variant => !shown.Contains(variant.Id) && !hidden.ContainsKey(variant.Id)).ToList()))
            .Where(entry => entry.Variants.Count > 0).ToList();
        if (parents.Count > 0)
        {
            Section(L("set.layout.add.options"));
            foreach (var (parent, variants) in parents)
            {
                var item = new MenuItem { Header = parent.Title, Icon = Glyphs.Icon(SymbolOf(parent), 14) };
                foreach (var variant in variants)
                {
                    var title = WheelModel.Display(variant, parent).Title;
                    item.Items.Add(TrayFlyout.MenuItem(variant.Title, null, () => Bring(variant.Id, () => Layout.Pin(variant.Id, title, RingKey))));
                }
                menu.Items.Add(item);
            }
        }

        var skip = shown.Concat(removed).Concat(hidden.Keys).ToHashSet();
        foreach (var other in WheelModes.All.Where(mode => mode != Ring))
        {
            var items = new List<Candidate>();
            foreach (var option in native[other])
            {
                var variants = option.Variants.Count > 1 ? option.Variants.Where(variant => !skip.Contains(variant.Id)).ToList() : [];
                var own = !skip.Contains(option.Id);
                if (!own && variants.Count == 0) continue;
                var children = new List<Candidate>();
                if (variants.Count > 0)
                {
                    if (own) children.Add(new Candidate(option.Id, option.Title, SymbolOf(option), []));
                    children.AddRange(variants.Select(variant => new Candidate(variant.Id, variant.Title, SymbolOf(variant, option), [])));
                }
                items.Add(new Candidate(option.Id, option.Title, SymbolOf(option), children));
            }
            if (items.Count == 0) continue;
            Section(L("set.layout.add.from", other.Title()));
            foreach (var candidate in items)
            {
                if (candidate.Children.Count == 0)
                {
                    menu.Items.Add(TrayFlyout.MenuItem(candidate.Title, candidate.Symbol, () => Bring(candidate.Id, () => Layout.AddFromRing(candidate.Id, RingKey))));
                    continue;
                }
                var item = new MenuItem { Header = candidate.Title, Icon = Glyphs.Icon(candidate.Symbol, 14) };
                foreach (var child in candidate.Children)
                {
                    // The slot itself first, then its options.
                    if (item.Items.Count == 1 && candidate.Children[0].Id == candidate.Id) item.Items.Add(new Separator());
                    item.Items.Add(TrayFlyout.MenuItem(child.Title, null, () => Bring(child.Id, () => Layout.AddFromRing(child.Id, RingKey))));
                }
                menu.Items.Add(item);
            }
        }

        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = L("set.layout.add.none"), IsEnabled = false });
        return menu;
    }

    // Ring-wide

    private FrameworkElement Actions()
    {
        var reset = Ui.Button(L("ctx.reset_ring"), () =>
        {
            focus.Prefer("layout.apply");
            Layout.Reset(RingKey);
        }, icon: "arrow.uturn.backward");
        reset.IsEnabled = Layout.IsCustomizedKey(RingKey);
        focus.Register(reset, "layout.reset");
        var apply = Ui.Button(L("set.layout.apply_all"), () => { });
        apply.Click += (_, _) => ApplyToAll(Window.GetWindow(apply));
        focus.Register(apply, "layout.apply");
        var bar = new WrapPanel { Margin = new Thickness(16, 8, 8, 10) };
        foreach (var button in new[] { reset, apply })
        {
            button.Margin = new Thickness(0, 2, 8, 2);
            bar.Children.Add(button);
        }
        return bar;
    }

    private void ApplyToAll(Window? owner)
    {
        var ring = Ring.Title();
        if (!ConfirmDialog.Ask(owner, L("set.layout.apply_all.q", ring), L("set.layout.apply_all.d", ring), L("set.layout.apply"))) return;
        focus.Prefer("layout.apply");
        Layout.ApplyToAllKinds(RingKey);
    }
}

/// Two children side by side when they fit, else one above the other (the editor's toolbar and its body).
internal sealed class Adaptive : Panel
{
    private bool sideBySide;

    public double Gap { get; set; } = 12;
    /// Side by side, the second child takes the rest of the row, which must leave it at least MinSecond.
    public bool FillSecond { get; set; }
    public double MinSecond { get; set; }
    public bool CenterFirstWhenStacked { get; set; }

    protected override Size MeasureOverride(Size available)
    {
        if (InternalChildren.Count < 2)
        {
            foreach (UIElement child in InternalChildren) child.Measure(available);
            return InternalChildren.Count == 1 ? InternalChildren[0].DesiredSize : default;
        }
        var first = InternalChildren[0];
        var second = InternalChildren[1];
        var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);
        first.Measure(unbounded);
        var firstWidth = first.DesiredSize.Width;
        double secondWidth;
        if (FillSecond)
        {
            secondWidth = MinSecond;
        }
        else
        {
            second.Measure(unbounded);
            secondWidth = second.DesiredSize.Width;
        }
        var infinite = double.IsInfinity(available.Width);
        sideBySide = infinite || firstWidth + Gap + secondWidth <= available.Width;
        if (sideBySide)
        {
            if (FillSecond) second.Measure(new Size(infinite ? double.PositiveInfinity : Math.Max(0, available.Width - firstWidth - Gap), double.PositiveInfinity));
            var width = infinite ? firstWidth + Gap + second.DesiredSize.Width : available.Width;
            return new Size(width, Math.Max(first.DesiredSize.Height, second.DesiredSize.Height));
        }
        var row = new Size(available.Width, double.PositiveInfinity);
        first.Measure(row);
        second.Measure(row);
        return new Size(available.Width, first.DesiredSize.Height + Gap + second.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        if (InternalChildren.Count < 2)
        {
            foreach (UIElement child in InternalChildren) child.Arrange(new Rect(final));
            return final;
        }
        var first = InternalChildren[0];
        var second = InternalChildren[1];
        if (sideBySide)
        {
            var firstWidth = Math.Min(first.DesiredSize.Width, final.Width);
            if (FillSecond)
            {
                first.Arrange(new Rect(0, 0, firstWidth, first.DesiredSize.Height));
                second.Arrange(new Rect(firstWidth + Gap, 0, Math.Max(0, final.Width - firstWidth - Gap), second.DesiredSize.Height));
            }
            else
            {
                first.Arrange(new Rect(0, 0, firstWidth, final.Height));
                var secondWidth = second.DesiredSize.Width;
                second.Arrange(new Rect(Math.Max(firstWidth + Gap, final.Width - secondWidth), 0, secondWidth, final.Height));
            }
            return final;
        }
        var width = Math.Min(first.DesiredSize.Width, final.Width);
        first.Arrange(new Rect(CenterFirstWhenStacked ? (final.Width - width) / 2 : 0, 0, width, first.DesiredSize.Height));
        second.Arrange(new Rect(0, first.DesiredSize.Height + Gap, final.Width, second.DesiredSize.Height));
        return final;
    }
}

/// The slot list: rows in wheel order, with a divider before each page after the first. Drag a row to move it; the
/// other rows slide out of the way (unless Windows has animations off) and the list scrolls near the window's edges.
internal sealed class ReorderStack : Panel
{
    private static readonly int PerPage = WheelModel.MaxPerPage - 1;
    private readonly List<UIElement> rows = [];
    private readonly List<UIElement> dividers = [];
    private readonly Dictionary<UIElement, double> lastTop = new();
    private List<int> order = [];
    private UIElement? pressed;
    private UIElement? dragged;
    private Point press;
    private double grab;
    private double dragTop;
    private List<int>? startOrder;
    private DispatcherTimer? autoScroll;
    private double scrollStep;

    public double Spacing { get; set; } = 2;
    public bool IsDragging => dragged != null;

    /// While dragging, after the order changed: the order (row indices) and the dragged row.
    public event Action<IReadOnlyList<int>, int>? Moving;
    /// The mouse let go of a dragged row: the final order and the dragged row.
    public event Action<IReadOnlyList<int>, int>? Dropped;
    /// Esc, or the mouse went elsewhere: the rows went back.
    public event Action? Cancelled;

    public void AddRow(UIElement row)
    {
        row.RenderTransform = new TranslateTransform();
        rows.Add(row);
        order.Add(rows.Count - 1);
        Children.Add(row);
    }

    public void AddDivider(UIElement divider)
    {
        dividers.Add(divider);
        Children.Add(divider);
    }

    private bool DividerBefore(int position) =>
        rows.Count > WheelModel.MaxPerPage && position > 0 && position % PerPage == 0 && position / PerPage - 1 < dividers.Count;

    private UIElement DividerAt(int position) => dividers[position / PerPage - 1];

    /// Where each position starts for the current order.
    private List<double> Tops()
    {
        var tops = new List<double>(order.Count);
        var y = 0.0;
        for (var position = 0; position < order.Count; position++)
        {
            if (DividerBefore(position)) y += DividerAt(position).DesiredSize.Height + Spacing;
            tops.Add(y);
            y += rows[order[position]].DesiredSize.Height + Spacing;
        }
        return tops;
    }

    protected override Size MeasureOverride(Size available)
    {
        var width = 0.0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(available.Width, double.PositiveInfinity));
            width = Math.Max(width, child.DesiredSize.Width);
        }
        if (order.Count == 0) return new Size(width, 0);
        var tops = Tops();
        return new Size(width, tops[^1] + rows[order[^1]].DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var tops = Tops();
        for (var position = 0; position < order.Count; position++)
        {
            if (DividerBefore(position))
            {
                var divider = DividerAt(position);
                divider.Arrange(new Rect(0, tops[position] - Spacing - divider.DesiredSize.Height, final.Width, divider.DesiredSize.Height));
            }
            var row = rows[order[position]];
            row.Arrange(new Rect(0, tops[position], final.Width, row.DesiredSize.Height));
            Slide(row, tops[position]);
        }
        return final;
    }

    /// The dragged row stays under the pointer; a row whose place changed glides there from where it was.
    private void Slide(UIElement row, double top)
    {
        var shift = (TranslateTransform)row.RenderTransform;
        if (row == dragged)
        {
            shift.BeginAnimation(TranslateTransform.YProperty, null);
            shift.Y = dragTop - top;
        }
        else if (lastTop.TryGetValue(row, out var before) && Math.Abs(before - top) > 0.5 && SystemParameters.ClientAreaAnimation)
        {
            var from = before + shift.Y - top;
            shift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(150)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        lastTop[row] = top;
    }

    /// A press on a row (not on its controls): a drag starts once the pointer moves a few pixels.
    public void Press(UIElement row, MouseButtonEventArgs args)
    {
        if (!rows.Contains(row) || pressed != null) return;
        pressed = row;
        press = args.GetPosition(this);
        grab = press.Y - (lastTop.TryGetValue(row, out var top) ? top : 0);
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (pressed != null && IsMouseCaptured) Drag(e.GetPosition(this));
    }

    private void Drag(Point point)
    {
        if (pressed == null) return;
        if (dragged == null)
        {
            if (Math.Abs(point.Y - press.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            dragged = pressed;
            startOrder = [.. order];
            SetZIndex(dragged, 1);
            Cursor = Cursors.SizeAll;
            if (dragged is SlotRowView view) view.IsLifted = true;
        }
        var height = dragged.DesiredSize.Height;
        dragTop = Math.Clamp(point.Y - grab, 0, Math.Max(0, ActualHeight - height));
        var index = rows.IndexOf(dragged);
        var position = order.IndexOf(index);
        var center = dragTop + height / 2;
        var moved = false;
        var tops = Tops();
        // Trade places with a neighbour once the dragged row's middle passes the neighbour's middle.
        while (position > 0 && center < tops[position - 1] + rows[order[position - 1]].DesiredSize.Height / 2)
        {
            (order[position], order[position - 1]) = (order[position - 1], order[position]);
            position--;
            moved = true;
            tops = Tops();
        }
        while (position < order.Count - 1 && center > tops[position + 1] + rows[order[position + 1]].DesiredSize.Height / 2)
        {
            (order[position], order[position + 1]) = (order[position + 1], order[position]);
            position++;
            moved = true;
            tops = Tops();
        }
        InvalidateArrange();
        if (moved) Moving?.Invoke([.. order], index);
        ScrollNearEdges();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (pressed == null) return;
        var row = dragged;
        Finish();
        if (row == null) return;
        e.Handled = true;
        Dropped?.Invoke([.. order], rows.IndexOf(row));
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        Cancel();
    }

    /// Puts the rows back as they were before the drag.
    public void Cancel()
    {
        if (pressed == null) return;
        var wasDragging = dragged != null;
        if (startOrder != null) order = startOrder;
        Finish();
        if (wasDragging) Cancelled?.Invoke();
    }

    private void Finish()
    {
        var row = dragged;
        pressed = null;
        dragged = null;
        startOrder = null;
        Cursor = null;
        autoScroll?.Stop();
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (row != null)
        {
            SetZIndex(row, 0);
            if (row is SlotRowView view) view.IsLifted = false;
            // Settle into the slot from where the row was let go.
            var shift = (TranslateTransform)row.RenderTransform;
            var top = Tops()[order.IndexOf(rows.IndexOf(row))];
            var from = dragTop - top;
            if (SystemParameters.ClientAreaAnimation && Math.Abs(from) > 0.5)
            {
                shift.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(120)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
            else
            {
                shift.BeginAnimation(TranslateTransform.YProperty, null);
                shift.Y = 0;
            }
            lastTop[row] = top;
        }
        InvalidateArrange();
    }

    /// Near the top or bottom of the page the page scrolls, so a slot can travel the whole list.
    private void ScrollNearEdges()
    {
        if (dragged == null || Scroller() is not { } viewer)
        {
            autoScroll?.Stop();
            return;
        }
        const double edge = 40;
        var pointer = Mouse.GetPosition(viewer);
        scrollStep = pointer.Y < edge ? -Math.Ceiling((edge - pointer.Y) / 3)
            : pointer.Y > viewer.ViewportHeight - edge ? Math.Ceiling((pointer.Y - viewer.ViewportHeight + edge) / 3) : 0;
        if (scrollStep == 0)
        {
            autoScroll?.Stop();
            return;
        }
        if (autoScroll == null)
        {
            autoScroll = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = TimeSpan.FromMilliseconds(30) };
            autoScroll.Tick += (_, _) => ScrollTick();
        }
        if (!autoScroll.IsEnabled) autoScroll.Start();
    }

    private void ScrollTick()
    {
        if (dragged == null || Scroller() is not { } viewer)
        {
            autoScroll?.Stop();
            return;
        }
        var before = viewer.VerticalOffset;
        viewer.ScrollToVerticalOffset(before + scrollStep);
        viewer.UpdateLayout();
        if (Math.Abs(viewer.VerticalOffset - before) < 0.1)
        {
            autoScroll?.Stop();
            return;
        }
        Drag(Mouse.GetPosition(this));
    }

    private ScrollViewer? Scroller()
    {
        for (DependencyObject? node = VisualTreeHelper.GetParent(this); node != null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ScrollViewer viewer) return viewer;
        }
        return null;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new GroupPeer(this, AutomationControlType.List);
}

/// One row of the slot list (a list item for screen readers): a selected row carries the accent bar, a dragged one lifts.
internal sealed class SlotRowView : Border
{
    private bool selected;
    private bool lifted;

    public SlotRowView(string title, string? note)
    {
        Focusable = true;
        FocusVisualStyle = Ui.FocusRing(4);
        CornerRadius = new CornerRadius(4);
        Padding = new Thickness(8, 4, 4, 4);
        MinHeight = 40;
        BorderThickness = new Thickness(1);
        AutomationProperties.SetName(this, title);
        if (note != null)
        {
            ToolTip = note;
            AutomationProperties.SetHelpText(this, note);
        }
        Update();
    }

    public bool IsSelected
    {
        get => selected;
        set
        {
            if (selected == value) return;
            selected = value;
            Update();
        }
    }

    public bool IsLifted
    {
        get => lifted;
        set
        {
            if (lifted == value) return;
            lifted = value;
            Update();
        }
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == IsMouseOverProperty) Update();
    }

    private void Update()
    {
        Background = lifted ? Theme.Surface : selected || IsMouseOver ? Theme.SubtleHover : Brushes.Transparent;
        BorderBrush = lifted ? Theme.Hairline : Brushes.Transparent;
        Effect = lifted ? new DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Direction = 270, Opacity = Theme.ShadowOpacity * 0.7, Color = Theme.ShadowColor } : null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (!selected || lifted) return;
        var height = Math.Min(16, Math.Max(0, ActualHeight - 8));
        dc.DrawRoundedRectangle(Theme.Accent, null, new Rect(0.5, (ActualHeight - height) / 2, 3, height), 1.5, 1.5);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new GroupPeer(this, AutomationControlType.ListItem);
}

/// Six dots: the row can be dragged.
internal sealed class Gripper : FrameworkElement
{
    public Gripper()
    {
        Width = 8;
        Height = 14;
        VerticalAlignment = VerticalAlignment.Center;
        Cursor = Cursors.SizeAll;
    }

    protected override void OnRender(DrawingContext dc)
    {
        // Transparent fill: the whole handle (not just its dots) shows the move cursor.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(new Point(-3, -6), new Size(ActualWidth + 6, ActualHeight + 12)));
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 2; column++) dc.DrawEllipse(Theme.TextTertiary, null, new Point(1.5 + column * 5, 2 + row * 5), 1.2, 1.2);
        }
    }
}

/// Asks before a change that reaches beyond what's on screen ("Use for All File Types…").
internal sealed class ConfirmDialog : EditorWindow<bool>
{
    private readonly string question;
    private readonly string detail;
    private readonly string confirm;
    private FlatButton? primary;

    private ConfirmDialog(string question, string detail, string confirm)
    {
        this.question = question;
        this.detail = detail;
        this.confirm = confirm;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Rebuild();
        Loaded += (_, _) => primary?.Focus();
    }

    protected override void Rebuild()
    {
        Title = L("set.layout");
        primary = Ui.Button(confirm, () => Finish(true), prominent: true);
        primary.IsDefault = true;
        var form = Ui.Column(10, Ui.Paragraph(question, 20, weight: FontWeights.SemiBold), Ui.Paragraph(detail, 13, Theme.TextSecondary));
        form.Margin = new Thickness(24, 22, 24, 18);
        var dock = new DockPanel();
        var footer = Footer(null, CancelButton(this), primary);
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(footer);
        dock.Children.Add(form);
        Content = dock;
    }

    /// True when the change was confirmed.
    public static bool Ask(Window? owner, string question, string detail, string confirm)
    {
        var dialog = new ConfirmDialog(question, detail, confirm);
        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        var answer = false;
        dialog.Completed += value => answer = value;
        dialog.ShowDialog();
        return answer;
    }
}
