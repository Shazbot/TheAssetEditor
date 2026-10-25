"""Verifies how the engine's system shaders for directly loaded RMV2 models read UV0.

A rigid that a VMD or DB engine asset names directly is drawn with the material embedded in the RMV2 file, not
with a WSModel material. Warhammer3.exe picks the shader from the uint16 at the start of each mesh's material
block (RMV2 mesh record + 0x50, i.e. after the 0x50-byte common mesh header: the vertex format, not the
ModelMaterialEnum at + 0x00; RVA 0x1B254C8, Patch 8.1): 0x04 -> shaders/system/weighted_standard_4, 0x0E ->
weighted_standard_8, anything else -> weighted_standard_2, with shaders/system/rigid_standard as the rigid
fallback (RVA 0x1B07568). So weighted and default_type materials get the same shaders. Those compile from
FX/weighted.hlsl and FX/rigidstandard.hlsl (fxc/fx_weighted_hlsl_*, fx_rigidstandard_hlsl_*); the engine swaps
in the blood, burn and fire variants (fx_weightedwithblood, fx_weightedwithburn, fx_rigidstandardblood,
fx_rigidstandardfire) at run time.

The shared texture repack rewrites UV0 of a part (uv' = uv * scale + offset) and crops the four modern embedded
slots. This script proves, per pixel shader variant, that

  * t_base_colour_map, t_normal_map, t_material_map and t_colour_mask_map are sampled at the raw UV0,
  * t_decal_blood_map reads UV0 (accepted under the repack's existing blood assumption),
  * t_fire_map is sampled at (UV0.x * a, UV0.y * b) plus UV0-independent terms (a time scroll), with shader
    constants a, b (1.2, 1.5 in Patch 8.1) (CAVEAT: no material parameter to compensate, so on a repacked part
    the burn pattern gets denser by the UV scale and shifts per chart while the unit burns),
  * t_soft_normal_map / t_curvature_and_translucency (the skin path) read UV0: reported, not failed; they are
    per-model textures only if the embedded material names them, and the repack refuses embedded materials
    with slots other than the four modern ones and the legacy Diffuse/Specular/Gloss entries,
  * every other texture read does not depend on UV0, and UV0 reaches nothing else.

The vertex shaders copy UV0 into several interpolator components (TEXCOORD1 = uv.xyxy, TEXCOORD4.xy = uv). A
pixel shader only sees separate inputs, so two of its input components are treated as the same value when
EVERY vertex shader of the family whose output signature matches the pixel shader's input layout writes both
with a plain mov from the same vertex input component. Anything else stays distinct (conservative).

Input (first argument): a folder of extracted fxc/fx_*_hlsl_*.hlsl_compiled files of the families below (they
are disassembled through powershell.exe + dxbc_disassemble.ps1) or a folder of already disassembled *.asm files
named <file>.<blob>.asm. Extract them from the shaders branch of the game dump, e.g.
    git -C K:/projects/wh3dump ls-tree -r --name-only shaders fxc/ \
        | grep -E 'fxc/fx_(weighted|weightedwithblood|weightedwithburn|rigidstandard|rigidstandardblood|rigidstandardfire)_hlsl_'
    (then `git show shaders:<path> > <folder>/<file name>` for each, with stdin redirected from /dev/null)

Usage:
    python3 verify_system_uv.py <folder> [--asm-cache <dir>] [--families a,b,...] [--verbose]

Output: a per family verdict (VERIFIED, VERIFIED WITH CAVEATS, FAILED) with the counts behind it.
"""
import collections
import os
import re
import subprocess
import sys

import verify_emissive_uv as v

FAMILIES = ['weighted', 'weightedwithblood', 'weightedwithburn',
            'rigidstandard', 'rigidstandardblood', 'rigidstandardfire']
MAP_SLOTS = {'t_base_colour_map', 't_normal_map', 't_material_map', 't_colour_mask_map'}
BLOOD = 't_decal_blood_map'
FIRE = 't_fire_map'
SKIN = {'t_soft_normal_map', 't_curvature_and_translucency'}
FILE_RE = re.compile(r'^fx_(.+)_hlsl_[0-9a-f]+\.hlsl_compiled\.(\d+)\.asm$')
SIG_LINE = re.compile(r'^// (\w+)\s+(\d+)\s+([xyzw]+)\s+(\d+)\s', re.M)
MOV_RE = re.compile(r'^mov (o\d+)\.([xyzw]+), (v\d+)\.([xyzw]+)\s*$', re.M)

# The evaluator reads these module globals.
v.ATLAS_SLOTS = set(MAP_SLOTS)
v.BLOOD = BLOOD


