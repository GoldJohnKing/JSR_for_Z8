using System.Text;
using System.Text.Json;
using Jsr4Z8.Core.Backend;
using Jsr4Z8.Core.Dark;
using Jsr4Z8.Core.Model;
using Jsr4Z8.Core.Ptc;
using Xunit;

namespace Jsr4Z8.Tests;

/// <summary>
/// Task 6: BackendRequest.BuildJson + ProtocolParser against the two real
/// captured backend runs in fixtures\protocol\ (stdout2 = failed chain ending
/// exit_code 3, stdout3 = full success ending exit_code 0). The wire shapes are
/// the pinned protocol facts from the plan; fixture lines are used verbatim.
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

    // ---- BackendRequest.BuildJson -------------------------------------------------

    [Fact]
    public void BuildJson_UsesProbeVerifiedManualShape()
    {
        string[] frames =
        {
            @"D:\pics\DSC_7576.NEF", @"D:\pics\DSC_7577.NEF", @"D:\pics\DSC_7578.NEF",
            @"D:\pics\DSC_7579.NEF", @"D:\pics\DSC_7580.NEF", @"D:\pics\DSC_7581.NEF",
            @"D:\pics\DSC_7582.NEF",
        };
        var group = new TaskGroup(frames);
        var dark = new DarkMatch(3200, BlackDn: 1005.89, ReadNoiseDn: 14.54, Source: "preset");

        string json = BackendRequest.BuildJson(group, new PtcMatch(1102.244, Exact: false, Source: "fit-high"),
            dark, outputDir: @"D:\pics\out", prefix: "jsr-001");

        Assert.DoesNotContain('\uFEFF', json);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement p = doc.RootElement.GetProperty("parameters");
        // The manual shape the 2026-10-14 live probe verified: the dark key is REPLACED
        // by manual + read_noise + black (backend answers "manual DN" / "manual RN(DN)").
        Assert.Equal(new[]
        {
            "black", "color", "deghost", "dng", "engine", "fullwell", "manual",
            "mode", "output", "prefix", "preview", "ptc", "read_noise", "tiff",
        }, p.EnumerateObject().Select(prop => prop.Name).OrderBy(n => n));
        Assert.False(p.TryGetProperty("dark", out _));

        Assert.True(p.GetProperty("manual").GetBoolean());
        Assert.Equal(1005.89m, p.GetProperty("black").GetDecimal());
        Assert.Equal(14.54m, p.GetProperty("read_noise").GetDecimal());
        Assert.Equal(1102.2m, p.GetProperty("fullwell").GetDecimal()); // 1102.244 → 1 decimal
        Assert.Equal("stormer7", p.GetProperty("engine").GetString());
        Assert.Equal("libraw", p.GetProperty("color").GetString());
    }

    [Fact]
    public void BuildJson_WithoutPresetRow_Throws()
    {
        var group = new TaskGroup(new[] { @"D:\pics\a.NEF" });
        Assert.Throws<ArgumentNullException>(() => BackendRequest.BuildJson(
            group, new PtcMatch(1102.2, true, "table"), dark: null, outputDir: @"D:\pics\out", prefix: "p"));
    }

    // ---- ProtocolParser.ParseLine: envelope lines ---------------------------------

    [Fact]
    public void ParseLine_StartLineFromFixture_YieldsStart()
    {
        string raw = FirstLineStartingWith("stdout3.log", "ASSISTANT_JSR_START|");

        var start = Assert.IsType<Start>(ProtocolParser.ParseLine(raw));

        Assert.StartsWith("jsr-", start.Json.JobId);
        Assert.Equal("running", start.Json.State);
        Assert.Equal("reconstruction", start.Json.Stage);
        Assert.Equal(@"D:\GitRepos\JSR_for_Z8\tmp_run1\out", start.Json.Output);
        Assert.Equal("stormer7", start.Json.Engine);
        Assert.Equal("full", start.Json.Mode);
        Assert.Contains("jsr_backend.exe", start.Json.Compressor);
        Assert.True(start.Json.Pid > 0);
        Assert.True(start.Json.Started > 0);
        Assert.True(start.Json.Dng);
    }

    [Fact]
    public void ParseLine_ResultLineFromFailedRun_YieldsFailedResult()
    {
        string raw = FirstLineStartingWith("stdout2.log", "ASSISTANT_JSR_RESULT|");

        var result = Assert.IsType<Result>(ProtocolParser.ParseLine(raw));

        Assert.Equal(3, result.Json.ExitCode);
        Assert.Equal("failed", result.Json.State);
        Assert.StartsWith("jsr-", result.Json.JobId);
        Assert.Contains("\r\n", result.Json.Log); // \r\n inside the log field is unescaped by the JSON reader
        Assert.Contains("MemoryError", result.Json.Log);
    }

    [Fact]
    public void ParseLine_ResultLineFromSuccessRun_YieldsCompleteResult()
    {
        string raw = FirstLineStartingWith("stdout3.log", "ASSISTANT_JSR_RESULT|");

        var result = Assert.IsType<Result>(ProtocolParser.ParseLine(raw));

        Assert.Equal(0, result.Json.ExitCode);
        Assert.Equal("complete", result.Json.State);
        Assert.Equal("reconstruction", result.Json.Stage);
    }

    [Fact]
    public void ParseLine_StderrErrorLine_YieldsErrorOnStderr()
    {
        var error = Assert.IsType<ErrorOnStderr>(ProtocolParser.ParseLine("ASSISTANT_JSR_ERROR|JSR未完成，原始日志：\"PTC {...}\""));
        Assert.Equal("JSR未完成，原始日志：\"PTC {...}\"", error.Message);
    }

    // ---- ProtocolParser.ParseLine: progress lines ---------------------------------

    [Fact]
    public void ParseLine_PrefixedV12Progress_YieldsLogWithProgress()
    {
        var log = Assert.IsType<Log>(ProtocolParser.ParseLine("ASSISTANT_JSR_LOG|SRHQ_V12_PROGRESS|57|Transformer 恢复"));
        Assert.Equal("SRHQ_V12_PROGRESS|57|Transformer 恢复", log.Text);
        Assert.NotNull(log.Progress);
        Assert.Equal(57, log.Progress!.Percent);
        Assert.Equal("Transformer 恢复", log.Progress.Stage);
    }

    [Fact]
    public void ParseLine_BareV12ProgressFromFixture_YieldsLogWithProgress()
    {
        // The captures emit most backend lines (including SRHQ progress) without the LOG envelope.
        string raw = FirstLineStartingWith("stdout3.log", "SRHQ_V12_PROGRESS|57|");

        var log = Assert.IsType<Log>(ProtocolParser.ParseLine(raw));

        Assert.Equal(raw, log.Text);
        Assert.Equal(57, log.Progress!.Percent);
        Assert.Equal("Transformer 恢复", log.Progress.Stage);
    }

    [Fact]
    public void ParseLine_EtaProgress_MapsExportPhaseTo95Through100()
    {
        var mid = Assert.IsType<Log>(ProtocolParser.ParseLine("SRHQ_ETA_PROGRESS|dng|10880|11040"));
        Assert.Equal(95 + 5 * 10880 / 11040, mid.Progress!.Percent); // 99
        Assert.Equal("DNG 导出", mid.Progress.Stage);

        var done = Assert.IsType<Log>(ProtocolParser.ParseLine("SRHQ_ETA_PROGRESS|dng|11040|11040"));
        Assert.Equal(100, done.Progress!.Percent);
        Assert.Equal("DNG 导出", done.Progress.Stage);

        var wrapped = Assert.IsType<Log>(ProtocolParser.ParseLine("ASSISTANT_JSR_LOG|SRHQ_ETA_PROGRESS|dng|128|11040"));
        Assert.Equal(95, wrapped.Progress!.Percent);
        Assert.Equal("DNG 导出", wrapped.Progress.Stage);
    }

    [Fact]
    public void ParseLine_PlainUnprefixedLine_YieldsUnrecognized()
    {
        var plain = Assert.IsType<Unrecognized>(ProtocolParser.ParseLine("Proxy 1/7 prepared"));
        Assert.Equal("Proxy 1/7 prepared", plain.Text);

        // Only the two SRHQ progress formats get bare-line treatment; other backend lines stay Unrecognized.
        var frame = Assert.IsType<Unrecognized>(ProtocolParser.ParseLine("SRHQ_V12_FRAME|0|+0.00|0.01|D:\\pics\\DSC_7576.NEF"));
        Assert.Equal("SRHQ_V12_FRAME|0|+0.00|0.01|D:\\pics\\DSC_7576.NEF", frame.Text);

        var empty = Assert.IsType<Unrecognized>(ProtocolParser.ParseLine(""));
        Assert.Equal("", empty.Text);
    }

    // ---- whole-file runs ----------------------------------------------------------

    [Fact]
    public void WholeFile_SuccessRun_EveryLineBecomesAnEventWithoutTextLoss()
    {
        string[] lines = ReadFixtureLines("stdout3.log");
        ProtocolEvent[] events = Array.ConvertAll(lines, ProtocolParser.ParseLine);

        Assert.Equal(lines.Length, events.Count()); // 事件总数 = 行数
        Assert.Single(events.OfType<Start>());
        var result = Assert.Single(events.OfType<Result>());
        Assert.Equal("complete", result.Json.State);
        Assert.Equal(0, result.Json.ExitCode);
        Assert.Contains(events, e => e is Log { Progress: { Percent: 57, Stage: "Transformer 恢复" } });

        foreach ((string line, ProtocolEvent ev) in lines.Zip(events))
        {
            switch (ev)
            {
                case Start:
                    Assert.StartsWith("ASSISTANT_JSR_START|", line);
                    break;
                case Result:
                    Assert.StartsWith("ASSISTANT_JSR_RESULT|", line);
                    break;
                case ErrorOnStderr:
                    Assert.StartsWith("ASSISTANT_JSR_ERROR|", line);
                    break;
                case Log log:
                    Assert.True(line == "ASSISTANT_JSR_LOG|" + log.Text || line == log.Text,
                        $"Log text lost for line: {line}");
                    break;
                case Unrecognized unrecognized:
                    Assert.Equal(line, unrecognized.Text); // no Unrecognized loses text
                    break;
            }
        }
    }

    [Fact]
    public void FullFile_SuccessLog_BackfilledFixture_CompleteResultWithEtaProgress()
    {
        // Task 16 Step 4: the real success stdout stream backfilled under its plan
        // name success.log (byte-identical to the Task 6 stdout3.log capture: one
        // full 7-frame V12 run). Pins the renamed fixture: full parse, single
        // complete RESULT, and the DNG-export ETA tail the UI's finish estimate
        // depends on.
        string[] lines = ReadFixtureLines("success.log");
        Assert.Equal(16175, lines.Length);

        ProtocolEvent[] events = Array.ConvertAll(lines, ProtocolParser.ParseLine);
        Assert.Single(events.OfType<Start>());
        var result = Assert.Single(events.OfType<Result>());
        Assert.Equal("complete", result.Json.State);
        Assert.Equal(0, result.Json.ExitCode);
        Assert.Contains(events, e => e is Log { Progress: { Stage: "DNG 导出" } });
    }

    [Fact]
    public void WholeFile_FailureRun_EndsInSingleFailedResult()
    {
        string[] lines = ReadFixtureLines("stdout2.log");
        ProtocolEvent[] events = Array.ConvertAll(lines, ProtocolParser.ParseLine);

        Assert.Equal(lines.Length, events.Count());
        Assert.Single(events.OfType<Start>());
        var result = Assert.Single(events.OfType<Result>());
        Assert.Equal("failed", result.Json.State);
        Assert.Equal(3, result.Json.ExitCode);

        foreach ((string line, ProtocolEvent ev) in lines.Zip(events))
        {
            switch (ev)
            {
                case Start:
                    Assert.StartsWith("ASSISTANT_JSR_START|", line);
                    break;
                case Result:
                    Assert.StartsWith("ASSISTANT_JSR_RESULT|", line);
                    break;
                case ErrorOnStderr:
                    Assert.StartsWith("ASSISTANT_JSR_ERROR|", line);
                    break;
                case Log log:
                    Assert.True(line == "ASSISTANT_JSR_LOG|" + log.Text || line == log.Text,
                        $"Log text lost for line: {line}");
                    break;
                case Unrecognized unrecognized:
                    Assert.Equal(line, unrecognized.Text);
                    break;
            }
        }
    }
}
