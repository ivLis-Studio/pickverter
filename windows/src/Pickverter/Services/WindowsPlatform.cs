using System.Windows.Threading;

namespace Pickverter.Services;

/// Plugs the Windows implementations into the core.
internal static class WindowsPlatform
{
    public static void Install(Dispatcher dispatcher)
    {
        Platform.Main = new DispatcherMain(dispatcher);
        Platform.Shell = new WinShell();
        Platform.Clipboard = new WinClipboard();
        Platform.Vision = new WinVision();
        Platform.Pdf = new WinPdf();
        Platform.Printer = new EdgePrinter();
        Platform.Office = new WinOffice();
        Platform.Media = new WinMedia();
        Platform.Codecs = new WinCodecs();
        Platform.Feedback = new WinFeedback();
        Platform.Startup = new WinStartup();
        Platform.Actions = new WinActions();
        Platform.Editors = new UI.Editors.EditorHost();
        Updater.Shared.Installer = new UpdateInstaller();
    }

    /// Slow probes (HEIC encoder, OCR languages, Office) run once in the background so the wheel never waits.
    public static void WarmUp() => _ = Task.Run(() =>
    {
        try
        {
            _ = Platform.Codecs.CanEncodeHeic;
            _ = Platform.Vision.OcrAvailable;
            _ = Platform.Vision.FacesAvailable;
            _ = Platform.Office.Has(OfficeApp.Word);
            _ = Platform.Media.Available;
            _ = Tools.FFmpeg;
            _ = Tools.Edge;
            _ = Terminals.Installed;
        }
        catch (Exception error)
        {
            Debug.Log("warm-up: " + error.Message);
        }
    });
}

internal sealed class DispatcherMain(Dispatcher dispatcher) : IMainThread
{
    private sealed class Pending(Action cancel) : IDisposable
    {
        public void Dispose() => cancel();
    }

    public bool IsMainThread => dispatcher.CheckAccess();

    public void Post(Action action) => dispatcher.BeginInvoke(action);

    public Task RunAsync(Action action)
    {
        if (dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        return dispatcher.InvokeAsync(action).Task;
    }

    public Task<T> RunAsync<T>(Func<T> func) => dispatcher.CheckAccess() ? Task.FromResult(func()) : dispatcher.InvokeAsync(func).Task;

    public IDisposable After(TimeSpan delay, Action action)
    {
        var cancelled = 0;
        Timer? timer = null;
        timer = new Timer(_ =>
        {
            timer?.Dispose();
            if (Volatile.Read(ref cancelled) == 0) dispatcher.BeginInvoke(() =>
            {
                if (Volatile.Read(ref cancelled) == 0) action();
            });
        }, null, delay, Timeout.InfiniteTimeSpan);
        return new Pending(() =>
        {
            Interlocked.Exchange(ref cancelled, 1);
            timer.Dispose();
        });
    }
}
