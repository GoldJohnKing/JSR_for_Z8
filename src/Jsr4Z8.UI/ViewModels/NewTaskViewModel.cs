using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jsr4Z8.Core;
using Jsr4Z8.Core.Model;
using Jsr4Z8.Core.Settings;

namespace Jsr4Z8.UI.ViewModels;

/// <summary>
/// One file chip inside the new-task drop zone (docs/design/add-task-flow-v3.html):
/// ★基准 follows slot 0, IsError turns an unreadable file red. The two commands
/// delegate to the owning <see cref="NewTaskViewModel"/> and take this chip as
/// their parameter.
/// </summary>
public partial class FrameChipVm : ObservableObject
{
    public FrameChipVm(string path, ICommand setReference, ICommand remove)
    {
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        SetReference = setReference;
        Remove = remove;
    }

    public string Path { get; }

    public string FileName { get; }

    public ICommand SetReference { get; }

    public ICommand Remove { get; }

    [ObservableProperty] private bool _isReference;
    [ObservableProperty] private bool _isError;
}

/// <summary>Live validation outcome of the dialog's current file set: CanRun gates
/// 创建任务组, IssuesText is the red issue line, PtcInfoText the green ✓ line
/// (each empty while its side is not shown).</summary>
public sealed record ValidationState(bool CanRun, string IssuesText, string PtcInfoText)
{
    public static readonly ValidationState Empty = new(false, "", "");
}

/// <summary>CreateCompleted payload: the dialog's result — the group (★基准 first) plus
/// the validation that gated it, so MainViewModel.AddValidated creates a Ready card
/// without re-reading the files.</summary>
public sealed record CreatedTaskGroup(TaskGroup Group, ValidationResult Validation);

/// <summary>
/// Headless ViewModel of the 新建任务组 dialog (docs/design/add-task-flow-v3.html):
/// one dashed drop zone whose 7 NEF frames live as chips (natural-sorted by file name,
/// first = ★基准, click a chip to swap), an independent dark-frame picker, and live
/// validation debounced 300 ms on a background thread (result marshaled back through
/// the injectable <c>post</c> seam — Dispatcher.UIThread in the app, inline in tests).
/// All rules live here; NewTaskDialog code-behind only wires drag-drop, file pickers
/// and closing. Drop semantics (carried ruling): a drop REPLACES the whole frame set.
/// </summary>
public partial class NewTaskViewModel : ObservableObject
{
    /// <summary>Spec: 300 ms 防抖.</summary>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(300);

    private readonly Func<string, RawMetadata> _read;
    private readonly TimeSpan _debounce;
    private readonly Action<Action> _post;
    private readonly SettingsService? _settings;
    private CancellationTokenSource? _validateCts;
    private Task _validationTask = Task.CompletedTask;
    private ValidationResult? _lastValidation;
    private int _generation;

    public NewTaskViewModel(
        Func<string, RawMetadata>? read = null,
        TimeSpan? debounce = null,
        Action<Action>? post = null,
        SettingsService? settings = null)
    {
        _read = read ?? RawMetadataReader.Read;
        _debounce = debounce ?? DefaultDebounce;
        _post = post ?? (action => action());
        _settings = settings;
        Frames.CollectionChanged += OnFramesChanged;
    }

    /// <summary>The drop zone's chip wall; slot 0 is always the ★基准 frame.</summary>
    public ObservableCollection<FrameChipVm> Frames { get; } = new();

    /// <summary>Settled validation pass of the latest change — the headless-test seam:
    /// <c>await vm.ValidationIdle</c> then assert <see cref="State"/>.</summary>
    public Task ValidationIdle => _validationTask;

    public event EventHandler<CreatedTaskGroup>? CreateCompleted;

    /// <summary>Raised by 取消; the dialog wiring closes the window.</summary>
    public event EventHandler? CancelRequested;

