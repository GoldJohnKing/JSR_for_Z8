using Jsr4Z8.Core.Backend;
using Xunit;

namespace Jsr4Z8.Tests;

/// <summary>
/// ProtocolParser robustness fuzz (the parser sits on a raw process pipe fed
/// by a foreign backend — any line it cannot make sense of must degrade to an
/// event, never throw: a parser exception kills both stdout reader tasks and
/// the job hangs without a diagnosis). Property-style checks:
///  (a) every malformed/hostile line returns an event and never throws, and
///      parsing is deterministic (same line → equal event, twice);
///  (b) determinism over every line of the real captured fixtures (direct bare-line
///      captures and the legacy envelope captures alike);
///  (c) lines starting with a known prefix family map to the right event kind
///      (well-formed SRHQ progress → Log-with-progress; everything else —
///      legacy ASSISTANT_* envelope text included, a second grammar the parser
///      no longer has — → Unrecognized).
/// </summary>
public class ProtocolParserFuzzTests
{
    /// <summary>Hostile corpus: empty, huge, invalid-UTF-8 residue (\uFFFD from
    /// the pipe decoder), binary garbage, bare/prefixed/suffixed pipes,
    /// truncated envelope JSON, NaN-ish/overflow/negative progress fields.</summary>
    public static TheoryData<string> MalformedCorpus => new()
    {
        "",
        "|",
        "|||",
        "|ASSISTANT_JSR_LOG",
        "ASSISTANT_JSR_LOG|text|ASSISTANT_JSR_LOG",
        new string('A', 10_000),
        string.Concat(Enumerable.Repeat(" ASSISTANT_JSR_LOG|", 600)),
        "ASSISTANT_JSR_LOG|\uFFFD\uFFFD\u0001\u0002\uFFFD",
        "ASSISTANT_JSR_LOG|" + new string((char)0, 64) + "\uFFFD\u0001\uFFFF",
        "\uFFFD\u0000\u0001\u0002\u0003\uFFFD\uFFFD",
        "ASSISTANT_JSR_START|",
        "ASSISTANT_JSR_START|{",
        "ASSISTANT_JSR_START|{\"job_id\":",
        "ASSISTANT_JSR_START|null",
        "ASSISTANT_JSR_START|[1,2,3]",
        "ASSISTANT_JSR_START|{\"job_id\":123,\"state\":true}",
        "ASSISTANT_JSR_START|" + new string('X', 10_000),
        "ASSISTANT_JSR_RESULT|",
        "ASSISTANT_JSR_RESULT|{\"exit_code\":",
        "ASSISTANT_JSR_RESULT|{\"exit_code\":3",
        // escaped unpaired surrogate: plain text to the bare-line parser (the JSON
        // reader that used to reject some envelope payloads is gone with the
        // envelopes); kept as hostile-UTF-8 residue.
        "ASSISTANT_JSR_RESULT|{\"exit_code\":0,\"state\":\"complete\",\"log\":\"\\uD800\"}",
        "ASSISTANT_JSR_RESULT|" + new string('Y', 10_000),
        "ASSISTANT_JSR_LOG|",
        "ASSISTANT_JSR_ERROR|",
        "ASSISTANT_JSR_ERROR|\uFFFD binary \x00 garbage \xFF",
        "SRHQ_V12_PROGRESS|",
        "SRHQ_V12_PROGRESS|-1|stage",
        "SRHQ_V12_PROGRESS|-99999999999999999999|stage",
        "SRHQ_V12_PROGRESS|99999999999999999999|stage",
        "SRHQ_V12_PROGRESS|nan|stage",
        "SRHQ_V12_PROGRESS|inf|stage",
        "SRHQ_V12_PROGRESS|1e5|stage",
        "SRHQ_V12_PROGRESS|+42|stage",
        "SRHQ_V12_PROGRESS| 42|stage",
        "SRHQ_V12_PROGRESS|42|",
        "SRHQ_V12_PROGRESS|42",
        "SRHQ_V12_PROGRESS|42|stage|extra",
        "SRHQ_ETA_PROGRESS|dng|-1|10",
        "SRHQ_ETA_PROGRESS|dng|5|0",
        "SRHQ_ETA_PROGRESS|dng|a|b",
        "SRHQ_ETA_PROGRESS|jpg|1|2",
        "SRHQ_ETA_PROGRESS|",
        "SRHQ_ETA_PROGRESS|dng|1|2|3",
        "SRHQ_V12_PROGRESS|" + new string('9', 500) + "|stage",
        "ASSISTANT_JSR_RESULT|{\"exit_code\":0,\"state\":\"complete\",\"log\":\"done\"\r",
    };

    // ---- (a) never throws, always an event, deterministic --------------------

    [Theory]
    [MemberData(nameof(MalformedCorpus))]
    public void ParseLine_MalformedInput_ReturnsEvent_NeverThrows_Deterministic(string line)
    {
        ProtocolEvent first = ProtocolParser.ParseLine(line);
        Assert.NotNull(first);
        ProtocolEvent second = ProtocolParser.ParseLine(line);
        Assert.Equal(first, second);            // records: value equality
        Assert.Equal(first.GetType(), second.GetType());
    }

