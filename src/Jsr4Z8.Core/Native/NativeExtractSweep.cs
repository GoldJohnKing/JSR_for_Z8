namespace Jsr4Z8.Core.Native;

/// <summary>
/// Sweeps superseded .NET single-file native-extraction directories.
/// <para>
/// A self-contained single-file app extracts its native binaries (libraw.dll,
/// libraw_wrapper.dll, dng_ljpeg.dll, the Avalonia natives and the VC++ runtime
/// DLLs staged next to them) into
/// <c>&lt;DOTNET_BUNDLE_EXTRACT_BASE_DIR or %TEMP%\.net&gt;\&lt;exe name&gt;\&lt;bundle-id&gt;\</c>.
/// The bundle id is a hash of the bundled file contents, so every new build lands in
/// a NEW directory, and the runtime never deletes the previous ones — it only checks
/// that an existing extraction still has all of its files.
/// </para>
/// <para>
/// Windows does not clean %TEMP% either: Storage Sense ships off by default and, when
/// on, runs only when disk space is low. Without this sweep the caches would grow
/// ~20 MB per build forever.
/// </para>
/// <para>
/// Same lifecycle as the payload cache (<see cref="Payload.PayloadManager"/>): the
/// replaced version goes away, the current one stays. It also removes the
/// "&lt;pid&gt;" working directories a hard kill leaves mid-extraction. Caches left
/// under OTHER file names are claimed by content — the same bundle id (the id is a
/// hash of the bundle contents and survives renames) or the shipped identity marker
/// <see cref="IdentityDllName"/> (any version, any file name) — or, for caches older
/// than the marker, by product shape (<see cref="ProductNamePrefix"/> plus the
/// <see cref="LegacyFingerprintFiles"/> pair); foreign products never match and are
/// never touched. Deletable means older than the sweep's own start — no time-based
/// grace window.
/// </para>
/// </summary>
public static class NativeExtractSweep
{
    /// <summary>
    /// Cutoff rule: a directory is deletable only when its LastWriteTimeUtc predates
    /// the sweep's own start. Anything created after the sweep began — the first job's
    /// temp dir, another instance extracting natives mid-startup — is spared; crash
    /// debris from any earlier moment, down to one second before launch, is not.
    /// (There is deliberately no time-based grace window: the one narrow race left is
    /// another instance that began extracting within the sub-second before this sweep
    /// starts.)
    /// </summary>
    private static DateTime SweepStartUtc() => DateTime.UtcNow;

    /// <summary>Attempts per directory before giving up; AV scanners and the search
    /// indexer briefly lock freshly written directories.</summary>
    internal const int RetryAttempts = 5;

    /// <summary>Backoff between delete attempts, in milliseconds.</summary>
    internal const int RetryBackoffMs = 200;

    /// <summary>
    /// Product claim prefix (case-insensitive): our distributed executables are named
    /// <c>JSR4Z8_v&lt;version&gt;.exe</c> (dev publishes: <c>Jsr4Z8.UI.exe</c>), so any
    /// app-name directory starting with this prefix is a cache of THIS product — some
    /// previous version's, under whatever name it was launched as. Other products
    /// (e.g. SaveZ8_v*) fail the prefix and are never touched.
    /// </summary>
    internal const string ProductNamePrefix = "jsr4z8";

    /// <summary>
    /// Product identity marker, prefix-free and collision-free by construction: an
    /// 83-byte text file with a .dll name, checked into the UI project and shipped in
    /// the bundle (the bundler only embeds .dll-named files — plain data files stay
    /// loose next to the exe — while the extracted content is never loaded, so it need
    /// not be a real PE). Its presence in a cache directory proves the cache is this
    /// product's, whatever file name the exe runs under. Replaces the earlier
    /// file-set fingerprint, which another LibRaw-based application could match.
    /// </summary>
    internal const string IdentityDllName = "jsr4z8.native-identity.dll";

    /// <summary>
    /// Files that must coexist in a bundle-id directory to claim a LEGACY app-name
    /// directory (built before the VC++ runtime trio shipped, so
    /// <see cref="ModernFingerprintFiles"/> cannot match): the P/Invoke pair only this
    /// product extracts together, gated by <see cref="ProductNamePrefix"/> because the
    /// predecessor Save_Z8 extracts the same pair under its own name.
    /// </summary>
    internal static readonly string[] LegacyFingerprintFiles = ["libraw_wrapper.dll", "dng_ljpeg.dll"];

