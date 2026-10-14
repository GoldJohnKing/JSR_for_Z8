// native/libraw_wrapper.cpp — Minimal C wrapper around LibRaw for P/Invoke from C#
// Ported from Save_Z8 native/libraw_wrapper.cpp (extern C surface identical;
// lr_get_make/lr_get_model/lr_get_iso added for JSR for Z8 metadata reads —
// Make/Model return the VERBATIM TIFF IFD0 tags, like the original JSR
// backend's exifread, because LibRaw's identify() simplifies idata.make
// ("NIKON CORPORATION" -> "Nikon")); falls back to LibRaw's values.
// Windows-only (MSVC) build; wide-character paths for Unicode filename
// support.

#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include "libraw/libraw.h"

typedef struct {
    LibRaw *lr;
    unsigned short *raw_image;
    int width, height;
    int black_level, white_level;
    int filters;
    char make[64];   // verbatim TIFF Make (tag 0x010F)
    char model[64];  // verbatim TIFF Model (tag 0x0110)
} lr_handle_t;

#ifdef _WIN32
#define EXPORT __declspec(dllexport)
#else
#define EXPORT __attribute__((visibility("default")))
#endif

// Reads the verbatim Make (0x010F) / Model (0x0110) ASCII tags from the
// TIFF IFD0 header of a raw file (NEF is TIFF-based). Returns 0 on success.
static int read_tiff_make_model(const wchar_t* filepath, char* make, char* model,
                                size_t cap)
{
    make[0] = model[0] = 0;
    FILE* f = _wfopen(filepath, L"rb");
    if (!f) return -1;

    // TIFF magic 42 is endian-dependent: II stores it as 2A 00, MM as 00 2A.
    unsigned char hdr[8];
    if (fread(hdr, 1, 8, f) != 8 ||
        !((hdr[0] == 'I' && hdr[1] == 'I' && hdr[2] == 42 && hdr[3] == 0) ||
          (hdr[0] == 'M' && hdr[1] == 'M' && hdr[2] == 0 && hdr[3] == 42)))
    {
        fclose(f);
        return -2;
    }
    // Little-endian (II) parse only: NEF is always II. Valid MM files are
    // rejected here so callers fall back to LibRaw's simplified Make/Model.
    if (hdr[0] == 'M') { fclose(f); return -2; }
    unsigned long ifd0 = (unsigned long)hdr[7] << 24 | (unsigned long)hdr[6] << 16
                       | (unsigned long)hdr[5] << 8 | hdr[4];
    if (fseek(f, (long)ifd0, SEEK_SET) != 0) { fclose(f); return -3; }

    unsigned char ec[2];
    if (fread(ec, 1, 2, f) != 2) { fclose(f); return -4; }
    int entries = ec[1] << 8 | ec[0];

    for (int i = 0; i < entries; i++)
    {
        unsigned char e[12];
        if (fread(e, 1, 12, f) != 12) break;
        int tag = e[1] << 8 | e[0];
        if (tag != 0x010F && tag != 0x0110) continue;
        // type 2 = ASCII; count = byte length incl. NUL
        unsigned long count = (unsigned long)e[7] << 24 | (unsigned long)e[6] << 16
                            | (unsigned long)e[5] << 8 | e[4];
        if (count < 2 || count > 1024) continue;
        unsigned long val = (unsigned long)e[11] << 24 | (unsigned long)e[10] << 16
                          | (unsigned long)e[9] << 8 | e[8];
        long save = ftell(f);
        char* dst = (tag == 0x010F) ? make : model;
        size_t n = count < cap ? count : cap - 1;
        if (count <= 4) {
            memcpy(dst, e + 8, n); // value inline in the entry
            dst[n] = 0;
        } else {
            if (fseek(f, (long)val, SEEK_SET) == 0) {
                size_t got = fread(dst, 1, n, f);
                dst[got < cap - 1 ? got : cap - 1] = 0;
            }
        }
        fseek(f, save, SEEK_SET);
    }
    fclose(f);
    return (make[0] || model[0]) ? 0 : -5;
}

