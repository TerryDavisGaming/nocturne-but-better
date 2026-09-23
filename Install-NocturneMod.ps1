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

$modVersion = '2.3.0'
$loaderHash = 'F4CC496BD098A0DF4164B81E3737297707F13A47C2478DBA2F60EEFAB784817A'
$pluginHash = '40ED8E9A39C073D9024668B065CFECBA90E4CEB0112833F3102C5D902AA618AC'
$knownPluginHashes = @(
    $pluginHash,
    '0B1D12F92561F9F33B4F081BC977419BDFCE85B7CDFC76DE0804FF0A6F690599',
    '212DB106C6761E23413703B464C94ED26CD6982BAFAE771F1ED7D4287BA83A39',
    '6E75984D4F6AB5F6D33F4031A53DD3E0B80E5BBD70529B429F9EEDA14D79BC4E',
    '43EA6E714435C6F376083A159F8566C712CA9C52B43F91E186909C7F9399A295',
    '5CEDADBF931553A7614B92EB0D5694B99F300135979CF997CEB01E597960657D',
    'C6025C68612A64B5EFD897C1495C6DF1A31B3389C0EDF531E99AFF0931E51E1C'
)
$melonModHash = '19DE2BBC86CE71DE8CC48BD06765255A5FC83F4ED34E7CED2619E42EF6C428C7'
$knownMelonModHashes = @($melonModHash, 'CFB3AFA7C2CA1590D6F3ABEF40A3034FDBB4D69F1D61A153B7AB26C678918921')
$gameHashes = @{
    'GameAssembly.dll' = 'FD4D5879A71CE00CA3FC3C3D176FFB3F146E9CEC52DD6A06807CF564C6542940'
    'Nocturne_Data\il2cpp_data\Metadata\global-metadata.dat' = '3BB22E4F33C103F6612AD5988C87528BD46BB23142CD733D22375CBE84837054'
    'Nocturne_Data\level2' = '29212ADA5708354D27BE23276E465CBB9CFFFD2FFE96240E192805A7E667A276'
    'Nocturne_Data\sharedassets2.assets' = '55DE80734B5FEAB4FBE269E0126C62C002205752A8BC3144843662947B51E213'
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

function Find-SteamGames {
    $roots = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($location in @(
        @('HKCU:\Software\Valve\Steam', 'SteamPath'),
        @('HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'InstallPath'),
        @('HKLM:\SOFTWARE\Valve\Steam', 'InstallPath')
    )) {
        $item = Get-ItemProperty -LiteralPath $location[0] -ErrorAction SilentlyContinue
        if ($item) {
            $property = $item.PSObject.Properties[$location[1]]
            if ($property -and $property.Value) { [void]$roots.Add([string]$property.Value) }
        }
    }
    if (${env:ProgramFiles(x86)}) { [void]$roots.Add((Join-Path ${env:ProgramFiles(x86)} 'Steam')) }
    $libraries = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($root in $roots) {
        [void]$libraries.Add($root)
        $vdf = Join-Path $root 'steamapps\libraryfolders.vdf'
        if (-not (Test-Path -LiteralPath $vdf -PathType Leaf)) { continue }
        $contents = Get-Content -LiteralPath $vdf -Raw
        # Current VDF uses path properties; older Steam versions used numbered values.
        foreach ($match in [regex]::Matches($contents, '"(?:path|\d+)"\s+"((?:\\.|[^"\\])*)"')) {
            $library = ConvertFrom-VdfPath $match.Groups[1].Value
            if ([IO.Path]::IsPathRooted($library)) { [void]$libraries.Add($library) }
        }
    }
    $candidates = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($library in $libraries) {
        $apps = Join-Path $library 'steamapps'
        $manifest = Join-Path $apps 'appmanifest_1374860.acf'
        $installName = 'Nocturne'
        if (Test-Path -LiteralPath $manifest -PathType Leaf) {
            $contents = Get-Content -LiteralPath $manifest -Raw
            $match = [regex]::Match($contents, '"installdir"\s+"((?:\\.|[^"\\])*)"')
            if ($match.Success) { $installName = ConvertFrom-VdfPath $match.Groups[1].Value }
        }
        # Never interpret an untrusted manifest's install folder as an absolute/traversal path.
        if ($installName -match '[\\/:]' -or $installName -in @('.', '..')) { continue }
        $candidate = Join-Path (Join-Path $apps 'common') $installName
        if (Test-Path -LiteralPath (Join-Path $candidate 'Nocturne.exe') -PathType Leaf) {
            [void]$candidates.Add([IO.Path]::GetFullPath($candidate))
        }
    }
    foreach ($candidate in $candidates) { Write-Output $candidate }
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
    if (-not $RequestedPath) { throw 'A Nocturne game folder is required.' }
    $resolved = [IO.Path]::GetFullPath($RequestedPath.Trim().Trim('"'))
    if (-not (Test-Path -LiteralPath (Join-Path $resolved 'Nocturne.exe') -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $resolved 'Nocturne_Data') -PathType Container)) {
        throw "This is not the Nocturne game folder: $resolved"
    }
    return $resolved.TrimEnd('\', '/')
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
        if ($enabled.Count -eq 0) { Write-Host 'Nocturne Flat Scroll is already uninstalled.'; return }
        Write-Host "Verified Nocturne Flat Scroll in: $gameRoot"
        $skipped = @()
        foreach ($target in $enabled) {
            $disabledPath = $target + '.disabled-' + [Guid]::NewGuid().ToString('N')
            Assert-SafeTarget $gameRoot $disabledPath
            if ($PSCmdlet.ShouldProcess($target, 'Disable this verified Nocturne Flat Scroll copy')) {
                [IO.File]::Move($target, $disabledPath)
                Write-Host "Disabled: $target"
            }
            else { $skipped += $target }
        }
        if ($WhatIfPreference) { return }
        if ($skipped.Count -gt 0) {
            foreach ($target in $skipped) { Write-Host "Still enabled: $target" }
            Write-Host 'Nocturne Flat Scroll was not fully uninstalled.'
            return
        }
        Write-Host 'Nocturne Flat Scroll is uninstalled. Each DLL was kept as a disabled backup.'
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
    $steamApps = Split-Path -Parent (Split-Path -Parent $gameRoot)
    $manifestPath = Join-Path $steamApps 'appmanifest_1374860.acf'
    if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw
        if ($manifest -notmatch '"appid"\s+"1374860"' -or $manifest -notmatch '"buildid"\s+"25460029"') {
            throw 'Steam reports a different Nocturne build. This package supports build 25460029 only.'
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

    Write-Host "Preflight passed for Nocturne build 25460029: $gameRoot"
    if ($selectedLoader -eq 'MelonLoader') {
        Write-Host ("Installing for MelonLoader {0}. This package was tested with MelonLoader 0.7.3." -f (Get-MelonLoaderVersion $gameRoot))
    }
    else {
        Write-Host ("Installing for BepInEx. Loader files to add: {0}. Existing compatible loader files and configurations will be preserved." -f $copies.Count)
    }
    if ($otherHash) { Write-Host "The copy for the other loader will be disabled so only one copy runs: $otherTarget" }
    if (-not $WhatIfPreference -and (Get-Process -Name Nocturne -ErrorAction SilentlyContinue)) { throw 'Nocturne started during preflight. Close it before installing.' }
    $operation = "Install BepInEx #788 where absent and Nocturne Flat Scroll $modVersion"
    if ($selectedLoader -eq 'MelonLoader') { $operation = "Install Nocturne Flat Scroll $modVersion for MelonLoader" }
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
    Write-Host "Installed Nocturne Flat Scroll $modVersion for $selectedLoader. Open Options > Gameplay for Note scrolling, Receptor height, Note skin, and Timing bar."
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
