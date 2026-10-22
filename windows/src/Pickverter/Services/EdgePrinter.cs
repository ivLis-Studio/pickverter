namespace Pickverter.Services;

/// HTML -> PDF with Microsoft Edge in headless mode (Edge ships with Windows).
internal sealed class EdgePrinter : IHtmlPrinter
{
    public bool Available => Tools.Edge != null;

    public async Task PrintAsync(string htmlPath, string pdfPath, CancellationToken token)
    {
        var edge = Tools.Edge ?? throw new PickException(L("err.edge_missing"));
        var profile = FileSystem.TempFolder("edge");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            var url = new Uri(Path.GetFullPath(htmlPath)).AbsoluteUri;
            await Shell.RunAsync(edge,
            [
                "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check", "--disable-extensions",
                "--disable-sync", "--user-data-dir=" + profile, "--no-pdf-header-footer", "--print-to-pdf-no-header",
                "--print-to-pdf=" + Path.GetFullPath(pdfPath), url,
            ], token: timeout.Token);
            if (!File.Exists(pdfPath) || new FileInfo(pdfPath).Length == 0) throw new PickException(L("err.pdf_create"));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new PickException(L("err.pdf_create"));
        }
        finally
        {
            FileSystem.TryDelete(profile);
        }
    }
}
