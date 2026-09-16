import json, struct, sys, re, collections


def load(p):
    with open(p, 'rb') as f:
        n = struct.unpack('<Q', f.read(8))[0]
        h = json.loads(f.read(n))
    md = h.pop('__metadata__', {})
    return h, md


def nelem(h):
    tot = 0
    for v in h.values():
        sh = v.get('shape') or []
        n = 1
        for s in sh:
            n *= s
        tot += n
    return tot


A, B = sys.argv[1], sys.argv[2]
ha, ma = load(A)
hb, mb = load(B)

print('=== A :', A.split('/')[-1])
print('    metadata :', json.dumps(ma)[:1200])
print('    tensors  :', len(ha), ' elements :', nelem(ha))
print('=== B :', B.split('/')[-1])
print('    metadata :', json.dumps(mb)[:1200])
print('    tensors  :', len(hb), ' elements :', nelem(hb))
print('=== element ratio B/A : %.4f' % (nelem(hb) / nelem(ha)))

sa, sb = set(ha), set(hb)
rem, add = sa - sb, sb - sa
print('=== only in A (removed) :', len(rem), ' only in B (added) :', len(add))


def sig(k):
    return re.sub(r'\d+', '#', k)


ca = collections.Counter(sig(k) for k in sa)
cb = collections.Counter(sig(k) for k in sb)
print('=== per-module tensor count diff (only when changed) ===')
for k in sorted(set(ca) | set(cb)):
    if ca.get(k, 0) != cb.get(k, 0):
        print('   %-72s %6d -> %6d' % (k, ca.get(k, 0), cb.get(k, 0)))

print('=== top-level block, removed tensor count ===')
blk = collections.Counter()
for k in rem:
    m = re.match(r'^([^.]+\.[^.]+\.)', k)
    blk[m.group(1) if m else k] += 1
for k, v in blk.most_common(50):
    print('   %-64s %d' % (k, v))

print('=== shape mismatch on common keys ===')
n = 0
for k in sorted(sa & sb):
    if ha[k].get('shape') != hb[k].get('shape'):
        print('   ! %-70s %s -> %s' % (k, ha[k].get('shape'), hb[k].get('shape')))
        n += 1
        if n > 30:
            print('   ... truncated')
            break
print('   mismatch shown :', n)

print('=== removed key samples ===')
for k in sorted(rem)[:40]:
    print('   -', k)
print('=== added key samples ===')
for k in sorted(add)[:40]:
    print('   +', k)