    /// <summary>
    /// Production entry point: discover the bundle extraction base and our own app-name
    /// directory, then sweep. Never throws — the sweep must not take a launch down.
    /// </summary>
    public static void Sweep()
    {
        try
        {
            string? baseDir = ResolveExtractBase();
            string? exeName = ProcessExeName();
            if (baseDir is null || exeName is null)
                return;

            Sweep(baseDir, exeName, ResolveCurrentExtractionDirs(baseDir, exeName));
        }
        catch (Exception)
        {
            // Best effort by design.
        }
    }

    /// <summary>
    /// The extraction base the host actually used: <c>DOTNET_BUNDLE_EXTRACT_BASE_DIR</c>
    /// when set (a relative value is resolved against the current directory, as the host
    /// does), otherwise <c>%TEMP%\.net</c>. Null when neither can be determined.
    /// </summary>
    internal static string? ResolveExtractBase() => ResolveExtractBase(
        Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR"),
        Path.GetTempPath());

    /// <summary>Test seam behind the no-arg overload.</summary>
    internal static string? ResolveExtractBase(string? overridden, string? tempPath)
    {
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            try
            {
                return Path.GetFullPath(overridden);
            }
            catch (Exception)
            {
                return null;   // unusable override: never guess a location to delete from
            }
        }

