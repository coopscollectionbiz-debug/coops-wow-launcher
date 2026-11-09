"""Build a deterministic launcher addon ZIP: <AddonName>/... at the archive root.

usage: python -I make_addon_zip.py <src_dir> <AddonName> <out.zip> [exclude ...]
Excluded names are matched against each path component (exact) or file suffix (*.ext).
"""
import hashlib, os, sys, zipfile

src, name, out, *excl = sys.argv[1:]
def skip(rel):
    parts = rel.split('/')
    for e in excl:
        if e.startswith('*.') and rel.endswith(e[1:]):
            return True
        if e in parts:
            return True
    return False

files = []
for root, dirs, fs in os.walk(src):
    dirs.sort()
    for f in sorted(fs):
        p = os.path.join(root, f)
        rel = os.path.relpath(p, src).replace(os.sep, '/')
        if not skip(rel):
            files.append((rel, p))
files.sort()
with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for rel, p in files:
        zi = zipfile.ZipInfo(f'{name}/{rel}', date_time=(2026, 11, 9, 0, 0, 0))
        zi.compress_type = zipfile.ZIP_DEFLATED
        zi.external_attr = 0o644 << 16
        with open(p, 'rb') as fh:
            z.writestr(zi, fh.read())
data = open(out, 'rb').read()
print(f'{out}: {len(files)} files, {len(data)} bytes, sha256 {hashlib.sha256(data).hexdigest()}')
