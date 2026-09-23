#requires -Version 5.1
<#
.SYNOPSIS
Enables or restores the optional Direct3D 11 fullscreen fix for Nocturne.
.DESCRIPTION
Supports Steam build 25460029 only. Changes three bytes and four padding bytes
in the local globalgamemanagers file; this script contains no game assets.
The original file is backed up beside it. Saves and preferences are untouched.
Close Nocturne first. Use -WhatIf to check compatibility without writing files.
.EXAMPLE
.\Set-NocturneFullscreenFix.ps1 -Action Enable
.EXAMPLE
.\Set-NocturneFullscreenFix.ps1 -Action Restore -GamePath 'D:\SteamLibrary\steamapps\common\Nocturne'
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [ValidateSet('Enable', 'Restore')]
    [string]$Action = 'Enable',
    [string]$GamePath
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$originalHash = 'FE762444F73E4D28B198B13793B6BBF191BC5782678B5A043561CF65AD6C4566'
$displayHash = '01D4723A65A8C0016BDC2FEC4408E577EC3B192748F2F870B783BD8E103F9890'
$assemblyHash = 'FD4D5879A71CE00CA3FC3C3D176FFB3F146E9CEC52DD6A06807CF564C6542940'
$metadataHash = '3BB22E4F33C103F6612AD5988C87528BD46BB23142CD733D22375CBE84837054'

function Get-FileSha256([string]$Path) {
    if (-not [IO.File]::Exists($Path)) { throw "Required file is missing: $Path" }
    # Get-FileHash in Windows PowerShell 5.1 can inherit -WhatIf into its
    # internal pipeline. Direct streaming hashing stays read-only in that case.
    $sha = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($Path)
    try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose(); $sha.Dispose() }
}

function Get-BytesSha256([byte[]]$Bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($Bytes)).Replace('-', '') }
    finally { $sha.Dispose() }
}

function Assert-NocturneClosed {
    if (Get-Process -Name Nocturne -ErrorAction SilentlyContinue) {
        throw 'Close Nocturne before enabling or restoring its fullscreen fix.'
    }
}

