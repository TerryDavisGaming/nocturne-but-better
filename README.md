# nocturne but better

flat upscroll and downscroll for nocturne, with compact vertical health and energy bars beside the chart. in the flat modes you can change the receptor height, the note size, and the lane spacing. it also has circle and arrow note skins, an early/late timing bar like the one in osu!mania, an optional hit sound, a miss sound volume that goes up to 300%, see-through enemy attacks, a switch for note flares, a preview of the game's note colors next to the red of a mine, and a "but better" under the nocturne logo. alt, tab, and the windows key no longer start a battle by accident. and you can make CUSTOM DIFFICULTIES for the game's songs in an IN-GAME EDITOR LIKE OSU!MANIA'S, play them instead of the game's charts, and share them as one file. you can switch back to the game's original look from the same menu.

it works on the base steam game, with melonloader, or with bepinex. YOU DON'T NEED A MOD LOADER: on a plain steam install, `install.cmd` sets up the bepinex loader that comes in the zip, so there's nothing else to download. if you already use melonloader 0.7.3 or newer, the mod goes into its `mods` folder instead.

it started out as nocturne flat scroll. the download and its files still use that name, so upgrades from older versions keep working.

[download for windows](https://github.com/TerryDavisGaming/nocturne-but-better/releases/download/v2.5.0/Nocturne-Flat-Scroll-2.5.0-Windows.zip) · [latest release](https://github.com/TerryDavisGaming/nocturne-but-better/releases/latest)

use the release zip to install. github's source download does not include the plugin or loader payload.

## settings

open options > gameplay. the mod adds nine rows above speed mod. [custom difficulties](#custom-difficulties) have their own page. the hit sound and miss sound settings are in options > audio, under sound effects.

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

in arcade and high scores, the game marks an encounter you've mastered with gold notes and two sparkles on its card. a CHAPTER BUTTON NOW GETS THE SAME once every encounter in that chapter has its sparkles: its label turns gold and the two sparkles sit on its corners, for the difficulty you're looking at.

the title screen and the nocturne card in the startup intro read "nocturne but better, by terrydavisgaming". the extra lines fade in and out with the logo.

## custom difficulties

you can play your own charts, or other people's, on the game's songs, and make new ones in the game. everything for it is on ITS OWN PAGE: press custom charts on the main menu, or open the custom charts tab in options. the page has six rows:

- chart editor. opens the editor (see below).
- import custom chart. click it and a normal windows file picker opens. pick a `.nbbchart` pack or an `.sm` file. the mod checks that the song exists, that the chart has the same number of lanes, and that it has a tempo and notes that fit, then copies the file into its custom charts folder. if something's wrong, the row tells you what. importing the same charts twice just says "already imported".
- custom chart song. the songs you have custom charts for.
- custom difficulty. off, or one of that song's custom charts, shown as "name by author". when one is picked it plays INSTEAD OF THE GAME'S CHART for that song, WHATEVER DIFFICULTY YOU SELECT. off gives you the game's charts back.
- export custom charts. left and right choose "save this song" or "save all songs", and clicking opens a save dialog. every chart you picked ends up in ONE `.nbbchart` FILE, so the person you send it to imports everything with one click.
- custom chart folder. "open" shows the folder in windows explorer. "write game charts" saves the game's own charts into a `_game charts` folder inside it, as `.sm` files for other editors.

the game's difficulty screen (options > gameplay > change difficulty, or the pause menu in a fight) gets a CUSTOM ENTRY under zen. it shows the custom chart picked for the song, and left and right switch between that song's charts and off. in a fight it's about the song you're fighting, and the change starts on your next try. anywhere else it's about the song picked on the custom charts page, and selecting the entry takes you to that page.

the song's own enemy attacks and lane changes stay in by default, so a custom chart fights the same enemy the same way. a chart can bring its own events instead (see the [technical notes](TECHNICAL-NOTES.md)). on songs with three melodies, the melody the chart was made for plays, so the music always matches the notes.

custom chart scores are SAVED SEPARATELY. they never replace your high scores on the game's own charts, and each custom chart keeps its own best.

boss fights that are split into parts (the m1 to m3 boss songs and gauntlets 1 to 4) CAN'T TAKE CUSTOM CHARTS YET. their parts share one score, so a custom part would mix into the game's high score. every other song works, including the realm bosses and the island enemies. if a chart can't be read when the battle starts, the game's own chart plays instead.

the folder is `%userprofile%\appdata\locallow\pracystudios\nocturne\nocturnebutbetter\customcharts`. you can also drop `.nbbchart` and `.sm` files into it yourself; they show up the next time you open the custom charts page. reset to default doesn't change which custom charts you picked, and uninstalling keeps the folder.

### the chart editor

the editor works like osu!mania's. open it from the custom charts page, pick a song and a melody (click one, or type to filter), then start from an empty chart, a copy of one of the game's difficulties, or one of your custom charts. the song's own music plays in time with the notes, and its tempo and enemy events come along.

the screen is laid out like osu!'s editor. tabs along the top (compose, timing, events, setup, keys), the chart's name, and save, export and exit buttons. a toolbox on the left, the lanes in the middle with the music's waveform beside them, a panel on the right for the tab you're on, and the timeline with play/pause along the bottom. every button can be clicked, and shows its key. it all stays on screen whatever shape your window is.

- COMPOSE. pick a tool (select, note, hold, mine) and click in a lane to place on the snap. drag up with the hold tool to draw a hold. right click deletes. with the select tool, click a note or drag a box, then drag the selection to move it, or use alt+arrows to move it by a snap or a lane. the panel has copy, cut, paste, delete, mirror, reverse, snap to grid, select all, undo and redo.
- the toolbox sets the snap (1/1 to 1/16, triplets too), zoom, playback speed (25% to 100%), the note ticks, a METRONOME, the waveform, and the music and tick volumes. the mouse wheel moves through the song (ctrl+wheel changes the snap, shift+wheel zooms), and clicking or dragging the timeline jumps.
- TIMING. the song's tempo changes, BOOKMARKS (add, clear, previous, next; they show on the timeline), and SCROLL SPEED CHANGES: from any point, notes can scroll faster or slower (x0.5, x2, anything you type) WITHOUT MOVING IN TIME, like slider velocity in osu!mania. the lanes in the editor show the speed changes, and they play in battle too.
- EVENTS. the enemy's scripted events for the song: lane layout changes, its props and animations, helper attacks, vines and other combat effects, camera moves and text. a chart STARTS WITH THE SONG'S OWN EVENTS, so the enemy plays exactly as usual. you can pick an event, move it, make it longer or shorter, copy it, delete it, add one, or edit its text, and "song's events" puts them all back. only a chart whose events you changed carries its own.
- SETUP. the chart's name and author, save, export, and the charts folder.
- KEYS. every editor key can be changed: click an action, then press its new key (with ctrl, shift or alt if you like). keys are saved on your pc.

ctrl+s saves it as a custom difficulty and PICKS IT FOR THAT SONG, so your next fight on that song plays it. EXPORT saves and then writes ONE `.nbbchart` FILE with the notes, the events and the scroll speeds, ready to send; the other person imports it from the custom charts page. esc leaves, and asks first if something isn't saved.

saved charts go in the custom charts folder, under the song's name, as normal stepmania `.sm` files. you can also edit them in arrowvortex, stepmania, or a text editor: keep the `#NBBSONG` and `#NBBMELODY` lines at the top and the song's `#BPMS`. the game ignores `#OFFSET`, and so does the editor. scroll speed changes are the standard `#SCROLLS` tag.

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

this package supports windows x64, steam nocturne 1.0.1, build 25487568. the installer checks the game files and refuses unknown builds or conflicting loader files. if access is denied, run the installer as administrator. a steam update needs another compatibility check.

## optional fullscreen flicker fix

for flickering black bars, close the game and run `enable-fullscreen-fix.cmd`. this applies a reversible directx 11 preference. run `restore-fullscreen-fix.cmd` to undo it. the scrolling mod works independently of this fix.

the fix stopped flickering on the original test pc under nocturne 1.0.0; other display and gpu combinations haven't been verified. game updates replace the patched file, so run `enable-fullscreen-fix.cmd` again after one.

## remove it

close the game and run `uninstall.cmd`. this disables the mod for both loaders and keeps the loaders, other mods, saved preferences, saves, and scores. restore the fullscreen fix separately if you enabled it.

## what was tested

version 2.5.0 was tested in-game on nocturne 1.0.1 with bepinex 6.0.0-be.788 and with melonloader 0.7.3. the melonloader test ran in a separate copy of the game folder.

custom difficulties, the editor, and the new menus, on both loaders:

- the custom charts page opened from the main menu and from the difficulty screen's custom entry. it showed only its six rows, the gameplay page got its own rows back, and the custom entry switched between a song's charts with left and right.
- the editor was driven with real key presses: typing to filter the song list, picking a song, a melody and a copy of the game's expert chart, playing and pausing, snap, speed, tools, select all, mirror, undo, and naming the chart. real mouse clicks placed a note, deleted it, and drew a hold. escape inside the editor never reached the menus underneath, and back worked normally after it closed.
- the editor's firefly music matched a reference render to the sample. that render was checked against the chart's notes, like the other songs' (median +3 ms), and a song whose music starts before the chart's first beat loaded with its lead-in.
- a chart saved in the editor loaded as a custom difficulty, played in the firefly battle instead of the game's chart, and kept its score apart. writing the chart out and reading it back gave the same notes.
- a chapter button turned gold with its sparkles once every card in the chapter showed theirs; the next chapter stayed as it was.

- a test build imported a two-chart pack, exported it, and imported the export, which was recognized as already imported.
- in options > gameplay it clicked import and used the real windows file picker. it typed a pack's path into the open dialog, typed a new file name into the save dialog for export, and cancelled a third dialog. each dialog opened in front of the game, the rows showed "already imported" and "saved 2 to ...", and cancelling changed nothing.
- "write game charts" wrote 177 charts for 103 songs, and one of them imported as six custom difficulties on its melody.
- with a custom difficulty picked, the firefly battle played it instead of the game's chart, on the chart's melody, and the score went under the custom chart instead of the game's high score.
- an `.sm` with no song was refused with a message saying how to name one.

the earlier features were tested on 2.4.x. on both loaders, a test build played the firefly battle with the game's auto-play on every lane and stepped through the settings:

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

installer checks covered both loaders, switching between them, upgrades from 2.1.2, 2.2.0, and 2.4.1, and removal, all in test copies of the game files.

custom charts haven't been tried on five-lane songs, with their own events, or from packs made in other tools. the editor hasn't been used with a controller. the chapter badge was checked by lighting a chapter's cards on screen, not with a chapter really mastered. notes haven't been played with a real keyboard or controller (the tests press lanes through the game's own input check), no controller was plugged in for the ready screen test, and the skins haven't been tried on a five-lane chart. only the firefly battle was played, so the vines, other enemies' attack effects, mines, and critical misses at high volume weren't seen or heard in a fight.

## source and licenses

see [technical notes](TECHNICAL-NOTES.md) for build instructions for both loaders and compatibility details. the package contains no game assets, game-generated assemblies, saves, or account data. you need your own installed copy of nocturne.

this is an unofficial community mod. see the [mod license](LICENSE-MOD.txt) and [third-party notices](THIRD-PARTY-NOTICES.txt) for licensing.
