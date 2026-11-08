# Lumenveil 1.1.0 optional graphics package

This package reproduces the graphics setup that works in `C:\WoWClient-Worgen-Test`: Lumenveil, DXVK and ReShade. It does not require ReShade Setup or any system-wide change.

- The files are the installed bytes from that client, taken on 2026-11-08.
- ReShade 6.8.0.2157 is the exact `ReShade32.dll` that the Worgen client loads from `C:\ProgramData\ReShade`.
- Nothing was rebuilt, upgraded or downloaded from upstream.
- The source client was only read.

## How the Worgen client loads each component

| Component | How it loads | Same in the package |
| --- | --- | --- |
| Lumenveil | `Wow.exe` imports `VERSION.dll`. The client-folder `version.dll` (the Lumenveil loader) loads `CoAVolFog.dll`, which reads `CoAVolFog*.ini` and the three `.bin` data files. | Yes |
| DXVK 3.1.1 | The client-folder `d3d9.dll` takes over D3D9 and renders through Vulkan. It reads `dxvk.conf`. | Yes |
| ReShade 6.8.0.2157 | ReShade Setup registered `C:\ProgramData\ReShade\ReShade32.json` as an implicit Vulkan layer in HKLM. It turns on only for programs listed in `C:\ProgramData\ReShade\ReShadeApps.ini`. It hooks the Vulkan instance and swapchain that DXVK creates, and reads `ReShade.ini` and the preset from the game folder. | Same DLL, but loaded as a client-local explicit layer (see below) |

### ReShade without ReShade Setup

- **Files.** The package installs `ReShade\ReShade32.dll` (an exact copy) and `ReShade\ReShade32.json` in the game folder. The manifest is the system one, with the layer renamed to `VK_LAYER_coops_reshade` so it can never collide with a system-wide ReShade.
- **No system changes.** Nothing is written to the registry, `ProgramData` or `ReShadeApps.ini`.
- **How it turns on.** When the package is installed and both layer files still match their installed hashes, **Launch WoW** starts `Wow.exe` with these variables, for that process only:
  - `VK_LAYER_PATH` and `VK_ADD_LAYER_PATH` set to `<client>\ReShade`
  - `VK_INSTANCE_LAYERS=VK_LAYER_coops_reshade`, added in front of any existing value
  - `DISABLE_VK_LAYER_reshade_1=1`, so a system-wide ReShade cannot inject a second copy
- **Without the package.** WoW starts exactly as before.
- **Limit.** ReShade is active only when WoW is started from the launcher. Lumenveil and DXVK load however WoW is started.
- **Vulkan check.** Before installing, the launcher checks that the 32-bit Vulkan runtime (`SysWOW64\vulkan-1.dll`) exists. If it is missing, the launcher says to update the graphics driver.

## Supported executables

Both executables were run with the installed package from the launcher. Both loaded the whole chain: Lumenveil with its data files, DXVK, and the client-local ReShade.

| SHA-256 | What it is |
| --- | --- |
| `4c459de6…` | Stock 3.3.5a build 12340, 7,704,216 bytes. This is the executable the v1.2.0 game manifest expects. |
| `3bd126a7…` | The same build with St0ny's AIO EXE patches, 7,707,648 bytes. |

The two builds share:

- the timestamp `0x4C2452FE` and image base `0x400000`;
- an identical import table, including `VERSION.dll` and `DINPUT8.dll`;
- identical `.text` and `.rdata` layout.

The patched build differs in about 1.2 KB of code patches, three added sections (`.glyph`, `.isnap`, `.lght`), and loader stubs for `Lexara.dll` and `LuxVelum.dll`. When the Lexara files are present, `Lexara.dll`, `skia.dll` and `DINPUT8.dll` load alongside the graphics chain.

## Build

```powershell
python -I .\packaging\lumenveil\build_package.py --out <new folder> `
  --wow-sha256 4c459de64d38b5795becc6c01a03ba00d34686598fae707c442446722617868a `
  --wow-sha256 3bd126a7d69a9177970cb0823f7e251f8b641f3625d599210ce6b2ae5ab6687c
```

