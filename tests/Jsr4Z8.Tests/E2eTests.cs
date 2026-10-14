using System.Diagnostics;
using System.Text;
using Jsr4Z8.Core.Backend;
using Jsr4Z8.Core.Dark;
using Jsr4Z8.Core.Model;
using Jsr4Z8.Core.Native;
using Jsr4Z8.Core.Payload;
using Jsr4Z8.Core.Ptc;
using Jsr4Z8.Core.Queue;
using Jsr4Z8.Core.Settings;
using Xunit;

namespace Jsr4Z8.Tests;

/// <summary>
/// Task 16 端到端验收: the REAL chain on the Windows host — real samples, the
/// real extracted backend payload, a real GPU run and the real finalizer
/// product. Opt-in via environment variable JSR4Z8_E2E=1; with it unset every
/// test returns early and passes (normal CI stays fast and GPU-free). With it
/// set, the four tests of this class run sequentially (single xUnit class =
/// single collection) and cost ~17 min of GPU time: three full ~5.5 min runs
/// plus one 30 s cancel.
///
/// Payload note: the queue's payload phase is pointed at the payload the
/// release exe's --selftest extracted into %LOCALAPPDATA%\JSR4Z8\backend\
/// &lt;hash&gt;\ via <see cref="TaskQueueRunner.ResolveBackendOverride"/> — the
/// dev/test Core assembly carries no embedded payload resource (that pair is
/// embedded only by the release build). The embedded-payload path itself —
/// extraction fast-path and self-heal — is exercised through the release exe's
/// --selftest and recorded in task-16-report.md.
/// </summary>
public class E2eTests
{
    private const string EnvVar = "JSR4Z8_E2E";
    private const string DngExportStage = "DNG 导出"; // ProtocolParser's mapped ETA stage label

    private static bool Enabled => Environment.GetEnvironmentVariable(EnvVar) == "1";

    // ---- shared plumbing ----------------------------------------------------

    private static string RepoRoot() => SampleFiles.RepoRoot();

    private static string SamplesDir() => Path.Combine(RepoRoot(), "samples");

    /// <summary>Gitignored scratch dir (tmp_e2e) for captured artifacts and evidence.</summary>
    private static string ArtifactsDir() => Path.Combine(RepoRoot(), "tmp_e2e", "artifacts");

    private static string EvidencePath => Path.Combine(ArtifactsDir(), "e2e-evidence.txt");

    /// <summary>Evidence line: console + append-only evidence file under tmp_e2e.</summary>
    private static void Ev(string line)
    {
        string stamped = $"[{DateTime.Now:HH:mm:ss}] {line}";
        Console.WriteLine("[E2E] " + stamped);
        Directory.CreateDirectory(ArtifactsDir());
        File.AppendAllText(EvidencePath, stamped + Environment.NewLine, Encoding.UTF8);
    }

    private static string[] FramesFor(string samplesDir) =>
        Enumerable.Range(7576, 7).Select(i => Path.Combine(samplesDir, "test_frames", "FX", "Lossless", $"DSC_{i}.NEF")).ToArray();

    /// <summary>The payload the release selftest extracted; asserts the operator ran it first.</summary>
    private static (string Bridge, string Resources) ResolveExtractedBackend()
    {
        Assert.True(Directory.Exists(PayloadManager.DefaultBackendRoot),
            $"后端载荷根不存在: {PayloadManager.DefaultBackendRoot}（先运行 out\\Jsr4Z8_v1.0.0.exe --selftest 释放载荷）");
        string[] candidates = Directory.GetDirectories(PayloadManager.DefaultBackendRoot)
            .Where(d => File.Exists(Path.Combine(d, TaskQueueRunner.BridgeFileName)))
            .ToArray();
        string backend = Assert.Single(candidates);
        return (Path.Combine(backend, TaskQueueRunner.BridgeFileName), Path.Combine(backend, "resources"));
    }

    /// <summary>Queue events collected during one run (background threads → lock).</summary>
    private sealed class RunCapture
    {
        public readonly List<string> Lines = new();
        public int V12ProgressLines;
        public int EtaProgressLines;
        public int MaxPercent = -1;
        public JsrStart? Start;

