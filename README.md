# nocturne flat scroll

flat upscroll and downscroll for nocturne, with compact vertical health and energy bars beside the chart and a receptor height you can adjust. it also has circle and arrow note skins, an early/late timing bar like the one in osu!mania, and a "but better" under the nocturne logo. you can switch back to the game's original look from the same menu. it runs on bepinex or melonloader.

[download for windows](https://github.com/TerryDavisGaming/nocturne-flat-scroll/releases/download/v2.3.0/Nocturne-Flat-Scroll-2.3.0-Windows.zip) · [latest release](https://github.com/TerryDavisGaming/nocturne-flat-scroll/releases/latest)

use the release zip to install. github's source download does not include the plugin or loader payload.

## settings

open options > gameplay. the mod adds four rows above speed mod.

note scrolling:

- default keeps the original perspective track and hud.
- 2d downscroll uses a flat track with notes moving downward.
- 2d upscroll uses a flat track with notes moving upward.

receptor height moves the receptors in from their edge of the screen in both 2d modes. it goes from -10% to +30% of the screen height in 1% steps, and 0% is the original spot. a positive value raises the receptors in downscroll and lowers them in upscroll, so the same number works in both directions. negative values move them closer to the edge. the latency calibration and difficulty previews move with it. the setting has no effect in default mode.

note skin:

- default keeps the game's bar notes.
- circle draws round notes and receptors.
- arrow draws arrows that point left, down, up, and right, like a dance game. a five-lane chart gets a diamond in the middle lane.

the skins use the same colors the game gives its own notes, and the receptors still flash when you hit. hold notes get a narrower trail to match. the latency calibration preview in options uses the skin too. both skins work in all three scrolling modes, and in 2d upscroll the arrows point the same way on screen as they do in downscroll.

timing bar shows how early or late each hit was, and it starts off. the bar sits just behind the receptors, so it moves with them: at the bottom in default and 2d downscroll, at the top in 2d upscroll, and along with the receptor height.

early hits land on the rabbit's side on the left and late hits on the turtle's side on the right. the colored bands are the game's okay, good, great, and perfect windows, each tick takes the color of its judgement, and the white arrow follows your recent average. only taps and the starts of holds count, so misses, auto-played lanes, and hold releases are left off. in 2d upscroll the enemy can cover the middle of the bar, because the game draws the fighters on top of the whole chart.

press left or right to change a value, and hold to keep changing it. all four settings are saved on that pc. reset to default sets them back to default, 0%, default, and off. upgrading from an earlier version keeps your saved settings.

both flat modes keep the game's artwork, icons, and vertical meter text. player meters sit lower left and enemy meters upper right, each pair close to the chart.

the title screen and the nocturne card in the startup intro read "nocturne but better, by terrydavisgaming". the extra lines fade in and out with the logo.

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

version 2.3.0 was tested in-game on bepinex 6.0.0-be.788 and on melonloader 0.7.3. the melonloader test ran in a separate copy of the game folder. on both loaders, a test build played the firefly battle with the game's auto-play on every lane and stepped through the settings:

- circle and arrow skins in default, 2d downscroll, and 2d upscroll, at receptor heights -10%, 0%, and +25%, with holds and receptor flashes. switching back to the default skin brought back the game's own notes, receptors, and flashes.
- the timing bar at the bottom in default and 2d downscroll and at the top in 2d upscroll. auto-played hits are left off the bar on purpose, so the test fed it made-up early and late hits. the game's hit hook reported every judgement with the fields the bar reads.
- "but better" and the credit line on the title screen and the intro card, fading with the logo.

on bepinex, options > gameplay showed the four rows above speed mod with working up and down navigation, and reset to default put all four back. the latency calibration preview showed the arrow skin. the receptor position measurements from 2.2.0 still apply.

installer checks covered both loaders, switching between them, upgrades from 2.1.2 and 2.2.0, and removal, all in test copies of the game files.

the timing bar hasn't been checked against real key presses yet, and the skins haven't been tried on a five-lane chart.

## source and licenses

see [technical notes](TECHNICAL-NOTES.md) for build instructions for both loaders and compatibility details. the package contains no game assets, game-generated assemblies, saves, or account data. you need your own installed copy of nocturne.

this is an unofficial community mod. see the [mod license](LICENSE-MOD.txt) and [third-party notices](THIRD-PARTY-NOTICES.txt) for licensing.
