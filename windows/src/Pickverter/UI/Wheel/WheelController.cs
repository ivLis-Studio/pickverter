using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Pickverter.Input;
using Pickverter.Interop;

namespace Pickverter.UI.Wheel;

/// Connects input, the model and the overlay window. Everything here runs on the UI thread.
internal sealed class WheelController
{
    public static WheelController Shared { get; } = new();

    public WheelModel Model { get; } = new();
    private WheelWindow? window;
    private IntPtr explorer;
    private bool menuOpen;
    private int presentation;
    private IDisposable? probeTimeout;
    private WheelMode? probeMode;

    private WheelController()
    {
        Model.Changed += OnChanged;
        Model.Launched += (option, mode, files, destination, anchor) =>
        {
            Debug.Log($"run {option.Id} · {files.Count} file(s) · {destination}");
            JobCenter.Shared.Run(option, mode, files, anchor, destination);
        };
    }

    public bool IsVisible => Model.IsVisible;

    private WheelWindow Window
    {
        get
        {
            if (window != null) return window;
            window = new WheelWindow(Model);
            window.FilesArrived += OnProbeFiles;
            window.DraggedOver += position => Model.PointerMoved(position.X - Model.Geometry.Center, position.Y - Model.Geometry.Center);
            window.FilesDropped += (files, position, flip) =>
            {
                if (!Model.PerformDrop(position.X - Model.Geometry.Center, position.Y - Model.Geometry.Center, files, flip)) Platform.Feedback.Beep();
            };
            return window;
        }
    }

    // Opening

    public async void OpenFromSelection(IntPtr root, Native.POINT point, int replayAlt)
    {
        if (Model.IsVisible) return;
        var ticket = ++presentation;
        var files = await Explorer.SelectionAsync(root);
        if (ticket != presentation || Model.IsVisible) return;
        if (files.Count == 0)
        {
            Debug.Log("trigger: nothing selected");
            if (replayAlt >= 0 && Native.GetForegroundWindow() == root) InputMonitor.Shared.ReplayAltTap(replayAlt);
            return;
        }
        explorer = root;
        Present(files, WheelModel.WheelOrigin.Selection, null, point);
    }

    public async void OpenFromClick(Native.POINT point)
    {
        if (Model.IsVisible) return;
        var ticket = ++presentation;
        await Task.Delay(140);
        if (ticket != presentation || Model.IsVisible) return;
        var root = Explorer.FileViewAt(point.X, point.Y);
        if (root == IntPtr.Zero || Explorer.IsEditingText(root)) return;
        var files = await Explorer.SelectionAsync(root);
        if (ticket != presentation || Model.IsVisible || files.Count == 0) return;
        explorer = root;
        Present(files, WheelModel.WheelOrigin.Selection, null, point);
    }

    public void Present(IReadOnlyList<string> files, WheelModel.WheelOrigin origin, WheelMode? mode, Native.POINT point)
    {
        var overlay = Window;
        overlay.Canvas.Probing = false;
        Model.Geometry.TabWidth = WheelCanvas.TabWidthFor(1);
        var center = overlay.Place(point);
        overlay.Canvas.Thumbnail = null;
        Model.Present(files, origin, mode, new ScreenPoint(center.X, center.Y));
        if (files.Count > 0)
        {
            var first = files[0];
            var ticket = presentation;
            _ = LoadThumbnail(first, ticket);
        }
    }

    private async Task LoadThumbnail(string file, int ticket)
    {
        var image = await Explorer.ThumbnailAsync(file, 96);
        if (ticket == presentation && Model.IsVisible && Model.Files.Count > 0 && Model.Files[0] == file && window != null) window.Canvas.Thumbnail = image;
    }

    /// Shift, Ctrl or Alt tapped while dragging: an invisible drop catcher appears under the pointer and the
    /// wheel opens as soon as the drag reports its files.
    public void DragTap(WheelMode? mode, Native.POINT point)
    {
        if (Model.IsVisible)
        {
            if (mode is { } ring) Model.SetMode(ring);
            else Model.CycleMode();
            return;
        }
        presentation++;
        probeMode = mode;
        var overlay = Window;
        overlay.Canvas.Probing = true;
        overlay.Canvas.Opacity = 1;
        overlay.Place(point, underPointer: true);
        overlay.Canvas.InvalidateVisual();
        overlay.Appear();
        InputMonitor.Shared.Nudge();
        probeTimeout?.Dispose();
        probeTimeout = Platform.Main.After(TimeSpan.FromMilliseconds(220), () =>
        {
            if (window?.Canvas.Probing == true) InputMonitor.Shared.Nudge();
        });
        Platform.Main.After(TimeSpan.FromMilliseconds(900), () =>
        {
            if (window?.Canvas.Probing != true) return;
            Debug.Log("drag: no files arrived");
            EndProbe();
        });
    }

