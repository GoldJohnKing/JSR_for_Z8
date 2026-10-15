using Xunit;

namespace Jsr4Z8.Tests;

/// <summary>
/// Per-test-class temp ownership. One fixture instance per test class (via
/// <see cref="IClassFixture{T}"/>; xUnit runs a class's tests sequentially, so no
/// cross-test collision) allocates a single GUID-named root under %TEMP% and hands
/// out GUID-named subdirectories inside it; <see cref="Dispose"/> deletes the whole
/// root recursively, best-effort, so even a failed test leaves nothing behind.
/// Every test that creates a jsr4z8_* temp path must go through this helper — the
/// product runtime's own %TEMP%\Jsr4Z8\&lt;job&gt; dirs are unrelated and stay
/// untouched.
/// </summary>
public sealed class TestTemp : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jsr4z8_tmp_" + Guid.NewGuid().ToString("N"));

    /// <summary>Creates and returns a fresh empty GUID-named directory under the fixture root.</summary>
    public string NewDir()
    {
        string dir = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
