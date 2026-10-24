using Windows.ApplicationModel;

namespace Pickverter.Services;

/// "Start with Windows" in the Microsoft Store edition: the package's startup task (AppxManifest.xml declares
/// desktop:StartupTask PickverterStartup, off until someone turns it on). The Run key doesn't work for packaged apps.
/// Windows keeps the switch: once the user turns Pickverter off in Task Manager › Startup apps or Settings › Apps ›
/// Startup, RequestEnableAsync can't override that (DisabledByUser), and a policy wins over both.
internal sealed class StoreStartup : IStartup
{
    public const string TaskId = "PickverterStartup";

    private readonly object gate = new();
    private readonly bool real;
    private StartupState state;
    private int generation;
    private int applying;

    public event Action? Changed;

    private StoreStartup(bool real, StartupState initial)
    {
        this.real = real;
        state = initial;
        // Read early, so the switch is right by the time a window shows it.
        if (real) _ = RefreshAsync();
    }

    /// The real startup task with package identity. Without it: a stand-in that remembers the switch in memory when the
    /// Store edition is simulated (PICKVERTER_CHANNEL=store), unavailable for a Store build started by path.
    public static StoreStartup Create() => StoreEdition.HasIdentity
        ? new StoreStartup(true, StartupState.Disabled)
        : new StoreStartup(false, StoreEdition.InsidePackageFolder ? StartupState.Unavailable : StartupState.Disabled);

    public StartupState State
    {
        get
        {
            lock (gate) return state;
        }
    }

    public bool LaunchAtLogin
    {
        get => State.IsOn();
        set
        {
            int request;
            lock (gate)
            {
                if (state.IsLocked() || state.IsOn() == value) return;
                // Shown right away; Windows' answer replaces it (DisabledByUser, a policy).
                state = value ? StartupState.Enabled : StartupState.Disabled;
                request = ++generation;
            }
            if (real) _ = Apply(value, request);
        }
    }

    public async Task RefreshAsync()
    {
        if (!real) return;
        int request;
        lock (gate) request = generation;
        try
        {
            var task = await StartupTask.GetAsync(TaskId);
            // A request still on its way has the newer answer.
            if (Volatile.Read(ref applying) == 0) Publish(Map(task.State), request);
        }
        catch (Exception error)
        {
            Debug.Log("startup task: " + error.Message);
            if (Volatile.Read(ref applying) == 0) Publish(StartupState.Unavailable, request);
        }
    }

    private async Task Apply(bool enable, int request)
    {
        Interlocked.Increment(ref applying);
        try
        {
            var task = await StartupTask.GetAsync(TaskId);
            if (enable)
            {
                Publish(Map(await task.RequestEnableAsync()), request);
            }
            else
            {
                task.Disable();
                Publish(Map(task.State), request);
            }
        }
        catch (Exception error)
        {
            Debug.Log("startup task: " + error.Message);
            Interlocked.Decrement(ref applying);
            await RefreshAsync();
            return;
        }
        Interlocked.Decrement(ref applying);
    }

    /// Keeps Windows' answer unless the switch moved again in the meantime.
    private void Publish(StartupState value, int request)
    {
        lock (gate)
        {
            if (request != generation || state == value) return;
            state = value;
        }
        Platform.Main.Post(() => Changed?.Invoke());
    }

    private static StartupState Map(StartupTaskState value) => value switch
    {
        StartupTaskState.Enabled => StartupState.Enabled,
        StartupTaskState.DisabledByUser => StartupState.DisabledByUser,
        StartupTaskState.DisabledByPolicy => StartupState.DisabledByPolicy,
        StartupTaskState.EnabledByPolicy => StartupState.EnabledByPolicy,
        _ => StartupState.Disabled,
    };

    /// Snapshots: show a state without asking Windows.
    internal void Simulate(StartupState value)
    {
        lock (gate)
        {
            state = value;
            generation++;
        }
    }
}
