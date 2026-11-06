"""Verify hosted payloads and package the launcher build. Does not publish."""
import pathlib, json, hashlib, zipfile
R=pathlib.Path(__file__).resolve().parent
A=R/'distribution/assets'
def sha(p):
    with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest()
exe=R/'distribution/standalone/CoopsWoWLauncher.exe'
assert exe.exists() and exe.stat().st_size>50_000_000, 'Expected self-contained build'
readme=R/'distribution/standalone/README.txt'
readme.write_text("Coop's WoW Launcher v1.2.0 (Windows x64)\n\n"
    "Extract this ZIP before running CoopsWoWLauncher.exe. The .NET runtime is included.\n"
    "Choose your supported Coop's HD enUS WoW 3.3.5a folder, then use Check / Install Patch.\n"
    "Only missing required HD archives and required updates download; all payloads verify before activation.\n"
    "Existing mismatched baseline files remain blocked. This is not a complete WoW download.\n"
    "Other HD packs and Reforged are not qualified.\n\n"
    "Required patch downloads are hosted with the v1.2.0 GitHub release.\n")
package=A/'Coops-WoW-Launcher-v1.2.0-win-x64.zip'
with zipfile.ZipFile(package,'w',zipfile.ZIP_DEFLATED,compresslevel=6) as z:
    z.write(exe,exe.name)
    z.write(readme,'README.txt')
with zipfile.ZipFile(package) as z: assert z.testzip() is None
m=json.loads((R/'manifest.json').read_text())
assert (R/'manifest.json').read_bytes()==(A/'manifest.json').read_bytes()
expected={}
for f in m['files']:
    entries=f.get('parts') or [f]
    for entry in entries:
        identity=(entry['size'],entry['sha256'])
        assert entry['sourceFile'] not in expected or expected[entry['sourceFile']]==identity
        expected[entry['sourceFile']]=identity
for name,(size,h) in expected.items():
    p=A/name
    assert p.exists() and p.stat().st_size==size and sha(p)==h, name
inventory=[]
for p in sorted(A.iterdir()):
    assert p.stat().st_size<2**31, 'Asset exceeds hosting limit: '+p.name
    inventory.append(dict(name=p.name,size=p.stat().st_size,sha256=sha(p)))
(R/'distribution/asset-inventory.json').write_text(json.dumps(inventory,indent=2)+'\n')
(R/'distribution/SHA256SUMS.txt').write_text(''.join(x['sha256']+'  '+x['name']+'\n' for x in inventory))
print('Verified',len(expected),'referenced payload assets;',len(inventory),'total release assets;',sum(x['size'] for x in inventory),'bytes')
print('Launcher archive:',package)
