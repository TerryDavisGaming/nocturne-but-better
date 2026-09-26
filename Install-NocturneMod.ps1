#Requires -Version 5.1
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [ValidateSet('Install', 'Uninstall')][string]$Action = 'Install',
    [ValidateSet('Auto', 'BepInEx', 'MelonLoader')][string]$Loader = 'Auto',
    [string]$GamePath,
    [string]$LoaderArchive,
    [string]$PluginPath,
    [string]$MelonModPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$packageRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $LoaderArchive) { $LoaderArchive = Join-Path $packageRoot 'payload\BepInEx-IL2CPP-x64-788.zip' }
if (-not $PluginPath) { $PluginPath = Join-Path $packageRoot 'payload\NocturneFlatScroll.dll' }
if (-not $MelonModPath) { $MelonModPath = Join-Path $packageRoot 'payload\NocturneFlatScroll.MelonLoader.dll' }

$modVersion = '2.6.2'
$loaderHash = 'F4CC496BD098A0DF4164B81E3737297707F13A47C2478DBA2F60EEFAB784817A'
$pluginHash = 'A1001EE03E0E2B04297EA6B8F6B0703CB63B41985B0A7218AB6E71A39244E2A0'
$knownPluginHashes = @(
    $pluginHash,
    '2FFD51528286B71C0268F53D74D1D88A99E0B5FBB6B6E930FE2C04B1D89F3EF8',
    '8153E76B0600FC4BBCEECC8418B6D3DDB1AADBEE03EF663278289BF8DC146DBD',
    'A11F80001E2F6E2ABAFE7DD78E1791AF69AB6EC77D2484BC02FBBED7325AB47E',
    'C48AF897B3731D023A6FF52013C66AAE0FBAF208DDB8C5ABC57CA2B76AA2FB82',
    '75A7B3C7E4CAC20A7A11834E8A69B441A6F48E4C7EEA26C4FFBB4A341D4A36DA',
    '5BB8EA599A9E7F8DF86D5E3E7F5F0ACDF136CDC79C35976687EAEFD2FDD2175C',
    'EECA5992CB2A195BCBE9B51BB9160165E166A6B153501E74DAB5BD16F6A18B50',
    'CABB75AB8A25F50DABF97E64F6152435A6F0DD026B755840E904325907A98BDA',
    '8585E7821293755C6FFE069E1B88E0D27ECB77559E9A7869022C3A2410F82EAA',
    '40ED8E9A39C073D9024668B065CFECBA90E4CEB0112833F3102C5D902AA618AC',
    '0B1D12F92561F9F33B4F081BC977419BDFCE85B7CDFC76DE0804FF0A6F690599',
    '212DB106C6761E23413703B464C94ED26CD6982BAFAE771F1ED7D4287BA83A39',
    '6E75984D4F6AB5F6D33F4031A53DD3E0B80E5BBD70529B429F9EEDA14D79BC4E',
    '43EA6E714435C6F376083A159F8566C712CA9C52B43F91E186909C7F9399A295',
    '5CEDADBF931553A7614B92EB0D5694B99F300135979CF997CEB01E597960657D',
    'C6025C68612A64B5EFD897C1495C6DF1A31B3389C0EDF531E99AFF0931E51E1C'
)
$melonModHash = '9E9146E36B6D285B104E3B6480302E3E2D147F7CC66600305079960524BD71B8'
$knownMelonModHashes = @($melonModHash, 'B68FDAA16E417ACE0795F0B14F89CC37BF411601B074F43EDBB6FA41E09CF224', 'D7A85B5541BA00292C761CA7FD287C96CD299B894284959DFC1ED5388B4B0FA0', 'EDA93DF8580864B262E1339A59A4CB96A6C0033C115FAF10444B34F17928EA34', 'AE16BD9C4EB9D468E6BF51B8A780A95B37D59F37252B6466AD6C4DE920F70986', '1D019D17B65C83ACFD747E3DB6446D872F4FA401365F37C0853FAC05C4D0BDC3', 'A872E7BF847981EAB25FCB9CB0B3CCF363517A923DD4B3C0941BA2BDD24BEEC9', '4A9CA300577AD598E5B2498ACDE6ED7D65CABE2DCCE169832BAE9A13A44E4BC0', '3A1876189499D11D57B4102CBD1B88D47EC0E8594EAC0B861B533F2F0414A6BD', 'E7575406BE68712A6853F5166E9847EE38E94C30C7DBCA23DA85E6D364799077', '19DE2BBC86CE71DE8CC48BD06765255A5FC83F4ED34E7CED2619E42EF6C428C7', 'CFB3AFA7C2CA1590D6F3ABEF40A3034FDBB4D69F1D61A153B7AB26C678918921')
$gameHashes = @{
    'GameAssembly.dll' = 'D7F8BD3A701D5154EBD57840AD9F1E11D14B307D242CFD53CFCACB72DB50B9D1'
    'Nocturne_Data\il2cpp_data\Metadata\global-metadata.dat' = 'A8ED37CBD7754037ADC72DC5D4B3A5E5C9D803355B80C8CAF9FE7C0C7BAA33EE'
    'Nocturne_Data\level2' = 'BB73CB8A1E3CCD47697479D4DCAE2B0DFC1D23E576575D2E1563F223359E127C'
    'Nocturne_Data\sharedassets2.assets' = '488C3683A13846C82C58907B2DA645A69C00E0918CA48A093CC49F9EEB181685'
}

