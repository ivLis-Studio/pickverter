namespace Pickverter.Interop;

/// Windows Runtime objects (PDF documents, files, OCR engines…) are released by .NET finalizers, and .NET doesn't run
/// finalizers when the process exits. Some Windows components fail fast when they still own objects as their DLL
/// unloads at exit (the PDF renderer ends the process with STATUS_INVALID_PARAMETER), so whatever is no longer in use
/// is released while the process is still whole: when the app quits and when a command-line run ends.
internal static class NativeObjects
{
    public static void ReleaseUnused(int limitMilliseconds = 3000)
    {
        try
        {
            GC.Collect();
            var finalizers = new Thread(() =>
            {
                GC.WaitForPendingFinalizers();
                GC.Collect();
                GC.WaitForPendingFinalizers();
            })
            {
                IsBackground = true,
                Name = "Pickverter release",
            };
            finalizers.Start();
            // Joining from the UI thread keeps COM calls flowing, so objects that must be released there still can be;
            // the limit keeps a stuck finalizer from holding up quitting.
            finalizers.Join(limitMilliseconds);
        }
        catch (Exception)
        {
        }
    }
}