    [ObservableProperty] private bool _hasNoFrames = true;
    [ObservableProperty] private bool _hasFrames;
    [ObservableProperty] private bool _hasDark;
    [ObservableProperty] private bool _hasNoDark = true;
    [ObservableProperty] private string? _darkPath;
    [ObservableProperty] private string _darkName = "";

    /// <summary>The set dark row's Z8·ISO·快门 tag (mockup: "Z8 · ISO 400 · 1/8000s").</summary>
    [ObservableProperty] private string _darkInfoText = "";

    [ObservableProperty] private ValidationState _state = ValidationState.Empty;

    /// <summary>Drag-drop AND click-to-pick entry: keeps only .NEF (case-insensitive — the
    /// Z 8 writes NEF, .nrw is rejected), dedupes case-insensitively, natural-sorts by file
    /// name and REPLACES the whole frame set (carried ruling: a drop is 补齐/替换 by
    /// re-dropping the full set). A drop containing no NEF at all is ignored, so a stray
    /// non-raw drop cannot wipe a full set.</summary>
    public void HandleFilesDropped(IEnumerable<string> paths)
    {
        List<string> frames = paths
            .Where(p => string.Equals(Path.GetExtension(p), ".nef", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(Path.GetFileName, NaturalFileNameComparer.Instance)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (frames.Count == 0)
            return;
        ReplaceFrames(frames);
        InvalidateAndSchedule();
    }

    /// <summary>Main-window drop prefill (post-release fix 2): the dialog opens with these
    /// files already loaded as chips — the exact HandleFilesDropped path (dedupe, .nef
    /// filter, natural sort, validation kickoff), so an 8+-file drop shows the same
    /// 帧数不符 issue as an in-dialog drop. The dark frame is deliberately NOT picked —
    /// it stays a user decision on the dialog's own 黑场 button.</summary>
    public void PreloadFiles(IEnumerable<string> paths) => HandleFilesDropped(paths);

    /// <summary>Dark-frame picker completion; the black-frame-equals-a-frame rule is
    /// enforced in the validation pass (Task 5 carried finding). Task 14: the selection
    /// is remembered — load-modify-save persists LastDarkPath (only that field changes)
    /// so the next dialog's picker starts in its folder.</summary>
    public void SelectDark(string path)
    {
        DarkPath = path;
        DarkName = Path.GetFileName(path);
        HasDark = true;
        HasNoDark = false;
        if (_settings is { } settings)
            settings.Save(settings.Load() with { LastDarkPath = path });
        InvalidateAndSchedule();
    }

    /// <summary>Task 14: the dark picker's start folder — the persisted LastDarkPath's
    /// dir (only the folder is reused, never the file itself).</summary>
    public string? LastDarkDir =>
        _settings is not null && _settings.Current.LastDarkPath is { Length: > 0 } last
            ? Path.GetDirectoryName(last)
            : null;

    /// <summary>Click on a chip: 与现任互换 — the new ★基准 takes slot 0, the old one its
    /// slot, everything else keeps order. Slot 0 is already the reference / not found: no-op.</summary>
    [RelayCommand]
    private void SetReference(FrameChipVm chip)
    {
        int i = Frames.IndexOf(chip);
        if (i <= 0)
            return;
        FrameChipVm old = Frames[0];
        Frames[0] = chip;
        Frames[i] = old;
        old.IsReference = false;
        chip.IsReference = true;
        InvalidateAndSchedule();
    }

    /// <summary>× on a chip: remove it; slot 0 (whatever it now is) is the reference.</summary>
    [RelayCommand]
    private void RemoveChip(FrameChipVm chip)
    {
        if (!Frames.Remove(chip))
            return;
        if (Frames.Count > 0)
            Frames[0].IsReference = true;
        InvalidateAndSchedule();
    }

    /// <summary>创建任务组: emits the group (★基准 first, rest in display order) with the
    /// validation that gated it; the app wiring turns it into a Ready card and closes.</summary>
    [RelayCommand(CanExecute = nameof(CanCreate))]
    private void Create()
    {
        if (_lastValidation is not { CanRun: true } || DarkPath is not { } dark)
            return;
        var group = new TaskGroup(Frames.Select(f => f.Path).ToArray(), dark);
        CreateCompleted?.Invoke(this, new CreatedTaskGroup(group, _lastValidation));
    }

    private bool CanCreate() => State.CanRun;

    [RelayCommand]
    private void Cancel() => CancelRequested?.Invoke(this, EventArgs.Empty);

    private void ReplaceFrames(List<string> frames)
    {
        Frames.Clear();
        for (int i = 0; i < frames.Count; i++)
        {
            var chip = new FrameChipVm(frames[i], SetReferenceCommand, RemoveChipCommand)
            {
                IsReference = i == 0,
            };
            Frames.Add(chip);
        }
    }

    /// <summary>Every mutation blanks the validation line immediately (创建任务组 disabled) so
    /// a stale ✓ can never enable Create against a changed file set; the debounced pass
    /// re-renders ~300 ms later.</summary>
    private void InvalidateAndSchedule()
    {
        State = ValidationState.Empty;
        DarkInfoText = "";
        ScheduleValidation();
    }

    private void ScheduleValidation()
    {
        CancellationTokenSource? old = _validateCts;
        _validateCts = new CancellationTokenSource();
        old?.Cancel();
        old?.Dispose();
        CancellationToken token = _validateCts.Token;
        int generation = ++_generation;
        _validationTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_debounce, token).ConfigureAwait(false);
                ValidationSnapshot snapshot = ValidateNow(generation);
                _post(() => ApplySnapshot(snapshot));
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                // Dialog robustness: an unexpected failure must not crash or hang the line.
                var failure = new ValidationSnapshot(generation, false,
                    $"校验时发生错误：{ex.Message}", "", Array.Empty<string>(),
                    "", new ValidationResult(false, Array.Empty<ValidationIssue>(), null));
                _post(() => ApplySnapshot(failure));
            }
        });
    }

    /// <summary>Background pass (no UI state touched): reads every file through the injectable
    /// reader, runs TaskGroupValidator, appends the dialog-local dark-equals-frame rule and
    /// renders the Chinese texts of mockup v3.</summary>
    private ValidationSnapshot ValidateNow(int generation)
    {
        // 阶段一空面板: no files at all → no validation line.
        if (Frames.Count == 0 && DarkPath == null)
            return new ValidationSnapshot(generation, false, "", "", Array.Empty<string>(),
                "", new ValidationResult(false, Array.Empty<ValidationIssue>(), null));

        var group = new TaskGroup(Frames.Select(f => f.Path).ToArray(), DarkPath ?? "");
        var passMeta = new Dictionary<string, RawMetadata>(StringComparer.OrdinalIgnoreCase);
        var passErrors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Func<string, RawMetadata> read = path =>
        {
            try
            {
                RawMetadata meta = _read(path);
                passMeta[path] = meta;
                return meta;
            }
            catch (RawReadException)
            {
                passErrors.Add(path);
                throw;
            }
        };

        ValidationResult result = TaskGroupValidator.Validate(group, read);

        var issues = new List<string>(result.Issues.Select(i => i.MessageZh));
        // Task 5 carried finding: Core's validator does not know the chip set, so the dialog
        // enforces the dark picker contract itself.
        if (DarkPath is { } dark && Frames.Any(f => string.Equals(f.Path, dark, StringComparison.OrdinalIgnoreCase)))
            issues.Add("黑场不能与帧文件相同");

        bool canRun = result.CanRun && issues.Count == 0;
        string ptcInfo = "";
        if (canRun && result.Ptc is { } ptc
            && Frames.Count == TaskGroupValidator.RequiredFrames
            && passMeta.TryGetValue(Frames[0].Path, out RawMetadata? reference))
        {
            ptcInfo = $"✓ {Frames.Count + 1} 个文件机型/ISO 一致 · PTC 匹配 Nikon Z8·ISO{Fmt(reference.Iso)}" +
                      $" → 满阱 {ptc.FullwellE.ToString("N0", CultureInfo.InvariantCulture)} e⁻（{(ptc.Exact ? "表值" : "拟合")}）";
        }

        string darkInfo = "";
        if (DarkPath != null && passMeta.TryGetValue(DarkPath, out RawMetadata? darkMeta))
        {
            var parts = new List<string>
            {
                TaskGroupValidator.NormalizeModel(darkMeta.Make, darkMeta.Model).ToUpperInvariant(),
                $"ISO {Fmt(darkMeta.Iso)}",
            };
            if (darkMeta.ExposureTime > 0)
                parts.Add(Shutter(darkMeta.ExposureTime));
            darkInfo = string.Join(" · ", parts);
        }

        return new ValidationSnapshot(generation, canRun, string.Join("\n", issues), ptcInfo,
            passErrors, darkInfo, result);
    }

    /// <summary>Applies a settled pass — unless a newer mutation superseded it (generation
    /// guard: a delayed marshal from an older pass must not resurrect a stale ✓).</summary>
    private void ApplySnapshot(ValidationSnapshot snapshot)
    {
        if (snapshot.Generation != _generation)
            return;
        _lastValidation = snapshot.Result;
        foreach (FrameChipVm chip in Frames)
            chip.IsError = snapshot.ErrorPaths.Contains(chip.Path);
        DarkInfoText = snapshot.DarkInfoText;
        State = new ValidationState(snapshot.CanRun, snapshot.IssuesText, snapshot.PtcInfoText);
    }

    private void OnFramesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        HasNoFrames = Frames.Count == 0;
        HasFrames = Frames.Count > 0;
    }

    partial void OnStateChanged(ValidationState value) => CreateCommand.NotifyCanExecuteChanged();

    private static string Fmt(double iso) => iso.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Camera-style shutter text: 1/8000s, 1/100s, 2s.</summary>
    private static string Shutter(double seconds) =>
        seconds >= 1
            ? $"{seconds.ToString(seconds == Math.Truncate(seconds) ? "0" : "0.##", CultureInfo.InvariantCulture)}s"
            : $"1/{Math.Round(1 / seconds):0}s";

    /// <summary>Background validation pass result, carried from the threadpool to the apply site.</summary>
    private sealed record ValidationSnapshot(
        int Generation,
        bool CanRun,
        string IssuesText,
        string PtcInfoText,
        IReadOnlyCollection<string> ErrorPaths,
        string DarkInfoText,
        ValidationResult Result);
}

