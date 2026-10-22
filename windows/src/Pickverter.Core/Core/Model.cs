using System.Text.Json.Serialization;

namespace Pickverter;

/// The three rings of the wheel. While dragging, Shift opens Convert, Ctrl opens Tools and Alt opens Actions.
public enum WheelMode { Convert, Tools, Actions }

public static class WheelModes
{
    public static readonly WheelMode[] All = [WheelMode.Convert, WheelMode.Tools, WheelMode.Actions];

    public static string Title(this WheelMode mode) => mode switch
    {
        WheelMode.Convert => L("mode.convert"),
        WheelMode.Tools => L("mode.tools"),
        _ => L("mode.actions"),
    };

    /// The Windows key that picks this ring (localized keycap label).
    public static string KeyName(this WheelMode mode) => mode switch
    {
        WheelMode.Convert => KeyNames.Shift,
        WheelMode.Tools => KeyNames.Ctrl,
        _ => KeyNames.Alt,
    };

    /// Compact keycap for the wheel's tab bar ("⇧" where "Shift" is a long word).
    public static string TabKey(this WheelMode mode) => mode == WheelMode.Convert ? KeyNames.ShiftShort : mode.KeyName();

    public static string Raw(this WheelMode mode) => mode switch
    {
        WheelMode.Convert => "convert",
        WheelMode.Tools => "tools",
        _ => "actions",
    };

    public static WheelMode? Parse(string? raw) => raw switch
    {
        "convert" => WheelMode.Convert,
        "tools" => WheelMode.Tools,
        "actions" => WheelMode.Actions,
        _ => null,
    };
}

/// Localized names of the keys printed on a Windows keyboard (Strg on German keyboards, Maj on French ones…).
public static class KeyNames
{
    public static string Shift => L("key.shift");
    public static string ShiftShort => L("key.shift.short");
    public static string Ctrl => L("key.ctrl");
    public static string Alt => L("key.alt");
    public static string Enter => L("key.enter");
    public static string Esc => L("key.esc");
    public static string Tab => L("key.tab");
    public static string Del => L("key.del");
    public static string Combo(string modifier, string key) => modifier + "+" + key;
}

public enum OutputLocation { SameFolder, Downloads, Desktop }

public static class OutputLocations
{
    public static readonly OutputLocation[] All = [OutputLocation.SameFolder, OutputLocation.Downloads, OutputLocation.Desktop];

    public static string Raw(this OutputLocation location) => location switch
    {
        OutputLocation.SameFolder => "sameFolder",
        OutputLocation.Downloads => "downloads",
        _ => "desktop",
    };

    public static OutputLocation? Parse(string? raw) => raw switch
    {
        "sameFolder" => OutputLocation.SameFolder,
        "downloads" => OutputLocation.Downloads,
        "desktop" => OutputLocation.Desktop,
        _ => null,
    };

    public static string Label(this OutputLocation location) => location switch
    {
        OutputLocation.SameFolder => L("set.location.same"),
        OutputLocation.Downloads => L("set.location.downloads"),
        _ => L("set.location.desktop"),
    };

    public static string DirectoryFor(this OutputLocation location, string source)
    {
        switch (location)
        {
            case OutputLocation.SameFolder:
                var dir = Path.GetDirectoryName(Path.GetFullPath(source));
                return dir != null && FileSystem.CanWrite(dir) ? dir : Platform.Shell.DownloadsFolder;
            case OutputLocation.Downloads:
                return Platform.Shell.DownloadsFolder;
            default:
                return Platform.Shell.DesktopFolder;
        }
    }
}

/// Where a finished result goes. Ask is resolved by the wheel before a job runs.
public enum ResultDestination { File, Clipboard, Both, Ask }

public static class ResultDestinations
{
    public static readonly ResultDestination[] All =
        [ResultDestination.File, ResultDestination.Clipboard, ResultDestination.Both, ResultDestination.Ask];

    public static string Raw(this ResultDestination destination) => destination switch
    {
        ResultDestination.File => "file",
        ResultDestination.Clipboard => "clipboard",
        ResultDestination.Both => "both",
        _ => "ask",
    };

    public static ResultDestination? Parse(string? raw) => raw switch
    {
        "file" => ResultDestination.File,
        "clipboard" => ResultDestination.Clipboard,
        "both" => ResultDestination.Both,
        "ask" => ResultDestination.Ask,
        _ => null,
    };

    public static string Label(this ResultDestination destination) => L("dest." + destination.Raw());

    public static string Symbol(this ResultDestination destination) => destination switch
    {
        ResultDestination.File => "doc",
        ResultDestination.Clipboard => "doc.on.clipboard",
        ResultDestination.Both => "doc.on.doc",
        _ => "questionmark.circle",
    };

