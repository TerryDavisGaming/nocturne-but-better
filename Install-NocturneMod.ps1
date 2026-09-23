#Requires -Version 5.1
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [ValidateSet('Install', 'Uninstall')][string]$Action = 'Install',
    [string]$GamePath,
    [string]$LoaderArchive,
    [string]$PluginPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$packageRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $LoaderArchive) { $LoaderArchive = Join-Path $packageRoot 'payload\BepInEx-IL2CPP-x64-788.zip' }
if (-not $PluginPath) { $PluginPath = Join-Path $packageRoot 'payload\NocturneFlatScroll.dll' }

$loaderHash = 'F4CC496BD098A0DF4164B81E3737297707F13A47C2478DBA2F60EEFAB784817A'
$pluginHash = '212DB106C6761E23413703B464C94ED26CD6982BAFAE771F1ED7D4287BA83A39'
$knownPluginHashes = @(
    $pluginHash,
    '6E75984D4F6AB5F6D33F4031A53DD3E0B80E5BBD70529B429F9EEDA14D79BC4E',
    '43EA6E714435C6F376083A159F8566C712CA9C52B43F91E186909C7F9399A295',
    '5CEDADBF931553A7614B92EB0D5694B99F300135979CF997CEB01E597960657D',
    'C6025C68612A64B5EFD897C1495C6DF1A31B3389C0EDF531E99AFF0931E51E1C'
)
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

# Dot-sourcing exposes the helpers for read-only discovery and isolated fixture tests.
# It does not install or uninstall anything.
if ($MyInvocation.InvocationName -eq '.') { return }

