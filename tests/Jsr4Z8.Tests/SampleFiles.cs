namespace Jsr4Z8.Tests;

/// <summary>Repo-relative file locator for tests: walks up from the test output
/// directory (build\bin\Jsr4Z8.Tests\...) to the repo root. All per-file accessors
/// (FindSample, fixtures/stub locators, E2E's RepoRoot) are thin wrappers over
/// <see cref="FindUp"/>/<see cref="RepoRoot"/>.</summary>
internal static class SampleFiles
{
    /// <summary>First existing <paramref name="relativePath"/> in an ancestor of the
    /// test output directory; throws when nothing matches within 12 levels.</summary>
    internal static string FindUp(string relativePath)
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && dir != null; i++)
        {
            string candidate = Path.Combine(dir, relativePath);
            if (File.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException(
            $"Not found walking up from {AppContext.BaseDirectory}: {relativePath}");
    }

    /// <summary>Repo root (the directory containing JSR_for_Z8.sln).</summary>
    internal static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir, "JSR_for_Z8.sln")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException($"repo root not found walking up from {AppContext.BaseDirectory}");
    }

    /// <summary>Locates a file under samples\.</summary>
    public static string FindSample(string name) => FindUp(Path.Combine("samples", name));
}
