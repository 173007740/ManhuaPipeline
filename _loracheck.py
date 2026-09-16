import json, struct, sys, re, collections


def load(p):
    with open(p, 'rb') as f:
        n = struct.unpack('<Q', f.read(8))[0]
        h = json.loads(f.read(n))
    md = h.pop('__metadata__', {})
    return h, md


full, pruned, lora = sys.argv[1], sys.argv[2], sys.argv[3]
hf, _ = load(full)
hp, _ = load(pruned)
hl, ml = load(lora)

print('=== LORA :', lora.split('/')[-1])
print('    metadata :', json.dumps(ml)[:900])
print('    tensors  :', len(hl))

pat = re.compile(r'\.(lora_A|lora_B|lora_down|lora_up|lora_alpha|alpha|diff|diff_b)(\.|$)')
bases = collections.Counter()
for k in hl:
    b = pat.sub('', k).rstrip('.')
    bases[b] += 1

sf, sp = set(hf), set(hp)
print('=== distinct lora target modules :', len(bases))
miss_p = sorted(b for b in bases if b not in sp)
miss_f = sorted(b for b in bases if b not in sf)
print('    targets missing in PRUNED model :', len(miss_p))
for b in miss_p[:40]:
    print('      MISSING-PRUNED', b)
print('    targets missing in FULL model   :', len(miss_f))
for b in miss_f[:20]:
    print('      MISSING-FULL', b)

print('=== lora target touching the pruned-away path ===')
for b in sorted(bases):
    if 'adaln' in b or 'time_embed' in b:
        print('      HIT', b)

print('=== lora target signature summary ===')
for k, v in collections.Counter(re.sub(r'\d+', '#', b) for b in bases).most_common(40):
    print('   %-72s %d' % (k, v))

print('=== sample lora keys ===')
for k in sorted(hl)[:25]:
    print('   ', k)
