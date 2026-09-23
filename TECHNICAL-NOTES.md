# nocturne flat scroll 2.2.0

the package targets windows x64, steam app 1374860, nocturne 1.0.0, build 25460029, unity 2022.3.62f2. the installer pins game code, metadata, and original layout assets. it refuses unknown versions rather than assuming an update is compatible. previous experimental static layout patches must be restored before using this portable package.

## loaders

the mod ships as two dlls built from the same source:

- `payload\nocturneflatscroll.dll` is the bepinex plugin. it installs as `bepinex\plugins\nocturneflatscroll\nocturneflatscroll.dll` and uses the official bepinex 6.0.0-be.788+5b766a3 windows x64 il2cpp loader, which the package bundles unchanged. the bundled loader includes its .net runtime.
- `payload\nocturneflatscroll.melonloader.dll` is the melonloader mod. it installs as `mods\nocturneflatscroll.melonloader.dll` and needs melonloader 0.7.3 or newer, which you install yourself. melonloader uses the system .net 6 runtime and downloads it if it's missing.

the installer's `-loader` option chooses between them. `auto`, the default, uses melonloader when it will start: `melonloader\net6\melonloader.dll` exists, a melonloader proxy dll such as `version.dll` is in the game folder, and `userdata\loader.cfg` doesn't turn it off. otherwise it uses bepinex. it refuses to add bepinex to a game with an enabled melonloader, because both loaders hook the same unity startup call and only one of them starts. it also refuses melonloader older than 0.7.3. installing for one loader disables this mod's copy for the other, so only one copy runs.

game-specific interop files are generated on the recipient's pc during the first launch with either loader and are not included. a .net sdk is only needed to rebuild the source.

the optional fullscreen script applies a small, reversible byte patch to the recipient's `nocturne_data\globalgamemanagers` to prefer directx 11. it validates both the input and resulting sha-256 hashes. no original or modified game asset is distributed with this package.

## settings

options > gameplay gets two rows above speed mod.

note scrolling cycles between default, 2d downscroll, and 2d upscroll. default keeps the original track and hud. the two flat modes keep the game's artwork and vertical bars, with player meters lower left and enemy meters upper right, close to the chart.

receptor height runs from -10% to +30% in 1% steps. it moves the flat field along its own vertical axis, which faces the combat camera, so the receptors change height without changing depth or lane width. one step is 1.9527 field units, which is 1% of the screen height at the combat camera's distance of 169.11 units with its 60° field of view. positive values move the receptors in from their edge of the screen: up in downscroll, down in upscroll. in upscroll the receptors start lower, at 33% of the screen height below the top, so values above about +17% take them past the middle. the calibration and difficulty previews use a 360-unit-high canvas, so there a step is 3.6 units. left and right stop at the ends; clicking the row cycles through every value.

the settings live in unity playerprefs under `NocturneFlatScroll.Mode.v3` and `NocturneFlatScroll.ReceptorHeight.v1`. an older `NocturneFlatScroll.Direction.v2` setting is migrated when present. reset to default sets default and 0%. installing on another pc does not transfer settings or saves.

## command-line use

close nocturne first. from the extracted package folder:

```powershell
# a preview performs validation but makes no changes to the game folder.
powershell -noprofile -executionpolicy bypass -file .\install-nocturnemod.ps1 -gamepath "d:\steamlibrary\steamapps\common\nocturne" -whatif

# install, or omit gamepath to discover the steam installation.
powershell -noprofile -executionpolicy bypass -file .\install-nocturnemod.ps1 -gamepath "d:\steamlibrary\steamapps\common\nocturne"

# pick the loader instead of detecting it.
powershell -noprofile -executionpolicy bypass -file .\install-nocturnemod.ps1 -loader melonloader

# disable this mod for both loaders; keep the loaders and other mods.
powershell -noprofile -executionpolicy bypass -file .\install-nocturnemod.ps1 -action uninstall -gamepath "d:\steamlibrary\steamapps\common\nocturne"

# optional display patch; use -action restore to reverse it.
powershell -noprofile -executionpolicy bypass -file .\set-nocturnefullscreenfix.ps1 -action enable -gamepath "d:\steamlibrary\steamapps\common\nocturne"
```

`install.cmd` and `uninstall.cmd` pass extra arguments through, so `install.cmd -loader bepinex` also works.

if the folder is protected, open powershell as administrator and rerun the same command. existing compatible loader files and settings are preserved; conflicting loader binaries stop the install. uninstall disables only recognized copies of this mod and keeps saved preferences. each disabled or replaced dll is kept beside the original with a `.disabled-` or `.backup-` suffix.

## rebuilding

launch the supported game once with the loader you want to build for, so it generates the game's interop assemblies. install a .net sdk that can target .net 6, then run one of:

```powershell
# bepinex plugin (default)
dotnet build .\source\nocturneflatscroll.csproj -c Release -p:bepdir="d:\steamlibrary\steamapps\common\nocturne\bepinex"

# melonloader mod
dotnet build .\source\nocturneflatscroll.csproj -c Release -p:loader=melonloader -p:melondir="d:\steamlibrary\steamapps\common\nocturne\melonloader"
```

the output goes to `source\bin\<loader>\Release\net6.0`. keep `Release` capitalized: the dll records the configuration name, so `-c release` builds a working dll with a different hash. built this way, inside or outside a git checkout, both dlls match the packaged ones byte for byte. the project references local loader and game-generated interop assemblies; neither the game nor those generated assemblies are in the source folder. melonloader prefixes the game's namespaces with `il2cpp`, so `source\globalusings.cs` maps the names for each loader. the installer accepts only the packaged dll hashes, so a custom build needs a matching installer hash update or a manual copy.

## scope of validation

version 2.2.0 was tested in-game on bepinex 6.0.0-be.788 and melonloader 0.7.3, the melonloader one in a separate copy of the game folder. on both, the options rows, their limits, hold-to-repeat, reset to default, and persistence between the two loaders were checked. a qa-only build measured the receptor line through the combat camera at the firefly battle ready screen for heights -10, 0, 5, 10, and 30. as a fraction of the screen height above the bottom edge, it sat at:

- downscroll: 0.2006 + 0.01 × height
- upscroll: 0.6662 - 0.01 × height

default mode restored the original perspective track, and the latency calibration preview moved by 20% at +20.

full-battle note hits, holds, meter animation, and a five-lane visual test have not been verified. the fullscreen fix resolved flicker on the original test pc; results on other display and gpu combinations can differ.

package checks cover windows powershell parsing and isolated installation, upgrade, loader switching, reinstallation, removal, incompatible-file rejection, and reversible display patching. see package-verification.txt for the completed checks.

## integrity

bepinex plugin sha-256:
`0b1d12f92561f9f33b4f081bc977419bdfce85b7cdfc76de0804ff0a6f690599`

melonloader mod sha-256:
`cfb3afa7c2ca1590d6f3abef40a3034fdbb4d69f1d61a153b7ab26c678918921`

both dlls are compiled from the same 2.2.0 source with debug symbols disabled, so they contain no development-machine pdb path. the installer also recognizes earlier 2.1.x bepinex builds when upgrading or uninstalling.

official loader archive sha-256:
`f4cc496bd098a0df4164b81e3737297707f13a47c2478dba2f60eefab784817a`

`sha256sums.txt` lists every package file except itself. it checks accidental corruption, not authorship: an unsigned package and its checksum list could both be changed. review the scripts and source if you need to verify their behavior.
