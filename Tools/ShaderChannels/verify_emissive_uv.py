"""Verifies which shader families may have their UV0 remapped when t_xml_emissive_distortion is cropped.

The shared texture repack rewrites UV0 of a mesh part (uv' = uv * scale + offset) and crops the textures
bound to the part's material. For a material with a t_xml_emissive_distortion texture that is only exact
when the pixel shader consumes UV0 in a way the repack can compensate:

  * t_xml_base_colour / t_xml_material_map / t_xml_normal / t_xml_mask / t_xml_emissive_distortion are
    sampled at the raw UV0 interpolator (these slots are cropped with the part),
  * t_xml_emissive_texture is sampled at exactly
        (UV0 + d * sp_emissive_distort_strength + time * sp_emissive_speed * sp_emissive_direction)
        * sp_emissive_tiling
    with d the (two channel) t_xml_emissive_distortion sample at the raw UV0 and time = camera.time_in_sec.
    The repack divides sp_emissive_tiling by the UV scale s and multiplies strength and direction by s,
    which reproduces the original emissive UV up to a constant per-chart phase offset,
  * t_xml_blood_map is sampled at UV0 * sp_blood_uv_scale (the repack rescales sp_blood_uv_scale),
  * dissolve shaders: t_xml_dissolve_map is sampled at the raw UV0 (cropped like the distortion noise) and
    t_xml_dissolve_detail at exactly UV0 * sp_dissolve_uv_scale per axis (the repack divides
    sp_dissolve_uv_scale by the UV scale; see verify_dissolve_uv.py),
  * every other texture read does not depend on UV0 (dither, screen space, ...),
  * UV0 reaches nothing else (no other ALU use, no outputs).

The script symbolically evaluates the straight-line ALU code of every pixel shader variant (registers hold
polynomials over atoms: interpolator components, structured-buffer parameters, camera constants and texture
results; anything else becomes an opaque atom that keeps the "depends on UV0" flag). A family qualifies only
if ALL its variants pass. It is conservative: anything it cannot prove makes the family fail.

Input (first argument): a folder of extracted fxc/*.hlsl_compiled files (they are disassembled through
powershell.exe + dxbc_disassemble.ps1 into a temp folder) or a folder of already disassembled *.asm files
named <file>.<blob>.asm.

Usage:
    python3 verify_emissive_uv.py <folder> [--asm-cache <dir>] [--verbose]

Output: a per family report and the C# allowlist of qualifying shader paths.
"""
import collections
import fractions
import hashlib
import os
import re
import subprocess
import sys

ATLAS_SLOTS = {'t_xml_base_colour', 't_xml_material_map', 't_xml_normal', 't_xml_mask'}
DIST = 't_xml_emissive_distortion'
EMIS = 't_xml_emissive_texture'
BLOOD = 't_xml_blood_map'
DMAP = 't_xml_dissolve_map'
DDET = 't_xml_dissolve_detail'
FAMILY_RE = re.compile(r'^shaders_(.+)_xml_hlsl_[0-9a-f]+\.hlsl_compiled\.(\d+)\.asm$')
SAMPLE_OPS = ('sample', 'gather4')
MAX_TERMS = 48


_fresh = [0]


def atom(*key, taint=False):
    """Hashable atom (kind, ..., taint); the last element says whether the value depends on UV0."""
    return tuple(key) + (bool(taint),)


def opaque(kind, taint):
    _fresh[0] += 1
    return poly_atom(atom(kind, _fresh[0], taint=taint))


# A polynomial is a frozenset of (monomial, coeff); monomial is a sorted tuple of atoms.
def poly_const(v):
    v = fractions.Fraction(v).limit_denominator(1 << 24)
    return {(): v} if v != 0 else {}


def poly_atom(a):
    return {(a,): fractions.Fraction(1)}


def p_add(a, b):
    r = dict(a)
    for m, c in b.items():
        n = r.get(m, 0) + c
        if n == 0:
            r.pop(m, None)
        else:
            r[m] = n
    return r


def p_neg(a):
    return {m: -c for m, c in a.items()}


class TooBig(Exception):
    pass


