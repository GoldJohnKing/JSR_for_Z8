using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jsr4Z8.Core;
using Jsr4Z8.Core.Dark;
using Jsr4Z8.Core.Model;
using Jsr4Z8.Core.Ptc;
using Jsr4Z8.UI.Services;

namespace Jsr4Z8.UI.ViewModels;

/// <summary>
/// Lifecycle of one task card (docs/design/card-states.html): Blocked ④校验失败/⑤机型不支持,
/// Ready ①, Running ②, Done ③, Failed ⑥运行失败.
/// </summary>
public enum TaskState
{
    Blocked,
    Ready,
    Running,
    Done,
    Failed,
}

/// <summary>One file chip in the card's chip row. Chips always display in natural
/// filename order — starring a reference only moves the ★ (<see cref="IsReference"/>),
/// never the slots; <see cref="TaskGroupViewModel.Group"/> carries the
/// reference-first contract instead. <see cref="InfoText"/> is the multi-line hover
/// text (one capture parameter per line, — for missing values; 无法读取 when the
/// frame's metadata could not be read).</summary>
public sealed partial class FrameChipViewModel : ObservableObject
{
    /// <summary>Display name (file name only) of the frame.</summary>
    public string FileName { get; }

    /// <summary>Full path of the frame — the identity RemoveFile works on.</summary>
    public string Path { get; }

    [ObservableProperty] private bool _isReference;
    [ObservableProperty] private string _infoText;

    public FrameChipViewModel(string path, bool isReference, RawMetadata? meta)
    {
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        _isReference = isReference;
        _infoText = FormatInfoText(meta);
    }

    /// <summary>Hover text: 机型/快门/光圈/ISO/位深/压缩/画幅/拍摄时间 one per line,
    /// each missing value rendered as —; a null metadata (unreadable file) collapses
    /// to the single line 无法读取.</summary>
    private static string FormatInfoText(RawMetadata? m)
    {
        if (m is null)
            return "无法读取";

        string model = string.Join(" ",
            new[] { m.Make, m.Model }.Where(s => !string.IsNullOrWhiteSpace(s)));
        string shutter = m.ExposureTime > 0 ? Shutter(m.ExposureTime) : "—";
        string aperture = m.Aperture > 0
            ? $"f/{m.Aperture.ToString("0.##", CultureInfo.InvariantCulture)}"
            : "—";
        string captured = string.IsNullOrWhiteSpace(m.DateTimeOriginal) ? "—" : m.DateTimeOriginal;
        return string.Join("\n", new[]
        {
            $"机型 {(model.Length == 0 ? "—" : model)}",
            $"快门 {shutter}",
            $"光圈 {aperture}",
            $"ISO {m.Iso.ToString("0.###", CultureInfo.InvariantCulture)}",
            $"位深 {m.BitsPerSample.ToString(CultureInfo.InvariantCulture)}bit",
            $"压缩 {Compression(m.NefCompression)}",
            $"画幅 {m.Width.ToString(CultureInfo.InvariantCulture)}×{m.Height.ToString(CultureInfo.InvariantCulture)}",
            $"拍摄时间 {captured}",
        });
    }

    /// <summary>Camera-style shutter text (the deleted new-task dialog's convention):
    /// 1/8000s, 1/100s, 2s, 0.5s.</summary>
    private static string Shutter(double seconds) =>
        seconds >= 1
            ? $"{seconds.ToString(seconds == Math.Truncate(seconds) ? "0" : "0.##", CultureInfo.InvariantCulture)}s"
            : $"1/{Math.Round(1 / seconds):0}s";

    /// <summary>Friendly NEF compression name (3 无损 / 13 HE / 14 HE*; anything else
    /// keeps its raw code).</summary>
    private static string Compression(int nefCompression) => nefCompression switch
    {
        3 => "无损",
        13 => "HE",
        14 => "HE*",
        _ => nefCompression.ToString(CultureInfo.InvariantCulture),
    };
}

