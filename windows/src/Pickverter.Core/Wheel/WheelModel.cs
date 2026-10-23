namespace Pickverter;

public enum WheelKey { Escape, Enter, Space, Left, Right, Up, Down, Tab, PageUp, PageDown, Home, End, Digit, Letter, Delete, Other, System }

public readonly record struct KeyInput(WheelKey Key, char Character = '\0', int Digit = 0, bool Shift = false, bool Ctrl = false, bool Alt = false);

[Flags]
public enum WheelChange
{
    None = 0,
    Visibility = 1,
    Items = 2,
    Highlight = 4,
    SubRing = 8,
    Tabs = 16,
    Hub = 32,
    Pointer = 64,
    All = Visibility | Items | Highlight | SubRing | Tabs | Hub | Pointer,
}

public sealed record MenuEntry
{
    public string Title { get; init; } = "";
    public string? Symbol { get; init; }
    public bool Checked { get; init; }
    public bool Enabled { get; init; } = true;
    public bool IsHeader { get; init; }
    public bool IsSeparator { get; init; }
    public Action? Run { get; init; }
    public IReadOnlyList<MenuEntry>? Submenu { get; init; }

    public static MenuEntry Separator => new() { IsSeparator = true };
}

/// The wheel's state and behaviour, independent of how it is drawn: items per ring, highlight, outer ring,
/// paging, keyboard, clicks and the "file or clipboard?" step. The Windows layer renders it and feeds input.
public sealed class WheelModel
{
    public const int MaxPerPage = 10;

    public enum WheelOrigin { Drag, Selection }

    public abstract record Item
    {
        public sealed record Option(PickOption Value) : Item;
        public sealed record More(int Page, int Total) : Item;
        public sealed record Destination(ResultDestination Value) : Item;

        public string Id => this switch
        {
            Option option => option.Value.Id,
            More more => "more." + more.Page,
            Destination destination => "dest." + destination.Value.Raw(),
            _ => "",
        };

        public PickOption? AsOption => (this as Option)?.Value;
        public IReadOnlyList<PickOption> Variants => AsOption?.Variants ?? [];
    }

    /// An option waiting for "file or clipboard?" when the destination preference is Ask.
    public sealed record PendingChoice(PickOption Option, IReadOnlyList<string> Files, WheelMode Mode);

    public sealed record ActiveOption(PickOption Option, PickOption? Parent, bool Implied);

    public WheelModel(bool demo = false) => IsDemo = demo;

    public WheelGeometry Geometry { get; } = new();
    public bool IsDemo { get; }

    public bool IsVisible { get; private set; }
    public WheelOrigin Origin { get; private set; } = WheelOrigin.Selection;
    public WheelMode Mode { get; private set; } = WheelMode.Convert;
    public IReadOnlyList<string> Files { get; private set; } = [];
    public IReadOnlyList<SegmentData> Segments { get; private set; } = [];
    public int? Highlighted { get; private set; }
    public int? SubHighlighted { get; private set; }
    public SubRingData? SubRing { get; private set; }
    public bool TabsHidden { get; private set; }
    public WheelMode? HoveredTab { get; private set; }
    public IReadOnlyDictionary<WheelMode, int> Counts { get; private set; } = new Dictionary<WheelMode, int>();
    public string? Flash { get; private set; }
    public int Page { get; private set; }
    public int PageCount { get; private set; } = 1;
    public PendingChoice? Choice { get; private set; }
    public double? PointerAngle { get; private set; }
    public IReadOnlyList<Item> Items { get; private set; } = [];
    /// Where the wheel sits on screen (physical pixels); results anchor their cards there.
    public ScreenPoint Center { get; set; }

    public event Action<WheelChange>? Changed;
    public event Action<PickOption, WheelMode, IReadOnlyList<string>, ResultDestination, ScreenPoint>? Launched;
    /// Tests: pretend a destination preference.
    public ResultDestination? DestinationOverride { get; set; }

    private readonly Dictionary<WheelMode, List<PickOption>> optionsByMode = new();
    private IDisposable? springTimer;
    private string? springKey;
    private IDisposable? flashTimer;
    private IDisposable? typeTimer;
    private string typeBuffer = "";
    private double scrollAccumulator;

    private void Notify(WheelChange change)
    {
        if (change == WheelChange.None) return;
        if ((change & (WheelChange.Highlight | WheelChange.Items | WheelChange.Visibility)) != 0) RefreshEstimate();
        Changed?.Invoke(change);
    }

    // Size preview

    /// The size preview in the hub (one file, an option SizeEstimator can try). The wheel on screen turns it on; demo,
    /// preview, snapshot and test wheels go without (EstimateNowAsync still works for them).
    public bool Estimates { get; set; }
    /// Running: the hub says "Checking the size…".
    public bool EstimatePending { get; private set; }
    public SizeEstimate? Estimate { get; private set; }
    public string? EstimateLine => EstimatePending ? L("est.calculating") : Estimate?.Line;

    private string? estimateKey;
    private CancellationTokenSource? estimateRun;
    private IDisposable? estimateTimer;

