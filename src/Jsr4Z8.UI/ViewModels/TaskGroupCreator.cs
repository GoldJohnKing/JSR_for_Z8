using Jsr4Z8.Core;
using Jsr4Z8.Core.Model;

namespace Jsr4Z8.UI.ViewModels;

/// <summary>Creation result: the group (基准帧 first), the validation that came with
/// it, and the per-path metadata the creation pass already read (readable frames only
/// — an unreadable path is simply absent; case-insensitive keys) — so the card VM can
/// seed its caches and build hover texts without re-reading the files.</summary>
public sealed record CreatedTaskGroup(
    TaskGroup Group,
    ValidationResult Validation,
    IReadOnlyDictionary<string, RawMetadata> Metadata);

/// <summary>
/// Inline group creation: dropping
/// or picking NEF files creates a task-group card directly in the main window. One
/// call = one candidate group, built in this order: .NEF filter (case-insensitive —
/// the Z 8 writes NEF, .nrw is rejected) → OrdinalIgnoreCase dedupe → natural sort →
/// a single memoized metadata read pass → reference-frame selection →
/// <see cref="TaskGroupValidator.Validate"/> through the same memoized reader (no
/// double read). A payload with no NEF at all returns null — the caller no-ops, so a
/// stray non-raw drop can never create an empty card. The heavy work runs on a
/// background thread and each call carries its own memoized reader, so concurrent
/// calls never share caches.
/// <para>CreateAsync is the seeded/test creation path; the main drop flow creates
/// cards immediately (validation:null) and the card validates itself via
/// <see cref="TaskGroupViewModel.ValidateAsync"/>. This class is the home of the
/// shared <see cref="PickReference"/>/<see cref="ExposureEqual"/> rules the card VM
/// re-runs.</para>
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

        string reference = PickReference(frames, memoized);
        TaskGroup group = new(frames
            .Where(p => !string.Equals(p, reference, StringComparison.OrdinalIgnoreCase))
            .Prepend(reference)
            .ToList());
        return new CreatedTaskGroup(group, TaskGroupValidator.Validate(group, memoized), meta);
    }

    /// <summary>Reference-frame rule (曝光包围), extracted so the card VM re-runs it on
    /// every add/remove: with every shutter equal to the natural-first frame's
    /// (tolerance below; ExposureTime 0 = EXIF absent carries no bracketing signal and
    /// counts as equal), the natural order stands and the first file is the 基准帧.
    /// Otherwise the frames order by (ExposureTime, natural file name) ascending and
    /// index Count/2 (7 → 3, the middle exposure) becomes the reference. An unreadable
    /// natural-first frame (no readable base) leaves the order alone. <paramref
    /// name="paths"/> must be non-empty and in natural display order — both callers
    /// (CreateAsync, the card VM's recompute) guarantee it, since no group exists
    /// without frames and the display order is maintained natural.</summary>
    public static string PickReference(IReadOnlyList<string> paths, Func<string, RawMetadata> read)
    {
        RawMetadata? first = TryRead(paths[0], read);
        if (first is null || first.ExposureTime == 0)
            return paths[0];

        double baseExposure = first.ExposureTime;
        var exposures = new double[paths.Count];
        bool bracketed = false;
        for (int i = 0; i < paths.Count; i++)
        {
            exposures[i] = TryRead(paths[i], read)?.ExposureTime ?? 0;
            if (!ExposureEqual(exposures[i], baseExposure))
                bracketed = true;
        }
        if (!bracketed)
            return paths[0];

        return paths
            .Select((path, i) => (Path: path, Exposure: exposures[i]))
            .OrderBy(t => t.Exposure)
            .ThenBy(t => Path.GetFileName(t.Path), NaturalFileNameComparer.Instance)
            .ElementAt(paths.Count / 2)
            .Path;
    }

    /// <summary>A read that fails (unreadable file) reads back as no bracketing signal:
    /// equal to the base, exposure 0 in the ascending order.</summary>
    private static RawMetadata? TryRead(string path, Func<string, RawMetadata> read)
    {
        try
        {
            return read(path);
        }
        catch (RawReadException)
        {
            return null;
        }
    }

    /// <summary>Exposure equality at the reference-pick tolerance (relative, either
    /// side 0 = EXIF absent carries no bracketing signal and counts as equal) —
    /// public because the card VM's <see cref="TaskGroupViewModel.IsBracketed"/>
    /// re-runs the same rule to decide ★ vs ±EV tag.</summary>
    public static bool ExposureEqual(double a, double b) =>
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