/// <summary>
/// Pure-state ViewModel for one task-group card. Wraps Core's TaskGroup/ValidationResult
/// without mutating them — <see cref="AddFilesAsync"/> (progressive top-up),
/// <see cref="RemoveFileCommand"/> and <see cref="SetReferenceCommand"/> replace the
/// group instance, notified via <see cref="Group"/> — and stays free of Avalonia
/// dependencies so it runs headless under xUnit. The card is a mutable file set now:
/// metadata is cached per path, every set change revalidates and refreshes Ptc/Dark,
/// and the Ready↔Blocked transition follows the new validation. Mutations are
/// UI-thread-affine (<see cref="AddFilesAsync"/> does its reads on a background thread
/// and applies inline after the await); invalid state transitions are still ignored
/// (never thrown) so background-pushed runner events can never crash the app.
/// </summary>
public partial class TaskGroupViewModel : ObservableObject
{
    private readonly ITaskRunner _runner;

    // Task 3 mutable file set: display order (natural) + per-path caches. Reads happen
    // on background threads (AddFilesAsync) or replay from these caches; VM mutations
    // are UI-thread-affine.
    private readonly Func<string, RawMetadata> _read;
    private readonly List<string> _framePaths;
    private readonly Dictionary<string, RawMetadata> _meta;
    private readonly Dictionary<string, string> _readErrors;
    private bool _manualReference;
    private string? _manualReferencePath;
    private CancellationTokenSource? _cts;

    /// <summary>The 7-frame job; <see cref="SetReferenceCommand"/> and the progressive
    /// add/remove recompute replace the instance (with change notification).</summary>
    public TaskGroup Group { get; private set; }

    /// <summary>The PTC match of the LATEST validation — the queue's request JSON needs
    /// its fullwell (BuildJson rounds inside). Refreshed on every file-set change so a
    /// Blocked→Ready top-up hands the runner the new match; null while Blocked.</summary>
    public PtcMatch? Ptc
    {
        get => _ptc;
        private set => SetProperty(ref _ptc, value);
    }

    private PtcMatch? _ptc;

    /// <summary>The preset dark of the LATEST validation — the queue's request JSON needs
    /// its black/read-noise pair. Refreshed with <see cref="Ptc"/>; null while Blocked.</summary>
    public DarkMatch? Dark
    {
        get => _dark;
        private set => SetProperty(ref _dark, value);
    }

    private DarkMatch? _dark;

    /// <summary>Reference frame filename (FramePaths[0]); follows a reference swap.</summary>
    [ObservableProperty] private string _referenceName = "";

    /// <summary>Output file the pipeline produces: JSR_&lt;基准帧名&gt;.dng; follows a
    /// reference swap.</summary>
    [ObservableProperty] private string _outputName = "";

    public ObservableCollection<FrameChipViewModel> FrameChips { get; } = new();

    /// <summary>Raised by the Remove command; the owning MainViewModel removes this card.</summary>
    public event EventHandler? RemoveRequested;

    [ObservableProperty] private TaskState _state;
    [ObservableProperty] private int _progress;
    [ObservableProperty] private string _stageText = "";
    [ObservableProperty] private string _issuesText = "";
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private string? _outputPath;

    /// <summary>Edits (progressive add / ✕ / ★基准) are a Ready/Blocked-only interaction:
    /// Running/Done/Failed cards are locked — a Running group is in flight and a Done
    /// output was produced from the old set. The add button, star and ✕ all gate on
    /// this (the command CanExecutes below mirror it).</summary>
    public bool IsEditable => State is TaskState.Ready or TaskState.Blocked;