$stagePath = $null
try {
    if (-not $WhatIfPreference -and (Get-Process -Name Nocturne -ErrorAction SilentlyContinue)) { throw 'Close Nocturne before running this installer.' }
    $gameRoot = Resolve-GameRoot $GamePath
    Assert-SafeTarget $gameRoot $gameRoot
    $pluginTarget = Join-Path $gameRoot 'BepInEx\plugins\NocturneFlatScroll\NocturneFlatScroll.dll'
    Assert-SafeTarget $gameRoot $pluginTarget
    if (Test-Path -LiteralPath $pluginTarget -PathType Container) { throw 'A folder is occupying the plugin DLL destination.' }
    $installedHash = $null
    if (Test-Path -LiteralPath $pluginTarget -PathType Leaf) {
        $installedHash = Get-Sha256 $pluginTarget
        if ($installedHash -notin $knownPluginHashes) { throw 'An unrecognized NocturneFlatScroll plugin is installed. Nothing was overwritten or disabled.' }
    }

    if ($Action -eq 'Uninstall') {
        # Removing this known plugin remains possible after Steam updates the game.
        if (-not $installedHash) { Write-Host 'Nocturne Flat Scroll is already uninstalled.'; return }
        $disabledPath = $pluginTarget + '.disabled-' + [Guid]::NewGuid().ToString('N')
        Assert-SafeTarget $gameRoot $disabledPath
        Write-Host "Verified Nocturne Flat Scroll in: $gameRoot"
        if ($PSCmdlet.ShouldProcess($pluginTarget, 'Disable only the verified Nocturne Flat Scroll plugin')) {
            [IO.File]::Move($pluginTarget, $disabledPath)
            Write-Host 'Nocturne Flat Scroll is uninstalled. Its DLL was kept as a disabled backup.'
            Write-Host 'BepInEx, other mods, saves, scores, and preferences were left in place.'
        }
        return
    }

    Assert-Hash $LoaderArchive $loaderHash
    Assert-Hash $PluginPath $pluginHash
    foreach ($relative in $gameHashes.Keys) { Assert-Hash (Join-Path $gameRoot $relative) $gameHashes[$relative] }
    $steamApps = Split-Path -Parent (Split-Path -Parent $gameRoot)
    $manifestPath = Join-Path $steamApps 'appmanifest_1374860.acf'
    if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw
        if ($manifest -notmatch '"appid"\s+"1374860"' -or $manifest -notmatch '"buildid"\s+"25460029"') {
            throw 'Steam reports a different Nocturne build. This package supports build 25460029 only.'
        }
    }

    $stageBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
    $stagePath = Join-Path $stageBase ('NocturneMod-' + [Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($stagePath)
    $loaderFiles = @(Expand-VerifiedLoader ([IO.Path]::GetFullPath($LoaderArchive)) $stagePath)
    $copies = New-Object 'System.Collections.Generic.List[object]'
    foreach ($file in $loaderFiles) {
        $target = Join-Path $gameRoot $file.Relative
        Assert-SafeTarget $gameRoot $target
        if (Test-Path -LiteralPath $target -PathType Container) { throw "A folder blocks a loader file: $target" }
        if (Test-Path -LiteralPath $target -PathType Leaf) {
            if ($file.Relative -eq 'doorstop_config.ini') { Assert-DoorstopConfig $target $gameRoot; continue }
            if ($file.Relative -like 'BepInEx\config\*') { continue }
            if ((Get-Sha256 $target) -eq $file.Hash) { continue }
            # Preserve unrelated documentation, but never mix different loader/runtime binaries.
            if ($file.Relative -eq 'changelog.txt' -or $file.Relative -like '*.xml') { continue }
            throw "An incompatible loader file is already installed: $($file.Relative). Nothing was overwritten. Use a compatible BepInEx installation or a clean game folder."
        }
        $copies.Add([pscustomobject]@{ Source = $file.Source; Target = $target; Hash = $file.Hash })
    }
    $temporaryPlugin = $pluginTarget + '.install-' + [Guid]::NewGuid().ToString('N')
    $backupPlugin = $pluginTarget + '.backup-' + [Guid]::NewGuid().ToString('N')
    Assert-SafeTarget $gameRoot $temporaryPlugin
    Assert-SafeTarget $gameRoot $backupPlugin
    if ($installedHash -and $installedHash -ne $pluginHash -and
        ((Get-Item -LiteralPath $pluginTarget).Attributes -band [IO.FileAttributes]::ReadOnly)) {
        throw 'The installed plugin is read-only. Remove its read-only attribute before upgrading.'
    }

    Write-Host "Preflight passed for Nocturne build 25460029: $gameRoot"
    Write-Host ("Loader files to add: {0}. Existing compatible loader files and configurations will be preserved." -f $copies.Count)
    if (-not $WhatIfPreference -and (Get-Process -Name Nocturne -ErrorAction SilentlyContinue)) { throw 'Nocturne started during preflight. Close it before installing.' }
    if (-not $PSCmdlet.ShouldProcess($gameRoot, 'Install BepInEx #788 where absent and Nocturne Flat Scroll 2.1.2')) { return }
    # No game-directory writes occur above this point. -WhatIf still validates/extracts to TEMP.
    $created = New-Object 'System.Collections.Generic.List[string]'
    $movedOldPlugin = $false
    try {
        foreach ($copy in $copies) { Copy-NewVerifiedFile $copy.Source $copy.Target $copy.Hash $created }
        if ($installedHash -ne $pluginHash) {
            Copy-NewVerifiedFile $PluginPath $temporaryPlugin $pluginHash $created
            if ($installedHash) {
                [IO.File]::Move($pluginTarget, $backupPlugin)
                $movedOldPlugin = $true
            }
            [IO.File]::Move($temporaryPlugin, $pluginTarget)
            [void]$created.Remove($temporaryPlugin)
            $created.Add($pluginTarget)
        }
    }
    catch {
        $failure = $_
        # Roll back only files this run created, retaining all pre-existing loader/config files.
        for ($i = $created.Count - 1; $i -ge 0; $i--) {
            Assert-SafeTarget $gameRoot $created[$i]
            if (Test-Path -LiteralPath $created[$i] -PathType Leaf) { [IO.File]::Delete($created[$i]) }
        }
        if ($movedOldPlugin -and -not (Test-Path -LiteralPath $pluginTarget)) { [IO.File]::Move($backupPlugin, $pluginTarget) }
        throw "Installation did not finish; newly copied files were rolled back. $($failure.Exception.Message)"
    }
    Write-Host 'Installed Nocturne Flat Scroll 2.1.2. Open Options > Gameplay > Note scrolling.'
    Write-Host 'The first launch can take longer while BepInEx creates game-specific files. Allow it to finish.'
    if ($movedOldPlugin) { Write-Host "The previous plugin was backed up as: $backupPlugin" }
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
