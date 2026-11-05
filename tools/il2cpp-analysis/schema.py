"""Reconstruct component schemas: field ids + wire types from generated SerializeComponent (raw),
plus the C# Snapshot field list. Output: out/component_schemas.txt"""
import sys, os, re
sys.path.insert(0, os.path.dirname(__file__))
from il2 import IL2, OUT

il = IL2()
m = il.m
name2ti = {il.tdname(ti): ti for ti in range(len(m.types))}

def fields_of(tname):
    ti = name2ti.get(tname)
    if ti is None: return []
    t = m.types[ti]
    out = []
    for k in range(t['fieldCount']):
        f = m.fields[t['fieldStart'] + k]
        if f[1] >= 0 and il.tname(f[1]).startswith('static'): continue
        n = m.s(f[0])
        if n.startswith('<') and 'BackingField' in n: n = n[1:n.index('>')]
        out.append((n, il.tname(f[1])))
    return out

FAM = {}
for r in ['ax', 'bx', 'cx', 'dx', 'si', 'di', 'bp', 'sp']:
    for v in ('r' + r, 'e' + r, r, r[0] + 'l' if r[1] == 'x' else r + 'l'):
        FAM[v] = r
for i in range(8, 16):
    for v in ('r%d' % i, 'r%dd' % i, 'r%dw' % i, 'r%db' % i):
        FAM[v] = 'r%d' % i
VOL = {'ax', 'cx', 'dx', 'r8', 'r9', 'r10', 'r11'}
LINE = re.compile(r'^\s*[0-9a-f]+:\s+([a-z0-9]+)\s*(.*?)\s*(?:#.*)?(?:;.*)?$')

def extract(asm):
    res = []; depth = 0; regs = {}
    for ln in asm.splitlines():
        mm = LINE.match(ln)
        if not mm: continue
        op, args = mm.group(1), mm.group(2)
        ops = [x.strip() for x in args.split(',')] if args else []
        dst = FAM.get(ops[0]) if ops else None
        if op == 'xor' and len(ops) == 2 and dst and dst == FAM.get(ops[1]):
            regs[dst] = 0
        elif op == 'mov' and dst and len(ops) == 2 and re.fullmatch(r'0x[0-9a-f]+', ops[1]):
            regs[dst] = int(ops[1], 16)
        elif op == 'mov' and dst and len(ops) == 2 and FAM.get(ops[1]):
            if FAM[ops[1]] in regs: regs[dst] = regs[FAM[ops[1]]]
            else: regs.pop(dst, None)
        elif op == 'lea' and dst:
            ml = re.fullmatch(r'\[(\w+)\+0x([0-9a-f]+)\]', ops[1])
            if ml and FAM.get(ml.group(1)) in regs:
                regs[dst] = regs[FAM[ml.group(1)]] + int(ml.group(2), 16)
            else:
                regs.pop(dst, None)
        elif op == 'call':
            mc = re.search(r'SchemaObject::(\w+|\.ctor)', ln)
            if mc:
                fn = mc.group(1)
                if fn == '.ctor': depth += 1
                elif fn == 'Serialize': depth = max(0, depth - 1)
                elif fn.startswith('Add'): res.append((depth, regs.get('dx', -1), fn[3:]))
            for r in VOL: regs.pop(r, None)
        elif dst and op not in ('cmp', 'test', 'push') and ops and not ops[0].startswith(('QWORD', 'DWORD', 'WORD', 'BYTE')):
            regs.pop(dst, None)
    return res

rows = [l.split('\t') for l in open(os.path.join(OUT, 'component_ids.tsv'), encoding='utf8').read().splitlines()]
out = open(os.path.join(OUT, 'component_schemas.txt'), 'w', encoding='utf8')
out.write('// Auto-extracted from GameAssembly.dll (Serialization::SerializeComponent). Raw call order;\n'
          '// indented lines are fields of nested objects / map entries. "?" = field id not resolvable statically.\n'
          '// Wire types: see docs/02_ZenFabric_Protokoll.md (SchemaObject).\n')
for kind, cid, cname in rows:
    if kind != 'ComponentId': continue
    out.write('\ncomponent %s = %s {\n' % (cname, cid))
    ex = extract(il.disasm(cname + '/Serialization::SerializeComponent', maxlen=0x8000))
    lastkey = None
    for d, fid, k in ex:
        key = (d, fid, k)
        if key == lastkey: continue
        lastkey = key
        out.write('  %s%-4s %s\n' % ('    ' * d, fid if fid >= 0 else '?', k))
    out.write('  // C# Snapshot: %s\n' % ', '.join('%s:%s' % x for x in fields_of(cname + '/Snapshot')))
    out.write('}\n')
out.close()
print('ok')
