"""IL2CPP helper: type resolution, method map, annotated disassembly via objdump."""
import struct, sys, os, re, subprocess, bisect, pickle
sys.path.insert(0, os.path.dirname(__file__))
from meta import Meta
from binmap import PE, GA

OBJDUMP = os.environ.get('OBJDUMP', r'C:/Program Files/JetBrains/CLion 2025.3.1/bin/mingw/bin/objdump.exe')
OUT = os.environ.get('ZENITH_OUT', os.path.join(os.path.dirname(os.path.abspath(__file__)), 'out'))
os.makedirs(OUT, exist_ok=True)
PRIM = {1: 'void', 2: 'bool', 3: 'char', 4: 'sbyte', 5: 'byte', 6: 'short', 7: 'ushort', 8: 'int', 9: 'uint',
        0xa: 'long', 0xb: 'ulong', 0xc: 'float', 0xd: 'double', 0xe: 'string', 0x18: 'IntPtr', 0x19: 'UIntPtr',
        0x1c: 'object', 0x16: 'TypedReference'}


class IL2:
    def __init__(self):
        self.m = Meta()
        self.pe = PE(GA)
        self._parent = {}
        for ti, t in enumerate(self.m.types):
            for k in range(t['nestedCount']):
                self._parent[self.m.nested[t['nestedStart'] + k]] = ti
        self._find_metareg()
        self._load_methods()

    def tdname(self, ti, short=False):
        if ti in self._parent:
            return self.tdname(self._parent[ti], short) + '/' + self.m.s(self.m.types[ti]['name'])
        if short:
            return self.m.s(self.m.types[ti]['name'])
        return self.m.fullname(ti)

    def _find_metareg(self):
        pe = self.pe; d = pe.d
        n = len(self.m.types)
        pat = struct.pack('<Q', n)
        for sn, va, vsz, rp, rs in pe.secs:
            if sn not in ('.data', '.rdata'): continue
            p = d.find(pat, rp, rp + rs)
            while p >= 0:
                if struct.unpack_from('<Q', d, p + 16)[0] == n:
                    # p = fieldOffsetsCount
                    base = p - 8 * 10
                    tc, tp = struct.unpack_from('<QQ', d, base + 48)
                    if 10000 < tc < 1000000:
                        self.typesCount, self.typesPtr = tc, tp
                        self.methodSpecsPtr = struct.unpack_from('<Q', d, base + 72)[0]
                        self.fieldOffsetsPtr = struct.unpack_from('<Q', d, p + 8)[0]
                        return
                p = d.find(pat, p + 8, rp + rs)
        raise Exception('metadata registration not found')

    def typeptr(self, idx):
        return self.pe.q(self.typesPtr - self.pe.base + idx * 8)

    def tname_ptr(self, ptr, depth=0):
        if depth > 8 or ptr == 0: return '?'
        o = self.pe.rva2off(ptr - self.pe.base)
        data, bits = struct.unpack_from('<QI', self.pe.d, o)
        t = (bits >> 16) & 0xff
        byref = (bits >> 29) & 1
        s = self._tname(t, data, depth)
        return s + ('&' if byref else '')

    def _tname(self, t, data, depth):
        if t in PRIM: return PRIM[t]
        if t in (0x11, 0x12):
            return self.tdname(data, short=True)
        if t == 0x1d:
            return self.tname_ptr(data, depth + 1) + '[]'
        if t == 0x14:
            return self.tname_ptr(struct.unpack_from('<Q', self.pe.d, self.pe.rva2off(data - self.pe.base))[0], depth + 1) + '[,]'
        if t == 0x0f:
            return self.tname_ptr(data, depth + 1) + '*'
        if t == 0x15:
            go = self.pe.rva2off(data - self.pe.base)
            tp, inst = struct.unpack_from('<QQ', self.pe.d, go)
            base = self.tname_ptr(tp, depth + 1)
            args = []
            if inst:
                io = self.pe.rva2off(inst - self.pe.base)
                argc, argv = struct.unpack_from('<QQ', self.pe.d, io)
                if argc < 20:
                    for i in range(argc):
                        args.append(self.tname_ptr(self.pe.q(argv - self.pe.base + 8 * i), depth + 1))
            return base.split('`')[0] + '<' + ','.join(args) + '>'
        if t in (0x13, 0x1e):
            return '!T'
        return 't%x' % t

    def tname(self, idx):
        try:
            return self.tname_ptr(self.typeptr(idx))
        except Exception as e:
            return 'T#%d' % idx

    def _load_methods(self):
        cache = os.path.join(OUT, 'methods.pkl')
        if os.path.exists(cache):
            self.byrva, self.byname, self.sorted = pickle.load(open(cache, 'rb'))
            return
        self.byrva = {}; self.byname = {}
        for line in open(os.path.join(OUT, 'methods.tsv'), encoding='utf8'):
            img, mi, name, rva = line.rstrip('\n').split('\t')
            rva = int(rva, 16)
            self.byname.setdefault(name, []).append((rva, int(mi)))
            if rva:
                self.byrva.setdefault(rva, name)
        self.sorted = sorted(self.byrva)
        pickle.dump((self.byrva, self.byname, self.sorted), open(cache, 'wb'))

    def usage(self, rva):
        """Decode an IL2CPP metadata-usage global at rva."""
        o = self.pe.rva2off(rva)
        if o is None: return None
        v = struct.unpack_from('<Q', self.pe.d, o)[0]
        if not (v & 1) or v > 0xffffffff: return None
        kind = (v >> 29) & 7
        idx = (v & 0x1FFFFFFE) >> 1
        try:
            if kind == 1: return 'TypeInfo<%s>' % self.tname(idx)
            if kind == 2: return 'Type<%s>' % self.tname(idx)
            if kind == 3:
                md = self.m.methods[idx]
                return 'Method<%s::%s>' % (self.tdname(md[1], True), self.m.s(md[0]))
            if kind == 4:
                o = self.m.h[46] + idx * 8
                tix, fix = struct.unpack_from('<ii', self.m.d, o)
                tp = self.typeptr(tix)
                td = struct.unpack_from('<Q', self.pe.d, self.pe.rva2off(tp - self.pe.base))[0]
                t = self.m.types[td]
                return 'Field<%s.%s>' % (self.tdname(td, True), self.m.s(self.m.fields[t['fieldStart'] + fix][0]))
            if kind == 5:
                sl = self.m.string_literals_cached()[idx] if hasattr(self.m, '_sl') else None
                if sl is None:
                    self.m._sl = self.m.string_literals()
                    sl = self.m._sl[idx]
                return 'Str"%s"' % sl.replace('\n', '\\n')[:120]
            if kind == 6:
                o = self.pe.rva2off(self.methodSpecsPtr - self.pe.base) + idx * 12
                mdi, ci, mi = struct.unpack_from('<iii', self.pe.d, o)
                md = self.m.methods[mdi]
                return 'Method<%s::%s>' % (self.tdname(md[1], True), self.m.s(md[0]))
        except Exception:
            return None
        return None

    def disasm(self, name_or_rva, maxlen=0x3000):
        if isinstance(name_or_rva, str):
            rvas = [r for r, _ in self.byname.get(name_or_rva, []) if r]
            if not rvas: return '<no such method %s>' % name_or_rva
            rva = rvas[0]
        else:
            rva = name_or_rva
        i = bisect.bisect_right(self.sorted, rva)
        end = self.sorted[i] if i < len(self.sorted) else rva + maxlen
        end = min(end, rva + maxlen)
        va = self.pe.base + rva
        out = subprocess.run([OBJDUMP, '-d', '-M', 'intel', '--no-show-raw-insn',
                              '--start-address=0x%x' % va, '--stop-address=0x%x' % (self.pe.base + end), GA],
                             capture_output=True, text=True).stdout
        lines = []
        for ln in out.splitlines():
            if not re.match(r'\s+[0-9a-f]+:', ln): continue
            ann = ''
            mm = re.search(r'(call|jmp)\s+(?:0x)?([0-9a-f]+)\b', ln)
            if mm:
                t = int(mm.group(2), 16) - self.pe.base
                if t in self.byrva and not (rva <= t < end): ann = self.byrva[t]
            mm = re.search(r'# ([0-9a-f]+) <', ln) or re.search(r'# 0x([0-9a-f]+)', ln) or re.search(r'# ([0-9a-f]+)$', ln)
            if mm:
                t = int(mm.group(1), 16) - self.pe.base
                u = self.usage(t)
                if u: ann = u
                elif t in self.byrva: ann = self.byrva[t]
            ln = re.sub(r'<[^>]*>', '', ln)
            lines.append(ln.rstrip() + ('    ; ' + ann if ann else ''))
        return '\n'.join(lines)


def _sl_cached(self):
    if not hasattr(self, '_sl'):
        self._sl = self.string_literals()
    return self._sl
Meta.string_literals_cached = _sl_cached

if __name__ == '__main__':
    il = IL2()
    for a in sys.argv[1:]:
        print('=====', a)
        print(il.disasm(int(a, 16) if re.fullmatch(r'[0-9a-f]+', a) else a))
