using System.Globalization;
using Jsr4Z8.Core.Dark;
using Jsr4Z8.Core.Model;
using Jsr4Z8.Core.Ptc;

namespace Jsr4Z8.Core.Backend;

/// <summary>
/// Builds the jsr_backend.exe argv (the direct-invoke wire contract, captured in
/// docs/superpowers/notes/2026-10-15-jsr-backend-argv-probe.md §2, byte-identical on
/// payload 2.3.27 §5): the caller's own request
/// semantics (--output-dir/--out-prefix, one --raw-frame per frame in group order,
/// manual RN/BL from the validator's preset dark row, fullwell from the validated
/// PTC match) plus the Bridge-filled defaults replayed as observed constants
/// (--dng-compression auto, --developer-mode, --out-png off, --out-format jpeg,
/// --jpeg-quality 95, --camera-profile-source libraw). No request.json and no
/// config dir — the engine needs neither (probe §7).
/// </summary>
public static class BackendRequest
{
    /// <summary>
    /// fullwell = ptc.FullwellE rounded to 1 decimal (AwayFromZero; probe: 1102.244 →
    /// 1102.2).
    /// <paramref name="dark"/> is the validator-resolved preset row (required);
    /// RN/BL pass through as-is in the invariant shortest form (1008, not 1008.0).
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(TaskGroup g, PtcMatch ptc, DarkMatch dark, string outputDir, string prefix)
    {
        ArgumentNullException.ThrowIfNull(dark);
        double fullwell = Math.Round(ptc.FullwellE, 1, MidpointRounding.AwayFromZero);

        var arguments = new List<string>
        {
            "--output-dir", outputDir,
            "--out-prefix", prefix,
            "--dng-compression", "auto",
        };
        foreach (string frame in g.FramePaths)
        {
            arguments.Add("--raw-frame");
            arguments.Add(frame);
        }
        arguments.Add("--developer-mode");
        arguments.Add("--read-noise-dn");
        arguments.Add(dark.ReadNoiseDn.ToString(CultureInfo.InvariantCulture));
        arguments.Add("--black-level-dn");
        arguments.Add(dark.BlackDn.ToString(CultureInfo.InvariantCulture));
        arguments.Add("--full-well-electrons");
        arguments.Add(fullwell.ToString(CultureInfo.InvariantCulture));
        arguments.Add("--deghost");
        arguments.Add("off");
        arguments.Add("--out-dng");
        arguments.Add("on");
        arguments.Add("--out-png");
        arguments.Add("off");
        arguments.Add("--out-tiff");
        arguments.Add("off");
        arguments.Add("--out-format");
        arguments.Add("jpeg");
        arguments.Add("--jpeg-quality");
        arguments.Add("95");
        arguments.Add("--camera-profile-source");
        arguments.Add("libraw");
        return arguments;
    }
}
