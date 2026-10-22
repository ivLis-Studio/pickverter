using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Pickverter.Interop;
using Pickverter.Services;

namespace Pickverter.UI.Wheel;

/// The overlay that shows the wheel: transparent, topmost, never activated, so File Explorer keeps the keyboard focus.
internal sealed class WheelWindow : Window
{
    private readonly WheelModel model;
    private readonly ScaleTransform zoom = new(1, 1);
    private IntPtr hwnd;

    public WheelCanvas Canvas { get; }
    public double Scale { get; private set; } = 1;
    public Native.POINT CenterPixels { get; private set; }

    public event Action<IReadOnlyList<string>, Point, bool>? FilesDropped;
    public event Action<IReadOnlyList<string>>? FilesArrived;
    public event Action<Point>? DraggedOver;

    public WheelWindow(WheelModel model)
    {
        this.model = model;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Title = "Pickverter";
        Width = model.Geometry.BoxSide;
        Height = model.Geometry.BoxSide;
        AllowDrop = true;
        Canvas = new WheelCanvas(model)
        {
            AllowDrop = true,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = zoom,
            Effect = new DropShadowEffect { BlurRadius = 26, ShadowDepth = 6, Direction = 270, Opacity = Theme.ShadowOpacity, Color = Theme.ShadowColor },
        };
        Content = Canvas;
        Announcer.Register(Canvas);
        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            Windowing.MakeOverlay(this);
        };
        DragEnter += OnDrag;
        DragOver += OnDrag;
        Drop += OnDrop;
        Theme.Changed += () =>
        {
            if (Canvas.Effect is DropShadowEffect shadow) shadow.Opacity = Theme.ShadowOpacity;
            Canvas.InvalidateVisual();
        };
    }

    /// Centres the wheel on a screen point (physical pixels). Near an edge the whole wheel moves inward, so every slot,
    /// the outer ring and the tabs stay inside the monitor's work area, clear of the taskbar. The invisible drop catcher
    /// used while dragging (underPointer) stays exactly under the pointer instead.
    public Native.POINT Place(Native.POINT point, bool underPointer = false)
    {
        var (_, work, scale) = Native.MonitorAt(point.X, point.Y);
        Scale = scale;
        var side = (int)Math.Round(model.Geometry.BoxSide * scale);
        var (cx, cy) = underPointer
            ? (point.X, point.Y)
            : model.Geometry.CenterIn(point.X, point.Y, work.Left, work.Top, work.Right, work.Bottom, scale);
        var x = (int)Math.Round(cx) - side / 2;
        var y = (int)Math.Round(cy) - side / 2;
        if (hwnd == IntPtr.Zero) hwnd = new WindowInteropHelper(this).EnsureHandle();
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, side, side, Native.SWP_NOACTIVATE);
        CenterPixels = new Native.POINT(x + side / 2, y + side / 2);
        return CenterPixels;
    }

    public void Appear()
    {
        if (!IsVisible)
        {
            Show();
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
        Canvas.BeginAnimation(OpacityProperty, null);
        var reduced = !SystemParameters.ClientAreaAnimation;
        if (reduced)
        {
            Canvas.Opacity = 1;
            zoom.ScaleX = zoom.ScaleY = 1;
            return;
        }
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Canvas.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(110)));
        var grow = new DoubleAnimation(0.92, 1, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease };
        zoom.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        zoom.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    public void Disappear()
    {
        if (!IsVisible) return;
        var fade = new DoubleAnimation(Canvas.Opacity, 0, TimeSpan.FromMilliseconds(90));
        fade.Completed += (_, _) =>
        {
            if (!model.IsVisible && !Canvas.Probing) Hide();
        };
        Canvas.BeginAnimation(OpacityProperty, fade);
    }

    /// Lets the window take the keyboard for a moment (the right-click menu needs it).
    public void AllowActivation(bool allow)
    {
        if (hwnd == IntPtr.Zero) return;
        if (allow) Native.AddExStyle(hwnd, 0, Native.WS_EX_NOACTIVATE);
        else Native.AddExStyle(hwnd, Native.WS_EX_NOACTIVATE);
    }

    // Drag and drop

    public static IReadOnlyList<string> Files(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop)) return [];
        return data.GetData(DataFormats.FileDrop) is string[] paths ? paths.Where(path => File.Exists(path) || Directory.Exists(path)).ToList() : [];
    }

    private void OnDrag(object sender, DragEventArgs args)
    {
        args.Handled = true;
        var files = Files(args.Data);
        if (Canvas.Probing)
        {
            args.Effects = DragDropEffects.None;
            if (files.Count > 0) FilesArrived?.Invoke(files);
            return;
        }
        var position = args.GetPosition(Canvas);
        DraggedOver?.Invoke(position);
        args.Effects = model.CanDropHere ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDrop(object sender, DragEventArgs args)
    {
        args.Handled = true;
        var files = Files(args.Data);
        if (files.Count == 0) return;
        var flip = (args.KeyStates & DragDropKeyStates.AltKey) != 0;
        args.Effects = DragDropEffects.Copy;
        FilesDropped?.Invoke(files, args.GetPosition(Canvas), flip);
    }
}
