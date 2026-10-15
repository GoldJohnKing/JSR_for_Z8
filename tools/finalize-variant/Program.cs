// tools/finalize-variant — ACR-compatibility bisection harness (raw-layout
// knob strips|tiles; cumulative feature strip-down; crop size-ladder; verbatim
// raw-stream passthrough; artifact verify):
// runs the product finalizer (JsrDngFinalizer) on a real backend DNG with a
// chosen raw storage layout and feature-suppression level, then prints a
// structural summary of the written file. Not part of the solution/UI chain.
//
// Usage: finalize-variant <backendDng> <refNef> <outPath> [knobs]
//   knobs (any order, any subset):
//   layout      strips|tiles         (default tiles = product default)
//   featureSet  V0|V1|V2|V3|full     (default full = product file)
//     V0   raw + IFD0 thumbnail + DNG basics — all four seams suppressed
//     V1   = V0 + geometry tags (ActiveArea + DefaultScale/CropOrigin/CropSize)
//     V2   = V1 + lens opcodes (OpcodeList2/3)
//     V3   = V2 + preview SubIFDs (IFD0 thumbnail is always present)
//     full = V3 + EXIF/GPS/XMP/Software/Baseline migration block (= product)
//   crop=WxH    decode + crop to W×H from (0,0), then finalize (size ladder)
//   passthrough backend strip bytes verbatim (no decode/re-encode; strips layout)
//
// Usage: finalize-variant verify <srcDng> <artifact.dng> [crop=WxH]
//   crop=WxH  pixel roundtrip: artifact pixels must equal src's (0,0,W,H) window exactly
//   (none)    passthrough check: every strip byte identical; MD5 of strip 0 + last

using System.Globalization;
using System.Security.Cryptography;
using Jsr4Z8.Core.Dng;

// ---- verify subcommand: check a previously written artifact -----------------
if (args.Length > 0 && args[0] == "verify")
{
    if (args.Length is < 3 or > 4)
    {
        Console.Error.WriteLine("usage: finalize-variant verify <srcDng> <artifact.dng> [crop=WxH]");
        return 2;
    }
    return TryParseCrop(args.Length == 4 ? args[3] : "", out int cw, out int ch)
        ? VerifyCrop(args[1], args[2], cw, ch)
        : VerifyPassthrough(args[1], args[2]);
}

if (args.Length is < 3 or > 6)
{
    Console.Error.WriteLine(
        "usage: finalize-variant <backendDng> <refNef> <outPath> [strips|tiles] [V0|V1|V2|V3|full] [crop=WxH|passthrough]\n" +
        "       finalize-variant verify <srcDng> <artifact.dng> [crop=WxH]");
    return 2;
}

// ---- knob scan over the optional trailing args ------------------------------
string layoutArg = "tiles", featureArg = "full";
bool layoutExplicit = false, passthrough = false;
int? cropW = null, cropH = null;
foreach (string arg in args[3..])
{
    string s = arg.ToLowerInvariant();
    if (s is "strips" or "tiles") { layoutArg = s; layoutExplicit = true; }
    else if (s is "v0" or "v1" or "v2" or "v3" or "full") featureArg = s;
    else if (s == "passthrough") passthrough = true;
    else if (TryParseCrop(s, out int w, out int h)) { cropW = w; cropH = h; }
    else
    {
        Console.Error.WriteLine($"unknown knob '{arg}' (expected strips|tiles|V0|V1|V2|V3|full|crop=WxH|passthrough)");
        return 2;
    }
}
// Passthrough is defined on the strips path (verbatim strip copy); default to
// strips when the caller didn't pick a layout explicitly.
if (passthrough && !layoutExplicit) layoutArg = "strips";

RawLayout? layout = layoutArg switch
{
    "strips" => RawLayout.Strips,
    "tiles" => RawLayout.Tiles,
    _ => null,
};
if (layout is null)
{
    Console.Error.WriteLine($"unknown layout '{layoutArg}' (expected strips|tiles)");
    return 2;
}

