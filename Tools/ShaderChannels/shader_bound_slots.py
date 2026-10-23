"""Builds the table "material shader -> t_xml_* texture slots the compiled shader binds".

The pack optimizer ignores a material texture slot that the material's shader never reads (the engine binds a
material slot to the compiled resource of the same name, so a slot without a resource is dead data).

Input:  a folder of extracted fxc/shaders_*_xml_hlsl_<hash>.hlsl_compiled files (FASTBIN0 container wrapping one or
        more DXBC blobs; the header names the HLSL source, e.g. shaders/weighted4_default.xml.hlsl).
        Optionally a folder with the shader definitions (shaders/*.xml.shader): a family is only emitted when
        shaders/<name>.xml.shader exists there (and when it is given, its techniques reference the same hlsl).
Output: for every family the UNION of t_xml_* resource names (any resource type, any stage VS/PS/GS/HS/DS/CS, any
        variant) from the DXBC RDEF chunk, written as a C# partial-class file (generated section).

Usage:
    python3 shader_bound_slots.py <fxc folder> <shader defs folder or -> <output .cs>
"""
import collections
import os
import re
import struct
import sys

SOURCE_RE = re.compile(rb'shaders/([A-Za-z0-9_./\-]+)\.xml\.hlsl')
VARIANT_RE = re.compile(r'(_alpha_blood|_clean_alpha|_blood|_alpha)$')


def dxbc_blobs(data):
    pos = 0
    while True:
        pos = data.find(b'DXBC', pos)
        if pos < 0:
            return
        if pos + 32 <= len(data):
            total = struct.unpack_from('<I', data, pos + 24)[0]
            if 32 <= total <= len(data) - pos:
                yield data[pos:pos + total]
                pos += total
                continue
        pos += 4


def rdef_names(blob):
    count = struct.unpack_from('<I', blob, 28)[0]
    for i in range(count):
        off = struct.unpack_from('<I', blob, 32 + 4 * i)[0]
        if blob[off:off + 4] != b'RDEF':
            continue
        chunk = off + 8
        res_count, res_off = struct.unpack_from('<II', blob, chunk + 8)
        for r in range(res_count):
            name_off, rtype = struct.unpack_from('<II', blob, chunk + res_off + 32 * r)
            end = blob.index(b'\0', chunk + name_off)
            yield blob[chunk + name_off:end].decode('ascii', 'replace'), rtype


def main():
    folder, defs, out = sys.argv[1:4]
    fams = collections.defaultdict(set)
    sources = {}
    nfiles = nblobs = 0
    for f in sorted(os.listdir(folder)):
        if not (f.startswith('shaders_') and f.endswith('.hlsl_compiled') and '_xml_hlsl_' in f):
            continue
        data = open(os.path.join(folder, f), 'rb').read()
        head = data[:data.find(b'DXBC')] if b'DXBC' in data else data[:512]
        m = SOURCE_RE.search(head[:512])
        if not m:
            sources[f] = None
            continue
        name = m.group(1).decode()
        # the file name must agree with the header (flattened path)
        if f != 'shaders_%s_xml_hlsl_%s' % (name.replace('/', '_'), f.rsplit('_xml_hlsl_', 1)[1]):
            sources[f] = None
            continue
        nfiles += 1
        ok = True
        names = set()
        blobs = list(dxbc_blobs(data))
        if not blobs:
            ok = False
        for b in blobs:
            nblobs += 1
            try:
                for n, t in rdef_names(b):
                    if n.lower().startswith('t_xml_'):
                        names.add(n.lower())
            except (struct.error, ValueError):
                ok = False
        if not ok:
            fams[name] = None  # poisoned: omit the family
        elif fams.get(name, 0) is not None:
            fams[name] |= names
    result = {}
    omitted = []
    for name, slots in sorted(fams.items()):
        if slots is None:
            omitted.append((name, 'a variant failed to parse'))
        elif defs != '-' and not os.path.exists(os.path.join(defs, 'shaders', name + '.xml.shader')):
            omitted.append((name, 'no shaders/%s.xml.shader' % name))
        else:
            result[name] = sorted(slots)
    # The engine can swap a material's shader for its _alpha / _blood / _alpha_blood / _clean_alpha sibling at run time
    # (the blood map is only bound by the *_blood shaders although the material names the plain one), so every
    # shader gets the union over its sibling group: this only ever widens the "read" set.
    groups = collections.defaultdict(set)
    for name in result:
        groups[VARIANT_RE.sub('', name)].update(result[name])
    result = {name: sorted(groups[VARIANT_RE.sub('', name)]) for name in result}
    for name in [n for n, slots in result.items() if not slots]:
        # A shader that binds no t_xml_ resource at all is not understood (its slots may be read some other way).
        omitted.append((name, 'binds no t_xml_ resource'))
        del result[name]
    sys.stderr.write('files %d blobs %d families %d emitted %d omitted %d\n' % (nfiles, nblobs, len(fams), len(result), len(omitted)))
    for n, why in omitted:
        sys.stderr.write('  omitted %s: %s\n' % (n, why))
    with open(out, 'w', newline='\n') as w:
        w.write('// <auto-generated>\n')
        w.write('// Generated by Tools/ShaderChannels/shader_bound_slots.py from the compiled game shaders (RDEF resource names\n')
        w.write('// of every variant and stage). Do not edit by hand; regenerate with the script.\n')
        w.write('// </auto-generated>\n')
        w.write('using System;\nusing System.Collections.Generic;\n\n')
        w.write('namespace Editors.PackOptimizer.Core\n{\n')
        w.write('    public sealed partial class PackTextureAtlasBatchService\n    {\n')
        w.write('        /// <summary>Shader path -> the t_xml_* texture slots (lower case) the compiled shader binds in any variant/stage.</summary>\n')
        w.write('        private static readonly Dictionary<string, HashSet<string>> SharedRepackShaderBoundSlots =\n')
        w.write('            new(StringComparer.OrdinalIgnoreCase)\n            {\n')
        for name, slots in result.items():
            w.write('                ["shaders/%s.xml.shader"] = new(StringComparer.OrdinalIgnoreCase) { %s },\n' % (name, ', '.join('"%s"' % s for s in slots)))
        w.write('            };\n    }\n}\n')


if __name__ == '__main__':
    main()
