# nocturne flat scroll

flat upscroll and downscroll for nocturne, with compact vertical health and energy bars beside the chart and a receptor height you can adjust. you can switch back to the game's original layout from the same menu. it runs on bepinex or melonloader.

[download for windows](https://github.com/TerryDavisGaming/nocturne-flat-scroll/releases/download/v2.2.0/Nocturne-Flat-Scroll-2.2.0-Windows.zip) · [latest release](https://github.com/TerryDavisGaming/nocturne-flat-scroll/releases/latest)

use the release zip to install. github's source download does not include the plugin or loader payload.

## settings

open options > gameplay. the mod adds two rows above speed mod.

note scrolling:

- default keeps the original perspective track and hud.
- 2d downscroll uses a flat track with notes moving downward.
- 2d upscroll uses a flat track with notes moving upward.

receptor height moves the receptors in from their edge of the screen in both 2d modes. it goes from -10% to +30% of the screen height in 1% steps, and 0% is the original spot. a positive value raises the receptors in downscroll and lowers them in upscroll, so the same number works in both directions. negative values move them closer to the edge. the latency calibration and difficulty previews move with it. the setting has no effect in default mode.

press left or right to change a value, and hold to keep changing it. both settings are saved on that pc. reset to default sets them back to default and 0%. upgrading from an earlier version keeps your saved scrolling direction.

both flat modes keep the game's artwork, icons, and vertical meter text. player meters sit lower left and enemy meters upper right, each pair close to the chart.

## install

1. install nocturne through steam, then close the game.
2. download the release zip and extract the whole folder. don't run the installer from inside the zip.
3. double-click `install.cmd`. it finds the steam installation automatically. if it asks for a path, use steam's "browse local files" option and select the folder containing `nocturne.exe`.
4. launch through steam and open options > gameplay. the first launch can take longer and may need internet access while the loader prepares files.

the installer picks the loader for you:

- if melonloader 0.7.3 or newer is installed and turned on, the mod goes into the `mods` folder. the zip doesn't include melonloader.
- otherwise it adds the bundled bepinex 6 loader (build 788) where it's missing and puts the plugin in `bepinex\plugins`.

only one copy of the mod runs at a time. if you switch loaders, run `install.cmd` again and it disables the copy for the other loader. to choose yourself, run `install.cmd -loader bepinex` or `install.cmd -loader melonloader` from a command prompt.

don't keep bepinex and melonloader in the same game folder. both hook the same startup call, so only one of them starts, and it's usually melonloader. the installer won't add bepinex to a game where melonloader is turned on.

melonloader users can also install by hand: copy `nocturneflatscroll.melonloader.dll` from the release into the game's `mods` folder.

this package supports windows x64, steam nocturne 1.0.0, build 25460029. the installer checks the game files and refuses unknown builds or conflicting loader files. if access is denied, run the installer as administrator. a steam update needs another compatibility check.

## optional fullscreen flicker fix

for flickering black bars, close the game and run `enable-fullscreen-fix.cmd`. this applies a reversible directx 11 preference. run `restore-fullscreen-fix.cmd` to undo it. the scrolling mod works independently of this fix.

the fix stopped flickering on the original test pc; other display and gpu combinations haven't been verified.

## remove it

close the game and run `uninstall.cmd`. this disables the mod for both loaders and keeps the loaders, other mods, saved preferences, saves, and scores. restore the fullscreen fix separately if you enabled it.

## what was tested

version 2.2.0 was tested in-game on bepinex 6.0.0-be.788 and on melonloader 0.7.3. on both loaders:

- the two rows appeared above speed mod, left and right stopped at -10% and +30%, holding a direction kept changing the value, and reset to default went back to default and 0%.
- at the firefly battle ready screen, downscroll receptors sat 20% of the screen height above the bottom at 0% and 50% at +30%. upscroll receptors moved from 67% to 37% above the bottom. every 1% step moved them by 1% of the screen height.
- default mode still restored the original track and hud.
- the latency calibration preview moved by the chosen amount.

the melonloader test ran in a separate copy of the game folder. installer checks covered both loaders, switching between them, upgrades from 2.1.2, and removal, all in test copies of the game files.

full-battle hits, holds, meter fill animation, and a five-lane visual test remain untested.

## source and licenses

see [technical notes](TECHNICAL-NOTES.md) for build instructions for both loaders and compatibility details. the package contains no game assets, game-generated assemblies, saves, or account data. you need your own installed copy of nocturne.

this is an unofficial community mod. see the [mod license](LICENSE-MOD.txt) and [third-party notices](THIRD-PARTY-NOTICES.txt) for licensing.
