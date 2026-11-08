# Lumenveil 1.0.0 optional graphics package

This package is a copy of the graphics setup that works in `C:\WoWClient-Worgen-Test`, taken on 2026-11-08. The files are the installed bytes from that client. Nothing was rebuilt or downloaded from upstream. The renderer and shaders were not changed. The source client was only read.

## Build

```powershell
python -I .\packaging\lumenveil\build_package.py --out <new folder> `
  --wow-sha256 4c459de64d38b5795becc6c01a03ba00d34686598fae707c442446722617868a `
  --wow-sha256 3bd126a7d69a9177970cb0823f7e251f8b641f3625d599210ce6b2ae5ab6687c
```

- [`packaging/lumenveil/worgen-graphics.json`](packaging/lumenveil/worgen-graphics.json) lists every packaged file, the one adjustment, and each exclusion with its reason.
- The builder sorts the entries, fixes their timestamps, and uses fixed compression, so two builds from the same client produce the same bytes. It reads every source file again at the end and stops if one changed during the build.
- Outputs:
  - `Lumenveil-1.0.0.zip`
  - `package-manifest.json`: per file, the source and packaged SHA-256 and size
  - `SHA256SUMS.txt`
  - `feed-payload.unsigned.json`: the `lumenveil-feed` payload, ready for the approved signer

The `--wow-sha256` values become `compatibleWowSha256`:

| Fingerprint | Executable |
| --- | --- |
| `4c459de6…` | Required by the v1.2.0 game manifest |
| `3bd126a7…` | The Worgen test client |

Choose the published list at release time.

## Result

| | |
| --- | --- |
| Package | `Lumenveil-1.0.0.zip`, 8,005,677 bytes |
| SHA-256 | `542bc2f31e01accb444f6fa355741c4666532ce2b40438b5d0391e5de363afff` |
| Files | 46. 45 are byte-identical to the Worgen client. |
| Adjustment | `ReShade.ini`: removed `IntermediateCachePath=C:\Users\Coop\AppData\Local\Temp\ReShade`. ReShade then uses its default `%TEMP%\ReShade` cache. Source 562 bytes `19774ae9…`, packaged 498 bytes. |

Key components:

| File | SHA-256 | Role |
| --- | --- | --- |
| `CoAVolFog.dll` | `cfe74c824f3ef1866a349be2a06279d828fa7bf960c91c1910867be4414fe416` | The Lumenveil renderer that is live in the Worgen client |
| `version.dll` | `cc7e3045fca1ba0694d11d09e89723f58ff836eedebc09755414ab95a2fa925a` | Lumenveil loader 0.1.1 |
| `d3d9.dll` | `265888c31ca78dffa290c39cb7e50bfb02762590e41927906e46fb32f01497fa` | DXVK 3.1.1 x86 |
| `CoAVolFog.ini` | `3740a88469e2b2cd530cc8e77c8b1961a9429c29dc23416050997a8044979802` | The live settings, unchanged (`SunScatter=1.04`, `TransparentFog=1`) |

The full list is in `package-manifest.json`. These files are installed in the game folder, next to `Wow.exe`:

- `CoAVolFog.dll`, `CoAVolFog.ini`, `CoAVolFog-regions.ini`
- `version.dll`, `d3d9.dll`, `dxvk.conf`
- `ReShade.ini`, `ReShade-Lumenveil.ini`
- `lampdata.bin`, `liquiddata.bin`, `gameobjectlights.bin`
- `reshade-shaders/`: 26 effects, headers and textures
- `Lumenveil/`: 9 licence and notice texts

## Not packaged

