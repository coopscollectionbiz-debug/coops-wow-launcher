# Launcher 1.3 candidate

This branch starts at `release/v1.2.0` (`fe0f6d0`). It includes the patch verification and recovery from `c950ffa`. The required game manifest and asset URLs still point to the existing `v1.2.0` release.

## Build the Windows x64 portable EXE

Run from the repository in PowerShell with a .NET SDK and the .NET 8 Windows x64 runtime packs.

```powershell
dotnet publish .\CoopsWoWLauncher.csproj -c Release -r win-x64 --self-contained true -o .\bin\portable -p:Version=1.3.0 -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=None
Get-FileHash .\bin\portable\CoopsWoWLauncher.exe -Algorithm SHA256
```

For a release bootstrap, pass `"-p:LauncherFeedKey=$launcherPublicKey"`. The value is the Base64 DER SubjectPublicKeyInfo of the approved ECDSA P-256 signing key. Embed the approved Lumenveil key with `"-p:LumenveilFeedKey=$graphicsPublicKey"` when it is available. Private keys stay in the signing environment. A build with no key for a feed displays that feed as unavailable. A key added or replaced after distribution requires a bootstrap that trusts it.

## Release assets

| Channel | Stable metadata | Versioned payload |
| --- | --- | --- |
| Launcher | `launcher-feed/metadata.json` | `launcher-v1.3.1/CoopLauncher-1.3.1.exe` |
| Required game assets | Existing `v1.2.0/manifest.json` | Existing `v1.2.0` assets |
| Optional Lumenveil | `lumenveil-feed/metadata.json` | `lumenveil-v<version>/Lumenveil-<version>.zip` |

Metadata contains Base64 `payload` and `signature` fields. Sign the exact UTF-8 payload bytes using ECDSA P-256, SHA-256, and IEEE P1363 fixed-field signature format. The signed object contains `channel`, `version` (three numeric components), `filename`, `url`, `size` in bytes, and `sha256`. For Lumenveil it also contains `installationEnabled`, `supportedBuilds`, `compatibleWowSha256`, `prerequisites`, and the exact relative archive `files` list. Asset URLs use the GitHub repository and versioned tag shown above. Never overwrite a versioned payload.

1. Provision approved signing keys and build the release bootstrap with the launcher public key embedded. Distribute that exact EXE with its SHA-256 as the one-time manual download.
2. Build a higher-version portable EXE with the same launcher trust key. In staging, sign metadata against the exact candidate bytes. Test activation from the bootstrap, restart at the original path, rollback, and preservation of `launcher_path.txt`. Rebuild release binaries without `StagingFeeds`, which is reserved for loopback tests.
3. When approved for release, upload the immutable launcher payload first, then replace the signed metadata asset on `launcher-feed`. Leave the game release and manifest in place.
4. Enable Lumenveil separately after choosing a distributable build, verifying the precise DLLs and their load chain, qualifying all supported executable fingerprints, and clearing source, licence, texture, shader, and preset obligations. Validate the effective preset and visuals in game, including Sun Scatter off. Only then upload the versioned ZIP and publish signed Lumenveil metadata. The launcher installs only client-local files. ReShade ships in the package as a client-local Vulkan layer that Launch WoW enables for that process; no system Vulkan layer is set up (see LUMENVEIL-PACKAGE.md).

The optional package is built from the working Worgen client. See [LUMENVEIL-PACKAGE.md](LUMENVEIL-PACKAGE.md) for the build command, file list, exclusions and installer tests. See the [Lumenveil release qualification](LUMENVEIL-RELEASE-QUALIFICATION.md) for the selected renderer, private QA preset and hashes, installer results, and remaining redistribution and gameplay gates.

## Test commands

```powershell
dotnet run --project .\tests\CompatibilityTests.csproj
dotnet run --project .\tests\winforms\ProgressControlTests.csproj
pwsh -File .\tests\staging\Run-StagingSmoke.ps1 -OutputRoot C:\path\to\new-staging-output -DisposableClient C:\path\to\disposable-wow-copy
```

The staging script uses Python 3, an ephemeral loopback HTTP port, and an in-memory signing key. It builds a bootstrap, a healthy candidate and a startup-failure candidate, then invokes the same handlers as the WinForms buttons. The optional client argument enables fixture installation/removal checks. Supply a disposable copy of the WoW client with WoW closed. The synthetic ZIP exercises the transport and installer only. It does not exercise the real renderer, Vulkan layer, or redistribution prerequisites.
