// native/dng_ljpeg_wrapper.cpp - Lossless JPEG (ITU T.81 process 14 / SOF3) tile
// encoder for DNG Compression=7 output, built on libjpeg-turbo 3.2 (16-bit
// lossless support). Emits one independent JPEG stream per tile, matching the
// profile of Adobe DNG Converter -c: PSV=1, Pt=0, 16-bit precision, 2
// interleaved components (CFA fake channels, h=v=1), per-tile optimized
// Huffman tables, no JFIF/Adobe/restart markers.
// Ported verbatim from Save_Z8 native/dng_ljpeg_wrapper.cpp (jpeglib.h is
// resolved via /Ilibs\include_dngljpeg, same layout as Save_Z8).
//
// Input contract: src points to `rows` scanlines; each scanline holds
// cols*channels interleaved 16-bit samples (for a Bayer row segment this is
// the mosaic data as-is: even columns = ch0, odd = ch1). Edge tiles must be
// pre-padded by the caller to the full tile geometry.

#include <stdlib.h>
#include <stdio.h>
#include <string.h>
#include "jpeglib.h"
#include <stdio.h>

#ifdef _WIN32
#define EXPORT __declspec(dllexport)
#else
#define EXPORT __attribute__((visibility("default")))
#endif

extern "C" {

// Encodes one tile into caller-provided `out` (capacity `cap` bytes).
// Returns the encoded size, or a negative value on error (-1 setup,
// -2 capacity exceeded, -3 encode failure, -4 unsupported params).
EXPORT long long lje_encode_tile(const unsigned short* src, unsigned rows,
                            unsigned cols /* per channel */,
                            unsigned channels, unsigned bitDepth,
                            unsigned char* out, size_t cap)
{
    if (!src || !out || rows == 0 || cols == 0 || channels == 0 ||
        channels > 4 || bitDepth != 16)
        return -4LL;

    struct jpeg_compress_struct cinfo;
    struct jpeg_error_mgr jerr;
    cinfo.err = jpeg_std_error(&jerr);
    jpeg_create_compress(&cinfo);

    unsigned char* buf = NULL;
    unsigned long size = 0;
    jpeg_mem_dest(&cinfo, &buf, &size);

    // JPEG X = samples per row PER COMPONENT (= MCUs per row with h=1):
    // an interleaved row buffer holds cols*channels u16 samples (one pair per MCU).
    cinfo.image_width = (JDIMENSION)cols;
    cinfo.image_height = (JDIMENSION)rows;
    cinfo.input_components = (int)channels;
    cinfo.in_color_space = JCS_UNKNOWN; // no color transform ever
    jpeg_set_defaults(&cinfo);

    cinfo.data_precision = 16;
    for (unsigned i = 0; i < channels; i++)
    {
        cinfo.comp_info[i].component_id = (int)i; // ADC profile: ids from 0
        cinfo.comp_info[i].h_samp_factor = 1;
        cinfo.comp_info[i].v_samp_factor = 1;
        cinfo.comp_info[i].quant_tbl_no = 0;
    }

    jpeg_enable_lossless(&cinfo, /*psv=*/1, /*pt=*/0);
    cinfo.optimize_coding = TRUE;         // per-tile Huffman optimization
    cinfo.write_JFIF_header = FALSE;
    cinfo.write_Adobe_marker = FALSE;
    cinfo.restart_interval = 0;
    cinfo.restart_in_rows = 0;
    cinfo.density_unit = 0;
    cinfo.X_density = 1;
    cinfo.Y_density = 1;

    jpeg_start_compress(&cinfo, TRUE);

    while (cinfo.next_scanline < cinfo.image_height)
    {
        // 16-bit precision path: rows are J16SAMPLE (unsigned short).
        J16SAMPROW row =
            (J16SAMPROW)(src + (size_t)cinfo.next_scanline * cols * channels);
        if (jpeg16_write_scanlines(&cinfo, (J16SAMPARRAY)&row, 1) != 1)
        {
            jpeg_abort_compress(&cinfo);
            jpeg_destroy_compress(&cinfo);
            free(buf);
            return -3LL;
        }
    }
    jpeg_finish_compress(&cinfo);
    jpeg_destroy_compress(&cinfo);

    long long result = (long long)size;
    if ((size_t)size > cap)
        result = -2; // capacity exceeded
    else
        memcpy(out, buf, size);
    free(buf);
    return result;
}


// Round-trip selftest: encode a deterministic 2-channel gradient tile with
// the same settings as lje_encode_tile, decode it back with libjpeg-turbo's
// own lossless decoder, and report the mismatching sample count (-1 on setup
// failure). Discriminates encoder-usage bugs from container/decoder issues.
EXPORT long long lje_selftest()
{
    enum { R = 16, C = 128, CH = 2 };
    static unsigned short src[R * C * CH];
    static unsigned short back[R * C * CH];
    for (int y = 0; y < R; y++)
        for (int x = 0; x < C * CH; x++)
            src[y * C * CH + x] = (unsigned short)((y * 37 + x * 101) & 0x3FF);

    struct jpeg_compress_struct cinfo;
    struct jpeg_error_mgr jerr;
    cinfo.err = jpeg_std_error(&jerr);
    jpeg_create_compress(&cinfo);
    unsigned char* buf = NULL; unsigned long size = 0;
    jpeg_mem_dest(&cinfo, &buf, &size);
    cinfo.image_width = C; cinfo.image_height = R;
    cinfo.input_components = CH; cinfo.in_color_space = JCS_UNKNOWN;
    jpeg_set_defaults(&cinfo);
    cinfo.data_precision = 16;
    for (int i = 0; i < CH; i++) {
        cinfo.comp_info[i].component_id = i;
        cinfo.comp_info[i].h_samp_factor = 1;
        cinfo.comp_info[i].v_samp_factor = 1;
    }
    jpeg_enable_lossless(&cinfo, 1, 0);
    cinfo.optimize_coding = TRUE;
    cinfo.write_JFIF_header = FALSE;
    cinfo.write_Adobe_marker = FALSE;
    jpeg_start_compress(&cinfo, TRUE);
    while (cinfo.next_scanline < cinfo.image_height) {
        J16SAMPROW row = (J16SAMPROW)(src + (size_t)cinfo.next_scanline * C * CH);
        if (jpeg16_write_scanlines(&cinfo, (J16SAMPARRAY)&row, 1) != 1) {
            jpeg_destroy_compress(&cinfo); free(buf); return -10;
        }
    }
    jpeg_finish_compress(&cinfo);
    jpeg_destroy_compress(&cinfo);

    struct jpeg_decompress_struct dinfo;
    dinfo.err = jpeg_std_error(&jerr);
    jpeg_create_decompress(&dinfo);
    jpeg_mem_src(&dinfo, buf, size);
    jpeg_read_header(&dinfo, TRUE);
    fprintf(stderr, "[selftest] dec: prec=%d nc=%d w=%u h=%u (want 16/%d/%d/%d) enc_size=%lu\n",
            dinfo.data_precision, dinfo.num_components, dinfo.image_width,
            dinfo.image_height, CH, C, R, size);
    long long mism;
    if (dinfo.data_precision != 16) mism = -21;
    else if ((int)dinfo.num_components != CH) mism = -22;
    else if (dinfo.image_width != (unsigned)C) mism = -23;
    else if (dinfo.image_height != (unsigned)R) mism = -24;
    else
    {
        mism = 0;
        jpeg_start_decompress(&dinfo);
        while (dinfo.output_scanline < dinfo.output_height) {
            J16SAMPROW row = (J16SAMPROW)(back + (size_t)dinfo.output_scanline * C * CH);
            if (jpeg16_read_scanlines(&dinfo, (J16SAMPARRAY)&row, 1) != 1) { mism = -3; break; }
        }
        if (mism == 0) {
            for (int i = 0; i < R * C * CH; i++)
                if (back[i] != src[i]) mism++;
        }
    }
    jpeg_abort_decompress(&dinfo);
    jpeg_destroy_decompress(&dinfo);
    free(buf);
    return mism;
}

} // extern "C"
