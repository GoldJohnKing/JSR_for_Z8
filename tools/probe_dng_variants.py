#!/usr/bin/env python3
# Forensic tool: ACR-bisection minimal-DNG generator (pylibjpeg decode / imagecodecs encode).
# Used in the 2026-10 DefaultCropOrigin campaign (ACR refuses absolute-origin crops).
# Not part of the build; run manually on a machine with ACR for final adjudication.
"""Generate minimal LinearRaw DNG variants (P1 strips / P2 tiles) for ACR bisection.
Pixels: decoded from our JSR_DSC_7576.dng tiles via pylibjpeg (classic IJG decoder).
Wrapper: minimal backend-style DNG (proven ACR-acceptable recipe), tiny RGB thumbnail IFD0.
Encoder: imagecodecs (libjpeg-turbo) lossless predictor 4.
"""
import struct, sys, numpy as np, pylibjpeg, imagecodecs

SRC = 'samples/JSR_DSC_7576.dng'
IMG_CACHE = '/tmp/jsr_img.npy'
W, H, TW, TH = 16560, 11040, 256, 256

def load_img():
    import os
    if os.path.exists(IMG_CACHE):
        return np.load(IMG_CACHE, mmap_mode='r')
    d = open(SRC, 'rb').read()
    off = 43264; n = struct.unpack_from('<H', d, off)[0]
    toff = coff = None
    for i in range(n):
        e = off + 2 + i * 12
        tag, typ, cnt = struct.unpack_from('<HHI', d, e)
        if tag == 324: toff = struct.unpack_from('<I', d, e + 8)[0]
        if tag == 325: coff = struct.unpack_from('<I', d, e + 8)[0]
    cols, rows = (W + TW - 1) // TW, (H + TH - 1) // TH
    img = np.zeros((rows * TH, cols * TW, 3), np.uint16)
    for k in range(cols * rows):
        r, c = divmod(k, cols)
        img[r*TH:(r+1)*TH, c*TW:(c+1)*TW] = pylibjpeg.decode(d[struct.unpack_from('<I', d, toff+4*k)[0]:][:struct.unpack_from('<I', d, coff+4*k)[0]])
    img = np.ascontiguousarray(img[:H, :W])
    np.save(IMG_CACHE, img)
    return img

class Blob:
    def __init__(self, data, typ, count): self.data, self.typ, self.count = data, typ, count

