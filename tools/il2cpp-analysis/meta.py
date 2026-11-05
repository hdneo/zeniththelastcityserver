"""Minimal IL2CPP global-metadata.dat v29 parser (names only + string literals)."""
import struct, sys, json

import os
GAME = os.environ.get('ZENITH_GAME_DIR', r'C:/Program Files/Oculus/Software/Software/ramen-vr-zenith')
META = os.path.join(GAME, 'UnityClient@Windows_Data', 'il2cpp_data', 'Metadata', 'global-metadata.dat')

class Meta:
    def __init__(self, path=META):
        self.d = open(path, 'rb').read()
        h = struct.unpack_from('<64I', self.d, 0)
        self.h = h
        def sec(i): return h[i], h[i + 1]
        self.strLit = sec(2); self.strLitData = sec(4); self.str = sec(6)
        self.events = sec(8); self.props = sec(10); self.methodsSec = sec(12)
        self.fdv = sec(16); self.fpdvData = sec(18)
        self.paramsSec = sec(22); self.fieldsSec = sec(24)
        self.nestedSec = sec(32)
        self.typedefsSec = sec(40); self.imagesSec = sec(42)
        self._parse()

    def s(self, idx):
        o = self.str[0] + idx
        e = self.d.index(b'\0', o)
        return self.d[o:e].decode('utf8', 'replace')

    def _parse(self):
        d = self.d
        o, n = self.typedefsSec
        self.types = []
        for i in range(n // 88):
            f = struct.unpack_from('<16i8H2I', d, o + i * 88)
            self.types.append(dict(
                name=f[0], ns=f[1], byval=f[2], declaring=f[3], parent=f[4], elem=f[5], gc=f[6], flags=f[7],
                fieldStart=f[8], methodStart=f[9], eventStart=f[10], propStart=f[11], nestedStart=f[12],
                ifaceStart=f[13], vtStart=f[14], ioStart=f[15],
                methodCount=f[16], propCount=f[17], fieldCount=f[18], eventCount=f[19], nestedCount=f[20],
                vtCount=f[21], ifaceCount=f[22], ioCount=f[23], bits=f[24], token=f[25]))
        o, n = self.methodsSec
        self.methods = [struct.unpack_from('<6i4H', d, o + i * 32) for i in range(n // 32)]
        o, n = self.fieldsSec
        self.fields = [struct.unpack_from('<3i', d, o + i * 12) for i in range(n // 12)]
        o, n = self.paramsSec
        self.params = [struct.unpack_from('<3i', d, o + i * 12) for i in range(n // 12)]
        o, n = self.imagesSec
        self.images = [struct.unpack_from('<10i', d, o + i * 40) for i in range(n // 40)]
        o, n = self.nestedSec
        self.nested = struct.unpack_from('<%di' % (n // 4), d, o)
        o, n = self.fdv
        # fieldIndex, typeIndex, dataIndex
        self.fieldDefaults = {}
        for i in range(n // 12):
            fi, ti, di = struct.unpack_from('<3i', d, o + i * 12)
            self.fieldDefaults[fi] = (ti, di)
        # type -> image
        self.typeImage = {}
        for img in self.images:
            nm = self.s(img[0])
            for t in range(img[2], img[2] + img[3]):
                self.typeImage[t] = nm

    def fullname(self, ti):
        t = self.types[ti]
        nm = self.s(t['name'])
        if t['declaring'] >= 0:
            # declaring is a type index into Il2CppType table (binary) - can't resolve here; use nested table
            pass
        ns = self.s(t['ns'])
        return (ns + '.' if ns else '') + nm

    def string_literals(self):
        o, n = self.strLit
        out = []
        for i in range(n // 8):
            ln, di = struct.unpack_from('<Ii', self.d, o + i * 8)
            b = self.d[self.strLitData[0] + di: self.strLitData[0] + di + ln]
            out.append(b.decode('utf8', 'replace'))
        return out


if __name__ == '__main__':
    m = Meta()
    # build nested-parent map
    parentOf = {}
    for ti, t in enumerate(m.types):
        for k in range(t['nestedCount']):
            parentOf[m.nested[t['nestedStart'] + k]] = ti
    def fn(ti):
        if ti in parentOf:
            return fn(parentOf[ti]) + '/' + m.s(m.types[ti]['name'])
        return m.fullname(ti)
    out = open(sys.argv[1], 'w', encoding='utf8')
    for ti, t in enumerate(m.types):
        out.write('\n// [%s] type#%d flags=%x\n' % (m.typeImage.get(ti), ti, t['flags']))
        out.write('class %s {\n' % fn(ti))
        for k in range(t['fieldCount']):
            fi = t['fieldStart'] + k
            f = m.fields[fi]
            out.write('  field %s  (typeIdx=%d)\n' % (m.s(f[0]), f[1]))
        for k in range(t['methodCount']):
            mi = t['methodStart'] + k
            md = m.methods[mi]
            ps = []
            for p in range(md[9]):
                pd = m.params[md[3] + p]
                ps.append('%s:t%d' % (m.s(pd[0]), pd[2]))
            out.write('  method#%d %s(%s) ret=t%d\n' % (mi, m.s(md[0]), ', '.join(ps), md[2]))
        out.write('}\n')
    out.close()
    with open(sys.argv[2], 'w', encoding='utf8') as f:
        for i, sl in enumerate(m.string_literals()):
            f.write('%d\t%s\n' % (i, sl.replace('\n', '\\n').replace('\r', '\\r')))
    print('types', len(m.types), 'methods', len(m.methods))
