using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Pickverter.Input;
using Pickverter.Interop;
using Pickverter.Services;

namespace Pickverter.UI;

/// One window per result card, so each gets the real Windows shadow and rounded corners. Cards stack in the
/// corner chosen in Settings, on the screen where the work started, and never take the keyboard focus.
internal sealed class CardPresenter : IJobPresenter
{
    public const double CardWidth = 368;
    private const double Gap = 10;
    private const double Inset = 12;

    private readonly List<CardWindow> cards = [];
    private readonly Dictionary<Guid, IDisposable> timers = new();
    private readonly HashSet<Guid> hovered = [];
    private Native.POINT? screenPoint;

    public void Show(Job job)
    {
        if (cards.Any(card => card.Job.Id == job.Id))
        {
            Layout();
            return;
        }
        if (cards.Count == 0)
        {
            Native.GetCursorPos(out var cursor);
            screenPoint = job.Anchor.IsZero ? cursor : new Native.POINT((int)job.Anchor.X, (int)job.Anchor.Y);
        }
        var window = new CardWindow(job, this);
        cards.Insert(0, window);
        while (cards.Count > 4) Remove(cards[^1].Job);
        Layout(window);
    }

    public void Remove(Job job)
    {
        if (timers.Remove(job.Id, out var timer)) timer.Dispose();
        hovered.Remove(job.Id);
        var card = cards.FirstOrDefault(entry => entry.Job.Id == job.Id);
        if (card == null) return;
        cards.Remove(card);
        card.Close();
        Layout();
    }

    public void SetHovered(Job job, bool inside)
    {
        if (inside) hovered.Add(job.Id);
        else hovered.Remove(job.Id);
    }

    public void ScheduleDismiss(Job job)
    {
        double seconds;
        if (job.Lingers is { } lingers) seconds = lingers;
        else seconds = job.State switch
        {
            JobState.Failed => 8,
            JobState.Succeeded => job.Outputs.Count == 0 ? (job.IsNotice ? 6 : 2.6) : 6,
            _ => 1.6,
        };
        if (timers.Remove(job.Id, out var previous)) previous.Dispose();
        timers[job.Id] = Platform.Main.After(TimeSpan.FromSeconds(seconds), () => Expire(job));
    }

    private void Expire(Job job)
    {
        if (hovered.Count > 0)
        {
            timers[job.Id] = Platform.Main.After(TimeSpan.FromMilliseconds(400), () => Expire(job));
            return;
        }
        Remove(job);
    }

    /// Stacks the cards from the chosen corner of the work area (taskbar excluded).
    public void Layout(CardWindow? appearing = null)
    {
        if (cards.Count == 0)
        {
            screenPoint = null;
            return;
        }
        var anchor = screenPoint ?? new Native.POINT(0, 0);
        var (_, work, scale) = Native.MonitorAt(anchor.X, anchor.Y);
        var corner = Preferences.Shared.HudPosition;
        var width = (int)Math.Round(CardWidth * scale);
        var inset = (int)Math.Round(Inset * scale);
        var gap = (int)Math.Round(Gap * scale);
        var x = corner.IsLeft() ? work.Left + inset : work.Right - width - inset;
        var edge = corner.IsTop() ? work.Top + inset : work.Bottom - inset;
        foreach (var card in cards)
        {
            var height = (int)Math.Round(card.MeasureHeight() * scale);
            var y = corner.IsTop() ? edge : edge - height;
            card.MoveTo(x, y, width, height, card == appearing, corner.IsTop());
            edge = corner.IsTop() ? edge + height + gap : edge - height - gap;
        }
    }
}

internal sealed class CardWindow : Window
{
    private readonly CardPresenter presenter;
    private readonly Border root = new();
    private readonly ProgressRing ring = new() { Width = 34, Height = 34 };
    private IntPtr hwnd;
    private DispatcherTimer? mover;
    private BitmapSource? preview;

    public Job Job { get; }

