import sys, os, re, struct
sys.path.insert(0, os.path.dirname(__file__))
from il2 import IL2, OUT
il = IL2(); m = il.m
names = {}
for l in open(os.path.join(OUT,'component_ids.tsv'),encoding='utf8'):
    k,c,n = l.rstrip('\n').split('\t'); names[int(c)] = n
# map field name -> raw data
pid_ti = [ti for ti in range(len(m.types)) if il.tdname(ti)=='<PrivateImplementationDetails>' and m.typeImage.get(ti)=='Improbable.Gdk.Generated.dll']
fdata = {}
for ti in pid_ti:
    t = m.types[ti]
    for k in range(t['fieldCount']):
        fi = t['fieldStart']+k
        if fi in m.fieldDefaults:
            fdata[m.s(m.fields[fi][0])] = m.fpdvData[0] + m.fieldDefaults[fi][1]
asm = il.disasm('Improbable.Generated.ComponentSets::.cctor', maxlen=0x10000)
count=None; field=None; edx=None; sets=[]; arr=None; regs={}
for ln in asm.splitlines():
    mm = re.search(r'mov\s+edx,0x([0-9a-f]+)', ln)
    if mm: edx = int(mm.group(1),16)
    mx = re.search(r'xor\s+(r\d+)d,d', ln)
    if mx: regs[mx.group(1)] = 0
    ml = re.search(r'lea\s+edx,\[(r\d+)\+0x([0-9a-f]+)\]', ln)
    if ml and ml.group(1) in regs: edx = regs[ml.group(1)] + int(ml.group(2),16)
    if 'call' in ln and '1808feae0' in ln: count = edx; arr = [count, None, []]
    ms = re.search(r'mov\s+DWORD PTR \[r\w+\+0x20\],0x([0-9a-f]+)', ln)
    if ms and arr is not None and arr[1] is None: arr[2].append(int(ms.group(1),16))
    mf = re.search(r'mov\s+rdx,.*Field<<PrivateImplementationDetails>\.(\w+)>', ln)
    if mf: field = mf.group(1)
    if 'InitializeArray' in ln: arr[1] = field; edx=None
    if 'ComponentSet::.ctor' in ln:
        n, f, direct = arr
        ids = list(struct.unpack_from('<%dI'%n, m.d, fdata[f])) if f in fdata else direct
        sets.append((edx, ids)); edx=None; arr=None
# field order in ComponentSets class
ti = [ti for ti in range(len(m.types)) if il.tdname(ti)=='Improbable.Generated.ComponentSets'][0]
t = m.types[ti]
fnames = [m.s(m.fields[t['fieldStart']+k][0]) for k in range(t['fieldCount'])]
with open(os.path.join(OUT,'component_sets.txt'),'w',encoding='utf8') as o:
    for i,(sid,ids) in enumerate(sets):
        o.write('set %s = %s  // %d components\n' % (fnames[i] if i < len(fnames) else '?', sid, len(ids)))
        for c in ids: o.write('    %-6d %s\n' % (c, names.get(c,'?')))
print(len(sets)); print([ (fnames[i] if i<len(fnames) else '?', s[0], len(s[1])) for i,s in enumerate(sets)])