    /// Follows the highlight: a known answer shows at once; otherwise, once the highlight has rested for 250 ms, the
    /// option runs on the side. Moving on or closing cancels it.
    private void RefreshEstimate()
    {
        ActiveOption? target = null;
        if (Estimates && !IsDemo && IsVisible && Choice == null && Files.Count == 1 && Active is { } active &&
            active.Option.Unavailable == null && active.Parent?.Unavailable == null)
            target = active;
        var key = target == null ? null : Files[0] + "\n" + target.Option.Id;
        if (key == estimateKey) return;
        StopEstimate();
        estimateKey = key;
        Estimate = null;
        EstimatePending = false;
        if (target == null || key == null) return;
        var option = target.Option;
        var file = Files[0];
        if (SizeEstimator.Shared.Known(option, file) is { } known)
        {
            Estimate = known;
            return;
        }
        if (!SizeEstimator.IsEstimable(option, target.Parent, file)) return;
        var run = new CancellationTokenSource();
        estimateRun = run;
        estimateTimer = Platform.Main.After(TimeSpan.FromMilliseconds(250), () => Platform.Main.Post(() =>
        {
            if (run.IsCancellationRequested || estimateKey != key) return;
            EstimatePending = true;
            Notify(WheelChange.Hub);
            _ = RunEstimate(option, file, key, run.Token);
        }));
    }