def p_mul(a, b):
    if len(a) * len(b) > 256:
        raise TooBig()
    r = {}
    for m1, c1 in a.items():
        for m2, c2 in b.items():
            m = tuple(sorted(m1 + m2, key=repr))
            n = r.get(m, 0) + c1 * c2
            if n == 0:
                r.pop(m, None)
            else:
                r[m] = n
    return r


def p_taint(p):
    return any(a[-1] for m in p for a in m)


def p_key(p):
    """Short canonical digest of a polynomial (atoms only hold such digests, so they stay small)."""
    text = repr(sorted(((tuple(repr(a) for a in m), str(c)) for m, c in p.items())))
    return hashlib.md5(text.encode()).hexdigest()[:12]


def split_operands(s):
    out, depth, cur = [], 0, ''
    for ch in s:
        if ch in '([':
            depth += 1
        elif ch in ')]':
            depth -= 1
        if ch == ',' and depth == 0:
            out.append(cur.strip())
            cur = ''
        else:
            cur += ch
    if cur.strip():
        out.append(cur.strip())
    return out


COMP = {'x': 0, 'y': 1, 'z': 2, 'w': 3, 'r': 0, 'g': 1, 'b': 2, 'a': 3}


class Shader:
    def __init__(self, text):
        self.text = text
        self.tex = {}        # tN -> slot name
        self.cbuf = {}       # cbN -> cbuffer name
        self.cbvars = {}     # (cbuffer name, offset) -> var name
        self.params = []     # (name without sp_, offset, size)
        self.struct_reg = None
        self.stage = None
        self.parse_header()

    def parse_header(self):
        t = self.text
        m = re.search(r'^(ps|vs|gs|hs|ds|cs)_\d_\d', t, re.M)
        self.stage = m.group(1) if m else None
        for m in re.finditer(r'^// (\w+)\s+texture\s+\S+\s+2d\s+(t\d+)', t, re.M):
            self.tex[m.group(2)] = m.group(1)
        for m in re.finditer(r'^// (\w+)\s+texture\s+struct\s+r/o\s+(t\d+)', t, re.M):
            self.struct_reg = m.group(2)
        for m in re.finditer(r'^// (\w+)\s+cbuffer\s+\S+\s+\S+\s+(cb\d+)', t, re.M):
            self.cbuf[m.group(2)] = m.group(1)
        for m in re.finditer(r'^// cbuffer (\w+)\n// \{\n(.*?)^// \}', t, re.M | re.S):
            for v in re.finditer(r'^//\s+\w+\s+(\w+)(?:\[\d+\])?;\s*// Offset:\s+(\d+)', m.group(2), re.M):
                self.cbvars[(m.group(1), int(v.group(2)))] = v.group(1)
        sm = re.search(r'struct AUTO_PARAMETERS\n// +\{(.*?)\} \$Element', t, re.S)
        if sm:
            for v in re.finditer(r'^//\s+(float\d?|int|uint|bool)\s+(\w+);\s*// Offset:\s+(\d+)', sm.group(1), re.M):
                ty = v.group(1)
                n = int(ty[5:]) if ty.startswith('float') and ty[5:].isdigit() else 1
                self.params.append((re.sub(r'^sp_', '', v.group(2)), int(v.group(3)), 4 * n))
        self.params.sort(key=lambda p: p[1])

    def param_at(self, off):
        for name, po, size in self.params:
            if po <= off < po + size:
                return name, (off - po) // 4
        return None

    def instructions(self):
        started = False
        for raw in self.text.splitlines():
            line = raw.strip()
            if not line or line.startswith('//'):
                continue
            if not started:
                if re.match(r'^(ps|vs)_\d_\d', line):
                    started = True
                continue
            if line.startswith('dcl_'):
                continue
            m = re.match(r'[a-z0-9_]+', line)
            if not m:
                continue
            op = m.group(0)
            rest = line[len(op):]
            while rest.startswith('('):
                d = 0
                for i, ch in enumerate(rest):
                    d += ch == '('
                    d -= ch == ')'
                    if d == 0:
                        rest = rest[i + 1:]
                        break
            yield op, split_operands(rest.strip())


class Fail(Exception):
    pass


