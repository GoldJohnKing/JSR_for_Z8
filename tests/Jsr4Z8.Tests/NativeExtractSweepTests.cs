using Jsr4Z8.Core.Native;
using Xunit;

namespace Jsr4Z8.Tests;

/// <summary>
/// NativeExtractSweep: the .NET single-file native extraction dir
/// (%TEMP%\.net\&lt;exe name&gt;\&lt;bundle-id&gt;) is replaced by a new directory on every
/// build and never pruned by the runtime, so the app sweeps superseded siblings at
/// startup. These tests pin the three properties that make it safe: the current
/// directory survives, an unknown current directory means "delete nothing", and a
/// freshly written sibling is spared.
/// </summary>
public sealed class NativeExtractSweepTests : IClassFixture<TestTemp>
{
    private const string Exe = "Jsr4Z8.UI";
    private readonly TestTemp _temp;

    public NativeExtractSweepTests(TestTemp temp) => _temp = temp;

    private (string BaseDir, string AppDir) NewExtractRoot()
    {
        string baseDir = _temp.NewDir();
        string appDir = Path.Combine(baseDir, Exe);
        Directory.CreateDirectory(appDir);
        return (baseDir, appDir);
    }

    private static string MakeOldDir(string appDir, string name)
    {
        string dir = Path.Combine(appDir, name);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "libraw.dll"), [1, 2, 3]);
        Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow - TimeSpan.FromHours(1));
        return dir;
    }

    [Fact]
    public void Sweep_DeletesSupersededSibling_KeepsCurrent()
    {
        (string baseDir, string appDir) = NewExtractRoot();
        string current = MakeOldDir(appDir, "IDt7zUa5h27Y");
        string previous = MakeOldDir(appDir, "3v_tqu5K9IaF");
        string crashLeftover = MakeOldDir(appDir, "1a2b");

        NativeExtractSweep.Sweep(baseDir, Exe, [current]);

        Assert.True(Directory.Exists(current));
        Assert.False(Directory.Exists(previous));
        Assert.False(Directory.Exists(crashLeftover));
    }

    [Fact]
    public void Sweep_KeepsSiblingCreatedAfterSweepStart()
    {
        (string baseDir, string appDir) = NewExtractRoot();
        string current = MakeOldDir(appDir, "current");
        string afterStart = Path.Combine(appDir, "2f3e");   // another instance extracting while we sweep
        Directory.CreateDirectory(afterStart);
        Directory.SetLastWriteTimeUtc(afterStart, DateTime.UtcNow.AddSeconds(30));   // "created" after the sweep's start

        NativeExtractSweep.Sweep(baseDir, Exe, [current]);

        Assert.True(Directory.Exists(current));
        Assert.True(Directory.Exists(afterStart));
    }

    [Fact]
    public void Sweep_UnknownCurrentDirectory_DeletesNothing()
    {
        (string baseDir, string appDir) = NewExtractRoot();
        string stale = MakeOldDir(appDir, "3v_tqu5K9IaF");

        NativeExtractSweep.Sweep(baseDir, Exe, []);

        Assert.True(Directory.Exists(stale));
    }

    [Fact]
    public void Sweep_MissingAppDir_IsNoOp()
    {
        string baseDir = _temp.NewDir();

        NativeExtractSweep.Sweep(baseDir, "NoSuchApp", [Path.Combine(baseDir, "NoSuchApp", "whatever")]);
    }

    [Fact]
    public void ResolveExtractBase_OverrideWins_TempFallback()
    {
        Assert.Equal(@"C:\cache\custom", NativeExtractSweep.ResolveExtractBase(@"C:\cache\custom", @"C:\Temp\"));
        Assert.Equal(Path.Combine(@"C:\Temp\", ".net"), NativeExtractSweep.ResolveExtractBase(null, @"C:\Temp\"));
        Assert.Null(NativeExtractSweep.ResolveExtractBase("  ", "  "));
    }

    [Fact]
    public void ProcessNameFrom_StripsDirectoryAndExtension()
    {
        Assert.Equal("Jsr4Z8.UI", NativeExtractSweep.ProcessNameFrom(@"D:\apps\Jsr4Z8.UI.exe"));
        Assert.Null(NativeExtractSweep.ProcessNameFrom(null));
        Assert.Null(NativeExtractSweep.ProcessNameFrom("   "));
    }

    [Fact]
    public void FilterToAppDir_KeepsOnlyOurAppDir_UnquotesAndSkipsJunk()
    {
        string baseDir = @"C:\Temp\.net";
        string appDir = Path.Combine(baseDir, Exe);
        string mine = Path.Combine(appDir, "IDt7zUa5h27Y");
        string[] candidates =
        [
            "\"" + mine + "\"",
            Path.Combine(baseDir, "OtherApp", "whatever"),
            appDir,
            null!,
            "   ",
            @"relative\path",
        ];

        IReadOnlyList<string> kept = NativeExtractSweep.FilterToAppDir(candidates, baseDir, Exe);

        Assert.Single(kept);
        Assert.Equal(mine, kept[0]);
    }

    [Fact]
    public void Sweep_DeletesRenamedAppDir_CarryingCurrentBundleId()
    {
        (string baseDir, string appDir) = NewExtractRoot();
        string current = MakeOldDir(appDir, "IDt7zUa5h27Y");           // keep => our current bundle id
        string renamed = MakeOldDir(baseDir, "totally_other_name");   // no product prefix: only the id can claim it
        MakeOldDir(renamed, "IDt7zUa5h27Y");                           //   ... same inner bundle id
        Directory.SetLastWriteTimeUtc(renamed, DateTime.UtcNow - TimeSpan.FromHours(1));   // child creation refreshes the dir mtime
        string foreign = MakeOldDir(baseDir, "SomeOtherApp");         // different app, different id
        MakeOldDir(foreign, "9zOthERID");
        Directory.SetLastWriteTimeUtc(foreign, DateTime.UtcNow - TimeSpan.FromHours(1));

        NativeExtractSweep.Sweep(baseDir, Exe, [current]);

        Assert.False(Directory.Exists(renamed));
        Assert.True(Directory.Exists(foreign));
    }

    [Fact]
    public void Sweep_KeepsRenamedAppDir_CreatedAfterSweepStart()
    {
        (string baseDir, string appDir) = NewExtractRoot();
        string current = MakeOldDir(appDir, "IDt7zUa5h27Y");
        string renamed = Path.Combine(baseDir, "JSR4Z8_renamed");
        Directory.CreateDirectory(Path.Combine(renamed, "IDt7zUa5h27Y"));
        Directory.SetLastWriteTimeUtc(renamed, DateTime.UtcNow.AddSeconds(30));   // started after our sweep

        NativeExtractSweep.Sweep(baseDir, Exe, [current]);

        Assert.True(Directory.Exists(renamed));
    }

    [Fact]
    public void Sweep_DeletesArbitrarilyRenamedAppDir_CarryingIdentityDll()
    {
        (string baseDir, string appDir) = NewExtractRoot();
        string current = MakeOldDir(appDir, "IDt7zUa5h27Y");

        string renamed = MakeOldDir(baseDir, "whatever_the_user_typed");   // no prefix, different id
        string renamedBundle = MakeOldDir(renamed, "9zOlDdIffeR3nt");
        File.WriteAllBytes(Path.Combine(renamedBundle, NativeExtractSweep.IdentityDllName), [1]);
        Directory.SetLastWriteTimeUtc(renamed, DateTime.UtcNow - TimeSpan.FromHours(1));

        string similarApp = MakeOldDir(baseDir, "another_libraw_app");     // same natives, no marker
        string similarBundle = MakeOldDir(similarApp, "whatever");
        foreach (string f in NativeExtractSweep.LegacyFingerprintFiles)
            File.WriteAllBytes(Path.Combine(similarBundle, f), [1]);
        Directory.SetLastWriteTimeUtc(similarApp, DateTime.UtcNow - TimeSpan.FromHours(1));

        NativeExtractSweep.Sweep(baseDir, Exe, [current]);

        Assert.False(Directory.Exists(renamed));
        Assert.True(Directory.Exists(similarApp));
    }

    [Fact]
    public void Sweep_DeletesOldVersionAppDir_ByProductFingerprint()
    {
        (string baseDir, string appDir) = NewExtractRoot();
        string current = MakeOldDir(appDir, "IDt7zUa5h27Y");

        string oldVersion = MakeOldDir(baseDir, "Jsr4Z8_v0.1.0");     // a previous version's cache
        string oldBundle = MakeOldDir(oldVersion, "9zOlDBuIldId");    //   ... different bundle id
        foreach (string f in NativeExtractSweep.LegacyFingerprintFiles)
            File.WriteAllBytes(Path.Combine(oldBundle, f), [1]);      //   ... but our native pair
        Directory.SetLastWriteTimeUtc(oldVersion, DateTime.UtcNow - TimeSpan.FromHours(1));

        string siblingProduct = MakeOldDir(baseDir, "SaveZ8_v1.0.0"); // same fingerprint, wrong prefix
        string siblingBundle = MakeOldDir(siblingProduct, "whatever");
        foreach (string f in NativeExtractSweep.LegacyFingerprintFiles)
            File.WriteAllBytes(Path.Combine(siblingBundle, f), [1]);
        Directory.SetLastWriteTimeUtc(siblingProduct, DateTime.UtcNow - TimeSpan.FromHours(1));

        string prefixNoFingerprint = MakeOldDir(baseDir, "JSR4Z8_impostor");   // prefix, no fingerprint
        Directory.SetLastWriteTimeUtc(prefixNoFingerprint, DateTime.UtcNow - TimeSpan.FromHours(1));

        NativeExtractSweep.Sweep(baseDir, Exe, [current]);

        Assert.False(Directory.Exists(oldVersion));
        Assert.True(Directory.Exists(siblingProduct));
        Assert.True(Directory.Exists(prefixNoFingerprint));
    }
}
