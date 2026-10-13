using System.Text.Encodings.Web;
using System.Text.Json;
using Jsr4Z8.Core.Model;
using Jsr4Z8.Core.Ptc;

namespace Jsr4Z8.Core.Backend;

/// <summary>
/// Builds the jsr_backend request JSON (protocol fact #5): the exact field set
/// {"paths":[7 abs paths],"parameters":{engine, mode, output, prefix, manual,
/// dark, fullwell, ptc, color, dng, tiff, preview, deghost, memory}} and nothing
/// else. The caller must write the returned string as UTF-8 without BOM.
/// </summary>
public static class BackendRequest
{
    // UnsafeRelaxedJsonEscaping keeps non-ASCII (Chinese stage names, paths) readable;
    // the \\ escaping of Windows backslashes is JSON-standard and unavoidable.
    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>fullwell = ptc.FullwellE rounded to 1 decimal (AwayFromZero); memory = vramGb in GB.</summary>
    public static string BuildJson(TaskGroup g, PtcMatch ptc, int vramGb, string outputDir, string prefix)
    {
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
                manual = false,
                dark = g.DarkPath,
                fullwell,
                ptc = "",
                color = "libraw",
                dng = true,
                tiff = false,
                preview = false,
                deghost = false,
                memory = vramGb,
            },
        };
        return JsonSerializer.Serialize(request, Options);
    }
}
