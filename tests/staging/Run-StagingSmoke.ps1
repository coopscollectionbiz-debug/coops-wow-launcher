#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$OutputRoot,
    [string]$DisposableClient
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '../../CoopsWoWLauncher.csproj'
$root = (New-Item -ItemType Directory -Path $OutputRoot).FullName
$feed = (New-Item -ItemType Directory -Path "$root/feed").FullName
$emptyClient = (New-Item -ItemType Directory -Path "$root/no game selected").FullName
$ecdsa = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
$key = [Convert]::ToBase64String($ecdsa.ExportSubjectPublicKeyInfo())
$runs = [Collections.Generic.List[string]]::new()

function Assert([bool]$Condition, [string]$What) {
    if (!$Condition) { throw $What }
}

function Wait-Until([scriptblock]$Predicate, [string]$What) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($clock.Elapsed.TotalSeconds -lt 65) {
        if (& $Predicate) { return }
        Start-Sleep -Milliseconds 250
    }
    throw "Timed out waiting for $What"
}

function Get-Launcher([string]$Exe) {
    Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($Exe)) -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $Exe }
}

function Build([string]$Version, [string]$Destination, [switch]$FailStartup) {
    $fail = $FailStartup.IsPresent.ToString().ToLowerInvariant()
    $options = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $Destination,
        '-p:PublishSingleFile=true', '-p:EnableCompressionInSingleFile=true', '-p:DebugType=None',
        "-p:Version=$Version", '-p:StagingFeeds=true', "-p:FailStartup=$fail",
        "-p:LauncherFeedKey=$key", "-p:LumenveilFeedKey=$key",
        "-p:LauncherFeedUrl=$base/launcher.json", "-p:LumenveilFeedUrl=$base/lumenveil.json", '-p:NuGetAudit=false')
    & dotnet publish $project @options | Out-Host
    Assert ($LASTEXITCODE -eq 0) "Publish failed for $Version"
    $published = @(Get-ChildItem -LiteralPath $Destination -File)
    Assert ($published.Count -eq 1 -and $published[0].Name -eq 'CoopsWoWLauncher.exe') "Publish did not produce a standalone EXE for $Version"
}

