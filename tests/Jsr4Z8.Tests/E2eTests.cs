using System.Diagnostics;
using System.Text;
using Jsr4Z8.Core.Backend;
using Jsr4Z8.Core.Dark;
using Jsr4Z8.Core.Model;
using Jsr4Z8.Core.Native;
using Jsr4Z8.Core.Payload;
using Jsr4Z8.Core.Ptc;
using Jsr4Z8.Core.Queue;
using Xunit;

namespace Jsr4Z8.Tests;

/// <summary>
/// 端到端验收 (E2E): the REAL chain on the Windows host — real samples, the
/// real extracted backend payload, a real GPU run and the real finalizer
/// product. Opt-in via environment variable JSR4Z8_E2E=1; with it unset every
/// test returns early and passes (normal CI stays fast and GPU-free). With it
/// set, the tests of this class run sequentially (single xUnit class =
/// single collection): four full ~5.5 min runs — one per sample group
/// (lossless / HE / HE* / bracketed) — plus one 30 s cancel, ~24 min total.
///
/// Payload note: the queue's payload phase is pointed at the payload the
/// release exe's --selftest extracted into %LOCALAPPDATA%\JSR4Z8\backend\
/// &lt;hash&gt;\ via <see cref="TaskQueueRunner.ResolveBackendOverride"/> — the
/// dev/test Core assembly carries no embedded payload resource (that pair is
/// embedded only by the release build). The embedded-payload path itself —
/// extraction fast-path and self-heal — is exercised through the release exe's
/// --selftest.
/// </summary>
public class E2eTests
{
    private const string EnvVar = "JSR4Z8_E2E";
    private const string RegenBaselinesVar = "JSR4Z8_E2E_REGEN_BASELINES";
    private const string DngExportStage = "DNG 导出"; // ProtocolParser's mapped ETA stage label

    private static bool Enabled => Environment.GetEnvironmentVariable(EnvVar) == "1";

    /// <summary>基线重建模式（有意变更后重建基线并随变更提交；默认断言模式拦无意识漂移）。</summary>
    private static bool RegenBaselines => Environment.GetEnvironmentVariable(RegenBaselinesVar) == "1";

    /// <summary>四格式 FullRun 的最终 DNG 哈希基线（「我们的输出 vs 原引擎输出」逐字节谓词）。</summary>
    private static string BaselinePath => Path.Combine(
        RepoRoot(), "tests", "Jsr4Z8.Tests", "fixtures", "e2e", "baselines.json");

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

    private static string[] FramesFor(string samplesDir) => FramesFor(samplesDir, "lossless", 7576);

    /// <summary>七帧组选取（等间隔编号）；首帧即基准帧（TaskGroup.ReferencePath = FramePaths[0]）。</summary>
    private static string[] FramesFor(string samplesDir, string groupDir, int firstFrame) =>
        Enumerable.Range(firstFrame, 7).Select(i => Path.Combine(samplesDir, "test_frames", groupDir, $"DSC_{i}.NEF")).ToArray();

    /// <summary>The payload the release selftest extracted; asserts the operator ran it first.
    /// Returns the direct-invoke engine executable (resources\jsr_backend_runtime\jsr_backend.exe
    /// — the pre-extracted runtime tree's root copy).</summary>
    private static string ResolveExtractedBackend()
    {
        Assert.True(Directory.Exists(PayloadManager.DefaultBackendRoot),
            $"后端载荷根不存在: {PayloadManager.DefaultBackendRoot}（先运行 out\\JSR4Z8_v<版本>.exe --selftest 释放载荷）");
        string[] candidates = Directory.GetDirectories(PayloadManager.DefaultBackendRoot)
            .Where(d => File.Exists(Path.Combine(d, TaskQueueRunner.EngineRelativePath)))
            .ToArray();
        string backend = Assert.Single(candidates);
        return Path.Combine(backend, TaskQueueRunner.EngineRelativePath);
    }

