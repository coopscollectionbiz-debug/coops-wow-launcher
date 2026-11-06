"""Create verified release assets from read-only official HD sources. Never upload."""
import hashlib, json, pathlib, re

R = pathlib.Path(__file__).resolve().parent
CLIENT = pathlib.Path('C:/WoWClient')
SOURCE = pathlib.Path('C:/coops-wow-launcher')
ASSETS = R/'distribution/assets'
ASSETS.mkdir(parents=True, exist_ok=True)
CHUNK = 1_000_000_000

def sha(path):
    with path.open('rb') as f: return hashlib.file_digest(f,'sha256').hexdigest()

def copy_verified(source, name, expected, size):
    target = ASSETS/name
    before = source.stat()
    if before.st_size != size: raise ValueError('Source size mismatch: '+str(source))
    if target.exists():
        if target.stat().st_size != size or sha(target) != expected: raise ValueError('Existing asset mismatch: '+name)
    else:
        h=hashlib.sha256()
        with source.open('rb') as inp, target.open('xb') as out:
            while b:=inp.read(8<<20): out.write(b); h.update(b)
        if h.hexdigest()!=expected: raise ValueError('Source hash mismatch: '+str(source))
    if (before.st_size,before.st_mtime_ns)!=(source.stat().st_size,source.stat().st_mtime_ns):
        raise ValueError('Source changed during read: '+str(source))
    return target

m=json.loads((R/'manifest.json').read_text())
m['files']=[f for f in m['files'] if not f.get('suppliesBaseline')]
m['minimumLauncherVersion']='1.2.0'
m['releaseId']='release1-hd-prerequisites-20261106.1'
managed=[]
for b in m['requiredBaseline']:
    if b.get('optional') or not re.fullmatch(r'Data/(?:patch-[5-9a-z]|enus/patch-enus-[a-z])\.mpq',b['path'],re.I): continue
    source=CLIENT/b['path']
    name='hd-'+b['sha256']+'.mpq'
    f=dict(name=b['path'],sourceFile=name,sha256=b['sha256'],size=b['size'],
           type='file',destination=b['path'],suppliesBaseline=True)
    if b['size'] < 2**31:
        copy_verified(source,name,b['sha256'],b['size'])
    else:
        before=source.stat(); full=hashlib.sha256(); parts=[]
        with source.open('rb') as inp:
            remaining=b['size']; index=0
            while remaining:
                index+=1; size=min(CHUNK,remaining); remaining-=size
                partname=name+'.part'+str(index).zfill(3)
                p=ASSETS/partname; h=hashlib.sha256(); left=size
                # Repeat runs safely verify existing assets while reading the source.
                out=None if p.exists() else p.open('xb')
                try:
                    while left:
                        buf=inp.read(min(8<<20,left))
                        if not buf: raise ValueError('Source ended early')
                        left-=len(buf); h.update(buf); full.update(buf)
                        if out: out.write(buf)
                finally:
                    if out: out.close()
                if p.stat().st_size!=size or sha(p)!=h.hexdigest(): raise ValueError('Part verification failed: '+partname)
                parts.append(dict(sourceFile=partname,size=size,sha256=h.hexdigest()))
        if full.hexdigest()!=b['sha256']: raise ValueError('Full source hash mismatch: '+str(source))
        after=source.stat()
        if (before.st_size,before.st_mtime_ns)!=(after.st_size,after.st_mtime_ns): raise ValueError('Source changed')
        f['parts']=parts
    managed.append(f)
    print('Verified '+b['path'],flush=True)
m['files'].extend(managed)
for f in m['files']:
    if f.get('suppliesBaseline'): continue
    source=CLIENT/'Data/patch-z.mpq' if f['type']=='file' else SOURCE/'addon-packages'/f['sourceFile']
    copy_verified(source,f['sourceFile'],f['sha256'],f['size'])
    print('Verified '+f['name'],flush=True)
(R/'manifest.json').write_text(json.dumps(m,indent=2)+'\n')
(ASSETS/'manifest.json').write_text(json.dumps(m,indent=2)+'\n')
inventory=[]
for p in sorted(ASSETS.iterdir()):
    inventory.append(dict(name=p.name,size=p.stat().st_size,sha256=sha(p)))
(R/'distribution/asset-inventory.json').write_text(json.dumps(inventory,indent=2)+'\n')
print('Prepared',len(managed),'managed prerequisites,',len(inventory),'assets;',sum(x['size'] for x in inventory),'bytes',flush=True)