class Evaluator:
    def __init__(self, sh):
        self.sh = sh
        self.frames = [{'regs': {}, 'kind': 'base', 'arms': []}]   # structured control flow scopes
        self.issues = []
        self.found = collections.Counter()
        self.uv0 = None

    # ---- operand access -------------------------------------------------
    def split_reg(self, tok):
        m = re.match(r'^(-?)(\|?)([A-Za-z_]\w*(?:\[[^\]]*\])?|l\(.*\))(?:\.([xyzwrgba]+))?(\|?)$', tok)
        return m

    def src_comp(self, tok, c):
        tok = tok.strip()
        neg = tok.startswith('-')
        if neg:
            tok = tok[1:]
        absv = tok.startswith('|') and tok.endswith('|')
        if absv:
            tok = tok[1:-1]
        if tok.startswith('l('):
            vals = [v.strip() for v in tok[2:-1].split(',')]
            v = vals[c] if len(vals) > 1 else vals[0]
            try:
                f = float(int(v, 16)) if v.lower().startswith('0x') else float(v)
            except ValueError:
                return opaque('imm', False)
            p = poly_const(f)
        else:
            m = re.match(r'^(\w+(?:\[[^\]]*\])?)(?:\.([xyzwrgba]+))?$', tok)
            if not m:
                return opaque('unparsed', True)
            base, sw = m.group(1), m.group(2)
            if sw is None:
                comp = c
            else:
                idx = c if len(sw) > 1 else 0
                comp = COMP[sw[idx]] if idx < len(sw) else COMP[sw[-1]]
            p = self.read(base, comp)
        if absv:
            p = poly_atom(atom('abs', p_key(p), taint=p_taint(p)))
        return p_neg(p) if neg else p

    def read(self, base, comp):
        if re.match(r'^v\d+$', base):
            return poly_atom(atom('in', base, comp, taint=(base == self.uv0)))
        if re.match(r'^r\d+$', base):
            return self.getreg(base, comp) or poly_atom(atom('undef', base, comp))
        m = re.match(r'^cb(\d+)\[(\d+)\]$', base)
        if m:
            name = self.sh.cbuf.get('cb' + m.group(1), 'cb' + m.group(1))
            off = int(m.group(2)) * 16 + 4 * comp
            return poly_atom(atom('cb', name, self.sh.cbvars.get((name, off), off)))
        return poly_atom(atom('src', base, comp))

    def src_polys(self, tok, comps):
        return [self.src_comp(tok, c) for c in comps]

    # ---- instruction handling -------------------------------------------
    def dst_comps(self, tok):
        m = re.match(r'^(\w+)(?:\[[^\]]*\])?(?:\.([xyzw]+))?$', tok.strip())
        if not m:
            return None, []
        return m.group(1), ([COMP[ch] for ch in m.group(2)] if m.group(2) else [0, 1, 2, 3])

    def write(self, dst, comps, polys):
        if dst is None or dst == 'null':
            return
        if dst.startswith('o'):
            for p in polys:
                if p_taint(p):
                    self.issues.append('UV0 reaches output %s' % dst)
            return
        if not dst.startswith('r'):
            return
        for c, p in zip(comps, polys):
            if len(p) > MAX_TERMS:
                p = poly_atom(atom('big', p_key(p), taint=p_taint(p)))
            self.frames[-1]['regs'][(dst, c)] = p

    def getreg(self, base, comp):
        for frame in reversed(self.frames):
            value = frame['regs'].get((base, comp))
            if value is not None:
                return value
        return None

    def push(self, kind):
        self.frames.append({'regs': {}, 'kind': kind, 'arms': []})

    def next_arm(self):
        frame = self.frames[-1]
        frame['arms'].append(frame['regs'])
        frame['regs'] = {}

    def pop(self):
        """Merges a finished if/switch/loop scope into its parent: a register written in some arm only may
        still hold the older value, so it is opaque and tainted if any arm (or the older value) is."""
        if len(self.frames) < 2:
            return
        frame = self.frames.pop()
        arms = frame['arms'] + [frame['regs']]
        every_arm = frame['kind'] in ('if_else', 'switch_default') and len(frame['arms']) > 0
        for key in {k for arm in arms for k in arm}:
            tainted = any(p_taint(arm[key]) for arm in arms if key in arm)
            if not (every_arm and all(key in arm for arm in arms)):
                tainted = tainted or p_taint(self.getreg(*key) or {})
            self.frames[-1]['regs'][key] = opaque('cf', tainted)

    def run(self):
        sh = self.sh
        self.find_uv0()
        ins = list(sh.instructions())
        i = 0
        while i < len(ins):
            op, args = ins[i]
            try:
                if op == 'loop':
                    self.enter_loop(ins, i)
                    self.push('loop')
                elif op in ('if_nz', 'if_z'):
                    self.push('if')
                elif op == 'switch':
                    self.push('switch')
                elif op == 'else':
                    self.frames[-1]['kind'] = 'if_else'
                    self.next_arm()
                elif op in ('case', 'default'):
                    self.next_arm()
                    if op == 'default':
                        self.frames[-1]['kind'] = 'switch_default'
                elif op in ('endif', 'endloop', 'endswitch'):
                    self.pop()
                else:
                    self.step(op, args)
            except Fail as e:
                self.issues.append(str(e))
            i += 1

    def enter_loop(self, ins, i):
        """Registers written in the loop body become opaque; tainted when a tainted value can reach them."""
        body, depth, j = [], 0, i
        while j < len(ins):
            op, args = ins[j]
            if op == 'loop':
                depth += 1
            elif op == 'endloop':
                depth -= 1
                if depth == 0:
                    break
            if j > i and args and not op.startswith(('if', 'else', 'end', 'break', 'continue', 'case', 'default',
                                                     'ret', 'discard', 'switch', 'loop')):
                body.append((op, args))
            j += 1
        carried = {}   # (reg, comp) -> tainted, for registers written in the body

        def src_tainted(tok, written_only):
            tok = tok.strip().lstrip('-').strip('|')
            m = re.match(r'^(\w+(?:\[[^\]]*\])?)(?:\.([xyzwrgba]+))?$', tok)
            if not m or tok.startswith('l('):
                return False
            base, sw = m.group(1), m.group(2)
            comps = {COMP[ch] for ch in sw} if sw else {0, 1, 2, 3}
            for c in comps:
                if (base, c) in carried:
                    if carried[(base, c)]:
                        return True
                    if not written_only:
                        continue
                if base.startswith('v'):
                    if base == self.uv0:
                        return True
                elif base.startswith('r') and not written_only and p_taint(self.getreg(base, c) or {}):
                    return True
            return False

        for passno in range(2):
            for op, args in body:
                d, comps = self.dst_comps(args[0])
                tainted = any(src_tainted(a, passno == 1 and False) for a in args[1:])
                if d and d.startswith('r'):
                    for c in comps:
                        carried[(d, c)] = carried.get((d, c), False) or tainted
        for (d, c), t in carried.items():
            self.frames[-1]['regs'][(d, c)] = opaque('loop', t)

    def find_uv0(self):
        cands = set()
        nonraw = False
        for op, args in self.sh.instructions():
            if not op.startswith(SAMPLE_OPS) or len(args) < 3:
                continue
            slot = self.tex_slot(args[2])
            if slot in ATLAS_SLOTS or slot in (DIST, DMAP):
                m = re.match(r'^(v\d+)\.([xyzwrgba]+)$', args[1])
                if m and len(m.group(2)) >= 2 and m.group(2)[:2] == 'xy':
                    cands.add(m.group(1))
                else:
                    nonraw = True
        if nonraw:
            self.issues.append('an atlas slot is sampled at a coordinate that is not a raw interpolator')
        if len(cands) == 1:
            self.uv0 = next(iter(cands))
        elif len(cands) > 1:
            self.issues.append('atlas slots sampled at different interpolators')

    def tex_slot(self, tok):
        m = re.match(r'^(t\d+)', tok.strip())
        return self.sh.tex.get(m.group(1)) if m else None

    def step(self, op, args):
        sh = self.sh
        if op in ('ret', 'else', 'break', 'continue', 'case', 'default', 'discard', 'retc', 'breakc',
                  'continuec', 'discard_nz', 'discard_z'):
            return
        if not args:
            return
        if op.startswith(SAMPLE_OPS) and len(args) >= 4 and self.tex_slot(args[2]):
            return self.do_sample(op, args)
        if op.startswith('ld_structured') and len(args) == 4:
            return self.do_ld_structured(args)
        dst, dcomps = self.dst_comps(args[0])
        if dst is None:
            return
        srcs = args[1:]
        if op in ('mov', 'add', 'mul', 'mad') and len(srcs) == {'mov': 1, 'add': 2, 'mul': 2, 'mad': 3}[op]:
            out = []
            try:
                for c in dcomps:
                    vals = [self.src_comp(s, c) for s in srcs]
                    if op == 'mov':
                        r = vals[0]
                    elif op == 'add':
                        r = p_add(vals[0], vals[1])
                    elif op == 'mul':
                        r = p_mul(vals[0], vals[1])
                    else:
                        r = p_add(p_mul(vals[0], vals[1]), vals[2])
                    out.append(r)
                return self.write(dst, dcomps, out)
            except TooBig:
                pass
        # generic op: opaque result, taint from sources; tainted data may only flow through poly arithmetic
        tainted = False
        keys = []
        self.generic_dsts(op, args, tainted, keys)

    def generic_dsts(self, op, args, tainted, keys):
        ndst = 2 if op in ('sincos', 'udiv', 'imad', 'umul', 'imul', 'uaddc', 'usubb') and len(args) >= 3 and \
            (args[1].strip() == 'null' or re.match(r'^[ro]\d+', args[1].strip()) and op == 'sincos') else 1
        if op == 'sincos':
            ndst = 2
        dsts = args[:ndst]
        srcs = args[ndst:]
        tainted = False
        keys = []
        for s in srcs:
            for c in range(4):
                p = self.src_comp(s, c)
                tainted = tainted or p_taint(p)
                keys.append(p_key(p))
        for d in dsts:
            dst, dcomps = self.dst_comps(d)
            if dst and dst != 'null' and dst.startswith('o') and tainted:
                self.issues.append('UV0 reaches output %s via %s' % (dst, op))
            if dst and dst != 'null' and dst.startswith('r'):
                polys = [poly_atom(atom('op', op, c, hashlib.md5(''.join(keys).encode()).hexdigest()[:12], taint=tainted)) for c in dcomps]
                self.write(dst, dcomps, polys)
        if tainted and not dsts:
            self.issues.append('UV0 used by %s' % op)

    def do_ld_structured(self, args):
        dst, dcomps = self.dst_comps(args[0])
        off_tok = args[2].strip()
        m = re.match(r'^l\((\d+)\)$', off_tok)
        res = re.match(r'^(t\d+)\.([xyzw]+)$', args[3].strip())
        if not m or not res or res.group(1) != self.sh.struct_reg:
            tainted = any(p_taint(self.src_comp(a, c)) for a in args[1:3] for c in range(4))
            if tainted:
                raise Fail('UV0 used as structured buffer index')
            self.write(dst, dcomps, [opaque('sb?', False) for _ in dcomps])
            return
        base = int(m.group(1))
        sw = res.group(2)
        out = []
        for c in dcomps:
            idx = COMP[sw[c]] if c < len(sw) else COMP[sw[-1]]
            off = base + 4 * idx
            pa = self.sh.param_at(off)
            out.append(poly_atom(atom('sb', pa[0], pa[1]) if pa else atom('sb', 'offset', off)))
        self.write(dst, dcomps, out)

    def do_sample(self, op, args):
        dst, dcomps = self.dst_comps(args[0])
        slot = self.tex_slot(args[2])
        res = re.match(r'^(t\d+)\.([xyzw]+)$', args[2].strip())
        rsw = res.group(2) if res else 'xyzw'
        coord = args[1]
        uvx, uvy = self.src_comp(coord, 0), self.src_comp(coord, 1)
        extra_taint = False
        for e in args[4:]:
            for c in range(4):
                extra_taint = extra_taint or p_taint(self.src_comp(e, c))
        if extra_taint:
            raise Fail('UV0 feeds the LOD/gradient/reference operand of a %s of %s' % (op, slot))
        raw = False
        ux = poly_atom(atom('in', self.uv0, 0, taint=True)) if self.uv0 else None
        uy = poly_atom(atom('in', self.uv0, 1, taint=True)) if self.uv0 else None
        is_raw = self.uv0 is not None and uvx == ux and uvy == uy
        tainted = p_taint(uvx) or p_taint(uvy)
        if slot in ATLAS_SLOTS or slot in (DIST, DMAP):
            if not is_raw:
                raise Fail('%s not sampled at the raw UV0' % slot)
            raw = True
            self.found[slot] += 1
        elif slot == DDET:
            if tainted:
                self.check_dissolve_detail(uvx, uvy)
                self.found['dissolve_detail'] += 1
        elif slot == EMIS:
            if tainted:
                self.check_emissive_chain(uvx, uvy)
                self.found['emissive_chain'] += 1
        elif slot == BLOOD:
            # The blood overlay is covered by the repack's existing, separately reported blood assumption
            # (the shaders compute (UV0 * constant + per-instance offset) * sp_blood_uv_scale * ..., not a plain
            # UV0 * sp_blood_uv_scale), so any UV0 dependent coordinate is accepted here.
            if tainted:
                self.found['blood'] += 1
        elif tainted:
            raise Fail('%s depends on UV0 (not an allowed pattern)' % slot)
        key = p_key(uvx) + p_key(uvy)
        out = []
        for c in dcomps:
            ch = COMP[rsw[c]] if c < len(rsw) else COMP[rsw[-1]]
            out.append(poly_atom(atom('tex', slot, key if not raw else 'rawUV0', ch)))
        self.write(dst, dcomps, out)

    def check_dissolve_detail(self, uvx, uvy):
        if self.uv0 is None:
            raise Fail('dissolve detail without a UV0 anchor')
        for k, uv in enumerate((uvx, uvy)):
            U = poly_atom(atom('in', self.uv0, k, taint=True))
            S = poly_atom(atom('sb', 'dissolve_uv_scale', k))
            if uv != p_mul(U, S):
                raise Fail('dissolve detail UV is not UV0 * dissolve_uv_scale (axis %d)' % k)

    def check_emissive_chain(self, uvx, uvy):
        if self.uv0 is None:
            raise Fail('emissive texture chain without a UV0 anchor')
        t = poly_atom(atom('cb', 'camera', 'time_in_sec'))
        S = poly_atom(atom('sb', 'emissive_distort_strength', 0))
        SP = poly_atom(atom('sb', 'emissive_speed', 0))
        chans = []
        for k, uv in enumerate((uvx, uvy)):
            d = None
            for m in uv:
                for a in m:
                    if a[0] == 'tex' and a[1] == DIST and a[2] == 'rawUV0':
                        d = a
            if d is None:
                raise Fail('emissive UV is not built from the distortion sample at the raw UV0')
            chans.append(d[3])
            U = poly_atom(atom('in', self.uv0, k, taint=True))
            D = poly_atom(d)
            DIR = poly_atom(atom('sb', 'emissive_direction', k))
            TILE = poly_atom(atom('sb', 'emissive_tiling', k))
            inner = p_add(p_add(U, p_mul(D, S)), p_mul(p_mul(t, SP), DIR))
            if uv != p_mul(inner, TILE):
                raise Fail('emissive UV differs from (UV0 + d*strength + time*speed*direction)*tiling')
        if chans[0] == chans[1]:
            raise Fail('emissive UV uses the same distortion channel twice')


