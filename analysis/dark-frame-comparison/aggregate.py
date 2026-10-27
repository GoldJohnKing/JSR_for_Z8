#!/usr/bin/env python3
"""Aggregate: reference z8_presets manifest vs our captured dark frames.

Inputs (same dir): stats_ours.json (compute_ours.py)
Reference: ../../reference/Jiangtherapee-Bridge-2.3.27-Windows-x64/resources/z8_presets/manifest.json
Outputs: dark_comparison_summary.csv, dark_comparison_per_phase.csv,
         algorithm_fidelity_vs_manifest.csv, and PNG charts.
"""
import json, os
import numpy as np
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
import matplotlib.ticker as mticker
from matplotlib import font_manager

HERE = os.path.dirname(os.path.abspath(__file__))
REF = os.path.join(HERE, '..', '..', 'reference', 'Jiangtherapee-Bridge-2.3.27-Windows-x64', 'resources', 'z8_presets')
COMP_NAME = {0: 'lossless', 3: 'HE*', 4: 'HE'}
COMP_CN = {0: '无损', 3: '高压缩★', 4: '高压缩'}
COMP_COLOR = {0: 'tab:blue', 3: 'tab:green', 4: 'tab:red'}

for f in font_manager.findSystemFonts(fontpaths=['/usr/share/fonts']):
    if 'NotoSansCJK' in f or 'NotoSerifCJK' in f:
        font_manager.fontManager.addfont(f)
plt.rcParams['font.family'] = ['Noto Sans CJK JP', 'Noto Serif CJK JP', 'DejaVu Sans']
plt.rcParams['axes.unicode_minus'] = False


def style_iso_axis(ax, ticks, fontsize=6.2):
    """在有采样的 ISO 值处直接标注整数刻度，替代对数轴的科学计数法。"""
    ax.set_xticks(ticks)
    ax.set_xticklabels([str(int(v)) for v in ticks], rotation=90, fontsize=fontsize)
    ax.xaxis.set_minor_locator(mticker.NullLocator())


def rn_from_vars(vars4):
    return float(np.sqrt(np.median(vars4)))