    public CardWindow(Job job, CardPresenter presenter)
    {
        Job = job;
        this.presenter = presenter;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Title = job.Headline;
        Width = CardPresenter.CardWidth;
        SizeToContent = SizeToContent.Manual;
        Background = Theme.Surface;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
        Content = root;
        root.Background = Theme.Surface;
        Announcer.Register(root);
        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            Windowing.MakeOverlay(this);
            Native.RemoveStyle(hwnd, Native.WS_MAXIMIZEBOX | Native.WS_MINIMIZEBOX);
            Windowing.Round(this);
        };
        MouseEnter += (_, _) => presenter.SetHovered(job, true);
        MouseLeave += (_, _) => presenter.SetHovered(job, false);
        job.PropertyChanged += OnJobChanged;
        Closed += (_, _) => job.PropertyChanged -= OnJobChanged;
        Theme.Changed += Rebuild;
        Loc.Changed += Rebuild;
        Closed += (_, _) =>
        {
            Theme.Changed -= Rebuild;
            Loc.Changed -= Rebuild;
        };
        Build();
        _ = LoadPreview();
    }

    private void OnJobChanged(object? sender, PropertyChangedEventArgs args)
    {
        switch (args.PropertyName)
        {
            case nameof(Job.Progress):
                ring.Value = Job.Progress;
                if (progressBar != null) progressBar.Value = Job.Progress;
                return;
            case nameof(Job.Note):
                return;
            case nameof(Job.Headline):
                if (headline != null && headline.Text != Job.Headline) headline.Text = Job.Headline;
                return;
            case nameof(Job.Subline):
                if (subline != null && subline.Text != Job.Subline)
                {
                    subline.Text = Job.Subline;
                    presenter.Layout();
                }
                return;
            case nameof(Job.Outputs):
                _ = LoadPreview();
                break;
        }
        Build();
        presenter.Layout();
    }

    internal void Rebuild()
    {
        Background = Theme.Surface;
        root.Background = Theme.Surface;
        Windowing.Round(this);
        Build();
        presenter.Layout();
    }

    private async Task LoadPreview()
    {
        if (Job.Outputs.Count == 0 || Job.Undone) return;
        var image = await Explorer.ThumbnailAsync(Job.Outputs[0], 72);
        if (image == null) return;
        preview = image;
        Build();
    }

    private LinearProgress? progressBar;
    private TextBlock? headline;
    private TextBlock? subline;

    private void Build()
    {
        Title = Job.Headline;
        var layout = new Grid { Margin = new Thickness(14, 12, 12, 12) };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var badge = Badge();
        badge.VerticalAlignment = VerticalAlignment.Top;
        layout.Children.Add(badge);

        var texts = new SpacedStack { Spacing = 3, Margin = new Thickness(11, 0, 6, 0) };
        headline = Ui.Text(Job.Headline, 13, FontWeights.SemiBold);
        texts.Children.Add(headline);
        subline = new TextBlock
        {
            Text = Job.Subline,
            FontSize = 12,
            FontFamily = Theme.SmallFont,
            Foreground = Job.State == JobState.Failed && Job.Tone == JobTone.Warning ? Theme.TextPrimary : Theme.TextSecondary,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 3 * 16.5,
            LineHeight = 16,
        };
        texts.Children.Add(subline);
        if (Job.IsRunning)
        {
            progressBar = new LinearProgress { Value = Job.Progress, Margin = new Thickness(0, 6, 0, 0) };
            texts.Children.Add(progressBar);
        }
        else
        {
            progressBar = null;
        }
        Grid.SetColumn(texts, 1);
        layout.Children.Add(texts);

        var close = FlatButton.IconOnly(Job.IsRunning ? L("card.stop") : L("card.close"), Job.IsRunning ? "stop.fill" : "xmark", () =>
        {
            if (Job.IsRunning) Job.Cancel();
            else presenter.Remove(Job);
        }, compact: true, subtle: true);
        close.Width = 26;
        close.VerticalAlignment = VerticalAlignment.Top;
        close.Margin = new Thickness(0, -4, -4, 0);
        Grid.SetColumn(close, 2);
        layout.Children.Add(close);

        var actions = Actions();
        if (actions != null)
        {
            actions.Margin = new Thickness(0, 10, 0, 0);
            Grid.SetRow(actions, 1);
            Grid.SetColumnSpan(actions, 3);
            layout.Children.Add(actions);
        }
        root.Child = layout;
        AutomationProperties.SetName(root, Job.Headline);
        AutomationProperties.SetHelpText(root, Job.Subline);
    }

    private FrameworkElement Badge()
    {
        var host = new Grid { Width = 34, Height = 34 };
        switch (Job.State)
        {
            case JobState.Running:
                ring.Value = Job.Progress;
                host.Children.Add(Ui.Detach(ring));
                host.Children.Add(Glyphs.Icon(Job.Symbol, 13, Theme.TextSecondary));
                break;
            case JobState.Succeeded when preview != null && !Job.Undone:
                host.Children.Add(new Image { Source = preview, Stretch = Stretch.Uniform, Width = 34, Height = 34 });
                break;
            case JobState.Succeeded:
                host.Children.Add(new Border { CornerRadius = new CornerRadius(17), Background = Theme.Tile });
                host.Children.Add(Glyphs.Icon(Job.IsNotice ? Job.Symbol : Job.Outputs.Count == 0 ? "checkmark" : Job.Symbol, 15, Theme.AccentText));
                break;
            case JobState.Failed:
                var informational = Job.IsNotice && Job.Tone == JobTone.Info;
                host.Children.Add(new Border { CornerRadius = new CornerRadius(17), Background = informational ? Theme.Tile : Theme.CautionSubtle });
                host.Children.Add(Glyphs.Icon(Job.IsNotice ? Job.Symbol : "exclamationmark", 15, informational ? Theme.AccentText : Theme.Caution));
                break;
            default:
                host.Children.Add(new Border { CornerRadius = new CornerRadius(17), Background = Theme.Tile });
                host.Children.Add(Glyphs.Icon("stop.fill", 12, Theme.TextSecondary));
                break;
        }
        return host;
    }

    /// The button row. It never wraps: when the labels don't fit, the secondary buttons shrink to icons.
    private FrameworkElement? Actions()
    {
        if (Job.State == JobState.Succeeded && Job.HasFiles)
        {
            var fits = new ViewThatFits();
            fits.Children.Add(ActionRow(compact: false));
            fits.Children.Add(ActionRow(compact: true));
            return fits;
        }
        var action = Job.Notice ?? (Job.IsRunning ? null : Job.FixAction);
        if (action == null) return null;
        var button = Ui.Button(action.Title, () =>
        {
            action.Run();
            presenter.Remove(Job);
        }, prominent: true, icon: action.Symbol, compact: true);
        return button;
    }

    private FrameworkElement ActionRow(bool compact)
    {
        var row = Ui.Row(6);
        if (Job.InCache)
        {
            row.Children.Add(Ui.Button(L("card.save"), () => _ = Job.SaveFromCache(), prominent: true, icon: "square.and.arrow.down", compact: true));
        }
        else
        {
            row.Children.Add(Ui.Button(L("card.reveal"), Job.Reveal, prominent: true, icon: "folder", compact: true));
        }
        row.Children.Add(Secondary(L("card.open"), "arrow.up.forward.app", Job.Open, compact));
        if (Job.CanCompare)
        {
            var before = Job.Files[0];
            var after = Job.Outputs[0];
            row.Children.Add(Secondary(L("card.compare"), "square.split.2x1", () => CompareWindow.Open(before, after), compact));
        }
        if (!Job.Copied) row.Children.Add(Secondary(L("card.copy"), "doc.on.clipboard", () => _ = Job.CopyToClipboard(), compact));
        if (Job.Undoable && !Job.InCache) row.Children.Add(Secondary(L("card.undo"), "arrow.uturn.backward", () => _ = Job.Undo(), compact));
        return row;
    }

    private static FlatButton Secondary(string title, string icon, Action click, bool compact) =>
        compact ? FlatButton.IconOnly(title, icon, click) : Ui.Button(title, click, icon: icon, compact: true);

    public double MeasureHeight()
    {
        root.Measure(new Size(CardPresenter.CardWidth, double.PositiveInfinity));
        return Math.Max(60, Math.Ceiling(root.DesiredSize.Height));
    }

    /// Positions the card in physical pixels; a new card slides in from the screen edge.
    public void MoveTo(int x, int y, int width, int height, bool appearing, bool fromTop)
    {
        if (hwnd == IntPtr.Zero)
        {
            hwnd = new WindowInteropHelper(this).EnsureHandle();
        }
        if (appearing || !IsVisible)
        {
            var offset = (int)Math.Round(10 * (fromTop ? -1 : 1) * (Native.GetDpiForWindow(hwnd) / 96.0));
            var after = InsertAfter();
            Native.SetWindowPos(hwnd, after, x, y + offset, width, height, Native.SWP_NOACTIVATE);
            Show();
            Native.SetWindowPos(hwnd, after, x, y + offset, width, height, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            Animate(x, y + offset, x, y, width, height);
            return;
        }
        Native.GetWindowRect(hwnd, out var current);
        if (current.Left == x && current.Top == y && current.Width == width && current.Height == height) return;
        Animate(current.Left, current.Top, x, y, width, height);
    }

    /// A new card goes above every window, except a Pickverter dialog the user is working in (an editor, the password
    /// prompt, the folder picker): it slides in underneath, so it never covers the dialog's buttons.
    private IntPtr InsertAfter()
    {
        var front = Native.GetForegroundWindow();
        if (front == IntPtr.Zero || front == hwnd) return Native.HWND_TOPMOST;
        Native.GetWindowThreadProcessId(front, out var process);
        if (process != (uint)Environment.ProcessId) return Native.HWND_TOPMOST;
        var root = Native.GetAncestor(front, Native.GA_ROOTOWNER);
        if (root == IntPtr.Zero) root = front;
        var topmost = (Native.GetWindowLongPtr(root, Native.GWL_EXSTYLE).ToInt64() & Native.WS_EX_TOPMOST) != 0;
        return topmost ? front : Native.HWND_TOPMOST;
    }

    // Moving never changes the stacking (SWP_NOZORDER): a card that slid in under a dialog stays under it.
    private void Animate(int fromX, int fromY, int toX, int toY, int width, int height)
    {
        mover?.Stop();
        const uint flags = Native.SWP_NOACTIVATE | Native.SWP_NOZORDER;
        if (!SystemParameters.ClientAreaAnimation)
        {
            Native.SetWindowPos(hwnd, IntPtr.Zero, toX, toY, width, height, flags);
            return;
        }
        var started = DateTime.UtcNow;
        const double duration = 180;
        mover = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(12) };
        mover.Tick += (_, _) =>
        {
            var t = Math.Min(1, (DateTime.UtcNow - started).TotalMilliseconds / duration);
            var eased = 1 - Math.Pow(1 - t, 3);
            var x = (int)Math.Round(fromX + (toX - fromX) * eased);
            var y = (int)Math.Round(fromY + (toY - fromY) * eased);
            Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, flags);
            if (t >= 1) mover?.Stop();
        };
        mover.Start();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        e.Handled = true;
    }
}
