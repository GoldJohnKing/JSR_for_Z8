using System.Text;
using Jsr4Z8.Core.Backend;
using Jsr4Z8.Core.Dark;
using Jsr4Z8.Core.Model;
using Jsr4Z8.Core.Ptc;
using Xunit;

namespace Jsr4Z8.Tests;

/// <summary>
/// Task 2 (direct invoke): BackendRequest.BuildArguments + the bare-line
/// ProtocolParser against the real direct-run captures in fixtures\protocol\
/// (direct_success.log = the 2026-10-15 probe §7 archive: one full 7-frame
/// lossless run, 324 bare engine lines, zero ASSISTANT_* envelopes, exit 0;
/// direct_fail.log = the hand-crafted bare failure shape). The argv template is
/// the probe-verified wire contract (docs/superpowers/notes/2026-10-15-jsr-backend-argv-probe.md
/// §2/§5); fixture lines are used verbatim.
/// </summary>
public class ProtocolTests
{
    /// <summary>Fixture stdout lines: split on '\n', drop the trailing terminator element, trim one '\r'.</summary>
    private static string[] ReadFixtureLines(string name)
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && dir != null; i++)
        {
            string candidate = Path.Combine(dir, "fixtures", "protocol", name);
            if (File.Exists(candidate))
            {
                string[] lines = File.ReadAllText(candidate, Encoding.UTF8).Split('\n');
                if (lines.Length > 0 && lines[^1].Length == 0)
                    lines = lines[..^1]; // the final '\n' terminates the last line, it is not a line of its own
                return Array.ConvertAll(lines, l => l.EndsWith('\r') ? l[..^1] : l);
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException($"Fixture not found: fixtures\\protocol\\{name} (searched upwards from {AppContext.BaseDirectory})");
    }

    private static string FirstLineStartingWith(string fixture, string prefix) =>
        ReadFixtureLines(fixture).First(l => l.StartsWith(prefix, StringComparison.Ordinal));

    // ---- BackendRequest.BuildArguments --------------------------------------------

    [Fact]
    public void BuildArguments_MatchesProbeVerifiedArgvTemplate()
    {
        // The exact main-chain argv the 2026-10-15 probe captured live (§2, re-verified
        // on 2.3.27 §5): order, values, one --raw-frame per frame in group order, the
        // Bridge-filled defaults replayed as observed constants.
        string[] frames =
        {
            @"D:\pics\DSC_7576.NEF", @"D:\pics\DSC_7577.NEF", @"D:\pics\DSC_7578.NEF",
            @"D:\pics\DSC_7579.NEF", @"D:\pics\DSC_7580.NEF", @"D:\pics\DSC_7581.NEF",
            @"D:\pics\DSC_7582.NEF",
        };
        var group = new TaskGroup(frames);
        var dark = new DarkMatch(3200, BlackDn: 1005.89, ReadNoiseDn: 14.54, Source: "preset");

        IReadOnlyList<string> args = BackendRequest.BuildArguments(
            group, new PtcMatch(1102.244, Exact: false, Source: "fit-high"),
            dark, outputDir: @"D:\pics\out", prefix: "jsr-001");

        string[] expected =
        {
            "--output-dir", @"D:\pics\out",
            "--out-prefix", "jsr-001",
            "--dng-compression", "auto",
            "--raw-frame", frames[0],
            "--raw-frame", frames[1],
            "--raw-frame", frames[2],
            "--raw-frame", frames[3],
            "--raw-frame", frames[4],
            "--raw-frame", frames[5],
            "--raw-frame", frames[6],
            "--developer-mode",
            "--read-noise-dn", "14.54",
            "--black-level-dn", "1005.89",
            "--full-well-electrons", "1102.2",
            "--deghost", "off",
            "--out-dng", "on",
            "--out-png", "off",
            "--out-tiff", "off",
            "--out-format", "jpeg",
            "--jpeg-quality", "95",
            "--camera-profile-source", "libraw",
        };
        Assert.Equal(expected, args);
    }

    [Fact]
    public void BuildArguments_NumberFormatting_InvariantAndOldFullwellRounding()
    {
        // Rounding is the old BuildJson behavior carried over verbatim: fullwell =
        // Math.Round(ptc.FullwellE, 1, AwayFromZero); RN/BL pass through as-is. The
        // InvariantCulture shortest form drops a trailing ".0" (probe §5: BL 1008 →
        // "--black-level-dn 1008", table FW 9512 → "9512", not "9512.0").
        var group = new TaskGroup(new[] { @"D:\pics\a.NEF" });
        var dark = new DarkMatch(3200, BlackDn: 1008, ReadNoiseDn: 14.07, Source: "preset");

        IReadOnlyList<string> args = BackendRequest.BuildArguments(
            group, new PtcMatch(9512.06, Exact: false, Source: "fit-high"),
            dark, outputDir: @"D:\pics\out", prefix: "p");

        Assert.Equal(new[] { "--read-noise-dn", "14.07" },
            args.SkipWhile(a => a != "--read-noise-dn").Take(2).ToArray());
        Assert.Equal(new[] { "--black-level-dn", "1008" },
            args.SkipWhile(a => a != "--black-level-dn").Take(2).ToArray());
        Assert.Equal(new[] { "--full-well-electrons", "9512.1" }, // 9512.06 → 1 decimal
            args.SkipWhile(a => a != "--full-well-electrons").Take(2).ToArray());
    }

    [Fact]
    public void BuildArguments_DeghostMapping_OffToday()
    {
        // The deghost switch keeps an explicit on/off mapping hook; the request path
        // always sends off today (the old BuildJson hardcoded deghost:false).
        Assert.Equal("off", BackendRequest.Deghost(false));
        Assert.Equal("on", BackendRequest.Deghost(true));
    }

    [Fact]
    public void BuildArguments_WithoutPresetRow_Throws()
    {
        var group = new TaskGroup(new[] { @"D:\pics\a.NEF" });
        Assert.Throws<ArgumentNullException>(() => BackendRequest.BuildArguments(
            group, new PtcMatch(1102.2, true, "table"), dark: null!, outputDir: @"D:\pics\out", prefix: "p"));
    }

    // ---- ProtocolParser.ParseLine: bare engine lines ------------------------------

    [Fact]
    public void ParseLine_BareV12ProgressFromDirectFixture_YieldsLogWithProgress()
    {
        string raw = FirstLineStartingWith("direct_success.log", "SRHQ_V12_PROGRESS|57|");

        var log = Assert.IsType<Log>(ProtocolParser.ParseLine(raw));

        Assert.Equal(raw, log.Text);
        Assert.Equal(57, log.Progress!.Percent);
        Assert.Equal("Transformer 恢复", log.Progress.Stage);
    }

    [Fact]
    public void ParseLine_EtaProgress_MapsExportPhaseTo95Through100()
    {
        var first = Assert.IsType<Log>(ProtocolParser.ParseLine("SRHQ_ETA_PROGRESS|dng|128|11040"));
        Assert.Equal(95, first.Progress!.Percent);
        Assert.Equal("DNG 导出", first.Progress.Stage);

        var mid = Assert.IsType<Log>(ProtocolParser.ParseLine("SRHQ_ETA_PROGRESS|dng|10880|11040"));
        Assert.Equal(95 + 5 * 10880 / 11040, mid.Progress!.Percent); // 99
        Assert.Equal("DNG 导出", mid.Progress.Stage);

        var done = Assert.IsType<Log>(ProtocolParser.ParseLine("SRHQ_ETA_PROGRESS|dng|11040|11040"));
        Assert.Equal(100, done.Progress!.Percent);
        Assert.Equal("DNG 导出", done.Progress.Stage);

        // Malformed variants stay ordinary text, never a throw.
        Assert.IsType<Unrecognized>(ProtocolParser.ParseLine("SRHQ_ETA_PROGRESS|dng|5|0"));
        Assert.IsType<Unrecognized>(ProtocolParser.ParseLine("SRHQ_ETA_PROGRESS|jpg|1|2"));
    }

    [Fact]
    public void ParseLine_NonProtocolLines_PassThroughUnrecognized()
    {
        // Controller ruling: the engine's first-line "PTC {...}" echo is an ordinary
        // log line, as are FRAME echo lines, plain chatter and the DONE tail.
        var ptc = Assert.IsType<Unrecognized>(ProtocolParser.ParseLine(FirstLineStartingWith("direct_success.log", "PTC ")));
        Assert.StartsWith("PTC {", ptc.Text);

        var frame = Assert.IsType<Unrecognized>(ProtocolParser.ParseLine("SRHQ_V12_FRAME|0|+0.00|0.01|D:\\pics\\DSC_7576.NEF"));
        Assert.Equal("SRHQ_V12_FRAME|0|+0.00|0.01|D:\\pics\\DSC_7576.NEF", frame.Text);

        var proxy = Assert.IsType<Unrecognized>(ProtocolParser.ParseLine("Proxy 1/7 prepared"));
        Assert.Equal("Proxy 1/7 prepared", proxy.Text);

        var done = Assert.IsType<Unrecognized>(ProtocolParser.ParseLine("DONE total 339.2s"));
        Assert.Equal("DONE total 339.2s", done.Text);

        var empty = Assert.IsType<Unrecognized>(ProtocolParser.ParseLine(""));
        Assert.Equal("", empty.Text);
    }

    [Fact]
    public void ParseLine_LegacyEnvelopeLine_YieldsUnrecognized()
    {
        // The ASSISTANT_* envelopes were Bridge-added and are gone in the direct
        // protocol; an old-envelope stream replayed through the parser must degrade
        // to ordinary text (full line preserved), never a second grammar.
        var log = Assert.IsType<Unrecognized>(ProtocolParser.ParseLine("ASSISTANT_JSR_LOG|SRHQ_V12_PROGRESS|57|Transformer 恢复"));
        Assert.Equal("ASSISTANT_JSR_LOG|SRHQ_V12_PROGRESS|57|Transformer 恢复", log.Text);

        Assert.IsType<Unrecognized>(ProtocolParser.ParseLine("ASSISTANT_JSR_START|{\"job_id\":\"j\",\"state\":\"running\"}"));
        Assert.IsType<Unrecognized>(ProtocolParser.ParseLine("ASSISTANT_JSR_RESULT|{\"exit_code\":0,\"state\":\"complete\"}"));
        Assert.IsType<Unrecognized>(ProtocolParser.ParseLine("ASSISTANT_JSR_ERROR|boom"));
    }

    // ---- whole-file runs against the direct captures -------------------------------

    [Fact]
    public void WholeFile_DirectSuccess_EveryLineBecomesAnEventWithoutTextLoss()
    {
        string[] lines = ReadFixtureLines("direct_success.log");
        ProtocolEvent[] events = Array.ConvertAll(lines, ProtocolParser.ParseLine);

        Assert.Equal(324, lines.Length);
        Assert.Equal(lines.Length, events.Length); // 事件总数 = 行数

        // The measured run-1 line mix (probe §7.2): 110 V12 + 87 ETA progress lines,
        // zero envelopes anywhere.
        var progress = events.OfType<Log>().Where(e => e.Progress is not null).ToList();
        Assert.Equal(110, progress.Count(e => e.Progress!.Stage != "DNG 导出"));
        Assert.Equal(87, progress.Count(e => e.Progress!.Stage == "DNG 导出"));
        Assert.Equal(197, progress.Count);
        Assert.DoesNotContain(lines, l => l.Contains("ASSISTANT", StringComparison.Ordinal));

        // The run ends on the terminal V12 line; the PTC echo is line 1 (ruling 3).
        Assert.Contains(events, e => e is Log { Progress: { Percent: 100, Stage: "导出完成" } });
        Assert.IsType<Unrecognized>(events[0]);
        Assert.StartsWith("PTC ", lines[0]);
        Assert.IsType<Unrecognized>(events[^1]);
        Assert.Equal("DONE total 339.2s", lines[^1]);

        // No text loss: every event carries its full line.
        foreach ((string line, ProtocolEvent ev) in lines.Zip(events))
        {
            string text = ev switch
            {
                Log l => l.Text,
                Unrecognized u => u.Text,
                _ => throw new Xunit.Sdk.XunitException($"unexpected event kind: {ev.GetType().Name}"),
            };
            Assert.Equal(line, text);
        }
    }

    [Fact]
    public void WholeFile_DirectFail_BareAbortAndMemoryLinesStayOrdinary()
    {
        // The failure shape (probe run0 / stub_fail): the bare abort line walks
        // stdout like any other line — the runner's humanized extraction scans the
        // tail for it; the parser itself must not special-case it.
        string[] lines = ReadFixtureLines("direct_fail.log");
        ProtocolEvent[] events = Array.ConvertAll(lines, ProtocolParser.ParseLine);

        Assert.Equal(12, lines.Length);
        Assert.Equal(lines.Length, events.Length);
        Assert.DoesNotContain(lines, l => l.Contains("ASSISTANT", StringComparison.Ordinal));

        Assert.Contains(events, e => e is Unrecognized { Text: var t } &&
            t.StartsWith("SRHQ_NET_ABORT:runtime|", StringComparison.Ordinal));
        Assert.Contains(events, e => e is Unrecognized { Text: var t } &&
            t.StartsWith("MemoryError:", StringComparison.Ordinal));
        Assert.Equal(2, events.OfType<Log>().Count(e => e.Progress is not null)); // the two V12 lines
    }
}
