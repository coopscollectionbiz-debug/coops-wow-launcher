# Launcher v1.1 compatibility investigation — 2026-11-06

## Result

Confirmed a narrow overvalidation bug, not evidence that all eight reported archives can be omitted. A targeted source/manifest hotfix allows three proven redundant/inactive archives to be absent. It does **not** make the player's seven other missing archives compatible. Do not tell this player that the hotfix alone fixes their installation or require unrelated downloads. Obtain their actual file inventory and hashes to qualify their client as a separate profile, or use their original supported HD package.

No server, existing WoW installation, or published release was changed. All test writes used temporary synthetic client folders. Source modifications do not constitute a release.

## Exact cause

`ReleaseUpdater.VerifyBaselineAsync` checks every `requiredBaseline` entry for existence, exact size and SHA-256. Previously every missing entry was fatal. `critical` only adds a note to the displayed filename; it never controlled whether a file is mandatory. Changing this to require only `critical=true` would remove checks on essential stock archives and is unsafe.

`ApplyAsync` rejects a failed baseline with `UNSUPPORTED_BASELINE` before downloads/installation, then scans conflicting archives. The manifest's client build/locale/description are metadata, not independent executable-version detection. Compatibility is an exact qualified HD baseline, not every client bearing version 3.3.5a. `Form1.cs` downloads the remote manifest from the fixed GitHub release asset URL; updating local JSON alone does not update players. No remote requests or release changes were made during this audit.

The local manifest has 46 baseline entries, generated as a broad inventory rather than a minimal dependency contract. Its `critical=true` prerequisites are Wow.exe, Data/patch-a.mpq (Goblin models), and Data/patch-s.mpq. Patch-z itself is an update payload, verified separately by SHA-256 before activation and after installation.

## The eight reported archives

Read-only StormLib enumeration, verified executable load-order evidence from the existing November 5 audit, direct current archive reads, Z DBC path/display-texture references, recursive M2 skin/animation/texture dependencies, and fallback comparisons were used. Filename letters alone were not treated as evidence.

| Archive | Findings | Decision |
|---|---|---|
| Data/enUS/patch-enUS-s.mpq | 11 payloads; **zero effective payloads**. Higher Data/patch-s (eight paths), Data/patch-x (one path) and verified patch-z (two paths) override every path. | Missing file is safe in the current qualified profile; now explicitly optional. |
| Data/patch-7.mpq | 16,073 effective payloads, 64 without fallback. Existing all-Z-model audit records 343 dependency edges to this archive, including SmoothReflect textures. | Retain requirement. A lower stock texture at the same path is not proof that the HD model renders correctly without this pack. |
| Data/patch-8.mpq | 1,446 effective payloads, 22 without fallback; existing all-Z-model audit records 20 edges here. | Retain requirement. |
| Data/patch-t.mpq | 3,000 effective payloads, 2,069 without fallback; existing all-Z-model audit records 58 edges here. | Retain requirement. |
| Data/patch-b.mpq | Blood-spurt models/skins/textures and two DBCs; 42 effective payloads, 24 without fallback. | Not proven essential to custom races, but not proven removable: removing DBC and effect assets changes the effective visual stack. Retain pending separate qualification. |
| Data/patch-i.mpq | Icon pack: 623 effective textures, nine without fallback. Current Z DBC reference scan reaches 533 changed textures with lower-stack fallbacks. | Candidate for future optional qualification; no evidence of missing current Z icon paths, but nine unique icons and other consumers were not fully qualified. Conservatively retain. |
| Data/patch-j.mpq | Character-creation UI models/textures; 154 effective payloads, eight without fallback. | Not a demonstrated direct Z M2 dependency; unique UI assets and custom creation UI prevent declaring it optional without further qualification. Retain. |
| Data/patch-u.mpq | Scourge/Taunka asset overlay; 826 effective payloads, none without fallback, 52 differ. Z DBC/model traversal reaches 824 paths here. | Candidate for future optional qualification. Matching path availability does not establish that fallback model/skin/animation sets match current DBCs. Retain. |

Also made `Data/enUS/base-enUS.mpq` and `Data/enUS/backup-enUS.mpq` explicitly optional: they are inactive backup archives absent from the supported executable's selected MPQ stack. Their presence is still integrity checked. This does not remove checks from active base/locale archives.

