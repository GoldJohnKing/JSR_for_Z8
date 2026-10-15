using Jsr4Z8.Core.Payload;
using Xunit;

namespace Jsr4Z8.Tests;

/// <summary>
/// Task 10: payload pack → extract → verify → self-heal pipeline, using the SAME
/// PayloadPacker code the release packer tool (tools/pack-payload) uses. The tests
/// build a minimal fake payload (2 packed files) in temp dirs — the real ~500 MB
/// backend payload is never committed or embedded into test builds (Task 15 packs
/// it at release time). Windows-host tests: the manager writes/renames/deletes real
/// directories.
/// </summary>
public class PayloadTests : IClassFixture<TestTemp>
{
    private readonly TestTemp _temp;

    public PayloadTests(TestTemp temp) => _temp = temp;

    private sealed record FakePayload(string SrcDir, string OutDir, PayloadManifest Manifest)
    {
        public string PayloadBin => Path.Combine(OutDir, PayloadPacker.PayloadFileName);
        public string ManifestJson => Path.Combine(OutDir, PayloadPacker.ManifestFileName);
    }

    private sealed class CollectingProgress : IProgress<double>
    {
        public List<double> Reports { get; } = new();
        public void Report(double value) => Reports.Add(value);
    }

    /// <summary>
    /// 2-packed-file fake backend distribution: the bridge exe and one nested resources
    /// file (exercises recursion + subdirectory creation) — plus excluded payload
    /// artifacts (K14 engine, Tcl data, cv2 ffmpeg DLL, z8_presets preset, top-level
    /// notices) and junk files the packer must NOT include (README, 使用说明,
    /// FILES-SHA256.json, ThirdPartySources/).
    /// </summary>
    private string MakeFakeTree()
    {
        string root = _temp.NewDir();
        Directory.CreateDirectory(Path.Combine(root, "resources", "licenses"));
        Directory.CreateDirectory(Path.Combine(root, "resources", "_internal", "_tcl_data"));
        Directory.CreateDirectory(Path.Combine(root, "resources", "_internal", "cv2"));
        Directory.CreateDirectory(Path.Combine(root, "resources", "z8_presets"));
        Directory.CreateDirectory(Path.Combine(root, "ThirdPartySources"));

        var rng = new Random(20261013);
        byte[] exe = new byte[64 * 1024];
        rng.NextBytes(exe);
        File.WriteAllBytes(Path.Combine(root, "Jiangtherapee Bridge.exe"), exe);
        File.WriteAllText(Path.Combine(root, "resources", "licenses", "notice.txt"), "fake third-party license text");
        File.WriteAllText(Path.Combine(root, "THIRD_PARTY_NOTICES.md"), "# fake notices");

        // Excluded from the payload by the packer's exclusion list (user ruling 2026-10-15):
        // present in the fake tree so every Pack call exercises the filter.
        File.WriteAllText(Path.Combine(root, "resources", "jsr_v98_k14_backend.exe"), "k14 engine");
        File.WriteAllText(Path.Combine(root, "resources", "_internal", "_tcl_data", "x.enc"), "tcl data");
        File.WriteAllText(Path.Combine(root, "resources", "_internal", "cv2", "opencv_videoio_ffmpeg500_64.dll"), "video ffmpeg dll");
        File.WriteAllText(Path.Combine(root, "resources", "z8_presets", "Z8_0_ISO000064.dng"), "preset-dark dng");

        // Everything below must be excluded from the payload.
        File.WriteAllText(Path.Combine(root, "README.md"), "readme");
        File.WriteAllText(Path.Combine(root, "DeepSeek助手使用说明.md"), "说明");
        File.WriteAllText(Path.Combine(root, "FILES-SHA256.json"), "{}");
        File.WriteAllText(Path.Combine(root, "ThirdPartySources", "src.zip"), "PK");

        return root;
    }

    private string TempDir() => _temp.NewDir();

    private FakePayload PackFake() => PackTree(MakeFakeTree());

