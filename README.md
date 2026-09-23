# nocturne flat scroll

flat upscroll and downscroll for nocturne, with compact vertical health and energy bars beside the chart. you can switch back to the game's original layout from the same menu.

[DOWNLOAD FOR WINDOWS](https://github.com/TerryDavisGaming/nocturne-flat-scroll/releases/download/v2.1.2/Nocturne-Flat-Scroll-2.1.2-Windows.zip) · [latest release](https://github.com/TerryDavisGaming/nocturne-flat-scroll/releases/latest)

use the release zip to install. github's source download does not include the plugin or loader payload.

## the three modes

open options > gameplay > note scrolling:

- default keeps the original perspective track and hud.
- 2d downscroll uses a flat track with notes moving downward.
- 2d upscroll uses a flat track with notes moving upward.

both flat modes keep the game's artwork, icons, and vertical meter text. player meters sit lower left; enemy meters sit upper right. the latest layout puts each pair close together and close to the chart.

fresh installs start on default, and reset defaults returns to it. your choice is saved on that pc. upgrading from an earlier version preserves your saved scrolling direction.

## install

1. install nocturne through steam, then CLOSE THE GAME.
2. download the release zip and extract the whole folder. don't run the installer from inside the zip.
3. double-click `install.cmd`. it finds the steam installation automatically. if it asks for a path, use steam's “browse local files” option and select the folder containing `nocturne.exe`.
4. launch through steam and choose a mode under options > gameplay > note scrolling. the first launch can take longer and may need internet access while the loader prepares files.

this package supports windows x64, steam nocturne 1.0.0, build 25460029. the installer checks the game files and refuses unknown builds or conflicting loader files. if access is denied, run the installer as administrator. a steam update needs another compatibility check.

## optional fullscreen flicker fix

for flickering black bars, close the game and run `enable-fullscreen-fix.cmd`. this applies a reversible directx 11 preference. run `restore-fullscreen-fix.cmd` to undo it. the scrolling mod works independently of this fix.

the fix stopped flickering on the original test pc; other display and gpu combinations haven't been verified.

## remove it

close the game and run `uninstall.cmd`. this disables the scrolling plugin and keeps the shared loader, other mods, saved preferences, saves, and scores. restore the fullscreen fix separately if you enabled it.

## what was tested

all three menu choices and saved selection were checked in-game. v2.1.2's compact hud was visually checked in upscroll at the firefly battle ready screen. both flat modes share that hud geometry, but the latest downscroll layout wasn't separately checked.

full-battle hits, holds, meter fill animation, and a five-lane visual test remain untested.

## source and licenses

see [technical notes](TECHNICAL-NOTES.md) for source build instructions and compatibility details. the package contains no game assets, game-generated assemblies, saves, or account data. you need your own installed copy of nocturne.

this is an unofficial community mod. see the [mod license](LICENSE-MOD.txt) and [third-party notices](THIRD-PARTY-NOTICES.txt) for licensing.
