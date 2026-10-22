using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Pickverter;

public enum JobState { Running, Succeeded, Failed, Cancelled }

public enum JobTone { Info, Warning }

public sealed record NoticeAction(string Title, string Symbol, Action Run);

/// The background-removal model isn't on this PC yet; the card offers the one-time download.
public sealed class ModelMissingException() : Exception(L("bg.model.needed"));

public sealed class Job : INotifyPropertyChanged
{
    public Job(PickOption option, WheelMode mode, IReadOnlyList<string> files, ResultDestination destination, ScreenPoint anchor = default)
    {
        Option = option;
        Title = option.Title;
        Symbol = option.Symbol;
        IsFormat = option.IsFormat;
        Mode = mode;
        Files = files;
        Destination = destination;
        Anchor = anchor;
    }

    /// Informational card without a job behind it.
    public Job(string notice, string detail, string symbol, JobTone tone = JobTone.Warning, NoticeAction? action = null, double? lingers = null)
    {
        Title = notice;
        Symbol = symbol;
        Mode = WheelMode.Actions;
        Files = [];
        Destination = ResultDestination.File;
        Tone = tone;
        Notice = action;
        Lingers = lingers;
        message = detail;
        state = tone == JobTone.Warning ? JobState.Failed : JobState.Succeeded;
        failure = tone == JobTone.Warning ? detail : null;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public PickOption? Option { get; }
    public string Title { get; }
    public string Symbol { get; }
    public bool IsFormat { get; }
    public WheelMode Mode { get; }
    public IReadOnlyList<string> Files { get; }
    public ResultDestination Destination { get; }
    public ScreenPoint Anchor { get; }
    public DateTime Created { get; } = DateTime.Now;
    public JobTone Tone { get; } = JobTone.Info;
    public NoticeAction? Notice { get; }
    /// Seconds before a notice fades on its own (null: the usual timing).
    public double? Lingers { get; }
    internal CancellationTokenSource? Cancellation { get; set; }

    private bool started = true;
    private NoticeAction? fixAction;
    private JobState state = JobState.Running;
    private string? failure;
    private double? progress;
    private string? note;
    private IReadOnlyList<string> outputs = [];
    private string? message;
    private bool undone;
    private bool inCache;
    private bool copied;
    private long sourceBytes;
    private long outputBytes;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name != nameof(Headline)) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Headline)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Subline)));
    }

    /// Interactive jobs stay invisible until the editor is confirmed.
    public bool Started { get => started; set => Set(ref started, value); }
    /// Offered on a failed card when something outside Pickverter can fix the failure.
    public NoticeAction? FixAction { get => fixAction; set => Set(ref fixAction, value); }
    public JobState State { get => state; private set => Set(ref state, value); }
    public string? Failure => failure;
    public double? Progress { get => progress; set => Set(ref progress, value); }
    public string? Note { get => note; set => Set(ref note, value); }
    public IReadOnlyList<string> Outputs { get => outputs; private set => Set(ref outputs, value); }
    public string? Message => message;
    public bool Undone { get => undone; private set => Set(ref undone, value); }
    /// Results sit in the private clipboard cache (not saved next to the original yet).
    public bool InCache { get => inCache; private set => Set(ref inCache, value); }
    public bool Copied { get => copied; private set => Set(ref copied, value); }
    public bool Undoable { get; private set; }
    public bool Silent { get; private set; }

    public bool IsRunning => State == JobState.Running;
    public bool IsNotice => Option == null;
    public bool HasFiles => Outputs.Count > 0 && !Undone;

    public async Task Complete(PickResult result)
    {
        message = result.Message;
        Undoable = result.Undoable && result.Outputs.Count > 0;
        Silent = result.Silent;
        InCache = result.Outputs.Any(ClipboardStore.Contains);
        if (result.Outputs.Count > 0)
        {
            sourceBytes = Files.Sum(FileNames.Size);
            outputBytes = result.Outputs.Sum(FileNames.Size);
            if (Destination.Copies() && Option?.ProducesFiles == true)
            {
                try
                {
                    await Platform.Clipboard.WriteResultsAsync(result.Outputs);
                    Copied = true;
                }
                catch (Exception error)
                {
                    Debug.Log("clipboard: " + error.Message);
                }
            }
        }
        Outputs = result.Outputs;
        Progress = 1;
        State = JobState.Succeeded;
    }

    public void Fail(string reason)
    {
        failure = reason;
        State = JobState.Failed;
    }

    public void MarkCancelled() => State = JobState.Cancelled;

    public string Label => IsFormat ? L("job.convert_label", Title) : Title;

    public string Headline
    {
        get
        {
            if (IsNotice) return Title;
            return State switch
            {
                JobState.Running => L("job.running", Label),
                JobState.Succeeded => Outputs.Count == 0 ? Label : Undone ? L("job.undone", Label) : L("job.done", Label),
                JobState.Failed => L("job.failed", Label),
                _ => L("job.cancelled", Label),
            };
        }
    }

    public string Subline
    {
        get
        {
            switch (State)
            {
                case JobState.Running:
                    return Note ?? (Files.Count == 1 ? FileNames.Name(Files[0]) : LP("wheel.items", Files.Count));
                case JobState.Failed:
                    return failure ?? "";
                case JobState.Cancelled:
                    return L("job.stopped");
            }
            if (IsNotice) return message ?? "";
            if (Undone) return L("job.trashed");
            if (Outputs.Count == 0) return message ?? "";
            var parts = new List<string> { Outputs.Count == 1 ? FileNames.Name(Outputs[0]) : LP("job.files_created", Outputs.Count) };
            var size = FileNames.FormatBytes(outputBytes);
            var shrinks = new[] { "tool.compress", "tool.resize", "tool.resolution" }.Any(prefix => Option?.Id.StartsWith(prefix, StringComparison.Ordinal) == true);
            if (shrinks && sourceBytes > 0 && outputBytes > 0 && outputBytes < sourceBytes)
            {
                var saved = (int)((1 - outputBytes / (double)sourceBytes) * 100);
                if (saved >= 1) size += $" (−{saved}%)";
            }
            parts.Add(size);
            var line = string.Join(" · ", parts);
            if (InCache) line = L("job.in_clipboard") + " · " + line;
            else if (Copied) line += " · " + L("job.also_copied");
            if (message != null) line += "\n" + message;
            return line;
        }
    }

    public void Cancel() => Cancellation?.Cancel();

    public void Reveal() => Platform.Shell.Reveal(Outputs);

    public void Open()
    {
        if (Outputs.Count > 0) Platform.Shell.Open(Outputs[0]);
    }

    public async Task CopyToClipboard()
    {
        await Platform.Clipboard.WriteResultsAsync(Outputs);
        Copied = true;
    }

    /// Moves clipboard-only results to where files normally go.
    public async Task SaveFromCache()
    {
        var location = Preferences.Shared.OutputLocation;
        var moved = new List<string>();
        for (var index = 0; index < Outputs.Count; index++)
        {
            var output = Outputs[index];
            var source = index < Files.Count ? Files[index] : Files.Count > 0 ? Files[0] : output;
            var target = FileNames.Unique(Path.Combine(location.DirectoryFor(source), FileNames.Name(output)));
            try
            {
                FileSystem.Move(output, target);
                moved.Add(target);
            }
            catch (Exception)
            {
                moved.Add(output);
            }
        }
        InCache = false;
        Undoable = true;
        Outputs = moved;
        if (Copied) await Platform.Clipboard.WriteResultsAsync(Outputs);
    }

    public async Task Undo()
    {
        var existing = Outputs.Where(path => File.Exists(path) || Directory.Exists(path)).ToList();
        await Platform.Shell.RecycleAsync(existing);
        Undone = true;
    }
}

