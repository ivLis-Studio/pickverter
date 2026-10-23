using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Pickverter.Services;

/// The Windows clipboard (always touched on the UI thread, retried while another app holds it open).
internal sealed class WinClipboard : IClipboardService
{
    private static readonly HashSet<string> TextTypes = new(StringComparer.OrdinalIgnoreCase)
        { "txt", "md", "csv", "tsv", "json", "srt", "vtt", "html", "rtf", "xml" };
    private static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
        { "png", "jpg", "jpeg", "bmp", "gif", "tif", "tiff", "ico", "webp", "heic", "avif" };

    public Task SetTextAsync(string text) => Put(() => new DataObject(DataFormats.UnicodeText, text));

    public Task SetFilesAsync(IReadOnlyList<string> paths) => Put(() => FileData(paths));

    /// Images go up as pixels plus the file, text as text plus the file, everything else as files.
    public async Task WriteResultsAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count != 1 || Directory.Exists(paths[0]))
        {
            await SetFilesAsync(paths);
            return;
        }
        var path = paths[0];
        var ext = FileNames.NormalizedExt(path);
        if (TextTypes.Contains(ext))
        {
            var text = await Task.Run(() => FileSystem.ReadText(path));
            await Put(() =>
            {
                var data = FileData(paths);
                data.SetData(DataFormats.UnicodeText, text);
                if (ext == "html") data.SetData(DataFormats.Html, HtmlClipboard(text));
                if (ext == "rtf") data.SetData(DataFormats.Rtf, text);
                return data;
            });
            return;
        }
        if (ImageTypes.Contains(ext))
        {
            var png = await Task.Run(() => PngBytes(path));
            await Put(() =>
            {
                var data = FileData(paths);
                if (png != null)
                {
                    data.SetData("PNG", new MemoryStream(png), false);
                    using var stream = new MemoryStream(png);
                    var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    data.SetImage(frame);
                }
                return data;
            });
            return;
        }
        await SetFilesAsync(paths);
    }

    public Task<string> DescribeAsync() => Platform.Main.RunAsync(() =>
    {
        try
        {
            var data = Clipboard.GetDataObject();
            return data == null ? "" : string.Join(", ", data.GetFormats(false));
        }
        catch (COMException)
        {
            return "";
        }
    });

    private static DataObject FileData(IReadOnlyList<string> paths)
    {
        var data = new DataObject();
        var list = new StringCollection();
        foreach (var path in paths) list.Add(Path.GetFullPath(path));
        data.SetFileDropList(list);
        // Paste in File Explorer copies (never moves) the file.
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1)));
        return data;
    }

    private static byte[]? PngBytes(string path)
    {
        try
        {
            if (FileNames.NormalizedExt(path) == "png") return File.ReadAllBytes(path);
            using var image = new ImageMagick.MagickImage(path);
            image.AutoOrient();
            image.Format = ImageMagick.MagickFormat.Png;
            return image.ToByteArray();
        }
        catch (Exception error)
        {
            Debug.Log("clipboard image: " + error.Message);
            return null;
        }
    }

    /// CF_HTML needs a small header with byte offsets.
    private static string HtmlClipboard(string html)
    {
        const string header = "Version:0.9\r\nStartHTML:{0:0000000000}\r\nEndHTML:{1:0000000000}\r\nStartFragment:{2:0000000000}\r\nEndFragment:{3:0000000000}\r\n";
        var prefix = "<html><body><!--StartFragment-->";
        var suffix = "<!--EndFragment--></body></html>";
        var sample = string.Format(CultureInfo.InvariantCulture, header, 0, 0, 0, 0);
        var start = Encoding.UTF8.GetByteCount(sample);
        var fragmentStart = start + Encoding.UTF8.GetByteCount(prefix);
        var fragmentEnd = fragmentStart + Encoding.UTF8.GetByteCount(html);
        var end = fragmentEnd + Encoding.UTF8.GetByteCount(suffix);
        return string.Format(CultureInfo.InvariantCulture, header, start, end, fragmentStart, fragmentEnd) + prefix + html + suffix;
    }

    private static async Task Put(Func<DataObject> build)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await OnSta(() => Clipboard.SetDataObject(build(), true));
                return;
            }
            catch (COMException) when (attempt < 8)
            {
                await Task.Delay(60);
            }
            catch (ExternalException) when (attempt < 8)
            {
                await Task.Delay(60);
            }
        }
    }

    /// The clipboard is OLE and wants an STA thread. The app hands the call to its UI thread; the command line has no
    /// UI thread, and after an await it may be on the thread pool (MTA), so it borrows a short-lived STA thread.
    private static Task OnSta(Action action)
    {
        if (Application.Current != null || Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) return Platform.Main.RunAsync(action);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action();
                done.SetResult();
            }
            catch (Exception error)
            {
                done.SetException(error);
            }
        })
        {
            IsBackground = true,
            Name = "Pickverter clipboard",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }
}
