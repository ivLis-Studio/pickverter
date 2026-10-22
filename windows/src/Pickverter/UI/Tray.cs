using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Pickverter.Input;
using Pickverter.Interop;

namespace Pickverter.UI;

/// The notification-area icon (Pickverter's home on Windows, like the menu bar on a Mac).
internal sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = Native.WM_USER + 0x50;
    private const uint IconId = 1;
    private readonly HwndSource source;
    private readonly uint taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
    private IntPtr icon;
    private bool added;

    public event Action<Native.POINT>? Clicked;
    public event Action<Native.POINT>? MenuRequested;

    public TrayIcon()
    {
        var parameters = new HwndSourceParameters("Pickverter tray") { Width = 0, Height = 0, WindowStyle = 0 };
        source = new HwndSource(parameters);
        source.AddHook(WndProc);
        Add();
    }

    private Native.NOTIFYICONDATA Data(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<Native.NOTIFYICONDATA>(),
        hWnd = source.Handle,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = icon,
        szTip = Tooltip,
        szInfo = "",
        szInfoTitle = "",
    };

    private static string Tooltip
    {
        get
        {
            var state = !InputMonitor.Shared.IsRunning ? L("menu.status.hook") : Preferences.Shared.Enabled ? null : L("menu.status.paused");
            return state == null ? "Pickverter" : "Pickverter · " + state;
        }
    }

    private void Add()
    {
        LoadIcon();
        var data = Data(Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP);
        added = Native.Shell_NotifyIcon(Native.NIM_ADD, ref data);
        data.uVersion = Native.NOTIFYICON_VERSION_4;
        Native.Shell_NotifyIcon(Native.NIM_SETVERSION, ref data);
    }

    public void Refresh()
    {
        LoadIcon();
        var data = Data(Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP);
        if (!Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref data) && !added) Add();
    }

    private void LoadIcon()
    {
        var previous = icon;
        var dpi = Native.GetDpiForWindow(source.Handle);
        var size = Native.GetSystemMetricsForDpi(Native.SM_CXSMICON, dpi == 0 ? 96 : dpi);
        icon = IconLoader.Load(Theme.TaskbarIsDark() ? "Tray-Dark.ico" : "Tray-Light.ico", size);
        if (previous != IntPtr.Zero) Native.DestroyIcon(previous);
    }

    /// Where the icon sits (for placing the flyout above it).
    public Native.RECT? Bounds
    {
        get
        {
            var id = new Native.NOTIFYICONIDENTIFIER { cbSize = Marshal.SizeOf<Native.NOTIFYICONIDENTIFIER>(), hWnd = source.Handle, uID = IconId };
            return Native.Shell_NotifyIconGetRect(ref id, out var rect) == 0 ? rect : null;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == (int)taskbarCreated)
        {
            added = false;
            Add();
            return IntPtr.Zero;
        }
        if (message == Native.WM_SETTINGCHANGE || message == Native.WM_DPICHANGED)
        {
            Platform.Main.Post(Refresh);
            return IntPtr.Zero;
        }
        if (message != CallbackMessage) return IntPtr.Zero;
        var code = (int)(lParam.ToInt64() & 0xFFFF);
        var packed = wParam.ToInt64();
        var point = new Native.POINT((short)(packed & 0xFFFF), (short)((packed >> 16) & 0xFFFF));
        switch (code)
        {
            case Native.NIN_SELECT:
            case Native.NIN_KEYSELECT:
                handled = true;
                Clicked?.Invoke(point);
                break;
            case Native.WM_CONTEXTMENU:
                handled = true;
                MenuRequested?.Invoke(point);
                break;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        var data = Data(0);
        Native.Shell_NotifyIcon(Native.NIM_DELETE, ref data);
        if (icon != IntPtr.Zero) Native.DestroyIcon(icon);
        source.Dispose();
    }
}

