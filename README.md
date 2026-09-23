# nocturne but better

flat upscroll and downscroll for nocturne, with compact vertical health and energy bars beside the chart. in the flat modes you can change the receptor height, the note size, and the lane spacing. it also has circle and arrow note skins, an early/late timing bar like the one in osu!mania, an optional hit sound, a miss sound volume that goes up to 300%, see-through enemy attacks, a switch for note flares, a preview of the game's note colors next to the red of a mine, and a "but better" under the nocturne logo. alt, tab, and the windows key no longer start a battle by accident. you can switch back to the game's original look from the same menu.

it works on the base steam game, with melonloader, or with bepinex. YOU DON'T NEED A MOD LOADER: on a plain steam install, `install.cmd` sets up the bepinex loader that comes in the zip, so there's nothing else to download. if you already use melonloader 0.7.3 or newer, the mod goes into its `mods` folder instead.

it started out as nocturne flat scroll. the download and its files still use that name, so upgrades from older versions keep working.

[download for windows](https://github.com/TerryDavisGaming/nocturne-but-better/releases/download/v2.4.0/Nocturne-Flat-Scroll-2.4.0-Windows.zip) · [latest release](https://github.com/TerryDavisGaming/nocturne-but-better/releases/latest)

use the release zip to install. github's source download does not include the plugin or loader payload.

## settings

open options > gameplay. the mod adds nine rows above speed mod. the hit sound and miss sound settings are in options > audio, under sound effects.

note scrolling:

- default keeps the original perspective track and hud.
- 2d downscroll uses a flat track with notes moving downward.
- 2d upscroll uses a flat track with notes moving upward.

receptor height moves the receptors in from their edge of the screen in both 2d modes. it goes from -10% to +30% of the screen height in 1% steps, and 0% is the original spot. a positive value raises the receptors in downscroll and lowers them in upscroll, so the same number works in both directions. negative values move them closer to the edge. the latency calibration and difficulty previews move with it. the setting has no effect in default mode.

note size makes the notes and receptors in both 2d modes smaller or larger, from 50% to 150% in 5% steps. hold notes, the hit flashes, and the key and auto labels under each receptor follow along. lane spacing moves the 2d lanes closer together or further apart, from 60% to 150%. the lane backgrounds narrow with smaller notes or closer lanes, so neighbouring lanes keep a gap, and the health and energy meters stay next to the outer lanes. very large notes on close lanes can overlap their neighbours; that's left to you. both settings leave default mode alone. the options previews show them too, with their spacing capped at 110% because there's no more room beside them.

note skin:

- default keeps the game's bar notes.
- circle draws round notes and receptors.
- arrow draws arrows that point left, down, up, and right, like a dance game. a five-lane chart gets a diamond in the middle lane.

the skins use the same colors the game gives its own notes, and the receptors still flash when you hit. hold notes get a narrower trail to match. the latency calibration preview in options uses the skin too. both skins work in all three scrolling modes, and in 2d upscroll the arrows point the same way on screen as they do in downscroll.

note flares turns off the burst that plays on a receptor when you hit a note or hold one. it starts on and works in every scrolling mode. mine explosions still show, because they tell you that you hit a mine and they carry the mine's sound. the latency calibration screen keeps its flares too.

timing bar shows how early or late each hit was, and it starts off. it moves with the receptors. in default and 2d downscroll it sits behind them at the bottom of the screen, and if your character would cover its middle, it moves below their feet or above their head. in 2d upscroll the enemy stands above the receptors, so timing bar position lets you choose where the bar goes. below enemy, the default, puts it just below the receptors, where only the incoming notes pass over it, and keeps it clear of your character's head at high receptor heights. above enemy puts it along the top of the screen, above the enemy, drawn in front of the fighters so a tall enemy can't hide it. the position setting only changes 2d upscroll.

early hits land on the rabbit's side on the left and late hits on the turtle's side on the right. the colored bands are the game's okay, good, great, and perfect windows, each tick takes the color of its judgement, and the white arrow follows your recent average. only taps and the starts of holds count, so misses, auto-played lanes, and hold releases are left off.

the game's own note colors row, further down the same page, now has a preview under it: your four lanes with a hold, the middle note of five-lane charts, and a mine, drawn in your note skin. it changes as soon as you pick another palette. mines keep the same red whatever the palette, and the enemy's health bar and attacked lanes use that red too, so a palette whose notes look like the mine is the one that will trip you up in a fight. kimothy's edge lanes and chaos come closest.

enemy attack opacity makes the enemy see-through while it attacks, so the notes behind it stay readable. it goes from 0% (invisible) to 100% (unchanged, the default) in 10% steps and works in every scrolling mode. most attacks are drawn as part of the enemy's own animation, like the firefly's beam, so the whole enemy fades for the length of the attack and comes back when the attack ends, taking about a tenth of a second each way. its shadow and any sidekicks fade with it. effects that only show up as attacks, like the vines that grow over the lanes, stay faded the whole time they're on screen. the game's own flashes and tints still show, and a defeated enemy is left alone so its death plays normally.

in options > audio, hit sound plays a short tick when you hit a note, and it starts off. hit sound volume sets how loud the tick is, from 5% to 100% (80% to start with). changing either one plays a tick a moment later, after the menu's own click, so you can hear the new level. the tick is the game's own menu click, so the game's sound effect volume sliders apply to it as well. it plays for taps and the starts of holds that you hit, once for a chord. misses already have the game's own sound, and auto-played lanes and hold releases stay quiet. the tick plays when the game judges your press, a frame or so after the key goes down, plus your audio output delay.

miss sound, also in options > audio, turns the game's miss sound on or off. it's the same setting as note miss sounds in options > gameplay, so changing one changes the other. miss sound volume goes from 10% to 300% in 10% steps, and 100% is the game's normal level. at any other level the mod plays the miss itself, from its own sound source turned up or down, so the music and the other sounds don't change. changing the volume plays a miss a moment later so you can hear it, and the game's sound effect volumes still apply. critical misses are already louder than normal ones, and the game's audio limiter stops them getting much louder past about 160%.

press left or right to change a value, and hold to keep changing it. all the settings are saved on that pc. reset to default in options > gameplay puts the nine gameplay rows back to default, 0%, 100%, 100%, default, on, off, below enemy, and 100%. the game's own reset there also turns miss sounds back on. like the game's own sound settings, the rest of the audio page has no reset, so the hit sound settings and the miss sound volume stay as they are. upgrading from an earlier version keeps your saved settings.

both flat modes keep the game's artwork, icons, and vertical meter text. player meters sit lower left and enemy meters upper right, each pair close to the chart.

at the "press any key" screen before a battle, alt, tab, and the windows key DON'T COUNT, so alt-tabbing away or opening the start menu won't start the fight. alt or windows held together with another key doesn't count either, since those are windows shortcuts. any other key, or a controller button, still starts it. other "press any key" screens, like the title screen, are unchanged.

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

version 2.4.0 was tested in-game on bepinex 6.0.0-be.788 and on melonloader 0.7.3. the melonloader test ran in a separate copy of the game folder. on both loaders, a test build played the firefly battle with the game's auto-play on every lane and stepped through the settings:

- circle and arrow skins in default, 2d downscroll, and 2d upscroll, at receptor heights from -10% to +30%, note sizes from 70% to 150%, and lane spacings from 70% to 150%, with holds and receptor flashes.
- the game's own move from four lanes to five and back, with the spacing following the lanes as they moved.
- going back to the default layout and skin. every lane position, receptor part, label, and note value the mod touches matched the game's own values again (70 of 70).
- the timing bar below the receptors in 2d upscroll, along the top of the screen above the enemy with the other position setting, under the player's feet in 2d downscroll, and at the bottom in default.
- note flares, switched off and on during the fight. with them off, every flare the game played was hidden; with them on, every one showed.
- enemy attack opacity at 0%, 30%, and 60%. each firefly attack faded the enemy and its shadow to the set level within about 80 ms and brought them back when the attack ended. a second run gave the firefly the kind of material that ignores transparency, which some later enemies use, and it faded the same way and got its own material back afterwards. the vines' layer took the set opacity too.
- the hit sound, recorded from the pc's audio output. each tick came through, 50%, 20%, and 5% measured exactly half, a fifth, and a twentieth of 100%, and the tick peaked about 56 ms after it was played. the game's audio limiter keeps it from getting any louder than 100%.
- the miss sound, recorded the same way. in the menu, 300%, 200%, 50%, and 10% measured exactly 3, 2, 0.5, and 0.1 times the 100% level. in the fight, with one lane left unplayed so its notes were missed, misses at 300% and 200% came out about 3.3 and 2.1 times as loud as the game's own misses at 100% (the game changes the miss sound as the enemy charges), and with the switch off no miss sound played.
- "but better" and the credit line on the title screen and the intro card, fading with the logo.

on bepinex, a second test turned auto-play off and pressed the lanes through the game's own input check at varied times. the game judged those presses as player taps. the timing bar showed them, and the hit sound played for them.

on both loaders, the battle's ready screen got real key presses from the test script. tab, the windows key, right alt, left alt, and alt+j each fired the game's any-key check, and none of them started the fight. neither did tab or windows sent down and up with no gap, the way a hotkey tool or macro sends them. j on its own started the countdown. the note colors preview was checked in every palette with the default, circle, and arrow skins: it sat right under the note colors row, and its notes had the exact colors the game gives its own notes.

on both loaders, options > gameplay showed the nine rows above speed mod with working up and down navigation, and reset to default put them all back. options > audio showed the hit sound and miss sound rows under sound effects with working navigation. the hit sound slider stopped at 5% and the miss sound slider at 10%, the miss slider read 250% and 300% correctly, and the miss switch also flipped the game's own note miss sounds row. the latency calibration preview showed the arrow skin at a larger size and spacing. the receptor position measurements from 2.2.0 still apply.

installer checks covered both loaders, switching between them, upgrades from 2.1.2 and 2.2.0, and removal, all in test copies of the game files.

notes haven't been played with a real keyboard or controller (the tests press lanes through the game's own input check), no controller was plugged in for the ready screen test, and the skins haven't been tried on a five-lane chart. only the firefly battle was played, so the vines, other enemies' attack effects, mines, and critical misses at high volume weren't seen or heard in a fight.

## source and licenses

see [technical notes](TECHNICAL-NOTES.md) for build instructions for both loaders and compatibility details. the package contains no game assets, game-generated assemblies, saves, or account data. you need your own installed copy of nocturne.

this is an unofficial community mod. see the [mod license](LICENSE-MOD.txt) and [third-party notices](THIRD-PARTY-NOTICES.txt) for licensing.