def signature(text, title):
    """{(semantic, index): (register, mask)} of the 'Input signature' or 'Output signature' block."""
    m = re.search(r'// ' + title + r':\n(.*?)\n(?://\n|[a-z])', text, re.S)
    out = {}
    if m:
        for s in SIG_LINE.finditer(m.group(1)):
            out[(s.group(1), int(s.group(2)))] = (int(s.group(4)), s.group(3))
    return out


class Vs:
    """A vertex shader: its output layout and, per output component, the vertex input component it is a plain
    copy of (None when written by anything else or more than once)."""

    def __init__(self, text):
        self.outputs = signature(text, 'Output signature')
        regs = {reg: sem for sem, (reg, _) in self.outputs.items()}
        body = text.split('\nvs_5_0', 1)[-1]
        writes = collections.Counter()
        for line in body.splitlines():
            m = re.match(r'^\w+\S*\s+(o\d+)\.([xyzw]+)', line)
            if m and not line.startswith('dcl_'):
                for ch in m.group(2):
                    writes[(m.group(1), ch)] += 1
        self.source = {}
        for m in MOV_RE.finditer(body):
            sw = m.group(4)
            for i, ch in enumerate(m.group(2)):
                src = sw[i] if i < len(sw) else sw[-1]
                if writes[(m.group(1), ch)] == 1:
                    sem = regs.get(int(m.group(1)[1:]))
                    if sem:
                        self.source[(sem, ch)] = (m.group(3), src)

    def matches(self, ps_inputs):
        for sem, (reg, mask) in ps_inputs.items():
            if sem[0].startswith('SV_'):
                continue
            out = self.outputs.get(sem)
            if out is None or out[0] != reg or not set(mask) <= set(out[1]):
                return False
        return True


def aliases(ps_text, vss):
    """{(vreg, comp index): (vreg, comp index)} for pixel shader input components proven equal."""
    ps_inputs = signature(ps_text, 'Input signature')
    partners = [vs for vs in vss if vs.matches(ps_inputs)]
    if not partners:
        return {}
    comps = [(sem, ch) for sem, (_, mask) in ps_inputs.items() if not sem[0].startswith('SV_') for ch in mask]
    # Two components are equal when every partner copies both from the same input component.
    classes = {c: frozenset(comps) for c in comps}
    for vs in partners:
        by_source = collections.defaultdict(set)
        for c in comps:
            src = vs.source.get(c)
            by_source[src if src else ('unique', c)].add(c)
        for group in by_source.values():
            for c in group:
                classes[c] = classes[c] & frozenset(group)
    reg_of = lambda c: ('v%d' % ps_inputs[c[0]][0], v.COMP[c[1]])
    out = {}
    for c in comps:
        rep = min((reg_of(x) for x in classes[c]), key=lambda r: (int(r[0][1:]), r[1]))
        if rep != reg_of(c):
            out[reg_of(c)] = rep
    return out


class SystemEvaluator(v.Evaluator):
    def __init__(self, sh, alias):
        super().__init__(sh)
        self.alias = alias
        self.skin = collections.Counter()

    def read(self, base, comp):
        if re.match(r'^v\d+$', base):
            base, comp = self.alias.get((base, comp), (base, comp))
        if re.match(r'^r\d+$', base):
            # A register holding 0 is an empty polynomial; the base class would read it as undefined.
            value = self.getreg(base, comp)
            if value is not None:
                return value
        return super().read(base, comp)

    def find_uv0(self):
        cands = set()
        nonraw = False
        for op, args in self.sh.instructions():
            if not op.startswith(v.SAMPLE_OPS) or len(args) < 3 or self.tex_slot(args[2]) not in MAP_SLOTS:
                continue
            m = re.match(r'^(v\d+)\.([xyzw]+)$', args[1])
            if not m or len(m.group(2)) < 2:
                nonraw = True
                continue
            a = self.alias.get((m.group(1), v.COMP[m.group(2)[0]]), (m.group(1), v.COMP[m.group(2)[0]]))
            b = self.alias.get((m.group(1), v.COMP[m.group(2)[1]]), (m.group(1), v.COMP[m.group(2)[1]]))
            if a[0] == b[0] and (a[1], b[1]) == (0, 1):
                cands.add(a[0])
            else:
                nonraw = True
        if nonraw:
            self.issues.append('a map slot is sampled at a coordinate that is not a raw interpolator')
        if len(cands) == 1:
            self.uv0 = next(iter(cands))
        elif len(cands) > 1:
            self.issues.append('map slots sampled at different interpolators')

    def do_sample(self, op, args):
        slot = self.tex_slot(args[2])
        if slot in (FIRE,) or slot in SKIN:
            uvx, uvy = self.src_comp(args[1], 0), self.src_comp(args[1], 1)
            if v.p_taint(uvx) or v.p_taint(uvy):
                if slot == FIRE:
                    self.check_constant_tiling(uvx, uvy)
                    self.found['fire_constant_tiling'] += 1
                else:
                    self.skin[slot] += 1
                    self.found['skin_uv0'] += 1
                dst, dcomps = self.dst_comps(args[0])
                self.write(dst, dcomps, [v.poly_atom(v.atom('tex', slot, 'uv0', c)) for c in dcomps])
                return
        return super().do_sample(op, args)

    def check_constant_tiling(self, uvx, uvy):
        # UV0 * shader constant, plus terms that do not depend on UV0 (the time scroll).
        for k, uv in enumerate((uvx, uvy)):
            u = v.atom('in', self.uv0, k, taint=True)
            tainted = [m for m in uv if any(a[-1] for a in m)]
            if tainted != [(u,)]:
                raise v.Fail('t_fire_map UV is not UV0 * a shader constant + UV0-independent terms (axis %d)' % k)


