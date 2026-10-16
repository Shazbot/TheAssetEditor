# Cross-rigid merge and texture memory: findings (2026-10-11, updated 2026-10-16)

Branch `feature/texture-atlas-consolidation`. Test pack: `!ak_teb3.pack` (dump in `K:\wh3mods\albion\teb`). In-game test assets: `K:\wh3mods\albion\abtest\` (see its `README.txt`).

## Summary

- The engine cost that merging reduces is **mesh parts per soldier**. Draw-call count and the number of distinct meshes did not affect frame rate in the test.
- The best case (10 parts per soldier collapsed to 1, screen full of one unit type) gave about **8 % FPS**. On this pack the tool's cross-rigid merging removes 2.6 % of soldier-part instances, worth roughly **0.2 % FPS** for 69 MiB of added geometry.
- The cross-rigid writer used to emit joint slots with more than five alternatives; the engine uses only five per slot, so output lost visual variety in-game. **Fixed** by nesting through generated group VMDs (see Open issues, item 1).
- Texture size is not expected to affect frame rate here (a working conclusion, not measured), so all texture work is judged on **video memory and pack size only**.
- Of the texture measures, only the shared-texture repack paid off on `ak_teb3`: 32.7 MB saved on four sets. Downscaling is down to 7 candidates (12.6 MB) once lords and heroes are excluded, and the other texture ideas were estimated and not built.
- Recommendation: do not build the shared-atlas planner or a per-configuration writer; stop extending the cross-rigid search; the slot limit is fixed, but given the payoff consider leaving cross-rigid off by default.

## How Warhammer 3 draws unit soldiers

From RenderDoc captures of the A/B test variants (Republican Guard, all soldiers at LOD 3):

| Variant | Parts per soldier | Mesh files per part | Soldier draw calls | Instances per call |
|---|---|---|---|---|
| A, split, identical | 10 | 1 | 10 | 346 |
| B, merged, identical | 1 | 1 | 1 | 346 |
| C, split, 8 byte-identical copies per slot | 10 | 5 used | 50 | 60–85 |
| D, merged, 8 byte-identical copies | 1 | 5 used | 5 | 62–75 |

- **One instanced draw per distinct mesh part in use**, whatever the soldier count. With about 3,300 soldiers on screen, B was still 1 call (3,309 instances) and C still 50.
- **Instancing is keyed on the mesh file.** Byte-identical copies under different names are drawn separately.
- **At most five alternatives per slot are used.** C and D listed 8 and the engine used exactly 5, the same five across all units. The mod author confirms the limit; it is worked around with nested (recursive) variant mesh definitions. In the source pack only 3 of 2,176 slots list more than five direct alternatives.
- **Slots vary independently**; the engine does not pick from whole-soldier presets.
- **Far soldiers are a proxy.** At deployment-camera distance every soldier was drawn by one call of a 188-triangle mesh (`DrawIndexedInstanced(564, ~1438)`); the VMD meshes are not rendered at all.
- Whole frames were about 290–620 draw calls.

## Frame-rate test

About 3,300 soldiers on screen, same scene and camera, GPU at 100 %:

| Variant | Parts per soldier | Soldier draw calls | FPS |
|---|---|---|---|
| A | 10 | 10 | about 26 |
| C | 10 | 50 | about 26 |
| B | 1 | 1 | about 28 |
| D | 1 | 5 | about 28 |

- A = C and B = D: a fivefold change in draw calls and distinct meshes made no difference.
- Split vs merged: about 2 FPS (roughly 8 %) for removing 90 % of soldier-part instances, with identical geometry and textures. Since the GPU was the bottleneck, at least part of the per-instance cost is GPU-side; what exactly was not identified.
- So the tool's `soldiers × parts` benefit model has the right shape, and merging varied slots into per-combination meshes does not hurt performance by multiplying meshes.

Limits of the test: single readings to the nearest FPS, no repeated runs, LOD 3 only, one machine, never CPU-bound.

## What the tool achieves on this pack

Baseline is 1,124 "expected battle draws" (soldier-part instances, army-weighted) over 172 analyzed root VMDs.

| | Saved | Share of baseline | Cost |
|---|---|---|---|
| Cross-rigid only, current gates | 29.9 | 2.6 % | 69 MiB geometry |
| Atlas + same-rigid merge + cross-rigid | 41.2 | about 3.7 % | 190 MiB textures, 52.5 MiB geometry |
| T0: today's merge identity, no writer limits | 26.6 | 2.4 % | – |
| T1: shared atlas (ceiling) | 173.6 | 15.4 % | 5.3 GiB geometry if all written |
| T1 under 64 / 128 / 256 MiB geometry cap | 63.7 / 87.2 / 113.7 | 5.7 / 7.8 / 10.1 % | plus about 175 / 221 / 283 MiB source textures to atlas |
| T3: T1 + weighted2/4 unified (approx.) | 213.7 | 19.0 % | – |
| T4: T3 + alpha unified (approx.) | 316.1 | 28.1 % | – |

T0 and the tiers were measured in combined mode, on top of atlases already built.

- **Cross-rigid merging is at its ceiling.** It selects 23.7 of a possible 26.6 in combined mode. More search breadth, a larger state cap or portfolio tuning can add at most about 3.
- **Materials are the limit.** 472 of 500 materials in the pack are distinct; ignoring the four atlasable textures there are 97 classes. Legacy `s_*` texture slots make no difference (T2 = T1).
- **Atlasing does not unlock cross-rigid merges today.** The atlas planner buckets merge affinity by geometry path and LOD (`BuildMergeAffinityGroups`, `PackTextureAtlasBatchService.AtlasPlanning.cs`), so it never places co-rendering parts from different rigids in one atlas.
- **A shared-atlas design is feasible but poor value.** All 172 roots expand exactly, no context exceeds 4,096 configurations, and the needed atlases are small (50 components at the 256 MiB cap, largest 31 materials, all but two fit one 4096² page). Using the frame-rate test's ratio (10 % fewer instances ≈ a little under 1 % FPS), the 256 MiB design is worth under 1 % FPS with the screen full of this mod's units.

Rough FPS equivalents, same ratio: current cross-rigid about 0.2 %; T1 ceiling a bit over 1 %; T4 ceiling about 2–2.5 %.

## Second pack: `ovn_albion.pack`

Combined mode (atlas, same-rigid merge, cross-rigid), run after the slot-limit fix. 68 gameplay-used root VMDs; run time 609 s.

| | `ak_teb3` (combined mode) | `ovn_albion` |
|---|---|---|
| Baseline expected draws | 1,124 | 1,570 |
| Cross-rigid saved | 23.7 (2.1 %) | 7.8 (0.5 %) |
| Cross-rigid payloads / geometry | 111 / 52.5 MiB | 19 / 24.2 MiB |
| Source VMDs rewritten | 43 | 4 |
| T0 ceiling (today's merge identity) | 26.6 (2.4 %) | 11.1 (0.7 %) |
| Atlas + same-rigid saved | 17.5 (1.6 %) | 39.8 (2.5 %) |
| New atlas textures | 189.8 MiB | 154.3 MiB |

- **Cross-rigid is worth even less here**: 0.5 % of soldier-part instances, roughly 0.05 % FPS by the frame-rate test's ratio. It is again close to its ceiling.
- **Atlasing with same-rigid merging does better** than on `ak_teb3` (2.5 % against 1.6 %). Everything the tool does today totals about 3 % of instances, roughly 0.3 % FPS.
- **Output is sound**: validation passes; 3 writer slots nested (largest 288 alternatives, depth up to 5, all exact), 336 group VMD files; no author-written slots above the five-alternative limit.
- **Shared-atlas ceilings are higher but still small**: T1 24.1 %, T3 27.3 %, T4 35.5 %; under a 64 / 128 MiB geometry cap 5.8 / 9.4 % (about 0.5 / 0.8 % FPS), needing 10 / 16 small shared atlases that all fit one page, built from about 95 / 143 MiB of source textures.

Both packs lead to the same conclusion: cross-rigid merging is not worthwhile and should stay off by default.

## Atlasing: cost and benefit

| | `ak_teb3` | `ovn_albion` |
|---|---|---|
| Soldier-part instances removed (atlas + same-rigid merge) | 1.6 % | 2.5 % |
| Atlas textures added to the pack (disk) | 189.8 MiB | 154.3 MiB |
| Expected loaded atlas memory in battle | 6.9 MiB | about 14.9 MiB |
| Expected source memory no longer loaded | 1.1 MiB | 7.1 MiB |
| Net expected video memory growth | 5.9 MiB | 7.8 MiB |
| Atlas planning time | 540-740 s | 402 s |

- **The large cost is pack size and run time, not video memory.** The value gate models, per texture, the probability that it is loaded in a battle before and after atlasing; a vanilla texture the mod no longer references stops counting unless a vanilla unit in the battle needs it. These are scenario averages, not a peak.
- **The gain is small because same-rigid parts are split by shader, not texture.** In the `ak_teb3` dump a WSModel has on average 2.08 distinct materials at LOD 0 and 1.96 if the four atlasable textures are ignored, so a perfect atlas removes about 6 % of the parts inside a rigid (rough, unweighted, about half the WSModels parsed). Texture differences sit between rigids, which is why the cross-rigid shared-atlas ceiling is much higher.
- Atlasing is now opt-in ("Build texture atlases (experimental)", off by default). With it off the tool applies structural-only merges and geometry optimisation.

## Where the frame time goes, and the LOD audit

Render-scale test, variant B, about 3,300 soldiers: 30 FPS at 100 % scale, 46 FPS at 50 %, GPU usage 98-100 % at both. Quartering the pixels saved 11.6 ms of a 33.3 ms frame, so roughly half the frame (about 15.5 ms) depends on pixel count and half (about 18 ms) is GPU work on geometry: vertices and soldier-part instances.

LOD audit of `ak_teb3` (172 roots, 287 source-owned and 299 vanilla rigids, 10 s):

- The mod's LODs follow a uniform 100 / 75 / 50 / 25 % triangle pattern; a typical soldier is 9,000-20,000 triangles at LOD 0 and 1,600-4,500 at the last LOD, in 4-10 mesh parts.
- 227 of 287 source-owned rigids switch at 10 / 20 / 30 m, so in an ordinary battle view nearly every soldier is at the last LOD. The tool's scenario model assumes 25 % of soldiers at each LOD, which overweights LOD 0.
- 31 rigids use 100 / 200 / 300 / 400 m and 2 crests use 1,000,000 on every LOD; 113 unit looks mix profiles. **These may be the author's intent; LOD geometry and switch distances are out of scope for the tool by the author's decision.**
- Possible defects to report to the author: `ak_teb_freecomp_legs_estalian_01` has zero triangles at LOD 2 and 3; the `lorenzo_lupo` parts have no LOD reduction.

With LOD geometry and distances out of scope, the geometry-side levers left to the tool are parts per soldier (merging, measured at about 2.7 ms for removing 90 % of them) and nothing else of size.

### Bone weights per vertex (report only, `PackTextureAtlasBatchService.BoneWeightAudit.cs`)

The idea: convert 4-weight vertices (`Cinematic`, `weighted4_*` shaders) to 2-weight (`Weighted`, `weighted2_*`) where the skinning result is the same. That halves the bone-matrix fetches per vertex and saves 4 bytes per vertex.

What the shaders do, from the Patch 8.1 vertex-shader disassembly:

- **`weighted2`** reads one weight byte w0 and uses 1 − w0 for the second bone; the stored second byte is ignored.
- **`weighted4`** reads three weight bytes and ignores the fourth. A fourth, implicit bone gets 1 − (w0+w1+w2) whenever b0+b1+b2 <= 254.
- So a vertex converts exactly when at most 2 distinct bones have an effective weight, counting the implicit one.

`ak_teb3` (all 287 source-owned rigids, 1.3 s):

| | Meshes | Vertices |
|---|---|---|
| 4-weight, all LODs | 1,049 | 1.17 M (LOD 0 and 1: 1,033 meshes, 1.13 M vertices) |
| 2-weight (LOD 2 and below, CA's convention) | 1,091 | 663 k |
| 4-weight vertices that convert exactly, one by one | – | 837 k (72 %) |
| 4-weight meshes where every vertex converts | 278 | 153 k (13 %) |
| Same, weighted by expected battle part instances | – | 13.0 % |

- **Memory:** 617 KB of vertex data.
- **Shader twins:** most convertible meshes use `weighted4_character(_alpha)`, whose `weighted2_` twins have the same texture channels. Four small families have twins whose channels differ (`character_emissive`, `default_alpha`, `random_colours`, `tinted_metal_skin`); one has no twin in the dump.
- **Verdict: not worth building.**
  - 4-weight geometry exists only at LOD 0 and 1, and most soldiers switch to their last LOD at 30 m, so in an ordinary battle view little of it is drawn.
  - Of what is drawn, 13 % would lose half its skinning fetches, which is likely well under 1 % FPS.
  - The rewrite (vertex formats plus material shaders) would carry more risk than any texture stage.

## Texture memory

Everything in this section is about video memory and pack size. None of it is expected to change frame rate. Numbers are for `ak_teb3` unless stated.

### Texture size and frame rate

- **Working conclusion: texture size does not affect FPS in this setup.** No A/B test with different texture sizes was run, so there is no direct measurement.
- **Supporting evidence:** the render-scale test (above) showed about half the frame depends on pixel count and the rest is GPU work on geometry and instances. At battle distance the GPU already samples lower mips, so removing the top mip does not reduce pixel work.
- All texture work below is therefore judged on memory only.

### Texture downscaling (`PackTextureAtlasBatchService.TextureDownscale.cs`)

- **Report and opt-in option.** "Downscale oversized textures (experimental)" (`downscaleTextures` on `Process`), off by default. The report always lists the candidates, whether or not the option is ticked. Disabled in cross-rigid-only mode.
- **Rule.** A texture file shrinks to what its users' texel density needs, using the atlas stage's reference density and 0.5 / 0.25 buckets. A candidate is at most 0.5x and both sides stay at least 256 px. Same path and format, so no material changes; mip slicing is used where possible.
- **Alongside atlasing.** Atlases are built from full-resolution textures first. Atlas textures are never downscaled; only textures the atlases did not replace are rewritten.
- **Lord/hero rule.** No texture used by a lord or hero is downscaled, and units that cannot be classified count as lord/hero. The first report listed 20 candidates (71.3 MB), three quarters of it one lord's textures; with the rule applied they are gone.
- **Result.** 7 candidates remain, all morion helmet textures at 2048x1024 to 1024x512, saving 12.6 MB. Another 135 textures are assessed and not oversized.

### Vanilla texture coverage (report only, `PackTextureAtlasBatchService.VanillaTextureCoverage.cs`)

Measures how much of each vanilla texture the pack's parts touch (256x256 cell grid, every LOD). A per-texture crop would replace the vanilla texture with a smaller copy in the pack.

- The pack references 690 vanilla textures (663 MB).
- Whole-pack upper bounds for cropping:

| Limit | Textures | Saved (at most) | Added to pack | Rewritten |
|---|---|---|---|---|
| Under 10 % coverage | 95 | 127 MB | 17 MB | 51 rigids |
| Under 25 % coverage | 219 | 217 MB | 71 MB | 104 rigids plus 4 vanilla copies |

- Nothing is saved above 25 % coverage, because a half-size crop cannot hold the used area.
- These are upper bounds. Whether the vanilla texture stays loaded anyway (another unit or mod part uses it) was not computed, because the existing residency model has side effects. Not built.

### Per-unit repack estimate (report only, `PackTextureAtlasBatchService.UnitTextureRepack.cs`)

Estimates repacking textures into atlases per unit, assuming a unit shows every alternative whenever it is on the field.

- **Sharing is heavy.** Only 12 of 392 rigids and 16 of 911 textures are used by a single unit. 528 of 657 units fall in one connected sharing group.

| Option | Idea | Whole pack (all units loaded) | Verdict |
|---|---|---|---|
| A | One atlas per unit, with rigid copies | 1.02 GB to 4.2 GB | Negative |
| B | One atlas per sharing group | Big group's atlas is 273 MB; 607 of 657 units get worse | Negative |
| C | One atlas per signature (exact set of units using a texture) | 1,021 MB to 721 MB; 348 units better, 309 unchanged, none worse | Never worse |
| C2 | C, merging signatures with at least 80 % overlap | 3 fewer atlases, 34 units worse | Worthless |

- **Option A details.** 4.1 GB of atlases are added and 1,834 rigid copies are needed. Sibling pairs get worse: Tilean militia variants 50.5 to 53.7 MB, Conquistador variants 53.8 to about 81 MB.
- **Option C selective builds** (best sets first): 5 groups 68 MB, 10 groups 111 MB, 25 groups 186 MB (43 rigids), 50 groups 247 MB, all 131 groups 301 MB.
- **Estimates ran optimistic.** They come from a coverage grid with 25 % packing slack; see the built result below.

### Shared texture repack, first increment (`PackTextureAtlasBatchService.SharedTextureRepack.cs`)

Built, opt-in: "Repack shared textures (experimental)" (off by default). New smaller textures hold only the regions a rigid's parts use, at the same texel density; the rigid's UVs are rewritten for all LODs, new materials are written and its WSModels repointed. Original textures are not edited.

- **Scope.** Option C sets confined to one mod-owned rigid. Cannot be combined with cross-rigid-only mode (which never writes textures). It can run together with atlasing and cross-rigid merges, in that order (atlasing, cross-rigid merges, repack): atlasing runs first, a rigid atlasing changed in any way is left out of the repack (reason "the rigid was changed by texture atlasing"), and an atlased part no longer counts as a user of the textures it used, so each part and texture is changed by at most one of the two. The thresholds and the actual-bytes re-check are unchanged. Cross-rigid merges keep their full candidate set and the repack gives way: the source rigids the writer built generated models from (`CrossRigidChangedRigids`) are left out (reason "the rigid was changed by cross-rigid merging"), and the generated rigids, which copy source parts with their original materials and so keep those textures loaded, block every set containing one of those textures. Source parts are not exempted (the source files are kept and may stay reachable).
- **Chart snapping.** Charts are snapped to 32 px so mips 0-3 are block-copied exactly. This fixed a bug: mip-3 errors came from regions offset by 2 texels against the 4x4 block grid.
- **Pixel check before writing.** Block-copied levels must match exactly. Re-encoded levels fail if more than 0.5 % of samples are over 96 or the mean is over 12.
- **Layout.** Layout groups combine tuples with the same slots, formats and ratios. Pages split at 4096. A group needs at least 512 KiB saved and a set at least 2 MiB.
- **Pass-through.** Auxiliary slots (blood, dither, distortion, distortion noise) pass through, and `blood_uv_scale` is divided by the UV scale. Tiny uniform textures are kept as authored.
- **Output paths.** Generated vanilla-material copies go under `variantmeshes\asset_editor\repack_materials\`, textures under `textures\asset_editor\atlases\*_repack_*.dds`. The rigid's embedded material is not updated (same as existing atlas practice).

Selection funnel:

| Step | Sets |
|---|---|
| Considered | 299 |
| Estimated saving below threshold | 261 |
| Not confined to one rigid | 22 |
| Rigid not eligible | 1 |
| Dropped by closure (15 parts whose material uses textures outside the set) and related checks | 5 |
| Failed the bytes gate (near-full coverage) | 6 |
| Failed the pixel check | 0 |
| **Built** | **4** |

Built sets:

| Set (rigid) | Before | After |
|---|---|---|
| `ak_teb_lupio_demi_body` | 11.18 MB | 5.59 MB |
| `teb_golden_lion_stuff` (4 tuples, 2 pages) | 17.48 MB | 11.32 MB |
| `ak_teb_lupio` | 11.18 MB | 0.70 MB |
| `teb_inquisitor_staff_02` | 11.18 MB | 0.70 MB |
| **Total** | **51.0 MB** | **18.3 MB** |

- **Saving 32.7 MB.** Zero pixel error on about 101,500 top-mip samples; validation passes; the stage takes 9.2 s.
- **The estimate was optimistic.** The demigryph body was estimated at 16.8 MB saved and saved 5.6 MB; the lion was estimated at 11.9 MB and saved 6.2 MB.
- **Checked in-game by the author:** Lupio, the lion and the inquisitor staff look fine. Blood density with blood enabled is not yet checked, and the `blood_uv_scale` rule is an assumption.
- **What was left:** the 22 multi-rigid sets and the 5 closure-dropped sets. Built in the next increment, below.

### Shared texture repack, multi-rigid (same file)

The repack now takes any signature set whose users are all mod-owned rigids, and rewrites every rigid using its textures consistently against one set of new textures.

- **Flat textures.** A texture of any size that is one flat colour is kept as authored and no longer blocks a part.
- **Merged plans.** When a material's textures belong to several signature sets, those sets are pulled into one plan, up to 8 sets and 16 rigids.
  - A plan is written completely or not at all.
  - A failed merged plan falls back to its seed set alone.
  - Validation checks that no unit loads more texture bytes than before.
- **Page splitting.** A layout group whose power-of-two page cannot shrink puts whole clusters of charts on up to 4 smaller pages. A chart is never duplicated and a mesh part is never split.

Results (validation passes, zero pixel error on every sample):

| | `ak_teb3` | `ovn_albion` |
|---|---|---|
| Signature sets considered | 299 | 211 |
| Below the 2 MiB estimate threshold | 254 | 155 |
| Merged into another set's plan | 10 | 16 |
| Dropped by closure, variants, tiling or ineligible rigids | 12 | 20 |
| Failed the bytes gate (near-full coverage) | 16 | 6 |
| **Built** | **7 sets, 12 rigids** | **14 sets, 28 rigids** |
| Texture bytes replaced → new | 76.5 → 25.2 MB | 172.3 → 43.3 MB |
| **Saved** | **51.4 MB** | **129.0 MB** |
| Of which from merged plans | 0 | 8.4 MB (Chaos lord capes) |
| Of which from page splitting | 2.8 MB | 2.1 MB |
| Pixel samples checked | 190,536 | 1,054,264 |
| Stage time | 14.7 s | 21.5 s |

- **Most rounding losses are single-cluster groups.** A group whose charts form one connected cluster cannot split (7 groups on `ak_teb3`, 18 on `ovn_albion`).
- **Many repacked textures are vanilla.** A vanilla unit in the same battle keeps the original loaded, so these savings are whole-pack upper bounds.
  - Copying vanilla rigids to unlock more sets was not done.
  - Only 3 such blockers exist: the two demigryph rigids and `brt_men_at_arms_helmet_01`.

### Exact texture compaction (`PackTextureAtlasBatchService.TextureCompaction.cs`)

Opt-in, "Compact textures exactly (experimental)", off by default and disabled in cross-rigid-only mode. It runs last, on the output's DDS files, and rewrites textures in place (same path, same sRGB-ness, same header kind).

- **Flat textures.** A texture whose every texel at every mip level is one exact RGBA becomes 4x4 with a 3-level chain.
- **Opaque BC2/BC3.** A BC2/BC3 texture whose alpha is 255 at every texel becomes BC1. The colour blocks are kept; where c0 < c1 the endpoints are swapped and the indices remapped, and equal endpoints get index 0.
- **Verified before writing.** Every result is decoded and compared texel for texel at every level before it is written, and validation checks it again.
- **What counts as a reference.** Only textures the pack owns that a material references are eligible: an xml material slot, or the texture list embedded in a rigid's material.
  - The embedded lists include the old WH2 slots (diffuse, specular, gloss map).
  - A texture also named by a UI, VMD, WSModel or other non-material file is skipped.
  - An unreadable rigid's texture paths count as non-material references.
- **Not covered.** Textures written by the atlas, repack or downscale stages are skipped, and so is BC7 (no exact decoder in the codebase).

| | `ak_teb3` | `ovn_albion` |
|---|---|---|
| DDS files in the output | 533 | 701 |
| Flat, shrunk to 4x4 | 6 (24.1 MB saved) | 13 (18.2 MB saved) |
| Opaque BC3 → BC1 | 37 (22.4 MB saved) | 13 (38.8 MB saved) |
| **Saved** | **46.5 MB** | **57.0 MB** |
| Referenced only by a rigid's embedded material | 120 | 115 |
| Skipped as BC7 | 21 | 10 |
| Stage time | 4.3 s | 6.1 s |

- **Largest items.**
  - `ak_teb3`: `lorenzo_mask.dds`, a flat 4096x4096 BC3 (22.4 MB → 176 bytes), and the green dragon's body and wing textures (5.6 → 2.8 MB each).
  - `ovn_albion`: `mask_alb_shield.dds` and `alb_truthsayer_body_mask.dds`, both opaque 4096x4096 BC3 (22.4 → 11.2 MB each).
- **Checked in-game by the author on `ak_teb3`:** no visible change.
- **Part of the saving is pack size only.** The game ignores a rigid's embedded textures when the rigid has a WSModel, and some rigids are not used at all (for example `ak_grave_de_sword_2h_*` and `ak_kf_lupio`). Rewriting those textures is harmless, just useless.

### Which texture channels the shaders read (`Wh3ShaderTextureChannels.json`)

The game's compiled shader cache is on the `shaders` branch of the dump repo `K:\projects\wh3dump` (Patch 8.1, build of 12/07/2026). `Tools/ShaderChannels/extract_shader_channels.py` builds the table from it:

- it extracts the compiled pixel shaders of the `weighted2_*`, `weighted4_*` and `rigid_*` shaders (4,932 blobs, 425 shaders);
- it disassembles them with `fxc.exe /dumpbin` from the Windows SDK;
- for each `t_xml_*` slot it records the channels any permutation samples.

The table is embedded in the tool. Rerun the script after a game patch.

| Slot | Channels read |
|---|---|
| `base_colour`, non-alpha shaders (`*_character`, `*_default`, `*_faction_colours`, `*_random_colours`) | RGB |
| `base_colour`, `*_alpha` and `alphatint` shaders, `weighted4_character_emissive` | RGBA |
| `material_map`, every shader | RG |
| `normal`, every shader | GA (alpha holds X) |
| `mask` | RGBA, or not bound (`random_colours`, `faction_colours`, `default`) |

### Dropping alpha no shader reads (same file, opt-in)

"Drop texture alpha no shader reads (experimental)", available only while compaction is ticked. A BC2/BC3 texture with real alpha becomes BC1 when:

- every user is an xml material whose shader is in the table, binds the texture's slot and does not read its alpha;
- every rigid that names it is drawn only through owned WSModels, checked with the same rules as the repack;
- it does not override a vanilla texture, and the vanilla game data is loaded.

RGB stays bit-exact and is verified at every level; only the alpha is dropped.

- **`ak_teb3`:** 12 textures, 24.8 → 12.4 MB (saves 12.4 MB). They are the morion guard and brass base colours, `ak_teb_lupio_kf_base`, `ak_teb_gambeson_base` and two knight leg base colours, all used by `weighted2_character`/`weighted4_character`.
- **Correctly kept back:**
  - 22 textures whose alpha is read (alpha-shader base colours, normals, masks);
  - 34 named by rigids that VMDs reference directly (mostly campaign ships);
  - 30 named by rigids whose consumer discovery is incomplete;
  - 1 vanilla override.
- **Checked in-game by the author** (morions, Lupio, the gambeson): no visible change. That supports the assumption that the material's shader is the one the game uses.
- **Compaction total on `ak_teb3` with the option:** 58.9 MB (24.1 MB flat, 22.4 MB opaque, 12.4 MB unread alpha).

## Open issues

1. **Joint slots exceeded five alternatives — fixed.** The writer created one `SLOT` with one wrapper per Cartesian combination, plus duplicates for optional-slot probabilities; the engine used five, and rewritten units visibly lost variety in-game. Writer-created slots are now split into at most five `VARIANT_MESH_REFERENCE` entries to generated group VMDs (`NestCrossRigidWriterSlots` in `PackTextureAtlasBatchService.CrossRigid.Emission.cs`), with a hard check at the end of emission. On this pack (combined mode): 71 slots nested, largest 3,000 alternatives, nesting depth up to 6, all splits exact, 6,542 group files reduced to 1,986 by dedupe; validation passes and the rewritten units look correct in-game. Author-written slots are left alone; three in the source pack list six alternatives (`ak_est_trooper_base` head, `ak_morion_pirazzo` bighead, `bor_strooper_base` head).
2. **The portfolio search no longer improves results** (baseline = best in every run since the per-payload ceiling) and optimises a different gate than the final writer. Kept at the author's request.
3. **Ten tests in `Test.KitbashEditor` fail** (`LoadAndSave_Rome2_*`, `Warhammer3_SaveKarl_*`, `ArgumentOutOfRangeException: length ('-1')`). Not investigated; ignored at the author's request.
4. **Blood density is unchecked** after the shared texture repack. The `blood_uv_scale` division is an assumption; check a repacked unit with blood enabled.
5. **The repack estimate is optimistic.** Built savings were 5.6 of 16.8 MB (demigryph body) and 6.2 of 11.9 MB (lion), so the Option C figures in the per-unit estimate should be read as upper bounds.
6. **The "stays loaded anyway" probability is not computed** for vanilla texture cropping, so its 127 / 217 MB figures are upper bounds.
7. **Repack savings on vanilla textures are upper bounds.** Vanilla units in the same battle keep the original textures loaded.
8. **BC7 textures are not compacted.** There is no exact BC7 decoder in the codebase (21 textures on `ak_teb3`, 10 on `ovn_albion`).
9. **The shader channel table is tied to one game build** (Patch 8.1). Regenerate it after a patch that changes shaders. `weighted2_random_colours_alpha` is missing from the dump, so textures it uses are never alpha-dropped.

## Changes made this session

- Far-LOD fix: sources past their last LOD distance use their last LOD instead of being dropped from the merged payload.
- Union LOD schedule samples the near edge of each interval instead of the midpoint.
- Payloads whose value sits in pair-fallback plans are routed to the supplementary stage instead of being rejected as zero-benefit (no effect on this pack).
- Per-payload cost ceiling (`CrossRigidPerPayloadCostCeilingMultiplier = 1.0`): geometry 238 → 69 MiB with draws unchanged.
- Emission save-and-reload replaced with owned copies (`MergeRmvModelsOwned`): emit phase 60 s → 4 s.
- Report additions: cost curve, missing-component counter, baseline expected draws, ideal-merge tiers with payload, state-cap, atlas-component and baked-variant estimates.
- `PackTextureAtlasBatchService.cs` split into 16 partial-class files.
- Writer-created slots nested to respect the engine's five-alternative limit.
- Cross-rigid merging is now opt-in: a "Cross-rigid merges (experimental)" checkbox, off by default (`crossRigidMerges` on `Process`). Previously it ran whenever mesh merging was enabled. Cross-rigid-only mode still runs it.
- No-atlas mode (`buildAtlases` on `Process`, default in the dialog): skips atlas discovery, planning and building; structural-only merges, geometry optimisation of the merged rigids, pruning and validation still run. On `ak_teb3`: 178 s (about 930 s with atlasing), 38 merge groups, 42 mesh parts removed, validation passes. Extending geometry optimisation to every source-owned gameplay rigid was tried and dropped: 353 extra rigids re-saved for 0.5 % fewer vertices.
- Report-only LOD audit (`PackTextureAtlasBatchService.LodAudit.cs`): per-rigid and per-unit triangles by LOD, switch distances, flags and rankings.
- Report-only asset checks (`PackTextureAtlasBatchService.AssetChecks.cs`): alpha-shader materials whose base colour is fully opaque, and duplicate textures and materials, each with the same-rigid merges they would unlock.
- Fixed `ParamList<T>.Clone()` in `Shared/GameFiles/RigidModel/MaterialHeaders/ParamList.cs`, which returned an empty list. Every cloned rigid material lost its alpha index, UV scale and decal/dirt indices, so merge compatibility ignored them and merged parts were written without them. The merge check is stricter as a result.
- New tests in `PackTextureAtlasCrossRigidTests.cs`; 329 pass across that class and `PackTextureAtlasValueGateTests`.

### 2026-10-12 to 15: texture memory

- Texture downscale (`PackTextureAtlasBatchService.TextureDownscale.cs`): report plus opt-in "Downscale oversized textures (experimental)", with the lord/hero rule (unclassifiable units count as lord/hero). Off by default.
- Vanilla texture coverage (`PackTextureAtlasBatchService.VanillaTextureCoverage.cs`): report only.
- Per-unit repack estimate (`PackTextureAtlasBatchService.UnitTextureRepack.cs`): report only, Options A, B, C, C2 and a selective view.
- Shared texture repack (`PackTextureAtlasBatchService.SharedTextureRepack.cs`): opt-in, first increment. New "Repack shared textures (experimental)" checkbox in `TextureAtlasProgressWindow.xaml`, off by default and disabled only in cross-rigid-only mode (it works next to atlasing and cross-rigid merges).
- Tests: the `FullyQualifiedName~PackTextureAtlas` filter now runs 504 tests, all passing.

### 2026-10-15 to 16: multi-rigid repack and exact compaction

- Shared texture repack extended to sets that span several mod-owned rigids. Flat textures of any size pass through.
- Signature sets that share a material are merged into one plan (up to 8 sets and 16 rigids, with a fallback to the seed set).
- Page splitting puts whole clusters on up to 4 smaller power-of-two pages.
- Exact texture compaction (`PackTextureAtlasBatchService.TextureCompaction.cs`): new opt-in checkbox. It counts xml material slots and the texture lists embedded in rigids as references.
- Shader channel table (`Wh3ShaderTextureChannels.json`, generator in `Tools/ShaderChannels/`) and the opt-in "Drop texture alpha no shader reads" rule. The repack's rigid checks were moved into a shared helper (`GetRigidNotOnlyThroughOwnedWsModelsReason`).
- Bone weight audit (`PackTextureAtlasBatchService.BoneWeightAudit.cs`): report only, runs in every mode except cross-rigid-only.
- Tests: the `FullyQualifiedName~PackTextureAtlas` filter now runs 589 tests, all passing.

## Recommendations

1. Do not build the shared-atlas planner or a per-configuration writer.
2. Stop extending the cross-rigid search.
3. The five-alternative limit is fixed; consider leaving cross-rigid off by default anyway, since it adds about 2,000 small VMD files and tens of MiB of geometry for roughly 0.2 % FPS.
4. Keep same-rigid merging and atlasing; they reduce parts per soldier without rewriting VMDs.
5. If more performance is wanted, look at per-soldier GPU cost (for example vertex count at distance), which the tool does not address. Reducing 4 bone weights to 2 was measured and rejected (see "Bone weights per vertex").
6. Texture work is memory only; do not expect FPS from it.
7. Keep texture downscaling, shared texture repack and exact compaction opt-in.
8. Do not build per-texture reverse atlasing or per-unit atlases (Options A and B).
9. Exact compaction is the cheapest memory win, with no visual risk. Enable it whenever a pack is processed. Dropping unread alpha is equally safe as long as the shader table matches the game build.
10. Do not copy vanilla rigids to unlock more repack sets; only 3 rigids block sets today.