    private void OnProbeFiles(IReadOnlyList<string> files)
    {
        if (window is not { } overlay || !overlay.Canvas.Probing) return;
        overlay.Canvas.Probing = false;
        Native.GetCursorPos(out var point);
        Present(files, WheelModel.WheelOrigin.Drag, probeMode, point);
    }

    private void EndProbe()
    {
        if (window is not { } overlay || !overlay.Canvas.Probing) return;
        overlay.Canvas.Probing = false;
        overlay.Canvas.InvalidateVisual();
        if (!Model.IsVisible) overlay.Hide();
    }

    // Input while open

    public void ModifierTap(WheelMode? mode, bool trigger)
    {
        if (!Model.IsVisible) return;
        if (mode is { } ring)
        {
            if (ring == Model.Mode && trigger) Model.Dismiss();
            else Model.SetMode(ring);
        }
        else if (trigger)
        {
            Model.Dismiss();
        }
    }

    public void HandleKey(KeyInput input)
    {
        if (Model.IsVisible && !menuOpen) Model.HandleKey(input);
    }

    private Point Offset(Native.POINT point)
    {
        var scale = window?.Scale ?? 1;
        var center = window?.CenterPixels ?? point;
        return new Point((point.X - center.X) / scale, (point.Y - center.Y) / scale);
    }

    public void PointerMoved(Native.POINT point)
    {
        if (!Model.IsVisible || menuOpen) return;
        var offset = Offset(point);
        Model.PointerMoved(offset.X, offset.Y);
    }

    public void Click(Native.POINT point, bool flip)
    {
        if (!Model.IsVisible || menuOpen) return;
        var offset = Offset(point);
        Model.HandleClick(offset.X, offset.Y, flip);
    }

    public void Scroll(double delta)
    {
        if (Model.IsVisible && !menuOpen) Model.Scroll(delta);
    }

    public void Dismiss()
    {
        if (!menuOpen) Model.Dismiss();
        EndProbe();
    }

    public void DragSessionEnded()
    {
        EndProbe();
        Model.DragSessionEnded();
    }

    // Right-click menu: remove, move, pin and pick defaults right where the slot is

    public void ShowContextMenu(Native.POINT point)
    {
        if (!Model.IsVisible || window == null) return;
        var offset = Offset(point);
        var result = Model.ContextMenu(offset.X, offset.Y, WindowManager.ShowWheelLayout);
        if (result is not { } menu)
        {
            Model.Dismiss();
            return;
        }
        var context = new ContextMenu { Placement = PlacementMode.MousePoint, PlacementTarget = window.Canvas, FontFamily = Theme.TextFont };
        foreach (var entry in menu.Entries) context.Items.Add(Item(entry));
        menuOpen = true;
        Publish();
        window.AllowActivation(true);
        Windowing.Activate(window);
        context.Closed += (_, _) =>
        {
            menuOpen = false;
            window.AllowActivation(false);
            if (explorer != IntPtr.Zero && Native.IsWindow(explorer)) Windowing.Bring(explorer);
            Model.AfterContextMenu(menu.Keep);
            Publish();
        };
        context.IsOpen = true;
    }

    private static object Item(MenuEntry entry)
    {
        if (entry.IsSeparator) return new Separator();
        var item = new MenuItem
        {
            Header = entry.Title,
            IsEnabled = entry.Enabled && !entry.IsHeader,
            IsCheckable = false,
            IsChecked = entry.Checked,
            FontWeight = entry.IsHeader ? FontWeights.SemiBold : FontWeights.Normal,
        };
        if (entry.Symbol != null && !entry.Checked) item.Icon = Glyphs.Icon(entry.Symbol, 14);
        if (entry.Submenu != null)
        {
            foreach (var child in entry.Submenu) item.Items.Add(Item(child));
        }
        else if (entry.Run is { } run)
        {
            item.Click += (_, _) => run();
        }
        return item;
    }

    // Model -> window

    private void OnChanged(WheelChange change)
    {
        if (window is { } overlay)
        {
            if (change.HasFlag(WheelChange.Visibility))
            {
                if (Model.IsVisible) overlay.Appear();
                else
                {
                    overlay.Disappear();
                    presentation++;
                }
            }
            overlay.Canvas.InvalidateVisual();
        }
        Publish();
    }

    private void Publish()
    {
        var overlay = window;
        if (!Model.IsVisible || overlay == null)
        {
            InputMonitor.Shared.Wheel = WheelSnapshot.Hidden;
            return;
        }
        var center = overlay.CenterPixels;
        InputMonitor.Shared.Wheel = new WheelSnapshot(true, Model.Origin, menuOpen, center.X, center.Y, overlay.Scale, Model.Reach,
            Model.Choice == null && !Model.TabsHidden, Model.Geometry.TabBarWidth);
    }
}
