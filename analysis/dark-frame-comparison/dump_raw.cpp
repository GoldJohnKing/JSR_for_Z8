// dump_raw: decode NEF (incl. Nikon HE/HE*) via the project's LibRaw fork and
// dump the untouched Bayer data (black pedestal included, 14-bit DN in uint16)
// plus a JSON metadata sidecar. For the JSR_for_Z8 dark-frame comparison.
#include <libraw/libraw.h>
#include <cstdio>
#include <cstring>
#include <cstdlib>
#include <string>

int main(int argc, char** argv) {
    if (argc < 3) { fprintf(stderr, "usage: dump_raw <rawfile> <outprefix>\n"); return 2; }
    LibRaw r;
    int rc = r.open_file(argv[1]);
    if (rc != LIBRAW_SUCCESS) { fprintf(stderr, "open failed: %s\n", libraw_strerror(rc)); return 1; }
    rc = r.unpack();
    if (rc != LIBRAW_SUCCESS) { fprintf(stderr, "unpack failed: %s\n", libraw_strerror(rc)); return 1; }
    uint16_t* img = r.imgdata.rawdata.raw_image;
    if (!img) { fprintf(stderr, "no raw_image (not a Bayer raw?)\n"); return 1; }
    unsigned W = r.imgdata.sizes.raw_width, H = r.imgdata.sizes.raw_height;
    std::string binpath = std::string(argv[2]) + ".bin";
    FILE* f = fopen(binpath.c_str(), "wb");
    if (!f) { perror("fopen"); return 1; }
    if (fwrite(img, sizeof(uint16_t), (size_t)W * H, f) != (size_t)W * H) { fprintf(stderr, "short write\n"); fclose(f); return 1; }
    fclose(f);

    printf("{");
    printf("\"file\":\"%s\",", argv[1]);
    printf("\"make\":\"%s\",\"model\":\"%s\",", r.imgdata.idata.make, r.imgdata.idata.model);
    printf("\"iso\":%.1f,\"shutter\":%.8f,\"aperture\":%.2f,", r.imgdata.other.iso_speed, r.imgdata.other.shutter, r.imgdata.other.aperture);
    printf("\"black\":%d,", r.imgdata.color.black);
    printf("\"cblack\":[%d,%d,%d,%d,%d,%d,%d,%d]", r.imgdata.color.cblack[0], r.imgdata.color.cblack[1],
           r.imgdata.color.cblack[2], r.imgdata.color.cblack[3], r.imgdata.color.cblack[4],
           r.imgdata.color.cblack[5], r.imgdata.color.cblack[6], r.imgdata.color.cblack[7]);
    printf(",\"maximum\":%d,", r.imgdata.color.maximum);
    printf("\"raw_width\":%u,\"raw_height\":%u,\"width\":%u,\"height\":%u,\"top\":%u,\"left\":%u,", W, H,
           r.imgdata.sizes.width, r.imgdata.sizes.height, r.imgdata.sizes.top_margin, r.imgdata.sizes.left_margin);
    printf("\"filters\":\"0x%llx\"", (unsigned long long)r.imgdata.idata.filters);
printf(",\"nef_compression\":%d", (int)r.imgdata.makernotes.nikon.NEFCompression);
    printf("}\n");
    return 0;
}
