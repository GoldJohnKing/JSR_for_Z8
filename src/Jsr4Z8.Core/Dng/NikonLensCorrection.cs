using System;
using System.IO;

namespace Jsr4Z8.Core.Dng;

/// <summary>
/// Decodes Nikon Z lens correction data (NEFInfo, SubIFD tag 0xC7D5) and converts it to
/// a DNG WarpRectilinear OpcodeList3 (big-endian serialization per Adobe DNG SDK).
///
/// Mapping derivation (empirical, calibrated against Adobe DNG Converter 18.6
/// output on Z8 firmware 3.11 — synthetic basis sweeps of the NEF rationals):
///   Green plane:  (off, a, b, c) = M_G · (1, k1, k2, k3)
///   Red plane:    G + A · p   (p = six block-7 rationals)
///   Blue plane:   G + C · p
/// Residuals: distortion 1e-6 (≈0.003 px), CA first-order (corner residual < 1 px).
/// Vignette (block 6) is written as OpcodeList2 only when the block 8 activity flag
/// (+0x04) signals Vignette Control ON; otherwise nothing is emitted (Adobe parity).
///
/// Ported from Save_Z8 src/SaveZ8.Core/NikonLensCorrection.cs (namespace only).
/// </summary>
public static class NikonLensCorrection
{
    // M_G rows: images of (1, k1, k2, k3) → (off, r2, r4, r6)
    private static readonly double[][] MG =
    {
        new[] {  1.02809531,  0.0,         -0.0111178147, 0.0        },
        new[] {  0.0,         0.0,          0.356251074,   1.03407210 },
        new[] {  0.0,         1.04009288,   0.898552896,   0.0        },
        new[] {  0.0,         0.0,         -0.207344855,   0.0        },
    };

    // CA responses per unit block-7 parameter (R and B identical shapes, disjoint params)
    private static readonly double[][] AR =
    {
        new[] { -0.0156,  0.4462,  0.6126, -0.2220 },  // p5
        new[] {  0.0001,  1.0927, -0.3067,  0.0763 },  // p7
        new[] {  1.0286, -0.0976,  0.0168,  0.0024 },  // p11
    };
    private static readonly double[][] CB =
    {
        new[] { -0.0156,  0.4462,  0.6126, -0.2220 },  // p14
        new[] {  0.0001,  1.0927, -0.3067,  0.0763 },  // p16
        new[] {  1.0286, -0.0976,  0.0168,  0.0024 },  // p20
    };

    private const double Q = 1048576.0; // rational denominator 2^20
    /// <summary>Reference t4 (block5 +0x40) at which the M matrix was calibrated.</summary>
    private const double T4Ref = -28655 / Q;

    public class WarpParams
    {
        /// <summary>Per plane [R,G,B] → (off, r2, r4, r6).</summary>
        public double[][] Planes { get; init; } = Array.Empty<double[]>();
        public double CenterX { get; init; } = 0.5;
        public double CenterY { get; init; } = 0.5;
    }

    public class VignetteParams
    {
        /// <summary>Gain polynomial gain(r²) = P0 + P1·r² + P2·r⁴ + P3·r⁶ + P4·r⁸.</summary>
        public double[] P { get; init; } = new double[5];
        public double CenterX { get; init; } = 0.5;
        public double CenterY { get; init; } = 0.5;
    }

    public class CorrectionResult
    {
        public WarpParams? Warp { get; init; }
        public VignetteParams? Vignette { get; init; }
    }