    private async Task RunEstimate(PickOption option, string file, string key, CancellationToken token)
    {
        SizeEstimate estimate;
        try
        {
            estimate = await SizeEstimator.Shared.EstimateAsync(option, file, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception error)
        {
            Debug.Log("estimate: " + error.Message);
            estimate = SizeEstimate.Nothing;
        }
        Platform.Main.Post(() =>
        {
            if (token.IsCancellationRequested || estimateKey != key) return;
            EstimatePending = false;
            Estimate = estimate;
            Notify(WheelChange.Hub);
            if (estimate.Line is { } line) Announce(line);
        });
    }

    private void StopEstimate()
    {
        estimateTimer?.Dispose();
        estimateTimer = null;
        estimateRun?.Cancel();
        estimateRun = null;
    }

    /// Snapshots and tests: the estimate for what is highlighted now, without the wait.
    public async Task EstimateNowAsync()
    {
        if (Files.Count != 1 || Active is not { } active) return;
        var option = active.Option;
        var file = Files[0];
        if (SizeEstimator.Shared.Known(option, file) is not { } estimate)
        {
            if (!SizeEstimator.IsEstimable(option, active.Parent, file)) return;
            estimate = await SizeEstimator.Shared.EstimateAsync(option, file, CancellationToken.None);
        }
        StopEstimate();
        estimateKey = file + "\n" + option.Id;
        EstimatePending = false;
        Estimate = estimate;
        Notify(WheelChange.Hub);
    }

    // Presenting

    public void Present(IReadOnlyList<string> files, WheelOrigin origin, WheelMode? requested, ScreenPoint center)
    {
        if (files.Count == 0) return;
        Files = files;
        Origin = origin;
        Choice = null;
        Center = center;
        LoadOptions();
        var preferred = requested ?? WheelMode.Convert;
        Mode = Counts.GetValueOrDefault(preferred) > 0 ? preferred
            : WheelModes.All.Cast<WheelMode?>().FirstOrDefault(mode => Counts.GetValueOrDefault(mode!.Value) > 0) ?? WheelMode.Actions;
        Page = 0;
        RebuildItems();
        Highlighted = null;
        SubHighlighted = null;
        SubRing = null;
        TabsHidden = false;
        HoveredTab = null;
        PointerAngle = null;
        Flash = null;
        typeBuffer = "";
        CancelSpring();
        IsVisible = !IsDemo;
        Debug.Log($"present {origin} mode={Mode} items={Items.Count}");
        Notify(WheelChange.All);
        Announce(L("a11y.wheel_open", Mode.Title(), LP("a11y.slots", Segments.Count), FilesTitle));
    }

    /// Catalog options with the user's wheel customization applied (demo wheels stay stock). Each ring can take slots
    /// from the other two, so all three are read before any is arranged.
    private void LoadOptions()
    {
        var native = new Dictionary<WheelMode, List<PickOption>>();
        foreach (var mode in WheelModes.All) native[mode] = Catalog.Options(Files, mode);
        var counts = new Dictionary<WheelMode, int>();
        foreach (var mode in WheelModes.All)
        {
            var options = native[mode];
            if (!IsDemo)
            {
                var pool = WheelModes.All.Where(other => other != mode).SelectMany(other => native[other]).ToList();
                options = WheelLayout.Shared.Arrange(options, WheelLayout.Key(mode, Files), pool);
            }
            optionsByMode[mode] = options;
            counts[mode] = options.Count;
        }
        Counts = counts;
    }

    public IReadOnlyList<PickOption> OptionsFor(WheelMode mode) => optionsByMode.TryGetValue(mode, out var list) ? list : [];

    /// After hiding, moving or pinning: rebuild in place and keep the same slot highlighted.
    public void RefreshLayout(string? keeping = null)
    {
        LoadOptions();
        RebuildItems();
        SubRing = null;
        SubHighlighted = null;
        TabsHidden = false;
        var index = keeping == null ? -1 : IndexOfItem(keeping);
        if (index >= 0)
        {
            Highlighted = index;
            PointerAngle = index * Geometry.MainSpan(Items.Count);
            SetExpanded(index);
        }
        else
        {
            Highlighted = null;
            PointerAngle = null;
        }
        Notify(WheelChange.All);
    }

    private int IndexOfItem(string id)
    {
        for (var index = 0; index < Items.Count; index++)
        {
            if (Items[index].Id == id) return index;
        }
        return -1;
    }

    public void Dismiss()
    {
        if (!IsVisible) return;
        Debug.Log("dismiss");
        IsVisible = false;
        CancelSpring();
        Notify(WheelChange.Visibility);
    }

    /// The drag ended without a drop on the wheel: close a moment later (a drop may still be on its way).
    public void DragSessionEnded()
    {
        if (!IsVisible || Origin != WheelOrigin.Drag) return;
        Platform.Main.After(TimeSpan.FromMilliseconds(300), () => Platform.Main.Post(() =>
        {
            if (IsVisible && Origin == WheelOrigin.Drag && Choice == null) Dismiss();
        }));
    }

    // Items

    private void RebuildItems()
    {
        if (Choice != null)
        {
            PageCount = 1;
            Page = 0;
            Items = [new Item.Destination(ResultDestination.File), new Item.Destination(ResultDestination.Clipboard), new Item.Destination(ResultDestination.Both)];
        }
        else
        {
            var options = OptionsFor(Mode);
            if (options.Count <= MaxPerPage)
            {
                PageCount = 1;
                Page = 0;
                Items = options.Select(option => (Item)new Item.Option(option)).ToList();
            }
            else
            {
                var perPage = MaxPerPage - 1;
                PageCount = (int)Math.Ceiling(options.Count / (double)perPage);
                Page = Math.Min(Page, PageCount - 1);
                var start = Page * perPage;
                var items = options.Skip(start).Take(perPage).Select(option => (Item)new Item.Option(option)).ToList();
                items.Add(new Item.More(Page, PageCount));
                Items = items;
            }
        }
        Segments = Items.Select(item => item switch
        {
            Item.Option option => new SegmentData(option.Value.Id, option.Value.Title, option.Value.Symbol)
            {
                IsFormat = option.Value.IsFormat,
                Unavailable = option.Value.Unavailable != null,
                NeedsDownload = Tools.IsDownload(option.Value.Unavailable),
                HasVariants = option.Value.Variants.Count > 0,
            },
            Item.More more => new SegmentData(item.Id, L("wheel.more"), "ellipsis") { IsMore = true, PageLabel = $"{more.Page + 1}/{more.Total}" },
            Item.Destination destination => new SegmentData(item.Id, destination.Value.Label(), destination.Value.Symbol()),
            _ => new SegmentData(item.Id, "", ""),
        }).ToList();
    }

    public Item? HighlightedItem => Highlighted is { } index && index >= 0 && index < Items.Count ? Items[index] : null;

    /// The option that would run right now: a variant when one is highlighted on the outer ring, otherwise the
    /// slot's preferred variant (implied), otherwise the slot itself.
    public ActiveOption? Active
    {
        get
        {
            if (HighlightedItem?.AsOption is not { } parent) return null;
            if (SubHighlighted is { } sub && sub >= 0 && sub < parent.Variants.Count) return new ActiveOption(parent.Variants[sub], parent, false);
            if (parent.Variants.Count > 1)
            {
                var preferred = IsDemo ? 0 : WheelLayout.Shared.PreferredVariant(parent);
                return new ActiveOption(parent.Variants[preferred], parent, true);
            }
            return new ActiveOption(parent, null, false);
        }
    }

    public bool CanDropHere
    {
        get
        {
            if (Active is not { } active) return false;
            var reason = active.Option.Unavailable ?? active.Parent?.Unavailable;
            return reason == null || Tools.IsDownload(reason);
        }
    }

    public void SetMode(WheelMode mode)
    {
        if (mode == Mode || Choice != null) return;
        SetExpanded(null);
        Mode = mode;
        Page = 0;
        Highlighted = null;
        SubHighlighted = null;
        PointerAngle = null;
        RebuildItems();
        typeBuffer = "";
        Notify(WheelChange.Items | WheelChange.Tabs | WheelChange.Highlight | WheelChange.Hub | WheelChange.Pointer);
        Announce(mode.Title());
    }

    public void CycleMode(int step = 1)
    {
        var index = (int)Mode;
        for (var i = 0; i < WheelModes.All.Length; i++)
        {
            index = (index + step + WheelModes.All.Length) % WheelModes.All.Length;
            if (Counts.GetValueOrDefault(WheelModes.All[index]) > 0)
            {
                SetMode(WheelModes.All[index]);
                return;
            }
        }
    }

    public void NextPage(int step = 1)
    {
        if (PageCount <= 1) return;
        SetExpanded(null);
        Page = (Page + step + PageCount) % PageCount;
        Highlighted = null;
        SubHighlighted = null;
        PointerAngle = null;
        RebuildItems();
        Notify(WheelChange.Items | WheelChange.Highlight | WheelChange.Hub | WheelChange.Pointer);
    }

    public void Scroll(double delta)
    {
        if (PageCount <= 1) return;
        scrollAccumulator += delta;
        if (Math.Abs(scrollAccumulator) >= 1)
        {
            NextPage(scrollAccumulator > 0 ? -1 : 1);
            scrollAccumulator = 0;
        }
    }

    // Outer ring

    private void SetExpanded(int? index)
    {
        if (index is not { } slot || slot < 0 || slot >= Items.Count || Items[slot].Variants.Count == 0)
        {
            if (SubRing != null)
            {
                SubRing = null;
                TabsHidden = false;
                Notify(WheelChange.SubRing | WheelChange.Tabs);
            }
            return;
        }
        if (SubRing?.ParentIndex == slot) return;
        var parent = Items[slot].AsOption!;
        var variants = parent.Variants;
        var preferred = IsDemo ? 0 : WheelLayout.Shared.PreferredVariant(parent);
        var ring = new SubRingData(slot, slot * Geometry.MainSpan(Items.Count), Geometry.SubSpan(variants.Count),
            variants.Select((variant, position) => new SegmentData(variant.Id, variant.Title, variant.Symbol)
            {
                Unavailable = variant.Unavailable != null,
                NeedsDownload = Tools.IsDownload(variant.Unavailable),
                IsDefault = position == preferred && variants.Count > 1,
            }).ToList());
        // The tab bar sits under 6 o'clock; hide it when the ring opens over it.
        SubRing = ring;
        TabsHidden = Math.Abs(ring.CenterAngle - Math.PI) < ring.HalfWidth + 0.68;
        Notify(WheelChange.SubRing | WheelChange.Tabs);
    }

    private int? SubIndex(double angle)
    {
        if (SubRing is not { } ring) return null;
        var delta = angle - ring.CenterAngle;
        while (delta > Math.PI) delta -= 2 * Math.PI;
        while (delta < -Math.PI) delta += 2 * Math.PI;
        if (Math.Abs(delta) > ring.HalfWidth + ring.Span * 0.35) return null;
        var raw = (int)Math.Floor((delta + ring.HalfWidth) / ring.Span);
        return Math.Clamp(raw, 0, ring.Items.Count - 1);
    }

    private void SetSub(int? index)
    {
        if (SubHighlighted == index) return;
        SubHighlighted = index;
        Notify(WheelChange.Highlight | WheelChange.Hub);
        if (index is { } sub && SubRing is { } ring && sub < ring.Items.Count) Announce(ring.Items[sub].Title);
    }

    // Pointer

    /// Tab under an offset from the center (none while the tabs are hidden or a destination is being chosen).
    public WheelMode? TabHit(double dx, double dy)
    {
        if (Choice != null || TabsHidden) return null;
        foreach (var mode in WheelModes.All)
        {
            var (x, y, w, h) = Geometry.TabRect(mode);
            if (dx >= x - 1 && dx <= x + w + 1 && dy >= y - 6 && dy <= y + h + 6) return mode;
        }
        return null;
    }

    /// How far from the center a click still belongs to the wheel.
    public double Reach => Geometry.Reach;

    public void PointerMoved(double dx, double dy)
    {
        if (!IsVisible) return;
        if (TabHit(dx, dy) is { } tab)
        {
            var changed = HoveredTab != tab;
            HoveredTab = tab;
            SetHighlight(null);
            SetSub(null);
            SetExpanded(null);
            PointerAngle = null;
            if (Origin == WheelOrigin.Drag) Spring("tab." + tab.Raw(), 0.26, () => SetMode(tab));
            Notify((changed ? WheelChange.Tabs : WheelChange.None) | WheelChange.Pointer);
            return;
        }
        if (HoveredTab != null)
        {
            HoveredTab = null;
            Notify(WheelChange.Tabs);
        }
        var distance = Math.Sqrt(dx * dx + dy * dy);
        var half = Geometry.BoxSide / 2;
        var inside = Math.Abs(dx) <= half && Math.Abs(dy) <= half;
        if (!inside || distance < Geometry.DeadZone || distance > Reach || Items.Count == 0)
        {
            SetHighlight(null);
            SetSub(null);
            if (distance < Geometry.DeadZone) SetExpanded(null);
            PointerAngle = null;
            CancelSpring();
            Notify(WheelChange.Pointer);
            return;
        }
        var angle = WheelGeometry.Angle(dx, dy);
        if (distance > Geometry.DiscRadius + 1 && SubRing is { } ring && SubIndex(angle) is { } sub)
        {
            SetHighlight(ring.ParentIndex);
            SetSub(sub);
            PointerAngle = ring.AngleOf(sub);
            CancelSpring();
            Notify(WheelChange.Pointer);
            return;
        }
        var span = Geometry.MainSpan(Items.Count);
        var index = (int)Math.Floor((angle + span / 2) / span) % Items.Count;
        PointerAngle = angle;
        SetHighlight(index);
        SetSub(null);
        SetExpanded(index);
        if (Origin == WheelOrigin.Drag && Items[index] is Item.More)
            Spring("more." + Page, 0.5, () => NextPage());
        else
            CancelSpring();
        Notify(WheelChange.Pointer);
    }

    private void SetHighlight(int? index)
    {
        if (Highlighted == index) return;
        Highlighted = index;
        Notify(WheelChange.Highlight | WheelChange.Hub);
        if (index is not { } slot || slot >= Segments.Count || !Platform.Feedback.ScreenReaderActive) return;
        var segment = Segments[slot];
        var line = segment.Title;
        if (segment.Unavailable) line += ", " + L("a11y.unavailable");
        if (segment.HasVariants && Items[slot].AsOption is { } option) line += ", " + LP("hint.options", option.Variants.Count);
        Announce(line);
    }

    private void Spring(string key, double delay, Action action)
    {
        if (key == springKey) return;
        CancelSpring();
        springKey = key;
        springTimer = Platform.Main.After(TimeSpan.FromSeconds(delay), () => Platform.Main.Post(() =>
        {
            if (springKey != key) return;
            springKey = null;
            action();
        }));
    }

    private void CancelSpring()
    {
        springKey = null;
        springTimer?.Dispose();
        springTimer = null;
    }

    // Committing

    /// flip: Alt was held (send the result the other way). forced: Ctrl+C / Ctrl+S.
    public void Commit(int? index = null, IReadOnlyList<string>? files = null, bool flip = false, ResultDestination? forced = null)
    {
        var slot = index ?? Highlighted;
        if (slot is not { } chosen || chosen < 0 || chosen >= Items.Count) return;
        switch (Items[chosen])
        {
            case Item.More:
                NextPage();
                break;
            case Item.Destination destination:
                if (Choice is not { } pending) return;
                Launch(pending.Option, pending.Mode, pending.Files, forced ?? destination.Value);
                break;
            case Item.Option item:
            {
                var parent = item.Value;
                var sub = chosen == Highlighted ? SubHighlighted : null;
                var option = sub is { } s && s >= 0 && s < parent.Variants.Count ? parent.Variants[s] : parent;
                if (sub == null && parent.Variants.Count > 1 && !IsDemo) option = parent.Variants[WheelLayout.Shared.PreferredVariant(parent)];
                var reason = option.Unavailable ?? parent.Unavailable;
                // A missing ffmpeg or rhwp isn't a dead end: the job downloads it first, right on its card.
                var needsDownload = Tools.IsDownload(reason);
                if (reason != null && !needsDownload)
                {
                    ShowFlash(reason);
                    Platform.Feedback.Beep();
                    return;
                }
                if (sub != null && option.Id != parent.Id && !IsDemo) WheelLayout.Shared.NoteUsed(parent.Id, option.Id);
                var targets = files is { Count: > 0 } ? files : Files;
                var display = Display(option, option.Id == parent.Id ? null : parent);
                if (needsDownload) display = Tools.Preparing(display, reason);
                if (ResolveDestination(display, flip, forced) is not { } destination)
                {
                    EnterChoice(new PendingChoice(display, targets, Mode));
                    return;
                }
                Launch(display, Mode, targets, destination);
                break;
            }
        }
    }

    /// A variant runs under its parent's name ("Rotate · Left 90°") so result cards stay readable.
    public static PickOption Display(PickOption option, PickOption? parent)
    {
        if (parent == null) return option;
        return option with
        {
            Title = parent.IsFormat ? parent.Title : parent.Title + " · " + option.Title,
            Symbol = option.Symbol.Length == 0 ? parent.Symbol : option.Symbol,
            IsFormat = parent.IsFormat,
            OutputExt = parent.OutputExt,
            Variants = [],
            PinnedFrom = null,
        };
    }

    private ResultDestination? ResolveDestination(PickOption option, bool flip, ResultDestination? forced)
    {
        if (!option.ProducesFiles) return ResultDestination.File;
        if (forced is { } value) return value;
        return (DestinationOverride ?? Preferences.Shared.Destination) switch
        {
            ResultDestination.Ask => flip ? ResultDestination.Clipboard : null,
            ResultDestination.File => flip ? ResultDestination.Clipboard : ResultDestination.File,
            ResultDestination.Clipboard => flip ? ResultDestination.File : ResultDestination.Clipboard,
            _ => flip ? ResultDestination.File : ResultDestination.Both,
        };
    }

    private void Launch(PickOption option, WheelMode mode, IReadOnlyList<string> files, ResultDestination destination)
    {
        var anchor = Center;
        Dismiss();
        Choice = null;
        Launched?.Invoke(option, mode, files, destination, anchor);
    }

    private void EnterChoice(PendingChoice pending)
    {
        Origin = WheelOrigin.Selection;
        SetExpanded(null);
        Choice = pending;
        RebuildItems();
        Highlighted = 0;
        SubHighlighted = null;
        PointerAngle = 0;
        Notify(WheelChange.All);
    }

    /// Drop of files on the wheel (drag origin). Returns false when nothing was under the pointer.
    public bool PerformDrop(double dx, double dy, IReadOnlyList<string> dropped, bool flip)
    {
        PointerMoved(dx, dy);
        if (!CanDropHere || Highlighted is not { } index)
        {
            Dismiss();
            return false;
        }
        Commit(index, dropped, flip);
        return true;
    }

    /// Returns true when the click belongs to the wheel (it is not passed on to the window below).
    public bool HandleClick(double dx, double dy, bool flip)
    {
        if (!IsVisible) return false;
        if (TabHit(dx, dy) is { } tab)
        {
            SetMode(tab);
            return true;
        }
        var distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance > Reach)
        {
            Dismiss();
            return false;
        }
        if (distance < Geometry.DeadZone)
        {
            Dismiss();
            return true;
        }
        PointerMoved(dx, dy);
        Commit(flip: flip);
        return true;
    }

