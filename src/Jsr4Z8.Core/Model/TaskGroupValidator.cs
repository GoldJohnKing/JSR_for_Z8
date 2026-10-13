using System.Globalization;
using System.Text;
using Jsr4Z8.Core.Ptc;

namespace Jsr4Z8.Core.Model;

/// <summary>Kind of an 8-file consistency violation in a <see cref="TaskGroup"/>.</summary>
public enum IssueKind
{
    /// <summary>Make is not Nikon at all (e.g. a Canon body in the group).</summary>
    CameraMismatch,

    /// <summary>A Nikon body, but not a Z 8 (e.g. "NIKON Z 6_3").</summary>
    NotZ8,

    /// <summary>ISO differs from the reference frame beyond the 1e-6 relative tolerance.</summary>
    IsoMismatch,

    /// <summary>A required file raised RawReadException, or the dark frame was never set.</summary>
    Unreadable,

    /// <summary>ISO is NaN or ±∞ — rejected here so it can never reach PtcTable.Lookup
    /// (Task 4 carried finding: Lookup(NaN) would return a NaN fit value).</summary>
    NotFiniteIso,

    /// <summary>Reference-frame ISO is outside the PTC table bounds [50, 102400].</summary>
    PtcUnavailable,

    /// <summary>Frame count is not exactly 7.</summary>
    FrameCount,
}

public sealed record ValidationIssue(string File, IssueKind Kind, string MessageZh);

/// <summary>Ptc is non-null if and only if CanRun is true (the exact/fit match for the base ISO).</summary>
public sealed record ValidationResult(bool CanRun, IReadOnlyList<ValidationIssue> Issues, PtcMatch? Ptc);

/// <summary>
/// Validates the 8 files of a <see cref="TaskGroup"/> (7 frames + dark) for the
/// one-camera/one-ISO contract the JSR pipeline assumes: exactly 7 frames; every
/// file a Nikon Z 8 after model normalization; ISO pairwise-identical to the
/// reference frame within 1e-6 relative; the dark frame set; and the base ISO
/// inside the PTC table. CanRun=false always comes with at least one issue whose
/// Chinese message names the offending file.
/// </summary>
public static class TaskGroupValidator
{
    private const int RequiredFrames = 7;
    private const double IsoRelativeTolerance = 1e-6;
    private const string NikonPrefix = "nikon";
    private const string Z8Canonical = "z8"; // NormalizeModel("NIKON CORPORATION", "NIKON Z 8")

    /// <summary>Production entry point: reads every file with the real LibRaw-backed reader.</summary>
    public static ValidationResult Validate(TaskGroup g) => Validate(g, RawMetadataReader.Read);

