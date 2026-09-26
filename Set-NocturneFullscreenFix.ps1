#requires -Version 5.1
<#
.SYNOPSIS
Enables or restores the optional Direct3D 11 fullscreen fix for Nocturne.
.DESCRIPTION
Supports Steam build 25487568 only. Changes three bytes and four padding bytes
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
$originalHash = 'D59B6C07BA986BB126D10004CC33E7986836C5B58E419C4789A3B187DB84EEE9'
$displayHash = '5982971BE3365B716E0204E8C99EACCEA05D7C82F52D787C13E3EE5B19B11418'
$assemblyHash = 'D7F8BD3A701D5154EBD57840AD9F1E11D14B307D242CFD53CFCACB72DB50B9D1'
$metadataHash = 'A8ED37CBD7754037ADC72DC5D4B3A5E5C9D803355B80C8CAF9FE7C0C7BAA33EE'

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

function Read-SharedText([string]$Path) {
    # Steam writes its .vdf and .acf files as UTF-8 and may have them open.
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]'ReadWrite, Delete')
    try { return (New-Object IO.StreamReader($stream, [Text.Encoding]::UTF8, $true)).ReadToEnd() }
    finally { $stream.Dispose() }
}

function Get-ExistingFolder([string]$Path) {
    # Steam keeps listing libraries on drives that are gone, and PS 5.1's Join-Path and Resolve-Path
    # throw for a missing drive. Anything that isn't an existing absolute folder is $null.
    try {
        if (-not $Path) { return $null }
        $Path = $Path.Trim().Replace('/', '\')
        if ($Path -notmatch '^(?:[A-Za-z]:\\|\\\\[^\\])') { return $null }
        $full = [IO.Path]::GetFullPath($Path)
        if (-not [IO.Directory]::Exists($full)) { return $null }
        if ($full.Length -gt 3) { $full = $full.TrimEnd('\') }
        return $full
    }
    catch { return $null }
}

function Get-SteamRoots {
    $found = $false
    foreach ($registryPath in @('HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam')) {
        try {
            $entry = Get-ItemProperty -LiteralPath $registryPath -ErrorAction SilentlyContinue
            if ($null -eq $entry) { continue }
            foreach ($property in @('SteamPath', 'InstallPath')) {
                $value = $entry.PSObject.Properties[$property]
                if ($null -ne $value -and -not [string]::IsNullOrWhiteSpace([string]$value.Value)) {
                    $folder = Get-ExistingFolder ([string]$value.Value)
                    if ($folder) { $found = $true; Write-Output $folder }
                }
            }
        }
        catch { continue }
    }
    # Steam rewrites SteamPath each time it starts, so its default folder is only a fallback. Beside a real
    # one it can be a junction to it, which would list the same game twice.
    if (-not $found -and ${env:ProgramFiles(x86)}) { Write-Output (${env:ProgramFiles(x86)}.TrimEnd('\') + '\Steam') }
}

function Find-NocturneGamePath([string]$ExplicitPath) {
    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        $resolved = $null
        try {
            # A relative path starts at PowerShell's current folder; cd doesn't change the one .NET uses.
            $resolved = $ExplicitPath.Trim().Trim('"')
            if (-not [IO.Path]::IsPathRooted($resolved)) {
                $resolved = [IO.Path]::Combine((Get-Location -PSProvider FileSystem).ProviderPath, $resolved)
            }
            $resolved = [IO.Path]::GetFullPath($resolved)
        }
        catch { $resolved = $null }
        if (-not $resolved -or -not [IO.Directory]::Exists($resolved)) { throw 'GamePath must name the Nocturne installation folder.' }
        if ($resolved.Length -gt 3) { $resolved = $resolved.TrimEnd('\', '/') }
        return $resolved
    }

    # One bad library (a missing drive, an offline share, an unreadable file) only skips that library.
    $libraries = New-Object 'System.Collections.Generic.List[string]'
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    # Each Steam folder's vdf is read once and each missing library is checked once: an offline share
    # can take many seconds to answer.
    $vdfRead = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $missing = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($steamRoot in @(Get-SteamRoots)) {
        try {
            $rootFolder = Get-ExistingFolder $steamRoot
            if (-not $rootFolder -or -not $vdfRead.Add($rootFolder)) { continue }
            if ($seen.Add($rootFolder)) { $libraries.Add($rootFolder) }
            $libraryFile = [IO.Path]::Combine($rootFolder, 'steamapps\libraryfolders.vdf')
            if (-not [IO.File]::Exists($libraryFile)) { continue }
            $libraryText = Read-SharedText $libraryFile
            # Current VDF uses path properties; older Steam versions used numbered values.
            foreach ($match in [regex]::Matches($libraryText, '"(?:path|\d+)"\s+"((?:\\.|[^"\\])*)"')) {
                $path = $match.Groups[1].Value.Replace('\\', '\')
                if ($missing.Contains($path)) { continue }
                $library = Get-ExistingFolder $path
                if (-not $library) { [void]$missing.Add($path) }
                elseif ($seen.Add($library)) { $libraries.Add($library) }
            }
        }
        catch { continue }
    }

    $candidates = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($library in $libraries) {
        try {
            $common = [IO.Path]::Combine($library, 'steamapps\common')
            $folder = 'Nocturne'
            $manifest = [IO.Path]::Combine($library, 'steamapps\appmanifest_1374860.acf')
            if ([IO.File]::Exists($manifest)) {
                try {
                    # No slash or colon: a folder name, never a path such as C:Elsewhere or ..\x.
                    $match = [regex]::Match((Read-SharedText $manifest), '"installdir"\s+"([^"\\/:]+)"')
                    if ($match.Success -and $match.Groups[1].Value -notin @('.', '..')) { $folder = $match.Groups[1].Value }
                }
                catch { $folder = 'Nocturne' }
            }
            $candidate = [IO.Path]::Combine($common, $folder)
            if ([IO.File]::Exists([IO.Path]::Combine($candidate, 'Nocturne.exe'))) {
                [void]$candidates.Add([IO.Path]::GetFullPath($candidate))
            }
        }
        catch { continue }
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

function Get-SteamManifestPath([string]$GameRoot) {
    # A Steam game sits in <library>\steamapps\common\<folder>. A copy elsewhere, even straight
    # under a drive root, has no manifest to check.
    $common = [IO.Path]::GetDirectoryName($GameRoot.TrimEnd('\', '/'))
    if (-not $common) { return $null }
    $steamApps = [IO.Path]::GetDirectoryName($common)
    if (-not $steamApps) { return $null }
    return [IO.Path]::Combine($steamApps, 'appmanifest_1374860.acf')
}

# Dot-sourcing only loads the functions above. It changes nothing.
if ($MyInvocation.InvocationName -eq '.') { return }

$root = Find-NocturneGamePath $GamePath
$data = Join-Path $root 'Nocturne_Data'
$target = Join-Path $data 'globalgamemanagers'
$backup = Join-Path $data 'globalgamemanagers.nocturne-fullscreen-original-25487568.bak'
if (-not [IO.File]::Exists((Join-Path $root 'Nocturne.exe'))) { throw 'GamePath does not contain Nocturne.exe.' }
if ((Get-FileSha256 (Join-Path $root 'GameAssembly.dll')) -ne $assemblyHash -or
    (Get-FileSha256 (Join-Path $data 'il2cpp_data\Metadata\global-metadata.dat')) -ne $metadataHash) {
    throw 'This game build is not supported. This fix supports Steam build 25487568 only; no files were changed.'
}
$steamManifest = Get-SteamManifestPath $root
if ($steamManifest -and [IO.File]::Exists($steamManifest)) {
    $manifestText = $null
    try { $manifestText = Read-SharedText $steamManifest }
    catch { Write-Output "Steam's Nocturne manifest could not be read, so its build number was not checked. The game files match build 25487568." }
    if ($null -ne $manifestText -and $manifestText -notmatch '"buildid"\s+"25487568"') {
        throw 'Steam reports an unsupported Nocturne build. No files were changed.'
    }
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
Write-Output "Compatibility verified: Nocturne Steam build 25487568; action $Action."
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