    public TaskGroupViewModel(TaskGroup group, ValidationResult? validation = null, ITaskRunner? runner = null,
        IReadOnlyDictionary<string, RawMetadata>? metadata = null, Func<string, RawMetadata>? read = null)
    {
        Group = group;
        _runner = runner ?? NullTaskRunner.Instance;
        _runner.IsBusyChanged += OnRunnerBusyChanged;
        _read = read ?? RawMetadataReader.Read;
        _framePaths = group.FramePaths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(Path.GetFileName, NaturalFileNameComparer.Instance)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _meta = new Dictionary<string, RawMetadata>(StringComparer.OrdinalIgnoreCase);
        if (metadata is not null)
            foreach (KeyValuePair<string, RawMetadata> kv in metadata)
                _meta[kv.Key] = kv.Value;
        _readErrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _ptc = validation?.Ptc;
        _dark = validation?.Dark;
        RebuildChips();
        ReferenceName = group.FramePaths.Count > 0 ? Path.GetFileName(group.ReferencePath) : "";
        OutputName = $"JSR_{Path.GetFileNameWithoutExtension(ReferenceName)}.dng";
        _state = validation is { CanRun: true } ? TaskState.Ready : TaskState.Blocked;
        if (validation is { CanRun: false })
            _issuesText = string.Join("\n", validation.Issues.Select(i => i.MessageZh));
    }

    /// <summary>Badge text (card-states.html): 就绪 / 运行阶段文字 / 已完成 / 失败 / 无法执行.
    /// While Running the settable <see cref="StageText"/> IS the badge text.</summary>
    public string StateText => State switch
    {
        TaskState.Ready => "就绪",
        TaskState.Running => string.IsNullOrEmpty(StageText) ? "运行中" : StageText,
        TaskState.Done => "已完成",
        TaskState.Failed => "失败",
        _ => "无法执行",
    };

    /// <summary>Drives the progress bar row visibility (mockup ② shows it only while running).</summary>
    public bool IsRunning => State == TaskState.Running;

    /// <summary>Meta line under the chips: the pending output name on Ready/Blocked,
    /// the written path once Done; empty while Running/Failed (the stage badge and
    /// error bar carry those states).</summary>
    public string MetaText => State switch
    {
        TaskState.Done => $"已输出 {OutputPath ?? OutputName}",
        TaskState.Ready or TaskState.Blocked => $"待输出 {OutputName}",
        _ => "",
    };

    /// <summary>
    /// State machine edges: Blocked→Ready (revalidated); Ready→Running (start) / Blocked
    /// (revalidate) / Failed (immediate run error); Running→Done/Failed (finish) / Ready
    /// (stop); Done→Ready (requeue); Failed→Running (retry) / Ready. Every Go* returns
    /// true iff the state CHANGED:
    /// same-state is a no-op returning false, and any other edge is IGNORED (returns
    /// false, no throw) — Task 13 pushes backend events from background threads, where a
    /// late event (e.g. progress arriving after a user stop) must never crash the app.
    /// </summary>
    public bool GoReady()
    {
        if (!Transition(TaskState.Ready, TaskState.Blocked, TaskState.Running, TaskState.Done, TaskState.Failed))
            return false;
        Progress = 0;
        StageText = "";
        ErrorText = "";
        return true;
    }

    public bool GoBlocked(string? issuesText = null)
    {
        if (!Transition(TaskState.Blocked, TaskState.Ready, TaskState.Failed))
            return false;
        if (issuesText != null)
            IssuesText = issuesText;
        Progress = 0;
        StageText = "";
        ErrorText = "";
        return true;
    }

    public bool GoRunning()
    {
        if (!Transition(TaskState.Running, TaskState.Ready, TaskState.Failed))
            return false;
        Progress = 0;
        StageText = "";
        ErrorText = ""; // retry after failure must not show the stale error bar
        return true;
    }

    public bool GoDone(string? outputPath = null)
    {
        if (!Transition(TaskState.Done, TaskState.Running))
            return false;
        Progress = 100;
        ErrorText = ""; // defense: a success after a failure must never show a stale error
        if (outputPath != null)
            OutputPath = outputPath;
        return true;
    }

    public bool GoFailed(string? message = null)
    {
        if (!Transition(TaskState.Failed, TaskState.Running, TaskState.Ready))
            return false;
        ErrorText = message ?? "";
        return true;
    }

    /// <summary>Backend progress in percent, clamped to 0-100 (Task 6 carried finding: raw
    /// PROGRESS lines may exceed 100); the optional stage text becomes the badge text.</summary>
    public void ReportProgress(int percent, string? stage = null)
    {
        Progress = Math.Clamp(percent, 0, 100);
        if (stage != null)
            StageText = stage;
    }

