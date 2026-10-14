using Jsr4Z8.Core.Native;
using Xunit;

namespace Jsr4Z8.Tests;

/// <summary>
/// Native interop smoke test: opens a real sample NEF through the ported
/// libraw_wrapper.dll and checks the metadata LibRaw reports for it.
/// Facts measured 2026-10-13 on samples/DSC_7576.NEF.
/// </summary>
public class NativeSmokeTests
{
    [Fact]
    public void OpenSampleNef_ReadsMakeModelIso()
    {
        string nef = FindSample(Path.Combine("test_frames","FX","Lossless","DSC_7576.NEF"));

        IntPtr handle = LibRawNative.OpenRaw(nef, out int error);
        Assert.True(handle != IntPtr.Zero, $"lr_open failed, error={error}");

        try
        {
            Assert.Equal("NIKON CORPORATION", LibRawNative.GetMake(handle));
            Assert.Contains("Z 8", LibRawNative.GetModel(handle));
            Assert.Equal(3200f, LibRawNative.GetIso(handle));

            float[] camMul = LibRawNative.GetCamMul(handle);
            Assert.Equal(4, camMul.Length);
        }
        finally
        {
            LibRawNative.Close(handle);
        }
    }

    /// <summary>Locates samples\&lt;name&gt; by walking up to the repo root.</summary>
    private static string FindSample(string name) => SampleFiles.FindSample(name);
}
