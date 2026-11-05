"""Find callers of methods matching a regex (direct E8 calls / E9 jmps). Builds a cache of all call edges once."""
import sys, os, re, struct, bisect, pickle
sys.path.insert(0, os.path.dirname(__file__))
from il2 import IL2, OUT

il = IL2()
pe = il.pe
cache = os.path.join(OUT, 'calls.pkl')
if os.path.exists(cache):
    edges = pickle.load(open(cache, 'rb'))
else:
    text = [s for s in pe.secs if s[0] in ('.text', 'il2cpp')]
    valid = set(il.byrva)
    edges = {}  # target -> set(caller rva)
    for name, va, vsz, rp, rs in text:
        d = pe.d[rp:rp + rs]
        for m in re.finditer(b'[\xe8\xe9]', d):
            i = m.start()
            if i + 5 > len(d): break
            rel = struct.unpack_from('<i', d, i + 1)[0]
            tgt = va + i + 5 + rel
            if tgt in valid:
                edges.setdefault(tgt, []).append(va + i)
    pickle.dump(edges, open(cache, 'wb'))

def owner(rva):
    i = bisect.bisect_right(il.sorted, rva) - 1
    return il.byrva[il.sorted[i]] if i >= 0 else '?'

pat = re.compile(sys.argv[1])
for tgt, name in il.byrva.items():
    if pat.search(name):
        callers = sorted(set(owner(c) for c in edges.get(tgt, [])))
        print('##', name)
        for c in callers:
            print('    <-', c)