- [`packaging/lumenveil/worgen-graphics.json`](packaging/lumenveil/worgen-graphics.json) lists each file with its source and role, the two documented adjustments, and each exclusion with its reason.
- The ZIP is deterministic: sorted entries, fixed timestamps and fixed compression. Two builds from the same sources produce identical bytes.
- The builder reads every source again at the end and stops if one changed during the build.
- Outputs:
  - `Lumenveil-1.1.0.zip`
  - `package-manifest.json` (source and packaged SHA-256 for each file)
  - `SHA256SUMS.txt`
  - `feed-payload.unsigned.json`

## Result

| | |
| --- | --- |
| Package | `Lumenveil-1.1.0.zip`, 9,822,056 bytes |
| SHA-256 | `dd78935936a16dceac76ddf48aae640d112d8f07104803e011b948e462f7a61a` |
| Files | 48. 46 are byte-identical to their source. |
| Adjustment 1 | `ReShade.ini`: removed `IntermediateCachePath=C:\Users\Coop\AppData\Local\Temp\ReShade`, so ReShade uses its default `%TEMP%\ReShade`. |
| Adjustment 2 | `ReShade/ReShade32.json`: the layer name and its disable variable are renamed. The layer is otherwise the system manifest. |

| File | SHA-256 | Role |
| --- | --- | --- |
| `CoAVolFog.dll` | `cfe74c824f3ef1866a349be2a06279d828fa7bf960c91c1910867be4414fe416` | Live Lumenveil renderer |
| `version.dll` | `cc7e3045fca1ba0694d11d09e89723f58ff836eedebc09755414ab95a2fa925a` | Lumenveil loader 0.1.1 |
| `d3d9.dll` | `265888c31ca78dffa290c39cb7e50bfb02762590e41927906e46fb32f01497fa` | DXVK 3.1.1 x86 |
| `ReShade/ReShade32.dll` | `538998f66c0197adcdeadfe8e5ea19dd6ca3253e40399476d291f385ce518b88` | ReShade 6.8.0.2157, 32-bit |
| `ReShade/ReShade32.json` | `8ea0e1630a1d9d7412748dbac53a4492e2753eb598f8ca16f008c23fbf468386` | Client-local layer manifest |
| `CoAVolFog.ini` | `3740a88469e2b2cd530cc8e77c8b1961a9429c29dc23416050997a8044979802` | Live settings, unchanged |

The other files:

- **Game folder:** `CoAVolFog-regions.ini`, `dxvk.conf`, `ReShade.ini`, `ReShade-Lumenveil.ini`, `lampdata.bin`, `liquiddata.bin`, `gameobjectlights.bin`
- **`reshade-shaders/`:** 26 effects, headers and textures
- **`Lumenveil/`:** 9 licence and notice texts

## Not packaged

| Item | Reason |
| --- | --- |
| `ReShade64.dll`, `ReShadeApps.ini`, the HKLM layer registration | System-wide ReShade install, not needed. WoW is 32-bit, and the package uses a client-local layer instead. |
| `dinput8.dll`, `lexara.dll`, `skia.dll` | Lexara loader from St0ny's EXE patcher. It is not part of the graphics chain. The installer leaves it in place, and it coexists with the package. |
| `Lumenveil/lamp_extract.exe`, `Lumenveil-Setup.cmd`, `Lumenveil-Uninstall.cmd` | Data generator and manual scripts. The package ships the generated `.bin` files, and the launcher installs and removes them. |
| Logs, draw captures, saved spots, disabled and backup copies, downloaded archives | Local or debug files |
| `Wow.exe`, EXE-patcher state, `Data/`, all `patch-*.mpq` including patch-z | Game files are managed by the game manifest and are never touched. |
| `WTF/`, `Cache/`, `Logs/`, `Errors/`, screenshots | Account and local data |

## Launcher behaviour

[`LumenveilInstaller.cs`](LumenveilInstaller.cs) works as follows:

- **Install**
  - It accepts only the declared file list.
  - If the player already has a file at a package path, it moves that file to `.coops-lumenveil\backup\<path>` and records the hash.
  - It still refuses folders, files the player edited after an install, and other graphics wrappers outside the package (`dxgi.dll`, `opengl32.dll`, a root `ReShade32.dll`).