    /// <summary>Packs an already-built source tree (e.g. a mutated variant of the <see cref="MakeFakeTree"/> output) into a fresh out dir.</summary>
    private FakePayload PackTree(string srcDir)
    {
        string outDir = TempDir();
        PayloadPackResult result = PayloadPacker.Pack(srcDir, outDir);
        using FileStream manifestStream = File.OpenRead(result.ManifestPath);
        return new FakePayload(srcDir, outDir, PayloadPacker.ReadManifestJson(manifestStream));
    }

    /// <summary>Fresh PayloadManager over the fake payload pair (simulates a new process launch — no session cache).</summary>
    private static PayloadManager NewManager(string baseRoot, FakePayload payload) =>
        new(baseRoot,
            () => File.OpenRead(payload.PayloadBin),
            () =>
            {
                using FileStream s = File.OpenRead(payload.ManifestJson);
                return PayloadPacker.ReadManifestJson(s);
            });

    [Fact]
    public void Pack_ScansOnlyBackendSources_AndWritesPayloadPair()
    {
        FakePayload payload = PackFake();

        Assert.True(File.Exists(payload.PayloadBin), "payload.bin written");
        Assert.True(File.Exists(payload.ManifestJson), "payload.manifest.json written");

        Assert.Equal(2, payload.Manifest.Files.Count);
        Assert.Equal(
            new[] { "Jiangtherapee Bridge.exe", "resources/licenses/notice.txt" }.OrderBy(p => p),
            payload.Manifest.Files.Select(f => f.Path.Replace('\\', '/')).OrderBy(p => p));
        // The notices are no longer packed (user ruling 2026-10-15): licenses ship inside resources/.
        Assert.DoesNotContain(
            payload.Manifest.Files,
            f => f.Path.Replace('\\', '/') == "THIRD_PARTY_NOTICES.md");

        Assert.Matches("^[0-9a-f]{64}$", payload.Manifest.Hash);
        foreach (PayloadManifestEntry entry in payload.Manifest.Files)
        {
            Assert.Matches("^[0-9a-f]{64}$", entry.Sha256);
            Assert.Equal(new FileInfo(Path.Combine(payload.SrcDir, entry.Path)).Length, entry.Length);
        }
    }

    [Fact]
    public void Pack_ExcludesUnneededBackendFiles()
    {
        string root = MakeFakeTree();
        // The base tree already carries jsr_v98_k14_backend.exe, _tcl_data/, the cv2 ffmpeg
        // DLL, z8_presets/ and THIRD_PARTY_NOTICES.md; add the remaining exclusion-list entries.
        Directory.CreateDirectory(Path.Combine(root, "resources", "_internal", "_tk_data"));
        Directory.CreateDirectory(Path.Combine(root, "resources", "_internal", "wgpu", "__pyinstaller"));
        File.WriteAllText(Path.Combine(root, "resources", "jsr_fast_k14_backend.exe"), "fast k14 engine");
        File.WriteAllText(Path.Combine(root, "resources", "_internal", "tcl86t.dll"), "tcl");
        File.WriteAllText(Path.Combine(root, "resources", "_internal", "tk86t.dll"), "tk");
        File.WriteAllText(Path.Combine(root, "resources", "_internal", "_tk_data", "y.enc"), "tk data");
        File.WriteAllText(Path.Combine(root, "resources", "_internal", "wgpu", "__pyinstaller", "conftest.py"), "# test residue");
        // Kept siblings: the exclusions are surgical, never subtree-wide.
        File.WriteAllText(Path.Combine(root, "resources", "jsr_backend.exe"), "the engine the assistant actually spawns");
        File.WriteAllText(Path.Combine(root, "resources", "_internal", "cv2", "__init__.py"), "cv2 stays");
        File.WriteAllText(Path.Combine(root, "resources", "_internal", "keep.dll"), "_internal stays");

        FakePayload payload = PackTree(root);
        string[] paths = payload.Manifest.Files.Select(f => f.Path.Replace('\\', '/')).ToArray();

        string[] excluded =
        {
            "THIRD_PARTY_NOTICES.md",
            "resources/jsr_v98_k14_backend.exe",
            "resources/jsr_fast_k14_backend.exe",
            "resources/_internal/_tcl_data/x.enc",
            "resources/_internal/_tk_data/y.enc",
            "resources/_internal/tcl86t.dll",
            "resources/_internal/tk86t.dll",
            "resources/_internal/cv2/opencv_videoio_ffmpeg500_64.dll",
            "resources/_internal/wgpu/__pyinstaller/conftest.py",
            "resources/z8_presets/Z8_0_ISO000064.dng",
        };
        foreach (string path in excluded)
            Assert.False(paths.Contains(path), $"{path} 必须被排除出载荷");

        string[] kept =
        {
            "Jiangtherapee Bridge.exe",
            "resources/licenses/notice.txt",
            "resources/jsr_backend.exe",
            "resources/_internal/cv2/__init__.py",
            "resources/_internal/keep.dll",
        };
        Assert.Equal(kept.Length, paths.Length); // nothing else snuck in or out
        foreach (string path in kept)
            Assert.Contains(path, paths);

        // payload.bin mirrors the manifest: excluded bodies have no headers either.
        using FileStream bin = File.OpenRead(payload.PayloadBin);
        Assert.Equal(
            paths.OrderBy(p => p, StringComparer.Ordinal),
            PayloadPacker.ReadArchiveIndex(bin).Select(e => e.Path.Replace('\\', '/')).ToArray());

        // Seam contract: either separator style, case-insensitive match (Windows source trees).
        Assert.True(PayloadPacker.IsExcluded("resources\\_internal\\_Tcl_Data\\x.enc"));
        Assert.False(PayloadPacker.IsExcluded("resources/_internal/cv2/opencv_world4700.dll"));
    }

