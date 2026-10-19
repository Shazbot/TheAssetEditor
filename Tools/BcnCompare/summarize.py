"""Summarises BcnCompare's CSVs into summary.md.  python summarize.py <result dir>"""
import csv, collections, statistics as st, sys, os

root = sys.argv[1] if len(sys.argv) > 1 else r"K:\projects\assedfork-bench\bcn-compare"
rows = list(csv.DictReader(open(os.path.join(root, "results.csv"))))
for r in rows:
    name = r["source"].lower()
    r["slot"] = next((s for s in ("base_colour", "material_map", "normal", "mask") if name.endswith("_" + s + ".dds")), "other")
    for key in list(r):
        if key not in ("source", "stage", "targetFormat", "setting", "encoder", "threads", "slot"):
            try:
                r[key] = float(r[key])
            except ValueError:
                pass

settings = ["DXT-Default", "DXT-Parallel", "DXT-Uniform", "BCn-Fast-MT", "BCn-Balanced-MT", "BCn-Best-MT", "BCn-Fast-ST", "BCn-Balanced-ST", "BCn-Best-ST"]
by = collections.defaultdict(dict)
for r in rows:
    by[int(r["sequence"])][r["setting"]] = r

out = []
def p(line=""):
    out.append(line)

def delta_stats(metric, group):
    res = {}
    for s in settings:
        if s.startswith("DXT"):
            continue
        d = [by[q][s][metric] - by[q]["DXT-Default"][metric] for q in group]
        res[s] = d
    return res

groups = collections.OrderedDict()
for fmt in ("BC1_UNORM_SRGB", "BC3_UNORM"):
    for slot in ("base_colour", "material_map", "normal", "mask"):
        g = [q for q, v in by.items() if v["DXT-Default"]["targetFormat"] == fmt and v["DXT-Default"]["slot"] == slot]
        if g:
            groups[(fmt, slot)] = sorted(g)

p("# BCnEncoder.Net vs DirectXTexNet on the optimizer's real encodes (!ak_teb3, atlas off)")
p()
p(f"{len(by)} captured encodes (all from the shared texture repack; the downscale re-encode path was not used in this run: all 7 downscales were mip slices).")
p()
p("## PSNR delta vs DXT-Default, mip 0 (dB, + means BCn is better). Uniform-RGB PSNR on the stored values.")
for (fmt, slot), group in groups.items():
    p(f"### {fmt} / {slot} ({len(group)} textures)")
    p("| setting | mean | median | worst | best | textures where BCn is worse (>0.5 dB) |")
    p("|---|---|---|---|---|---|")
    for s, d in delta_stats("rgb_psnr_mip0", group).items():
        p(f"| {s} | {st.mean(d):+.2f} | {st.median(d):+.2f} | {min(d):+.2f} | {max(d):+.2f} | {sum(1 for x in d if x < -0.5)} |")
    p()

for metric, title in (("wpsnr_mip0", "DirectXTex-weighted (0.2125/0.7154/0.0721) PSNR, mip 0"), ("rgb_psnr_chain", "uniform RGB PSNR, mip-chain average")):
    p(f"## PSNR delta vs DXT-Default: {title}")
    p("| group | " + " | ".join(s for s in settings if not s.startswith("DXT") and not s.endswith("ST")) + " |")
    p("|---|" + "---|" * 3)
    for (fmt, slot), group in groups.items():
        d = delta_stats(metric, group)
        p(f"| {fmt} / {slot} | " + " | ".join(f"{st.mean(d[s]):+.2f} (worst {min(d[s]):+.2f})" for s in settings if not s.startswith("DXT") and not s.endswith("ST")) + " |")
    p()