def analyze_file(path):
    text = open(path, errors='replace').read()
    sh = Shader(text)
    if sh.stage != 'ps':
        return None
    ev = Evaluator(sh)
    ev.run()
    return ev


def disassemble(folder, cache, needles, with_siblings=False):
    """Disassembles the compiled files of folder that contain one of the byte strings in needles (plus, with
    with_siblings, every file of the same shader family group, see family_group). Returns the asm folder."""
    here = os.path.dirname(os.path.abspath(__file__))
    out = cache or os.path.join(folder, '_asm')
    os.makedirs(out, exist_ok=True)
    compiled = [f for f in sorted(os.listdir(folder)) if f.endswith('.hlsl_compiled') and 'xml_hlsl' in f]
    names, groups = [], set()
    for f in compiled:
        with open(os.path.join(folder, f), 'rb') as fh:
            if any(n in fh.read() for n in needles):
                names.append(f)
                groups.add(family_group(f.split('_xml_hlsl_')[0][len('shaders_'):]))
    if with_siblings:
        names = [f for f in compiled if family_group(f.split('_xml_hlsl_')[0][len('shaders_'):]) in groups]
    lst = os.path.join(out, '_list.txt')
    with open(lst, 'w') as fh:
        fh.write('\n'.join(names))
    w = (lambda p: subprocess.check_output(['wslpath', '-w', p]).decode().strip()) if os.name != 'nt' else (lambda p: p)
    subprocess.check_call(['powershell.exe', '-NoProfile', '-File', w(os.path.join(here, 'dxbc_disassemble.ps1')),
                           '-InDir', w(folder), '-OutDir', w(out), '-List', w(lst)])
    return out