    [Fact]
    public void Pack_IsDeterministic_SameInputSameBytes()
    {
        string src = MakeFakeTree();
        string out1 = TempDir();
        string out2 = TempDir();
        PayloadPacker.Pack(src, out1);
        PayloadPacker.Pack(src, out2);

        Assert.Equal(
            File.ReadAllBytes(Path.Combine(out1, PayloadPacker.PayloadFileName)),
            File.ReadAllBytes(Path.Combine(out2, PayloadPacker.PayloadFileName)));
        Assert.Equal(
            File.ReadAllBytes(Path.Combine(out1, PayloadPacker.ManifestFileName)),
            File.ReadAllBytes(Path.Combine(out2, PayloadPacker.ManifestFileName)));
    }

    [Fact]
    public void EnsureExtracted_RoundTrip_AllFilesByteIdentical()
    {
        FakePayload payload = PackFake();
        string baseRoot = TempDir();

        string backend = NewManager(baseRoot, payload).EnsureExtracted();

        // Target dir is the backend root: base\<manifest hash first 16 hex chars>.
        Assert.Equal(Path.Combine(baseRoot, payload.Manifest.Hash[..16]), backend);
        Assert.True(File.Exists(Path.Combine(backend, "Jiangtherapee Bridge.exe")));
        Assert.False(File.Exists(Path.Combine(backend, PayloadManager.IncompleteMarkerName)), "marker removed after successful extraction");
        foreach (PayloadManifestEntry entry in payload.Manifest.Files)
        {
            Assert.Equal(
                File.ReadAllBytes(Path.Combine(payload.SrcDir, entry.Path)),
                File.ReadAllBytes(Path.Combine(backend, entry.Path)));
        }
    }

