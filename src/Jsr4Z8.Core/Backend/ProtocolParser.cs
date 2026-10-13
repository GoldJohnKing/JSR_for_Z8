using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jsr4Z8.Core.Backend;

/// <summary>Envelope header of an ASSISTANT_JSR_START line.</summary>
public sealed record JsrStart(
    [property: JsonPropertyName("job_id")] string JobId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("output")] string Output,
    [property: JsonPropertyName("engine")] string Engine,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("compressor")] string Compressor,
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("started")] double Started,
    [property: JsonPropertyName("dng")] bool Dng);

/// <summary>Envelope payload of the single ASSISTANT_JSR_RESULT line that ends every run.</summary>
public sealed record JsrResult(
    [property: JsonPropertyName("exit_code")] int ExitCode,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("log")] string Log,
    [property: JsonPropertyName("job_id")] string JobId,
    [property: JsonPropertyName("output")] string Output,
    [property: JsonPropertyName("engine")] string Engine,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("compressor")] string Compressor,
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("started")] double Started,
    [property: JsonPropertyName("dng")] bool Dng);

/// <summary>Backend progress: 0-100 percent plus a Chinese stage label.</summary>
public sealed record ProgressLine(int Percent, string Stage);

/// <summary>One parsed stdout/stderr line of the jsr_backend protocol.</summary>
public abstract record ProtocolEvent;

public sealed record Start(JsrStart Json) : ProtocolEvent;

/// <summary>Backend log text; Progress is set when the text is an SRHQ progress line.</summary>
public sealed record Log(string Text, ProgressLine? Progress) : ProtocolEvent;

public sealed record Result(JsrResult Json) : ProtocolEvent;

/// <summary>ASSISTANT_JSR_ERROR|&lt;msg&gt; on stderr when a run could not complete.</summary>
public sealed record ErrorOnStderr(string Message) : ProtocolEvent;

/// <summary>Any other line; the full text is preserved so nothing the backend printed is lost.</summary>
public sealed record Unrecognized(string Text) : ProtocolEvent;

/// <summary>
/// Parses one line of jsr_backend stdout/stderr (protocol facts #1-#4):
/// ASSISTANT_JSR_START|{json} once, ASSISTANT_JSR_LOG|&lt;text&gt; N times,
/// exactly one ASSISTANT_JSR_RESULT|{json} at the end, ASSISTANT_JSR_ERROR|&lt;msg&gt;
/// on stderr. SRHQ_V12_PROGRESS|int|stage and SRHQ_ETA_PROGRESS|dng|done|total
/// are recognized both inside LOG text and bare (the measured captures emit most
/// backend lines, progress included, without the LOG envelope); the export-phase
/// ETA maps onto the last 5 percent (95 + 5*done/total, "DNG 导出"). A trailing
/// '\r' (CRLF pipe) is stripped; malformed envelope JSON degrades to
/// Unrecognized instead of throwing, with the full line preserved.
/// </summary>
public static class ProtocolParser
{
    private const string StartPrefix = "ASSISTANT_JSR_START|";
    private const string LogPrefix = "ASSISTANT_JSR_LOG|";
    private const string ResultPrefix = "ASSISTANT_JSR_RESULT|";
    private const string ErrorPrefix = "ASSISTANT_JSR_ERROR|";
    private const string V12ProgressPrefix = "SRHQ_V12_PROGRESS|";
    private const string EtaProgressPrefix = "SRHQ_ETA_PROGRESS|";
    private const string DngExportStage = "DNG 导出";

    public static ProtocolEvent ParseLine(string raw)
    {
        string line = raw.EndsWith('\r') ? raw[..^1] : raw;

        if (line.StartsWith(StartPrefix, StringComparison.Ordinal))
        {
            try
            {
                JsrStart? json = JsonSerializer.Deserialize<JsrStart>(line[StartPrefix.Length..]);
                return json is not null ? new Start(json) : new Unrecognized(line);
            }
            catch (JsonException)
            {
                return new Unrecognized(line);
            }
        }

        if (line.StartsWith(ResultPrefix, StringComparison.Ordinal))
        {
            try
            {
                JsrResult? json = JsonSerializer.Deserialize<JsrResult>(line[ResultPrefix.Length..]);
                return json is not null ? new Result(json) : new Unrecognized(line);
            }
            catch (JsonException)
            {
                return new Unrecognized(line);
            }
        }

        if (line.StartsWith(ErrorPrefix, StringComparison.Ordinal))
            return new ErrorOnStderr(line[ErrorPrefix.Length..]);

        if (line.StartsWith(LogPrefix, StringComparison.Ordinal))
            return new Log(line[LogPrefix.Length..], TryParseProgress(line[LogPrefix.Length..]));

        // The captures emit SRHQ progress lines without the LOG envelope too.
        ProgressLine? bare = TryParseProgress(line);
        if (bare is not null)
            return new Log(line, bare);

        return new Unrecognized(line);
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