        string temp = tempPath ?? string.Empty;
        return string.IsNullOrWhiteSpace(temp) ? null : Path.Combine(temp, ".net");
    }

    /// <summary>
    /// The running executable's file name without extension — the app-name directory the
    /// host used. Null when unknown (e.g. some single-file-less hosts).
    /// </summary>
    internal static string? ProcessExeName() => ProcessNameFrom(Environment.ProcessPath);

    /// <summary>Test seam behind <see cref="ProcessExeName"/>.</summary>
    internal static string? ProcessNameFrom(string? processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath))
            return null;
        try
        {
            return Path.GetFileNameWithoutExtension(Path.GetFileName(processPath));
        }
        catch (Exception)
        {
            return null;   // invalid path characters: treat as unknown
        }
    }

    /// <summary>
    /// The extraction directories the runtime told us it loaded natives from, restricted to
    /// our own app-name directory. Empty means "unknown" — the sweep then deletes nothing.
    /// Sources, in order: the documented-in-practice
    /// <c>NATIVE_DLL_SEARCH_DIRECTORIES</c> runtime property (single-file: the extraction
    /// directory) and <c>BUNDLE_EXTRACTION_PATH</c> (older/newer hosts).
    /// </summary>
    internal static IReadOnlyList<string> ResolveCurrentExtractionDirs(string baseDir, string exeName)
    {
        var raw = new List<string?>();
        if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string searchDirs)
            raw.AddRange(searchDirs.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        if (AppContext.GetData("BUNDLE_EXTRACTION_PATH") is string extractionPath)
            raw.Add(extractionPath);
        return FilterToAppDir(raw, baseDir, exeName);
    }

    /// <summary>Test seam behind <see cref="ResolveCurrentExtractionDirs"/>: the candidates that sit directly in <c>&lt;baseDir&gt;\&lt;exeName&gt;</c>.</summary>
    internal static IReadOnlyList<string> FilterToAppDir(IEnumerable<string?> candidates, string baseDir, string exeName)
    {
        string appDir = Path.Combine(baseDir, exeName);
        var found = new List<string>();

        foreach (string? candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;
            string full;
            try
            {
                full = Path.GetFullPath(candidate.Trim().Trim('"'));
            }
            catch (Exception)
            {
                continue;
            }

            string? parent = Path.GetDirectoryName(full);
            if (parent is not null &&
                string.Equals(parent, appDir, StringComparison.OrdinalIgnoreCase) &&
                !found.Contains(full, StringComparer.OrdinalIgnoreCase))
            {
                found.Add(full);
            }
        }

        return found;
    }

    /// <summary>
    /// Test seam, single traversal of <c>&lt;baseDir&gt;</c>'s app-name directories:
    /// <list type="bullet">
    /// <item>our own: delete every bundle-id directory that is not in
    /// <paramref name="keep"/> and is older than <see cref="GraceWindow"/>;</item>
    /// <item>any other name provably belonging to this product: delete the whole
    /// directory. Proof is content — a child named like one of our current bundle ids
    /// (the SAME build under another file name) or carrying
    /// <see cref="IdentityDllName"/> (any build that ships the marker, whatever its
    /// file name) — or, for caches older than the marker, product shape
    /// (<see cref="ProductNamePrefix"/> plus the <see cref="LegacyFingerprintFiles"/>
    /// pair). Foreign directories match nothing and are never touched.</item>
    /// </list>
    /// Deletes nothing when <paramref name="keep"/> is empty — the current extraction is
    /// unknown, and with a renamed exe our own directory is exactly a look-alike, so
    /// guessing which directory is in use is what we must not do.
    /// </summary>
    internal static void Sweep(string baseDir, string exeName, IReadOnlyList<string> keep)
    {
        try
        {
            if (keep.Count == 0)
                return;

            if (!Directory.Exists(baseDir))
                return;

            DateTime cutoff = SweepStartUtc();
            var keepSet = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
            var currentIds = new HashSet<string>(
                keep.Select(Path.GetFileName).OfType<string>(), StringComparer.OrdinalIgnoreCase);

            foreach (string appDir in Directory.EnumerateDirectories(baseDir))
            {
                if (string.Equals(Path.GetFileName(appDir), exeName, StringComparison.OrdinalIgnoreCase))
                {
                    SweepOwn(appDir, keepSet, cutoff);
                }
                else if (LastWriteUtc(appDir) < cutoff && IsOurs(appDir, currentIds))
                {
                    TryDeleteDirectory(appDir);
                }
            }
        }
        catch (Exception)
        {
            // Best effort by design.
        }
    }

    /// <summary>Prune superseded bundle-id directories (and crash-leftover &lt;pid&gt; working dirs) in our own app-name directory.</summary>
    private static void SweepOwn(string appDir, HashSet<string> keepSet, DateTime cutoff)
    {
        foreach (string dir in Directory.EnumerateDirectories(appDir))
        {
            if (keepSet.Contains(dir))
                continue;

            if (LastWriteUtc(dir) < cutoff)
                TryDeleteDirectory(dir);
        }
    }

    /// <summary>True when <paramref name="appDir"/> is provably this product's cache: it carries one of our current bundle ids (the same build under another file name), or it is named after our product and one of its bundle-id directories contains the <see cref="FingerprintFiles"/> pair (a previous version's cache).</summary>
    private static bool IsOurs(string appDir, HashSet<string> currentIds) =>
        ContainsAnyChildNamed(appDir, currentIds) || IsProductCache(appDir);

    /// <summary>Product identity marker (any version, any file name) or legacy shape (conventional name plus native pair) — a cache of this product under a past file name.</summary>
    private static bool IsProductCache(string appDir)
    {
        try
        {
            foreach (string child in Directory.EnumerateDirectories(appDir))
                if (File.Exists(Path.Combine(child, IdentityDllName)))
                    return true;
        }
        catch (Exception)
        {
            return false;   // unreadable: claim nothing
        }

        if (!Path.GetFileName(appDir).StartsWith(ProductNamePrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            foreach (string child in Directory.EnumerateDirectories(appDir))
                if (Array.TrueForAll(LegacyFingerprintFiles, f => File.Exists(Path.Combine(child, f))))
                    return true;
            return false;
        }
        catch (Exception)
        {
            return false;   // unreadable: claim nothing
        }
    }

    /// <summary>True when <paramref name="appDir"/> has a direct child directory named like one of <paramref name="ids"/>.</summary>
    private static bool ContainsAnyChildNamed(string appDir, HashSet<string> ids)
    {
        try
        {
            foreach (string child in Directory.EnumerateDirectories(appDir))
                if (ids.Contains(Path.GetFileName(child)))
                    return true;
            return false;
        }
        catch (Exception)
        {
            return false;   // unreadable: claim nothing
        }
    }

    /// <summary>Directory last write, or <see cref="DateTime.MaxValue"/> on IO errors — unreadable means "possibly fresh", so it is spared.</summary>
    private static DateTime LastWriteUtc(string dir)
    {
        try
        {
            return Directory.GetLastWriteTimeUtc(dir);
        }
        catch (Exception)
        {
            return DateTime.MaxValue;
        }
    }

    /// <summary>Recursive delete retrying transient AV/indexer locks; failures are swallowed (retried next launch).</summary>
    private static void TryDeleteDirectory(string path)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt >= RetryAttempts)
                    return;
                Thread.Sleep(RetryBackoffMs);
            }
        }
    }
}