/// Picks the best image for a size from an .ico embedded in the app.
internal static class IconLoader
{
    public static IntPtr Load(string resource, int size)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
        if (stream == null) return IntPtr.Zero;
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        var count = BitConverter.ToUInt16(bytes, 4);
        var best = -1;
        var bestSize = 0;
        for (var index = 0; index < count; index++)
        {
            var entry = 6 + index * 16;
            var width = bytes[entry] == 0 ? 256 : bytes[entry];
            if (best < 0 || (width >= size && (bestSize < size || width < bestSize)) || (bestSize < size && width > bestSize))
            {
                best = index;
                bestSize = width;
            }
        }
        if (best < 0) return IntPtr.Zero;
        var at = 6 + best * 16;
        var length = BitConverter.ToInt32(bytes, at + 8);
        var offset = BitConverter.ToInt32(bytes, at + 12);
        var image = new byte[length];
        Array.Copy(bytes, offset, image, 0, length);
        return Native.CreateIconFromResourceEx(image, (uint)length, true, 0x00030000, size, size, 0);
    }
}

/// The flyout that opens from the tray icon: status, how to open the wheel, where results go, recent jobs.
internal sealed class TrayFlyout : Window
{
    private readonly Border root = new();

    public TrayFlyout()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        Topmost = true;
        Width = 336;
        SizeToContent = SizeToContent.Height;
        Title = "Pickverter";
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(0, 0, 0, 1),
            UseAeroCaptionButtons = false,
        });
        Content = root;
        SourceInitialized += (_, _) =>
        {
            Windowing.MakeOverlay(this, noActivate: false);
            Native.RemoveStyle(new WindowInteropHelper(this).Handle, Native.WS_MAXIMIZEBOX | Native.WS_MINIMIZEBOX);
            Windowing.Round(this);
        };
        Deactivated += (_, _) => Hide();
        KeyDown += (_, args) =>
        {
            if (args.Key == System.Windows.Input.Key.Escape) Hide();
        };
        Theme.Changed += Rebuild;
        Loc.Changed += Rebuild;
        Preferences.Shared.PropertyChanged += OnChanged;
        Updater.Shared.PropertyChanged += OnChanged;
        Closed += (_, _) =>
        {
            Theme.Changed -= Rebuild;
            Loc.Changed -= Rebuild;
            Preferences.Shared.PropertyChanged -= OnChanged;
            Updater.Shared.PropertyChanged -= OnChanged;
        };
    }

    private void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (IsVisible) Rebuild();
    }

    public void Rebuild()
    {
        Background = Theme.Surface;
        root.Background = Theme.Surface;
        root.Child = Build();
    }

    public void Toggle(Native.RECT? icon)
    {
        if (IsVisible)
        {
            Hide();
            return;
        }
        Rebuild();
        Show();
        Place(icon);
        Windowing.Activate(this);
    }

    private void Place(Native.RECT? icon)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.GetCursorPos(out var cursor);
        var anchor = icon is { } rect ? new Native.POINT((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2) : cursor;
        var (bounds, work, scale) = Native.MonitorAt(anchor.X, anchor.Y);
        root.Measure(new Size(Width, double.PositiveInfinity));
        var width = (int)Math.Round(Width * scale);
        var height = (int)Math.Round(root.DesiredSize.Height * scale);
        var gap = (int)Math.Round(12 * scale);
        int x, y;
        if (work.Bottom < bounds.Bottom)
        {
            x = anchor.X - width / 2;
            y = work.Bottom - height - gap;
        }
        else if (work.Top > bounds.Top)
        {
            x = anchor.X - width / 2;
            y = work.Top + gap;
        }
        else if (work.Left > bounds.Left)
        {
            x = work.Left + gap;
            y = anchor.Y - height / 2;
        }
        else
        {
            x = work.Right - width - gap;
            y = work.Bottom - height - gap;
        }
        x = Math.Clamp(x, work.Left + gap, work.Right - width - gap);
        y = Math.Clamp(y, work.Top + gap, work.Bottom - height - gap);
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, width, height, 0);
    }

    private UIElement Build()
    {
        var prefs = Preferences.Shared;
        var updater = Updater.Shared;
        var stack = new SpacedStack { Spacing = 14, Margin = new Thickness(16) };

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new Image { Source = BrandArt.Glyph(), Width = 30, Height = 30, Margin = new Thickness(0, 0, 10, 0) });
        var status = !InputMonitor.Shared.IsRunning ? L("menu.status.hook") : prefs.Enabled ? L("menu.status.ready") : L("menu.status.paused");
        var title = Ui.Column(1, Ui.Text("Pickverter", 14, FontWeights.SemiBold), Ui.Text(status, 12, brush: Theme.TextSecondary, wrap: true));
        title.Margin = new Thickness(0, 0, 10, 0);
        Grid.SetColumn(title, 1);
        header.Children.Add(title);
        var toggle = Ui.Toggle(prefs.Enabled, value => prefs.Enabled = value, L("set.enable"));
        Grid.SetColumn(toggle, 2);
        header.Children.Add(toggle);
        stack.Children.Add(header);

        if (!InputMonitor.Shared.IsRunning)
        {
            stack.Children.Add(Banner("hand.raised", L("menu.status.hook"), L("perm.input.failed"), null, null));
        }
        else if (Services.FolderProtection.State == Services.FolderProtectionState.Blocking)
        {
            stack.Children.Add(Banner("lock.shield", L("perm.cfa.title"), L("perm.cfa.blocking"), L("perm.cfa.allow"),
                () => _ = AllowFolders()));
        }

        var how = Ui.Column(8, Section(L("menu.how")));
        var trigger = prefs.TriggerKey.Glyph();
        how.Children.Add(Gesture([trigger], null, prefs.DoubleTap ? L("menu.double") : L("menu.tap"), L("menu.tap.text")));
        how.Children.Add(Gesture([trigger], null, L("menu.click"), L("menu.click.text")));
        how.Children.Add(Gesture([KeyNames.Shift, KeyNames.Ctrl, KeyNames.Alt], L("menu.drag"), null, L("menu.drag.text")));
        stack.Children.Add(how);

        var destination = new Grid();
        destination.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        destination.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        destination.Children.Add(Section(L("set.destination")));
        var combo = Ui.Combo(ResultDestinations.All.Select(value => (value, value.Label())), prefs.Destination, value => prefs.Destination = value,
            L("set.destination"), 150);
        Grid.SetColumn(combo, 1);
        destination.Children.Add(combo);
        stack.Children.Add(destination);

        var recent = JobCenter.Shared.Jobs.Where(job => !job.IsNotice).Take(4).ToList();
        if (recent.Count > 0)
        {
            var list = Ui.Column(6, Section(L("menu.recent")));
            foreach (var job in recent) list.Children.Add(Recent(job));
            stack.Children.Add(list);
        }

        if (updater.Latest is { } latest && updater.Phase is Updater.UpdatePhase.Available or Updater.UpdatePhase.Ready)
        {
            var ready = updater.Phase == Updater.UpdatePhase.Ready;
            stack.Children.Add(Banner("arrow.down.circle", L("menu.update", latest.Version), null,
                ready ? L("update.restart") : L("update.card.details"), () =>
                {
                    Hide();
                    if (ready) updater.InstallAndRelaunch();
                    else WindowManager.ShowUpdates();
                }));
        }

        stack.Children.Add(Ui.Divider());
        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var settings = Ui.Button(L("menu.settings"), () =>
        {
            Hide();
            WindowManager.ShowSettings();
        }, icon: "gearshape", compact: true, subtle: true);
        footer.Children.Add(settings);
        var more = FlatButton.IconOnly(L("menu.more"), "ellipsis", () => { }, subtle: true);
        more.Click += (_, _) => MoreMenu(more);
        Grid.SetColumn(more, 1);
        footer.Children.Add(more);
        var quit = Ui.Button(L("menu.quit"), WindowManager.Quit, icon: "power", compact: true, subtle: true);
        Grid.SetColumn(quit, 3);
        footer.Children.Add(quit);
        stack.Children.Add(footer);
        return stack;
    }

    private async Task AllowFolders()
    {
        await Services.FolderProtection.AllowAsync();
        Rebuild();
    }

    private void MoreMenu(FrameworkElement anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Top, FontFamily = Theme.TextFont };
        menu.Items.Add(MenuItem(L("menu.guide"), "sparkles", () =>
        {
            Hide();
            WindowManager.ShowOnboarding();
        }));
        var check = MenuItem(L("menu.check_updates"), "arrow.down.circle", () =>
        {
            Hide();
            WindowManager.ShowUpdates(check: true);
        });
        check.IsEnabled = Updater.Shared.IsEnabled;
        menu.Items.Add(check);
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(L("about.github"), "link", () =>
        {
            Hide();
            Platform.Shell.OpenUrl(Updater.Shared.RepositoryPage);
        }));
        menu.IsOpen = true;
    }

    public static MenuItem MenuItem(string title, string? icon, Action click)
    {
        var item = new MenuItem { Header = title };
        if (icon != null) item.Icon = Glyphs.Icon(icon, 14);
        item.Click += (_, _) => click();
        return item;
    }

    private static TextBlock Section(string text) => Ui.Text(text, 12, FontWeights.SemiBold, Theme.TextSecondary);

    private static FrameworkElement Gesture(IReadOnlyList<string> keys, string? prefix, string? suffix, string text)
    {
        SpacedStack Keys()
        {
            var left = Ui.Row(5);
            if (prefix != null) left.Children.Add(Ui.Text(prefix, 12, brush: Theme.TextSecondary));
            foreach (var key in keys) left.Children.Add(KeyCap.Make(key));
            if (suffix != null) left.Children.Add(Ui.Text(suffix, 12, brush: Theme.TextSecondary));
            return left;
        }
        // One line when it fits; otherwise the description moves under the keys (never cut off).
        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.Children.Add(Keys());
        var label = Ui.Text(text, 12.5, alignment: TextAlignment.Right);
        label.Margin = new Thickness(12, 0, 0, 0);
        label.HorizontalAlignment = HorizontalAlignment.Right;
        label.TextTrimming = TextTrimming.None;
        Grid.SetColumn(label, 1);
        line.Children.Add(label);
        var stacked = Ui.Column(3, Keys(), Ui.Text(text, 12.5, brush: Theme.TextSecondary, wrap: true));
        var fits = new ViewThatFits();
        fits.Children.Add(line);
        fits.Children.Add(stacked);
        return fits;
    }

    private FrameworkElement Recent(Job job)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new Grid { Width = 26, Height = 26, Margin = new Thickness(0, 0, 9, 0) };
        icon.Children.Add(new Border { CornerRadius = new CornerRadius(13), Background = Theme.Tile });
        icon.Children.Add(Glyphs.Icon(job.Symbol, 13, Theme.TextSecondary));
        grid.Children.Add(icon);
        var texts = Ui.Column(0, Ui.Text(job.Headline, 12.5), Ui.Text(job.Created.ToString("t", Loc.Culture), 11, brush: Theme.TextSecondary));
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        if (job.HasFiles && !job.InCache)
        {
            var reveal = FlatButton.IconOnly(L("card.reveal"), "folder", () =>
            {
                Hide();
                job.Reveal();
            }, subtle: true);
            Grid.SetColumn(reveal, 2);
            grid.Children.Add(reveal);
        }
        return grid;
    }

    private static Border Banner(string icon, string title, string? detail, string? action, Action? run)
    {
        var content = Ui.Column(8);
        var head = Ui.Row(8, Glyphs.Icon(icon, 15, Theme.AccentText), Ui.Text(title, 12.5, FontWeights.SemiBold));
        content.Children.Add(head);
        if (detail != null) content.Children.Add(Ui.Caption(detail));
        if (action != null && run != null) content.Children.Add(Ui.Button(action, run, compact: true));
        return new Border { Background = Theme.Tile, CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 10, 12, 10), Child = content };
    }
}
