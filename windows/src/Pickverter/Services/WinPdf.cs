using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace Pickverter.Services;

/// Windows' PDF renderer (the one Microsoft Edge and Photos use).
internal sealed class WinPdf : IPdfRenderer
{
    public bool Available => true;

    public async Task RenderPageAsync(string pdf, int index, double dpi, string pngPath, CancellationToken token)
    {
        await using var session = await PdfSession.OpenAsync(pdf);
        await session.RenderAsync(index, dpi, pngPath, token);
    }
}

/// One open PDF, for rendering several pages without reading the file again. The file is read through a handle that
/// never stops other apps from saving, renaming or deleting it, and that handle closes as soon as the session ends.
internal sealed class PdfSession : IAsyncDisposable
{
    private readonly FileStream file;
    private readonly IRandomAccessStream stream;
    private readonly PdfDocument document;

    private PdfSession(FileStream file, IRandomAccessStream stream, PdfDocument document)
    {
        this.file = file;
        this.stream = stream;
        this.document = document;
    }

    public int PageCount => (int)document.PageCount;

    public static async Task<PdfSession> OpenAsync(string path)
    {
        FileStream? file = null;
        try
        {
            file = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
            var stream = file.AsRandomAccessStream();
            var document = await PdfDocument.LoadFromStreamAsync(stream);
            return new PdfSession(file, stream, document);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (file != null) await file.DisposeAsync();
            if (error.HResult == unchecked((int)0x8007052B)) throw new PickException(L("err.pdf_locked"));
            throw new PickException(L("err.pdf_open"));
        }
    }

    public async Task RenderAsync(int index, double dpi, string pngPath, CancellationToken token)
    {
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
        using var rendered = new InMemoryRandomAccessStream();
        try
        {
            await page.RenderToStreamAsync(rendered, options);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new PickException(L("err.render_page"));
        }
        rendered.Seek(0);
        await using var output = File.Create(pngPath);
        await using var input = rendered.AsStreamForRead();
        await input.CopyToAsync(output, token);
    }

    public async ValueTask DisposeAsync()
    {
        // The document itself goes with the next garbage collection (Windows has no way to close it).
        stream.Dispose();
        await file.DisposeAsync();
    }
}