    [Fact]
    public void ParseLine_MalformedCorpus_AllAtOnce_NeverThrows()
    {
        // Belt-and-braces whole-corpus sweep in one test: even a line that some
        // future refactor makes order-dependent cannot pass the per-line theory
        // by accident here.
        foreach (string line in MalformedCorpus)
        {
            ProtocolEvent evt = ProtocolParser.ParseLine(line);
            Assert.NotNull(evt);
            Assert.Equal(evt, ProtocolParser.ParseLine(line));
        }
    }

    // ---- (b) determinism over the real captured fixtures ----------------------

    [Theory]
    [InlineData("direct_success.log")]
    [InlineData("direct_fail.log")]
    [InlineData("stdout2.log")]
    [InlineData("stdout3.log")]
    [InlineData("success.log")]
    public void ParseLine_RealFixtureLines_ParseDeterministically(string fixture)
    {
        string[] lines = ReadFixtureLines(fixture);
        Assert.True(lines.Length > 10, $"fixture unexpectedly small: {lines.Length} lines");
        foreach (string line in lines)
        {
            ProtocolEvent first = ProtocolParser.ParseLine(line);
            ProtocolEvent second = ProtocolParser.ParseLine(line);
            Assert.Equal(first, second);
            Assert.Equal(first.GetType(), second.GetType());
        }
    }

    // ---- (c) prefix families map to the right event kind ----------------------

    [Fact]
    public void ParseLine_KnownPrefixFamilies_MapToRightEventKind()
    {
        // Well-formed SRHQ progress → exact Log-with-progress; everything else —
        // including legacy ASSISTANT_* envelope text (a second grammar the parser
        // no longer has) → Unrecognized with the full line preserved.
        var v12 = Assert.IsType<Log>(ProtocolParser.ParseLine("SRHQ_V12_PROGRESS|42|stage"));
        Assert.NotNull(v12.Progress);
        Assert.Equal(42, v12.Progress!.Percent);
        Assert.Equal("stage", v12.Progress.Stage);
        var eta = Assert.IsType<Log>(ProtocolParser.ParseLine("SRHQ_ETA_PROGRESS|dng|1|2"));
        Assert.NotNull(eta.Progress);
        Assert.Equal(97, eta.Progress!.Percent);            // 95 + 5·1/2
        Assert.IsType<Unrecognized>(ProtocolParser.ParseLine("totally unknown line"));
        Assert.IsType<Unrecognized>(ProtocolParser.ParseLine("ASSISTANT_JSR_LOG|plain text"));
        Assert.IsType<Unrecognized>(ProtocolParser.ParseLine("ASSISTANT_JSR_LOG|SRHQ_V12_PROGRESS|42|stage"));
        Assert.IsType<Unrecognized>(ProtocolParser.ParseLine("ASSISTANT_JSR_START|{"));
        Assert.IsType<Unrecognized>(ProtocolParser.ParseLine("ASSISTANT_JSR_ERROR|boom"));
    }

    [Theory]
    [InlineData("direct_success.log")]
    [InlineData("direct_fail.log")]
    [InlineData("stdout2.log")]
    [InlineData("stdout3.log")]
    [InlineData("success.log")]
    public void ParseLine_FixtureLinesWithKnownPrefix_LandInRightFamily(string fixture)
    {
        // Every REAL line starting with an SRHQ progress prefix lands in Log (or
        // Unrecognized for malformed payloads) — never in a sibling family; every
        // other line, legacy envelope captures included, is Unrecognized.
        foreach (string line in ReadFixtureLines(fixture))
        {
            ProtocolEvent evt = ProtocolParser.ParseLine(line);
            if (line.StartsWith("SRHQ_V12_PROGRESS|", StringComparison.Ordinal) ||
                line.StartsWith("SRHQ_ETA_PROGRESS|", StringComparison.Ordinal))
                Assert.True(evt is Log or Unrecognized, $"{Truncate(line)} → {evt.GetType().Name}");
            else
                Assert.IsType<Unrecognized>(evt);
        }
    }

    private static string Truncate(string line) =>
        line.Length <= 40 ? line : line[..40] + "…";

    /// <summary>Fixture stdout lines (ProtocolTests read-back pattern).</summary>
    private static string[] ReadFixtureLines(string name)
    {
        string path = SampleFiles.FindUp(Path.Combine("tests", "Jsr4Z8.Tests", "fixtures", "protocol", name));
        string[] lines = File.ReadAllText(path, System.Text.Encoding.UTF8).Split('\n');
        if (lines.Length > 0 && lines[^1].Length == 0)
            lines = lines[..^1];
        return Array.ConvertAll(lines, l => l.EndsWith('\r') ? l[..^1] : l);
    }
}