    /// <summary>
    /// Parses the NEFInfo blob (verbatim from the raw SubIFD) and computes correction
    /// parameters. Warp is always derived from block 5; vignette only when block 6
    /// exists and the block 8 activity flag (+0x04) is nonzero (Vignette Control state).
    /// Returns null when the blob is absent/malformed.
    /// </summary>
    public static CorrectionResult? Decode(byte[]? nefInfo)
    {
        if (nefInfo is not { Length: > 64 }) return null;
        if (nefInfo[0] != (byte)'N' || nefInfo[1] != (byte)'i' || nefInfo[2] != (byte)'k' ||
            nefInfo[3] != (byte)'o' || nefInfo[4] != (byte)'n' || nefInfo[5] != 0)
            return null;

        int tiffBase = 10; // "Nikon\0" + 4-byte version
        if (tiffBase + 4 > nefInfo.Length) return null;
        if (nefInfo[tiffBase] != 0x49 || nefInfo[tiffBase + 1] != 0x49) return null; // II

        uint ifdOff = U32(nefInfo, (uint)(tiffBase + 4));
        uint o = (uint)(tiffBase + ifdOff);
        if (o + 2 > nefInfo.Length) return null;
        int entries = U16(nefInfo, o);

        byte[]? block5 = null, block6 = null, block7 = null, block8 = null;
        for (int i = 0; i < entries; i++)
        {
            uint p = o + 2 + (uint)(i * 12);
            if (p + 12 > nefInfo.Length) break;
            ushort tag = U16(nefInfo, p);
            ushort type = U16(nefInfo, p + 2);
            uint count = U32(nefInfo, p + 4);
            uint val = U32(nefInfo, p + 8);
            if (type != 7) continue; // undef blocks

            int typeSize = 1;
            long size = (long)typeSize * count;
            if (size <= 4 || val + size > nefInfo.Length) continue;
            uint dataOff = (uint)(tiffBase + val);
            if (dataOff + size > nefInfo.Length) continue;

            if (tag == 5 && count == 84) block5 = Slice(nefInfo, dataOff, 84);
            if (tag == 6 && count == 116) block6 = Slice(nefInfo, dataOff, 116);
            if (tag == 7 && count == 104) block7 = Slice(nefInfo, dataOff, 104);
            if (tag == 8 && count == 24) block8 = Slice(nefInfo, dataOff, 24);
        }

        if (block5 == null) return null;

        // Distortion triple: rational pairs at +0x14, +0x1c, +0x24
        double k1 = I32(block5, 0x14) / Q, k2 = I32(block5, 0x1c) / Q, k3 = I32(block5, 0x24) / Q;
        // Focus-dependent scale term at +0x40: the whole warp scales by 1/(1+t4)
        // (verified empirically; residual 1e-7 across the calibration sweep)
        double t4 = I32(block5, 0x40) / Q;
        double scale = (1.0 + T4Ref) / (1.0 + t4);

        // Green plane = pure distortion
        var g = new double[4];
        double[] basis = { 1, k1, k2, k3 };
        for (int r = 0; r < 4; r++)
            g[r] = MG[r][0] * 1 + MG[r][1] * k1 + MG[r][2] * k2 + MG[r][3] * k3;

        var r0 = (double[])g.Clone();
        var b0 = (double[])g.Clone();

        // Chromatic aberration: block-7 rationals; (p5,p7,p11) → R, (p14,p16,p20) → B
        if (block7 != null)
        {
            double[] pr = { I32(block7, 5 * 4) / Q, I32(block7, 7 * 4) / Q, I32(block7, 11 * 4) / Q };
            double[] pb = { I32(block7, 14 * 4) / Q, I32(block7, 16 * 4) / Q, I32(block7, 20 * 4) / Q };
            for (int j = 0; j < 3; j++)
                for (int c = 0; c < 4; c++)
                {
                    r0[c] += AR[j][c] * pr[j];
                    b0[c] += CB[j][c] * pb[j];
                }
        }

        // Focus-dependent global scale (t4): every coefficient scales by (1+t4ref)/(1+t4)
        for (int c = 0; c < 4; c++)
        {
            g[c] *= scale;
            r0[c] *= scale;
            b0[c] *= scale;
        }

        return new CorrectionResult
        {
            Warp = new WarpParams { Planes = new[] { r0, g, b0 } },
            Vignette = DecodeVignette(block6, block8),
        };
    }

    /// <summary>
    /// Vignette (block 6): three rationals at +0x24/+0x34/+0x44 map reversed and
    /// scaled into the DNG FixVignetteRadial gain polynomial:
    ///   P0 = v3·(21/128), P1 = v2·(21/128), P2 = v1·(21/128), P3 = P4 = 0
    /// (empirical, calibrated against Adobe DNG Converter 18.6; residual 3e-4 ≈ 0.02% gain).
    /// Emitted only when the block 8 flag (+0x04) is nonzero (Vignette Control active).
    /// </summary>
    private static VignetteParams? DecodeVignette(byte[]? block6, byte[]? block8)
    {
        if (block6 == null || block8 == null) return null;
        if (I32(block8, 0x04) == 0) return null; // Vignette Control off

        const double scale = 21.0 / 128.0;
        double v1 = I32(block6, 0x24) / Q, v2 = I32(block6, 0x34) / Q, v3 = I32(block6, 0x44) / Q;
        return new VignetteParams { P = new[] { v3 * scale, v2 * scale, v1 * scale, 0, 0 } };
    }