    /// <summary>Test/UI seam: <paramref name="read"/> may throw RawReadException per file,
    /// which becomes an Unreadable issue naming that file instead of aborting the group.</summary>
    public static ValidationResult Validate(TaskGroup g, Func<string, RawMetadata> read)
    {
        var issues = new List<ValidationIssue>();

        if (g.FramePaths.Count != RequiredFrames)
            issues.Add(new ValidationIssue("", IssueKind.FrameCount,
                $"帧数不符：任务组需要 {RequiredFrames} 帧，实际 {g.FramePaths.Count} 帧"));

        // A required input file that was never set is treated as unreadable.
        bool hasDark = !string.IsNullOrWhiteSpace(g.DarkPath);
        if (!hasDark)
            issues.Add(new ValidationIssue("", IssueKind.Unreadable,
                "黑场文件未设置（任务组需要 1 张黑场帧）"));

        var paths = new List<string>(g.FramePaths);
        if (hasDark)
            paths.Add(g.DarkPath);

        var files = new List<(string Path, RawMetadata Meta)>();
        foreach (string path in paths)
        {
            try
            {
                files.Add((path, read(path)));
            }
            catch (RawReadException ex)
            {
                issues.Add(new ValidationIssue(path, IssueKind.Unreadable,
                    $"{Path.GetFileName(path)} 无法读取：{ex.Message}"));
            }
        }

        foreach ((string path, RawMetadata m) in files)
        {
            string name = Path.GetFileName(path);
            if (!Collapse(m.Make).StartsWith(NikonPrefix, StringComparison.Ordinal))
                issues.Add(new ValidationIssue(path, IssueKind.CameraMismatch,
                    $"{name} 的相机为 {m.Make} {m.Model}，仅支持 Nikon Z8"));
            else if (NormalizeModel(m.Make, m.Model) != Z8Canonical)
                issues.Add(new ValidationIssue(path, IssueKind.NotZ8,
                    $"{name} 的机型为 {m.Model}，仅支持 Nikon Z8"));

            if (!double.IsFinite(m.Iso))
                issues.Add(new ValidationIssue(path, IssueKind.NotFiniteIso,
                    $"{name} 的 ISO 值无效（非有限数值）"));
        }

        PtcMatch? ptc = null;
        // The reference frame is the comparison base for ISO and PTC. If it was never
        // read (unreadable), those reference-relative checks are simply skipped — the
        // Unreadable issue already blocks the group. Found via explicit loop because
        // FirstOrDefault on a value tuple returns a (null-bearing) default tuple that
        // nullability patterns cannot distinguish from a real match.
        RawMetadata? referenceMeta = null;
        string? referencePath = g.FramePaths.Count > 0 ? g.FramePaths[0] : null;
        if (referencePath != null)
        {
            foreach ((string path, RawMetadata m) in files)
            {
                if (path == referencePath)
                {
                    referenceMeta = m;
                    break;
                }
            }
        }

        if (referenceMeta != null)
        {
            double baseIso = referenceMeta.Iso;
            if (double.IsFinite(baseIso))
            {
                foreach ((string path, RawMetadata m) in files)
                {
                    if (!double.IsFinite(m.Iso))
                        continue; // already flagged NotFiniteIso above
                    if (Math.Abs(m.Iso - baseIso) > IsoRelativeTolerance * Math.Max(1.0, Math.Abs(baseIso)))
                        issues.Add(new ValidationIssue(path, IssueKind.IsoMismatch,
                            $"{Path.GetFileName(path)} 的 ISO({Fmt(m.Iso)}) 与基准帧({Fmt(baseIso)}) 不一致"));
                }

                ptc = PtcTable.LoadDefault().Lookup(baseIso);
                if (ptc == null)
                    issues.Add(new ValidationIssue(referencePath, IssueKind.PtcUnavailable,
                        $"基准帧 {Path.GetFileName(referencePath)} 的 ISO {Fmt(baseIso)} 超出 PTC 表范围 [50, 102400]，无法估算满阱"));
            }
        }

        return new ValidationResult(issues.Count == 0, issues, issues.Count == 0 ? ptc : null);
    }

    /// <summary>
    /// Canonical model form for camera identity: trim, case-fold, collapse internal
    /// whitespace, strip the vendor prefix (the Make's first word, e.g. "NIKON Z 8" → "Z 8"),
    /// then drop all remaining spaces so "Z 8" and "Z8" compare equal ("z8").
    /// </summary>
    public static string NormalizeModel(string make, string model)
    {
        string collapsed = Collapse(model);
        string vendor = Collapse(make);
        int space = vendor.IndexOf(' ');
        string firstWord = space > 0 ? vendor[..space] : vendor;
        if (firstWord.Length > 0 && collapsed.StartsWith(firstWord + " ", StringComparison.Ordinal))
            collapsed = collapsed[firstWord.Length..].Trim();
        return collapsed.Replace(" ", "");
    }

    /// <summary>Lowercases, trims and collapses all whitespace runs to single spaces.</summary>
    private static string Collapse(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool pendingSpace = false;
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>ISO for Chinese messages, e.g. 3200 → "3200", 51.5 → "51.5".</summary>
    private static string Fmt(double iso) => iso.ToString("0.###", CultureInfo.InvariantCulture);
}