function Find-NocturneGamePath([string]$ExplicitPath) {
    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        $resolved = (Resolve-Path -LiteralPath $ExplicitPath).ProviderPath
        if (-not [IO.Directory]::Exists($resolved)) { throw 'GamePath must name the Nocturne installation folder.' }
        return $resolved
    }

    $steamRoots = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($registryPath in @('HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam')) {
        $entry = Get-ItemProperty -LiteralPath $registryPath -ErrorAction SilentlyContinue
        if ($null -eq $entry) { continue }
        foreach ($property in @('SteamPath', 'InstallPath')) {
            $value = $entry.PSObject.Properties[$property]
            if ($null -ne $value -and -not [string]::IsNullOrWhiteSpace([string]$value.Value)) {
                [void]$steamRoots.Add([string]$value.Value)
            }
        }
    }
    if (${env:ProgramFiles(x86)}) { [void]$steamRoots.Add((Join-Path ${env:ProgramFiles(x86)} 'Steam')) }

    $libraries = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($steamRoot in $steamRoots) {
        [void]$libraries.Add($steamRoot)
        $libraryFile = Join-Path $steamRoot 'steamapps\libraryfolders.vdf'
        if (-not [IO.File]::Exists($libraryFile)) { continue }
        $libraryText = [IO.File]::ReadAllText($libraryFile)
        foreach ($match in [regex]::Matches($libraryText, '"path"\s+"((?:\\.|[^"\\])*)"')) {
            [void]$libraries.Add($match.Groups[1].Value.Replace('\\', '\'))
        }
    }

    $candidates = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($library in $libraries) {
        $common = Join-Path $library 'steamapps\common'
        $folder = 'Nocturne'
        $manifest = Join-Path $library 'steamapps\appmanifest_1374860.acf'
        if ([IO.File]::Exists($manifest)) {
            $match = [regex]::Match([IO.File]::ReadAllText($manifest), '"installdir"\s+"([^"\\/]+)"')
            if ($match.Success -and $match.Groups[1].Value -notin @('.', '..')) { $folder = $match.Groups[1].Value }
        }
        $candidate = Join-Path $common $folder
        if ([IO.File]::Exists((Join-Path $candidate 'Nocturne.exe'))) {
            [void]$candidates.Add([IO.Path]::GetFullPath($candidate))
        }
    }
    if ($candidates.Count -eq 1) { foreach ($candidate in $candidates) { return $candidate } }
    if ($candidates.Count -gt 1) { throw 'More than one Nocturne installation was found. Specify the intended folder with -GamePath.' }
    throw 'Nocturne was not found in the Steam libraries. Specify its installation folder with -GamePath.'
}

function Convert-DisplayBytes([byte[]]$Source, [string]$SourceHash, [bool]$Enable) {
    $isOriginal = $SourceHash -eq $originalHash
    $isPatched = $SourceHash -eq $displayHash
    if (-not ($isOriginal -or $isPatched)) { throw 'Refusing an unknown globalgamemanagers file.' }
    $sourceLength = if ($isOriginal) { 447916 } else { 447920 }
    if ($Source.Length -ne $sourceLength) { throw 'Unexpected globalgamemanagers length.' }
    $oldHeader = if ($isOriginal) { 0xAC } else { 0xB0 }
    $oldFirstApi = if ($isOriginal) { 0x12 } else { 0x02 }
    $oldSecondApi = if ($isOriginal) { 0x02 } else { 0x12 }
    if ($Source[31] -ne $oldHeader -or $Source[227468] -ne $oldFirstApi -or $Source[227472] -ne $oldSecondApi) {
        throw 'The pinned graphics-API byte ranges do not match.'
    }
    if ($isPatched) {
        for ($offset = 447916; $offset -lt 447920; $offset++) {
            if ($Source[$offset] -ne 0) { throw 'The pinned trailing padding does not match.' }
        }
    }

    $length = if ($Enable) { 447920 } else { 447916 }
    $result = New-Object byte[] $length
    [Buffer]::BlockCopy($Source, 0, $result, 0, [Math]::Min($Source.Length, $length))
    # Unity's big-endian file-size header, followed by its D3D11/D3D12 priority list.
    # Four zero padding bytes reproduce the tested serialized file exactly.
    $result[31] = if ($Enable) { 0xB0 } else { 0xAC }
    $result[227468] = if ($Enable) { 0x02 } else { 0x12 }
    $result[227472] = if ($Enable) { 0x12 } else { 0x02 }
    $expected = if ($Enable) { $displayHash } else { $originalHash }
    if ((Get-BytesSha256 $result) -ne $expected) { throw 'Sparse patch verification failed. No game file was changed.' }
    return ,$result
}

function Write-VerifiedStage([string]$Directory, [byte[]]$Bytes, [string]$ExpectedHash) {
    $path = Join-Path $Directory ('.nocturne-fullscreen-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    $created = $false
    try {
        # CreateNew prevents overwriting any existing file, including a concurrent stage.
        $stream = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $created = $true
        try { $stream.Write($Bytes, 0, $Bytes.Length); $stream.Flush($true) }
        finally { $stream.Dispose() }
        if ((Get-FileSha256 $path) -ne $ExpectedHash) { throw 'Staged file verification failed.' }
        return $path
    }
    catch {
        if ($created -and [IO.File]::Exists($path)) { [IO.File]::Delete($path) }
        throw
    }
}

$root = Find-NocturneGamePath $GamePath
$data = Join-Path $root 'Nocturne_Data'
$target = Join-Path $data 'globalgamemanagers'
$backup = Join-Path $data 'globalgamemanagers.nocturne-fullscreen-original-25460029.bak'
if (-not [IO.File]::Exists((Join-Path $root 'Nocturne.exe'))) { throw 'GamePath does not contain Nocturne.exe.' }
if ((Get-FileSha256 (Join-Path $root 'GameAssembly.dll')) -ne $assemblyHash -or
    (Get-FileSha256 (Join-Path $data 'il2cpp_data\Metadata\global-metadata.dat')) -ne $metadataHash) {
    throw 'This game build is not supported. This fix supports Steam build 25460029 only; no files were changed.'
}
$steamManifest = Join-Path (Split-Path (Split-Path $root -Parent) -Parent) 'appmanifest_1374860.acf'
if ([IO.File]::Exists($steamManifest) -and [IO.File]::ReadAllText($steamManifest) -notmatch '"buildid"\s+"25460029"') {
    throw 'Steam reports an unsupported Nocturne build. No files were changed.'
}

$source = [IO.File]::ReadAllBytes($target)
$sourceHash = Get-BytesSha256 $source
if ($sourceHash -notin @($originalHash, $displayHash)) {
    throw 'globalgamemanagers is not the supported original or fullscreen fix. No files were changed.'
}
if (Test-Path -LiteralPath $backup) {
    if ((Get-FileSha256 $backup) -ne $originalHash) { throw 'The existing fullscreen backup is not the expected original. No files were changed.' }
}
$enable = $Action -eq 'Enable'
$desiredHash = if ($enable) { $displayHash } else { $originalHash }
$desired = Convert-DisplayBytes $source $sourceHash $enable
$original = Convert-DisplayBytes $source $sourceHash $false
$needsBackup = $enable -and -not [IO.File]::Exists($backup)
$needsChange = $sourceHash -ne $desiredHash
if (-not $WhatIfPreference) { Assert-NocturneClosed }
Write-Output "Compatibility verified: Nocturne Steam build 25460029; action $Action."
if (-not ($needsBackup -or $needsChange)) { Write-Output 'Already in the requested state. No files changed.'; return }
if (-not $PSCmdlet.ShouldProcess($target, "$Action optional Direct3D 11 fullscreen fix; keep a verified original backup")) { return }

$stage = $null
$backupStage = $null
try {
    Assert-NocturneClosed
    if ($needsChange) { $stage = Write-VerifiedStage $data $desired $desiredHash }
    if ($needsBackup) { $backupStage = Write-VerifiedStage $data $original $originalHash }
    # Recheck the actual files after staging, before the first permanent write.
    if ((Get-FileSha256 $target) -ne $sourceHash) { throw 'The game file changed during preparation; no replacement was made.' }
    if (Test-Path -LiteralPath $backup) {
        if ((Get-FileSha256 $backup) -ne $originalHash) { throw 'The backup changed during preparation; no replacement was made.' }
    }
    Assert-NocturneClosed
    if ($backupStage) {
        if (-not [IO.File]::Exists($backup)) { [IO.File]::Move($backupStage, $backup); $backupStage = $null }
    }
    if ($stage) {
        # The stage and target share a directory/volume. File.Replace is atomic.
        [IO.File]::Replace($stage, $target, [NullString]::Value)
        $stage = $null
        if ((Get-FileSha256 $target) -ne $desiredHash) { throw 'Replacement verification failed. The verified original backup has been preserved.' }
    }
}
finally {
    foreach ($temporary in @($stage, $backupStage)) {
        if ($temporary -and [IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
    }
}
if ($enable) { Write-Output 'Enabled the Direct3D 11 fullscreen fix. The original is backed up beside globalgamemanagers.' }
else { Write-Output 'Restored the exact original display configuration. Any local backup was kept.' }