def disassemble(folder, cache, families):
    here = os.path.dirname(os.path.abspath(__file__))
    out = cache or os.path.join(folder, '_asm')
    os.makedirs(out, exist_ok=True)
    names = [f for f in sorted(os.listdir(folder))
             if f.endswith('.hlsl_compiled') and (lambda m: m and m.group(1) in families)(re.match(r'^fx_(.+)_hlsl_', f))]
    lst = os.path.join(out, '_list.txt')
    with open(lst, 'w') as fh:
        fh.write('\n'.join(names))
    w = (lambda p: subprocess.check_output(['wslpath', '-w', p]).decode().strip()) if os.name != 'nt' else (lambda p: p)
    subprocess.check_call(['powershell.exe', '-NoProfile', '-File', w(os.path.join(here, 'dxbc_disassemble.ps1')),
                           '-InDir', w(folder), '-OutDir', w(out), '-List', w(lst)])
    return out


def read_asm(path):
    # dxbc_disassemble.ps1 writes CRLF text with a trailing NUL.
    return open(path, errors='replace').read().replace('\r', '').replace('\x00', '')


def main():
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    verbose = '--verbose' in sys.argv
    cache = sys.argv[sys.argv.index('--asm-cache') + 1] if '--asm-cache' in sys.argv else None
    families = FAMILIES
    if '--families' in sys.argv:
        families = sys.argv[sys.argv.index('--families') + 1].split(',')
    args = [a for a in args if a not in (cache, ','.join(families))]
    if not args:
        print(__doc__)
        return 2
    folder = args[0]
    if any(f.endswith('.hlsl_compiled') for f in os.listdir(folder)):
        folder = disassemble(folder, cache, set(families))

    texts = collections.defaultdict(list)
    for f in sorted(os.listdir(folder)):
        m = FILE_RE.match(f)
        if m and m.group(1) in families:
            texts[m.group(1)].append((f, read_asm(os.path.join(folder, f))))

    print('Families: %s\n' % ', '.join(families))
    for name in families:
        vss = [Vs(t) for _, t in texts[name] if re.search(r'^vs_5', t, re.M)]
        ok = bad = 0
        reasons = collections.Counter()
        found = collections.Counter()
        skin = collections.Counter()
        aliased = 0
        for f, t in texts[name]:
            sh = v.Shader(t)
            if sh.stage != 'ps':
                continue
            alias = aliases(t, vss)
            aliased += bool(alias)
            ev = SystemEvaluator(sh, alias)
            ev.run()
            if ev.issues:
                bad += 1
                for r in set(ev.issues):
                    reasons[r] += 1
            else:
                ok += 1
            found.update(ev.found)
            skin.update(ev.skin)
        if ok + bad == 0:
            verdict = 'NOT FOUND'
        elif bad:
            verdict = 'FAILED'
        elif found['fire_constant_tiling'] or found['skin_uv0']:
            verdict = 'VERIFIED WITH CAVEATS'
        else:
            verdict = 'VERIFIED'
        print('%-20s %-22s pixel variants %d ok / %d bad, %d vertex shaders, %d variant(s) with proven UV copies' % (
            name, verdict, ok, bad, len(vss), aliased))
        print('    samples: %s' % ', '.join('%s %d' % kv for kv in sorted(found.items())))
        if found['fire_constant_tiling']:
            print('    CAVEAT: t_fire_map at UV0 x shader constants + time scroll (burn pattern density follows the '
                  'repack UV scale)')
        if skin:
            print('    NOTE: skin textures read at UV0: %s (per-model only if the embedded material names them)' %
                  ', '.join('%s %d' % kv for kv in sorted(skin.items())))
        for r, n in reasons.most_common(None if verbose else 5):
            print('      %4d x %s' % (n, r))
    return 0


if __name__ == '__main__':
    sys.exit(main())