    [Fact]
    public void EnsureExtracted_RenameTransientlyLocked_RetriesThenSucceeds()
    {
        FakePayload payload = PackFake();
        string baseRoot = TempDir();
        var manager = NewManager(baseRoot, payload);
        int attempts = 0;
        manager.DirectoryMoveHook = (source, dest) =>
        {
            attempts++;
            if (attempts == 1) // AV scanner / indexer briefly holds the freshly-written tmp dir
                throw new IOException("The process cannot access the file because it is being used by another process.");
            Directory.Move(source, dest);
        };

        string backend = manager.EnsureExtracted();

        Assert.Equal(2, attempts); // one transient failure, the retry landed the rename
        Assert.Equal(Path.Combine(baseRoot, payload.Manifest.Hash[..16]), backend);
        Assert.False(Directory.Exists(Path.Combine(baseRoot, payload.Manifest.Hash[..16] + ".tmp-" + Environment.ProcessId)), "tmp dir renamed away");
        foreach (PayloadManifestEntry entry in payload.Manifest.Files)
            Assert.Equal(
                File.ReadAllBytes(Path.Combine(payload.SrcDir, entry.Path)),
                File.ReadAllBytes(Path.Combine(backend, entry.Path)));
    }

    [Fact]
    public void EnsureExtracted_RenameLockedForGood_ThrowsAfterRetryBudget()
    {
        FakePayload payload = PackFake();
        string baseRoot = TempDir();
        var manager = NewManager(baseRoot, payload);
        int attempts = 0;
        manager.DirectoryMoveHook = (_, _) =>
        {
            attempts++;
            throw new UnauthorizedAccessException("locked for good");
        };

        Assert.Throws<UnauthorizedAccessException>(() => manager.EnsureExtracted());
        Assert.Equal(PayloadManager.FsLockRetryAttempts, attempts); // full budget spent before giving up
    }

    [Fact]
    public void EnsureExtracted_MissingFile_SelfHealsFully()
    {
        FakePayload payload = PackFake();
        string baseRoot = TempDir();
        string backend = NewManager(baseRoot, payload).EnsureExtracted();

        File.Delete(Path.Combine(backend, "resources", "licenses", "notice.txt"));

        // Fresh instance = next launch: must detect the gap, wipe the dir and re-extract.
        var second = NewManager(baseRoot, payload);
        string healed = second.EnsureExtracted();
        Assert.Equal(backend, healed);
        Assert.Equal(1, second.ManifestVerifyPasses); // the failed validation did run a full pass
        foreach (PayloadManifestEntry entry in payload.Manifest.Files)
            Assert.Equal(
                File.ReadAllBytes(Path.Combine(payload.SrcDir, entry.Path)),
                File.ReadAllBytes(Path.Combine(backend, entry.Path)));
    }

    [Fact]
    public void EnsureExtracted_CorruptedFileByte_SelfHealsFully()
    {
        FakePayload payload = PackFake();
        string baseRoot = TempDir();
        string backend = NewManager(baseRoot, payload).EnsureExtracted();

        // Flip one byte, keep the length, so only the SHA-256 check can catch it.
        string victim = Path.Combine(backend, "Jiangtherapee Bridge.exe");
        byte[] bytes = File.ReadAllBytes(victim);
        bytes[100] ^= 0xFF;
        File.WriteAllBytes(victim, bytes);

        string healed = NewManager(baseRoot, payload).EnsureExtracted();
        Assert.Equal(backend, healed);
        Assert.Equal(
            File.ReadAllBytes(Path.Combine(payload.SrcDir, "Jiangtherapee Bridge.exe")),
            File.ReadAllBytes(victim));
    }

    [Fact]
    public void EnsureExtracted_SecondCallInSameSession_SkipsFullRehash()
    {
        FakePayload payload = PackFake();
        string baseRoot = TempDir();
        var manager = NewManager(baseRoot, payload);

        string first = manager.EnsureExtracted();
        Assert.Equal(0, manager.ManifestVerifyPasses); // first call extracts, no existing-dir pass

        // Same instance (same process): session cache hit, no re-hash at all.
        Assert.Equal(first, manager.EnsureExtracted());
        Assert.Equal(0, manager.ManifestVerifyPasses);

        // Fresh instance (next launch): one full verify pass, succeeds, returns the same dir.
        var nextLaunch = NewManager(baseRoot, payload);
        Assert.Equal(first, nextLaunch.EnsureExtracted());
        Assert.Equal(1, nextLaunch.ManifestVerifyPasses);
        // And that instance caches too: a further call must not re-hash.
        Assert.Equal(first, nextLaunch.EnsureExtracted());
        Assert.Equal(1, nextLaunch.ManifestVerifyPasses);
    }

