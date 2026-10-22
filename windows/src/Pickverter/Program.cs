using Pickverter.Services;

namespace Pickverter;

public static class Program
{
    private const string InstanceName = @"Local\ivLisStudio.Pickverter";

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "cli") return Cli.CommandLine.Run(args[1..]);
        if (args.Length > 0 && args[0] == "--apply-update") return UpdateApplier.Apply(args[1..]);
        if (args.Length > 0 && args[0] == "--quit") return QuitRunning();
        using var mutex = new Mutex(true, InstanceName, out var first);
        if (!first)
        {
            // Already running: ask that copy to show itself (opening Pickverter from Start does that).
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
        var app = new PickverterApp(args, show, quit);
        return app.Run();
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