function Write-Feed([string]$Channel, [string]$Version, [string]$Asset, [hashtable]$Extra = @{}, [switch]$BadSignature) {
    $filename = [IO.Path]::GetFileName($Asset)
    $metadata = @{
        channel = $Channel; version = $Version; filename = $filename; url = "$base/$filename"
        size = (Get-Item -LiteralPath $Asset).Length; sha256 = (Get-FileHash -LiteralPath $Asset).Hash
    }
    foreach ($field in $Extra.GetEnumerator()) { $metadata[$field.Key] = $field.Value }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($metadata | ConvertTo-Json -Compress -Depth 8))
    $signature = $ecdsa.SignData($bytes, [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
    if ($BadSignature) { $signature[0] = $signature[0] -bxor 1 }
    @{
        payload = [Convert]::ToBase64String($bytes); signature = [Convert]::ToBase64String($signature)
    } | ConvertTo-Json -Compress | Set-Content -LiteralPath "$feed/$Channel.json" -Encoding utf8NoBOM
}

function New-Run([string]$Name, [string]$Client = $emptyClient) {
    $folder = (New-Item -ItemType Directory -Path "$root/$Name").FullName
    $exe = Join-Path $folder 'Coop Launcher.exe'
    Copy-Item -LiteralPath "$root/bootstrap/CoopsWoWLauncher.exe" -Destination $exe
    $runs.Add($exe)
    $settings = Join-Path $folder 'launcher_path.txt'
    [IO.File]::WriteAllText($settings, $Client + [Environment]::NewLine)
    [pscustomobject]@{ Folder = $folder; Exe = $exe; Settings = $settings
        SettingsHash = (Get-FileHash -LiteralPath $settings).Hash
        Log = (Join-Path $folder 'staging-progress.log')
        State = (Join-Path $folder '.coops-launcher-update') }
}

function Start-Smoke($Run, [string]$Mode) {
    $proc = Start-Process -FilePath $Run.Exe -ArgumentList '--staging-smoke', $Mode -WindowStyle Hidden -PassThru
    Assert ($proc.WaitForExit(90000)) "Launcher smoke process did not exit ($Mode)"
    Assert ($proc.ExitCode -eq 0) "Launcher smoke process returned $($proc.ExitCode) ($Mode)"
}

function Check-Settings($Run) {
    Assert ((Get-FileHash -LiteralPath $Run.Settings).Hash -eq $Run.SettingsHash) 'Launcher settings changed'
}

function Check-Progress([string]$LogPath, [string[]]$Phases) {
    $lines = @(Get-Content -LiteralPath $LogPath)
    $ordered = $lines -join [Environment]::NewLine
    $last = -1
    foreach ($phase in $Phases) {
        $index = $ordered.IndexOf($phase, $last + 1, [StringComparison]::Ordinal)
        Assert ($index -gt $last) "Missing or out-of-order phase '$phase' in $LogPath"
        $last = $index
    }
    Assert (@($lines | Where-Object { $_ -match '/ .*\([0-9]+%\).* [1-9][0-9.,]* [KMG]?i?B/s' }).Count -gt 0) "No live byte/size/rate sample in $LogPath"
}

$server = $null
try {
    $portFile = Join-Path $root 'port.txt'
    $arguments = '"{0}" "{1}" "{2}"' -f "$PSScriptRoot/feed_server.py", $feed, $portFile
    $server = Start-Process python -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardError "$root/http.log"
    Wait-Until { Test-Path -LiteralPath $portFile } 'loopback feed'
    $base = 'http://127.0.0.1:' + (Get-Content -LiteralPath $portFile -Raw)

    Build '1.3.0' "$root/bootstrap"
    Build '1.3.1' "$root/good"
    Build '1.3.2' "$root/broken" -FailStartup
    $good = Join-Path $feed 'CoopLauncher-1.3.1.exe'
    $broken = Join-Path $feed 'CoopLauncher-1.3.2.exe'
    Copy-Item "$root/good/CoopsWoWLauncher.exe" $good
    Copy-Item "$root/broken/CoopsWoWLauncher.exe" $broken

    Write-Feed 'launcher' '1.3.1' $good
    $normal = New-Run 'successful update'
    Start-Smoke $normal 'update'
    Wait-Until { (Test-Path "$($normal.State)/staging-health.log") -and
        !(Test-Path "$($normal.State)/pending.json") -and (Get-Launcher $normal.Exe) } 'healthy candidate restart'
    Assert ((Get-FileHash $normal.Exe).Hash -eq (Get-FileHash $good).Hash) 'Candidate did not replace the bootstrap'
    Assert ((Get-Content "$($normal.State)/staging-health.log" -Raw).Trim() -eq '1.3.1') 'Unexpected checkpoint version'
    Assert (!(Test-Path "$($normal.State)/previous.exe")) 'Healthy update retained rollback EXE'
    Check-Settings $normal
    Check-Progress $normal.Log @('Downloading', 'Verifying', 'Ready to restart', 'Restarting')
    $duplicate = Start-Process -FilePath $normal.Exe -WindowStyle Hidden -PassThru
    Assert ($duplicate.WaitForExit(5000)) 'Second instance did not exit'
    Assert (@(Get-Launcher $normal.Exe).Count -eq 1) 'Two launcher instances stayed open'
    Get-Launcher $normal.Exe | Stop-Process
    Write-Output 'PASS signed bootstrap update, restart, startup checkpoint, settings, and single-instance guard'

    Write-Feed 'launcher' '1.3.2' $broken
    $rollback = New-Run 'startup rollback'
    Start-Smoke $rollback 'update'
    Wait-Until { (Test-Path "$($rollback.State)/staging-recovery.log") -and
        !(Test-Path "$($rollback.State)/pending.json") -and (Get-Launcher $rollback.Exe) } 'prior launcher restart after failed startup'
    Assert ((Get-FileHash $rollback.Exe).Hash -eq (Get-FileHash "$root/bootstrap/CoopsWoWLauncher.exe").Hash) 'Bootstrap was not restored'
    Assert (!(Test-Path "$($rollback.State)/staging-health.log")) 'Broken build reached the checkpoint'
    Check-Settings $rollback
    Get-Launcher $rollback.Exe | Stop-Process
    Write-Output 'PASS failed candidate startup, previous EXE rollback and restart, and settings'

    $interrupted = New-Run 'interrupted handoff'
    New-Item -ItemType Directory -Path $interrupted.State | Out-Null
    Copy-Item -LiteralPath $interrupted.Exe -Destination "$($interrupted.State)/previous.exe"
    Copy-Item -LiteralPath $good -Destination $interrupted.Exe -Force
    @{
        executable = $interrupted.Exe; version = '1.3.1'; token = [guid]::NewGuid().ToString('N')
        sha256 = (Get-FileHash $good).Hash; size = (Get-Item $good).Length
    } | ConvertTo-Json -Compress | Set-Content -LiteralPath "$($interrupted.State)/pending.json" -Encoding utf8NoBOM
    $interruptedRun = Start-Process -FilePath $interrupted.Exe -WindowStyle Hidden -PassThru
    Assert ($interruptedRun.WaitForExit(10000)) 'Interrupted launcher did not yield to recovery'
    Wait-Until { (Test-Path "$($interrupted.State)/staging-recovery.log") -and
        !(Test-Path "$($interrupted.State)/pending.json") -and (Get-Launcher $interrupted.Exe) } 'interrupted handoff recovery'
    Assert ((Get-FileHash $interrupted.Exe).Hash -eq (Get-FileHash "$root/bootstrap/CoopsWoWLauncher.exe").Hash) 'Interrupted update did not restore bootstrap'
    Check-Settings $interrupted
    Get-Launcher $interrupted.Exe | Stop-Process
    Write-Output 'PASS interrupted replacement recovers on next startup'

    Write-Feed 'launcher' '1.3.1' $good -BadSignature
    $bad = New-Run 'invalid signature'
    Start-Smoke $bad 'update'
    Assert ((Get-Content $bad.Log -Raw) -match 'signature is invalid') 'Invalid feed signature was not rejected'
    Assert (!(Test-Path $bad.State)) 'Invalid signature reached update staging'
    Write-Output 'PASS invalid signature rejected before download'

    Write-Feed 'launcher' '1.3.1' $good @{ sha256 = '0' * 64 }
    $badHash = New-Run 'invalid EXE hash'
    Start-Smoke $badHash 'update'
    Assert ((Get-Content $badHash.Log -Raw) -match 'failed SHA-256 verification') 'Invalid EXE hash was not rejected'
    Assert ((Get-FileHash $badHash.Exe).Hash -eq (Get-FileHash "$root/bootstrap/CoopsWoWLauncher.exe").Hash) 'Invalid EXE replaced bootstrap'

    Write-Feed 'launcher' '1.3.3' $good
    $badVersion = New-Run 'invalid EXE version'
    Start-Smoke $badVersion 'update'
    Assert ((Get-Content $badVersion.Log -Raw) -match 'EXE version does not match') 'EXE version mismatch was not rejected'
    Assert (!(Test-Path "$($badVersion.State)/pending.json")) 'EXE version mismatch started handoff'

    foreach ($version in @('1.3.0', '1.2.9')) {
        Write-Feed 'launcher' $version $good
        $old = New-Run "same or older $version"
        Start-Smoke $old 'update'
        Assert ((Get-Content $old.Log -Raw) -match 'no newer update') 'Same or older metadata was not rejected'
        Assert (!(Test-Path $old.State)) 'Same or older metadata reached staging'
    }
    Write-Output 'PASS corrupt EXE, version mismatch, same version, and downgrade rejection'

    $zip = Join-Path $feed 'Lumenveil-fixture-0.1.0.zip'
    $zipStream = [IO.File]::Create($zip)
    $archive = [IO.Compression.ZipArchive]::new($zipStream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $entry = $archive.CreateEntry('Lumenveil/SmokeFixture.dds', [IO.Compression.CompressionLevel]::NoCompression)
        $entryStream = $entry.Open()
        try {
            $bytes = [byte[]]::new(4MB)
            [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
            $entryStream.Write($bytes)
        } finally { $entryStream.Dispose() }
    } finally { $archive.Dispose(); $zipStream.Dispose() }

    $graphics = New-Run 'graphics download'
    $extra = @{
        installationEnabled = $false; compatibleWowSha256 = @(); prerequisites = @('Synthetic fixture only')
        files = @('Lumenveil/SmokeFixture.dds')
    }
    Write-Feed 'lumenveil' '0.1.0' $zip $extra
    Start-Smoke $graphics 'graphics'
    Check-Progress $graphics.Log @('Downloading', 'Verifying', 'Download verified')
    Assert ((Get-Content $graphics.Log -Raw) -notmatch 'Extracting|Installing') 'Download unexpectedly installed graphics'
    $cached = @(Get-ChildItem "$($graphics.Folder)/staging-graphics-cache" -Recurse -Filter '*.payload')
    Assert ($cached.Count -eq 1 -and (Get-FileHash $cached[0].FullName).Hash -eq (Get-FileHash $zip).Hash) 'Cached ZIP hash was wrong'
    Write-Output 'PASS real Lumenveil handler downloaded and verified fixture ZIP with live progress and no installation'

    if ($DisposableClient) {
        $client = (Resolve-Path -LiteralPath $DisposableClient).Path
        $originalClient = 'C:\WoWClient-Worgen-Test'
        Assert ($client -ne $originalClient -and !$client.StartsWith($originalClient + '\', [StringComparison]::OrdinalIgnoreCase)) 'Choose a separate disposable copy'
        Assert (((Get-Item -LiteralPath $client).Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'Disposable client may not be a link'
        $wow = Join-Path $client 'Wow.exe'
        $protected = @($wow, "$client/Data/patch-z.mpq", "$client/WTF/Config.wtf", "$client/Data/enUS/realmlist.wtf") |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }
        $hashes = @{}
        foreach ($path in $protected) { $hashes[$path] = (Get-FileHash -LiteralPath $path).Hash }
        $extra.installationEnabled = $true
        $extra.compatibleWowSha256 = @($hashes[$wow])
        $extra.supportedBuilds = @('3.3.5a (12340) disposable test fingerprint')
        Write-Feed 'lumenveil' '0.1.0' $zip $extra
        $fixture = Join-Path $client 'Lumenveil/SmokeFixture.dds'
        Assert (!(Test-Path -LiteralPath $fixture)) 'Fixture path already exists in the disposable client'
        $install = New-Run 'graphics installation' $client
        $wrappers = @('d3d9.dll', 'dxgi.dll', 'dinput8.dll', 'opengl32.dll', 'ReShade32.dll', 'ReShade.ini', 'dxvk.conf', 'version.dll', 'CoAVolFog.dll', 'CoAVolFog.ini') |
            Where-Object { Test-Path -LiteralPath (Join-Path $client $_) }
        if ($wrappers.Count -gt 0) {
            Start-Smoke $install 'graphics-install'
            Assert ((Get-Content $install.Log -Raw) -match 'Resolve these existing graphics files') 'Expected wrapper conflict was not shown'
            Assert (!(Test-Path -LiteralPath $fixture)) 'Conflict still installed a graphics file'
            Write-Output 'PASS existing graphics conflict blocks installation'
        }

        $quarantine = (New-Item -ItemType Directory -Path "$root/fixture wrapper backup").FullName
        try {
            foreach ($wrapper in $wrappers) { Move-Item -LiteralPath (Join-Path $client $wrapper) -Destination $quarantine }
            $unsupported = New-Run 'unsupported graphics client' $client
            $extra.compatibleWowSha256 = @('0' * 64)
            Write-Feed 'lumenveil' '0.1.0' $zip $extra
            Start-Smoke $unsupported 'graphics-install'
            Assert ((Get-Content $unsupported.Log -Raw) -match 'unavailable for the selected client') 'Unsupported client was not reported'
            Assert (!(Test-Path -LiteralPath $fixture)) 'Unsupported client received graphics files'
            $extra.compatibleWowSha256 = @($hashes[$wow])

            $badZip = Join-Path $feed 'Lumenveil-invalid.zip'
            $zipStream = [IO.File]::Create($badZip)
            $archive = [IO.Compression.ZipArchive]::new($zipStream, [IO.Compression.ZipArchiveMode]::Create)
            try { $archive.CreateEntry('../Outside.txt') | Out-Null }
            finally { $archive.Dispose(); $zipStream.Dispose() }
            Write-Feed 'lumenveil' '0.1.0' $badZip $extra
            $unsafe = New-Run 'unsafe graphics ZIP' $client
            Start-Smoke $unsafe 'graphics-install'
            Assert ((Get-Content $unsafe.Log -Raw) -match 'unsafe path') 'Archive traversal was not rejected'
            Assert (!(Test-Path -LiteralPath $fixture)) 'Invalid ZIP installed graphics files'
            Write-Feed 'lumenveil' '0.1.0' $zip $extra
            Write-Output 'PASS unsupported WoW fingerprint and unsafe signed ZIP rejected for installation'

            Start-Smoke $install 'graphics-install'
            Check-Progress $install.Log @('Downloading', 'Verifying', 'Download verified', 'Extracting', 'Installing', 'Lumenveil installed')
            Assert ((Get-Item -LiteralPath $fixture).Length -eq 4MB) 'Fixture was not installed'
            Start-Smoke $install 'graphics-remove'
            Assert (!(Test-Path -LiteralPath $fixture)) 'Fixture was not removed'
            Assert ((Get-Content $install.Log -Raw) -match 'Lumenveil removed') 'Remove was not reported'
            Start-Smoke $install 'graphics-install'
            Set-Content -LiteralPath $fixture -Value 'player edit'
            $edited = (Get-FileHash -LiteralPath $fixture).Hash
            Start-Smoke $install 'graphics-remove'
            Assert ((Get-FileHash -LiteralPath $fixture).Hash -eq $edited) 'Removal discarded a later player edit'
            Assert ((Get-Content $install.Log -Raw) -match 'edited files were kept') 'Preserved edit was not reported'
            Remove-Item -LiteralPath $fixture
            $clientState = Join-Path $client '.coops-lumenveil'
            $transaction = [guid]::NewGuid().ToString('N')
            [IO.File]::WriteAllText($fixture, 'interrupted install fixture')
            @{
                id = $transaction; removing = $false
                files = @(@{ path = 'Lumenveil/SmokeFixture.dds'; sha256 = (Get-FileHash -LiteralPath $fixture).Hash })
            } | ConvertTo-Json -Depth 5 -Compress | Set-Content -LiteralPath "$clientState/pending.json" -Encoding utf8NoBOM
            Start-Smoke $install 'graphics'
            Assert (!(Test-Path -LiteralPath $fixture) -and !(Test-Path "$clientState/pending.json")) 'Interrupted graphics install was not recovered'
            foreach ($path in $protected) {
                Assert ((Get-FileHash -LiteralPath $path).Hash -eq $hashes[$path]) "Protected client file changed: $path"
            }
            Write-Output 'PASS fixture install and removal on disposable client; protected client hashes preserved'
        } finally {
            foreach ($wrapper in $wrappers) {
                $backup = Join-Path $quarantine $wrapper
                if (Test-Path -LiteralPath $backup) { Move-Item -LiteralPath $backup -Destination $client }
            }
        }
    }

    Stop-Process -Id $server.Id
    $server.WaitForExit()
    $offline = New-Run 'offline feed'
    Start-Smoke $offline 'update'
    Assert ((Get-Content $offline.Log -Raw) -match 'Launcher update check failed') 'Offline launcher check did not return cleanly'
    Assert (!(Test-Path $offline.State)) 'Offline check reached staging'
    Write-Output 'PASS offline launcher feed returns cleanly'
} finally {
    if ($server -and !$server.HasExited) { Stop-Process -Id $server.Id }
    foreach ($exe in $runs) { Get-Launcher $exe | Stop-Process -ErrorAction SilentlyContinue }
    $ecdsa.Dispose()
}