        public void OnEvent(QueueEvent e)
        {
            switch (e)
            {
                case BackendStarted s:
                    Start = s.Start;
                    break;
                case BackendLogLine l:
                    lock (Lines) Lines.Add(l.Text);
                    if (l.Progress is { } p)
                    {
                        if (p.Stage == DngExportStage) EtaProgressLines++;
                        else V12ProgressLines++;
                        MaxPercent = Math.Max(MaxPercent, p.Percent);
                    }
                    break;
            }
        }
    }

    private static (TaskGroup Group, PtcMatch Ptc, DarkMatch? Dark, TaskQueueRunner Engine, RunCapture Capture) PrepareRun(
        string samplesDir)
    {
        var group = new TaskGroup(FramesFor(samplesDir));
        var validation = TaskGroupValidator.Validate(group);
        Assert.True(validation.CanRun, string.Join("；", validation.Issues.Select(i => i.MessageZh)));
        Assert.NotNull(validation.Ptc);
        PtcMatch ptc = validation.Ptc!;
        Ev($"校验: Z8 · ISO3200 · 满阱 {ptc.FullwellE:0.0} e⁻（{(ptc.Exact ? "表值" : ptc.Source)}）" +
           $" · 黑场预置匹配（black {validation.Dark!.BlackDn} DN · RN {validation.Dark.ReadNoiseDn} DN）");

        var (bridge, resources) = ResolveExtractedBackend();
        var engine = new TaskQueueRunner
        {
            ResolveBackendOverride = _ => new TaskQueueRunner.BackendPayload(bridge, resources),
        };
        return (group, ptc, validation.Dark, engine, new RunCapture());
    }

    private static void ReportOutcome(TaskRunOutcome outcome, RunCapture capture, string label)
    {
        string state = outcome.Success ? "Success" : outcome.Cancelled ? "Cancelled" : "Failed";
        Ev($"{label}: {state}, 耗时 {outcome.Elapsed.TotalSeconds:F1}s, 消息「{outcome.Message}」, " +
           $"输出 {outcome.OutputPath ?? "-"}, 诊断目录 {outcome.DiagnosticDir ?? "-"}");
        lock (capture.Lines)
            Ev($"{label}: 日志 {capture.Lines.Count} 行, V12 进度 {capture.V12ProgressLines} 条, " +
               $"导出 ETA 进度 {capture.EtaProgressLines} 条, 峰值百分比 {capture.MaxPercent}%");
    }

