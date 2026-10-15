using System.Globalization;
using System.Text;
using Jsr4Z8.Core.Dark;
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

    /// <summary>A required file raised RawReadException (frames or a user-set dark file).</summary>
    Unreadable,

    /// <summary>ISO is NaN or ±∞ — rejected here so it can never reach PtcTable.Lookup
    /// (Task 4 carried finding: Lookup(NaN) would return a NaN fit value).</summary>
    NotFiniteIso,

    /// <summary>Bit depth (TIFF BitsPerSample) differs from the reference frame.</summary>
    BitDepthMismatch,

    /// <summary>RAW compression code (TIFF Compression) differs from the reference frame.</summary>
    CompressionMismatch,

    /// <summary>Crop window (raw sensor dimensions) differs from the reference frame.</summary>
    CropMismatch,

    /// <summary>The capture format is unsupported: readout outside the profile the
    /// preset dark table covers (14bit · lossless/HE/HE*), or an off-table ISO —
    /// there is no fallback; the user must reshoot in a supported format.</summary>
    CaptureFormatUnsupported,

    /// <summary>Reference-frame ISO is outside the PTC table bounds [50, 102400].</summary>
    PtcUnavailable,

    /// <summary>Frame count is not exactly 7.</summary>
    FrameCount,
}

public sealed record ValidationIssue(string File, IssueKind Kind, string MessageZh);

/// <summary>Ptc is non-null if and only if CanRun is true (the exact/fit match for the base ISO).
/// Dark is non-null if and only if CanRun is true — the preset row the request
/// builder sends as the backend's manual calibration pair.</summary>
public sealed record ValidationResult(
    bool CanRun, IReadOnlyList<ValidationIssue> Issues, PtcMatch? Ptc, DarkMatch? Dark = null);

/// <summary>
/// Validates a <see cref="TaskGroup"/> (7 frames + optional dark) for the
/// five-dimension capture contract: 机型/ISO/位深/压缩/画幅 must be pairwise
/// identical across the 7 frames (model per-file as Nikon Z8; the rest vs the
/// reference frame). Table matching: PTC is a per-pixel parameter keyed on
/// model+ISO only (readout does not change the normalized shot slope); the dark
/// calibration is always preset-matched on (compression, ISO) and only exists for
/// the measured profiles (14bit · NEFCompression∈{3,13,14}) — crop is not a
/// matching dimension (read noise is crop-independent, DX vs FX ≤1% at high ISO),
/// so any crop window takes the FX-measured rows. Anything outside is an
/// unsupported capture format.
/// CanRun=false always comes with at least one issue whose Chinese message
/// names the offending file.
/// </summary>
public static class TaskGroupValidator
{
    /// <summary>The exact frame count a <see cref="TaskGroup"/> requires (7 frames);
    /// the dialog's ✓ line gates on it too.</summary>
    public const int RequiredFrames = 7;
    private const double IsoRelativeTolerance = 1e-6;
    private const string NikonPrefix = "nikon";
    private const string Z8Canonical = "z8"; // NormalizeModel("NIKON CORPORATION", "NIKON Z 8")

    /// <summary>The readout profile the preset dark table covers: TIFF BitsPerSample
    /// 14, NEF family compression 34713 AND — the actual discriminator — MakerNote
    /// NEFCompression 3 (lossless), 13 (High Efficiency) or 14 (High Efficiency*),
    /// each carrying its own measured rows (+3..7% quantization noise on the lossy
    /// codecs). The TIFF compression code alone cannot gate: every Z8 NEF —
    /// lossless, HE and HE* — carries 34713.</summary>
    private const int SupportedBitsPerSample = 14;
    private const int SupportedCompression = 34713;
    private static readonly int[] SupportedNefCompressions = { 3, 13, 14 };


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

        // Dark calibration is preset-matched from the reference frame's
        // (compression, ISO).
        DarkMatch? dark = null;