(bool Geom, bool Opcodes, bool Previews, bool ExifGps)? featureSet = featureArg switch
{
    "v0" => (false, false, false, false),
    "v1" => (true, false, false, false),
    "v2" => (true, true, false, false),
    "v3" => (true, true, true, false),
    "full" => (true, true, true, true),
    _ => null,
};
if (featureSet is null)
{
    Console.Error.WriteLine($"unknown featureSet '{featureArg}' (expected V0|V1|V2|V3|full)");
    return 2;
}
var f = featureSet.Value;

Console.WriteLine($"backend:    {args[0]}");
Console.WriteLine($"ref NEF:    {args[1]}");
Console.WriteLine($"layout:     {layout.Value}");
Console.WriteLine($"features:   {featureArg.ToLowerInvariant()}  " +
    $"geometry={f.Geom} opcodes={f.Opcodes} previews={f.Previews} exifGps={f.ExifGps}");
Console.WriteLine(passthrough
    ? "mode:       passthrough — backend strip bytes verbatim, no decode/re-encode"
    : cropW is int cW && cropH is int cH
        ? $"mode:       crop — decode, window (0,0)-({cW},{cH}), re-encode"
        : "mode:       standard — decode + re-encode");

// Finalize names its output JSR_<基准帧名>.dng inside destDir; relocate it to
// the caller's outPath (same directory ⇒ a local rename).
string outPath = Path.GetFullPath(args[2]);
string? destDir = Path.GetDirectoryName(outPath);
if (string.IsNullOrEmpty(destDir)) destDir = ".";
Directory.CreateDirectory(destDir);

string produced = JsrDngFinalizer.Finalize(args[0], args[1], destDir, layout: layout.Value,
    includeGeometryTags: f.Geom, includeOpcodes: f.Opcodes,
    includePreviews: f.Previews, includeExifGps: f.ExifGps,
    passthroughRawStreams: passthrough, cropW: cropW, cropH: cropH);
if (!string.Equals(Path.GetFullPath(produced), outPath, StringComparison.OrdinalIgnoreCase))
    File.Move(produced, outPath, overwrite: true);

Console.WriteLine($"output:     {outPath}");
Console.WriteLine($"size:       {new FileInfo(outPath).Length.ToString("N0", CultureInfo.InvariantCulture)} bytes");

Summarize(outPath);
return 0;

/// <summary>Parses a "crop=WxH" knob (lowercase-tolerant); anything else fails.</summary>
static bool TryParseCrop(string knob, out int w, out int h)
{
    w = h = 0;
    string s = knob.ToLowerInvariant();
    if (!s.StartsWith("crop=")) return false;
    string[] parts = s["crop=".Length..].Split('x');
    return parts.Length == 2 && int.TryParse(parts[0], out w) && int.TryParse(parts[1], out h)
        && w > 0 && h > 0;
}

/// <summary>Crop verify (C1..C3): the artifact's decoded pixels must equal the
/// source's (0,0,W,H) window EXACTLY — lossless LJ both ways, no resample.</summary>
static int VerifyCrop(string srcPath, string artifactPath, int cw, int ch)
{
    Console.WriteLine($"src:      {srcPath}");
    Console.WriteLine($"artifact: {artifactPath}  (expected {cw}x{ch} window at (0,0))");

    var src = BackendDngReader.Read(srcPath);
    if (cw > src.W || ch > src.H)
    {
        Console.Error.WriteLine($"crop {cw}x{ch} exceeds source {src.W}x{src.H}");
        return 1;
    }
    var art = BackendDngReader.Read(artifactPath);
    Console.WriteLine($"src:      {src.W}x{src.H}x{src.Samples}");
    Console.WriteLine($"artifact: {art.W}x{art.H}x{art.Samples}");
    if (art.W != cw || art.H != ch || art.Samples != src.Samples)
    {
        Console.Error.WriteLine($"artifact geometry {art.W}x{art.H}x{art.Samples} != expected {cw}x{ch}x{src.Samples}");
        return 1;
    }

    int s = src.Samples;
    long rowSpan = (long)cw * s;
    for (int row = 0; row < ch; row++)
    {
        long srcRow = (long)row * src.W * s;
        long artRow = (long)row * rowSpan;
        for (int i = 0; i < rowSpan; i++)
            if (src.Data[srcRow + i] != art.Data[artRow + i])
            {
                Console.Error.WriteLine(
                    $"MISMATCH row {row} sample {i}: src {src.Data[srcRow + i]} vs artifact {art.Data[artRow + i]}");
                return 1;
            }
    }
    Console.WriteLine($"roundtrip: EXACT — {((long)cw * ch * s).ToString("N0", CultureInfo.InvariantCulture)} samples identical to the src window");
    return 0;
}