p("## BC3 channels, mip 0 (PSNR dB; DXT-Default | BCn-Balanced | BCn-Best; mean over textures). BC3 normal: R, G, B, A are separate data.")
for slot in ("normal", "mask"):
    g = groups.get(("BC3_UNORM", slot))
    if not g:
        continue
    p(f"### BC3 {slot} ({len(g)})")
    for ch in ("r", "g", "b", "a"):
        vals = [st.mean(by[q][s][f"{ch}_psnr_mip0"] for q in g) for s in ("DXT-Default", "BCn-Balanced-MT", "BCn-Best-MT")]
        p(f"- {ch.upper()}: {vals[0]:.2f} | {vals[1]:.2f} | {vals[2]:.2f}")
p()

p("## Worst 5 textures for BCn-Best-MT vs DXT-Default (uniform RGB PSNR mip 0 delta)")
deltas = sorted(((by[q]["BCn-Best-MT"]["rgb_psnr_mip0"] - by[q]["DXT-Default"]["rgb_psnr_mip0"], q) for q in by))
for d, q in deltas[:5]:
    r = by[q]["DXT-Default"]
    p(f"- {d:+.2f} dB  [{int(r['sequence'])}] {r['targetFormat']} {int(r['width'])}x{int(r['height'])} stage={r['stage']} {r['source']}")
p()
p("## Worst 5 textures for BCn-Balanced-MT vs DXT-Default")
deltas = sorted(((by[q]["BCn-Balanced-MT"]["rgb_psnr_mip0"] - by[q]["DXT-Default"]["rgb_psnr_mip0"], q) for q in by))
for d, q in deltas[:5]:
    r = by[q]["DXT-Default"]
    p(f"- {d:+.2f} dB  [{int(r['sequence'])}] {r['targetFormat']} {int(r['width'])}x{int(r['height'])} stage={r['stage']} {r['source']}")
p()

p("## Max absolute RGB error, mip 0 (mean / worst over textures)")
for fmt in ("BC1_UNORM_SRGB", "BC3_UNORM"):
    g = [q for q in by if by[q]["DXT-Default"]["targetFormat"] == fmt]
    p(f"- {fmt}: " + "; ".join(f"{s} {st.mean(by[q][s]['maxabs_rgb_mip0'] for q in g):.0f}/{max(by[q][s]['maxabs_rgb_mip0'] for q in g):.0f}" for s in ("DXT-Default", "DXT-Uniform", "BCn-Balanced-MT", "BCn-Best-MT")))
p()

p("## BC1 punch-through alpha")
bc1alpha = [q for q in by if by[q]["DXT-Default"]["targetFormat"] == "BC1_UNORM_SRGB" and by[q]["DXT-Default"]["inputHasAlphaBelow255"] == 1]
p(f"{len(bc1alpha)} BC1 inputs have some alpha < 255; texels with alpha < 128 at mip 0 across them: {int(sum(by[q]['DXT-Default']['alpha_lt128_texels_mip0'] for q in bc1alpha))}.")
for s in settings:
    tot0 = sum(by[q][s]["alpha_cut_mismatch_mip0"] for q in bc1alpha)
    totc = sum(by[q][s]["alpha_cut_mismatch_chain"] for q in bc1alpha)
    p(f"- {s}: alpha-cut mismatches mip 0 = {int(tot0)}, whole chain = {int(totc)}")
p()

p("## Encode time (sum over textures of the best of 3 runs, ms; CPU ms in brackets)")
p("| setting | threads | total ms | CPU ms | vs DXT-Default |")
p("|---|---|---|---|---|")
base = sum(by[q]["DXT-Default"]["encode_ms_min"] for q in by)
for s in settings:
    t = sum(by[q][s]["encode_ms_min"] for q in by)
    c = sum(by[q][s]["cpu_ms_min"] for q in by)
    p(f"| {s} | {by[next(iter(by))][s]['threads']} | {t:.0f} | {c:.0f} | {t / base:.2f}x |")
p()
p("Per format (ms): " + "; ".join(f"{fmt}: " + ", ".join(f"{s}={sum(by[q][s]['encode_ms_min'] for q in by if by[q]['DXT-Default']['targetFormat']==fmt):.0f}" for s in ("DXT-Default", "DXT-Parallel", "BCn-Balanced-MT", "BCn-Best-MT", "BCn-Balanced-ST", "BCn-Best-ST")) for fmt in ("BC1_UNORM_SRGB", "BC3_UNORM")))
p()