/// Shows result cards (the Windows layer draws them; tests and the command line don't).
public interface IJobPresenter
{
    void Show(Job job);
    void ScheduleDismiss(Job job);
    void Remove(Job job);
}

public sealed class NullPresenter : IJobPresenter
{
    public void Show(Job job) { }
    public void ScheduleDismiss(Job job) { }
    public void Remove(Job job) { }
}

public sealed class JobCenter
{
    public static JobCenter Shared { get; } = new();

    public ObservableCollection<Job> Jobs { get; } = [];
    public IJobPresenter Presenter { get; set; } = new NullPresenter();

    public Job Run(PickOption option, WheelMode mode, IReadOnlyList<string> files, ScreenPoint anchor, ResultDestination destination = ResultDestination.File)
    {
        var job = new Job(option, mode, files, destination, anchor);
        Jobs.Insert(0, job);
        while (Jobs.Count > 40) Jobs.RemoveAt(Jobs.Count - 1);
        if (destination == ResultDestination.Clipboard) ClipboardStore.Prune();
        var signal = new StartSignal();
        var cancellation = new CancellationTokenSource();
        job.Cancellation = cancellation;
        var context = new PickContext(files, Preferences.Shared.OutputLocation, destination, anchor, job.Id, signal, cancellation.Token,
            (fraction, note) => Platform.Main.Post(() =>
            {
                if (!job.IsRunning) return;
                if (fraction.HasValue) job.Progress = fraction;
                if (note != null) job.Note = note.Length == 0 ? null : note;
            }));
        if (option.Interactive)
        {
            job.Started = false;
            signal.Set(() => Platform.Main.Post(() =>
            {
                if (!job.IsRunning) return;
                job.Started = true;
                Presenter.Show(job);
            }));
        }
        _ = Execute(job, option, context, mode == WheelMode.Actions ? 450 : 120);
        return job;
    }