extern "C" {

// Windows: use wide-character path (supports Chinese/Japanese/etc. in filenames)
EXPORT void* lr_open(const wchar_t* filepath, int* error) {
    *error = 0;
    lr_handle_t *h = (lr_handle_t*)calloc(1, sizeof(lr_handle_t));
    if (!h) { *error = -1; return NULL; }

    h->lr = new LibRaw();
    if (!h->lr) { free(h); *error = -1; return NULL; }

    // open_file's wchar_t overload is selected by the signature above.
    int ret = h->lr->open_file(filepath);

    if (ret != LIBRAW_SUCCESS) {
        delete h->lr; free(h);
        *error = ret;
        return NULL;
    }

    ret = h->lr->unpack();
    if (ret != LIBRAW_SUCCESS) {
        delete h->lr; free(h);
        *error = ret;
        return NULL;
    }

    h->width = h->lr->imgdata.sizes.raw_width;
    h->height = h->lr->imgdata.sizes.raw_height;
    h->raw_image = h->lr->imgdata.rawdata.raw_image;

    if (!h->raw_image) {
        delete h->lr; free(h);
        *error = -2;
        return NULL;
    }

    h->black_level = h->lr->imgdata.color.black;
    h->white_level = h->lr->imgdata.color.maximum;
    h->filters = h->lr->imgdata.idata.filters;

    // Verbatim TIFF Make/Model for callers; LibRaw's simplified values as
    // fallback when the header scan fails.
#ifdef _WIN32
    if (read_tiff_make_model(filepath, h->make, h->model, sizeof h->make) != 0)
#endif
    {
        strncpy(h->make, h->lr->imgdata.idata.make, sizeof(h->make) - 1);
        strncpy(h->model, h->lr->imgdata.idata.model, sizeof(h->model) - 1);
    }

    return h;
}

EXPORT int lr_get_width(void* handle) {
    return ((lr_handle_t*)handle)->width;
}

EXPORT int lr_get_height(void* handle) {
    return ((lr_handle_t*)handle)->height;
}

EXPORT unsigned short* lr_get_raw_image(void* handle) {
    return ((lr_handle_t*)handle)->raw_image;
}

EXPORT int lr_get_black_level(void* handle) {
    return ((lr_handle_t*)handle)->black_level;
}

EXPORT int lr_get_white_level(void* handle) {
    return ((lr_handle_t*)handle)->white_level;
}

EXPORT int lr_get_cam_mul(void* handle, float* out4) {
    LibRaw *lr = ((lr_handle_t*)handle)->lr;
    for (int i = 0; i < 4; i++) out4[i] = lr->imgdata.color.cam_mul[i];
    return 0;
}

EXPORT int lr_get_cfa_pattern(void* handle, unsigned char* out_pattern) {
    lr_handle_t *h = (lr_handle_t*)handle;
    unsigned int f = (unsigned int)h->filters;
    for (int y = 0; y < 2; y++) {
        for (int x = 0; x < 2; x++) {
            int c = (f >> (((y << 1 & 14) + (x & 1)) << 1)) & 3;
            out_pattern[y * 2 + x] = (unsigned char)c;
        }
    }
    return 0;
}

// Camera identity/ISO metadata, read after lr_open. Make/Model are the
// verbatim TIFF tags; pointers stay valid until lr_close.
EXPORT const char* lr_get_make(void* handle) {
    return ((lr_handle_t*)handle)->make;
}

EXPORT const char* lr_get_model(void* handle) {
    return ((lr_handle_t*)handle)->model;
}

EXPORT float lr_get_iso(void* handle) {
    LibRaw *lr = ((lr_handle_t*)handle)->lr;
    return lr->imgdata.other.iso_speed;
}

// TIFF-level raw IFD facts (identify-time), used by the 5-dimension capture
// consistency contract: tiff_bps = BitsPerSample, tiff_compress = the raw
// strip's Compression tag (34713 = NEF family). NOTE: tiff_compress CANNOT
// separate lossless NEF from HE/HE* — all Z8 NEFs carry 34713 there; the real
// discriminator is the Nikon MakerNote NEFCompression field below.
EXPORT int lr_get_bits_per_sample(void* handle) {
    LibRaw *lr = ((lr_handle_t*)handle)->lr;
    return (int)lr->get_internal_data_pointer()->unpacker_data.tiff_bps;
}

EXPORT int lr_get_compression(void* handle) {
    LibRaw *lr = ((lr_handle_t*)handle)->lr;
    return (int)lr->get_internal_data_pointer()->unpacker_data.tiff_compress;
}

// Nikon MakerNote NEFCompression (tag 0x0051/0x0093, parsed by identify):
// 3 = lossless compressed (the profile the preset dark table measured on),
// 13 = High Efficiency, 14 = High Efficiency* — the ONLY reliable way to
// tell HE from lossless, both of which share TIFF Compression 34713.
EXPORT int lr_get_nef_compression(void* handle) {
    LibRaw *lr = ((lr_handle_t*)handle)->lr;
    return (int)lr->imgdata.makernotes.nikon.NEFCompression;
}

EXPORT void lr_close(void* handle) {
    if (!handle) return;
    lr_handle_t *h = (lr_handle_t*)handle;
    if (h->lr) delete h->lr;
    free(h);
}

EXPORT const char* lr_version(void) {
    return LIBRAW_VERSION_STR;
}

// LibRaw's own error description for a return code (verbatim, English).
EXPORT const char* lr_strerror(int code) {
    return libraw_strerror(code);
}

} // extern "C"
