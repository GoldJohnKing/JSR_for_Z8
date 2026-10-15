using System.IO;
using Jsr4Z8.Core;
using Jsr4Z8.Core.Model;

namespace Jsr4Z8.UI.ViewModels;

/// <summary>Creation result: the group (基准帧 first) plus the validation that came with
/// it, so MainViewModel.AddValidated creates a card without re-reading the files.</summary>
public sealed record CreatedTaskGroup(TaskGroup Group, ValidationResult Validation);

/// <summary>
/// Inline group creation (Task 4, replacing the deleted 新建任务组 dialog): dropping
/// or picking NEF files creates a task-group card directly in the main window. One
/// call = one candidate group, built in this order: .NEF filter (case-insensitive —
/// the Z 8 writes NEF, .nrw is rejected) → OrdinalIgnoreCase dedupe → natural sort →
/// a single memoized metadata read pass → reference-frame selection →
/// <see cref="TaskGroupValidator.Validate"/> through the same memoized reader (no
/// double read). A payload with no NEF at all returns null — the caller no-ops, so a
/// stray non-raw drop can never create an empty card. The heavy work runs on a
/// background thread and each call carries its own memoized reader, so concurrent
/// calls never share caches.
/// </summary>
public static class TaskGroupCreator
{
    /// <summary>Exposure equality tolerance, relative to the natural-first frame's
    /// shutter (same shape as the validator's ISO rule).</summary>
    private const double ExposureRelativeTolerance = 1e-6;

    public static Task<CreatedTaskGroup?> CreateAsync(IReadOnlyList<string> paths,
        Func<string, RawMetadata>? read = null)
    {
        List<string> frames = paths
            .Where(p => string.Equals(Path.GetExtension(p), ".nef", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(Path.GetFileName, NaturalFileNameComparer.Instance)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (frames.Count == 0)
            return Task.FromResult<CreatedTaskGroup?>(null);

        Func<string, RawMetadata> source = read ?? RawMetadataReader.Read;
        return Task.Run<CreatedTaskGroup?>(() => Create(frames, source));
    }

    /// <summary>Background pass: one memoized reader for the whole call (successes are
    /// cached — a failed read is retried so the validator can turn it into an Unreadable
    /// issue naming the file), reference pick, then validation through that reader.</summary>
    private static CreatedTaskGroup Create(List<string> frames, Func<string, RawMetadata> source)
    {
        var meta = new Dictionary<string, RawMetadata>(StringComparer.OrdinalIgnoreCase);
        Func<string, RawMetadata> memoized = path =>
        {
            if (meta.TryGetValue(path, out RawMetadata? cached))
                return cached;
            RawMetadata m = source(path);
            meta[path] = m;
            return m;
        };

        // One read pass feeds the reference pick; a file absent from the cache
        // afterwards is unreadable (it keeps its natural slot — the validator turns
        // the re-thrown RawReadException into an Unreadable issue naming it).
        foreach (string path in frames)
        {
            try
            {
                _ = memoized(path);
            }
            catch (RawReadException)
            {
            }
        }

        TaskGroup group = new(OrderForReference(frames, meta));
        return new CreatedTaskGroup(group, TaskGroupValidator.Validate(group, memoized));
    }

    /// <summary>Reference-frame rule (曝光包围): with every shutter equal to the
    /// natural-first frame's (tolerance above; ExposureTime 0 = EXIF absent carries no
    /// bracketing signal and counts as equal), the natural order stands and the first
    /// file is the 基准帧. Otherwise the frames order by (ExposureTime, natural file
    /// name) ascending and index Count/2 (7 → 3, the middle exposure) becomes the
    /// reference at FramePaths[0]; the remaining frames keep natural order behind it.
    /// An unreadable natural-first frame (no readable base) leaves the order alone.</summary>
    private static IReadOnlyList<string> OrderForReference(List<string> frames,
        IReadOnlyDictionary<string, RawMetadata> meta)
    {
        if (!meta.TryGetValue(frames[0], out RawMetadata? first) || first.ExposureTime == 0)
            return frames;
        double baseExposure = first.ExposureTime;
        if (frames.All(p => !meta.TryGetValue(p, out RawMetadata? m) ||
                            ExposureEqual(m.ExposureTime, baseExposure)))
            return frames;

        string reference = frames
            .OrderBy(p => meta.TryGetValue(p, out RawMetadata? m) ? m.ExposureTime : 0)
            .ThenBy(Path.GetFileName, NaturalFileNameComparer.Instance)
            .ElementAt(frames.Count / 2);
        return frames.Where(p => !string.Equals(p, reference, StringComparison.OrdinalIgnoreCase))
            .Prepend(reference)
            .ToList();
    }

    private static bool ExposureEqual(double a, double b) =>
        a == 0 || b == 0 ||
        Math.Abs(a - b) <= ExposureRelativeTolerance * Math.Max(1.0, Math.Abs(b));
}

/// <summary>Natural (digit-run aware) ascending file-name order — the ordering
/// <see cref="TaskGroup"/>'s contract assigns to a group's frames: "DSC_2.NEF" sorts
/// before "DSC_10.NEF" (plain ordinal would put "10" first). Digit runs compare by
/// numeric value, non-digit chunks case-insensitively; ties fall back to the full path.</summary>
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
