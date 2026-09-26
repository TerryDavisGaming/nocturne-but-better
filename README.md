# nocturne but better

flat upscroll and downscroll for nocturne, with compact vertical health and energy bars beside the chart. in the flat modes you can change the receptor height, the note size, and the lane spacing. it also has circle and arrow note skins, an early/late timing bar like the one in osu!mania, an optional hit sound, a miss sound volume that goes up to 300%, see-through enemy attacks, a switch for note flares, a preview of the game's note colors next to the red of a mine, and a "but better" under the nocturne logo. alt, tab, and the windows key no longer start a battle by accident. you can make CUSTOM DIFFICULTIES for the game's songs in an IN-GAME EDITOR LIKE OSU!MANIA'S, play them instead of the game's charts, and share them as one file. and you can build CUSTOM BATTLES: your own song and charts against a game enemy or your own art, with set gear, a set level, and boss-style dialogue if you want them. they play in the arcade, which now opens from the main menu, and they share as one file too. you can switch back to the game's original look from options > gameplay.

it works on the base steam game, with melonloader, or with bepinex. YOU DON'T NEED A MOD LOADER: on a plain steam install, `install.cmd` sets up the bepinex loader that comes in the zip, so there's nothing else to download. if you already use melonloader 0.7.3 or newer, the mod goes into its `mods` folder instead.

it started out as nocturne flat scroll. the download and its files still use that name, so upgrades from older versions keep working.