    private async Task Execute(Job job, PickOption option, PickContext context, int delay)
    {
        IDisposable? reveal = null;
        if (!option.Interactive)
        {
            reveal = Platform.Main.After(TimeSpan.FromMilliseconds(delay), () => Platform.Main.Post(() =>
            {
                if (job.IsRunning) Presenter.Show(job);
            }));
        }
        try
        {
            var result = await Task.Run(() => option.Perform(context), context.Token);
            await Platform.Main.RunAsync(() => { });
            await job.Complete(result);
        }
        catch (OperationCanceledException)
        {
            job.MarkCancelled();
        }
        catch (ModelMissingException error)
        {
            job.FixAction = new NoticeAction(L("bg.model.download"), "arrow.down.circle", () => RunWithModel(job, option));
            job.Fail(error.Message);
        }
        catch (Exception error) when (Errors.IsAccessDenied(error))
        {
            var folder = job.Files.Count > 0 ? Path.GetDirectoryName(job.Files[0]) ?? job.Files[0] : "";
            if (Platform.Shell.ControlledFolderAccess)
                job.FixAction = new NoticeAction(L("access.fix"), "lock.open", Platform.Shell.OpenControlledFolderSettings);
            job.Fail(L("err.no_access", FileNames.Name(folder)));
        }
        catch (Exception error)
        {
            Debug.Log($"job {option.Id} failed: {error}");
            job.Fail(Errors.Describe(error));
        }
        finally
        {
            reveal?.Dispose();
        }
        Platform.Main.Post(() => Finish(job));
    }

    /// Downloads the background model (progress on a fresh card), then runs the option again.
    private void RunWithModel(Job failed, PickOption option)
    {
        Presenter.Remove(failed);
        var combined = option with
        {
            Perform = async ctx =>
            {
                await BackgroundModel.EnsureAsync(ctx);
                return await option.Perform(ctx);
            },
        };
        Run(combined, failed.Mode, failed.Files, failed.Anchor, failed.Destination);
    }

    private void Finish(Job job)
    {
        if (!job.Started)
        {
            // The editor was cancelled before anything happened: leave no trace.
            if (job.State == JobState.Cancelled)
            {
                Jobs.Remove(job);
                return;
            }
            job.Started = true;
        }
        if (Platform.Feedback.ScreenReaderActive) Platform.Feedback.Announce(job.Headline);
        switch (job.State)
        {
            case JobState.Succeeded:
                if (job.Silent)
                {
                    Presenter.Remove(job);
                    Jobs.Remove(job);
                    return;
                }
                Presenter.Show(job);
                Presenter.ScheduleDismiss(job);
                if (Preferences.Shared.PlaySounds && job.Outputs.Count > 0) Platform.Feedback.Success();
                break;
            case JobState.Failed:
                Presenter.Show(job);
                Presenter.ScheduleDismiss(job);
                if (Preferences.Shared.PlaySounds) Platform.Feedback.Failure();
                break;
            case JobState.Cancelled:
                Presenter.ScheduleDismiss(job);
                break;
        }
    }

    public void Notice(string title, string detail, string symbol, JobTone tone = JobTone.Warning, NoticeAction? action = null, double? lingers = null)
    {
        var job = new Job(title, detail, symbol, tone, action, lingers);
        Presenter.Show(job);
        Presenter.ScheduleDismiss(job);
    }

    /// Runs an option to completion without cards (command line, tests).
    public static async Task<PickResult> RunDirect(PickOption option, IReadOnlyList<string> files, ResultDestination destination,
                                                   Action<double?, string?>? sink = null, CancellationToken token = default)
    {
        var context = new PickContext(files, OutputLocation.SameFolder, destination, default, null, null, token, sink);
        return await option.Perform(context);
    }
}
