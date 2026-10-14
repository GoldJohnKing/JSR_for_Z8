// tools/finalize-variant — ACR-compatibility bisection harness (post-release
// fix 3: raw-layout knob strips|tiles; fix 4: cumulative feature strip-down):
// runs the product finalizer (JsrDngFinalizer) on a real backend DNG with a
// chosen raw storage layout and feature-suppression level, then prints a
// structural summary of the written file. Not part of the solution/UI chain.
//
// Usage: finalize-variant <backendDng> <referenceNef> <outPath> [layout] [featureSet]
//   layout      strips|tiles         (default tiles = product default)
//   featureSet  V0|V1|V2|V3|full     (default full = product file)
//     V0   raw + IFD0 thumbnail + DNG basics — all four seams suppressed
//     V1   = V0 + geometry tags (ActiveArea + DefaultScale/CropOrigin/CropSize)
//     V2   = V1 + lens opcodes (OpcodeList2/3)
//     V3   = V2 + preview SubIFDs (IFD0 thumbnail is always present)
//     full = V3 + EXIF/GPS/XMP/Software/Baseline migration block (= product)

using System.Globalization;
using Jsr4Z8.Core.Dng;

if (args.Length is < 3 or > 5)
{
    Console.Error.WriteLine(
        "usage: finalize-variant <backendDng> <referenceNef> <outPath> [strips|tiles] [V0|V1|V2|V3|full]");
    return 2;
}

string layoutArg = args.Length >= 4 ? args[3] : "tiles";
RawLayout? layout = layoutArg.ToLowerInvariant() switch
{
    "strips" => RawLayout.Strips,
    "tiles" => RawLayout.Tiles,
    _ => null,
};
if (layout is null)
{
    Console.Error.WriteLine($"unknown layout '{args[3]}' (expected strips|tiles)");
    return 2;
}

string featureArg = args.Length >= 5 ? args[4] : "full";
(bool Geom, bool Opcodes, bool Previews, bool ExifGps)? featureSet = featureArg.ToLowerInvariant() switch
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
    Console.Error.WriteLine($"unknown featureSet '{args[4]}' (expected V0|V1|V2|V3|full)");
    return 2;
}
var f = featureSet.Value;

Console.WriteLine($"backend:    {args[0]}");
Console.WriteLine($"ref NEF:    {args[1]}");
Console.WriteLine($"layout:     {layout.Value}");
Console.WriteLine($"features:   {featureArg.ToLowerInvariant()}  " +
    $"geometry={f.Geom} opcodes={f.Opcodes} previews={f.Previews} exifGps={f.ExifGps}");

// Finalize names its output JSR_<基准帧名>.dng inside destDir; relocate it to
// the caller's outPath (same directory ⇒ a local rename).
string outPath = Path.GetFullPath(args[2]);
string? destDir = Path.GetDirectoryName(outPath);
if (string.IsNullOrEmpty(destDir)) destDir = ".";
Directory.CreateDirectory(destDir);

string produced = JsrDngFinalizer.Finalize(args[0], args[1], destDir, layout: layout.Value,
    includeGeometryTags: f.Geom, includeOpcodes: f.Opcodes,
    includePreviews: f.Previews, includeExifGps: f.ExifGps);
if (!string.Equals(Path.GetFullPath(produced), outPath, StringComparison.OrdinalIgnoreCase))
    File.Move(produced, outPath, overwrite: true);

Console.WriteLine($"output:     {outPath}");
Console.WriteLine($"size:       {new FileInfo(outPath).Length.ToString("N0", CultureInfo.InvariantCulture)} bytes");

Summarize(outPath);
return 0;

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
