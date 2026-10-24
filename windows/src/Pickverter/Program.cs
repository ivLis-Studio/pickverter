using Pickverter.Interop;
using Pickverter.Services;

namespace Pickverter;

public static class Program
{
    private const string InstanceName = @"Local\ivLisStudio.Pickverter";

    [STAThread]
    public static int Main(string[] args)
    {
        // GitHub or Microsoft Store edition (and, for the Store, its folders) before anything reads a path or a setting.
        StoreEdition.Detect();
        if (args.Length > 0 && args[0] == "cli")
        {
            var status = Cli.CommandLine.Run(args[1..]);
            ProcessEnd.Now(status);
            return status;
        }
        // The updater's hand-over and the installer's request to quit belong to the GitHub edition (Inno Setup, zip).
        if (args.Length > 0 && args[0] == "--apply-update") return Distribution.IsStore ? 64 : UpdateApplier.Apply(args[1..]);
        if (args.Length > 0 && args[0] == "--quit") return Distribution.IsStore ? 64 : QuitRunning();
        // Signing in: the Run key passes --background; the Store edition's startup task says so in its activation.
        var background = args.Contains("--background") || StoreEdition.StartedAtSignIn();
        using var mutex = new Mutex(true, InstanceName, out var first);
        if (!first)
        {
            // Already running: ask that copy to show itself (opening Pickverter from Start does that). The Store
            // edition started at sign-in or brought back after an update just leaves the running copy alone.
            if (Distribution.IsStore && background) return 0;
            try
            {
                using var signal = EventWaitHandle.OpenExisting(InstanceName + ".Show");
                signal.Set();
            }
            catch (Exception)
            {
            }
            return 0;
        }
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Show");
        using var quit = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Quit");
        var app = new PickverterApp(args, background, show, quit);
        var code = app.Run();
        ProcessEnd.Now(code);
        return code;
    }

    /// The installer and uninstaller ask a running copy to quit before they touch its files.
    private static int QuitRunning()
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting(InstanceName + ".Quit");
            signal.Set();
        }
        catch (Exception)
        {
            return 0;
        }
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                using var probe = Mutex.OpenExisting(InstanceName);
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return 0;
            }
            Thread.Sleep(100);
        }
        return 1;
    }
}