/// <summary>Passthrough verify (W1): every raw strip of the artifact must be
/// byte-identical to the source's — the backend's own entropy bytes inside our
/// wrapper. Prints MD5 of strip 0 and the last strip.</summary>
static int VerifyPassthrough(string srcPath, string artifactPath)
{
    Console.WriteLine($"src:      {srcPath}");
    Console.WriteLine($"artifact: {artifactPath}");

    var (srcInfo, srcStreams) = BackendDngReader.ReadStrips(srcPath);
    var (artInfo, artStreams) = BackendDngReader.ReadStrips(artifactPath);
    Console.WriteLine($"src:      {srcInfo.W}x{srcInfo.H}x{srcInfo.Samples}  strips={srcStreams.Length}");
    Console.WriteLine($"artifact: {artInfo.W}x{artInfo.H}x{artInfo.Samples}  strips={artStreams.Length}");

    if (srcInfo.W != artInfo.W || srcInfo.H != artInfo.H || srcInfo.Samples != artInfo.Samples)
    {
        Console.Error.WriteLine($"geometry mismatch: src {srcInfo.W}x{srcInfo.H}x{srcInfo.Samples} vs artifact {artInfo.W}x{artInfo.H}x{artInfo.Samples}");
        return 1;
    }
    if (srcStreams.Length != artStreams.Length)
    {
        Console.Error.WriteLine($"strip count mismatch: {srcStreams.Length} vs {artStreams.Length}");
        return 1;
    }

    for (int i = 0; i < srcStreams.Length; i++)
        if (!srcStreams[i].AsSpan().SequenceEqual(artStreams[i]))
        {
            Console.Error.WriteLine($"MISMATCH strip {i}: {srcStreams[i].Length} B vs {artStreams[i].Length} B (or bytes differ)");
            return 1;
        }

    int last = srcStreams.Length - 1;
    Console.WriteLine($"strip0:   {Md5(srcStreams[0])}  ({srcStreams[0].Length.ToString("N0", CultureInfo.InvariantCulture)} B)");
    Console.WriteLine($"          artifact {Md5(artStreams[0])}");
    Console.WriteLine($"strip{last}:  {Md5(srcStreams[last])}  ({srcStreams[last].Length.ToString("N0", CultureInfo.InvariantCulture)} B)");
    Console.WriteLine($"          artifact {Md5(artStreams[last])}");
    Console.WriteLine($"roundtrip: {srcStreams.Length} strips byte-identical to the source (verbatim passthrough confirmed)");
    return 0;
}

static string Md5(byte[] data) =>
    Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();

