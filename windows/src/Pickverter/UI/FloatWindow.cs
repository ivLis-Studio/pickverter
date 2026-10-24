using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Pickverter.Input;
using Pickverter.Interop;
using Pickverter.UI.Editors;

namespace Pickverter.UI;

/// "Float": an always-on-top preview to keep a picture or document in view while you work.
/// Drag to move, scroll to resize, double-click to open, Esc closes.
internal sealed class FloatWindow : Window
{
    private readonly string file;
    private readonly Image image = new() { Stretch = Stretch.Uniform };
    private readonly FlatButton close;
    private double aspect = 4.0 / 3;

    public static void Show(IReadOnlyList<string> files, ScreenPoint anchor)
    {
        var offset = 0;
        foreach (var path in files.Take(6))
        {
            var window = new FloatWindow(path);
            window.Present(anchor, offset);
            offset += 28;
        }
    }

    private FloatWindow(string file)
    {
        this.file = file;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        Topmost = true;
        Title = FileNames.Name(file);
        Background = Theme.Surface;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0, 0, 0, 1),
            UseAeroCaptionButtons = false,
        });
        close = FlatButton.IconOnly(L("card.close"), "xmark", Close, compact: true);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.VerticalAlignment = VerticalAlignment.Top;
        close.Margin = new Thickness(8);
        close.Visibility = Visibility.Hidden;
        var root = new Grid();
        root.Children.Add(image);
        root.Children.Add(close);
        Content = root;
        SourceInitialized += (_, _) =>
        {
            Windowing.MakeOverlay(this, noActivate: false);
            Windowing.Round(this);
        };
        MouseEnter += (_, _) => close.Visibility = Visibility.Visible;
        MouseLeave += (_, _) => close.Visibility = Visibility.Hidden;
        MouseLeftButtonDown += (_, args) =>
        {
            if (args.ClickCount == 2) Platform.Shell.Open(file);
            else if (args.OriginalSource is not FlatButton) DragMove();
        };
        MouseWheel += (_, args) => Zoom(args.Delta > 0 ? 1.1 : 1 / 1.1);
        KeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape) Close();
        };
    }

    private async void Present(ScreenPoint anchor, int offset)
    {
        Native.GetCursorPos(out var cursor);
        var x = anchor.IsZero ? cursor.X : (int)anchor.X;
        var y = anchor.IsZero ? cursor.Y : (int)anchor.Y;
        var (_, work, scale) = Native.MonitorAt(x, y);
        var source = await Load();
        if (source != null)
        {
            image.Source = source;
            aspect = source.PixelWidth / (double)Math.Max(1, source.PixelHeight);
        }
        var maxWidth = work.Width * 0.34;
        var maxHeight = work.Height * 0.42;
        var width = Math.Min(maxWidth, maxHeight * aspect);
        var height = width / aspect;
        var px = (int)Math.Clamp(x - width / 2 + offset * scale, work.Left + 8, work.Right - width - 8);
        var py = (int)Math.Clamp(y - height / 2 + offset * scale, work.Top + 8, work.Bottom - height - 8);
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, px, py, (int)width, (int)height, Native.SWP_NOACTIVATE);
        base.Show();
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, px, py, (int)width, (int)height, Native.SWP_NOACTIVATE);
    }

    private async Task<BitmapSource?> Load()
    {
        switch (FileKinds.Of(file))
        {
            case FileKind.Image:
                return await Previews.Image(file, 2400);
            case FileKind.Pdf:
                var png = Path.Combine(FileSystem.TempRoot, "pickverter-float-" + Guid.NewGuid().ToString("N")[..8] + ".png");
                try
                {
                    await Platform.Pdf.RenderPageAsync(file, 0, 144, png, CancellationToken.None);
                    return Previews.Decode(await File.ReadAllBytesAsync(png));
                }
                catch (Exception)
                {
                    return await Explorer.ThumbnailAsync(file, 768);
                }
                finally
                {
                    FileSystem.TryDelete(png);
                }
            case FileKind.Video:
                return await Previews.Frame(file, 1600, 0.3) ?? await Explorer.ThumbnailAsync(file, 768);
            default:
                return await Explorer.ThumbnailAsync(file, 512);
        }
    }

    private void Zoom(double factor)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.GetWindowRect(hwnd, out var rect);
        var width = Math.Clamp(rect.Width * factor, 120, 4000);
        var height = width / aspect;
        var cx = rect.Left + rect.Width / 2;
        var cy = rect.Top + rect.Height / 2;
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, (int)(cx - width / 2), (int)(cy - height / 2), (int)width, (int)height, Native.SWP_NOACTIVATE);
    }
}
