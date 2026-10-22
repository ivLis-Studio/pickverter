using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Pickverter.Services;

/// Windows' PDF renderer (the one Microsoft Edge and Photos use).
internal sealed class WinPdf : IPdfRenderer
{
    public bool Available => true;

    public async Task RenderPageAsync(string pdf, int index, double dpi, string pngPath, CancellationToken token)
    {
        PdfDocument document;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(pdf));
            document = await PdfDocument.LoadFromFileAsync(file);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (error.HResult == unchecked((int)0x8007052B)) throw new PickException(L("err.pdf_locked"));
            throw new PickException(L("err.pdf_open"));
        }
        if (index < 0 || index >= document.PageCount) throw new PickException(L("err.render_page"));
        using var page = document.GetPage((uint)index);
        token.ThrowIfCancellationRequested();
        var scale = dpi / 96.0;
        var width = Math.Clamp(page.Size.Width * scale, 1, 12000);
        var height = Math.Clamp(page.Size.Height * scale, 1, 12000);
        var options = new PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Round(width),
            DestinationHeight = (uint)Math.Round(height),
            BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
            BitmapEncoderId = Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId,
        };
        using var stream = new InMemoryRandomAccessStream();
        try
        {
            await page.RenderToStreamAsync(stream, options);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new PickException(L("err.render_page"));
        }
        stream.Seek(0);
        await using var output = File.Create(pngPath);
        await stream.AsStreamForRead().CopyToAsync(output, token);
    }

    public static async Task<int> PageCountAsync(string pdf)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(pdf));
            var document = await PdfDocument.LoadFromFileAsync(file);
            return (int)document.PageCount;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