The counts above describe this installed release, not a universal MPQ naming standard. Existing all-Z-model edge counts include repeated dependencies; the new combined traversal deduplicates paths, so its counts differ. Neither static audit proves full runtime compatibility for an alternate client. Full evidence: `dependency-evidence.json`, and the adjacent `../Reforged-Compatibility-Audit/` loader/disassembly, inventory, model-dependency and DBC provenance reports.

## Original HD / test / Reforged comparison

`baseline-hashes.json` records complete SHA-256 reads of every baseline file in both HD copies. C:/WoWClient matches **46/46**, including all eight reported files. That intact officially identified baseline will not encounter this missing-file error merely from updating the launcher. This establishes the supplied local copy, not every historical distribution archive; no original download ZIP or player inventory was provided.

C:/WoWClient-Worgen-Test matches **45/46**: all MPQs match; its Wow.exe hash is `3bd126a7d69a9177970cb0823f7e251f8b641f3625d599210ce6b2ae5ab6687c`, whereas the qualified executable is `4c459de64d38b5795becc6c01a03ba00d34686598fae707c442446722617868a`. It will still fail executable validation. No new executable hash was allowed without qualification. All hashed files retained their size and modification time during hashing.

C:/WoWReforged has numerous absent or differently sized baseline files (see `client-inventory.json`). Existing compatibility investigation proves that the current Z carries old-HD creature mappings with missing/different Reforged assets, including Charger/Felhunter failures and Goblin animation/profile differences. Reforged must remain rejected by this profile. The current original/test Z is the 134,636,848-byte release payload with SHA-256 `565d1b17e676ea54f78b7a0898dbbda064ee27b0c629344b42578e9aea660dee` as established by prior complete archive audit; the manifest payload identity was preserved.

Players with deleted/repacked files, a different distribution revision, a modified executable, or another HD pack can see new rejection after upgrading because earlier launchers did not enforce this exact baseline. For the reported eight-file case, only the locale-S requirement is proven unnecessary; the seven-file remainder is not a demonstrated false rejection of a qualified client.

## Implemented change and integrity preservation

Added `BaselineFile.Optional`, default false, independent of `Critical`. Only an explicitly optional **missing** file skips the missing-file error. Present optional files retain exact size/SHA-256 verification and existing caching behavior. Optional archives remain recognized by the archive scanner. Three manifest entries are qualified as optional with evidence notes. No hashes, payload identities, obsolete rules, transactional operations, recovery routines or archive-conflict rules were changed. Main project excludes the new test sources from the Windows app compilation.

## Validation

`dotnet run --project tests/CompatibilityTests.csproj`: **10 tests passed** under .NET 8. Synthetic fixtures use the real manifest's complete path/optionality policy and small deterministic payloads; they do not pretend to be real MPQ gameplay tests.

Coverage: complete HD layout verifies/installs; all qualified optional MPQs absent; every remaining mandatory baseline entry absent individually; legacy default mandatory behavior and critical=false retention; present optional corruption with unchanged size and size mismatch; incompatible executable content; higher-priority and unknown locale archive rejection; corrupted payload SHA rejection preserving old patch; transaction rollback after replacement; process-termination recovery restoring original bytes.

`dotnet build CoopsWoWLauncher.csproj --configuration Release`: **passed, zero errors**. NuGet vulnerability-feed lookup emitted NU1900 warnings because its endpoint was unavailable; restore/build and tests succeeded. No new package dependency was introduced.

## Permanent mechanism and release decision

Keep exact hashes for the qualified executable, essential stock baseline, required HD provider archives, and every patch payload. Explicitly record evidence-backed optional archives rather than interpreting every noncritical archive as optional. Extend support with separately qualified **whole client profiles**, each with its own prerequisite hashes and release payload; a version string alone cannot detect incompatible DBC/model bundles. Do not accept arbitrary same-version executables, substitute Reforged hashes, or require unrelated MPQs to make an inventory check green.

A narrow launcher **and manifest** hotfix is warranted for proven redundant-file false rejection. Old v1.1 ignores the new optional metadata and will still reject absent files, so a manifest-only release is insufficient. At authorized release time, assign a new launcher version, update minimumLauncherVersion coherently, and publish the tested binary/manifest together. Nothing has been published or versioned for release here. This hotfix is **not** sufficient to promise support for the reported player's seven other missing packs; a separate inventory/dependency qualification is needed before relaxing those constraints.
