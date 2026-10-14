using Jsr4Z8.Core.Dng;
using Xunit;

namespace Jsr4Z8.Tests;

/// <summary>
/// Foreign-stream decode truth (encoder/decoder symmetric-bias lesson,
/// AGENT §6.4): fixtures/backend-dng/roi_sample.dng is a REAL backend product
/// — a PIL/tifffile-encoded lossless-JPEG LinearRaw stream from a different
/// lineage than our encoder. These tests decode it through OUR reader
/// (BackendDngReader → ljd_decode_stream/libjpeg-turbo) and lock the result
/// against precomputed constants: pixel count, per-channel min/max/mean and a
/// cheap FNV-1a hash over every 997th sample. The constants encode OUR
/// decoder's correct reading of the foreign stream as measured on 2026-10-14
/// (generator run against commit 2609273, 141/141 green). If a future
/// encoder/decoder change breaks foreign-stream compatibility — predictor
/// handling, interleaving, endianness, anything — the hash moves and these
/// tests scream, catching it before a user's six-minute GPU run ends in an
/// unreadable product.
/// </summary>
public class ForeignStreamDecodeTruthTests
{
    private const int ExpectedW = 2048;
    private const int ExpectedH = 1536;
    private const long ExpectedPixelCount = 9_437_184L;       // 2048 · 1536 · 3

    /// <summary>Per-channel (R,G,B) min/max/mean of the decoded foreign stream
    /// (channel c = samples c, c+3, c+6, …). Computed with the same sequential
    /// accumulation order the test uses.</summary>
    private static readonly (int Min, int Max, double Mean)[] ExpectedChannels =
    {
        (Min: 1382, Max: 30270, Mean: 14722.087946573893),    // R
        (Min: 1687, Max: 56521, Mean: 25476.238411585490),    // G
        (Min:  892, Max: 34120, Mean: 16088.092686017355),     // B
    };

    /// <summary>FNV-1a (offset 14695981039346656037, prime 1099511628211) over
    /// every 997th decoded sample value — 9,466 hashed samples.</summary>
    private const ulong ExpectedFnv1aStride997 = 56_130_404_432_875_986UL;

    /// <summary>One foreign-stream decode per test session.</summary>
    private static readonly Lazy<BackendDng> Decoded = new(() =>
        BackendDngReader.Read(FinalizerTests.FindFixture("roi_sample.dng")));

    [Fact]
    public void ForeignStream_DecodesToDeclaredGeometryAndDomain()
    {
        var d = Decoded.Value;
        Assert.Equal(ExpectedW, d.W);
        Assert.Equal(ExpectedH, d.H);
        Assert.Equal(3, d.Samples);
        Assert.Equal(34892, d.Photometric);       // LinearRaw
        Assert.Equal(0, d.BlackLevel);            // backend domain BL=0/WL=65535
        Assert.Equal(65535, d.WhiteLevel);
        Assert.Equal(ExpectedPixelCount, d.Data.LongLength);
    }

    [Fact]
    public void ForeignStream_PerChannelMinMaxMean_MatchCommittedTruth()
    {
        var d = Decoded.Value;
        for (int c = 0; c < d.Samples; c++)
        {
            long n = 0;
            double sum = 0;
            int min = int.MaxValue, max = int.MinValue;
            for (long i = c; i < d.Data.LongLength; i += d.Samples)
            {
                int v = d.Data[i];
                n++;
                sum += v;
                if (v < min) min = v;
                if (v > max) max = v;
            }
            var expected = ExpectedChannels[c];
            Assert.Equal(expected.Min, min);
            Assert.Equal(expected.Max, max);
            Assert.Equal(d.Data.LongLength / d.Samples, n);
            double mean = sum / n;
            Assert.True(Math.Abs(mean - expected.Mean) <= 1e-9 * expected.Mean,
                $"channel {c}: decoded mean {mean:R} drifted from committed truth {expected.Mean:R}");
        }
    }

    [Fact]
    public void ForeignStream_StridedFnv1aHash_MatchesCommittedTruth()
    {
        var d = Decoded.Value;
        ulong h = 14695981039346656037UL;          // FNV-1a 64 offset basis
        long hashed = 0;
        for (long i = 0; i < d.Data.LongLength; i += 997)
        {
            h ^= d.Data[i];
            h *= 1099511628211UL;                  // FNV-1a 64 prime
            hashed++;
        }
        Assert.Equal(9466L, hashed);                // ceil(9437184 / 997)
        Assert.True(h == ExpectedFnv1aStride997,
            $"FNV-1a over every 997th sample moved: got {h}, committed truth {ExpectedFnv1aStride997}. " +
            "A foreign-stream decode regression changed the pixel stream — regenerate the constant " +
            "ONLY after proving the new reading is the correct one against an independent decoder " +
            "(pylibjpeg/tifffile), never from our decoder alone.");
    }
}
