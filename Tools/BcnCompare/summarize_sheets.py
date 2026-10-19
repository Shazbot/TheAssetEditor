"""Summarises BcnCompare's sheets.csv / coverage.csv / remaining.csv.  python summarize_sheets.py <dir>"""
import csv, collections, math, statistics as st, sys, os

root = sys.argv[1]
def rd(n): return list(csv.DictReader(open(os.path.join(root, n))))
sheets, cov, rem = rd("sheets.csv"), rd("coverage.csv"), rd("remaining.csv")
out = []
def p(s=""): out.append(s)
def psnr(sse, texels): return 100.0 if sse <= 0 else min(100.0, 10 * math.log10(255.0 ** 2 / (sse / texels)))

p("# Shared repack block copy: results")
p()
p("## Final sheet quality against the captured composed input (uniform RGB PSNR dB; max abs RGB error)")
by = collections.defaultdict(dict)
for r in sheets: by[int(r["sequence"])][r["version"]] = r
groups = collections.OrderedDict()
for q, v in by.items():
    k = (v["dxt-encode-only"]["format"], v["dxt-encode-only"]["slot"])
    groups.setdefault(k, []).append(q)
p("| group (n) | metric | DirectXTex encode only (pre-copy) | old final (host-1) | new final (blockcopy) |")
p("|---|---|---|---|---|")
for k, g in groups.items():
    for metric, label in (("rgb_psnr_mip0", "mip0 RGB PSNR"), ("rgb_psnr_chain", "chain RGB PSNR"), ("maxabs_rgb_mip0", "mip0 max err (worst)")):
        cells = []
        for ver in ("dxt-encode-only", "old-final", "new-final"):
            vals = [float(by[q][ver][metric]) for q in g if ver in by[q]]
            if not vals: cells.append("n/a"); continue
            cells.append(f"{max(vals):.0f}" if metric.startswith("maxabs") else f"{st.mean(vals):.2f} (min {min(vals):.2f}, n={len(vals)})")
        p(f"| {k[0]} {k[1]} ({len(g)}) | {label} | " + " | ".join(cells) + " |")
p()
p("Per texture where old-final and new-final differ (mip0 RGB PSNR):")
for q in sorted(by):
    v = by[q]
    if "old-final" in v and "new-final" in v and v["old-final"]["rgb_psnr_mip0"] != v["new-final"]["rgb_psnr_mip0"]:
        p(f"- [{q}] {v['new-final']['source'].strip(chr(34))[-60:]}: encode-only {float(v['dxt-encode-only']['rgb_psnr_mip0']):.2f} / old final {float(v['old-final']['rgb_psnr_mip0']):.2f} -> new final {float(v['new-final']['rgb_psnr_mip0']):.2f}; max err {v['old-final']['maxabs_rgb_mip0']} -> {v['new-final']['maxabs_rgb_mip0']}")
p()

p("## Coverage (blocks)")
tot = collections.Counter()
for r in cov:
    for k in ("blocks_total", "copied", "gutter", "generated", "partial", "misaligned", "beyond_mips", "format_mismatch", "unreadable"):
        tot[k] += int(r[k])
p(f"All sheets: {tot['copied']:,} of {tot['blocks_total']:,} blocks copied ({100*tot['copied']/tot['blocks_total']:.2f} %); encoded {tot['blocks_total']-tot['copied']:,}: " + ", ".join(f"{tot[k]:,} {k}" for k in ("gutter", "generated", "partial", "misaligned", "beyond_mips", "format_mismatch", "unreadable") if tot[k]))
p()
p("### By format and texture type")
p("| format / slot | sheets | blocks | copied % | gutter | generated | partial | misaligned |")
p("|---|---|---|---|---|---|---|---|")
grp = collections.defaultdict(collections.Counter)
n = collections.defaultdict(set)
for r in cov:
    k = (r["format"], r["slot"])
    n[k].add(r["sequence"])
    for c in ("blocks_total", "copied", "gutter", "generated", "partial", "misaligned"):
        grp[k][c] += int(r[c])
for k in sorted(grp):
    g = grp[k]
    p(f"| {k[0]} {k[1]} | {len(n[k])} | {g['blocks_total']:,} | {100*g['copied']/g['blocks_total']:.1f} | {g['gutter']:,} | {g['generated']:,} | {g['partial']:,} | {g['misaligned']:,} |")
p()
p("### By mip level (all sheets)")
p("| level | blocks | copied % | gutter | generated | partial | misaligned |")
p("|---|---|---|---|---|---|---|")
lv = collections.defaultdict(collections.Counter)
for r in cov:
    for c in ("blocks_total", "copied", "gutter", "generated", "partial", "misaligned"):
        lv[int(r["level"])][c] += int(r[c])
for l in sorted(lv):
    g = lv[l]
    p(f"| {l} | {g['blocks_total']:,} | {100*g['copied']/g['blocks_total']:.1f} | {g['gutter']:,} | {g['generated']:,} | {g['partial']:,} | {g['misaligned']:,} |")
p()
p("### By sheet size class")
size = collections.defaultdict(collections.Counter)
for r in cov:
    side = max(int(r["width"]), int(r["height"]))
    for c in ("blocks_total", "copied", "gutter", "partial", "misaligned"):
        size[side][c] += int(r[c])
for s_ in sorted(size):
    g = size[s_]
    p(f"- longest side {s_}: {g['blocks_total']:,} blocks, {100*g['copied']/g['blocks_total']:.1f} % copied, {g['gutter']:,} gutter, {g['partial']:,} partial, {g['misaligned']:,} misaligned")
p()

p("## What is left for an encoder: quality of the still-encoded blocks only (PSNR over the texels of non-copied blocks)")
agg = collections.defaultdict(lambda: [0, 0.0, 0.0, 0, 0])
for r in rem:
    if r["fate"] == "copied": continue
    for key in ((r["format"], r["slot"], r["fate"], r["encoder"]), ("ALL", "ALL", r["fate"], r["encoder"]), ("ALL", "ALL", "ALL", r["encoder"]), (r["format"], r["slot"], "ALL", r["encoder"])):
        a = agg[key]
        a[0] += int(r["texels"]); a[1] += float(r["sse_rgb"]); a[2] += float(r["sse_a"]); a[3] = max(a[3], int(r["max_rgb"])); a[4] = max(a[4], int(r["max_a"]))
p("| format / slot | cause | texels | DXT-Default RGB PSNR (max err) | BCn-Balanced | BCn-Best |")
p("|---|---|---|---|---|---|")
keys = sorted({k[:3] for k in agg}, key=lambda k: (k[0] != "ALL", k))
for fmt, slot, fate in keys:
    cells = []
    texels = 0
    for enc in ("DXT-Default", "BCn-Balanced", "BCn-Best"):
        a = agg.get((fmt, slot, fate, enc))
        if not a: cells.append("-"); continue
        texels = a[0]
        cells.append(f"{psnr(a[1]/3, a[0]):.2f} dB ({a[3]})")
    p(f"| {fmt} {slot} | {fate} | {texels:,} | " + " | ".join(cells) + " |")
open(os.path.join(root, "summary.md"), "w", encoding="utf-8").write("\n".join(out) + "\n")
print("\n".join(out))