- **WoW running:** only a WoW running from the selected folder blocks install or removal. A process whose path cannot be read also blocks.
- **Detection:** the launcher shows "Lumenveil 1.1.0 is installed".
- **Launch:** when the installed layer files verify, **Launch WoW** adds the ReShade layer environment (see above).
- **Remove**
  - It deletes only package files that still match their installed hash.
  - It restores the backed-up originals and removes the folders the install created, including `ReShade\` and `.coops-lumenveil`.
  - If the player edited an installed file, the launcher keeps it and says so.
- **Interrupted install or removal:** the pending transaction is rolled back on the next launch.
- **Removed UI:** the manual "ReShade Vulkan setup" help button is gone.

## Validation (2026-11-08)

`dotnet run --project tests/graphics-package -- --output <new dir> --client <disposable client> --package <package dir> [--no-plant] [--launch] [--skip-self-update]`

The harness drives the real staging launcher EXE through a signed loopback feed with an ephemeral key. All three disposable clients passed every check.

| Client | Executable | Starting state | Result |
| --- | --- | --- | --- |
| `disposable-CleanClient` | `4c459de6` stock | No ReShade and no DXVK | 17/17, including self-update and rollback |
| `disposable-WoWClient` | `4c459de6` stock | The player's own `ReShade.ini`, `ReShadePreset.ini`, DXVK `d3d9.dll`, a large `reshade-shaders/` set and a ReShade add-on, plus a planted `dxvk.conf` | 15/15 |
| `disposable-CleanClient` | `3bd126a7` St0ny-patched, plus Lexara files | No ReShade and no DXVK | 15/15 |

Every run checked:

- All 48 packaged files match the manifest and the live sources.
- Download only: live progress, and nothing in the client changed.
- Install, twice: all 48 files match, and the originals were backed up. The launcher reports 1.1.0 as installed.
- Remove, twice: the whole client tree is identical to before the install.
- Interrupted install (simulated): rolled back to the exact pre-install tree.
- Interrupted removal (simulated): returned to the exact installed tree, and a later removal completed cleanly.
- **Real game launch**
  - The launcher's Launch path started WoW, which ran to the login screen.
  - In the process module list, `VERSION.dll`, `CoAVolFog.dll`, `d3d9.dll` and `ReShade\ReShade32.dll` were all loaded from the client folder.
  - `ReShade.log` showed ReShade 6.8.0.2157 "loaded from '<client>\ReShade\ReShade32.dll'". It hooked `vkCreateInstance` and `vkCreateSwapchainKHR` and compiled the five preset techniques (LAUNCHPAD, SMAA, FakeHDR, Curves, SHARPEN).
  - No system ReShade was loaded, and ReShade initialised exactly once.
  - `CoAVolFog.log` showed the renderer loaded from the client, with 82,373 lamps, 10,099 windows and 4 maps of liquid data.
  - The DXVK log was written by the client-folder `d3d9.dll`.
- **After the game exited**
  - The only changes were the game's own logs and config.
  - Removal restored every package path and player file.
- `Wow.exe` and `Data/patch-z.mpq` (`565d1b17…`) were unchanged.
- **Self-update** (clean run): the signed 1.3.0 → 1.3.1 update restarted at the same path and kept `launcher_path.txt`. A 1.3.2 build that fails at startup rolled back.

`CompatibilityTests` (24) and the WinForms progress tests also passed. Removal also works with the feed offline.

### Notes

- **Player's extra effects.** On the client with its own large shader set, ReShade compiles all effects on its search path: 323 there. Two of that player's own iMMERSE effects (FILMGRAIN, SOLARIS) fail to compile against the packaged iMMERSE headers. Neither is in the preset, and they come back untouched on removal.
- **System ReShade on the same path.** Not directly exercised: no disposable client path is listed in `ReShadeApps.ini`. `DISABLE_VK_LAYER_reshade_1=1` covers that case, so a system ReShade registered for the same `Wow.exe` path does not inject a second copy.
- **Signing keys.** The release build `launcher-1.3.0` embeds no feed keys, so it shows the optional graphics feed as unavailable until a keyed build is made. The tests used staging builds with ephemeral keys.
