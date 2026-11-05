"""Map IL2CPP methods -> RVA in GameAssembly.dll via CodeGenModules. Writes methods.tsv"""
import struct, sys, mmap, os
sys.path.insert(0, os.path.dirname(__file__))
from meta import Meta

from meta import GAME
GA = os.path.join(GAME, 'GameAssembly.dll')

class PE:
    def __init__(self, path):
        f = open(path, 'rb')
        self.d = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
        pe = struct.unpack_from('<I', self.d, 0x3c)[0]
        nsec = struct.unpack_from('<H', self.d, pe + 6)[0]
        optsz = struct.unpack_from('<H', self.d, pe + 20)[0]
        self.base = struct.unpack_from('<Q', self.d, pe + 24 + 24)[0]
        self.secs = []
        so = pe + 24 + optsz
        for i in range(nsec):
            name = self.d[so:so + 8].rstrip(b'\0').decode()
            vsz, va, rsz, rptr = struct.unpack_from('<4I', self.d, so + 8)
            self.secs.append((name, va, vsz, rptr, rsz))
            so += 40

    def rva2off(self, rva):
        for n, va, vsz, rp, rs in self.secs:
            if va <= rva < va + max(vsz, rs):
                return rp + rva - va
        return None

    def off2rva(self, off):
        for n, va, vsz, rp, rs in self.secs:
            if rp <= off < rp + rs:
                return va + off - rp
        return None

    def q(self, rva):
        return struct.unpack_from('<Q', self.d, self.rva2off(rva))[0]

    def cstr(self, rva):
        o = self.rva2off(rva)
        e = self.d.find(b'\0', o)
        return self.d[o:e].decode('utf8', 'replace')


def find_modules(pe, imageNames):
    """Find Il2CppCodeGenModule for each image name by pointer search."""
    res = {}
    data = pe.d
    # find data sections
    dsecs = [s for s in pe.secs if s[0] in ('.data', '.rdata')]
    for nm in imageNames:
        b = nm.encode() + b'\0'
        pos = 0
        cands = []
        while True:
            pos = data.find(b'\0' + b, pos)
            if pos < 0: break
            cands.append(pos + 1); pos += 1
        for off in cands:
            rva = pe.off2rva(off)
            if rva is None: continue
            ptr = struct.pack('<Q', pe.base + rva)
            for sn, va, vsz, rp, rs in dsecs:
                p = data.find(ptr, rp, rp + rs)
                while p >= 0:
                    if (p - rp) % 8 == 0:
                        mrva = va + p - rp
                        cnt = struct.unpack_from('<I', data, p + 8)[0]
                        mp = struct.unpack_from('<Q', data, p + 16)[0]
                        if cnt < 1000000 and (mp == 0 or pe.rva2off(mp - pe.base)):
                            res[nm] = (mrva, cnt, mp)
                    p = data.find(ptr, p + 1, rp + rs)
            if nm in res: break
    return res


if __name__ == '__main__':
    m = Meta()
    pe = PE(GA)
    imgs = [m.s(img[0]) for img in m.images]
    mods = find_modules(pe, imgs)
    print('found modules', len(mods), 'of', len(imgs), file=sys.stderr)
    out = open(sys.argv[1], 'w', encoding='utf8')
    parentOf = {}
    for ti, t in enumerate(m.types):
        for k in range(t['nestedCount']):
            parentOf[m.nested[t['nestedStart'] + k]] = ti
    def fn(ti):
        if ti in parentOf:
            return fn(parentOf[ti]) + '/' + m.s(m.types[ti]['name'])
        return m.fullname(ti)
    for img in m.images:
        nm = m.s(img[0])
        if nm not in mods: continue
        mrva, cnt, mp = mods[nm]
        for ti in range(img[2], img[2] + img[3]):
            t = m.types[ti]
            for k in range(t['methodCount']):
                mi = t['methodStart'] + k
                md = m.methods[mi]
                rid = md[5] & 0xffffff
                if mp and 0 < rid <= cnt:
                    va = pe.q(mp - pe.base + (rid - 1) * 8)
                    rva = va - pe.base if va else 0
                else:
                    rva = 0
                out.write('%s\t%d\t%s::%s\t%x\n' % (nm, mi, fn(ti), m.s(md[0]), rva))
    out.close()