| Item | Reason |
| --- | --- |
| `C:\ProgramData\ReShade\ReShade32.dll` | ReShade runs as a system-wide Vulkan layer that ReShade Setup registers. It does not run from the client folder. The player installs it with ReShade Setup; the launcher's ReShade Vulkan setup button explains how. |
| `dinput8.dll`, `lexara.dll`, `skia.dll` | The Lexara loader from St0ny's EXE patcher. It is not part of the graphics chain. |
| `Lumenveil/lamp_extract.exe`, `Lumenveil-Setup.cmd`, `Lumenveil-Uninstall.cmd` | The data generator and the manual setup scripts. The package ships the generated `.bin` files instead, and the launcher handles install and removal. |
| Logs | `CoAVolFog.log`, `ReShade.log`, `LuxVelum.log`, `wow_d3d9.log`, `Lumenveil/setup-*.log` |
| Draw-capture debug output | `CoAVolFog-draws/`, `CoAVolFog-draws.csv` |
| Personal saved spots | `CoAVolFog-spots/`, `CoAVolFog-spots.txt` |
| Disabled or backup copies | `*.disabled`, `*.pre-modern-disabled`, `*.bak` |
| Downloaded archives | `dxvk/`, `DXVK-WoW-x86.zip` |
| Game files | `Wow.exe`, EXE-patcher state, `Data/`, all `patch-*.mpq` including patch-z |
| Account and local data | `WTF/`, `Cache/`, `Logs/`, `Errors/`, `GameSessions/`, screenshots |

## Launcher behaviour

[`LumenveilInstaller.cs`](LumenveilInstaller.cs) works as follows:

- **Install**
  - It accepts only the declared file list.
  - It now allows the three Lumenveil data files at the game root.
  - If the player already has a file at a package path, it moves that file to `.coops-lumenveil\backup\<path>` and records the original hash. Before this change, it refused to install.
  - It still refuses folders, files the player edited after an install, and other wrappers outside the package (`dxgi.dll`, `opengl32.dll`, `ReShade32.dll`).
  - `dinput8.dll` no longer blocks installation, because it is the Lexara loader in the reference client.
- **Detection**: the receipt records the installed version. The launcher shows "Lumenveil 1.0.0 is installed".
- **Remove**
  - It deletes only package files that still match their installed hash.
  - It restores each backed-up original and removes the folders the install created.
  - When nothing is left to keep, it deletes `.coops-lumenveil`.
  - If the player edited an installed file, the launcher keeps that file and leaves the original in the backup folder, and says so.
- **Interrupted install or removal**: on the next launch, the pending transaction is rolled back.
  - Install: placed package files are removed and the moved originals are restored.
  - Removal: restored originals go back to the backup folder and the package files return.

The launcher still does not set up the system Vulkan layer.

## Validation

`dotnet run --project tests/graphics-package -- --output <new dir> --client <disposable client> --package <package dir>`

This drives the real staging launcher EXE through a signed loopback feed with an ephemeral key.

### Result on 2026-11-08: all 13 checks passed

The disposable client was a full copy of `C:\WoWClient`: 33.5 GB, 38,842 files, executable fingerprint `4c459de6…`. That client already had its own `ReShade.ini` and `d3d9.dll`, so the backup-and-restore path ran on real player files. The harness also planted a player-owned `dxvk.conf`.

1. All 46 ZIP entries match the manifest and the live Worgen files. The only difference is the documented `ReShade.ini` adjustment.
2. Download only: the launcher showed live progress and verified the package. Nothing in the client changed.
3. Install, round 1: all 46 installed files match the package and the Worgen source, and the originals were backed up with matching hashes. The launcher reports "Lumenveil 1.0.0 is installed".
4. Remove, round 1: the whole client tree is identical to before the install, including small-file hashes and large-file sizes and timestamps. The player files are back, and the created folders and `.coops-lumenveil` are gone.
5. Reinstall and remove, round 2: same result as round 1.
6. Interrupted install, simulated:
   - State before the next launch: two package files were already placed and an original was moved aside. One package file was still staged, and no receipt had been written.
   - On the next launch, the install rolled back to the exact pre-install tree.
7. Interrupted removal, simulated:
   - State before the next launch: a package file had been moved out and the player's original had already been restored.
   - On the next launch, the client returned to the exact installed tree. A later removal completed cleanly.
8. `Wow.exe` and `Data/patch-z.mpq` (`565d1b17…`) were unchanged.
9. Launcher self-update:
   - The signed 1.3.0 → 1.3.1 update replaced the EXE and restarted at the same path. `launcher_path.txt` was unchanged.
   - A 1.3.2 build that fails at startup rolled back to the previous EXE.

`CompatibilityTests` (24) and the WinForms progress tests also passed. After the run, all 46 source files and `Wow.exe` in `C:\WoWClient-Worgen-Test` still hash the same as at packaging.

Not tested: playing the game with the installed package. The renderer and Vulkan-layer behaviour are unchanged from the Worgen client and outside the scope of this package.