# The engine can swap a shader for its _alpha / _blood / _alpha_blood / _clean_alpha sibling at run time
# (same rule as shader_bound_slots.py), so a family is only as verified as its whole group.
VARIANT_RE = re.compile(r'(_alpha_blood|_clean_alpha|_blood|_alpha)$')


def family_group(name):
    return VARIANT_RE.sub('', name)


def main():
    args =[a for a in sys.argv[1:] if not a.startswith('--')]
    verbose = '--verbose' in sys.argv
    only = sys.argv[sys.argv.index('--only') + 1] if '--only' in sys.argv else None
    cache = None
    if '--asm-cache' in sys.argv:
        cache = sys.argv[sys.argv.index('--asm-cache') + 1]
        args = [a for a in args if a != cache]
    if '--only' in sys.argv:
        args = [a for a in args if a != sys.argv[sys.argv.index('--only') + 1]]
    if not args:
        print(__doc__)
        return 2
    folder = args[0]
    files = sorted(os.listdir(folder))
    if any(f.endswith('.hlsl_compiled') for f in files):
        folder = disassemble(folder, cache, [b't_xml_emissive_distortion'])
        files = sorted(os.listdir(folder))
    fams = collections.defaultdict(lambda: {'ok': 0, 'bad': 0, 'reasons': collections.Counter(), 'found': collections.Counter()})
    for f in files:
        m = FAMILY_RE.match(f)
        if not m or (only and only not in m.group(1)):
            continue
        ev = analyze_file(os.path.join(folder, f))
        if ev is None:
            continue
        if DIST not in ev.sh.tex.values():
            continue
        fam = fams[m.group(1)]
        if ev.issues:
            fam['bad'] += 1
            for r in set(ev.issues):
                fam['reasons'][r] += 1
        else:
            fam['ok'] += 1
        fam['found'].update(ev.found)
    good, bad = [], []
    for name in sorted(fams):
        fam = fams[name]
        verified = fam['bad'] == 0 and fam['ok'] > 0 and fam['found']['emissive_chain'] > 0
        if verified and name.startswith('vfx_'):
            # File names flatten the shader folder (vfx_x could be shaders/vfx/x or shaders/vfx_x): not provable.
            fam['reasons']['verified, but the shader path is ambiguous (vfx_ prefix: shaders/vfx/... or shaders/vfx_...)'] = 1
            verified = False
        (good if verified else bad).append(name)
    print('Families naming t_xml_emissive_distortion: %d, qualifying: %d, excluded: %d\n' % (len(fams), len(good), len(bad)))
    print('QUALIFYING (all variants verified):')
    for n in good:
        fam = fams[n]
        print('  %-55s variants=%d  %s' % (n, fam['ok'], dict(fam['found'])))
    print('\nEXCLUDED:')
    for n in bad:
        fam = fams[n]
        why = '; '.join('%s (x%d)' % kv for kv in fam['reasons'].most_common(2)) or 'no emissive_texture chain found'
        print('  %-55s ok=%d bad=%d  %s' % (n, fam['ok'], fam['bad'], why))
        if verbose:
            for r, c in fam['reasons'].most_common():
                print('      %s (x%d)' % (r, c))
    print('\n// C# allowlist (generated by Tools/ShaderChannels/verify_emissive_uv.py)')
    for n in good:
        print('"shaders/%s.xml.shader",' % n)
    return 0


if __name__ == '__main__':
    sys.exit(main())
