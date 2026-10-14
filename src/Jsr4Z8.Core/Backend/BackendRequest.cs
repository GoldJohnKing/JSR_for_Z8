using System.Text.Encodings.Web;
using System.Text.Json;
using Jsr4Z8.Core.Dark;
using Jsr4Z8.Core.Model;
using Jsr4Z8.Core.Ptc;

namespace Jsr4Z8.Core.Backend;

/// <summary>
/// Builds the jsr_backend request JSON (protocol fact #5) in the probe-verified
/// manual calibration shape (backend logs "manual DN" / "manual RN(DN)"): the dark
/// key is replaced by manual:true + read_noise + black carrying the validator's
/// preset dark row. The caller must write the returned string as UTF-8 without BOM.
/// </summary>
public static class BackendRequest
{
    // UnsafeRelaxedJsonEscaping keeps non-ASCII (Chinese stage names, paths) readable;
    // the \\ escaping of Windows backslashes is JSON-standard and unavoidable.
    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>fullwell = ptc.FullwellE rounded to 1 decimal (AwayFromZero). No memory
    /// field is emitted at all (post-release directive: 不设预算 — probed to run
    /// state=complete with the key absent, so the backend/driver owns allocation).
    /// <paramref name="dark"/> is the validator-resolved preset row (always required
    /// since the manual dark-file path was removed, 2026-10-15).</summary>
    public static string BuildJson(TaskGroup g, PtcMatch ptc, DarkMatch dark, string outputDir, string prefix)
    {
        ArgumentNullException.ThrowIfNull(dark);
        double fullwell = Math.Round(ptc.FullwellE, 1, MidpointRounding.AwayFromZero);

        var request = new
        {
            paths = g.FramePaths,
            parameters = new
            {
                engine = "stormer7",
                mode = "full",
                output = outputDir,
                prefix,
                manual = true,
                read_noise = dark.ReadNoiseDn,
                black = dark.BlackDn,
                fullwell,
                ptc = "",
                color = "libraw",
                dng = true,
                tiff = false,
                preview = false,
                deghost = false,
            },
        };
        return JsonSerializer.Serialize(request, Options);
    }
}