    [Fact]
    public void EnsureExtracted_StaleIncompleteMarker_ForcesFullReextract()
    {
        FakePayload payload = PackFake();
        string baseRoot = TempDir();
        string backend = NewManager(baseRoot, payload).EnsureExtracted();

        // Simulate a crash that left the target dir with an .incomplete marker:
        // never trusted regardless of file contents.
        File.WriteAllText(Path.Combine(backend, PayloadManager.IncompleteMarkerName), "");

        string healed = NewManager(baseRoot, payload).EnsureExtracted();
        Assert.Equal(backend, healed);
        Assert.False(File.Exists(Path.Combine(backend, PayloadManager.IncompleteMarkerName)), "self-heal wiped the dir incl. marker");
        foreach (PayloadManifestEntry entry in payload.Manifest.Files)
            Assert.Equal(
                File.ReadAllBytes(Path.Combine(payload.SrcDir, entry.Path)),
                File.ReadAllBytes(Path.Combine(backend, entry.Path)));
    }

    [Fact]
    public void EnsureExtracted_RemovesCrashedTmpDirs()
    {
        FakePayload payload = PackFake();
        string baseRoot = TempDir();

        // Leftover from a crashed extraction of the same payload version.
        string stale = Path.Combine(baseRoot, payload.Manifest.Hash[..16] + ".tmp-999999");
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "junk.bin"), "half-written");

        string backend = NewManager(baseRoot, payload).EnsureExtracted();

        Assert.False(Directory.Exists(stale), "crashed tmp dir cleaned up");
        Assert.True(File.Exists(Path.Combine(backend, "Jiangtherapee Bridge.exe")));
    }

    [Fact]
    public void EnsureExtracted_PrunesStaleSiblingVersions()
    {
        FakePayload payloadA = PackFake();
        string baseRoot = TempDir();
        string dirA = NewManager(baseRoot, payloadA).EnsureExtracted();

        // Payload B: same fake tree with one file changed → different manifest hash → different extraction dir.
        File.WriteAllText(Path.Combine(payloadA.SrcDir, "resources", "licenses", "notice.txt"), "fake third-party license text, version 2");
        FakePayload payloadB = PackTree(payloadA.SrcDir);

        // Crashed tmp leftover of the OLD version — any sibling != current hashDir must go too.
        string oldTmp = Path.Combine(baseRoot, payloadA.Manifest.Hash[..16] + ".tmp-424242");
        Directory.CreateDirectory(oldTmp);

        string dirB = NewManager(baseRoot, payloadB).EnsureExtracted();

        Assert.Equal(Path.Combine(baseRoot, payloadB.Manifest.Hash[..16]), dirB);
        Assert.NotEqual(dirA, dirB);
        Assert.False(Directory.Exists(dirA), "previous-version dir pruned after the new version is live");
        Assert.False(Directory.Exists(oldTmp), "old-version crashed tmp leftover pruned too");
        Assert.True(File.Exists(Path.Combine(dirB, "Jiangtherapee Bridge.exe")), "current version intact after pruning");
    }

    [Fact]
    public void EnsureExtracted_ReportsMonotonicProgressReachingOne()
    {
        FakePayload payload = PackFake();
        string baseRoot = TempDir();
        var progress = new CollectingProgress();

        NewManager(baseRoot, payload).EnsureExtracted(progress);

        Assert.NotEmpty(progress.Reports);
        for (int i = 1; i < progress.Reports.Count; i++)
            Assert.True(progress.Reports[i] >= progress.Reports[i - 1] - 1e-9, $"progress went backwards at {i}");
        Assert.Equal(1.0, progress.Reports[^1], precision: 6);
    }

    // ---- EmbeddedUpdater: pack-payload's swap-in decision (manifest byte-compare) ----

    /// <summary>Writes a fake embedded pair — opaque bytes, because <see cref="EmbeddedUpdater"/>
    /// only byte-compares the manifest and copies the pair; it never parses them.</summary>
    private static void WritePair(string dir, byte[] manifest, byte[] bin)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, PayloadPacker.ManifestFileName), manifest);
        File.WriteAllBytes(Path.Combine(dir, PayloadPacker.PayloadFileName), bin);
    }

    [Fact]
    public void Pack_UnchangedManifest_KeepsExistingEmbedded()
    {
        byte[] manifest = { 0x7B, 0x22, 0x76, 0x65, 0x72, 0x73, 0x69, 0x6F, 0x6E, 0x22 }; // {"version"
        byte[] bin = { 1, 2, 3 };
        string embedded = TempDir();
        WritePair(embedded, manifest, bin);

        // Fresh pack of the SAME source: identical manifest bytes (a deterministic packer
        // then also yields an identical bin; the differing bin here documents that the
        // keep/replace decision is manifest-driven, not bin-driven).
        string temp = TempDir();
        WritePair(temp, manifest, new byte[] { 9, 9, 9, 9 });

        bool replaced = EmbeddedUpdater.Apply(embedded, temp);

        Assert.False(replaced);
        Assert.False(Directory.Exists(temp), "keep deletes the temp pack dir");
        Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(embedded, PayloadPacker.ManifestFileName)));
        Assert.Equal(bin, File.ReadAllBytes(Path.Combine(embedded, PayloadPacker.PayloadFileName)));
    }

    [Fact]
    public void Pack_UnchangedManifest_ManifestOnlyEmbedded_Replaces()
    {
        // Incomplete embedded pair — manifest kept, bin deleted for fast dev builds
        // (exactly the state build.bat's tail NOTE leaves behind). A same-manifest
        // pack must repair it, never bless the half pair with a keep decision.
        byte[] manifest = { 0x7B, 0x22, 0x76, 0x65, 0x72, 0x73, 0x69, 0x6F, 0x6E, 0x22 }; // {"version"
        byte[] bin = { 4, 5, 6 };
        string embedded = TempDir();
        Directory.CreateDirectory(embedded);
        // Manifest-only remnant: no payload.bin in the embedded dir.
        File.WriteAllBytes(Path.Combine(embedded, PayloadPacker.ManifestFileName), manifest);

        string temp = TempDir();
        WritePair(temp, manifest, bin);

        bool replaced = EmbeddedUpdater.Apply(embedded, temp);

        Assert.True(replaced, "manifest-only remnant must go through replace");
        Assert.False(Directory.Exists(temp), "replace consumes the temp pack dir");
        Assert.Equal(bin, File.ReadAllBytes(Path.Combine(embedded, PayloadPacker.PayloadFileName)));
        Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(embedded, PayloadPacker.ManifestFileName)));
    }

    [Fact]
    public void Pack_ChangedManifest_ReplacesEmbedded()
    {
        string embedded = TempDir();
        WritePair(embedded, new byte[] { 1, 1, 1 }, new byte[] { 1, 2, 3 });

        string temp = TempDir();
        byte[] newManifest = { 2, 2, 2, 2 };
        byte[] newBin = { 7, 7, 7 };
        WritePair(temp, newManifest, newBin);

        bool replaced = EmbeddedUpdater.Apply(embedded, temp);

        Assert.True(replaced);
        Assert.False(Directory.Exists(temp), "replace consumes the temp pack dir");
        Assert.Equal(newManifest, File.ReadAllBytes(Path.Combine(embedded, PayloadPacker.ManifestFileName)));
        Assert.Equal(newBin, File.ReadAllBytes(Path.Combine(embedded, PayloadPacker.PayloadFileName)));

        // Absent embedded pair (fresh clone, or deleted for fast dev builds) → replace too.
        string fresh = TempDir();
        string temp2 = TempDir();
        WritePair(temp2, newManifest, newBin);
        Assert.True(EmbeddedUpdater.Apply(fresh, temp2));
        Assert.True(File.Exists(Path.Combine(fresh, PayloadPacker.PayloadFileName)));
    }
}