    /// <summary>Queue events collected during one run (background threads → lock).</summary>
    private sealed class RunCapture
    {
        public readonly List<string> Lines = new();
        public int V12ProgressLines;
        public int EtaProgressLines;
        public int MaxPercent = -1;
        /// <summary>分协议流单调性：V12 流与 ETA 流各自不得回退（跨流尾部 100.0→96 为上游协议
        /// 固有形状——ETA 流先到 100.0、V12 流再 96→100 收尾——不计入；线程化对齐乱序发射
        /// 进度行会在此暴露，见方案 docs/superpowers/notes/2026-10-28-pair-parallel-plan.md）。</summary>
        public int V12PercentRegressions;
        public int EtaPercentRegressions;
        private int _lastV12 = -1;
        private double _lastEta = -1;
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
                        if (p.Stage == DngExportStage)
                        {
                            EtaProgressLines++;
                            if (p.Percent < _lastEta)
                                EtaPercentRegressions++;
                            _lastEta = p.Percent;
                        }
                        else
                        {
                            V12ProgressLines++;
                            if (p.Percent < _lastV12)
                                V12PercentRegressions++;
                            _lastV12 = p.Percent;
                        }
                        MaxPercent = Math.Max(MaxPercent, p.Percent);
                    }
                    break;
            }
        }
    }

    private static (TaskGroup Group, PtcMatch Ptc, DarkMatch Dark, TaskQueueRunner Engine, RunCapture Capture) PrepareRun(
        string[] frames)
    {
        var group = new TaskGroup(frames);
        var validation = TaskGroupValidator.Validate(group);
        Assert.True(validation.CanRun, string.Join("；", validation.Issues.Select(i => i.MessageZh)));
        Assert.NotNull(validation.Ptc);
        PtcMatch ptc = validation.Ptc!;
        Ev($"校验: Z8 · ISO{validation.Dark!.Iso:0} · 满阱 {ptc.FullwellE:0.0} e⁻（{(ptc.Exact ? "表值" : ptc.Source)}）" +
           $" · 黑场预置匹配（black {validation.Dark.BlackDn} DN · RN {validation.Dark.ReadNoiseDn} DN）");

        var engine = new TaskQueueRunner
        {
            ResolveBackendOverride = _ => new TaskQueueRunner.BackendPayload(ResolveExtractedBackend()),
        };
        return (group, ptc, validation.Dark!, engine, new RunCapture());
    }

    private static void ReportOutcome(TaskRunOutcome outcome, RunCapture capture, string label)
    {
        string state = outcome.Success ? "Success" : outcome.Cancelled ? "Cancelled" : "Failed";
        Ev($"{label}: {state}, 耗时 {outcome.Elapsed.TotalSeconds:F1}s, 消息「{outcome.Message}」, " +
           $"输出 {outcome.OutputPath ?? "-"}");
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
    /// measured: alive right at the 已停止 card, gone within seconds. The
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

    // ---- full runs: one per sample group (compression format × bracketing) ----

    /// <summary>LibRaw 直读基准帧：钉死压缩格式（无损=3 / HE=13 / HE*=14），并取 ISO 与
    /// AsShotNeutral 期望值（=1/cam_mul，终化器写入值的同源推导）。</summary>
    private static (int Iso, double AsnR, double AsnB) ProbeReference(string nefPath, int expectedCompression)
    {
        IntPtr h = LibRawNative.OpenRaw(nefPath, out int error);
        Assert.True(h != IntPtr.Zero, $"LibRaw 打开失败: {nefPath} (error={error})");
        try
        {
            Assert.Equal(expectedCompression, LibRawNative.GetNefCompression(h));
            float[] cam = LibRawNative.GetCamMul(h);
            return ((int)LibRawNative.GetIso(h), 1.0 / cam[0], 1.0 / cam[2]);
        }
        finally
        {
            LibRawNative.Close(h);
        }
    }

    [Theory]
    [Trait("Category", "E2E")]
    [InlineData("lossless", 7576, 3)]            // 无损压缩
    [InlineData("he_compressed", 7652, 13)]      // 高压缩 HE
    [InlineData("he_star_compressed", 7863, 14)] // 高压缩★ HE*
    [InlineData("unequal_exposure", 7855, 3)]    // 曝光包围（无损压缩；中间曝光 1/100s 为基准帧）
    public async Task FullRun_NoDarkFile_PresetAutoMatch_ProducesValidJsrDng(
        string groupDir, int firstFrame, int nefCompression)
    {
        if (!Enabled)
        {
            Ev($"skipped ({groupDir}; set {EnvVar}=1 to run)");
            return;
        }
        string[] frames = FramesFor(SamplesDir(), groupDir, firstFrame);
        if (!frames.All(File.Exists))
        {
            Ev($"skipped (样本组缺失: {groupDir})");
            return;
        }

        (int iso, double asnR, double asnB) = ProbeReference(frames[0], nefCompression);

        var (group, ptc, dark, engine, capture) = PrepareRun(frames);
        Assert.NotNull(dark);
        Assert.Equal((double)iso, dark!.Iso);
        TaskRunOutcome outcome = await engine
            .RunAsync(group, ptc, dark, CancellationToken.None, capture.OnEvent);
        ReportOutcome(outcome, capture, $"全流程·预置黑场·{groupDir}");

        Assert.True(outcome.Success, outcome.Message);
        // 分流单调（V12/ETA 各自不得回退；跨流尾部 100.0→96 为协议固有形状，不计）。
        Assert.Equal(0, capture.V12PercentRegressions);
        Assert.Equal(0, capture.EtaPercentRegressions);
        string expected = Path.Combine(Path.GetDirectoryName(frames[0])!,
            $"JSR_{Path.GetFileNameWithoutExtension(frames[0])}.dng"); // 输出目录=基准帧所在目录
        Assert.Equal(Path.GetFullPath(expected), Path.GetFullPath(outcome.OutputPath!));
        Assert.True(File.Exists(expected), $"最终 DNG 缺失: {expected}");
        AssertFinalDng(expected, new[] { asnR, 1.0, asnB }, iso);

        // 逐字节谓词：我们的构建产物 vs 原引擎输出基线（引擎/算法/标定有意变更后
        // 以 JSR4Z8_E2E_REGEN_BASELINES=1 重建并随变更提交；其余漂移即回归）。
        Ev(E2eBaselineStore.AssertOrRegen(BaselinePath, groupDir, expected, RegenBaselines));
    }

    private static void AssertFinalDng(string dngPath, double[] expectedAsShotNeutral, int expectedIso)
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

        // IFD0 identity & attribution (the tool names itself, no Artist tag)
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
        // DefaultCropOrigin is ActiveArea-relative (DNG spec) —
        // full-frame pair writes (0,0), never an absolute (24,16).
        Assert.Equal(0u, TestTiff.U32(raw.Tags[50719].Value, 0));
        Assert.Equal(0u, TestTiff.U32(raw.Tags[50719].Value, 8));
        Assert.Equal(16512u, TestTiff.U32(raw.Tags[50720].Value, 0));
        Assert.Equal(11008u, TestTiff.U32(raw.Tags[50720].Value, 8));
        Assert.Equal(9u, ifd0.Tags[50721].Count); // ColorMatrix1 (SRATIONAL×9)
        Assert.Equal(21u, TestTiff.U16(ifd0.Tags[50778].Value, 0)); // CalibrationIlluminant1 = D65
        Assert.True(ifd0.Tags.ContainsKey(50730), "BaselineExposure missing");
        Assert.True(ifd0.Tags.ContainsKey(50731), "BaselineNoise missing");
        Assert.True(ifd0.Tags.ContainsKey(50732), "BaselineSharpness missing");

        // AsShotNeutral = 1/cam_mul of the reference frame (expected values are
        // derived from the same LibRaw read the finalizer uses).
        var asn = ifd0.Tags[50728].Value;
        Assert.Equal(3u, ifd0.Tags[50728].Count);
        Assert.InRange(TestTiff.U32(asn, 0) / 1e6, expectedAsShotNeutral[0] - 1e-4, expectedAsShotNeutral[0] + 1e-4);
        Assert.InRange(TestTiff.U32(asn, 8) / 1e6, 1.0 - 1e-4, 1.0 + 1e-4);
        Assert.InRange(TestTiff.U32(asn, 16) / 1e6, expectedAsShotNeutral[2] - 1e-4, expectedAsShotNeutral[2] + 1e-4);

        // EXIF carried from the reference NEF
        Assert.True(ifd0.Tags.ContainsKey(0x8769), "ExifIFD pointer missing");
        var exif = TestTiff.ParseIfd(file, TestTiff.U32(ifd0.Tags[0x8769].Value, 0));
        Assert.Equal(expectedIso, TestTiff.U16(exif.Tags[0x8827].Value, 0)); // ISOSpeedRatings
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
        // every 3-sample LinearRaw DNG with -2 — the backend's own
        // tifffile products are rejected too, so it is a reader limitation,
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

    // ---- Step 3b: stop mid-run kills the backend tree, deletes temp ----

    [Fact]
    [Trait("Category", "E2E")]
    public async Task StopMidRun_CancelsWithin5Seconds_NoBackendLeftover_TempDeleted()
    {
        if (!Enabled)
        {
            Ev("skipped (set JSR4Z8_E2E=1 to run)");
            return;
        }

        var (group, ptc, dark, engine, capture) = PrepareRun(FramesFor(SamplesDir()));
        using var cts = new CancellationTokenSource();
        Task<TaskRunOutcome> run = engine
            .RunAsync(group, ptc, dark, cts.Token, capture.OnEvent);
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
        // 失败/停止即删: the run's job dir lived
        // under %TEMP%\Jsr4Z8\<jobId> — nothing may remain under that root.
        string tempRoot = Path.Combine(Path.GetTempPath(), "Jsr4Z8");
        Assert.True(!Directory.Exists(tempRoot) || !Directory.EnumerateDirectories(tempRoot).Any(),
            $"停止后临时目录应被删除，仍残留子目录: {tempRoot}");

        // No backend process leftover (tree kill through the harness; the forced
        // processes vanish from tasklist within seconds of driver teardown). The
        // payload carries no top-level Bridge shell and the direct
        // invocation never spawns one — jsr_backend.exe is the only image to watch.
        double goneBackend = await WaitProcessGoneSeconds("jsr_backend.exe", TimeSpan.FromSeconds(15));
        Assert.True(goneBackend >= 0, "jsr_backend.exe 停止后 15s 内未退出");
        Ev($"tasklist: 无 jsr_backend.exe 残留（{goneBackend:F1}s 内消失，≤15s 轮询）");
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
        {
            // 副本须保留 FramesFor 的子目录布局（samples 重组引入 test_frames\lossless）
            // ——拍平复制会让 PrepareRun(samplesCopy) 找不到 NEF。
            string dest = Path.Combine(samplesCopy, Path.GetRelativePath(SamplesDir(), name));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(name, dest);
        }
        Ev($"中文+空格路径副本: {samplesCopy}");

        var (group, ptc, dark, engine, capture) = PrepareRun(FramesFor(samplesCopy));
        TaskRunOutcome outcome = await engine
            .RunAsync(group, ptc, dark, CancellationToken.None, capture.OnEvent);
        ReportOutcome(outcome, capture, "中文路径全流程");

        Assert.True(outcome.Success, outcome.Message);
        string expected = Path.Combine(samplesCopy, "test_frames", "lossless", "JSR_DSC_7576.dng"); // 输出目录=基准帧所在目录
        Assert.Equal(Path.GetFullPath(expected), Path.GetFullPath(outcome.OutputPath!));
        Assert.True(File.Exists(expected), $"中文路径产物缺失: {expected}");
        Ev($"中文路径产物: {new FileInfo(expected).Length / 1024.0 / 1024.0:F1} MB");
    }
}
