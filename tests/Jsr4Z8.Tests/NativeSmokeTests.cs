using Jsr4Z8.Core.Native;
using Xunit;

namespace Jsr4Z8.Tests;

/// <summary>
/// Native interop smoke test: opens a real sample NEF through the ported
/// libraw_wrapper.dll and checks the metadata LibRaw reports for it.
/// Measured on samples/test_frames/lossless/DSC_7576.NEF.
/// </summary>
public class NativeSmokeTests
{
    [Fact]
    public void OpenSampleNef_ReadsMakeModelIso()
    {
        string nef = SampleFiles.FindSample(Path.Combine("test_frames","lossless","DSC_7576.NEF"));

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
}
