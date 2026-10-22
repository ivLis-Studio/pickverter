namespace Pickverter.Interop;

/// How the process ends once the app has shut down in order: settings are saved as they change, logs are written line by
/// line, and quitting already removed the keyboard hooks and the tray icon.
internal static class ProcessEnd
{
    /// Ends the process without unloading its DLLs. Unloading runs Windows components' static destructors after the thread
    /// pool is gone: Windows' PDF renderer keeps a Direct3D device in one, and on the software rasterizer (no GPU: virtual
    /// machines, some remote sessions) destroying that device queues work on the departed thread pool, which ends the
    /// process with STATUS_INVALID_PARAMETER and a crash report. Nothing Pickverter keeps depends on that last step.
    public static void Now(int code)
    {
        try
        {
            Console.Out.Flush();
            Console.Error.Flush();
        }
        catch (Exception)
        {
        }
        Native.TerminateProcess(Native.GetCurrentProcess(), unchecked((uint)code));
    }
}