[download for windows](https://github.com/TerryDavisGaming/nocturne-but-better/releases/download/v2.6.2/Nocturne-Flat-Scroll-2.6.2-Windows.zip) · [latest release](https://github.com/TerryDavisGaming/nocturne-but-better/releases/latest)

use the release zip to install. github's source download does not include the plugin or loader payload.

## settings

open options > gameplay. the mod adds ten rows above speed mod. [custom difficulties](#custom-difficulties) have their own page, and the [battle creator](#the-battle-creator) opens from it. the hit sound and miss sound settings are in options > audio, under sound effects.

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

infinite consumables (arcade), the last of the mod's rows, starts off. when it's on, using a consumable in an arcade battle doesn't use it up. the game's own limits stay, so you still get one use per battle. it works in the main menu's arcade and in the story's arcade cabinet, for the game's songs and for custom battles that use your own gear. a [custom battle that sets your gear](#gear-and-level) uses up its own consumable as usual and never touches yours.

in options > audio, hit sound plays a short tick when you hit a note, and it starts off. hit sound volume sets how loud the tick is, from 5% to 100% (80% to start with). changing either one plays a tick a moment later, after the menu's own click, so you can hear the new level. the tick is the game's own menu click, so the game's sound effect volume sliders apply to it as well. it plays for taps and the starts of holds that you hit, once for a chord. misses already have the game's own sound, and auto-played lanes and hold releases stay quiet. the tick plays when the game judges your press, a frame or so after the key goes down, plus your audio output delay.

miss sound, also in options > audio, turns the game's miss sound on or off. it's the same setting as note miss sounds in options > gameplay, so changing one changes the other. miss sound volume goes from 10% to 300% in 10% steps, and 100% is the game's normal level. at any other level the mod plays the miss itself, from its own sound source turned up or down, so the music and the other sounds don't change. changing the volume plays a miss a moment later so you can hear it, and the game's sound effect volumes still apply. critical misses are already louder than normal ones, and the game's audio limiter stops them getting much louder past about 160%.

press left or right to change a value, and hold to keep changing it. all the settings are saved on that pc. reset to default in options > gameplay puts the ten gameplay rows back to default, 0%, 100%, 100%, default, on, off, below enemy, 100%, and off. the game's own reset there also turns miss sounds back on. like the game's own sound settings, the rest of the audio page has no reset, so the hit sound settings and the miss sound volume stay as they are. upgrading from an earlier version keeps your saved settings.

both flat modes keep the game's artwork, icons, and vertical meter text. player meters sit lower left and enemy meters upper right, each pair close to the chart.

at the "press any key" screen before a battle, alt, tab, and the windows key DON'T COUNT, so alt-tabbing away or opening the start menu won't start the fight. alt or windows held together with another key doesn't count either, since those are windows shortcuts. any other key, or a controller button, still starts it. other "press any key" screens, like the title screen, are unchanged.

in arcade and high scores, the game marks an encounter you've mastered with gold notes and two sparkles on its card. a CHAPTER BUTTON NOW GETS THE SAME once every encounter in that chapter has its sparkles: its label turns gold and the two sparkles sit on its corners, for the difficulty you're looking at.

the title screen and the nocturne card in the startup intro read "nocturne but better, by terrydavisgaming". the extra lines fade in and out with the logo.

## custom difficulties

you can play your own charts, or other people's, on the game's songs, and make new ones in the game. everything for it is on ITS OWN PAGE: press custom charts on the main menu, or open the custom charts tab in options. the page has seven rows:

- chart editor. opens the editor (see below).
- battle creator. opens the [battle creator](#the-battle-creator) for [custom battles](#custom-battles).
- import custom chart. click it and a normal windows file picker opens. pick a `.nbbchart` pack or an `.sm` file. the mod checks that the song exists, that the chart has the same number of lanes, and that it has a tempo and notes that fit, then copies the file into its custom charts folder. if something's wrong, the row tells you what. importing the same charts twice just says "already imported".
- custom chart song. the songs you have custom charts for.
- custom difficulty. off, or one of that song's custom charts, shown as "name by author". when one is picked it plays INSTEAD OF THE GAME'S CHART for that song, WHATEVER DIFFICULTY YOU SELECT. off gives you the game's charts back.
- export custom charts. left and right choose "save this song" or "save all songs", and clicking opens a save dialog. every chart you picked ends up in ONE `.nbbchart` FILE, so the person you send it to imports everything with one click.
- custom chart folder. "open" shows the folder in windows explorer. "write game charts" saves the game's own charts into a `_game charts` folder inside it, as `.sm` files for other editors.

the game's difficulty screen (options > gameplay > change difficulty, or the pause menu in a fight) gets a CUSTOM ENTRY under zen. it shows the custom chart picked for the song, and left and right switch between that song's charts and off. in a fight it's about the song you're fighting, and the change starts on your next try. anywhere else it's about the song picked on the custom charts page, and selecting the entry takes you to that page.

the song's own enemy attacks and lane changes stay in by default, so a custom chart fights the same enemy the same way. a chart can bring its own events instead (see the [technical notes](TECHNICAL-NOTES.md)). on songs with three melodies, the melody the chart was made for plays, so the music always matches the notes.

custom chart scores are SAVED SEPARATELY. they never replace your high scores on the game's own charts, and each custom chart keeps its own best. when the game checks achievements after a fight, it reads the song's own scores, so a custom chart's score doesn't count as the song's.

boss fights that are split into parts (the m1 to m3 boss songs and gauntlets 1 to 4) CAN'T TAKE CUSTOM CHARTS YET. their parts share one score, so a custom part would mix into the game's high score. every other song works, including the realm bosses and the island enemies. if a chart can't be read when the battle starts, the game's own chart plays instead.

the folder is `%userprofile%\appdata\locallow\pracystudios\nocturne\nocturnebutbetter\customcharts`. you can also drop `.nbbchart` and `.sm` files into it yourself; they show up the next time you open the custom charts page. reset to default doesn't change which custom charts you picked, and uninstalling keeps the folder.

### the chart editor

the editor works like osu!mania's. open it from the custom charts page, pick a song and a melody (click one, or type to filter), then start from an empty chart, a copy of one of the game's difficulties, or one of your custom charts. the song's own music plays in time with the notes, and its tempo and enemy events come along. a custom battle's charts open in the same editor from the battle creator (see [charting and testing a battle](#charting-and-testing-a-battle)).

the screen is laid out like osu!'s editor. tabs along the top (compose, timing, events, setup, keys), the chart's name, and test, save, export and exit buttons. a toolbox on the left, the lanes in the middle with the music's waveform beside them, a panel on the right for the tab you're on, and the timeline with play/pause along the bottom. every button can be clicked, and shows its key. it all stays on screen whatever shape your window is.

- COMPOSE. pick a tool (select, note, hold, mine) and click in a lane to place on the snap. drag up with the hold tool to draw a hold. right click deletes. with the select tool, click a note or drag a box, then drag the selection to move it, or use alt+arrows to move it by a snap or a lane. the panel has copy, cut, paste, delete, mirror, reverse, snap to grid, select all, undo and redo.
- the toolbox sets the snap (1/1 to 1/16, triplets too), zoom, playback speed (25% to 100%), the note ticks, a METRONOME, the waveform, and the music and tick volumes. the mouse wheel moves through the song (ctrl+wheel changes the snap, shift+wheel zooms), and clicking or dragging the timeline jumps.
- TIMING. the song's tempo changes, BOOKMARKS (add, clear, previous, next; they show on the timeline), and SCROLL SPEED CHANGES: from any point, notes can scroll faster or slower (x0.5, x2, anything you type) WITHOUT MOVING IN TIME, like slider velocity in osu!mania. the lanes in the editor show the speed changes, and they play in battle too.
- EVENTS. the enemy's scripted events for the song: lane layout changes, its props and animations, helper attacks, vines and other combat effects, camera moves and text. a chart STARTS WITH THE SONG'S OWN EVENTS, so the enemy plays exactly as usual. you can pick an event, move it, make it longer or shorter, copy it, delete it, add one, or edit its text, and "song's events" puts them all back. only a chart whose events you changed carries its own.
- SETUP. the chart's name and author, save, export, and the charts folder.
- KEYS. every editor key can be changed: click an action, then press its new key (with ctrl, shift or alt if you like). keys are saved on your pc.
- TEST. the test button in the top bar, or f5, plays the chart in a real battle from just before the playhead: two bars earlier, kept between 1.5 and 4 seconds. notes before the playhead are left out. shift+f5, or shift+click, plays it from the start. it works on the game's songs and on custom battles, and it uses what's in the editor, saved or not. NOTHING IS SAVED: the results screen and the score are skipped, saves are blocked, and a test doesn't count towards achievements. you can't lose a test, and the pause menu's exit button reads "back to the editor". when the battle ends you're back on the same tab at the same spot, with your undo and your unsaved changes, and the status line says how it went, like "test finished: 96.4%, full combo. f5 tests again from here." tests only run from the title screen, so open the editor from the main menu's custom charts button or from options there.

ctrl+s saves it as a custom difficulty and PICKS IT FOR THAT SONG, so your next fight on that song plays it. EXPORT saves and then writes ONE `.nbbchart` FILE with the notes, the events and the scroll speeds, ready to send; the other person imports it from the custom charts page. esc leaves, and asks first if something isn't saved.

a custom battle's charts save into the battle instead. in them, a five-lane battle's middle lane is labelled attack, with your own attack key under it, and dialogue lines said during the song get a lane of their own on the left. [charting and testing a battle](#charting-and-testing-a-battle) has the rest.

saved charts go in the custom charts folder, under the song's name, as normal stepmania `.sm` files. you can also edit them in arrowvortex, stepmania, or a text editor: keep the `#NBBSONG` and `#NBBMELODY` lines at the top and the song's `#BPMS`. for the game's songs the game ignores `#OFFSET`, and so does the editor. a custom battle's `#OFFSET` counts, and so does a custom chart's when its own `#MUSIC` file plays (see [charting and testing a battle](#charting-and-testing-a-battle)). scroll speed changes are the standard `#SCROLLS` tag.

## custom battles

a custom battle is your own song with your own charts, in up to six difficulties, against an enemy you set up, with gear, a level and dialogue if you want them. custom battles are ARCADE ONLY: they never show up in the story. you make them in the battle creator and share them as one `.nbbbattle` file, and the battles in your battles folder show up on the arcade's custom tab.

### the arcade

the title screen's main menu gets an arcade button. the game has one already, hidden in its release builds, and the mod shows it. it NEEDS A SAVE: like continue, it only works once there's a save to continue from. it opens the arcade over the title on your latest save, with the songs you've unlocked there and the custom tab.

scores go into that save's `.score` file (`ProdSlotN.score`), the same way the story's arcade cabinet writes them, so steam cloud syncs them too. YOUR PLACE IN THE STORY DOESN'T CHANGE. while the arcade is open the mod refuses every write to the story's `.sav` files, and when you leave, it reads your latest save from disk again, so nothing else the arcade changed sticks (play time, items you used). it also notes the save files when the arcade opens and logs a warning if they've changed when it closes.

custom battles get a chapter after the game's own chapters, on a tab called custom. it's in the arcade, whether you opened it from the main menu or the story's arcade cabinet, and in high scores. the tab only shows when at least one custom battle loads. it's always unlocked, and the game's discovery percentage and trophy ranks leave it out. battles are sorted by title, and the folder is read again every time an arcade screen opens, so a battle you just saved or dropped in is there the next time. every battle offers all six difficulties, and one without its own chart plays the nearest charted one (the easier one when two are as near). a battle that can't be read is left out, and the battle creator's list says what's wrong with it.

selecting a battle shows a box under the score on the right: what the battle sets, if it sets anything (see [gear and level](#gear-and-level)), then its lore, or who made the song and the charts when it has none.

the song plays through the mod's own player, without the game's audio engine, and the battle ends a beat after the last note. when the first note comes early in the song, the notes start up to 3 s before the song, so they scroll in from the far end of the lane instead of showing up halfway down it. five-lane battles use the game's centred five-lane layout. custom battles never count towards achievements and give no xp. the arcade keeps their scores under each battle's id, which never changes, so a battle keeps its scores through edits and new titles.

### the battle creator

open the custom charts page (custom charts on the main menu, or the custom charts tab in options) and choose battle creator, the second row. its list starts with:

- new battle... pick a song file (`.ogg`, `.mp3`, `.wav`, `.flac`, `.m4a` or `.wma`), then 4 or 5 lanes. in five lanes the middle lane is played with the attack key. the lane count can't change later. the creator makes a folder named after the song, copies the song into it, and starts an empty chart at 120 bpm with a mantis as the enemy.
- new battle from an osu!mania beatmap (.osz)..., tagged "beta, not recommended" (see [below](#importing-an-osumania-beatmap-beta)).
- import a .nbbbattle file... unpacks a shared battle into a new folder you can edit.
- open the battles folder, in windows explorer.

your battles follow, each with its artist, lane count, charted difficulties, what it sets, and how many problems it has. click one, or use up, down and enter. f5 reads the folder again and esc closes the list.

the mod reads `.ogg` and `.wav` songs itself. `.mp3`, `.flac`, `.m4a` and `.wma` go through windows' own media decoders, so on a windows n edition without the media feature pack, convert those to `.ogg` or `.wav`.

a battle has seven pages down the left, which tab and shift+tab cycle through:

- info. the title (about 16 letters fit on the arcade card, and the field tells you when yours doesn't), the artist, the charter, and the lore (shift+enter starts a new line). the lore shows in the arcade's box, under what the battle sets. the [arcade card](#the-arcade-card) is on the right.
- song. the song file, its length, the offset, the bpm and the lanes, with "replace the song..." (the charts stay) and "edit charts".
- charts. the six difficulties, beginner, novice, adept, expert, elite and zen, with their note counts. click one to chart it in the chart editor.
- enemy, then art. see [the enemy and its art](#the-enemy-and-its-art).
- gear & level. see [gear and level](#gear-and-level).
- dialogue. see [dialogue](#dialogue).

the panel on the left also shows the lanes, the charted difficulties, and either "ready for the arcade" or up to three problems in plain words. the bottom bar has save (ctrl+s), export .nbbbattle..., open folder, delete battle, and back (esc). back with unsaved changes asks whether to save, and "don't save" sends any song, pictures or art you added since the last save to the recycle bin. the creator has no undo, except on the dialogue page.

save writes `battle.json` to a temporary file first and then swaps it in, and it keeps any keys it doesn't know. files the saved battle no longer uses go to the recycle bin. delete battle asks twice and then moves the whole folder to the recycle bin. the creator NEVER DELETES ANYTHING FOR GOOD: if windows can't recycle something, it stays where it is.

the battle creator also opens from options during the story, but its tests only run from the title screen.

### charting and testing a battle

a battle's charts open in the same [chart editor](#the-chart-editor): click a difficulty on the charts page, or "edit charts" on the song page. the battle's own song plays. the editor adds these for battles:

- a difficulty bar with the six tabs, beginner to zen. a green dot marks the charted ones. an empty tab offers "start empty" or "copy from" another tab, and ctrl+pageup and ctrl+pagedown switch tabs. the tempo, the events and the scroll speeds are shared by every tab.
- lanes. four-lane battles are played with the lane keys (d f j k unless you changed them). in five-lane battles the middle lane is played with the attack key, and the editor labels that lane attack, with your own attack key under it (space by default). your own attacks are off in five lanes, so THE ENEMY ONLY TAKES DAMAGE FROM PLAYER ATTACK EVENTS, which the events tab's "player attack" button adds.
- TIMING against the song: set bpm, add or remove a tempo change, "first beat here", and buttons that move every beat 1 or 10 ms against the music ([ and ] move it 1 ms, and with shift, 10 ms). tap tempo (t) listens to your taps and offers the bpm it hears, rounded (enter) or with two decimals (shift+enter). "loop 2 bars" plays two bars over and over with the metronome, so you can move the beats until the clicks sit on the music. the timing tab warns you when beat 0 comes before the song starts (a positive offset), since notes before 0:00 can't be played, and the creator lists it as a problem when notes are there.
- EVENTS are the battle's own, and a new battle has none. "add here" and "player attack" make one at the playhead, and a player attack hits the enemy in four lanes too. enemy animations and combat effects use the stand-in enemy's own, and events that spawn a prop or show text show nothing in a custom battle.
- a DIALOGUE LANE. lines said during the song show in a violet lane on the left and as marks on the timeline, and a line that stops the song is drawn across the lanes. the events tab switches between events and dialogue. there you can add a line at the playhead, edit its text and speaker, move it, make it show longer or shorter, make it stop the song, copy or delete it, or open it in the creator. the lines are shared by every difficulty.
- the setup tab has save, open folder, start the tab's chart empty or delete it, copy from another tab, and "dialogue in tests" on or off.

ctrl+s saves every difficulty into the battle's chart file (at least one needs notes), and the dialogue into `battle.json` when you changed it here. exporting is the creator's job.

TEST (f5, or shift+f5 from the start) plays the tab that's showing in a real battle, with your unsaved notes and events and your unsaved changes in the creator, like a new title, enemy or dialogue. from the start it plays the dialogue before the song, every line during it, and the lines after a win. from partway it plays the lines said during the song from where the test starts (two bars before the playhead), then the lines after a win. lines after a loss never play in a test, since a test can't be lost. the dialogue page has its own test buttons too.

### the enemy and its art

the enemy page picks a game enemy to stand in. with "game enemy's art", the battle looks and fights like that enemy. with "custom art", it fights like that enemy, with its attacks, sounds and stats, and looks like your own pictures or videos from the art page. the picker lists each enemy's own stats. the scripted bosses (yako, nocturne, caged wei, sue, winged wei, kitsune and ladybug) only show with "advanced bosses: on", with a warning that they may not play well, and they can't be used with custom art. a new battle starts with the mantis.

under "stats" you can set hp, damage, attack windup, energy per miss and passive energy. a blank field keeps the enemy's own number. the info boxes in the top right of the battle can be the enemy's own, or the battle's own: an enemy name and up to three boxes, each with a title and up to three lines of text. the enemy name only shows there. in a custom battle the stand-in isn't a boss, has no special attack, and drops nothing.

custom art has four animations: idle, attack, hurt and defeat. only the idle is needed. without an attack or hurt animation the idle shows instead, and without a defeat the hurt one does. choose... asks what kind of file it is first, and then the windows picker lists only that kind:

- image. one still `.png` or `.jpg`, shown as it is. an image is never cut into frames.
- gif. an animated `.gif`, played with its own timing.
- video. an `.mp4` (h.264) or `.webm` (vp8) video.
- sprite sheet. one `.png` or `.jpg` with the frames in a grid, read left to right, then down, like the game's own. the creator guesses the grid, and you can set the columns, rows, first frame and number of frames.

each animation then shows only the settings that apply to it: frames a second or speed, how long a still picture shows, where the attack's hit lands (worked out for you, or on a frame or time you pick, and the parry window opens a quarter of a second before it), its size compared with the idle, where it sits, a see-through colour (the corner colour, green, blue, black, white or magenta, with a range), mirroring, and whether the defeat loops. the preview shows the enemy on the battle's screen or close up, plays and steps through each animation (space, left and right), marks the hit, and lets you drag the art into place. the whole enemy has its own size, place, mirror, crisp pixels and shadow settings.

the creator checks each file from its first bytes before it copies it into the battle's `art` folder, and it says in plain words why it refuses one: webp, bmp, tiff and heic files, a file of the wrong kind, one that's too big, sideways or upside-down phone videos, and 10-bit video. pictures and gifs can be up to 32 mb, and videos up to 256 mb and 1920 x 1080. an idle video can run up to 60 seconds, and hurt and defeat videos up to 10.

VIDEOS HAVE NO SEE-THROUGH PARTS unless they're a webm with transparency, so an mp4 shows as a rectangle. gifs and pngs don't have that problem, and TURN INTO FRAMES... fixes it for any video. it makes the video a sprite sheet at 12, 15 or 24 frames a second, so a see-through colour works and it plays on any pc. it shows its progress, and esc (or leaving the page) stops it with nothing changed. the sheet goes in the battle's `art` folder with up to 240 frames. after that, one click on the corner colour cuts out a flat background like a green screen. "back to the video" undoes it, with the video's settings as they were, until you save.

in the arcade, the art starts loading when the battle's card stays highlighted for a moment, which gives it time to be ready for the first fight, videos included. if it can't load, the fight says "custom art couldn't load" (or "custom art wasn't ready yet", and the next fight tries again), and the enemy looks like its stand-in.

### the arcade card

the info page's right column shows the battle's arcade card at the arcade's own size, made the same way the arcade makes it, so what you see there is what the arcade shows. the card's picture slot is SQUARE. the best picture is a square one, 76 x 76 for pixel art or 456 x 456 for drawings and photos. see-through parts show the menu behind, and the corners are rounded off. choose image... takes a `.png` or `.jpg` of up to 16 mb and copies it into the battle's `images` folder. remove takes it off, and the arcade shows a plain card.

for a picture that isn't square, fit chooses between filling the square (what a battle's first picture starts with; a new picture keeps the fit you chose) and showing the whole picture with see-through bars. crop moves the square across the picture, in 10% steps or to a number you type, and you can drag the picture as well. crisp pixels is on, off, or auto, which keeps small pictures (up to 128 pixels across the slot) crisp and smooths bigger ones. the arcade keeps a card at 512 x 512 at most.

### gear and level

the gear & level page sets what the player fights with:

- gear. "player's own gear", or "set gear for this battle" with a weapon, armor, head, off hand, amulet and consumable, each empty or one of the game's items. "show test items" adds the game's test items to the pickers. the items only list once the game has loaded them, so if the page says they aren't loaded, load a save and try again.
- health upgrades, with set gear. the player's own, or a number from 0 to 99.
- level. "player's own level", or "set level for this battle", from 1 to 20. it starts at your level when a save is loaded, and the page shows what the level adds over level 1. level changes strength, regen and critical. gear that sets a stat outright (like the pool noodle) wins over the level.

in a battle that sets gear, the player has exactly those items, and empty slots stay empty. key items, followers, the pet and money stay theirs. their gear, health upgrades and level COME BACK AFTERWARDS however the battle ends, whether they win, lose, or quit from the pause menu. nothing is saved and no xp is earned. the game allows one consumable use per battle. a set-gear battle uses up its own consumable, never the player's, and [infinite consumables](#settings) doesn't apply to it.

players see what a battle sets before they start. in the arcade, the box on the right says it, like "sets your level and gear. level 12 (yours: 8). gear: only ancient katana, alloy vest, potion. health upgrades: 0. yours come back afterward." ("only" means some slots are left empty), and the card gets an amber tag under its melody row: "set gear, level 12", "set gear" or "set level 12". the page shows the same text under "players see in the arcade".

### dialogue

a battle can talk the way the game's bosses do. the dialogue page has a tab for each moment:

- before. lines before the song starts. the ready prompt waits while they run, and you press a key for each next line, like the game's own talks.
- during. lines said while the song plays, each at its own time. the game's dialogue box comes and goes by itself and the notes keep coming, so these lines ignore keys. the box's background turns see-through while they show, so you can still read the notes behind it. the text, the name tag and the face stay solid. a line can stop the song instead, like a boss's talk between songs: the song and the notes stop, you read the lines at your own pace, and the song goes on a second after the last one. the pause menu can't open during a stop.
- after a win. lines after the last note, before the results.
- after a loss. lines when you're beaten. the notes freeze and the song stops, then the lines run before the battle ends.
- speakers. your own speakers.

a line is said by karma (the player, at the top of the speaker picker, standing on the left), by one of the game's characters with their own faces, by the narrator (a narration box with no picture), or by one of your own speakers. type in the picker to jump to a name. each line has an expression, a side, a name tag (up to 24 letters, or the speaker's own), and up to 150 letters of text. a line during the song has a time (type one like 1:02.5 or 62.5, or a beat like beat 96, which moves with the notes) and how long it shows. other lines wait for a key, or go on by themselves after a set time, and then a key can't skip them.

REPLY (ctrl+r) adds a line right after the chosen one, said by the other side: karma answers anyone else, and when karma speaks, whoever spoke before her (not counting the narrator) answers. the other buttons add, copy, move (a beat earlier or later for lines during the song), delete and undo lines. their keys, while you're not typing: insert adds, ctrl+d copies, delete deletes, ctrl+up and ctrl+down move, ctrl+z and ctrl+y undo and redo, and space plays the preview.

the preview draws the box the way the game does, in the game's fonts, with play and a close-up view. "test in a battle" and "test from this line" run the battle through the chart editor's test and come back to the page. the chart editor's [dialogue lane](#charting-and-testing-a-battle) edits the lines said during the song too.

your own speakers get a name, a picture (`.png` or `.jpg`, up to 4 mb and 2048 pixels a side), more pictures for other expressions, a side, a mirror switch and a nudge. pictures up to 256 x 240 show pixel for pixel, tiny ones are scaled up by a whole number (up to 4 times), and bigger ones are scaled down to fit. a battle can have 12 speakers of its own. deleting one that has lines asks whether to delete those lines too or give them to the narrator. a battle holds up to 60 lines before the song, 200 during it, and 30 each after a win and after a loss.

the lines never reach your save: the game doesn't count them as scenes you've seen. a line that can't be read is left out, and if the dialogue fails some other way, the battle plays without it.

### importing an osu!mania beatmap (beta)

"new battle from an osu!mania beatmap (.osz)..." in the battle creator's list makes a battle from an osu!mania `.osz` file: its song, its 4-key or its 5-key charts (a battle has one lane count: with both in the beatmap you pick one, and import it again for the others), its tempo and its speed changes. it's marked BETA, NOT RECOMMENDED, because osu!mania charts are made for osu! and may not play well as battles:

- the game's own timing windows and health apply, in place of osu!'s judgement and hp drain.
- notes are put on the game's beat grid, which can move them a little, and holds too short for the grid become taps.
- osu! has no attacks. in five lanes the import adds a player attack every 8 bars (you can turn that off), and the enemy only takes damage from those.
- a battle has one set of speed changes for all its difficulties, so only one difficulty's speed changes come over.
- apart from generated test beatmaps, only five real beatmap sets have been tried, and only outside the game.

test play every chart before you share the battle.

the `.osz` is only read, never changed. after "reading the beatmap...", a summary shows what the battle will get: the lanes, each difficulty and the slot it goes in (choose one to move it or leave it out), whose speed changes to use (or none), the tempo, the song and the card, the player attacks for five lanes, a warning when notes come in the first 1.5 seconds (choose it to leave them out), and a list of what was changed or left out. only 4-key and 5-key difficulties come over, up to six. "make the battle" builds it and opens it on the charts page, where everything is editable like in any other battle. the background picture becomes the card if it's a png or jpeg. the background video, hit sounds and the storyboard are left out. a broken beatmap is refused with the reason, like "can't import that beatmap: it isn't a .osz file (it can't be opened as a zip)."

### sharing battles

export .nbbbattle..., on the creator's bottom bar, saves the battle and writes it as ONE `.nbbbattle` FILE with everything in it: the song, the charts, the pictures, the art and the dialogue. it can't be saved inside the battles folder. anyone with the mod can import it with "import a .nbbbattle file..." in the battle creator, which unpacks it into a new folder they can edit. they can also drop the file, or a battle's folder, into their battles folder. the arcade plays a `.nbbbattle` there as it is, and the creator lists it as a zip and offers to unpack it when they choose it (the zip then goes to the recycle bin).

every battle has an id that never changes, and its scores are kept under it. importing a battle you already have makes it a separate one with a new id. a folder copied in windows explorer keeps its id, so the arcade shows only one of the two. choosing the one the arcade skips (the later one by name) in the battle creator offers to make it a separate battle with a new id; the scores stay with the one the arcade shows.

### where the files are

custom battles live in `%userprofile%\appdata\locallow\pracystudios\nocturne\nocturnebutbetter\custombattles`, which the mod makes when it needs it. "open the battles folder" in the battle creator opens it. a battle is a folder with a `battle.json` in it, or a `.nbbbattle` file. the arcade also looks one folder deeper, so you can group battles in folders.

a battle the creator made has:

- `battle.json`: the title, song, enemy, gear, level, card and dialogue. you can edit it by hand, and the [technical notes](TECHNICAL-NOTES.md) list every key.
- `charts/song.sm`: every difficulty's chart in one stepmania file.
- `audio`: the song.
- `images`: the card picture.
- `art`: the enemy's art, and any sprite sheets made with turn into frames.
- `portraits`: your speakers' pictures.

a `.creator-work` folder inside the battles folder holds battles while they're being made or unpacked, and the creator cleans it out when it opens. videos from zipped battles are copied to `...\nocturnebutbetter\cache\enemyart` before they play, and that folder is trimmed to 1 gb. custom battle scores are kept in your save's `.score` file. uninstalling keeps all of it.

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

close the game and run `uninstall.cmd`. this disables the mod for both loaders and keeps the loaders, other mods, saved preferences, saves, and scores. it doesn't touch the mod's `nocturnebutbetter` folder in `appdata\locallow` either, so your custom charts and custom battles stay, along with the enemy art cache. restore the fullscreen fix separately if you enabled it.

## what was tested

version 2.6.2 changed the installer and the start of custom songs. the installer passed 81 checks in test copies of the game files, 32 of them new ones for finding the game: fake steam folders listing a library on a missing drive (the error players saw), an offline network share, folders that are gone, folder names with [brackets] and letters like ü, steam's old library file format, and locked or unreadable steam files. in each case it found the one test game, and the same checks fail on 2.6.1's installer. it also installed and uninstalled without being told the game folder, and upgraded the published 2.6.1 and 2.6.0. the real installer upgraded the test pc's own game from 2.6.1. two custom battles on mp3 songs were played twice each on bepinex, one with 169 speed changes and its first note at 0:00, the other with an `#OFFSET`: the notes started 2.02 s and 0.61 s before the song, and the start logs showed steady frames (the longest 35 ms) with the song and the notes within 30 ms of each other. the stutter tests (250 ms and 700 ms frames forced at the start) were run on 2.6.1, where the notes lurched to 1.3 to 2.3 times their speed for a moment, the game's own firefly song too. 2.6.2's change was checked against a model of the game's clock with the same stutters, not in the game. the creator's new "tempo change here" wasn't tried in the game.

version 2.6.1 was tested in-game on nocturne 1.0.1 with bepinex 6.0.0-be.788 and with melonloader 0.7.3, the same way as 2.6.0 below. each fix got a test that goes through the player's own path, and each test was also run on the old code first to show it catches the bug. on both loaders unless it says otherwise:

- quitting from the pause menu: five fights in a row, each paused the way esc does and left with the pause menu's exit button, one of them through the pause menu's gameplay page. every later fight had its music and moving notes, and options > gameplay logged no errors. on 2.6.0 the same test stopped at the second fight, which had no music.
- timing with speed mod at 170 in 2d upscroll: the notes moved at a steady speed from the start, and in battles with an `#OFFSET` of -2, -1.53 and -0.2 s (and +0.5 s on bepinex) the first note reached the receptors within 5 ms of where the chart editor puts it. 2.6.0 ignored the offset, so its notes were that far off the music, and a battle whose first note is on beat 0 started with notes already at the receptors. a battle whose first note is 0.2 s in started its notes 1.32 s before the song, and they scrolled in from the far end of the lane. bepinex also ran it in the default 3d view.
- enemy attack opacity at 50%: 12 of 12 attacks after a custom enemy's health reached 0 faded (0 of 12 with 2.6.0's code), and the firefly fight's enemy still played its death before the results.
- custom enemy art in the 10 test battles: the hurt picture when it was hit, and the defeat picture only when the enemy died at the end.
- the 2.6.0 tests again: set gear 228 of 228 and the level 233 of 233; dialogue in the arcade; the chart editor's test (all six runs on bepinex, two on melonloader). on bepinex also the battle creator's .osz import with all its checks (its 0.3 s offset battle now started 1.64 s early and put its notes where the editor does), the loss and dialogue file runs, and the dialogue page's 93 of 93. on melonloader also the creator's art page.
- no run changed a `.sav` file, and on melonloader no run logged a "native->managed trampoline" error.

turn into frames and the arcade card pictures weren't run again for 2.6.1, since nothing they use changed.

version 2.6.0 was tested in-game on nocturne 1.0.1 with bepinex 6.0.0-be.788 and with melonloader 0.7.3. the melonloader test ran in a separate copy of the game folder. every run saved the player's plugin, settings and saves first and put them back afterwards, and checked that the story's `.sav` files hadn't changed.

custom battles, on both loaders unless it says otherwise:

- four-lane and five-lane battles played to the end of their songs in the main menu's arcade. their scores went into the save's `ProdSlotN.score`, and the `.sav` files stayed the same.
- set gear and infinite consumables passed 228 of 228 checks: a win, a retry, quitting from the pause menu, a real loss, extra health, a save check in the middle of a battle, and infinite consumables on and off.
- the per-battle level and the arcade's box passed 233 of 233 checks.
- the battle creator passed 155 of 155 checks: every page opened, and they covered the pickers, play 10 s, save, export, the charts, the art page, and the .osz import. 20 broken beatmaps were refused in plain words. imported battles played in the arcade, and a five-lane one was beaten with the player attacks the import adds.
- custom enemy art was checked in 10 test battles: a gif, a sprite sheet, a png, an mp4, a webm, a big video, broken files, a zip, and art switched off. videos showed on the first fight.
- turn into frames passed 29 of 29 checks. a green-screen mp4 was cut out with the corner colour, esc stopped it, and "back to the video" brought the video back.
- arcade card pictures passed 81 of 81 checks on 16 cards: square, tall and wide pictures, tiny pixel art, a 4000 x 3000 picture, crops, and bad values in `battle.json`.
- the chart editor's test ran from the playhead and from the start, on battles and on the game's songs. each time the score was skipped, nothing was saved, and the editor came back. bepinex ran all six test runs. melonloader ran two of them, both from the playhead: a battle and a game song.
- dialogue: lines before the song held the ready prompt, lines during the song came within 6 ms of their time in a see-through box, and lines that stop the song paused it and started it again. win lines came before the results. a real loss played its loss lines with the music stopped. those ran on both loaders. on bepinex it also worked in downscroll and upscroll, from a separate dialogue file, in five lanes, and in the chart editor's test from the start and from partway, and a battle with 10 broken dialogue entries still loaded and played. the story's seen-scene flags didn't change.
- the creator's dialogue page passed 93 of 93 checks: every tab, adding, copying, replying and undoing lines, the speaker picker with karma at the top and typing to jump, faces and sides, a new speaker with a picture, save and reopen, and the chart editor's dialogue lane. its preview put the portraits where the game does.
- on melonloader, no run logged a "native->managed trampoline" error.

installer checks covered both loaders, switching between them, upgrades from the published 2.1.2, 2.2.0, 2.4.1, 2.5.0, 2.6.0 and 2.6.1, and removal, all in test copies of the game files.

2.5.0's custom difficulties and chart editor were tested on both loaders. the custom charts page opened from the main menu and from the difficulty screen's custom entry. the editor was driven with real key presses and mouse clicks, and its firefly music matched a reference render to the sample. a chart saved in it played in the firefly battle instead of the game's chart, with its score kept apart. a pack was imported, exported and imported again, the real windows file pickers opened in front of the game, "write game charts" wrote 177 charts for 103 songs, and an `.sm` with no song was refused with a message saying how to name one. a chapter button turned gold with its sparkles once every card in the chapter showed theirs.

the earlier features were tested on 2.4.x, on both loaders, in the firefly battle with the game's auto-play: the circle and arrow skins in all three scroll modes across the range of receptor heights, note sizes and lane spacings, the game's move from four lanes to five and back, and the default layout coming back exactly (70 of 70 values). the timing bar in each of its spots, note flares off and on, and enemy attack opacity at 0%, 30% and 60% all worked. the hit sound and miss sound were recorded from the pc's audio output, and each level came out as set. on bepinex, lanes pressed through the game's own input check counted as player taps. at the ready screen, tab, windows, both alt keys and alt+j didn't start the fight, and neither did tab or windows sent the way a macro sends them. j on its own did. the note colors preview matched the game's own colors in every palette and skin.

custom battles haven't been played with a real keyboard or a controller (the tests use auto-play or press lanes through the game's own input check), and nobody has played them by hand from the story's arcade cabinet. very long songs haven't been tried in the arcade, and the .osz import hasn't met real beatmaps in the game, only the generated test set and five real sets checked outside it. from earlier versions: custom charts haven't been tried on five-lane songs, with their own events, or from packs made in other tools. the editor hasn't been used with a controller. the chapter badge was checked by lighting a chapter's cards on screen, without really mastering a chapter. no controller was plugged in for the ready screen test, and the skins haven't been tried on a five-lane chart. only the firefly battle was played for the display and sound settings, so the vines, other enemies' attack effects, mines, and critical misses at high volume weren't seen or heard in a fight.

## source and licenses

see [technical notes](TECHNICAL-NOTES.md) for build instructions for both loaders and compatibility details. the package contains no game assets, game-generated assemblies, saves, or account data. you need your own installed copy of nocturne.

this is an unofficial community mod. see the [mod license](LICENSE-MOD.txt) and [third-party notices](THIRD-PARTY-NOTICES.txt) for licensing.
