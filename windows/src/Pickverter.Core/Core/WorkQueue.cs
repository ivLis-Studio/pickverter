namespace Pickverter;

/// Limits how many heavy jobs (encodes, renders) run at once. Quick actions never wait. Slots are handed over
/// in FIFO order; a cancelled waiter simply leaves the line.
public sealed class WorkQueue
{
    public static WorkQueue Shared { get; } = new();

    private static readonly AsyncLocal<bool> Holding = new();

    /// True inside work that already owns a slot, so nested Produce calls never deadlock.
    public static bool IsHoldingSlot
    {
        get => Holding.Value;
        set => Holding.Value = value;
    }

    private readonly object gate = new();
    private readonly LinkedList<TaskCompletionSource> waiters = new();
    private int running;

    public int Limit => Preferences.Shared.MaxJobs > 0 ? Preferences.Shared.MaxJobs : LimitForAuto;

    /// A quarter of the cores, between 1 and 3.
    public int LimitForAuto => Math.Max(1, Math.Min(3, Environment.ProcessorCount / 4));

    public bool IsFull
    {
        get { lock (gate) return running >= Limit; }
    }

    public int ActiveCount
    {
        get { lock (gate) return running; }
    }

    public int WaitingCount
    {
        get { lock (gate) return waiters.Count; }
    }

    public async Task AcquireAsync(CancellationToken token)
    {
        TaskCompletionSource waiter;
        LinkedListNode<TaskCompletionSource> node;
        lock (gate)
        {
            if (running < Limit)
            {
                running++;
                return;
            }
            waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            node = waiters.AddLast(waiter);
        }
        await using (token.Register(() =>
        {
            bool removed;
            lock (gate)
            {
                removed = node.List != null;
                if (removed) waiters.Remove(node);
            }
            if (removed) waiter.TrySetCanceled(token);
        }))
        {
            await waiter.Task;
        }
    }

    public void Release()
    {
        TaskCompletionSource? next = null;
        lock (gate)
        {
            if (waiters.Count > 0 && running <= Limit)
            {
                next = waiters.First!.Value;
                waiters.RemoveFirst();
            }
            else
            {
                running = Math.Max(0, running - 1);
            }
        }
        // The slot moves straight to the next waiter (running stays the same).
        next?.TrySetResult();
    }
}

/// Private cache for results that only live on the clipboard.
public static class ClipboardStore
{
    public static string Root => Path.Combine(Platform.Paths.Cache, "Clipboard");

    public static string Folder(Guid job)
    {
        var folder = Path.Combine(Root, job.ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static bool Contains(string path) =>
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase);

    /// Drops cached results older than a day (the clipboard has long moved on by then).
    public static void Prune()
    {
        try
        {
            if (!Directory.Exists(Root)) return;
            var cutoff = DateTime.UtcNow.AddDays(-1);
            foreach (var entry in Directory.EnumerateFileSystemEntries(Root))
            {
                if (Directory.GetLastWriteTimeUtc(entry) < cutoff) FileSystem.TryDelete(entry);
            }
        }
        catch (Exception)
        {
        }
    }
}