/// <summary>Random-access TIFF walk of the raw SubIFD (IFD0 → SubIFDs[0]):
/// geometry tags + strip (273/278/279) or tile (322-325) inventory.</summary>
static void Summarize(string path)
{
    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    ushort U16(long o) { fs.Position = o; return (ushort)(fs.ReadByte() | (fs.ReadByte() << 8)); }
    uint U32(long o) { fs.Position = o; return (uint)(fs.ReadByte() | (fs.ReadByte() << 8)
        | (fs.ReadByte() << 16) | (fs.ReadByte() << 24)); }

    Dictionary<ushort, (ushort Type, int Cnt, uint Val)> ParseIfd(long offset)
    {
        var tags = new Dictionary<ushort, (ushort, int, uint)>();
        int count = U16(offset);
        for (int i = 0; i < count; i++)
        {
            long e = offset + 2 + i * 12;
            tags[U16(e)] = (U16(e + 2), (int)U32(e + 4), U32(e + 8));
        }
        return tags;
    }

    // First value of a LONG array entry: inline when count=1, else blob[0].
    uint First((ushort Type, int Cnt, uint Val) e) => e.Cnt == 1 ? e.Val : U32(e.Val);

    // Sum of a LONG array entry (byte counts): inline when count=1, else blob.
    long SumLongs((ushort Type, int Cnt, uint Val) e)
    {
        if (e.Cnt == 1) return e.Val;
        long total = 0;
        for (int i = 0; i < e.Cnt; i++) total += U32(e.Val + (uint)(4 * i));
        return total;
    }

    if (U16(0) != 0x4949 || U16(2) != 42)
    {
        Console.WriteLine("summary:  not a little-endian TIFF?!");
        return;
    }

    var raw = ParseIfd(U32(4));
    if (raw.TryGetValue(330, out var sub))              // SubIFDs: raw first
        raw = ParseIfd(First(sub));

    // Scalar display value of an entry: SHORTs are packed two-per-u32 inline,
    // arrays larger than 4 bytes live in a blob at the value-field offset.
    uint U(ushort tag)
    {
        if (!raw.TryGetValue(tag, out var e)) return 0;
        if (e.Type == 3)
            return e.Cnt == 1 ? e.Val & 0xFFFF : U16(e.Val);
        return e.Cnt == 1 ? e.Val : U32(e.Val);
    }

    Console.WriteLine($"raw IFD:  {U(256)}x{U(257)}  compression={U(259)}  photometric={U(262)}  " +
        $"samples={U(277)}  bps={U(258)}");

    // Geometry quartet: crop artifacts land via the
    // finalizer's dims-mismatch fallback — full-image ActiveArea/DefaultCrop.
    if (raw.TryGetValue(50829, out var aa) && aa.Cnt == 4)
        Console.WriteLine($"geom:     ActiveArea=[t={U32(aa.Val)} l={U32(aa.Val + 4)} " +
            $"b={U32(aa.Val + 8)} r={U32(aa.Val + 12)}]");
    if (raw.TryGetValue(50720, out var dcs) && dcs.Cnt == 2)
    {
        string origin = raw.TryGetValue(50719, out var dco) && dco.Cnt == 2
            ? $"{U32(dco.Val)},{U32(dco.Val + 8)}" : "-";
        Console.WriteLine($"          CropSize={U32(dcs.Val)}x{U32(dcs.Val + 8)}  CropOrigin=({origin})");
    }

    void SoiAt(long offset)
    {
        fs.Position = offset;
        Console.WriteLine($"stream0:  SOI={fs.ReadByte():X2} {fs.ReadByte():X2}");
    }

    if (raw.TryGetValue(324, out var tileOff))          // TileOffsets → tiled layout
    {
        Console.WriteLine($"layout:   TILED  {U(322)}x{U(323)}  tiles={tileOff.Cnt}");
        Console.WriteLine($"payload:  {SumLongs(raw[325]).ToString("N0", CultureInfo.InvariantCulture)} bytes of LJ tile streams");
        SoiAt(First(tileOff));
    }
    else if (raw.TryGetValue(273, out var stripOff))    // StripOffsets → strip layout
    {
        uint rps = U(278), h = U(257);
        long lastRows = stripOff.Cnt > 0 ? h - (long)(stripOff.Cnt - 1) * rps : 0;
        Console.WriteLine($"layout:   STRIPS RowsPerStrip={rps}  strips={stripOff.Cnt}  lastStripRows={lastRows}");
        Console.WriteLine($"payload:  {SumLongs(raw[279]).ToString("N0", CultureInfo.InvariantCulture)} bytes of LJ strip streams");
        SoiAt(First(stripOff));
    }
    else
    {
        Console.WriteLine("layout:   neither strip nor tile tags found");
    }
}