        var files = new List<(string Path, RawMetadata Meta)>();
        foreach (string path in g.FramePaths)
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
                // Five-dimension capture consistency: every file —
                // the 7 frames AND a user-supplied dark — must match the reference
                // frame on ISO, bit depth, compression and crop window (model
                // identity is checked per-file above). Any mismatch blocks the run.
                foreach ((string path, RawMetadata m) in files)
                {
                    if (!double.IsFinite(m.Iso))
                        continue; // already flagged NotFiniteIso above
                    if (Math.Abs(m.Iso - baseIso) > IsoRelativeTolerance * Math.Max(1.0, Math.Abs(baseIso)))
                        issues.Add(new ValidationIssue(path, IssueKind.IsoMismatch,
                            $"{Path.GetFileName(path)} 的 ISO({Fmt(m.Iso)}) 与基准帧({Fmt(baseIso)}) 不一致"));
                    if (m.BitsPerSample != referenceMeta.BitsPerSample)
                        issues.Add(new ValidationIssue(path, IssueKind.BitDepthMismatch,
                            $"{Path.GetFileName(path)} 的位深({m.BitsPerSample}bit) 与基准帧({referenceMeta.BitsPerSample}bit) 不一致"));
                    if (m.NefCompression != referenceMeta.NefCompression)
                        issues.Add(new ValidationIssue(path, IssueKind.CompressionMismatch,
                            $"{Path.GetFileName(path)} 的 RAW 压缩类型({NefCompressionName(m.NefCompression)}) 与基准帧({NefCompressionName(referenceMeta.NefCompression)}) 不一致"));
                    if (m.Width != referenceMeta.Width || m.Height != referenceMeta.Height)
                        issues.Add(new ValidationIssue(path, IssueKind.CropMismatch,
                            $"{Path.GetFileName(path)} 的画幅({m.Width}×{m.Height}) 与基准帧({referenceMeta.Width}×{referenceMeta.Height}) 不一致"));
                }

                // PTC is a per-pixel parameter: it varies with model and ISO only —
                // encoding/crop do not change the normalized shot slope.
                ptc = PtcTable.LoadDefault().Lookup(baseIso);
                if (ptc == null)
                    issues.Add(new ValidationIssue(referencePath!, IssueKind.PtcUnavailable,
                        $"基准帧 {Path.GetFileName(referencePath)} 的 ISO {Fmt(baseIso)} 超出 PTC 表范围 {PtcRange()}，无法估算满阱"));

                // The preset dark is a readout measurement: it only exists for the
                // profiles the table was derived on — anything outside is an
                // unsupported capture format.
                bool profileOk = referenceMeta.BitsPerSample == SupportedBitsPerSample
                              && referenceMeta.Compression == SupportedCompression
                              && SupportedNefCompressions.Contains(referenceMeta.NefCompression);
                if (!profileOk)
                {
                    issues.Add(new ValidationIssue(referencePath!, IssueKind.CaptureFormatUnsupported,
                        $"基准帧 {Path.GetFileName(referencePath)} 的拍摄格式不支持（{referenceMeta.BitsPerSample}bit · " +
                        $"{NefCompressionName(referenceMeta.NefCompression)}）；" +
                        $"本软件仅支持 14bit · 无损/HE/HE* 压缩的 NEF，请改用受支持的拍摄格式"));
                }
                else
                {
                    dark = DarkTable.LoadDefault().Lookup(baseIso, referenceMeta.NefCompression);
                    if (dark is null)
                        issues.Add(new ValidationIssue(referencePath!, IssueKind.CaptureFormatUnsupported,
                            $"基准帧 ISO {Fmt(baseIso)} 不在预置黑场参数表覆盖范围（无损/HE/HE* 各覆盖 64–102400 的 31 档），此类格式不支持"));
                }
            }
        }

        return new ValidationResult(issues.Count == 0, issues, issues.Count == 0 ? ptc : null,
            issues.Count == 0 ? dark : null);
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

    /// <summary>Friendly Chinese name of a Nikon NEFCompression code for issue
    /// messages (3 无损 / 13 HE / 14 HE*; anything else keeps its raw code).</summary>
    private static string NefCompressionName(int code) => code switch
    {
        3 => "无损压缩(3)",
        13 => "高效率 HE(13)",
        14 => "高效率 HE*(14)",
        _ => $"压缩类型 {code}",
    };

    /// <summary>PTC table ISO bounds ("[50, 102400]"), derived from the embedded rows
    /// so the message can never drift from the actual table.</summary>
    private static string PtcRange()
    {
        IReadOnlyList<PtcRow> rows = PtcTable.LoadDefault().Rows;
        return $"[{Fmt(rows[0].Iso)}, {Fmt(rows[^1].Iso)}]";
    }
}
