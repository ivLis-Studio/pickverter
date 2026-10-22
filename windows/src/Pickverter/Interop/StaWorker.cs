using System.Collections.Concurrent;

namespace Pickverter.Interop;

/// A single-threaded apartment thread for shell COM calls. File Explorer can stall; a stuck call never blocks
/// the UI thread, and the next caller gets a fresh worker.
internal sealed class StaWorker
{
    private readonly BlockingCollection<Action> queue = new();

    private StaWorker(string name)
    {
        var thread = new Thread(() =>
        {
            foreach (var work in queue.GetConsumingEnumerable())
            {
                try
                {
                    work();
                }
                catch (Exception error)
                {
                    Pickverter.Debug.Log(name + ": " + error.Message);
                }
            }
        })
        {
            IsBackground = true,
            Name = name,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, StaWorker> Workers = new();

    public static StaWorker For(string name)
    {
        lock (Gate)
        {
            if (!Workers.TryGetValue(name, out var worker))
            {
                worker = new StaWorker("Pickverter " + name);
                Workers[name] = worker;
            }
            return worker;
        }
    }

    private static void Abandon(string name, StaWorker worker)
    {
        lock (Gate)
        {
            if (Workers.TryGetValue(name, out var current) && current == worker) Workers.Remove(name);
        }
        worker.queue.CompleteAdding();
    }

    public Task<T> Run<T>(Func<T> func)
    {
        var source = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Add(() =>
        {
            try
            {
                source.TrySetResult(func());
            }
            catch (Exception error)
            {
                source.TrySetException(error);
            }
        });
        return source.Task;
    }

    /// Runs on the named worker; gives up (returning fallback) after the timeout and retires the stuck thread.
    public static async Task<T> Run<T>(string name, Func<T> func, TimeSpan timeout, T fallback)
    {
        var worker = For(name);
        Task<T> task;
        try
        {
            task = worker.Run(func);
        }
        catch (InvalidOperationException)
        {
            Abandon(name, worker);
            worker = For(name);
            task = worker.Run(func);
        }
        try
        {
            return await task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            Pickverter.Debug.Log(name + ": timed out");
            Abandon(name, worker);
            return fallback;
        }
        catch (Exception error)
        {
            Pickverter.Debug.Log(name + ": " + error.Message);
            return fallback;
        }
    }
}
