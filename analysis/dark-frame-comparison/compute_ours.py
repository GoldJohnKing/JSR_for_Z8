#!/usr/bin/env python3
"""Compute dark-frame statistics for our captured Z8 NEFs with the SAME
estimator as the backend z8_presets manifest (robust 4.5-sigma clipped
mean/variance per CFA phase; unit-norm 2x2 Haar variance per phase).

Each NEF is decoded by the project's LibRaw fork (external/libraw,
nikon-he-production) via /tmp/darkcmp/dump_raw -> raw Bayer uint16 with the
1008 DN pedestal included. Statistics are computed on the full visible frame
and on the central 1024x1024 crop (the montage scale of the reference export).

Output: stats_ours.json
"""
import json, os, subprocess, sys, tempfile
import numpy as np

DUMP = '/tmp/darkcmp/dump_raw'
ROOT = '/mnt/f/Recordings/dark_frames/FX'
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'stats_ours.json')
FOLDERS = {'Lossless': 0, 'HE Star': 3, 'HE': 4}  # backend compression codes


def robust_stats(x):
    x = x.astype(np.float64).ravel()
    c = np.median(x)
    s = 1.4826 * np.median(np.abs(x - c))
    if s <= 0:
        lo, hi = np.quantile(x, [0.05, 0.95])
        s = float(np.std(x[(x >= lo) & (x <= hi)]))
    if s <= 0:
        return float(c), 0.0, 0.0
    z = x
    for _ in range(2):
        z = x[np.abs(x - c) <= 4.5 * s]
        if z.size < 256:
            z = x
        c = float(z.mean())
        s = float(z.std())
    return c, s * s, 1.0 - z.size / x.size


def phase_haar_var(p):
    hh, ww = p.shape
    hh -= hh % 2
    ww -= ww % 2
    q = p[:hh, :ww]
    h = (q[0::2, 0::2] - q[0::2, 1::2] - q[1::2, 0::2] + q[1::2, 1::2]) * 0.5
    return robust_stats(h)[1]


def frame_stats(a):
    """a: 2D float Bayer image -> dict of per-phase stats."""
    out = []
    for y, x in [(0, 0), (0, 1), (1, 0), (1, 1)]:
        p = a[y::2, x::2]
        mean, var, rej = robust_stats(p)
        out.append(dict(phase=[y, x], mean_dn=mean, spatial_var=var,
                        rejected=rej, haar_var=phase_haar_var(p)))
    return out


def main():
    results = []
    for folder, comp in FOLDERS.items():
        files = sorted(f for f in os.listdir(os.path.join(ROOT, folder)) if f.upper().endswith('.NEF'))
        for i, fn in enumerate(files):
            src = os.path.join(ROOT, folder, fn)
            prefix = os.path.join(tempfile.gettempdir(), f'darkcmp_{os.getpid()}')
            binp = prefix + '.bin'
            proc = subprocess.run([DUMP, src, prefix], capture_output=True, text=True)
            if proc.returncode != 0:
                print(f'!! DECODE FAIL {folder}/{fn}: {proc.stderr.strip()}', flush=True)
                continue
            meta = json.loads(proc.stdout)
            a = np.fromfile(binp, dtype=np.uint16).reshape(meta['raw_height'], meta['raw_width'])
            # visible crop (margins are 0 for Z8 FX, but be safe)
            t, l = meta['top'], meta['left']
            vis = a[t:t + meta['height'], l:l + meta['width']].astype(np.float32)
            H, W = vis.shape
            y0, x0 = (H - 1024) // 2, (W - 1024) // 2
            rec = dict(folder=folder, comp=comp, file=fn,
                       iso=int(round(meta['iso'])), shutter=meta['shutter'],
                       nef_compression=meta.get('nef_compression'),
                       black=meta['black'], white=meta['maximum'],
                       full=frame_stats(vis),
                       crop1024=frame_stats(vis[y0:y0 + 1024, x0:x0 + 1024]))
            results.append(rec)
            os.remove(binp)
            ph = rec['full']
            print(f'[{folder}] {fn} ISO{rec["iso"]}: residual=' +
                  ', '.join(f'{p["mean_dn"]-1008:+.2f}' for p in ph) +
                  ' haar_rn=%.2f' % np.sqrt(np.median([p['haar_var'] for p in ph])), flush=True)
    with open(OUT, 'w') as f:
        json.dump(results, f, indent=1)
    print(f'wrote {OUT}: {len(results)} files')


if __name__ == '__main__':
    main()