def main():
    manifest = json.load(open(os.path.join(REF, 'manifest.json')))
    ours = json.load(open(os.path.join(HERE, 'stats_ours.json')))
    ref = {}
    for p in manifest['profiles']:
        ref[(p['compression'], p['iso'])] = p
    ours_by = {}
    for r in ours:
        ours_by[(r['comp'], r['iso'])] = r

    # ---------- summary CSV ----------
    rows = []
    for (comp, iso) in sorted(ours_by, key=lambda k: (k[0], k[1])):
        o = ours_by[(comp, iso)]
        f = ref[(comp, iso)]
        of = o['full']
        oc = o['crop1024']
        our_haar_rn = rn_from_vars([p['haar_var'] for p in of])
        our_haar_rn_c = rn_from_vars([p['haar_var'] for p in oc])
        our_spatial_rn = rn_from_vars([p['spatial_var'] for p in of])
        ref_haar_rn = f['haar_read_noise_dn']
        ref_temp_rn = f['temporal_read_noise_dn']
        ref_resid = float(np.mean([ph['residual_dn'] for ph in f['per_phase']]))
        our_resid = float(np.mean([p['mean_dn'] for p in of])) - 1008.0
        ref_mean = float(np.mean([ph['mean_dn'] for ph in f['per_phase']]))
        our_mean = float(np.mean([p['mean_dn'] for p in of]))
        rows.append(dict(
            compression=COMP_NAME[comp], backend_comp=comp, iso=iso,
            shutter_s_ours=o['shutter'], shutter_s_ref=f['shutter_seconds'],
            black_dn=f['black_dn'],
            ref_mean_dn=round(ref_mean, 3), our_mean_dn=round(our_mean, 3),
            delta_mean_dn=round(our_mean - ref_mean, 3),
            ref_residual_dn=round(ref_resid, 3), our_residual_dn=round(our_resid, 3),
            delta_residual_dn=round(our_resid - ref_resid, 3),
            ref_haar_read_noise_dn=round(ref_haar_rn, 3),
            our_haar_read_noise_dn=round(our_haar_rn, 3),
            our_haar_read_noise_dn_crop1024=round(our_haar_rn_c, 3),
            delta_read_noise_dn=round(our_haar_rn - ref_haar_rn, 3),
            delta_read_noise_pct=round((our_haar_rn / ref_haar_rn - 1) * 100, 2),
            ref_temporal_read_noise_dn=round(ref_temp_rn, 3),
            our_spatial_read_noise_dn=round(our_spatial_rn, 3),
        ))
    keys = list(rows[0].keys())
    with open(os.path.join(HERE, 'dark_comparison_summary.csv'), 'w') as fp:
        fp.write(','.join(keys) + '\n')
        for r in rows:
            fp.write(','.join(str(r[k]) for k in keys) + '\n')

    # ---------- per-phase CSV ----------
    with open(os.path.join(HERE, 'dark_comparison_per_phase.csv'), 'w') as fp:
        fp.write('compression,iso,phase,'
                 'ref_mean_dn,our_mean_dn,delta_mean_dn,'
                 'ref_residual_dn,our_residual_dn,delta_residual_dn,'
                 'ref_spatial_var_dn2,our_spatial_var_dn2,spatial_var_rel_dev_pct,'
                 'ref_haar_var_dn2,our_haar_var_dn2,haar_var_rel_dev_pct\n')
        for (comp, iso) in sorted(ours_by, key=lambda k: (k[0], k[1])):
            o = ours_by[(comp, iso)]
            f = ref[(comp, iso)]
            for i in range(4):
                rp = f['per_phase'][i]
                op = o['full'][i]
                fp.write(f"{COMP_NAME[comp]},{iso},{'{}{}'.format(*rp['phase'])},"
                         f"{rp['mean_dn']:.3f},{op['mean_dn']:.3f},{op['mean_dn']-rp['mean_dn']:+.3f},"
                         f"{rp['residual_dn']:.3f},{op['mean_dn']-1008.0:.3f},{op['mean_dn']-1008.0-rp['residual_dn']:+.3f},"
                         f"{rp['spatial']['variance']:.3f},{op['spatial_var']:.3f},{(op['spatial_var']/rp['spatial']['variance']-1)*100:+.2f},"
                         f"{rp['haar_variance_dn2']:.3f},{op['haar_var']:.3f},{(op['haar_var']/rp['haar_variance_dn2']-1)*100:+.2f}\n")

    # ---------- algorithm fidelity CSV (montage recompute vs manifest) ----------
    import tifffile

    def robust_stats(x):
        x = x.astype(np.float64).ravel()
        c = np.median(x)
        s = 1.4826 * np.median(np.abs(x - c))
        if s <= 0:
            lo, hi = np.quantile(x, [0.05, 0.95])
            s = float(np.std(x[(x >= lo) & (x <= hi)]))
        if s <= 0:
            return float(c), 0.0
        z = x
        for _ in range(2):
            z = x[np.abs(x - c) <= 4.5 * s]
            if z.size < 256:
                z = x
            c = float(z.mean())
            s = float(z.std())
        return c, s * s

    with open(os.path.join(HERE, 'algorithm_fidelity_vs_manifest.csv'), 'w') as fp:
        fp.write('compression,iso,metric,manifest_value,montage_recompute,rel_dev_pct\n')
        for p in manifest['profiles']:
            a = tifffile.imread(os.path.join(REF, p['dark_file'])).astype(np.float64)
            for i, (y, x) in enumerate([(0, 0), (0, 1), (1, 0), (1, 1)]):
                q = a[y::2, x::2]
                hh, ww = q.shape
                hh -= hh % 2
                ww -= ww % 2
                h = (q[:hh:2, :ww:2] - q[:hh:2, 1:ww:2] - q[1:hh:2, :ww:2] + q[1:hh:2, 1:ww:2]) * 0.5
                mmean, mhaar = robust_stats(q)[0], robust_stats(h)[1]
                rp = p['per_phase'][i]
                fp.write(f"{COMP_NAME[p['compression']]},{p['iso']},mean_dn_phase{i},{rp['mean_dn']:.4f},{mmean:.4f},{(mmean/rp['mean_dn']-1)*100:+.3f}\n")
                fp.write(f"{COMP_NAME[p['compression']]},{p['iso']},haar_var_phase{i},{rp['haar_variance_dn2']:.4f},{mhaar:.4f},{(mhaar/rp['haar_variance_dn2']-1)*100:+.3f}\n")

    # ---------- charts ----------
    isos = sorted({r['iso'] for r in rows})
    ISO_TICKS = [int(i) for i in isos]

    # fig 1: read noise vs ISO, three compression panels
    fig, axes = plt.subplots(1, 3, figsize=(15, 4.6), sharey=True)
    for ax, comp in zip(axes, [0, 3, 4]):
        rr = sorted([r for r in rows if r['backend_comp'] == comp], key=lambda r: r['iso'])
        x = [r['iso'] for r in rr]
        ax.plot(x, [r['ref_temporal_read_noise_dn'] for r in rr], '-', color='#999', lw=1.2,
                label='参考 temporal RN（manifest）')
        ax.plot(x, [r['ref_haar_read_noise_dn'] for r in rr], 'o-', ms=3.5, lw=1.4,
                color=COMP_COLOR[comp], label='参考 haar RN（manifest）')
        ax.plot(x, [r['our_haar_read_noise_dn'] for r in rr], 's--', ms=4, lw=1.4, mfc='none',
                color='tab:purple', label='实拍 haar RN（本项目算法复算）')
        ax.set_xscale('log')
        ax.set_yscale('log')
        style_iso_axis(ax, ISO_TICKS)
        ax.set_title(f'{COMP_CN[comp]}（{COMP_NAME[comp]}）')
        ax.set_xlabel('ISO')
        ax.grid(alpha=.3, which='both')
    axes[0].set_ylabel('读出噪声 (DN)')
    axes[0].legend(fontsize=8.5, loc='upper left')
    fig.suptitle('Z8 黑场读出噪声：上游参考标定 vs 实拍（FX，标称黑电平 1008 DN，14bit）')
    fig.tight_layout()
    fig.savefig(os.path.join(HERE, 'fig1_read_noise_vs_iso.png'), dpi=150)

    # fig 2: read-noise deviation in absolute DN + per-compression box
    fig, axes = plt.subplots(1, 2, figsize=(13, 4.8))
    ax = axes[0]
    for comp in [0, 3, 4]:
        rr = sorted([r for r in rows if r['backend_comp'] == comp], key=lambda r: r['iso'])
        ax.plot([r['iso'] for r in rr], [r['delta_read_noise_dn'] for r in rr], 'o-',
                ms=3.5, lw=1.2, color=COMP_COLOR[comp], label=COMP_CN[comp])
    ax.axhline(0, color='k', lw=.8)
    ax.set_xscale('log')
    style_iso_axis(ax, ISO_TICKS)
    ax.set_xlabel('ISO')
    ax.set_ylabel('读出噪声偏差 (实拍−参考, DN)')
    ax.grid(alpha=.3, which='both')
    ax.legend(fontsize=9)
    ax = axes[1]
    data, labels, colors = [], [], []
    for comp in [0, 3, 4]:
        rr = [r for r in rows if r['backend_comp'] == comp]
        data.append([r['delta_read_noise_dn'] for r in rr])
        labels.append(COMP_CN[comp])
        colors.append(COMP_COLOR[comp])
    bp = ax.boxplot(data, tick_labels=labels, patch_artist=True, widths=.5, showmeans=True)
    for patch, c in zip(bp['boxes'], colors):
        patch.set_facecolor(c)
        patch.set_alpha(.35)
    for med in bp['medians']:
        med.set_color('black')
    ax.axhline(0, color='k', lw=.8)
    ax.grid(alpha=.3, axis='y')
    ax.set_ylabel('读出噪声偏差 (实拍−参考, DN)')
    for i, comp in enumerate([0, 3, 4]):
        d = np.array([r['delta_read_noise_dn'] for r in rows if r['backend_comp'] == comp])
        ax.text(i + 1, ax.get_ylim()[1] * .92,
                f'中位 {np.median(d):+.2f} DN\n范围 {d.min():+.2f}~{d.max():+.2f} DN',
                ha='center', fontsize=9)
    fig.suptitle('实拍黑场 vs 参考标定：读出噪声偏差分布（绝对 DN，每压缩档 31 个 ISO）')
    fig.tight_layout()
    fig.savefig(os.path.join(HERE, 'fig2_read_noise_deviation.png'), dpi=150)

    # fig 3: residual pedestal vs ISO
    fig, axes = plt.subplots(1, 3, figsize=(15, 4.6), sharey=True)
    ymax = 0
    for ax, comp in zip(axes, [0, 3, 4]):
        rr = sorted([r for r in rows if r['backend_comp'] == comp], key=lambda r: r['iso'])
        x = [r['iso'] for r in rr]
        ax.plot(x, [r['ref_residual_dn'] for r in rr], 'o-', ms=3.5, lw=1.2,
                color=COMP_COLOR[comp], label='参考（manifest，4帧连拍拼接）')
        ax.plot(x, [r['our_residual_dn'] for r in rr], 's--', ms=4, lw=1.2, mfc='none',
                color='tab:purple', label='实拍（单帧全幅）')
        ax.set_xscale('log')
        style_iso_axis(ax, ISO_TICKS)
        ax.set_title(f'{COMP_CN[comp]}（{COMP_NAME[comp]}）')
        ax.set_xlabel('ISO')
        ax.grid(alpha=.3, which='both')
        ymax = max(ymax, max(r['ref_residual_dn'] for r in rr), max(r['our_residual_dn'] for r in rr))
    for ax in axes:
        ax.set_ylim(min(-2, -abs(ymax) * .05), ymax * 1.15)
    axes[0].set_ylabel('黑电平偏置 (mean−1008, DN)')
    axes[0].legend(fontsize=8.5)
    fig.suptitle('Z8 黑场黑电平偏置：参考 vs 实拍（标称黑电平 1008 DN）')
    fig.tight_layout()
    fig.savefig(os.path.join(HERE, 'fig3_residual_pedestal.png'), dpi=150)

    # ---------- fig 4: upstream inter-frame (same-parameter burst) inherent deviation ----------
    up_path = os.path.join(HERE, 'stats_upstream.json')
    if os.path.exists(up_path):
        up = json.load(open(up_path))
        up_by = {}
        for u in up:
            up_by.setdefault(u['compression'], []).append(u)
        ISO_TICKS_UP = sorted({int(u['iso']) for u in up})
        fig, axes = plt.subplots(1, 3, figsize=(16, 4.8))

        # (a) upstream 4-frame haar-RN spread vs ISO (absolute DN range)
        ax = axes[0]
        for cname in ['lossless', 'HE*', 'HE']:
            rr = sorted(up_by[cname], key=lambda u: u['iso'])
            ax.plot([u['iso'] for u in rr],
                    [max(u['frame_haar_rn_dn']) - min(u['frame_haar_rn_dn']) for u in rr], 'o-', ms=3,
                    lw=1.1, color=COMP_COLOR[{v: k for k, v in COMP_NAME.items()}[cname]],
                    label=COMP_CN[{v: k for k, v in COMP_NAME.items()}[cname]])
        ax.set_xscale('log')
        ax.set_yscale('log')
        style_iso_axis(ax, ISO_TICKS_UP, fontsize=5.6)
        ax.set_xlabel('ISO')
        ax.set_ylabel('4 帧 haar RN 帧间极差 (DN)')
        ax.set_title('(a) 上游同参数 4 帧连拍：读噪帧间偏差（DN）')
        ax.grid(alpha=.3, which='both')
        ax.legend(fontsize=8.5)

        # (b) upstream 4-frame residual pedestal spread vs ISO
        ax = axes[1]
        for cname in ['lossless', 'HE*', 'HE']:
            rr = sorted(up_by[cname], key=lambda u: u['iso'])
            ax.plot([u['iso'] for u in rr], [u['mean_residual_spread_dn'] for u in rr], 'o-', ms=3,
                    lw=1.1, color=COMP_COLOR[{v: k for k, v in COMP_NAME.items()}[cname]])
        ax.set_xscale('log')
        ax.set_yscale('log')
        style_iso_axis(ax, ISO_TICKS_UP, fontsize=5.6)
        ax.set_xlabel('ISO')
        ax.set_ylabel('4 帧黑电平偏置极差 (DN)')
        ax.set_title('(b) 上游 4 帧连拍：黑电平偏置漂移（Hi 档显著）')
        ax.grid(alpha=.3, which='both')

        # (c) distribution comparison in absolute DN: upstream per-frame |dev| vs ours-vs-ref |dev|
        ax = axes[2]
        data, labels, colors = [], [], []
        for cname in ['lossless', 'HE*', 'HE']:
            comp_key = {v: k for k, v in COMP_NAME.items()}[cname]
            data.append([abs(v - u['haar_rn_median_dn']) for u in up_by[cname] for v in u['frame_haar_rn_dn']])
            labels.append(f'{COMP_CN[comp_key]}帧间|Δ|')
            colors.append(COMP_COLOR[comp_key])
        for comp in [0, 3, 4]:
            data.append([abs(r['delta_read_noise_dn']) for r in rows if r['backend_comp'] == comp])
            labels.append(f'{COMP_CN[comp]}实拍↔参考')
            colors.append(COMP_COLOR[comp])
        bp = ax.boxplot(data, tick_labels=labels, patch_artist=True, widths=.6, showmeans=True)
        for patch, c in zip(bp['boxes'], colors):
            patch.set_facecolor(c)
            patch.set_alpha(.30)
        for med in bp['medians']:
            med.set_color('black')
        ax.set_ylabel('|RN 偏差| (DN)')
        ax.set_title('(c) 帧间固有偏差 vs 实拍↔参考偏差（DN）')
        ax.tick_params(axis='x', rotation=60, labelsize=8)
        ax.grid(alpha=.3, axis='y')
        med_up = np.median([abs(v - u['haar_rn_median_dn']) for u in up for v in u['frame_haar_rn_dn']])
        med_ours = np.median([abs(r['delta_read_noise_dn']) for r in rows])
        ax.axhline(med_up, color='#555', lw=1, ls=':')
        ax.axhline(med_ours, color='#555', lw=1, ls='--')
        ax.text(0.99, med_up, f'帧间|Δ|中位 {med_up:.2f} DN', ha='right', va='bottom', fontsize=8, transform=ax.get_yaxis_transform())
        ax.text(0.99, med_ours, f'实拍↔参考|Δ|中位 {med_ours:.2f} DN', ha='right', va='bottom', fontsize=8, transform=ax.get_yaxis_transform())

        fig.suptitle('拼接图拆分：上游同参数 4 帧连拍的固有帧间误差（四象限=同裁切区 4 帧，已验证）')
        fig.tight_layout()
        fig.savefig(os.path.join(HERE, 'fig4_upstream_interframe.png'), dpi=150)

        # ---------- fig 5: upstream 4-frame burst deviation of DN (pedestal) and RN ----------
        fig, axes = plt.subplots(1, 2, figsize=(14.5, 4.9))
        comp_of = {v: k for k, v in COMP_NAME.items()}

        # (a) DN deviation: per-profile 4-frame pedestal residuals (individual values)
        ax = axes[0]
        for cname in ['lossless', 'HE*', 'HE']:
            rr = sorted(up_by[cname], key=lambda u: u['iso'])
            c = COMP_COLOR[comp_of[cname]]
            xs = [u['iso'] for u in rr]
            for x, u in zip(xs, rr):
                v = u['frame_means_residual_dn']
                ax.vlines(x, min(v), max(v), color=c, alpha=.45, lw=1)
            ax.scatter(np.repeat(xs, 4), np.concatenate([u['frame_means_residual_dn'] for u in rr]),
                       s=5, color=c, alpha=.55, label=COMP_CN[comp_of[cname]], zorder=3)
        ax.axhline(0, color='k', lw=.7)
        ax.set_xscale('log')
        style_iso_axis(ax, ISO_TICKS_UP, fontsize=5.6)
        ax.set_xlabel('ISO')
        ax.set_ylabel('4 帧黑电平偏置 (DN)')
        ax.set_title('(a) DN 帧间偏差：每档 4 帧的黑电平偏置（竖线=帧间极差）')
        ax.grid(alpha=.3, which='both')
        ax.legend(fontsize=8.5, loc='upper left')
        ax.text(.98, .96, 'ISO≤32000 帧间极差最大 4.67 DN（HE* 16000）\nHi 档达 25.1 DN（lossless 102400）',
                transform=ax.transAxes, ha='right', va='top', fontsize=8,
                bbox=dict(fc='white', ec='#999', alpha=.85))

        # (b) RN deviation: per-frame haar RN minus profile median (absolute DN)
        ax = axes[1]
        for cname in ['lossless', 'HE*', 'HE']:
            rr = sorted(up_by[cname], key=lambda u: u['iso'])
            c = COMP_COLOR[comp_of[cname]]
            xs = [u['iso'] for u in rr]
            devs = [[v - u['haar_rn_median_dn'] for v in u['frame_haar_rn_dn']] for u in rr]
            for x, d in zip(xs, devs):
                ax.vlines(x, min(d), max(d), color=c, alpha=.45, lw=1)
            ax.scatter(np.repeat(xs, 4), np.concatenate(devs), s=5, color=c, alpha=.55,
                       label=COMP_CN[comp_of[cname]], zorder=3)
        ax.axhline(0, color='k', lw=.7)
        ax.set_xscale('log')
        style_iso_axis(ax, ISO_TICKS_UP, fontsize=5.6)
        ax.set_xlabel('ISO')
        ax.set_ylabel('4 帧 RN 偏差 (帧−中位, DN)')
        ax.set_title('(b) RN 帧间偏差：每档 4 帧 haar RN − 该档中位（竖线=帧间极差）')
        ax.grid(alpha=.3, which='both')
        ax.legend(fontsize=8.5, loc='upper right')
        ax.text(.02, .04, '全档 |帧−中位| 中位 0.04 DN；最大单帧偏差 +3.69 DN（HE* ISO102400），低感段最大 −0.28 DN（HE ISO500）',
                transform=ax.transAxes, ha='left', va='bottom', fontsize=8,
                bbox=dict(fc='white', ec='#999', alpha=.85))

        fig.suptitle('上游同参数 4 帧连拍：标定值本身的帧间偏差（DN 黑电平偏置 / RN）')
        fig.tight_layout()
        fig.savefig(os.path.join(HERE, 'fig5_upstream_dn_deviation.png'), dpi=150)

        # ---------- fig 6: low-ISO zoom (64-1600) of the 4-frame DN/RN deviation ----------
        band = [u for u in up if 64 <= u['iso'] <= 1600]
        iso_band = sorted({int(u['iso']) for u in band})
        worst_dn = max(band, key=lambda u: u['mean_residual_spread_dn'])
        worst_rn = max(band, key=lambda u: max(abs(v - u['haar_rn_median_dn']) for v in u['frame_haar_rn_dn']))

        fig, axes = plt.subplots(1, 2, figsize=(14.5, 4.9))

        # (a) DN deviation, zoomed
        ax = axes[0]
        for cname in ['lossless', 'HE*', 'HE']:
            rr = sorted([u for u in band if u['compression'] == cname], key=lambda u: u['iso'])
            c = COMP_COLOR[comp_of[cname]]
            xs = [u['iso'] for u in rr]
            for x, u in zip(xs, rr):
                v = u['frame_means_residual_dn']
                ax.vlines(x, min(v), max(v), color=c, alpha=.5, lw=1.4)
            ax.scatter(np.repeat(xs, 4), np.concatenate([u['frame_means_residual_dn'] for u in rr]),
                       s=14, color=c, alpha=.6, label=COMP_CN[comp_of[cname]], zorder=3)
        ax.axhline(0, color='k', lw=.7)
        ax.set_xscale('log')
        style_iso_axis(ax, iso_band, fontsize=7)
        ax.set_xlabel('ISO')
        ax.set_ylabel('4 帧黑电平偏置 (DN)')
        ax.set_title('(a) DN 帧间偏差（低感段放大）')
        ax.grid(alpha=.3, which='both')
        ax.legend(fontsize=8.5, loc='upper left')
        ax.text(.98, .96,
                f'本段帧间极差最大 {worst_dn["mean_residual_spread_dn"]:.2f} DN（{worst_dn["compression"]} ISO{worst_dn["iso"]}）',
                transform=ax.transAxes, ha='right', va='top', fontsize=8,
                bbox=dict(fc='white', ec='#999', alpha=.85))

        # (b) RN deviation, zoomed (absolute DN)
        ax = axes[1]
        for cname in ['lossless', 'HE*', 'HE']:
            rr = sorted([u for u in band if u['compression'] == cname], key=lambda u: u['iso'])
            c = COMP_COLOR[comp_of[cname]]
            xs = [u['iso'] for u in rr]
            devs = [[v - u['haar_rn_median_dn'] for v in u['frame_haar_rn_dn']] for u in rr]
            for x, d in zip(xs, devs):
                ax.vlines(x, min(d), max(d), color=c, alpha=.5, lw=1.4)
            ax.scatter(np.repeat(xs, 4), np.concatenate(devs), s=14, color=c, alpha=.6,
                       label=COMP_CN[comp_of[cname]], zorder=3)
        ax.axhline(0, color='k', lw=.7)
        ax.set_xscale('log')
        style_iso_axis(ax, iso_band, fontsize=7)
        ax.set_xlabel('ISO')
        ax.set_ylabel('4 帧 RN 偏差 (帧−中位, DN)')
        ax.set_title('(b) RN 帧间偏差（低感段放大，DN）')
        ax.grid(alpha=.3, which='both')
        ax.legend(fontsize=8.5, loc='upper right')
        dmax = max(abs(v - worst_rn['haar_rn_median_dn']) for v in worst_rn['frame_haar_rn_dn'])
        ax.text(.02, .04,
                f'本段最大单帧偏差 −{dmax:.2f} DN（{worst_rn["compression"]} ISO{worst_rn["iso"]}，DCG 档）',
                transform=ax.transAxes, ha='left', va='bottom', fontsize=8,
                bbox=dict(fc='white', ec='#999', alpha=.85))

        fig.suptitle('上游同参数 4 帧连拍帧间偏差：低感段放大（ISO 64–1600）')
        fig.tight_layout()
        fig.savefig(os.path.join(HERE, 'fig6_upstream_lowiso_deviation.png'), dpi=150)

        # ---------- fig 7: adjacent-frame pair deviation bars (same ISO), 64-1600 ----------
        import matplotlib.patches as mpatches
        pair_alphas = [1.0, 0.62, 0.38]
        pair_names = ['|F1−F2|', '|F2−F3|', '|F3−F4|']
        band_iso = sorted({int(u['iso']) for u in band})
        lookup = {(u['compression'], int(u['iso'])): u for u in band}
        x = np.arange(len(band_iso))
        bw = 0.085
        fig, axes = plt.subplots(1, 2, figsize=(16, 4.9))

        for ax, metric, ptitle in [
                (axes[0], 'frame_means_residual_dn', '(a) DN：相邻帧黑电平偏置差'),
                (axes[1], 'frame_haar_rn_dn', '(b) RN：相邻帧 haar RN 差')]:
            maxv, maxwhere = 0.0, ''
            group_vals = {}  # (cname, iso) -> [(val, pos), ...]
            for ci, cname in enumerate(['lossless', 'HE*', 'HE']):
                for pi in range(3):
                    for k, iso in enumerate(band_iso):
                        f = lookup[(cname, iso)][metric]
                        v = abs(f[pi + 1] - f[pi])
                        pos = x[k] + (ci * 3 + pi) * bw - (9 - 1) * bw / 2
                        group_vals.setdefault((cname, iso), []).append((v, pos))
                        if v > maxv:
                            maxv, maxwhere = v, f'{cname} ISO{iso} {pair_names[pi]}'
                    pos_arr = x + (ci * 3 + pi) * bw - (9 - 1) * bw / 2
                    vals = [abs(lookup[(cname, iso)][metric][pi + 1] - lookup[(cname, iso)][metric][pi])
                            for iso in band_iso]
                    ax.bar(pos_arr, vals, width=bw * 0.92, color=COMP_COLOR[comp_of[cname]],
                           alpha=pair_alphas[pi], edgecolor='none')
            # 每组（ISO×压缩）最高柱：组顶横线 + 数值标注
            for (cname, iso), items in group_vals.items():
                gmax = max(v for v, _ in items)
                xs = [p for _, p in items]
                c = COMP_COLOR[comp_of[cname]]
                ax.hlines(gmax, min(xs) - bw * 0.45, max(xs) + bw * 0.45, color=c, lw=0.7, alpha=.9)
                ax.text((min(xs) + max(xs)) / 2, gmax + maxv * 0.015, f'{gmax:.2f}',
                        rotation=90, fontsize=5.6, ha='center', va='bottom', color=c)
            ax.set_xticks(x)
            ax.set_xticklabels([str(v) for v in band_iso], rotation=90, fontsize=7)
            ax.set_xlabel('ISO')
            ax.set_ylabel('相邻帧偏差绝对值 (DN)')
            ax.set_title(ptitle)
            ax.grid(alpha=.3, axis='y')
            ax.set_ylim(0, max(maxv * 1.55, 0.05))
            ax.text(.99, .95, f'带内最大 {maxv:.2f} DN（{maxwhere}）', transform=ax.transAxes,
                    ha='right', va='top', fontsize=8, bbox=dict(fc='white', ec='#999', alpha=.85))
        handles = [mpatches.Patch(facecolor=COMP_COLOR[comp_of[c]], label=COMP_CN[comp_of[c]])
                   for c in ['lossless', 'HE*', 'HE']]
        handles += [mpatches.Patch(facecolor='#888', alpha=a, label=pair_names[i])
                    for i, a in enumerate(pair_alphas)]
        axes[0].legend(handles=handles, fontsize=7.5, ncol=2, loc='upper left')
        fig.suptitle('上游同参数 4 帧连拍：同 ISO 相邻帧标定值偏差（ISO 64–1600；每压缩 3 柱 = 3 个相邻帧对，帧序 F1=左上→F4=右下象限）')
        fig.tight_layout()
        fig.savefig(os.path.join(HERE, 'fig7_upstream_frame_pairs.png'), dpi=150)

        # ---------- fig 8: cross-machine per-frame dev (ours vs upstream each frame) vs same-machine max ----------
        comp_name_of = {0: 'lossless', 3: 'HE*', 4: 'HE'}
        up_lookup = {(u['compression'], int(u['iso'])): u for u in band}
        our_lookup = {(comp_name_of[r['backend_comp']], int(r['iso'])): r for r in rows}
        x8 = np.arange(len(band_iso))
        bw8 = 0.062
        cross_alphas = [0.80, 0.60, 0.45, 0.32]
        fig, axes = plt.subplots(1, 2, figsize=(16, 4.9))
        for ax, metric_ours, metric_up, ptitle in [
                (axes[0], 'our_residual_dn', 'frame_means_residual_dn', '(a) DN：黑电平偏置偏差'),
                (axes[1], 'our_haar_read_noise_dn', 'frame_haar_rn_dn', '(b) RN：haar 读噪偏差')]:
            sm_band_max, same_map, cross_map = {}, {}, {}
            for cname in ['lossless', 'HE*', 'HE']:
                gmax = 0.0
                for iso in band_iso:
                    f = up_lookup[(cname, iso)][metric_up]
                    same_map[(cname, iso)] = max(abs(f[p + 1] - f[p]) for p in range(3))
                    gmax = max(gmax, same_map[(cname, iso)])
                    our_v = float(our_lookup[(cname, iso)][metric_ours])
                    cross_map[(cname, iso)] = [abs(our_v - v) for v in f]
                sm_band_max[cname] = gmax
            ymax = max(max(same_map.values()), max(max(v) for v in cross_map.values()))
            off8 = (15 - 1) * bw8 / 2
            for ci, cname in enumerate(['lossless', 'HE*', 'HE']):
                c = COMP_COLOR[comp_of[cname]]
                pos0 = x8 + (ci * 5) * bw8 - off8
                posC = x8[:, None] + (ci * 5 + 1 + np.arange(4))[None, :] * bw8 - off8
                ax.bar(pos0, [same_map[(cname, iso)] for iso in band_iso], width=bw8 * 0.88,
                       color=c, hatch='//', alpha=.9)
                for k in range(4):
                    ax.bar(posC[:, k], [cross_map[(cname, iso)][k] for iso in band_iso],
                           width=bw8 * 0.88, color=c, alpha=cross_alphas[k])
            for cname in ['lossless', 'HE*', 'HE']:
                ax.axhline(sm_band_max[cname], color=COMP_COLOR[comp_of[cname]], ls='--', lw=1, alpha=.8)
            ax.set_xticks(x8)
            ax.set_xticklabels([str(v) for v in band_iso], rotation=90, fontsize=7)
            ax.set_xlabel('ISO')
            ax.set_ylabel('偏差绝对值 (DN)')
            ax.set_title(ptitle)
            ax.grid(alpha=.3, axis='y')
            ax.set_ylim(0, ymax * 1.4)
            all_cross = [v for lst in cross_map.values() for v in lst]
            cross_med = float(np.median(all_cross))
            ax.text(.99, .95, f'跨机柱（\|实拍−Fi\|，n=180）中位 {cross_med:.2f} DN，最大 {max(all_cross):.2f} DN\n'
                    f'同机带内最大 {max(sm_band_max.values()):.2f} DN', transform=ax.transAxes,
                    ha='right', va='top', fontsize=8, bbox=dict(fc='white', ec='#999', alpha=.85))
        comp_handles = [mpatches.Patch(facecolor=COMP_COLOR[comp_of[c]], label=COMP_CN[comp_of[c]])
                        for c in ['lossless', 'HE*', 'HE']]
        kind_handles = [mpatches.Patch(facecolor='#888', hatch='//', alpha=.9, label='同机最大（相邻帧对）')] + \
                       [mpatches.Patch(facecolor='#888', alpha=a, label=f'|实拍−F{i+1}|')
                        for i, a in enumerate(cross_alphas)]
        axes[0].legend(handles=comp_handles + kind_handles, fontsize=7, ncol=2, loc='upper left')
        fig.suptitle('跨机 vs 同机：DN/RN 偏差对比（ISO 64–1600；每压缩 5 柱 = 同机最大 + 实拍逐帧对比上游 4 帧；虚线=各压缩带内同机最大）')
        fig.tight_layout()
        fig.savefig(os.path.join(HERE, 'fig8_cross_vs_same_machine.png'), dpi=150)

    # ---------- console digest ----------
    print('=== 读出噪声（haar 口径）偏差统计（实拍 vs 参考，绝对 DN） ===')
    for comp in [0, 3, 4]:
        d = np.array([r['delta_read_noise_dn'] for r in rows if r['backend_comp'] == comp])
        p = np.array([r['delta_read_noise_pct'] for r in rows if r['backend_comp'] == comp])
        print(f'{COMP_CN[comp]:6s}: n={len(d)}  中位={np.median(d):+.3f} DN ({np.median(p):+.2f}%)  '
              f'RMS={np.sqrt((d ** 2).mean()):.3f} DN  范围=[{d.min():+.2f}, {d.max():+.2f}] DN')
    print('=== 黑电平偏置差（Δresidual, DN） ===')
    for comp in [0, 3, 4]:
        d = np.array([r['delta_residual_dn'] for r in rows if r['backend_comp'] == comp])
        rn = np.array([r['ref_haar_read_noise_dn'] for r in rows if r['backend_comp'] == comp])
        print(f'{COMP_CN[comp]:6s}: 中位={np.median(d):+.2f} DN  范围=[{d.min():+.2f}, {d.max():+.2f}] DN  '
              f'|Δ|/参考RN 中位={np.median(np.abs(d)/rn)*100:.1f}%  最大={np.max(np.abs(d)/rn)*100:.1f}%')
    # DCG rows check
    print('=== ISO 500 DCG 切换点（参考 vs 实拍 haar RN） ===')
    for comp in [0, 3, 4]:
        a = [r for r in rows if r['backend_comp'] == comp and r['iso'] in (400, 500)]
        for r in a:
            print(f'{COMP_CN[r["backend_comp"]]:6s} ISO{r["iso"]:>6}: ref={r["ref_haar_read_noise_dn"]:8.2f}  ours={r["our_haar_read_noise_dn"]:8.2f}')


if __name__ == '__main__':
    main()
