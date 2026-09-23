# Nocturne Flat Scroll 2.1.2

The package targets Windows x64, Steam app **1374860**, Nocturne **1.0.0**,
build **25460029**, Unity **2022.3.62f2**. The installer pins game code,
metadata, and original layout assets. It refuses unknown versions rather
than assuming an update is compatible. Previous experimental static layout
patches must be restored before using this portable package.

The scrolling mod is installed as
`BepInEx/plugins/NocturneFlatScroll/NocturneFlatScroll.dll`. It uses the
official **BepInEx 6.0.0-be.788+5b766a3** Windows x64 IL2CPP loader. The
bundled loader archive is unchanged. Game-specific interop files are
generated on the recipient's PC during first launch and are not included.
The bundled loader includes its .NET runtime; a separate .NET SDK is only
needed to rebuild the source.

The optional fullscreen script applies a small, reversible byte patch to
the recipient's `Nocturne_Data/globalgamemanagers` to prefer DirectX 11. It
validates both the input and resulting SHA-256 hashes. No original or
modified game asset is distributed with this package.

## Settings

**Options > Gameplay > Note scrolling** cycles between Default, 2D
Downscroll, and 2D Upscroll. Default retains the original track and HUD.
The two flat modes preserve the game's artwork and vertical bars. The
player meters sit lower left, enemy meters upper right, close to the chart.
The setting is stored in `NocturneFlatScroll.Mode.v3` in Unity PlayerPrefs.
An older `NocturneFlatScroll.Direction.v2` setting is migrated when present.
Installing on another PC does not transfer settings or saves.

## Command-line use

Close Nocturne first. From the extracted package folder:

```powershell
# A preview performs validation but makes no changes to the game folder.
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install-NocturneMod.ps1 -GamePath "D:\SteamLibrary\steamapps\common\Nocturne" -WhatIf

# Install, or omit GamePath to discover the Steam installation.
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install-NocturneMod.ps1 -GamePath "D:\SteamLibrary\steamapps\common\Nocturne"

# Disable this mod; preserve the shared loader and other plugins.
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install-NocturneMod.ps1 -Action Uninstall -GamePath "D:\SteamLibrary\steamapps\common\Nocturne"

# Optional display patch; use -Action Restore to reverse it.
powershell -NoProfile -ExecutionPolicy Bypass -File .\Set-NocturneFullscreenFix.ps1 -Action Enable -GamePath "D:\SteamLibrary\steamapps\common\Nocturne"
```

If the folder is protected, open PowerShell as administrator and rerun the
same command. Existing compatible loader files and settings are preserved;
conflicting loader binaries cause the install to stop. Uninstall disables
only a recognized copy of this plugin and keeps saved preferences.

## Rebuilding

Install the loader and launch the supported game once to generate
`BepInEx/interop`. Install a .NET SDK that can target .NET 6, then run:

```powershell
dotnet build .\source\NocturneFlatScroll.csproj -c Release -p:BepDir="D:\SteamLibrary\steamapps\common\Nocturne\BepInEx"
```

The project references local loader and game-generated interop assemblies;
neither the game nor those generated assemblies are included in the source
folder. The installer accepts the packaged DLL hash. A custom rebuild will
need a corresponding installer hash update or a manual plugin replacement.

## Scope of validation

All three options and setting persistence were checked in-game. The latest
compact bar placement was visually checked in 2D Upscroll at the Firefly
battle ready screen. Both 2D modes share the bar geometry. Full-battle note
hits, holds, meter animation, and a five-lane visual test have not been
verified. The fullscreen fix resolved flicker on the original test PC;
results on other display/GPU combinations can differ.

Package checks cover Windows PowerShell parsing and isolated installation,
reinstallation, removal, incompatible-file rejection, and reversible
display patching. See PACKAGE-VERIFICATION.txt for the completed checks.

## Integrity

Plugin SHA-256:
`212DB106C6761E23413703B464C94ED26CD6982BAFAE771F1ED7D4287BA83A39`

The packaged DLL is compiled from the same 2.1.2 source with debug symbols
disabled, so it contains no development-machine PDB path. Its hash differs
from the original local build. The installer recognizes both builds when
upgrading or uninstalling.

Official loader archive SHA-256:
`F4CC496BD098A0DF4164B81E3737297707F13A47C2478DBA2F60EEFAB784817A`

`SHA256SUMS.txt` lists every package file except itself. It checks accidental
corruption, not authorship: an unsigned package and its checksum list could
both be changed. Review scripts/source if you need to verify their behavior.