    public static bool Copies(this ResultDestination destination) =>
        destination is ResultDestination.Clipboard or ResultDestination.Both;
}

public sealed class PickResult
{
    public List<string> Outputs { get; init; } = [];
    public string? Message { get; init; }
    /// New files that can go to the Recycle Bin from the result card.
    public bool Undoable { get; init; } = true;
    /// No result card at all (Share, Terminal, ...).
    public bool Silent { get; init; }

    public static PickResult Quiet => new() { Undoable = false, Silent = true };
    public static PickResult Note(string message) => new() { Message = message, Undoable = false };
    public static PickResult Of(params string[] outputs) => new() { Outputs = [.. outputs] };
}

public sealed class PickException(string message) : Exception(message);

/// Thrown when a file in a batch needs no work (e.g. already the target format).
public sealed class PickSkipException : Exception;

public delegate Task<PickResult> PickAction(PickContext context);

public sealed record PickOption
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string Detail { get; init; } = "";
    public string Symbol { get; init; } = "";
    public bool IsFormat { get; init; }
    public string? OutputExt { get; init; }
    public string? Unavailable { get; init; }
    /// Creates new files, so the result can go to a file, the clipboard or both.
    public bool ProducesFiles { get; init; } = true;
    /// Shown on the outer ring when the slot is hovered. A click on the slot runs the preferred one.
    public IReadOnlyList<PickOption> Variants { get; init; } = [];
    /// Opens an editor window first; the result card only appears once the work itself starts.
    public bool Interactive { get; init; }
    /// Set on a variant that the user pinned to the main ring: the id of the slot it belongs to.
    public string? PinnedFrom { get; init; }
    public required PickAction Perform { get; init; }
}

/// Fires once, the first time real work starts (shared by a context and all of its slices).
public sealed class StartSignal
{
    private readonly object gate = new();
    private Action? action;
    private bool fired;

    public bool HasFired { get { lock (gate) return fired; } }

    public void Set(Action next)
    {
        lock (gate) action = next;
    }

    public void Fire()
    {
        Action? pending;
        lock (gate)
        {
            fired = true;
            pending = action;
            action = null;
        }
        pending?.Invoke();
    }
}

public readonly record struct ScreenPoint(double X, double Y)
{
    public static readonly ScreenPoint Zero = new(0, 0);
    public bool IsZero => X == 0 && Y == 0;
}

/// Everything an engine needs to run one option: inputs, output policy, progress and cancellation.
public sealed class PickContext
{
    private readonly Action<double?, string?> sink;
    private readonly double offset;
    private readonly double scale;

    public PickContext(IReadOnlyList<string> files, OutputLocation location, ResultDestination destination = ResultDestination.File,
                       ScreenPoint anchor = default, Guid? jobId = null, StartSignal? started = null,
                       CancellationToken token = default, Action<double?, string?>? sink = null, double offset = 0, double scale = 1)
    {
        Files = files;
        Location = location;
        Destination = destination;
        Anchor = anchor;
        JobId = jobId ?? Guid.NewGuid();
        Started = started ?? new StartSignal();
        Token = token;
        this.sink = sink ?? ((_, _) => { });
        this.offset = offset;
        this.scale = scale;
    }

    public IReadOnlyList<string> Files { get; }
    public OutputLocation Location { get; }
    public ResultDestination Destination { get; }
    public ScreenPoint Anchor { get; }
    public Guid JobId { get; }
    public StartSignal Started { get; }
    public CancellationToken Token { get; }

    /// The same job (progress, output policy, cancellation) working on other files.
    public PickContext With(IReadOnlyList<string> files) =>
        new(files, Location, Destination, Anchor, JobId, Started, Token, sink, offset, scale);

    public void Report(double? fraction, string? note = null)
    {
        Started.Fire();
        sink(fraction.HasValue ? offset + Math.Clamp(fraction.Value, 0, 1) * scale : null, note);
    }

    /// Called by editors once the user confirmed: from here on the job is real work.
    public void BeginWork() => Started.Fire();

    public void Check() => Token.ThrowIfCancellationRequested();

    /// A child context whose progress maps into one slot of a batch.
    public PickContext Slice(int index, int count)
    {
        var width = scale / Math.Max(count, 1);
        return new PickContext(Files, Location, Destination, Anchor, JobId, Started, Token, sink, offset + width * index, width);
    }

    /// Clipboard-only results are written to a private cache folder instead of next to the original.
    public string DirectoryFor(string source) =>
        Destination == ResultDestination.Clipboard ? ClipboardStore.Folder(JobId) : Location.DirectoryFor(source);