    /// <summary>
    /// Serializes OpcodeList2 (tag 51009) containing one FixVignetteRadial opcode.
    /// Layout per Adobe DNG SDK dng_opcode_FixVignetteRadial::PutData:
    ///   5×f64 gain coefficients + f64 centerX + f64 centerY (big-endian).
    /// </summary>
    public static byte[] SerializeOpcodeList2(VignetteParams v)
    {
        int paramLen = 7 * 8;
        var ms = new MemoryStream(8 + 16 + paramLen);
        var bw = new BinaryWriter(ms);
        bw.Write(Be((uint)1));                 // one opcode
        bw.Write(Be(3u));                      // opcode id: FixVignetteRadial
        bw.Write(Be(0x0103_0000));             // minVersion 1.3.0.0
        bw.Write(Be(0u));                      // flags
        bw.Write(Be((uint)paramLen));
        for (int i = 0; i < 5; i++) bw.Write(Be(v.P[i]));
        bw.Write(Be(v.CenterX));
        bw.Write(Be(v.CenterY));
        return ms.ToArray();
    }

    /// <summary>
    /// Serializes OpcodeList3 (tag 51022) containing one WarpRectilinear opcode.
    /// Byte order: big-endian (opcode lists are always big-endian, DNG spec / SDK).
    /// Layout per Adobe DNG SDK dng_opcode_WarpRectilinear::PutData:
    ///   list: { u32 count; per opcode: u32 id, u32 minVersion, u32 flags, u32 paramLen, params }
    ///   params: i32 planes; per plane 6×f64 (off, r2, r4, r6, tan0, tan1); f64 cx; f64 cy
    /// </summary>
    public static byte[] SerializeOpcodeList3(WarpParams w)
    {
        int planes = w.Planes.Length;
        int paramLen = 4 + planes * 48 + 16;
        var ms = new MemoryStream(8 + 16 + paramLen);
        var bw = new BinaryWriter(ms);

        bw.Write(Be((uint)1));                 // one opcode
        bw.Write(Be(1u));                      // opcode id: WarpRectilinear
        bw.Write(Be(0x0103_0000));             // minVersion 1.3.0.0
        bw.Write(Be(0u));                      // flags
        bw.Write(Be((uint)paramLen));
        bw.Write(Be((uint)planes));
        for (int p = 0; p < planes; p++)
        {
            var v = w.Planes[p];
            bw.Write(Be(v[0]));                // offset
            bw.Write(Be(v[1]));                // r^2
            bw.Write(Be(v[2]));                // r^4
            bw.Write(Be(v[3]));                // r^6
            bw.Write(Be(0.0));                 // tan0
            bw.Write(Be(0.0));                 // tan1
        }
        bw.Write(Be(w.CenterX));
        bw.Write(Be(w.CenterY));
        return ms.ToArray();
    }

    // ---------- little helpers ----------

    private static ushort U16(byte[] d, uint o) => (ushort)(d[o] | (d[o + 1] << 8));
    private static uint U32(byte[] d, uint o) =>
        (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));
    private static int I32(byte[] d, int o) => unchecked((int)U32(d, (uint)o));

    private static byte[] Slice(byte[] d, uint off, int len)
    {
        var r = new byte[len];
        Array.Copy(d, off, r, 0, len);
        return r;
    }

    private static byte[] Be(uint v) => new[]
        { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    private static byte[] Be(double v)
    {
        // IEEE 754 big-endian
        long bits = BitConverter.DoubleToInt64Bits(v);
        return new[]
        {
            (byte)(bits >> 56), (byte)(bits >> 48), (byte)(bits >> 40), (byte)(bits >> 32),
            (byte)(bits >> 24), (byte)(bits >> 16), (byte)(bits >> 8), (byte)bits,
        };
    }
}