def read(name):
    path = os.path.join(root, name)
    return list(csv.DictReader(open(path))) if os.path.exists(path) else []

p("## Decoder agreement (BCnDecoder vs DirectXTex decode of the same BCn-encoded blocks)")
for r in read("decoder_agreement.csv"):
    p(f"- seq {r['sequence']} mip {r['mip']} {r['format']}: max channel difference {r['max_abs_diff_between_decoders']}, {r['texels_differing']}/{r['texels']} texels differ")
p()

p("## Managed mip generation vs DirectXTex GenerateMipMaps(DEFAULT)")
mips = read("mips.csv")
p("(One step at a time: level n is built from DirectXTex's own level n-1.)")
for cand in ("StoredBox", "StoredAlphaWeighted", "LinearBox", "LinearAlphaWeighted"):
    sel = [r for r in mips if r["candidate"] == cand]
    if sel:
        for srgb in ("1", "0"):
            s2 = [r for r in sel if r["srgb"] == srgb]
            if s2:
                p(f"- {cand}, srgb={srgb}: {len(s2)} mip levels; mean RGB PSNR {st.mean(float(r['psnr_rgb']) for r in s2):.2f} dB, min {min(float(r['psnr_rgb']) for r in s2):.2f}; mean A PSNR {st.mean(float(r['psnr_a']) for r in s2):.2f}; max abs diff {max(int(r['max_abs']) for r in s2)}; mean exact-match {st.mean(float(r['exact_match_percent']) for r in s2):.2f} %")
p()
p("## Managed resize (area-weighted box) vs DirectXTex Resize(BOX)")
resize = read("resize.csv")
for cand in ("StoredBox", "LinearBox"):
    for target in sorted({r["to"] for r in resize if r["candidate"] == cand}):
        pass
by_ratio = collections.defaultdict(list)
for r in resize:
    if not r["candidate"].startswith("DirectXTex-failed") and r["psnr_rgb"]:
        w0, h0 = map(int, r["from"].split("x"))
        w1, h1 = map(int, r["to"].split("x"))
        ratio = "1/2" if w1 * 2 == w0 else "1/4" if w1 * 4 == w0 else "3/4"
        by_ratio[(r["candidate"], r["srgb"], ratio)].append(r)
for key in sorted(by_ratio):
    s2 = by_ratio[key]
    p(f"- {key[0]}, srgb={key[1]}, ratio {key[2]}: {len(s2)} images; mean RGB PSNR {st.mean(float(r['psnr_rgb']) for r in s2):.2f}, min {min(float(r['psnr_rgb']) for r in s2):.2f}; max abs diff {max(int(r['max_abs']) for r in s2)}; mean exact-match {st.mean(float(r['exact_match_percent']) for r in s2):.2f} %")
failed = [r for r in resize if r["candidate"].startswith("DirectXTex-failed")]
if failed:
    p(f"- DirectXTex Resize(BOX) failed for {len(failed)} (image, size) pairs: " + ", ".join(sorted({f"{r['from']}->{r['to']}" for r in failed})))
thr = os.path.join(root, "alpha_threshold.csv")
if os.path.exists(thr):
    tr = list(csv.DictReader(open(thr)))
    first = lambda col: next(int(r["alpha"]) for r in tr if r[col] == "255")
    p()
    p(f"## BC1 punch-through threshold (uniform-alpha 4x4 blocks): first alpha value that stays opaque: DirectXTex alphaRef 0.5 -> {first('directxtex_alphaRef_0.5_decoded_alpha')}, BCnEncoder Bc1WithAlpha -> {first('bcn_bc1withalpha_decoded_alpha')}")
open(os.path.join(root, "summary.md"), "w", encoding="utf-8").write("\n".join(out) + "\n")
print("\n".join(out))