def build(path, streams, stream_offsets_in_layout, layout):  # layout: 'strips'|'tiles'
    # streams: list of encoded JPEG bytes; stream_offsets_in_layout: list of (row, col) grid positions (strips: col=0)
    LE = '<'
    thumb_w, thumb_h = 160, 120
    thumb = bytes([128] * 3 * thumb_w * thumb_h)
    CM = [1142300, -456400, -112300, -481600, 1289500, 211900, -92900, 123100, 782600]

    def entry(tag, typ, count, inline=None, blob=None):
        return dict(tag=tag, typ=typ, count=count, inline=inline, blob=blob)

    S = {1:1, 2:1, 3:2, 4:4, 5:8, 7:1, 10:4}
    ifd0 = [
        entry(254, 4, 1, 1),                    # NST = thumbnail
        entry(256, 4, 1, thumb_w), entry(257, 4, 1, thumb_h),
        entry(258, 3, 3, blob=struct.pack('<3H', 8, 8, 8)),
        entry(259, 3, 1, 1),                    # uncompressed thumb
        entry(262, 3, 1, 2),                    # RGB
        entry(271, 2, 6, blob=b'Nikon\x00'), entry(272, 2, 4, blob=b'Z 8\x00'),
        entry(273, 4, 1, blob=None),            # thumb data offset -> fixup (pixel blob)
        entry(277, 3, 1, 3), entry(278, 4, 1, thumb_h),
        entry(279, 4, 1, len(thumb)),
        entry(282, 5, 1, blob=struct.pack('<II', 300, 1)), entry(283, 5, 1, blob=struct.pack('<II', 300, 1)),
        entry(296, 3, 1, 2),
        entry(305, 2, 11, blob=b'probe1\x00'),
        entry(330, 4, 1, blob=None),            # SubIFDs ptr -> fixup (raw ifd)
        entry(700, 7, 32, blob=b'<x:xmpmeta xmlns:x="adobe:ns:meta/"/>\x00'),
        entry(50706, 1, 4, blob=bytes([1, 4, 0, 0])), entry(50707, 1, 4, blob=bytes([1, 4, 0, 0])),
        entry(50708, 2, 4, blob=b'Z 8\x00'),
        entry(50721, 10, 9, blob=b''.join(struct.pack('<ii', v, 1000000) for v in CM)),
        entry(50728, 5, 3, blob=struct.pack('<6I', 560175, 1000000, 1000000, 1000000, 557734, 1000000)),
        entry(50778, 3, 1, 21),                 # CalibrationIlluminant1 D65
    ]
    raw = [
        entry(254, 4, 1, 0),
        entry(256, 4, 1, W), entry(257, 4, 1, H),
        entry(258, 3, 3, blob=struct.pack('<3H', 16, 16, 16)),
        entry(259, 3, 1, 7),                    # LJ
        entry(262, 3, 1, 34892),                # LinearRaw
        entry(277, 3, 1, 3),
        entry(284, 3, 1, 1),
    ]
    if layout == 'strips':
        raw += [entry(273, 4, len(streams), blob=None), entry(278, 4, 1, 128), entry(279, 4, len(streams), blob=None)]
    else:
        raw += [entry(322, 3, 1, TW), entry(323, 3, 1, TH),
                entry(324, 4, len(streams), blob=None), entry(325, 4, len(streams), blob=None)]
    raw += [entry(50714, 3, 1, 0), entry(50717, 4, 1, 65535)]
    ifd0.sort(key=lambda e: e['tag']); raw.sort(key=lambda e: e['tag'])

    def ifd_size(ifd): return 2 + 12 * len(ifd) + 4

    # layout: header(8) | IFD0 | raw IFD | blobs | streams | thumb
    off_ifd0 = 8
    off_raw = off_ifd0 + ifd_size(ifd0)
    off_blobs = off_raw + ifd_size(raw)
    # assign blob offsets sequentially (4-aligned)
    def place_blobs(ifd, cursor):
        for e in ifd:
            if e.get('blob') is None: continue
            pad = (-cursor) % 4
            e['offset'] = cursor + pad
            cursor = e['offset'] + len(e['blob'])
        return cursor
    # pass1 sizes: need blobs AFTER both IFDs; do offsets in two passes with cursor starting at off_blobs
    cursor = off_blobs
    for ifd in (ifd0, raw):
        for e in ifd:
            if e.get('blob') is None: continue
            pad = (-cursor) % 4; e['offset'] = cursor + pad; cursor = e['offset'] + len(e['blob'])
    off_streams = cursor + ((-cursor) % 4)
    stream_pos = []
    sc = off_streams
    for s in streams:
        stream_pos.append(sc); sc += len(s)
    off_thumb = sc + ((-sc) % 4)
    # fixups
    for e in ifd0:
        if e['tag'] == 273: e['offset'] = off_thumb; e['blob'] = b''  # inline-capable? count=1 LONG -> inline value
        if e['tag'] == 330: e['offset'] = off_raw; e['blob'] = b''
    for e in raw:
        if e['tag'] in (273, 279, 324, 325):
            e['offset'] = None  # array blob assigned below
    # offsets/counts arrays for raw
    if layout == 'strips':
        arr_off = sc2 = off_thumb + len(thumb) + ((-(off_thumb + len(thumb))) % 4)
        off_arr = arr_off; cnt_arr = arr_off + 4 * len(streams)
        for e in raw:
            if e['tag'] == 273: e['offset'] = off_arr
            if e['tag'] == 279: e['offset'] = cnt_arr
        arrays = struct.pack('<%dI' % len(streams), *stream_pos) + struct.pack('<%dI' % len(streams), *(len(s) for s in streams))
    else:
        base = off_thumb + len(thumb) + ((-(off_thumb + len(thumb))) % 4)
        off_arr, cnt_arr = base, base + 4 * len(streams)
        for e in raw:
            if e['tag'] == 324: e['offset'] = off_arr
            if e['tag'] == 325: e['offset'] = cnt_arr
        arrays = struct.pack('<%dI' % len(streams), *stream_pos) + struct.pack('<%dI' % len(streams), *(len(s) for s in streams))

    out = bytearray()
    out += b'II\x2a\x00' + struct.pack('<I', off_ifd0)
    def emit(ifd):
        b = struct.pack('<H', len(ifd))
        for e in ifd:
            b += struct.pack('<HHI', e['tag'], e['typ'], e['count'])
            sz = S[e['typ']] * e['count']
            if e.get('blob') is not None:
                if sz <= 4 and e['tag'] not in (273,) and e['count'] <= 2 and e['typ'] in (3, 4):
                    # small blobs can still be inline; simpler: always offset for blobs
                    b += struct.pack('<I', e['offset'])
                else:
                    b += struct.pack('<I', e['offset'])
            elif e.get('offset') is not None:
                b += struct.pack('<I', e['offset'])
            else:
                v = e['inline']
                if e['typ'] == 3: b += struct.pack('<HH', v, 0)
                elif e['typ'] == 4: b += struct.pack('<I', v)
                else: raise ValueError(e)
        b += struct.pack('<I', 0)
        return b
    ifd0_bytes = emit(ifd0); raw_bytes = emit(raw)
    assert len(ifd0_bytes) == ifd_size(ifd0) and len(raw_bytes) == ifd_size(raw)
    out += ifd0_bytes + raw_bytes
    assert len(out) == off_blobs, (len(out), off_blobs)
    for ifd in (ifd0, raw):
        for e in ifd:
            blob = e.get('blob')
            if blob is None or blob == b'': continue
            pad = (-len(out)) % 4; out += b'\x00' * pad
            assert len(out) == e['offset'], (e['tag'], len(out), e['offset'])
            out += blob
    pad = (-len(out)) % 4; out += b'\x00' * pad
    assert len(out) == off_streams
    for s in streams: out += s
    pad = (-len(out)) % 4; out += b'\x00' * pad
    assert len(out) == off_thumb
    out += thumb
    pad = (-len(out)) % 4; out += b'\x00' * pad
    assert len(out) == (off_arr if layout == 'strips' else base), (len(out))
    out += arrays
    open(path, 'wb').write(out)
    print(path, len(out), 'bytes,', len(streams), layout)

def main():
    img = load_img()
    which = sys.argv[1] if len(sys.argv) > 1 else 'both'
    if which in ('1', 'both'):
        streams = []
        for r in range(0, H, 128):
            streams.append(imagecodecs.jpeg_encode(np.ascontiguousarray(img[r:r+128]), lossless=True, predictor=4))
            if r % 1280 == 0: print('strip', r, flush=True)
        build('/tmp/P1_strips.dng', streams, None, 'strips')
    if which in ('2', 'both'):
        streams = []
        for ty in range(0, H, TH):
            for tx in range(0, W, TW):
                streams.append(imagecodecs.jpeg_encode(np.ascontiguousarray(img[ty:ty+TH, tx:tx+TW]), lossless=True, predictor=4))
            if (ty // TH) % 8 == 0: print('tile row', ty, flush=True)
        build('/tmp/P2_tiles.dng', streams, None, 'tiles')

if __name__ == '__main__':
    main()
