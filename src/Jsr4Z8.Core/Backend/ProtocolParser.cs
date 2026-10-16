namespace Jsr4Z8.Core.Backend;

/// <summary>Backend progress: 0-100 percent plus a Chinese stage label.</summary>
public sealed record ProgressLine(int Percent, string Stage);

/// <summary>One parsed stdout/stderr line of the jsr_backend bare-line protocol.</summary>
public abstract record ProtocolEvent;

/// <summary>Engine log text; Progress is set when the text is an SRHQ progress line.</summary>
public sealed record Log(string Text, ProgressLine? Progress) : ProtocolEvent;

/// <summary>Any other line; the full text is preserved so nothing the engine printed is lost.</summary>
public sealed record Unrecognized(string Text) : ProtocolEvent;

/// <summary>
/// Parses one line of jsr_backend stdout/stderr — the BARE-LINE protocol of the
/// direct invocation: SRHQ_V12_PROGRESS|percent|stage and
/// SRHQ_ETA_PROGRESS|dng|done|total are the only structured lines; everything else
/// (PTC echo, FRAME lines, Proxy chatter, DONE tail) is ordinary text carried
/// verbatim. The export-phase ETA maps onto the last 5 percent
/// (95 + 5*done/total, "DNG 导出"). A trailing '\r' (CRLF pipe) is stripped.
/// The bare-line protocol has no ASSISTANT_* envelopes; a stray envelope-shaped line
/// parses as Unrecognized like any other text — there is no second grammar.
/// </summary>
public static class ProtocolParser
{
    private const string V12ProgressPrefix = "SRHQ_V12_PROGRESS|";
    private const string EtaProgressPrefix = "SRHQ_ETA_PROGRESS|";
    private const string DngExportStage = "DNG 导出";

    public static ProtocolEvent ParseLine(string raw)
    {
        string line = raw.EndsWith('\r') ? raw[..^1] : raw;

        ProgressLine? progress = TryParseProgress(line);
        return progress is not null ? new Log(line, progress) : new Unrecognized(line);
    }

    /// <summary>Extracts V12 progress (SRHQ_V12_PROGRESS|percent|stage) or export-phase
    /// ETA (SRHQ_ETA_PROGRESS|dng|done|total → 95..100, "DNG 导出"); null when the text is neither.</summary>
    private static ProgressLine? TryParseProgress(string text)
    {
        if (text.StartsWith(V12ProgressPrefix, StringComparison.Ordinal))
        {
            string[] parts = text.Split('|', 3);
            if (parts.Length == 3 && int.TryParse(parts[1], out int percent))
                return new ProgressLine(percent, parts[2]);
            return null;
        }

        if (text.StartsWith(EtaProgressPrefix, StringComparison.Ordinal))
        {
            string[] parts = text.Split('|');
            if (parts.Length == 4 && parts[1] == "dng"
                && int.TryParse(parts[2], out int done)
                && int.TryParse(parts[3], out int total)
                && total > 0)
            {
                return new ProgressLine(95 + 5 * done / total, DngExportStage);
            }
            return null;
        }

        return null;
    }
}
