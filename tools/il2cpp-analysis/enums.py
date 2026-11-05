import sys, struct, os
sys.path.insert(0, os.path.dirname(__file__))
from il2 import IL2
il = IL2()
m = il.m
def const(fi):
    if fi not in m.fieldDefaults: return None
    ti, di = m.fieldDefaults[fi]
    tn = il.tname(ti)
    o = m.fpdvData[0] + di
    fmt = {'int':'<i','uint':'<I','short':'<h','ushort':'<H','byte':'<B','sbyte':'<b','long':'<q','ulong':'<Q','bool':'<?','float':'<f','double':'<d'}.get(tn)
    def cu(o):
        b = m.d[o]
        if b & 0x80 == 0: return b
        if b & 0xC0 == 0x80: return ((b & 0x3f) << 8) | m.d[o+1]
        if b & 0xE0 == 0xC0: return ((b & 0x1f) << 24) | (m.d[o+1] << 16) | (m.d[o+2] << 8) | m.d[o+3]
        if b == 0xF0: return struct.unpack_from('<I', m.d, o+1)[0]
        if b == 0xFE: return 0xFFFFFFFE
        return 0xFFFFFFFF
    if tn in ('int','short','sbyte','long'):
        u = cu(o)
        if u == 0xFFFFFFFF: return -2**31
        return -((u >> 1) + 1) if u & 1 else u >> 1
    if tn in ('uint','ushort'): return cu(o)
    if fmt: return struct.unpack_from(fmt, m.d, o)[0]
    if tn == 'string':
        b = m.d[o]
        if b & 0x80 == 0: u, o = b, o+1
        elif b & 0xC0 == 0x80: u, o = ((b&0x3f)<<8)|m.d[o+1], o+2
        elif b & 0xE0 == 0xC0: u, o = ((b&0x1f)<<24)|(m.d[o+1]<<16)|(m.d[o+2]<<8)|m.d[o+3], o+4
        else: u, o = struct.unpack_from('<I', m.d, o+1)[0], o+5
        ln = (u >> 1) if not (u & 1) else -1
        if ln < 0: return 'null'
        return repr(m.d[o:o+ln].decode('utf8','replace'))
    return '<%s>' % tn
pat = sys.argv[1]
for ti, t in enumerate(m.types):
    n = il.tdname(ti)
    if pat not in n: continue
    print('==', n)
    for k in range(t['fieldCount']):
        fi = t['fieldStart'] + k
        f = m.fields[fi]
        c = const(fi)
        print('  %s : %s%s' % (m.s(f[0]), il.tname(f[1]), '' if c is None else ' = %s' % c))
