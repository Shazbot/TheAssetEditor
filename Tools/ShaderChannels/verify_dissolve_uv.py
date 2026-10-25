"""Verifies which shader families may have their UV0 remapped when t_xml_dissolve_detail / t_xml_dissolve_map
are involved (the dissolve shaders: character_dissolve*, daemon_prince*, dragon_dissolve*, ...).

The shared texture repack rewrites UV0 of a mesh part (uv' = uv * scale + offset). A dissolve shader samples
    m = t_xml_dissolve_map(UV0).r                                 raw UV0, same interpolator as the base colour
    d = t_xml_dissolve_detail(UV0 * sp_dissolve_uv_scale).r       float2 parameter, per axis, nothing else
and compares smoothstep((m + d) / 2) + 0.1 with sp_dissolve_amount. The repack crops the (shared, common)
dissolve map like an atlas slot (a copy) and divides sp_dissolve_uv_scale by the UV scale, which reproduces
the detail UV up to a constant per-chart phase shift. This script proves, per pixel shader variant, that

  * t_xml_dissolve_map is sampled at the raw UV0,
  * t_xml_dissolve_detail is sampled at exactly (UV0.x * sp_dissolve_uv_scale.x, UV0.y * sp_dissolve_uv_scale.y),
  * everything else follows the rules of verify_emissive_uv.py (it reuses its symbolic evaluator; for the
    families that also bind t_xml_emissive_distortion the emissive chain must verify too).

A family qualifies only if ALL its variants pass AND every shader of its _alpha / _blood / _alpha_blood /
_clean_alpha group that exists in the input passes (the engine can swap a shader for its sibling at run time).
Families whose name starts with vfx_ stay excluded (file names flatten the shader folder, so the shader path
is ambiguous). It is conservative: anything it cannot prove makes the family fail.

Input (first argument): a folder of extracted fxc/*.hlsl_compiled files (every file naming a t_xml_dissolve_*
resource and its group siblings are disassembled through powershell.exe + dxbc_disassemble.ps1) or a folder of
already disassembled *.asm files named <file>.<blob>.asm.

Usage:
    python3 verify_dissolve_uv.py <folder> [--asm-cache <dir>] [--verbose]

Output: a per family report and two C# allowlists: SharedRepackDissolveVerifiedShaders (all qualifying
dissolve families) and the subset that also verifies the emissive chain (these belong in
SharedRepackEmissiveVerifiedShaders as well).
"""
import collections
import os
import sys

import verify_emissive_uv as v

DISSOLVE_SLOTS = (v.DMAP, v.DDET)


def scan(folder):
    fams = collections.defaultdict(lambda: {'ok': 0, 'bad': 0, 'reasons': collections.Counter(),
                                            'found': collections.Counter(), 'dissolve': False, 'dist': False})
    for f in sorted(os.listdir(folder)):
        m = v.FAMILY_RE.match(f)
        if not m:
            continue
        ev = v.analyze_file(os.path.join(folder, f))
        if ev is None:
            continue
        fam = fams[m.group(1)]
        names = set(ev.sh.tex.values())
        fam['dissolve'] |= any(slot in names for slot in DISSOLVE_SLOTS)
        fam['dist'] |= v.DIST in names
        if ev.issues:
            fam['bad'] += 1
            for r in set(ev.issues):
                fam['reasons'][r] += 1
        else:
            fam['ok'] += 1
        fam['found'].update(ev.found)
    return fams


def main():
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    verbose = '--verbose' in sys.argv
    cache = None
    if '--asm-cache' in sys.argv:
        cache = sys.argv[sys.argv.index('--asm-cache') + 1]
        args = [a for a in args if a != cache]
    if not args:
        print(__doc__)
        return 2
    folder = args[0]
    if any(f.endswith('.hlsl_compiled') for f in os.listdir(folder)):
        folder = v.disassemble(folder, cache, [b't_xml_dissolve_map', b't_xml_dissolve_detail'], with_siblings=True)
    fams = scan(folder)

    groups = collections.defaultdict(list)
    for name in fams:
        groups[v.family_group(name)].append(name)
    good, emissive, bad = [], [], []
    for name in sorted(fams):
        fam = fams[name]
        if not fam['dissolve']:
            continue
        found = fam['found']
        verified = fam['bad'] == 0 and fam['ok'] > 0 and (found[v.DMAP] + found['dissolve_detail']) > 0
        for sibling in groups[v.family_group(name)]:
            if sibling != name and fams[sibling]['bad'] != 0:
                fam['reasons']['sibling shader %s does not verify' % sibling] = 1
                verified = False
        if verified and name.startswith('vfx_'):
            fam['reasons']['verified, but the shader path is ambiguous (vfx_ prefix: shaders/vfx/... or shaders/vfx_...)'] = 1
            verified = False
        (good if verified else bad).append(name)
        if verified and fam['dist'] and found['emissive_chain'] > 0:
            emissive.append(name)
    print('Families naming a dissolve texture: %d, qualifying: %d (with a verified emissive chain: %d), excluded: %d\n' % (
        len(good) + len(bad), len(good), len(emissive), len(bad)))
    print('QUALIFYING (all variants and siblings verified):')
    for n in good:
        fam = fams[n]
        print('  %-55s variants=%d  %s' % (n, fam['ok'], dict(fam['found'])))
    print('\nEXCLUDED:')
    for n in bad:
        fam = fams[n]
        why = '; '.join('%s (x%d)' % kv for kv in fam['reasons'].most_common(2)) or 'no dissolve sample found'
        print('  %-55s ok=%d bad=%d  %s' % (n, fam['ok'], fam['bad'], why))
        if verbose:
            for r, c in fam['reasons'].most_common():
                print('      %s (x%d)' % (r, c))
    noemissive = [n for n in good if fams[n]['dist'] and n not in emissive]
    if noemissive:
        print('\nNOTE: bind t_xml_emissive_distortion but no verified emissive chain (parts stay blocked by the emissive reason):')
        for n in noemissive:
            print('  ' + n)
    print('\n// C# SharedRepackDissolveVerifiedShaders (generated by Tools/ShaderChannels/verify_dissolve_uv.py)')
    for n in good:
        print('"shaders/%s.xml.shader",' % n)
    print('\n// C# additions to SharedRepackEmissiveVerifiedShaders (dissolve families with a verified emissive chain)')
    for n in emissive:
        print('"shaders/%s.xml.shader",' % n)
    return 0


if __name__ == '__main__':
    sys.exit(main())
