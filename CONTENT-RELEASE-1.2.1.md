# Content release 1.2.1 (2026-11-09)

The live game manifest (`releases/download/v1.2.0/manifest.json`) moves from
Release 1.2 (`release1.2-20261109.1`, sha256 23a04f79…) to `release1.2.1-20261109.1`.
No launcher binary change: launchers 1.2.0 and 1.3.0 both read this manifest.

Only two addon entries change. Each has a new, uniquely named asset on the v1.2.0 release:

| Addon | Asset | Size | SHA-256 | Source |
|---|---|---|---|---|
| MultiBot | MultiBot-chatless-020210b.zip | 4,910,891 | 559cd4fb…0887f | Wishmaster117/MultiBot-Chatless 020210b (2026-10-28) |
| DungeonClear | DungeonClear-3.6-088e6e0.zip | 37,281 | 4d2d4ed5…25f2b | jrad7/mod-dungeon-clear-addon 088e6e0 (v3.6) |

CleanBot, Transmog and TransmogCollection are unchanged.

Server dependency: the new MultiBot negotiates capabilities with `mod-multibot-bridge`.
The manifest was published only after production ran bridge 9f0e8ba (server Release 1.2.1,
`/opt/azerothcore/releases/release1.2.1-20261109.1`). DungeonClear 3.6 sends the same
addon messages as 3.5 and needs no server change.

Packages are built with `tools/addon-packaging/make_addon_zip.py` (deterministic, `<Addon>/`
at the archive root) and the manifest with `make_manifest.py`. Staging, harness logs and
VPS scripts: `C:\WHE-Staging\Worgen-Audit\Release-1.2.1`.
