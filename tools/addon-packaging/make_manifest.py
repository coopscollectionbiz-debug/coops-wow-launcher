"""Derive the Release 1.2.1 launcher manifest from the live Release 1.2 manifest.

Only the MultiBot and DungeonClear addon entries plus release identifiers change.
usage: python -I make_manifest.py <live-manifest.json> <addons_dir> <out-manifest.json>
"""
import hashlib, json, os, sys

live, addons, out = sys.argv[1:]
m = json.load(open(live, encoding='utf-8'))
assert m['releaseId'] == 'release1.2-20261109.1', m['releaseId']

updates = {
    'MultiBot': 'MultiBot-chatless-020210b.zip',
    'DungeonClear': 'DungeonClear-3.6-088e6e0.zip',
}
seen = set()
for f in m['files']:
    if f['type'] == 'addon' and f['name'] in updates:
        p = os.path.join(addons, updates[f['name']])
        data = open(p, 'rb').read()
        f['sourceFile'] = os.path.basename(p)
        f['sha256'] = hashlib.sha256(data).hexdigest()
        f['size'] = len(data)
        seen.add(f['name'])
assert seen == set(updates), seen

m['releaseId'] = 'release1.2.1-20261109.1'
m['payloadVersion'] = 'release1.2.1-client-20261109.1'
m['p2ManifestRevision'] += '; Release 1.2.1: MultiBot-Chatless 020210b (needs mod-multibot-bridge 9f0e8ba) and DungeonClear 3.6'
text = json.dumps(m, indent=2) + '\n'
open(out, 'w', encoding='utf-8', newline='\n').write(text)
print(out, hashlib.sha256(text.encode()).hexdigest())
