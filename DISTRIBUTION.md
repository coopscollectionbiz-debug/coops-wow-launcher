# Required HD patch distribution — v1.2.0 candidate

The release contains the tested launcher and verified HD patch assets. Original WoW client installations and the server were not modified during preparation.

## What players receive

The launcher manages all **26 mandatory custom HD MPQ destinations**, plus the essential release patch-z and the five existing addons. The HD prerequisites total 10,517,151,493 bytes before deduplication. Matching already installed archives are not downloaded. Duplicate locale/global copies with identical SHA-256 share one hosted payload.

Missing required HD archives are downloadable dependencies, not instructions for players to find MPQs elsewhere. The three evidence-qualified optional archives remain optional and are not distributed. Stock/base archives and Wow.exe remain compatibility prerequisites; this is not a complete WoW download or an automatic converter for other HD packs/Reforged.

## Safeguards

* Each provisioned archive has both its existing requiredBaseline record and a matching files entry with suppliesBaseline=true. Exact path, size and SHA-256 must agree. Only custom Data/patch-[5-9a-z] and enUS lettered patch archives can use this mechanism.
* Missing qualified archives may proceed to staging. Existing wrong-size/wrong-hash archives still fail preflight without replacement; executable, base, locale, optional-present integrity, and conflicting-archive safeguards remain enforced.
* All required payloads stage and verify before client files change. Missing archives join the same recoverable journal/transaction as patch-z. Failed installation or interrupted-process recovery removes newly installed archives and restores prior patch bytes.
* Identical global/locale payloads share downloads but get independent activation files, so moving one into place cannot consume another destination's staged bytes.
* Files exceeding GitHub's per-asset limit are split into ordered parts of at most 1,000,000,000 bytes. Each part uses the existing resumable SHA-256 downloader; the assembled complete MPQ must independently match its original full SHA-256 before activation. Partial downloads remain available for retry. Three oversized MPQs (patch-6, patch-a, patch-f) each use three parts.

GitHub requires each release asset to be under 2 GiB: https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases

## Assets and source

`distribution/assets/` contains the verified MPQ payloads/parts, exact patch-z, existing addon ZIPs and manifest. `distribution/asset-inventory.json` records prepared payload sizes and SHA-256. Asset names use full content hashes to prevent collisions across differently named destinations and release updates. The launcher package is generated separately and added to the final inventory.

All HD sources were read from C:/WoWClient and checked against the existing baseline hashes. Patch-z was deliberately sourced from the verified 134,636,848-byte client copy: the unrelated source-tree patch-z.mpq is a different size and was not used. Addon packages were copied from the launcher addon-packages directory and verified against the existing manifest. No existing hashes or patch-z content were changed.

`prepare_distribution.py` reproduces the assets locally. It never publishes. The release source and manifest are applied to C:/coops-wow-launcher after verification. Large release assets remain in this staging directory and are not embedded in the EXE or source repository.

## Version and publication

Launcher version and minimumLauncherVersion are **1.2.0**. ReleaseId is `release1-hd-prerequisites-20261106.1`. Launcher manifest and payload URLs are pinned together to the **v1.2.0** release. The launcher and all assets are released together.

To reproduce a release, upload the complete contents of `distribution/assets/`, including the launcher package, to the repository's v1.2.0 release and publish them together. Upload part files exactly as named; the assembled oversized sourceFile is metadata and must not be uploaded separately. Do not replace the v0.2.0 manifest: older launchers cannot provision missing baseline patches. Existing v1.1 users need the new launcher binary; there is no newly added self-updater.

## Validation

Regression suite: `dotnet run --project tests/CompatibilityTests.csproj`.

**22 regression tests passed.** The initial real-payload run exposed shared-content staging being consumed by the first destination; the transaction rolled back. The fix adds independent activation copies and a regression case before repeating the complete real-payload test.

**The corrected real-payload integration passed:** all 27 MPQs installed and verified, using 31 local source requests (shared payloads were reused); a repeat check required zero requests. All 36 referenced payload assets were independently hash-verified. The prepared release consists of 38 assets including the manifest and self-contained launcher ZIP. The Windows x64 single-file publish succeeded. No gameplay test or online release download was performed.

Real payload integration: `dotnet run --project tests/CompatibilityTests.csproj -- --assets <absolute-path-to-distribution/assets>`.

The real payload integration installs all 27 MPQs through the real downloader and transaction code into an isolated synthetic base-client fixture. It verifies the HD prerequisite hashes, multipart assembly, patch-z identity, and that a repeat check requires no downloads. It does not launch WoW or establish support for another client profile. All file writes stay in generated temporary fixtures.

NuGet may emit NU1900 when its vulnerability-feed endpoint is unavailable. No third-party package dependency was added.

## Rebuild locally

1. Run `python prepare_distribution.py` to verify/reproduce the payload assets.
2. Run the regression and real-payload integration commands above.
3. Run `dotnet publish CoopsWoWLauncher.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --output distribution/standalone`.
4. Run `python finalize_distribution.py` to build the launcher ZIP with player extraction/use instructions, verify all referenced payloads, enforce per-asset size limits, and write the final SHA-256 inventory. This does not upload anything.