    private bool Transition(TaskState to, params TaskState[] from)
    {
        if (State == to)
            return false;
        if (Array.IndexOf(from, State) < 0)
            return false;
        State = to;
        return true;
    }

    /// <summary>Start (and Retry, below): enter Running, then hand the card to the runner.
    /// The queue runner maps engine events onto the card and finishes it through
    /// GoDone/GoFailed. The optional external token (queue-level entry, below) links a
    /// 全部开始-level stop into this run; the card's own ■停止 cancels only its own token.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    public Task StartAsync() => StartCoreAsync(CancellationToken.None);

    /// <summary>Queue-level entry (MainViewModel.StartAll): cancelling externalToken stops
    /// this run together with the whole queue; the card's own Stop stops only this run.</summary>
    public Task StartAsync(CancellationToken externalToken) => StartCoreAsync(externalToken);

    private async Task StartCoreAsync(CancellationToken externalToken)
    {
        if (!GoRunning())
            return;
        _cts?.Dispose();
        _cts = externalToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(externalToken)
            : new CancellationTokenSource();
        try
        {
            await _runner.RunAsync(this, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            GoFailed("已停止");
        }
        catch (Exception ex)
        {
            GoFailed(ex.Message);
        }
    }

    // Single-flight (Task 13): while the queue is running another card, 开始此组/重试
    // are disabled — one run at a time.
    private bool CanStart() => State == TaskState.Ready && !_runner.IsBusy;

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private Task RetryAsync() => StartAsync();

    private bool CanRetry() => State == TaskState.Failed && !_runner.IsBusy;

    private void OnRunnerBusyChanged(object? sender, EventArgs e)
    {
        StartCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Stops the in-flight run by cancelling its token; the runner's continuation
    /// moves the card to Failed("已停止").</summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _cts?.Cancel();

    private bool CanStop() => State == TaskState.Running;

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void Remove() => RemoveRequested?.Invoke(this, EventArgs.Empty);

    // Per docs/design/card-states.html: ✕移除 shows on Blocked(④⑤)/Ready(①)/Done(③)/Failed(⑥)
    // cards; Running(②) shows only ■停止.
    private bool CanRemove() => State is TaskState.Blocked or TaskState.Ready or TaskState.Done or TaskState.Failed;

    /// <summary>★星标基准 (Task 3 semantics — 标记不重排): starring a chip marks it as
    /// the manual reference — chips stay in natural display order, only the ★ moves;
    /// <see cref="Group"/> is rebuilt with the starred frame first (the remaining
    /// frames keep display order) and ReferenceName/OutputName follow. No
    /// revalidation here: PTC/dark depend on ISO+compression, which are group-uniform
    /// when runnable (Blocked groups hold null either way), and the existing issues
    /// text stays. Removing the starred frame later falls back to the automatic rule.</summary>
    [RelayCommand(CanExecute = nameof(CanSetReference))]
    private void SetReference(FrameChipViewModel chip)
    {
        if (chip is null || !IsEditable ||
            !_framePaths.Contains(chip.Path, StringComparer.OrdinalIgnoreCase))
            return;
        _manualReference = true;
        _manualReferencePath = chip.Path;
        ApplyReference(chip.Path);
    }

    private bool CanSetReference() => IsEditable;

    /// <summary>渐进补齐 (card-level add; the 添加 RAW… button and card drops arrive in
    /// Task 4's views): .NEF filter → dedupe vs the existing set (OrdinalIgnoreCase) →
    /// no-op when nothing new. New files are read on a background thread — failures
    /// are recorded but still added as frames, so revalidation flags them 无法读取 —
    /// then the whole derived set (ordering, reference, validation) is recomputed and
    /// applied inline after the await: the VM is UI-thread-affine for mutations, the
    /// worker only touches snapshot copies. A card that locked while the read was in
    /// flight discards the result.</summary>
    public async Task AddFilesAsync(IReadOnlyList<string> paths)
    {
        if (!IsEditable || paths.Count == 0)
            return;

        List<string> fresh = paths
            .Where(p => string.Equals(Path.GetExtension(p), ".nef", StringComparison.OrdinalIgnoreCase))
            .Where(p => !_framePaths.Contains(p, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (fresh.Count == 0)
            return;

        List<string> next = _framePaths.Concat(fresh).ToList();
        var meta = new Dictionary<string, RawMetadata>(_meta, StringComparer.OrdinalIgnoreCase);
        var errors = new Dictionary<string, string>(_readErrors, StringComparer.OrdinalIgnoreCase);
        Func<string, RawMetadata> source = _read;
        await Task.Run(() =>
        {
            foreach (string path in next)
            {
                if (meta.ContainsKey(path) || errors.ContainsKey(path))
                    continue;
                try
                {
                    meta[path] = source(path);
                }
                catch (RawReadException ex)
                {
                    errors[path] = ex.Message;
                }
            }
        });

        if (!IsEditable)
            return;

        // Merge (not replace): another in-flight add may have landed while this call
        // was reading — unioning keeps the every-path-cached invariant, so the
        // recompute below never needs a UI-thread read.
        foreach (KeyValuePair<string, RawMetadata> kv in meta)
            _meta[kv.Key] = kv.Value;
        foreach (KeyValuePair<string, string> kv in errors)
            _readErrors[kv.Key] = kv.Value;
        _framePaths.AddRange(fresh);
        RecomputeAndApply();
    }

    /// <summary>✕ on a chip: removes that frame and its cache entries. Removing the
    /// starred reference hands the pick back to the automatic exposure rule
    /// (<c>_manualReference=false</c>); removing the LAST frame raises
    /// <see cref="RemoveRequested"/> (the card disappears) instead of leaving an empty
    /// group. Anything else recomputes and revalidates synchronously — pure with
    /// respect to the file system, because every surviving path is already cached or
    /// error-recorded. No-op on locked cards.</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveFile))]
    public void RemoveFile(FrameChipViewModel chip)
    {
        if (chip is null || !IsEditable)
            return;
        int index = _framePaths.FindIndex(
            p => string.Equals(p, chip.Path, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return;

        string path = _framePaths[index];
        _framePaths.RemoveAt(index);
        _meta.Remove(path);
        _readErrors.Remove(path);
        if (_manualReference && string.Equals(_manualReferencePath, path, StringComparison.OrdinalIgnoreCase))
        {
            _manualReference = false;
            _manualReferencePath = null;
        }

        if (_framePaths.Count == 0)
        {
            RemoveRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        RecomputeAndApply();
    }

    private bool CanRemoveFile() => IsEditable;

    /// <summary>The reference after a set change: the manual star while it still
    /// exists, otherwise the creator's exposure-middle rule over the display order.</summary>
    private string CurrentReference(IReadOnlyList<string> display) =>
        _manualReference && _manualReferencePath is { } starred &&
        display.Contains(starred, StringComparer.OrdinalIgnoreCase)
            ? starred
            : TaskGroupCreator.PickReference(display, ReadCached);

    /// <summary>Recomputes the whole derived set after the frame paths changed: natural
    /// display order, reference, full revalidation through the caches — then applies
    /// (Group, chips, Ptc/Dark, IssuesText and the Ready↔Blocked transition). AddFilesAsync
    /// guarantees every path is cached or error-recorded before calling; an uncached
    /// path (a card built without full metadata) falls back to a synchronous read here.</summary>
    private void RecomputeAndApply()
    {
        List<string> display = _framePaths
            .OrderBy(Path.GetFileName, NaturalFileNameComparer.Instance)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _framePaths.Clear();
        _framePaths.AddRange(display);

        string reference = CurrentReference(display);
        TaskGroup group = BuildGroup(display, reference);
        ApplyValidated(group, TaskGroupValidator.Validate(group, ReadCached));
    }

    /// <summary>Star-only apply (no revalidation): rebuild Group reference-first, follow
    /// ReferenceName/OutputName, move the ★ flags — chips never change order.</summary>
    private void ApplyReference(string reference)
    {
        Group = BuildGroup(_framePaths, reference);
        OnPropertyChanged(nameof(Group));
        ReferenceName = Path.GetFileName(reference);
        OutputName = $"JSR_{Path.GetFileNameWithoutExtension(ReferenceName)}.dng";
        foreach (FrameChipViewModel chip in FrameChips)
            chip.IsReference = string.Equals(chip.Path, reference, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Full apply after revalidation: Group, chips (natural order, ★ flags,
    /// hover texts), ReferenceName/OutputName, refreshed Ptc/Dark, the joined issues
    /// text and the Ready↔Blocked transition (the runner reads Ptc/Dark when it
    /// starts, so it always sees the match of the current set).</summary>
    private void ApplyValidated(TaskGroup group, ValidationResult validation)
    {
        Group = group;
        OnPropertyChanged(nameof(Group));
        RebuildChips();
        ReferenceName = Path.GetFileName(group.ReferencePath);
        OutputName = $"JSR_{Path.GetFileNameWithoutExtension(ReferenceName)}.dng";
        Ptc = validation.Ptc;
        Dark = validation.Dark;
        IssuesText = validation.CanRun
            ? ""
            : string.Join("\n", validation.Issues.Select(i => i.MessageZh));
        if (validation.CanRun)
            GoReady();
        else
            GoBlocked(null);
    }

    private static TaskGroup BuildGroup(IEnumerable<string> display, string reference) => new(
        display.Where(p => !string.Equals(p, reference, StringComparison.OrdinalIgnoreCase))
            .Prepend(reference)
            .ToList());

    /// <summary>Rebuilds the chip row in natural display order with the ★ on the
    /// reference and per-chip hover texts from the metadata cache (a cache miss —
    /// unreadable at read time — renders 无法读取).</summary>
    private void RebuildChips()
    {
        FrameChips.Clear();
        string reference = Group.FramePaths.Count > 0 ? Group.ReferencePath : "";
        foreach (string path in _framePaths)
            FrameChips.Add(new FrameChipViewModel(
                path,
                string.Equals(path, reference, StringComparison.OrdinalIgnoreCase),
                _meta.TryGetValue(path, out RawMetadata? m) ? m : null));
    }

    /// <summary>Memoized reader for revalidation/reference recomputes: cache hit → the
    /// recorded metadata; a recorded failure replays its RawReadException (so the
    /// validator re-flags the file 无法读取 without a UI-thread re-read); anything else
    /// falls through to the real reader and caches the success.</summary>
    private RawMetadata ReadCached(string path)
    {
        if (_meta.TryGetValue(path, out RawMetadata? cached))
            return cached;
        if (_readErrors.TryGetValue(path, out string? error))
            throw new RawReadException(error);
        RawMetadata m = _read(path);
        _meta[path] = m;
        return m;
    }

    /// <summary>Done card's 📂 button (final-review finding 1): opens Explorer with the
    /// written output file selected (/select). Guards: a null/empty or unresolvable
    /// OutputPath is a no-op; when the containing folder still exists but the file was
    /// moved, the folder opens WITHOUT /select; the whole command is wrapped so a shell
    /// failure can never throw out of the button handler.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    private void OpenFolder()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(OutputPath))
                return;
            string? dir = Path.GetDirectoryName(Path.GetFullPath(OutputPath));
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return;
            string arguments = File.Exists(OutputPath)
                ? $"/select,\"{OutputPath}\""
                : $"\"{dir}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Shell wiring (final-review finding 1): never throw from the command.
        }
    }

    private bool CanOpenFolder() => State == TaskState.Done;

    partial void OnStateChanged(TaskState value)
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(MetaText));
        OnPropertyChanged(nameof(IsEditable));
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
        OpenFolderCommand.NotifyCanExecuteChanged();
        SetReferenceCommand.NotifyCanExecuteChanged();
        RemoveFileCommand.NotifyCanExecuteChanged();
    }

    partial void OnStageTextChanged(string value) => OnPropertyChanged(nameof(StateText));

    partial void OnOutputPathChanged(string? value) => OnPropertyChanged(nameof(MetaText));

    partial void OnOutputNameChanged(string value) => OnPropertyChanged(nameof(MetaText));
}
