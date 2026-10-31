# Meshoptimizer triangle ordering findings (2026-10-31)

## Adopted change

Optimize Pack now orders each rewritten rigid model part with
`meshopt_optimizeVertexCache`, followed by `meshopt_optimizeOverdraw`, then the
existing first-use vertex remap. Exact full-vertex deduplication is preserved.
The overdraw threshold is **1.05f**: the overdraw pass may sacrifice up to 5% of
the preceding native cache pass's vertex-cache efficiency to reduce overdraw.
This threshold is relative to that cache pass, not to the previous managed
algorithm or to frame rate.

The existing modified-rigid scope and geometry-option gate are unchanged. The
new public wrapper validates triangle buffers, vertex bounds and finite float3
positions before entering the native code. No triangles, including degenerate
triangles, are removed by the ordering pass.

On `feature/wh3-asset-host`, the implementation lives in the headless
`GameWorld.MeshOptimizer` library and uses its existing native-library resolver.
`PackOptimizerVersions.OutputVersion` advances from **37 to 38** because the
serialized vertex/index order changes and cached optimizer outputs must expire.

## Measured results

The experiment used the pre-extraction editor pipeline at baseline commit
`13901098b` on `feature/texture-atlas-consolidation`. Each source pack received
one baseline and one candidate Release run using `Tools/PackOptimizerBench`.
The baseline used the managed triangle-ordering heuristic; the candidate used
the adopted native cache/overdraw sequence. The subsequent host-branch port
adapts namespaces, System.Numerics and LibraryImport to the headless layout.
**These pack measurements were not repeated on the newer host pipeline.**

| Pack | Rewritten/generated rigids | Mesh parts, all LODs | Geometry stage, baseline → native | Estimated vertex transformations | Estimated shaded pixels |
|---|---:|---:|---:|---:|---:|
| TEB (`!ak_teb3.pack`) | 164 | 2,428 | 4.097 → 1.763 s | −0.48% | −8.64% |
| Albion (`ovn_albion.pack`) | 55 | 902 | 4.341 → 1.682 s | −1.38% | −13.17% |

| Pack | Vertices compared | Triangles compared | Cache ACMR, baseline → native | Software overdraw, baseline → native | Output bytes, both variants |
|---|---:|---:|---:|---:|---:|
| TEB | 2,280,719 | 2,742,092 | 0.987893 → 0.983165 | 1.264383 → 1.155151 | 558,497,720 |
| Albion | 1,766,660 | 2,542,118 | 0.883723 → 0.871572 | 1.435771 → 1.246669 | 648,455,546 |

Overdraw improved at every LOD for both packs. TEB's LOD3 and LOD4 estimated
vertex transformations increased by 0.15% and 0.29%, respectively; the total
still improved. Albion's cache estimate improved at every LOD.

The whole Process timings were TEB 197.7 → 114.7 seconds and Albion
99.9 → 70.9 seconds. Those single samples include cache/system variation and
build activity during baseline work, so the whole-run difference cannot be
attributed to triangle ordering. The isolated geometry-phase times are above.

## Output integrity and quality

The application's pack loader and rigid-model parser compared every matching
rewritten/generated rigid. Both comparisons had **zero integrity errors**:

- Rigid, LOD and mesh headers, part counts, serialized materials, full vertex
  attributes, vertex/index counts, and oriented triangle multisets matched.
- Duplicate and degenerate triangle multiplicity and triangle winding matched.
- Software-rasterizer pixel coverage matched exactly.
- All non-rigid payloads, complete output file sets and pack sizes matched.

These checks establish preservation of model detail and attribute precision.
The lower shaded-pixel counts indicate less estimated work on surfaces hidden
behind other surfaces. Analyzer statistics use static positions and a software
rasterizer, and do not establish an FPS improvement or an in-game rendering
comparison. Reordering blended transparent triangles can affect appearance;
that remains untested. All-LOD aggregates are unweighted by army probability.

## Pack validation

WHMM's Windows CLI performed full-payload validation with the explicit base-game
manifest and compared each candidate with its baseline. Both comparisons had
**zero added or resolved diagnostics** and `regressed: false`.

Validation remained incomplete and returned exit code 5 because pre-existing
baseline diagnostics persisted unchanged:

| Diagnostic | TEB, before = after | Albion, before = after |
|---|---:|---:|
| `INVALID_RIGID_LOADER_REFERENCE` | 2 | 2 |
| `INVALID_XML` | 20 | 15 |
| `MISSING_RMV2_TEXTURE` | 9,462 | 7,224 |
| `RIGID_LOADER_SCAN_LIMIT` | 1 | 1 |
| `UNSUPPORTED_RMV2_PART` | 2 | 2 |

The bounded loader scan and unsupported parts prevent an exhaustive dependency
validation claim. The separate application-parser audit covered all 164 TEB and
55 Albion rewritten/generated rigids listed above.

## Reproduction details

Inputs were read-only under `K:\wh3mods\albion`; output packs were written to
separate experiment directories. Source sizes and modification times remained
unchanged.

| Input | Bytes | SHA-256 |
|---|---:|---|
| `!ak_teb3.pack` | 822,613,511 | `5c4391ff0796b53daa0f9bcfa5031d42587eee59f33a62adb96bed4aa2dc3c0e` |
| `ovn_albion.pack` | 613,360,551 | `01e2477fa4bde038d55cf82fd30346b0f1440c7a495178696f010450a82d6211` |

The shipped meshoptimizer version is **1.3**, upstream commit
`9e1f07b159d3cb777f1c67ed31fc11fd117986f4`. The experiment and host branch use
byte-identical native DLLs (SHA-256
`0fa84d8095aefb3f408a4ded385c6d56ad1b7751d7b736930c00e776c3c728f0`).

The existing bench runner used native Windows .NET and the WH3 game-pack loader.
Atlases were off; compatible-part merging, cross-rigid merging, geometry
optimization, texture downscaling, shared texture repacking, texture compaction
and unread-alpha removal were on. Other settings were the unchanged runner's
defaults. Cache statistics used `meshopt_analyzeVertexCache` with cache size 32,
warp size 32 and primitive-group size 0. Overdraw used `meshopt_analyzeOverdraw`
with packed float3 positions.

Full original reports, output packs, fingerprints, native analyzer results,
validation reports, source identities and logs remain as local artifacts under
`K:\projects\assedfork\.codex-meshopt\`. They are intentionally not committed.
The self-contained tables and methodology above record the findings in Git.

## Regression checks

The original implementation passed eight focused geometry tests. After adapting
the code to the host branch and pinning the changed golden hash, the host port
passed **nine focused Release checks** (eight geometry tests and the versioned
output golden test), with zero failures or skips. The focused filter was:

```text
FullyQualifiedName~RigidGeometryOptimizerTests|FullyQualifiedName~OutputVersion_GoldenHashIsPinnedForTheCurrentVersion
```

Full pack benchmarks were not rerun for the port. The golden test was first run
to obtain the new version's deterministic hash, then rerun after adding the
version-38 entry; historical entries were preserved.

Output version 38 pins golden hash `16AFD48B29182B062A5395A8C9533AB89D5C5CD76D4E463392A3B16202ADEC9B`; historical version entries 1–37 are unchanged.
