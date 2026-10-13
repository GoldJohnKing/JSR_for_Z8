namespace Jsr4Z8.Tests;

/// <summary>Locates files under samples\ by walking up from the test output directory to the repo root.</summary>
internal static class SampleFiles
{
    public static string FindSample(string name)
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && dir != null; i++)
        {
            string candidate = Path.Combine(dir, "samples", name);
            if (File.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException($"Sample not found: samples\\{name} (searched upwards from {AppContext.BaseDirectory})");
    }
}
