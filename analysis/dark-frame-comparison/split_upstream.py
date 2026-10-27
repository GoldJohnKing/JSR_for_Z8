#!/usr/bin/env python3
"""Unsplice the reference dark montages and measure the upstream's own
inter-frame (same-parameter burst) deviation.

Layout forensics (see README §8): each 1024x1024 montage = four 512x512
quadrants, one per exposure of a same-parameter 4-frame burst, all covering the
SAME sensor crop (same sites). Evidence:
  - each quadrant shows single-frame internal structure (row-profile corr
    0.29-0.53 vs single-frame control 0.29-0.48; negative control explained in
    README);
  - cross-quadrant same-position difference variance ~= manifest temporal
    variance (validated below for all 102 profiles).

Outputs: stats_upstream.json + upstream_interframe.csv
"""
import json, os
import numpy as np
import tifffile

HERE = os.path.dirname(os.path.abspath(__file__))
REF = os.path.join(HERE, '..', '..', 'reference', 'Jiangtherapee-Bridge-2.3.27-Windows-x64',
                   'resources', 'z8_presets')
COMP_NAME = {0: 'lossless', 3: 'HE*', 4: 'HE'}


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


def haar_var(p):
    hh, ww = p.shape
    hh -= hh % 2
    ww -= ww % 2
    q = p[:hh, :ww]
    return robust_stats((q[0::2, 0::2] - q[0::2, 1::2] - q[1::2, 0::2] + q[1::2, 1::2]) * 0.5)[1]


def frame_stats(Q):
    """512x512 Bayer quadrant -> 4-phase stats."""
    out = []
    for y, x in [(0, 0), (0, 1), (1, 0), (1, 1)]:
        p = Q[y::2, x::2]
        mean, var, rej = robust_stats(p)
        out.append(dict(mean_dn=mean, spatial_var=var, rejected=rej, haar_var=haar_var(p)))
    return out


def main():
    manifest = json.load(open(os.path.join(REF, 'manifest.json')))
    rows, temp_dev = [], []
    for prof in manifest['profiles']:
        a = tifffile.imread(os.path.join(REF, prof['dark_file'])).astype(np.float64)
        Qs = [a[:512, :512], a[:512, 512:], a[512:, :512], a[512:, 512:]]
        fs = [frame_stats(q) for q in Qs]

        # per-frame aggregate (same recipe as the table: sqrt of median of 4 phases)
        rn = [float(np.sqrt(np.median([ph['haar_var'] for ph in f]))) for f in fs]
        med = float(np.median(rn))
        mean_res = [float(np.mean([ph['mean_dn'] for ph in f])) - prof['black_dn'] for f in fs]

        # temporal: 6 pairwise same-site difference variances (robust), per phase
        tv = []
        for pi in range(4):
            pair_vars = []
            for i in range(4):
                for j in range(i + 1, 4):
                    # phase-select the difference to stay within one CFA channel
                    y, x = [(0, 0), (0, 1), (1, 0), (1, 1)][pi]
                    di = Qs[i][y::2, x::2]
                    dj = Qs[j][y::2, x::2]
                    pair_vars.append(robust_stats(di - dj)[1] / 2.0)
            tv.append(float(np.median(pair_vars)))
        mt = float(np.median(tv))
        ref_t = float(np.median([ph['temporal_variance_dn2'] for ph in prof['per_phase']]))
        if ref_t > 0:
            temp_dev.append((mt / ref_t - 1) * 100)

        rows.append(dict(
            compression=COMP_NAME[prof['compression']], iso=prof['iso'],
            frame_means_residual_dn=[round(v, 3) for v in mean_res],
            mean_residual_spread_dn=round(float(np.ptp(mean_res)), 3),
            frame_haar_rn_dn=[round(v, 3) for v in rn],
            haar_rn_median_dn=round(med, 3),
            haar_rn_spread_pct=round(float(np.ptp(rn)) / med * 100, 2),
            haar_rn_std_pct=round(float(np.std(rn, ddof=1)) / med * 100, 2),
            montage_temporal_var_dn2=[round(v, 3) for v in tv],
            manifest_temporal_var_dn2=round(ref_t, 3),
            temporal_dev_pct=round((mt / ref_t - 1) * 100, 2) if ref_t > 0 else None,
        ))
        print(f"{prof['compression_name']:>8} ISO{prof['iso']:>6}: RN 4帧={ [f'{v:.2f}' for v in rn] } "
              f"散布{rows[-1]['haar_rn_spread_pct']:.1f}%  残余散布{rows[-1]['mean_residual_spread_dn']:.2f} DN  "
              f"temporal偏差{rows[-1]['temporal_dev_pct']:+.1f}%", flush=True)

    with open(os.path.join(HERE, 'stats_upstream.json'), 'w') as f:
        json.dump(rows, f, indent=1)
    keys = ['compression', 'iso', 'mean_residual_spread_dn', 'haar_rn_median_dn',
            'haar_rn_spread_pct', 'haar_rn_std_pct', 'manifest_temporal_var_dn2', 'temporal_dev_pct']
    with open(os.path.join(HERE, 'upstream_interframe.csv'), 'w') as f:
        f.write(','.join(keys) + '\n')
        for r in rows:
            f.write(','.join(str(r[k]) for k in keys) + '\n')

    td = np.array(temp_dev)
    print(f'\n== 拼接图布局验证：6 帧对差方差(稳健) vs manifest temporal（102 档） ==')
    print(f'   中位 {np.median(td):+.2f}%  均值 {td.mean():+.2f}%  范围 [{td.min():+.1f}%, {td.max():+.1f}%]  |Δ|中位 {np.median(np.abs(td)):.2f}%')
    for comp in ['lossless', 'HE*', 'HE']:
        sp = np.array([r['haar_rn_std_pct'] for r in rows if r['compression'] == comp])
        mr = np.array([r['mean_residual_spread_dn'] for r in rows if r['compression'] == comp])
        print(f'== {comp}: 上游4帧 RN 帧间std/中位 中位{np.median(sp):.2f}% 范围[{sp.min():.2f},{sp.max():.2f}]%   '
              f'残余帧间极差 中位{np.median(mr):.2f} DN 最大{mr.max():.2f} DN')


if __name__ == '__main__':
    main()