    /// <summary>tasklist /FI probe: is a process with this image name still alive?</summary>
    private static bool ProcessAlive(string image)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c tasklist /FI \"IMAGENAME eq {image}\" /FO CSV /NH")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(TimeSpan.FromSeconds(10));
        return output.Contains(image, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Polls tasklist until the image disappears; seconds taken, or -1 past the
    /// limit. taskkill /T /F initiates termination but a force-killed GPU process stays
    /// in the tasklist snapshot for seconds while the driver releases its VRAM —
    /// measured 2026-10-14: alive right at the 已停止 card, gone within seconds. The
    /// acceptance criterion is "no leftover shortly after", hence a bounded wait.</summary>
    private static async Task<double> WaitProcessGoneSeconds(string image, TimeSpan limit)
    {
        var clock = Stopwatch.StartNew();
        while (ProcessAlive(image))
        {
            if (clock.Elapsed > limit)
                return -1;
            await Task.Delay(500);
        }
        return clock.Elapsed.TotalSeconds;
    }

    // ---- Step 1 + 2: full run on the real samples + full product DNG check ----

    [Fact]
    [Trait("Category", "E2E")]
    public async Task FullRun_NoDarkFile_PresetAutoMatch_ProducesValidJsrDng()
    {
        if (!Enabled)
        {
            Ev("skipped (set JSR4Z8_E2E=1 to run)");
            return;
        }

        var (group, ptc, dark, engine, capture) = PrepareRun(SamplesDir());
        Assert.NotNull(dark);
        Assert.Equal(3200d, dark!.Iso);
        TaskRunOutcome outcome = await engine
            .RunAsync(group, ptc, dark, new AppSettings(), CancellationToken.None, capture.OnEvent);
        ReportOutcome(outcome, capture, "全流程·预置黑场");

        Assert.True(outcome.Success, outcome.Message);
        string expected = Path.Combine(SamplesDir(), "test_frames", "FX", "Lossless", "JSR_DSC_7576.dng");
        Assert.True(File.Exists(expected), $"最终 DNG 缺失: {expected}");
        AssertFinalDng(expected);
    }


    [Fact]
    [Trait("Category", "E2E")]
    public async Task FullRun_HeFrames_NoDark_HePresetAutoMatch_ProducesValidJsrDng()
    {
        if (!Enabled)
        {
            Ev("skipped (set JSR4Z8_E2E=1 to run)");
            return;
        }

        // samples/he_frames = the 7 HE ISO-3200 frames (2026-10-14): the group must
        // auto-match the HE preset row (13, 3200) — NOT the lossless row, whose
        // read noise is −46% off — and run the manual-DN path end to end.
        string heDir = Path.Combine(SamplesDir(), "test_frames", "FX", "he_frames");
        var group = new TaskGroup(Directory.GetFiles(heDir, "*.NEF").OrderBy(p => p).ToArray());
        var validation = TaskGroupValidator.Validate(group);
        Assert.True(validation.CanRun, string.Join("；", validation.Issues.Select(i => i.MessageZh)));
        Assert.NotNull(validation.Ptc);
        Assert.NotNull(validation.Dark);
        Assert.Equal(3200d, validation.Dark!.Iso);
        DarkMatch heRow = DarkTable.LoadDefault().Lookup(3200, 13)!;
        Assert.Equal(heRow.BlackDn, validation.Dark.BlackDn, 3);
        Assert.Equal(heRow.ReadNoiseDn, validation.Dark.ReadNoiseDn, 3);
        Ev($"校验: Z8·HE·ISO3200 · 满阱 {validation.Ptc!.FullwellE:0.0} e⁻ · 黑场 HE 预置行 " +
           $"（black {validation.Dark.BlackDn} DN · RN {validation.Dark.ReadNoiseDn} DN；无损行 RN {DarkTable.LoadDefault().Lookup(3200, 3)!.ReadNoiseDn} DN 不适用）");

        var (bridge, resources) = ResolveExtractedBackend();
        var engine = new TaskQueueRunner { ResolveBackendOverride = _ => new TaskQueueRunner.BackendPayload(bridge, resources) };
        TaskRunOutcome outcome = await engine
            .RunAsync(group, validation.Ptc, validation.Dark, new AppSettings(), CancellationToken.None, _ => { });
        ReportOutcome(outcome, new RunCapture(), "全流程·HE 预置黑场");

        Assert.True(outcome.Success, outcome.Message);
        string expected = Path.Combine(heDir, "JSR_DSC_7652.dng");
        Assert.True(File.Exists(expected), $"最终 DNG 缺失: {expected}");
        AssertFinalDng(expected, group.ReferencePath);
    }
    /// <summary>Overload computing the expected AsShotNeutral from the reference
    /// NEF's own as-shot WB (1/cam_mul, G normalized) — captures shot at another
    /// time/scene carry different multipliers than the locked DSC_7576 values.</summary>
    private static void AssertFinalDng(string dngPath, string referenceNefPath)
    {
        var reference = Jsr4Z8.Core.RawMetadataReader.Read(referenceNefPath);
        AssertFinalDng(dngPath, new[]
        {
            1.0 / reference.CamMul[0], 1.0 / reference.CamMul[1], 1.0 / reference.CamMul[2],
        });
    }

    private static void AssertFinalDng(string dngPath, double[]? expectedAsShotNeutral = null)
    {
        long size = new FileInfo(dngPath).Length;
        long uncompressed = 16560L * 11040 * 3 * 2;
        Ev($"产物: {dngPath} ({size / 1024.0 / 1024.0:F1} MB; 未压缩 {uncompressed / 1024.0 / 1024.0:F0} MB 的 " +
           $"{100.0 * size / uncompressed:F1}%)");
        Assert.InRange(size, 100_000_000, 700_000_000); // sane super-res LinearRaw product

        byte[] file = File.ReadAllBytes(dngPath);
        Assert.Equal(0x49, file[0]);
        Assert.Equal(0x49, file[1]);
        Assert.Equal(42, TestTiff.U16(file, 2));
        var ifd0 = TestTiff.ParseIfd(file, TestTiff.U32(file, 4));

        // IFD0 identity & attribution (probe fact #9: the tool names itself, no Artist tag)
        Assert.Equal("JSR for Z8", TestTiff.Ascii(ifd0.Tags[305].Value));
        Assert.False(ifd0.Tags.ContainsKey(315), "IFD0 不得携带 Artist 署名");

        // raw SubIFD topology: ×2 LinearRaw RGB, lossless JPEG tiles
        Assert.True(ifd0.Tags.ContainsKey(330), "SubIFDs missing");
        var (_, _, v330) = ifd0.Tags[330];
        var raw = TestTiff.ParseIfd(file, TestTiff.U32(v330, 0));
        Assert.Equal(0u, TestTiff.U32(raw.Tags[254].Value, 0));
        Assert.Equal(16560u, TestTiff.U32(raw.Tags[256].Value, 0));
        Assert.Equal(11040u, TestTiff.U32(raw.Tags[257].Value, 0));
        Assert.Equal(7, TestTiff.U16(raw.Tags[259].Value, 0));
        Assert.Equal(34892, TestTiff.U16(raw.Tags[262].Value, 0));
        Assert.Equal(3, TestTiff.U16(raw.Tags[277].Value, 0));
        Assert.Equal(16, TestTiff.U16(raw.Tags[258].Value, 0));
        Assert.Equal(0, TestTiff.U16(raw.Tags[50714].Value, 0));
        Assert.Equal(65535u, TestTiff.U32(raw.Tags[50717].Value, 0));

        // Save_Z8 conventions spot check: geometry pair, color tags, baseline trio
        Assert.True(raw.Tags.ContainsKey(50718), "DefaultScale missing");
        var aa = raw.Tags[50829].Value; // ActiveArea top,left,bottom,right (×2 domain)
        Assert.Equal(16u, TestTiff.U32(aa, 0));
        Assert.Equal(24u, TestTiff.U32(aa, 4));
        Assert.Equal(11024u, TestTiff.U32(aa, 8));
        Assert.Equal(16536u, TestTiff.U32(aa, 12));
        // Post-release fix 8: DefaultCropOrigin is ActiveArea-relative (DNG spec) —
        // full-frame pair writes (0,0), never the pre-fix absolute (24,16).
        Assert.Equal(0u, TestTiff.U32(raw.Tags[50719].Value, 0));
        Assert.Equal(0u, TestTiff.U32(raw.Tags[50719].Value, 8));
        Assert.Equal(16512u, TestTiff.U32(raw.Tags[50720].Value, 0));
        Assert.Equal(11008u, TestTiff.U32(raw.Tags[50720].Value, 8));
        Assert.Equal(9u, ifd0.Tags[50721].Count); // ColorMatrix1 (SRATIONAL×9)
        Assert.Equal(21u, TestTiff.U16(ifd0.Tags[50778].Value, 0)); // CalibrationIlluminant1 = D65
        Assert.True(ifd0.Tags.ContainsKey(50730), "BaselineExposure missing");
        Assert.True(ifd0.Tags.ContainsKey(50731), "BaselineNoise missing");
        Assert.True(ifd0.Tags.ContainsKey(50732), "BaselineSharpness missing");

        // AsShotNeutral = 1/cam_mul of the reference frame (probe fact #8 locked the
        // DSC_7576 values [0.560175, 1.0, 0.557734]; captures with their own as-shot
        // WB pass the expectation in).
        var asn = ifd0.Tags[50728].Value;
        Assert.Equal(3u, ifd0.Tags[50728].Count);
        double e0 = expectedAsShotNeutral?[0] ?? 0.560175;
        double e2 = expectedAsShotNeutral?[2] ?? 0.557734;
        Assert.InRange(TestTiff.U32(asn, 0) / 1e6, e0 - 1e-4, e0 + 1e-4);
        Assert.InRange(TestTiff.U32(asn, 8) / 1e6, 1.0 - 1e-4, 1.0 + 1e-4);
        Assert.InRange(TestTiff.U32(asn, 16) / 1e6, e2 - 1e-4, e2 + 1e-4);

        // EXIF carried from the reference NEF
        Assert.True(ifd0.Tags.ContainsKey(0x8769), "ExifIFD pointer missing");
        var exif = TestTiff.ParseIfd(file, TestTiff.U32(ifd0.Tags[0x8769].Value, 0));
        Assert.Equal(3200, TestTiff.U16(exif.Tags[0x8827].Value, 0)); // ISOSpeedRatings
        Assert.True(exif.Tags.ContainsKey(0x829A), "ExposureTime missing");
        Assert.True(exif.Tags.ContainsKey(0x920A), "FocalLength missing");
        Assert.True(exif.Tags.ContainsKey(0x927C), "MakerNote not preserved");

        // Lens opcodes (NEF vignette ON → OpcodeList2, Z8 → WarpRectilinear OpcodeList3)
        Assert.True(raw.Tags.TryGetValue(51009, out var ol2), "OpcodeList2 missing");
        Assert.True(ol2.Value.Length >= 60, "OpcodeList2 too short");
        Assert.Equal(1u, TestTiff.Be32(ol2.Value, 0));
        Assert.Equal(3u, TestTiff.Be32(ol2.Value, 4));
        Assert.True(raw.Tags.TryGetValue(51022, out var ol3), "OpcodeList3 missing");
        Assert.True(ol3.Value.Length >= 160, "OpcodeList3 too short");
        Assert.Equal(1u, TestTiff.Be32(ol3.Value, 0));
        Assert.Equal(1u, TestTiff.Be32(ol3.Value, 4));

        // ≥2 JPEG previews (IFD0 thumbnail + SubIFD previews from the reference NEF)
        int previews = 0;
        TestTiff.CountJpegPreviews(file, ifd0, ref previews);
        uint subCount = ifd0.Tags[330].Count;
        for (int i = 1; i < subCount; i++)
            TestTiff.CountJpegPreviews(file, TestTiff.ParseIfd(file, TestTiff.U32(v330, i * 4)), ref previews);
        Assert.True(previews >= 2, $"expected ≥2 JPEG preview IFDs, found {previews}");

        // Pixel decode: one center tile through the LJ decoder → non-trivial statistics
        int tw = TestTiff.U16(raw.Tags[322].Value, 0), th = TestTiff.U16(raw.Tags[323].Value, 0);
        int tilesX = (16560 + tw - 1) / tw, tilesY = (11040 + th - 1) / th;
        int centerIndex = (tilesY / 2) * tilesX + tilesX / 2;
        uint off = TestTiff.U32(raw.Tags[324].Value, centerIndex * 4);
        uint len = TestTiff.U32(raw.Tags[325].Value, centerIndex * 4);
        var strip = new byte[len];
        Array.Copy(file, (long)off, strip, 0, len);
        var tile = new ushort[tw * th * 3];
        long decoded = DngLjpegNative.DecodeStream(strip, tile, 0, out uint sw, out uint sh, out uint sc);
        Assert.Equal((long)tw * th * 3, decoded);
        Assert.Equal((uint)tw, sw);
        Assert.Equal((uint)th, sh);
        Assert.Equal(3u, sc);
        int min = tile.Min(), max = tile.Max();
        double mean = tile.Average(v => (double)v);
        Ev($"中心 tile[{tilesY / 2},{tilesX / 2}] 解码 {decoded} 样本: min={min} max={max} mean={mean:F1}");
        Assert.True(min < max, "center tile decodes to a constant plane");
        Assert.InRange(mean, 100, 65000); // non-trivial, neither black nor clipped white

        // LibRaw metadata probe (evidence only, no assert): this LibRaw fork REJECTS
        // every 3-sample LinearRaw DNG with -2 — measured 2026-10-14 on the backend's
        // own tifffile products too (probe1/roi_sample), so it is a reader limitation,
        // not a product defect. Pixel verification above goes through the LJ decoder.
        Ev($"LibRaw {LibRawNative.GetVersion()}");
        IntPtr h = LibRawNative.OpenRaw(dngPath, out int error);
        if (h != IntPtr.Zero)
        {
            try
            {
                Ev($"LibRaw 回读: {LibRawNative.GetWidth(h)}×{LibRawNative.GetHeight(h)} " +
                   $"{LibRawNative.GetMake(h)} {LibRawNative.GetModel(h)}");
            }
            finally
            {
                LibRawNative.Close(h);
            }
        }
        else
        {
            Ev($"LibRaw 回读: 不支持 LinearRaw DNG（error={error}，后端原生产物同样被拒）");
        }
    }

    // ---- Step 3b: stop mid-run kills the backend tree, keeps temp ----

    [Fact]
    [Trait("Category", "E2E")]
    public async Task StopMidRun_CancelsWithin5Seconds_NoBackendLeftover_TempKept()
    {
        if (!Enabled)
        {
            Ev("skipped (set JSR4Z8_E2E=1 to run)");
            return;
        }

        var (group, ptc, dark, engine, capture) = PrepareRun(SamplesDir());
        using var cts = new CancellationTokenSource();
        Task<TaskRunOutcome> run = engine
            .RunAsync(group, ptc, dark, new AppSettings(), cts.Token, capture.OnEvent);
        await Task.Delay(TimeSpan.FromSeconds(30)); // reach alignment phase
        var stopWatch = Stopwatch.StartNew();
        cts.Cancel();
        TaskRunOutcome outcome = await run;
        stopWatch.Stop();
        ReportOutcome(outcome, capture, "中途停止");
        Ev($"停止时延: {stopWatch.Elapsed.TotalSeconds:F1}s（要求 ≤5s）");

        Assert.False(outcome.Success);
        Assert.True(outcome.Cancelled, outcome.Message);
        Assert.StartsWith("已停止", outcome.Message);
        Assert.True(stopWatch.Elapsed <= TimeSpan.FromSeconds(5.5),
            $"停止耗时 {stopWatch.Elapsed.TotalSeconds:F1}s > 5s");
        Assert.NotNull(outcome.DiagnosticDir);
        Assert.True(Directory.Exists(outcome.DiagnosticDir!), "停止后临时目录应保留");

        // No backend process leftover (tree kill through the harness; the forced
        // processes vanish from tasklist within seconds of driver teardown)
        double goneBackend = await WaitProcessGoneSeconds("jsr_backend.exe", TimeSpan.FromSeconds(15));
        Assert.True(goneBackend >= 0, "jsr_backend.exe 停止后 15s 内未退出");
        double goneBridge = await WaitProcessGoneSeconds("Jiangtherapee Bridge.exe", TimeSpan.FromSeconds(15));
        Assert.True(goneBridge >= 0, "Jiangtherapee Bridge.exe 停止后 15s 内未退出");
        Ev($"tasklist: 无 jsr_backend.exe / Jiangtherapee Bridge.exe 残留" +
           $"（{goneBackend:F1}s / {goneBridge:F1}s 内消失，≤15s 轮询）");
    }

    // ---- Step 3a: Chinese + spaces directory path full run ----

    [Fact]
    [Trait("Category", "E2E")]
    public async Task ChinesePathCopy_FullRun_Succeeds()
    {
        if (!Enabled)
        {
            Ev("skipped (set JSR4Z8_E2E=1 to run)");
            return;
        }

        string root = Path.Combine(RepoRoot(), "tmp_e2e", "中文 路径 测试");
        string samplesCopy = Path.Combine(root, "样张 目录");
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (DirectoryNotFoundException) { }
        Directory.CreateDirectory(samplesCopy);
        foreach (string name in FramesFor(SamplesDir()))
            File.Copy(name, Path.Combine(samplesCopy, Path.GetFileName(name)));
        Ev($"中文+空格路径副本: {samplesCopy}");

        var (group, ptc, dark, engine, capture) = PrepareRun(samplesCopy);
        TaskRunOutcome outcome = await engine
            .RunAsync(group, ptc, dark, new AppSettings(), CancellationToken.None, capture.OnEvent);
        ReportOutcome(outcome, capture, "中文路径全流程");

        Assert.True(outcome.Success, outcome.Message);
        string expected = Path.Combine(samplesCopy, "JSR_DSC_7576.dng");
        Assert.Equal(Path.GetFullPath(expected), Path.GetFullPath(outcome.OutputPath!));
        Assert.True(File.Exists(expected), $"中文路径产物缺失: {expected}");
        Ev($"中文路径产物: {new FileInfo(expected).Length / 1024.0 / 1024.0:F1} MB");
    }

    // ---- Step 4 capture: raw ASSISTANT_* stdout stream of one full success run ----

    [Fact]
    [Trait("Category", "E2E")]
    public async Task SuccessStdoutCapture_SpawnsBridgeDirectly_SavesRawEnvelopeStream()
    {
        if (!Enabled)
        {
            Ev("skipped (set JSR4Z8_E2E=1 to run)");
            return;
        }

        var (group, ptc, dark, _, _) = PrepareRun(SamplesDir());
        var (bridge, resources) = ResolveExtractedBackend();
        string workDir = Path.Combine(ArtifactsDir(), "capture");
        try { Directory.Delete(workDir, recursive: true); } catch (DirectoryNotFoundException) { }
        Directory.CreateDirectory(Path.Combine(workDir, "out"));
        Directory.CreateDirectory(Path.Combine(workDir, "cfg"));
        string outDir = Path.Combine(workDir, "out");
        string requestPath = Path.Combine(workDir, "request.json");
        string stdoutPath = Path.Combine(workDir, "success.log");
        string stderrPath = Path.Combine(workDir, "success.stderr.log");

        // The same request the queue builds (UTF-8 no BOM, prefix=cap1).
        string request = BackendRequest.BuildJson(group, ptc, dark, outputDir: outDir, prefix: "cap1");
        File.WriteAllText(requestPath, request, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // Raw tap: spawn the bridge exactly as BackendRunner does, but persist every
        // stdout/stderr line VERBATIM (CRLF) instead of through the parser — this is
        // the ASSISTANT_* envelope stream the success.log fixture needs.
        var psi = new ProcessStartInfo
        {
            FileName = bridge,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("--assistant-jsr-job");
        psi.ArgumentList.Add(requestPath);
        psi.ArgumentList.Add(resources);
        psi.ArgumentList.Add(Path.Combine(workDir, "cfg"));

        var clock = Stopwatch.StartNew();
        using var process = Process.Start(psi)!;
        using var stdoutFile = new StreamWriter(stdoutPath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        using var stderrFile = new StreamWriter(stderrPath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        Task pumpOut = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
                stdoutFile.WriteLine(line);
        });
        Task pumpErr = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
                stderrFile.WriteLine(line);
        });
        await Task.WhenAll(pumpOut, pumpErr);
        await process.WaitForExitAsync(CancellationToken.None);
        clock.Stop();
        Ev($"抓取运行: bridge 退出码 {process.ExitCode}, 耗时 {clock.Elapsed.TotalSeconds:F1}s, " +
           $"stdout {new FileInfo(stdoutPath).Length / 1024.0 / 1024.0:F2} MB");
        Assert.Equal(0, process.ExitCode);

        // Pre-commit validation of the captured stream: single START, single complete RESULT.
        string[] lines = File.ReadAllLines(stdoutPath);
        int starts = 0, results = 0, v12 = 0, eta = 0;
        JsrResult? resultJson = null;
        foreach (string line in lines)
        {
            switch (ProtocolParser.ParseLine(line))
            {
                case Start:
                    starts++;
                    break;
                case Result r:
                    results++;
                    resultJson = r.Json;
                    break;
                case Log l when l.Progress is not null:
                    if (l.Progress.Stage == DngExportStage) eta++;
                    else v12++;
                    break;
            }
        }
        Ev($"抓取流: {lines.Length} 行, START {starts}, RESULT {results}, " +
           $"V12 进度 {v12}, ETA 进度 {eta}");
        Assert.Equal(1, starts);
        Assert.Equal(1, results);
        Assert.NotNull(resultJson);
        Assert.Equal("complete", resultJson!.State);
        Assert.Equal(0, resultJson.ExitCode);
        Assert.True(v12 > 0 && eta > 0);

        // The backend really produced its DNG (kept for reference; not finalized here).
        string? product = TaskQueueRunner.FindNewestProductDng(outDir, "cap1");
        Assert.True(product is not null, "抓取运行未产出后端 DNG");
        Ev($"抓取运行后端产物: {product} ({new FileInfo(product!).Length / 1024.0 / 1024.0:F1} MB)");
        Ev($"success.log 待回填: {stdoutPath}");
    }

}