    // Customizing (right-click)

    /// Remove, reorder, pin and pick defaults right where the slot is (this ring, this file type). Returns null when
    /// there is no slot there. openSettings gets the ring and the selection's file type (null when it mixes types).
    public (IReadOnlyList<MenuEntry> Entries, string? Keep)? ContextMenu(double dx, double dy, Action<WheelMode, FileKind?> openSettings)
    {
        if (!IsVisible || Choice != null) return null;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance >= Reach) return null;
        PointerMoved(dx, dy);
        if (HighlightedItem?.AsOption is not { } parent) return null;
        var variant = SubHighlighted is { } sub && sub >= 0 && sub < parent.Variants.Count ? parent.Variants[sub] : null;
        var layout = WheelLayout.Shared;
        var key = WheelLayout.Key(Mode, Files);
        var visible = OptionsFor(Mode).Select(option => option.Id).ToList();
        var entries = new List<MenuEntry>
        {
            new() { Title = variant == null ? parent.Title : parent.Title + " · " + variant.Title, IsHeader = true, Enabled = false },
            MenuEntry.Separator,
        };
        string? keep = parent.Id;
        if (variant != null)
        {
            // An option of a slot brought in from another ring can't hang under it: it comes over as a slot of its own.
            var borrowed = layout.IsAdded(parent.Id, key);
            var pinned = borrowed ? layout.IsAdded(variant.Id, key) : layout.IsPinned(variant.Id, key);
            entries.Add(new MenuEntry
            {
                Title = pinned ? L("ctx.unpin") : L("ctx.pin"),
                Symbol = pinned ? "pin.slash" : "pin",
                Run = () =>
                {
                    if (borrowed)
                    {
                        if (pinned) layout.Remove(variant.Id, key);
                        else layout.AddFromRing(variant.Id, key);
                    }
                    else if (pinned) layout.Unpin(variant.Id, key);
                    else layout.Pin(variant.Id, Display(variant, parent).Title, key);
                },
            });
            var isDefault = layout.Defaults.TryGetValue(parent.Id, out var chosen) && chosen == variant.Id;
            entries.Add(new MenuEntry
            {
                Title = L("ctx.make_default"),
                Symbol = "checkmark.circle",
                Checked = isDefault,
                Run = () => layout.SetDefault(parent.Id, isDefault ? null : variant.Id),
            });
        }
        else
        {
            if (parent.PinnedFrom is { } source)
            {
                entries.Add(new MenuEntry { Title = L("ctx.unpin"), Symbol = "pin.slash", Run = () => layout.Unpin(parent.Id, key) });
                keep = source;
            }
            else
            {
                // Out of this ring for this file type only; Settings › Wheel › Add Slot brings it back.
                entries.Add(new MenuEntry { Title = L("ctx.hide"), Symbol = "minus.circle", Run = () => layout.Remove(parent.Id, key) });
                keep = null;
            }
            var position = Math.Max(0, visible.IndexOf(parent.Id));
            entries.Add(new MenuEntry
            {
                Title = L("ctx.move_earlier"),
                Symbol = "arrow.counterclockwise",
                Enabled = position > 0,
                Run = () => layout.Move(parent.Id, -1, visible, key),
            });
            entries.Add(new MenuEntry
            {
                Title = L("ctx.move_later"),
                Symbol = "arrow.clockwise",
                Enabled = position < visible.Count - 1,
                Run = () => layout.Move(parent.Id, 1, visible, key),
            });
            if (parent.Variants.Count > 1)
            {
                var explicitDefault = layout.Defaults.TryGetValue(parent.Id, out var value) ? value : null;
                var submenu = new List<MenuEntry>
                {
                    new() { Title = L("ctx.default_auto"), Checked = explicitDefault == null, Run = () => layout.SetDefault(parent.Id, null) },
                    MenuEntry.Separator,
                };
                submenu.AddRange(parent.Variants.Select(option => new MenuEntry
                {
                    Title = option.Title,
                    Checked = explicitDefault == option.Id,
                    Run = () => layout.SetDefault(parent.Id, option.Id),
                }));
                entries.Add(new MenuEntry { Title = L("ctx.default"), Symbol = "checkmark.circle", Submenu = submenu });
            }
        }
        entries.Add(MenuEntry.Separator);
        if (layout.IsCustomizedKey(key)) entries.Add(new MenuEntry { Title = L("ctx.reset_ring"), Symbol = "arrow.uturn.backward", Run = () => layout.Reset(key) });
        var kinds = Files.Select(FileKinds.Of).Distinct().ToList();
        FileKind? kind = kinds.Count == 1 ? kinds[0] : null;
        var ring = Mode;
        entries.Add(new MenuEntry
        {
            Title = L("ctx.settings"),
            Symbol = "gearshape",
            Run = () =>
            {
                Dismiss();
                openSettings(ring, kind);
            },
        });
        return (entries, keep);
    }

    /// After the right-click menu closed: show the changes and keep the slot where the pointer is.
    public void AfterContextMenu(string? keep)
    {
        if (IsVisible) RefreshLayout(keep);
    }

    // Keyboard

    /// Returns true when the key is consumed by the wheel.
    public bool HandleKey(KeyInput input)
    {
        if (!IsVisible) return false;
        Debug.Log($"key {input.Key} origin={Origin}");
        if (Origin == WheelOrigin.Drag)
        {
            if (input.Key == WheelKey.Escape) Dismiss();
            return false;
        }
        if (input.Key == WheelKey.System)
        {
            Dismiss();
            return false;
        }
        if (input.Ctrl)
        {
            if (input.Key == WheelKey.Letter && input.Character is 'c' or 's')
            {
                Commit(forced: input.Character == 'c' ? ResultDestination.Clipboard : ResultDestination.File);
                return true;
            }
            Dismiss();
            return false;
        }
        if (input.Alt)
        {
            Dismiss();
            return false;
        }
        switch (input.Key)
        {
            case WheelKey.Escape:
                Dismiss();
                break;
            case WheelKey.Enter:
            case WheelKey.Space:
                Commit();
                break;
            case WheelKey.Left:
                MoveHighlight(-1);
                break;
            case WheelKey.Right:
                MoveHighlight(1);
                break;
            case WheelKey.Up:
                MoveVariant(-1);
                break;
            case WheelKey.Down:
                MoveVariant(1);
                break;
            case WheelKey.Tab:
                if (Choice == null) CycleMode(input.Shift ? -1 : 1);
                break;
            case WheelKey.PageUp:
                NextPage(-1);
                break;
            case WheelKey.PageDown:
                NextPage(1);
                break;
            case WheelKey.Home:
                if (Items.Count > 0) Select(0);
                break;
            case WheelKey.End:
                if (Items.Count > 0) Select(Items.Count - 1);
                break;
            case WheelKey.Digit:
            {
                var number = input.Digit == 0 ? 10 : input.Digit;
                if (number - 1 < Items.Count)
                {
                    SetHighlight(number - 1);
                    SetSub(null);
                    Commit();
                }
                break;
            }
            case WheelKey.Letter:
                if (Choice == null) Typeahead(char.ToLowerInvariant(input.Character));
                break;
        }
        return true;
    }

    private void Select(int index)
    {
        SetHighlight(index);
        SetSub(null);
        SetExpanded(index);
        PointerAngle = index * Geometry.MainSpan(Items.Count);
        Notify(WheelChange.Pointer);
    }

    private void MoveHighlight(int step)
    {
        if (Items.Count == 0) return;
        var count = Items.Count;
        var next = Highlighted is { } current ? (current + step + count) % count : (step > 0 ? 0 : count - 1);
        Select(next);
    }

    /// ↑ ↓ walk the outer ring of the highlighted slot (or the main ring when it has none).
    private void MoveVariant(int step)
    {
        if (Highlighted is not { } current || current >= Items.Count || Items[current].Variants.Count == 0)
        {
            MoveHighlight(step);
            return;
        }
        SetExpanded(current);
        var count = Items[current].Variants.Count;
        var next = SubHighlighted is { } sub ? (sub + step + count) % count : (step > 0 ? 0 : count - 1);
        SetSub(next);
        if (SubRing is { } ring) PointerAngle = ring.AngleOf(next);
        Notify(WheelChange.Pointer);
    }

    private void Typeahead(char letter)
    {
        typeBuffer += letter;
        typeTimer?.Dispose();
        typeTimer = Platform.Main.After(TimeSpan.FromSeconds(1.1), () => Platform.Main.Post(() => typeBuffer = ""));
        var options = OptionsFor(Mode);
        var query = typeBuffer;
        var match = -1;
        for (var index = 0; index < options.Count; index++)
        {
            if (options[index].Title.StartsWith(query, StringComparison.CurrentCultureIgnoreCase))
            {
                match = index;
                break;
            }
        }
        if (match < 0)
        {
            for (var index = 0; index < options.Count; index++)
            {
                if (options[index].Id.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    match = index;
                    break;
                }
            }
        }
        if (match < 0) return;
        var slot = match;
        if (PageCount > 1)
        {
            var perPage = MaxPerPage - 1;
            var targetPage = match / perPage;
            if (targetPage != Page)
            {
                SetExpanded(null);
                Page = targetPage;
                RebuildItems();
                Notify(WheelChange.Items);
            }
            slot = match % perPage;
        }
        Select(slot);
    }

    private void ShowFlash(string message)
    {
        Flash = message;
        Notify(WheelChange.Hub);
        flashTimer?.Dispose();
        flashTimer = Platform.Main.After(TimeSpan.FromSeconds(1.8), () => Platform.Main.Post(() =>
        {
            Flash = null;
            Notify(WheelChange.Hub);
        }));
    }

    private static void Announce(string text)
    {
        if (Platform.Feedback.ScreenReaderActive && text.Length > 0) Platform.Feedback.Announce(text);
    }

    // Hub text

    public string FilesTitle => Files.Count == 0 ? "" : Files.Count == 1 ? FileNames.Name(Files[0]) : LP("wheel.items", Files.Count);

    public string Hint => Origin == WheelOrigin.Drag ? L("wheel.hint.drag") : L("wheel.hint.click");

    public string PreviewLine(PickOption option, PickOption? parent)
    {
        var format = parent?.IsFormat == true ? parent : option.IsFormat ? option : null;
        if (format?.OutputExt is { } ext)
        {
            if (Files.Count == 1) return FileNames.Base(Files[0]) + "." + ext;
            return LP("wheel.files_to", Files.Count, format.Title);
        }
        if (parent != null) return parent.Title;
        return option.Detail;
    }

    /// Small reminder of the Alt-click override, shown only for options that create files.
    public string? DestinationHint(PickOption option)
    {
        if (!option.ProducesFiles || Choice != null) return null;
        return Preferences.Shared.Destination is ResultDestination.File or ResultDestination.Ask ? L("hint.alt_clipboard") : L("hint.alt_file");
    }

    // Demo, snapshots and tests

    public void DemoHighlight(int? index, int? sub = null)
    {
        if (index is not { } slot || slot < 0 || slot >= Items.Count)
        {
            Highlighted = null;
            SubHighlighted = null;
            SubRing = null;
            TabsHidden = false;
            PointerAngle = null;
            Notify(WheelChange.All);
            return;
        }
        Highlighted = slot;
        SetExpanded(slot);
        SubHighlighted = sub;
        PointerAngle = sub is { } s && SubRing is { } ring ? ring.AngleOf(s) : slot * Geometry.MainSpan(Items.Count);
        Notify(WheelChange.All);
    }

    public void DemoSetMode(WheelMode mode)
    {
        SubRing = null;
        TabsHidden = false;
        Mode = mode;
        Page = 0;
        Highlighted = null;
        SubHighlighted = null;
        PointerAngle = null;
        RebuildItems();
        Notify(WheelChange.All);
    }

    public void DemoChoice()
    {
        if (OptionsFor(Mode).FirstOrDefault() is not { } option) return;
        Choice = new PendingChoice(option, Files, Mode);
        RebuildItems();
        Highlighted = 0;
        PointerAngle = 0;
        Notify(WheelChange.All);
    }

    public void ForceVisibleForTesting() => IsVisible = true;

    // Settings preview

    /// Settings › Wheel: shows rings that are already arranged (every slot of one file type) without opening anything.
    /// The page stays when the ring does.
    public void ShowLayout(IReadOnlyList<string> files, WheelMode mode, IReadOnlyDictionary<WheelMode, List<PickOption>> rings)
    {
        var sameRing = mode == Mode && files.SequenceEqual(Files);
        Files = files;
        Origin = WheelOrigin.Selection;
        Choice = null;
        var counts = new Dictionary<WheelMode, int>();
        foreach (var ring in WheelModes.All)
        {
            optionsByMode[ring] = rings.TryGetValue(ring, out var list) ? [.. list] : [];
            counts[ring] = optionsByMode[ring].Count;
        }
        Counts = counts;
        Mode = mode;
        if (!sameRing) Page = 0;
        RebuildItems();
        Highlighted = null;
        SubHighlighted = null;
        SubRing = null;
        TabsHidden = false;
        HoveredTab = null;
        PointerAngle = null;
        Flash = null;
        Notify(WheelChange.All);
    }

    /// Highlights one slot of the whole ring, turning to its page, without opening its outer ring (null: none).
    public void PreviewHighlight(int? index)
    {
        SubRing = null;
        SubHighlighted = null;
        TabsHidden = false;
        var count = OptionsFor(Mode).Count;
        if (index is not { } slot || slot < 0 || slot >= count || Choice != null)
        {
            Highlighted = null;
            PointerAngle = null;
            Notify(WheelChange.All);
            return;
        }
        if (count > MaxPerPage)
        {
            var perPage = MaxPerPage - 1;
            if (Page != slot / perPage)
            {
                Page = slot / perPage;
                RebuildItems();
            }
            slot %= perPage;
        }
        Highlighted = slot;
        PointerAngle = slot * Geometry.MainSpan(Items.Count);
        Notify(WheelChange.All);
    }
}