    /// "photo.heic" + "png" -> "photo.png"; with a suffix -> "photo compressed.png". Never overwrites.
    public string DestinationFor(string source, string? ext, string? suffix = null)
    {
        var name = FileNames.Split(FileNames.Name(source)).Base;
        if (!string.IsNullOrEmpty(suffix)) name += " " + suffix;
        if (!string.IsNullOrEmpty(ext)) name += "." + ext;
        return FileNames.Unique(Path.Combine(DirectoryFor(source), FileNames.Sanitize(name)));
    }

    /// Runs body against a hidden staging path, then moves the finished result into place. Explorer never
    /// sees half-written files and failures leave nothing behind. Heavy work waits for a slot in the shared queue.
    public async Task<string> Produce(string destination, Func<string, Task> body)
    {
        BeginWork();
        if (WorkQueue.IsHoldingSlot) return await Stage(destination, body);
        var queue = WorkQueue.Shared;
        if (queue.IsFull) sink(null, L("job.queued"));
        await queue.AcquireAsync(Token);
        try
        {
            sink(null, "");
            WorkQueue.IsHoldingSlot = true;
            return await Stage(destination, body);
        }
        finally
        {
            queue.Release();
        }
    }

    public Task<string> Produce(string destination, Action<string> body) =>
        Produce(destination, path => { body(path); return Task.CompletedTask; });

    private async Task<string> Stage(string destination, Func<string, Task> body)
    {
        Check();
        var folder = Path.GetDirectoryName(destination) ?? Path.GetTempPath();
        Directory.CreateDirectory(folder);
        // Staging inside the destination folder keeps the final step a rename on the same volume.
        var work = Path.Combine(folder, ".pickverter-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var info = Directory.CreateDirectory(work);
            info.Attributes |= FileAttributes.Hidden;
        }
        catch (Exception) when (!Directory.Exists(work))
        {
            work = Path.Combine(Path.GetTempPath(), "pickverter-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
        }
        catch (Exception)
        {
        }
        try
        {
            var staged = Path.Combine(work, FileNames.Name(destination));
            await body(staged);
            Check();
            if (!File.Exists(staged) && !Directory.Exists(staged)) throw new PickException(L("err.no_result"));
            var final = FileNames.Unique(destination);
            try
            {
                FileSystem.Move(staged, final);
            }
            catch (IOException)
            {
                // Something took the name in the meantime: pick the next free one.
                final = FileNames.Unique(final);
                FileSystem.Move(staged, final);
            }
            return final;
        }
        finally
        {
            FileSystem.TryDelete(work);
        }
    }
}

public static class Batch
{
    /// Runs body for every selected file and keeps going past individual failures.
    public static PickAction PerFile(Func<string, PickContext, Task<string>> body) => async ctx =>
    {
        var outputs = new List<string>();
        var failures = new List<string>();
        var skipped = 0;
        var count = ctx.Files.Count;
        for (var index = 0; index < count; index++)
        {
            ctx.Check();
            var file = ctx.Files[index];
            if (count > 1) ctx.Report((double)index / count, $"{index + 1}/{count} · {FileNames.Name(file)}");
            try
            {
                outputs.Add(await body(file, ctx.Slice(index, count)));
            }
            catch (PickSkipException)
            {
                skipped++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                if (ctx.Token.IsCancellationRequested) throw new OperationCanceledException(ctx.Token);
                if (count == 1) throw;
                failures.Add($"{FileNames.Name(file)}: {Errors.Describe(error)}");
            }
        }
        if (outputs.Count == 0)
        {
            if (failures.Count > 0) throw new PickException(failures[0]);
            if (skipped > 0) throw new PickException(L("err.same_format"));
        }
        return new PickResult
        {
            Outputs = outputs,
            Message = failures.Count > 0 ? LP("job.failed_some", failures.Count, failures[0]) : null,
        };
    };
}

public static class Errors
{
    public static string Describe(Exception error) => error switch
    {
        PickException pick => pick.Message,
        UnauthorizedAccessException => L("err.no_access", error.Message),
        AggregateException { InnerException: not null } aggregate => Describe(aggregate.InnerException!),
        _ => error.Message,
    };

    public static bool IsAccessDenied(Exception error) => error switch
    {
        UnauthorizedAccessException => true,
        AggregateException { InnerException: not null } aggregate => IsAccessDenied(aggregate.InnerException!),
        IOException io => (io.HResult & 0xFFFF) == 5,
        _ => false,
    };
}

/// Set PICKVERTER_DEBUG=1 to trace gestures and jobs.
public static class Debug
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("PICKVERTER_DEBUG") is { Length: > 0 };
    public static Action<string>? Sink { get; set; }

    public static void Log(string message)
    {
        if (!Enabled) return;
        var line = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + message;
        if (Sink != null) Sink(line); else Console.Error.WriteLine(line);
    }
}