/// <summary>Natural (digit-run aware) ascending file-name order — the ordering
/// <see cref="TaskGroup"/>'s contract assigns to this dialog: "DSC_2.NEF" sorts before
/// "DSC_10.NEF" (plain ordinal would put "10" first). Digit runs compare by numeric
/// value, non-digit chunks case-insensitively; ties fall back to the full path.</summary>
internal sealed class NaturalFileNameComparer : IComparer<string?>
{
    public static readonly NaturalFileNameComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x == null) return -1;
        if (y == null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                int xi = DigitRunEnd(x, i);
                int yj = DigitRunEnd(y, j);
                string dx = x[i..xi].TrimStart('0');
                string dy = y[j..yj].TrimStart('0');
                int c = dx.Length != dy.Length ? dx.Length.CompareTo(dy.Length) : string.CompareOrdinal(dx, dy);
                if (c != 0) return c;
                i = xi;
                j = yj;
            }
            else
            {
                int c = char.ToLowerInvariant(x[i]).CompareTo(char.ToLowerInvariant(y[j]));
                if (c != 0) return c;
                i++;
                j++;
            }
        }
        return (x.Length - i).CompareTo(y.Length - j);   // the exhausted side sorts first
    }

    private static int DigitRunEnd(string s, int i)
    {
        while (i < s.Length && char.IsDigit(s[i]))
            i++;
        return i;
    }
}