function Get-Sha256([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Required file is missing: $Path" }
    # PS 5.1 Get-FileHash inherits CLI -WhatIf inside its property pipeline.
    # Hash directly so validation is truly read-only and also works in previews.
    $sha = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($Path)
    try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose(); $sha.Dispose() }
}

function Assert-Hash([string]$Path, [string]$Expected) {
    if ((Get-Sha256 $Path) -ne $Expected) {
        throw "This file does not match the supported package/game build: $Path. No game files have been changed."
    }
}

function ConvertFrom-VdfPath([string]$Value) {
    return $Value.Replace('\\', '\').Replace('\"', '"')
}

function Read-SharedText([string]$Path) {
    # Steam writes its .vdf and .acf files as UTF-8 and may have them open.
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]'ReadWrite, Delete')
    try { return (New-Object IO.StreamReader($stream, [Text.Encoding]::UTF8, $true)).ReadToEnd() }
    finally { $stream.Dispose() }
}

function Get-ExistingFolder([string]$Path) {
    # Steam keeps listing libraries on drives that are gone, and PS 5.1's Join-Path throws for a missing
    # drive. Plain string and .NET checks only: anything that isn't an existing absolute folder is $null.
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
    # The registry's SteamPath uses forward slashes, like c:/program files (x86)/steam.
    $found = $false
    foreach ($location in @(
        @('HKCU:\Software\Valve\Steam', 'SteamPath'),
        @('HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'InstallPath'),
        @('HKLM:\SOFTWARE\Valve\Steam', 'InstallPath')
    )) {
        try {
            $item = Get-ItemProperty -LiteralPath $location[0] -ErrorAction SilentlyContinue
            if ($item) {
                $property = $item.PSObject.Properties[$location[1]]
                if ($property -and $property.Value) {
                    $folder = Get-ExistingFolder ([string]$property.Value)
                    if ($folder) { $found = $true; Write-Output $folder }
                }
            }
        }
        catch { Write-Verbose "Skipped $($location[0]): $($_.Exception.Message)" }
    }
    # Steam rewrites SteamPath each time it starts, so its default folder is only a fallback. Beside a real
    # one it can be a junction to it, which would list the same game twice.
    if (-not $found -and ${env:ProgramFiles(x86)}) { Write-Output (${env:ProgramFiles(x86)}.TrimEnd('\') + '\Steam') }
}

function Find-SteamGames {
    # One bad library (a missing drive, an offline share, an unreadable file) only skips that library.
    $libraries = New-Object 'System.Collections.Generic.List[string]'
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    # Each Steam folder's vdf is read once and each missing library is checked once: an offline share
    # can take many seconds to answer.
    $vdfRead = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $missing = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($root in @(Get-SteamRoots)) {
        try {
            $rootFolder = Get-ExistingFolder $root
            if (-not $rootFolder -or -not $vdfRead.Add($rootFolder)) { continue }
            if ($seen.Add($rootFolder)) { $libraries.Add($rootFolder) }
            $vdf = [IO.Path]::Combine($rootFolder, 'steamapps\libraryfolders.vdf')
            if (-not [IO.File]::Exists($vdf)) { continue }
            $contents = Read-SharedText $vdf
            # Current VDF uses path properties; older Steam versions used numbered values.
            foreach ($match in [regex]::Matches($contents, '"(?:path|\d+)"\s+"((?:\\.|[^"\\])*)"')) {
                $path = ConvertFrom-VdfPath $match.Groups[1].Value
                if ($missing.Contains($path)) { continue }
                $library = Get-ExistingFolder $path
                if (-not $library) { [void]$missing.Add($path) }
                elseif ($seen.Add($library)) { $libraries.Add($library) }
            }
        }
        catch { Write-Verbose "Skipped Steam folder ${root}: $($_.Exception.Message)" }
    }
    $candidates = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($library in $libraries) {
        try {
            $apps = [IO.Path]::Combine($library, 'steamapps')
            $manifest = [IO.Path]::Combine($apps, 'appmanifest_1374860.acf')
            $installName = 'Nocturne'
            if ([IO.File]::Exists($manifest)) {
                # An unreadable manifest leaves Steam's default folder name.
                try {
                    $match = [regex]::Match((Read-SharedText $manifest), '"installdir"\s+"((?:\\.|[^"\\])*)"')
                    if ($match.Success) { $installName = ConvertFrom-VdfPath $match.Groups[1].Value }
                }
                catch { Write-Verbose "Could not read ${manifest}: $($_.Exception.Message)" }
            }
            # Never interpret an untrusted manifest's install folder as an absolute/traversal path.
            if ($installName -match '[\\/:]' -or $installName -in @('.', '..')) { continue }
            $candidate = [IO.Path]::Combine([IO.Path]::Combine($apps, 'common'), $installName)
            if ([IO.File]::Exists([IO.Path]::Combine($candidate, 'Nocturne.exe'))) {
                $candidate = [IO.Path]::GetFullPath($candidate)
                if ($candidates.Add($candidate)) { Write-Output $candidate }
            }
        }
        catch { Write-Verbose "Skipped Steam library ${library}: $($_.Exception.Message)" }
    }
}

function Resolve-GameRoot([string]$RequestedPath) {
    if (-not $RequestedPath) {
        $found = @(Find-SteamGames)
        if ($found.Count -eq 1) { $RequestedPath = $found[0] }
        else {
            if ($found.Count -gt 1) {
                Write-Host 'More than one Nocturne installation was found:'
                foreach ($path in $found) { Write-Host "  $path" }
            }
            else { Write-Host 'Nocturne was not found automatically in the Steam libraries.' }
            $RequestedPath = Read-Host 'Enter the full folder path containing Nocturne.exe'
        }
    }
    if (-not $RequestedPath -or -not $RequestedPath.Trim().Trim('"')) { throw 'A Nocturne game folder is required.' }
    $resolved = $RequestedPath.Trim().Trim('"')
    $isGame = $false
    # A missing drive, an offline share or a mistyped path gets the same plain answer, not a PowerShell error.
    try {
        # A relative path starts at PowerShell's current folder; cd doesn't change the one .NET uses.
        if (-not [IO.Path]::IsPathRooted($resolved)) {
            $resolved = [IO.Path]::Combine((Get-Location -PSProvider FileSystem).ProviderPath, $resolved)
        }
        $resolved = [IO.Path]::GetFullPath($resolved)
        $isGame = [IO.File]::Exists([IO.Path]::Combine($resolved, 'Nocturne.exe')) -and
            [IO.Directory]::Exists([IO.Path]::Combine($resolved, 'Nocturne_Data'))
    }
    catch { $isGame = $false }
    if (-not $isGame) { throw "This is not the Nocturne game folder: $resolved" }
    return $resolved.TrimEnd('\', '/')
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

function Assert-SafeTarget([string]$Root, [string]$Path) {
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $full = [IO.Path]::GetFullPath($Path)
    if ($full -ne $rootFull -and -not $full.StartsWith($rootFull + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "A destination is outside the selected game folder: $Path"
    }
    # A junction or symlink inside the game must not redirect a write to another folder.
    $current = $full
    while ($current.Length -ge $rootFull.Length) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "A destination uses a junction or symbolic link: $current. Choose a regular game folder."
            }
            if ($current -ne $full -and -not $item.PSIsContainer) { throw "A file blocks a required folder: $current" }
        }
        if ($current -eq $rootFull) { break }
        $current = Split-Path -Parent $current
    }
}

function Read-Ini([string]$Path) {
    $values = @{}
    $section = ''
    foreach ($line in Get-Content -LiteralPath $Path) {
        $trimmed = $line.Trim()
        if ($trimmed -match '^\[([^\]]+)\]') { $section = $matches[1].Trim(); continue }
        if ($trimmed -match '^([^#;=]+?)\s*=\s*(.*?)\s*$') {
            $key = $section + '.' + $matches[1].Trim()
            if ($values.ContainsKey($key)) { throw "Duplicate setting in existing Doorstop configuration: $key" }
            $values[$key] = ($matches[2] -replace '\s+[;#].*$', '').Trim().Trim('"')
        }
    }
    return $values
}

function Assert-DoorstopConfig([string]$Path, [string]$Root) {
    $ini = Read-Ini $Path
    if ($ini['General.enabled'] -notin @('true', '1')) {
        throw 'The existing doorstop_config.ini has Doorstop disabled. Enable it before installing; this installer preserves your configuration.'
    }
    $expectedPaths = @{
        'General.target_assembly' = 'BepInEx\core\BepInEx.Unity.IL2CPP.dll'
        'Il2Cpp.coreclr_path' = 'dotnet\coreclr.dll'
        'Il2Cpp.corlib_dir' = 'dotnet'
    }
    foreach ($key in $expectedPaths.Keys) {
        if (-not $ini.ContainsKey($key) -or -not $ini[$key]) { throw "The existing Doorstop configuration is missing $key." }
        $configured = [string]$ini[$key]
        if (-not [IO.Path]::IsPathRooted($configured)) { $configured = Join-Path $Root $configured }
        if ([IO.Path]::GetFullPath($configured).TrimEnd('\', '/') -ne (Join-Path $Root $expectedPaths[$key])) {
            throw "The existing Doorstop configuration uses an incompatible $key. It was not overwritten."
        }
    }
}

function Expand-VerifiedLoader([string]$Archive, [string]$Stage) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        $entries = New-Object 'System.Collections.Generic.List[object]'
        $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $zip.Entries) {
            $relative = $entry.FullName.Replace('/', '\')
            $directory = $relative.EndsWith('\')
            $relative = $relative.TrimEnd('\')
            if (-not $relative -or [IO.Path]::IsPathRooted($relative)) { throw 'The loader archive contains an unsafe path.' }
            foreach ($part in $relative.Split('\')) {
                if (-not $part -or $part -in @('.', '..') -or $part -match '[<>:"|?*\x00-\x1f]' -or
                    $part -match '[. ]$' -or $part -match '^(?i:con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)') {
                    throw "The loader archive contains an unsafe path: $relative"
                }
            }
            if (-not $seen.Add($relative)) { throw "The loader archive repeats a destination: $relative" }
            if ((($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw 'Symbolic links are not allowed in the loader archive.' }
            $target = [IO.Path]::GetFullPath((Join-Path $Stage $relative))
            if (-not $target.StartsWith($Stage + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'The loader archive escapes its staging folder.' }
            $entries.Add([pscustomobject]@{ Entry = $entry; Relative = $relative; Target = $target; Directory = $directory })
        }
        # Validate every archive entry before extracting any of them.
        foreach ($item in $entries) {
            if ($item.Directory) { [void][IO.Directory]::CreateDirectory($item.Target); continue }
            [void][IO.Directory]::CreateDirectory((Split-Path -Parent $item.Target))
            $inputStream = $item.Entry.Open()
            try {
                $outputStream = [IO.File]::Open($item.Target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
                try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose() }
            }
            finally { $inputStream.Dispose() }
            Write-Output ([pscustomobject]@{ Source = $item.Target; Relative = $item.Relative; Hash = (Get-Sha256 $item.Target) })
        }
    }
    finally { $zip.Dispose() }
}

function Copy-NewVerifiedFile([string]$Source, [string]$Target, [string]$Expected, $CreatedFiles) {
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $Target))
    $inputStream = [IO.File]::OpenRead($Source)
    try {
        $outputStream = [IO.File]::Open($Target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
        $CreatedFiles.Add($Target)
        try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose() }
    }
    finally { $inputStream.Dispose() }
    if ((Get-Sha256 $Target) -ne $Expected) { throw "Copy verification failed: $Target" }
}

$minimumMelonLoader = [Version]'0.7.3'

function Test-MelonLoader([string]$Root) {
    # MelonLoader 0.6 and newer keep the IL2CPP runtime in MelonLoader\net6.
    return Test-Path -LiteralPath (Join-Path $Root 'MelonLoader\net6\MelonLoader.dll') -PathType Leaf
}

function Test-LegacyMelonLoader([string]$Root) {
    return (Test-Path -LiteralPath (Join-Path $Root 'MelonLoader\MelonLoader.dll') -PathType Leaf) -and
        -not (Test-MelonLoader $Root)
}

function Test-MelonLoaderProxy([string]$Root) {
    # The proxy can use any of these names; version.dll is the default.
    foreach ($name in @('version', 'winhttp', 'winmm', 'dinput', 'dinput8', 'dsound', 'd3d8', 'd3d9',
                        'd3d10', 'd3d11', 'd3d12', 'ddraw', 'msacm32')) {
        $path = Join-Path $Root ($name + '.dll')
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($path)
        if ("$($info.ProductName) $($info.FileDescription)" -match 'MelonLoader') { return $true }
    }
    return $false
}

function Test-MelonLoaderDisabled([string]$Root) {
    # UserData\Loader.cfg can switch MelonLoader off with "disable = true" under [loader].
    $config = Join-Path $Root 'UserData\Loader.cfg'
    if (-not (Test-Path -LiteralPath $config -PathType Leaf)) { return $false }
    $section = ''
    foreach ($line in Get-Content -LiteralPath $config) {
        $trimmed = $line.Trim()
        if ($trimmed -match '^\[([^\]]+)\]') { $section = $matches[1].Trim(); continue }
        if ($section -eq 'loader' -and $trimmed -cmatch '^disable\s*=\s*true\s*(#.*)?$') { return $true }
    }
    return $false
}

function Get-MelonLoaderVersion([string]$Root) {
    $path = Join-Path $Root 'MelonLoader\net6\MelonLoader.dll'
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($path)
    return [Version]::new([Math]::Max(0, $info.FileMajorPart), [Math]::Max(0, $info.FileMinorPart), [Math]::Max(0, $info.FileBuildPart))
}

function Resolve-Loader([string]$Requested, [string]$Root) {
    if (Test-LegacyMelonLoader $Root) {
        throw "This game has a MelonLoader version older than 0.6. Update MelonLoader to $minimumMelonLoader or newer, or remove it and use BepInEx. Nothing was changed."
    }
    $hasMelon = Test-MelonLoader $Root
    $hasProxy = $hasMelon -and (Test-MelonLoaderProxy $Root)
    $disabled = $hasMelon -and (Test-MelonLoaderDisabled $Root)
    # Both loaders hook the same Unity startup call and only one of them can start. An
    # enabled MelonLoader takes it, so a BepInEx plugin would never load beside it.
    $melonActive = $hasProxy -and -not $disabled
    if ($Requested -eq 'Auto') {
        if ($melonActive) { $Requested = 'MelonLoader' }
        else {
            if ($hasMelon) { Write-Host 'MelonLoader is installed but will not start (its proxy DLL is missing or it is turned off), so BepInEx is used.' }
            $Requested = 'BepInEx'
        }
    }
    if ($Requested -eq 'MelonLoader') {
        if (-not $hasMelon) {
            throw "MelonLoader was not found in this game folder. Install MelonLoader $minimumMelonLoader or newer first, or choose BepInEx. Nothing was changed."
        }
        if (-not $hasProxy) {
            throw 'MelonLoader files are present, but its proxy DLL (normally version.dll) is missing, so MelonLoader would not start. Reinstall MelonLoader, or choose BepInEx. Nothing was changed.'
        }
        if ($disabled) {
            throw 'MelonLoader is turned off in UserData\Loader.cfg ("disable = true" under [loader]). Turn it back on, or choose BepInEx. Nothing was changed.'
        }
        $version = Get-MelonLoaderVersion $Root
        if ($version -lt $minimumMelonLoader) {
            throw "MelonLoader $version is installed. This mod needs MelonLoader $minimumMelonLoader or newer. Update MelonLoader and run this again. Nothing was changed."
        }
    }
    elseif ($melonActive) {
        throw 'MelonLoader is installed and enabled in this game folder, and it stops BepInEx from starting. Install for MelonLoader instead, or remove MelonLoader first. Nothing was changed.'
    }
    return $Requested
}

function Get-ExistingCopyHash([string]$Root, [string]$Path, [string[]]$KnownHashes, [string]$Description) {
    # Only a file at this path is a copy of the mod; nothing there means nothing is written.
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    Assert-SafeTarget $Root $Path
    return Get-KnownInstalledHash $Path $KnownHashes $Description
}

function Get-KnownInstalledHash([string]$Path, [string[]]$KnownHashes, [string]$Description) {
    if (Test-Path -LiteralPath $Path -PathType Container) { throw "A folder is occupying the $Description destination: $Path" }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    $hash = Get-Sha256 $Path
    if ($hash -notin $KnownHashes) {
        throw "An unrecognized $Description is installed at $Path. Nothing was overwritten or disabled."
    }
    return $hash
}

# Dot-sourcing exposes the helpers for read-only discovery and isolated fixture tests.
# It does not install or uninstall anything.
if ($MyInvocation.InvocationName -eq '.') { return }

$stagePath = $null
try {
    if (-not $WhatIfPreference -and (Get-Process -Name Nocturne -ErrorAction SilentlyContinue)) { throw 'Close Nocturne before running this installer.' }
    $gameRoot = Resolve-GameRoot $GamePath
    Assert-SafeTarget $gameRoot $gameRoot
    $pluginTarget = Join-Path $gameRoot 'BepInEx\plugins\NocturneFlatScroll\NocturneFlatScroll.dll'
    $melonTarget = Join-Path $gameRoot 'Mods\NocturneFlatScroll.MelonLoader.dll'
    $installedHash = Get-ExistingCopyHash $gameRoot $pluginTarget $knownPluginHashes 'NocturneFlatScroll plugin'
    $installedMelonHash = Get-ExistingCopyHash $gameRoot $melonTarget $knownMelonModHashes 'NocturneFlatScroll MelonLoader mod'

    if ($Action -eq 'Uninstall') {
        # Removing a known copy remains possible after Steam updates the game.
        $enabled = @()
        if ($installedHash) { $enabled += $pluginTarget }
        if ($installedMelonHash) { $enabled += $melonTarget }
        if ($enabled.Count -eq 0) { Write-Host 'Nocturne But Better is already uninstalled.'; return }
        Write-Host "Verified Nocturne But Better in: $gameRoot"
        $skipped = @()
        foreach ($target in $enabled) {
            $disabledPath = $target + '.disabled-' + [Guid]::NewGuid().ToString('N')
            Assert-SafeTarget $gameRoot $disabledPath
            if ($PSCmdlet.ShouldProcess($target, 'Disable this verified Nocturne But Better copy')) {
                [IO.File]::Move($target, $disabledPath)
                Write-Host "Disabled: $target"
            }
            else { $skipped += $target }
        }
        if ($WhatIfPreference) { return }
        if ($skipped.Count -gt 0) {
            foreach ($target in $skipped) { Write-Host "Still enabled: $target" }
            Write-Host 'Nocturne But Better was not fully uninstalled.'
            return
        }
        Write-Host 'Nocturne But Better is uninstalled. Each DLL was kept as a disabled backup.'
        Write-Host 'Mod loaders, other mods, saves, scores, and preferences were left in place.'
        return
    }

    $selectedLoader = Resolve-Loader $Loader $gameRoot
    if ($selectedLoader -eq 'MelonLoader') {
        $sourcePath = $MelonModPath; $sourceHash = $melonModHash
        $target = $melonTarget; $currentHash = $installedMelonHash
        # Only one copy may run: the BepInEx plugin is disabled when switching loaders.
        $otherTarget = $pluginTarget; $otherHash = $installedHash
        Assert-Hash $sourcePath $sourceHash
    }
    else {
        $sourcePath = $PluginPath; $sourceHash = $pluginHash
        $target = $pluginTarget; $currentHash = $installedHash
        $otherTarget = $melonTarget; $otherHash = $installedMelonHash
        Assert-Hash $LoaderArchive $loaderHash
        Assert-Hash $sourcePath $sourceHash
    }
    foreach ($relative in $gameHashes.Keys) { Assert-Hash (Join-Path $gameRoot $relative) $gameHashes[$relative] }
    $manifestPath = Get-SteamManifestPath $gameRoot
    if ($manifestPath -and [IO.File]::Exists($manifestPath)) {
        $manifest = $null
        try { $manifest = Read-SharedText $manifestPath }
        catch { Write-Host "Steam's Nocturne manifest could not be read, so its build number was not checked. The game files match build 25487568." }
        if ($null -ne $manifest -and ($manifest -notmatch '"appid"\s+"1374860"' -or $manifest -notmatch '"buildid"\s+"25487568"')) {
            throw 'Steam reports a different Nocturne build. This package supports build 25487568 only.'
        }
    }

    $copies = New-Object 'System.Collections.Generic.List[object]'
    if ($selectedLoader -eq 'BepInEx') {
        $stageBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
        $stagePath = Join-Path $stageBase ('NocturneMod-' + [Guid]::NewGuid().ToString('N'))
        [void][IO.Directory]::CreateDirectory($stagePath)
        $loaderFiles = @(Expand-VerifiedLoader ([IO.Path]::GetFullPath($LoaderArchive)) $stagePath)
        foreach ($file in $loaderFiles) {
            $loaderTarget = Join-Path $gameRoot $file.Relative
            Assert-SafeTarget $gameRoot $loaderTarget
            if (Test-Path -LiteralPath $loaderTarget -PathType Container) { throw "A folder blocks a loader file: $loaderTarget" }
            if (Test-Path -LiteralPath $loaderTarget -PathType Leaf) {
                if ($file.Relative -eq 'doorstop_config.ini') { Assert-DoorstopConfig $loaderTarget $gameRoot; continue }
                if ($file.Relative -like 'BepInEx\config\*') { continue }
                if ((Get-Sha256 $loaderTarget) -eq $file.Hash) { continue }
                # Preserve unrelated documentation, but never mix different loader/runtime binaries.
                if ($file.Relative -eq 'changelog.txt' -or $file.Relative -like '*.xml') { continue }
                throw "An incompatible loader file is already installed: $($file.Relative). Nothing was overwritten. Use a compatible BepInEx installation or a clean game folder."
            }
            $copies.Add([pscustomobject]@{ Source = $file.Source; Target = $loaderTarget; Hash = $file.Hash })
        }
    }
    Assert-SafeTarget $gameRoot $target
    if (Test-Path -LiteralPath $target -PathType Container) { throw "A folder is occupying the mod DLL destination: $target" }
    $temporaryTarget = $target + '.install-' + [Guid]::NewGuid().ToString('N')
    $backupTarget = $target + '.backup-' + [Guid]::NewGuid().ToString('N')
    $otherDisabled = $otherTarget + '.disabled-' + [Guid]::NewGuid().ToString('N')
    Assert-SafeTarget $gameRoot $temporaryTarget
    Assert-SafeTarget $gameRoot $backupTarget
    if ($otherHash) { Assert-SafeTarget $gameRoot $otherDisabled }
    if ($currentHash -and $currentHash -ne $sourceHash -and
        ((Get-Item -LiteralPath $target).Attributes -band [IO.FileAttributes]::ReadOnly)) {
        throw 'The installed mod DLL is read-only. Remove its read-only attribute before upgrading.'
    }

    Write-Host "Preflight passed for Nocturne build 25487568: $gameRoot"
    if ($selectedLoader -eq 'MelonLoader') {
        Write-Host ("Installing for MelonLoader {0}. This package was tested with MelonLoader 0.7.3." -f (Get-MelonLoaderVersion $gameRoot))
    }
    else {
        Write-Host ("Installing for BepInEx. Loader files to add: {0}. Existing compatible loader files and configurations will be preserved." -f $copies.Count)
    }
    if ($otherHash) { Write-Host "The copy for the other loader will be disabled so only one copy runs: $otherTarget" }
    if (-not $WhatIfPreference -and (Get-Process -Name Nocturne -ErrorAction SilentlyContinue)) { throw 'Nocturne started during preflight. Close it before installing.' }
    $operation = "Install BepInEx #788 where absent and Nocturne But Better $modVersion"
    if ($selectedLoader -eq 'MelonLoader') { $operation = "Install Nocturne But Better $modVersion for MelonLoader" }
    if (-not $PSCmdlet.ShouldProcess($gameRoot, $operation)) { return }
    # No game-directory writes occur above this point. -WhatIf still validates/extracts to TEMP.
    $created = New-Object 'System.Collections.Generic.List[string]'
    $movedOldCopy = $false
    $movedOtherCopy = $false
    try {
        foreach ($copy in $copies) { Copy-NewVerifiedFile $copy.Source $copy.Target $copy.Hash $created }
        if ($currentHash -ne $sourceHash) {
            Copy-NewVerifiedFile $sourcePath $temporaryTarget $sourceHash $created
            if ($currentHash) {
                [IO.File]::Move($target, $backupTarget)
                $movedOldCopy = $true
            }
            [IO.File]::Move($temporaryTarget, $target)
            [void]$created.Remove($temporaryTarget)
            $created.Add($target)
        }
        if ($otherHash) {
            [IO.File]::Move($otherTarget, $otherDisabled)
            $movedOtherCopy = $true
        }
    }
    catch {
        $failure = $_
        # Roll back only files this run created, retaining all pre-existing loader/config files.
        for ($i = $created.Count - 1; $i -ge 0; $i--) {
            Assert-SafeTarget $gameRoot $created[$i]
            if (Test-Path -LiteralPath $created[$i] -PathType Leaf) { [IO.File]::Delete($created[$i]) }
        }
        if ($movedOldCopy -and -not (Test-Path -LiteralPath $target)) { [IO.File]::Move($backupTarget, $target) }
        if ($movedOtherCopy -and -not (Test-Path -LiteralPath $otherTarget)) { [IO.File]::Move($otherDisabled, $otherTarget) }
        throw "Installation did not finish; newly copied files were rolled back. $($failure.Exception.Message)"
    }
    Write-Host "Installed Nocturne But Better $modVersion for $selectedLoader. Open Options > Gameplay and Options > Audio for its settings."
    if ($selectedLoader -eq 'BepInEx') {
        Write-Host 'The first launch can take longer while BepInEx creates game-specific files. Allow it to finish.'
    }
    else {
        Write-Host 'The first launch after installing MelonLoader can take longer while it creates game-specific files.'
    }
    if ($movedOldCopy) { Write-Host "The previous version was backed up as: $backupTarget" }
    if ($movedOtherCopy) { Write-Host "The copy for the other loader was disabled as: $otherDisabled" }
    Write-Host 'Saves, scores, preferences, other mods, and display settings were not changed.'
}
finally {
    if ($stagePath -and (Test-Path -LiteralPath $stagePath -PathType Container)) {
        $fullStage = [IO.Path]::GetFullPath($stagePath)
        $fullTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + '\'
        if (-not $fullStage.StartsWith($fullTemp, [StringComparison]::OrdinalIgnoreCase) -or
            (Split-Path -Leaf $fullStage) -notmatch '^NocturneMod-[a-f0-9]{32}$') {
            throw 'Refusing to clean up an unexpected temporary path.'
        }
        Remove-Item -LiteralPath $fullStage -Recurse -Force -WhatIf:$false -Confirm:$false
    }
}
