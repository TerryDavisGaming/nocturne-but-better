# nocturne+

nocturne+ is a mod for nocturne. it lets you make and share your own battles and charts, and adds quick save and quick load anywhere in the story. it also adds flat 2d upscroll and downscroll and a set of gameplay settings you pick yourself. it runs on windows and works on the base steam game, with melonloader 0.7.3 or newer, or with the bepinex 6 (build 788) that comes in the zip. NO MOD LOADER IS NEEDED: on a plain steam install, `install.cmd` sets up that bundled bepinex for you, so there's nothing else to download.

[download nocturne+ 2.8.0 for windows](https://github.com/TerryDavisGaming/nocturne-plus/releases/download/v2.8.0/Nocturne-Plus-2.8.0-Windows.zip) · [latest release](https://github.com/TerryDavisGaming/nocturne-plus/releases/latest)

use the RELEASE ZIP to install. github's source download doesn't include the plugin or the loader payload.

## what's in it

- [MAKE YOUR OWN CUSTOM BATTLES](#make-your-own-custom-battle) in the [battle creator](#the-battle-creator): your own song and charts against one of the game's enemies or your own art (pictures, gifs, sprite sheets or videos), with set gear, a set level and boss-style dialogue if you want them. the creator can also [import an osu!mania beatmap](#importing-an-osumania-beatmap-beta) (beta), and you can share what you make [on the hub](#share-your-battle-on-the-hub) or [as one file](#sharing-battles-as-files).
- [GET CUSTOM BATTLES](#get-custom-battles) on the title screen opens the mod's online hub, where you find and download battles and custom difficulties that other players made, and upload your own. the mod only contacts the hub while that page is open, and the online hub row in options > custom charts turns it off.
- FLAT 2D UPSCROLL AND DOWNSCROLL, with compact vertical health and energy bars beside the chart, and the enemy's armor badge and statuses beside its bars, moved below the game's enemy info boxes when those would cover them. you set the [receptor height, note size and lane spacing](#gameplay-settings) yourself.
- [CUSTOM DIFFICULTIES](#custom-difficulties-and-the-chart-editor) for the game's own songs, made in an [in-game chart editor](#the-chart-editor) like osu!mania's, with a test button that plays your chart in a real battle, and shared as one `.nbbchart` file or on the hub. the one you pick (in options > custom charts, or with the custom entry the mod adds below zen on the game's difficulty screen) plays instead of the game's chart for that song, on any difficulty.
- [QUICK SAVE AND QUICK LOAD](#quick-save-and-quick-load) anywhere in the story: f5 saves wherever you can walk around, into the game's autosave so your own save slots are never written over, and f9 takes you back to your latest save. they stay off until you turn them on with one row at the bottom of options > gameplay, and either key can be changed.
- an [ARCADE ON THE MAIN MENU](#the-arcade-on-the-main-menu) that plays the songs you've unlocked and every custom battle you've added, without changing your place in the story. it has its [own gear](#gear-in-the-arcade), picked right there, and two rows in options > gameplay add infinite consumables for arcade battles and let that gear use all items (a battle with an item your save doesn't own saves no score and counts for no achievements).
- CIRCLE AND ARROW [NOTE SKINS](#gameplay-settings), in every scrolling mode.
- an EARLY/LATE [TIMING BAR](#gameplay-settings) like osu!'s hit error meter, next to the receptors, or above the enemy in 2d upscroll.
- a [HIT SOUND](#gameplay-settings), a short tick on every note you hit, with its own volume, and a MISS SOUND VOLUME that goes up to 300%. both are in options > audio.
- [SEE-THROUGH ENEMY ATTACKS](#gameplay-settings), so the notes behind an attacking enemy stay readable.
- a switch for [NOTE FLARES](#gameplay-settings), the burst on a receptor when you hit or hold a note. mine explosions always show.
- a [PREVIEW OF THE NOTE COLORS](#gameplay-settings) under the game's note colors row, next to the red of a mine.
- [AKUMA](#gameplay-settings), a note color palette of the mod's own that colors each lane by the controller button it's bound to, like the frets on a guitar.
- alt, tab and the windows key [NO LONGER START A BATTLE](#gameplay-settings) by accident at the "press any key" prompt.
- [GOLD CHAPTER BUTTONS](#gameplay-settings) on the arcade and high scores screens, once every encounter in the chapter is mastered.
- a ONE-CLICK [INSTALLER](#install) that picks your loader, keeps your saves and settings and turns off an older copy of the mod, and an [uninstaller](#remove-it) that keeps everything you made.
- an optional [fullscreen flicker fix](#optional-fullscreen-flicker-fix) for flickering black bars.
- [the title screen](#the-title-screen) reads nocturne+: a + follows the logo, and the title menu shows the mod's version under the game's.

you can switch back to the game's original look from options > gameplay.

it started out as nocturne flat scroll and was then called nocturne but better. from 2.8.0 it's nocturne+. what carries over, and how the installer turns off the old copy, is in [upgrading from an older version](#upgrading-from-an-older-version).

## install

1. install nocturne through steam, then close the game.
2. download the [release zip](https://github.com/TerryDavisGaming/nocturne-plus/releases/download/v2.8.0/Nocturne-Plus-2.8.0-Windows.zip) and EXTRACT THE WHOLE FOLDER. don't run the installer from inside the zip. github's source download won't do, because it doesn't have the plugin or the loader.

   the extracted folder has `install.cmd`, `uninstall.cmd`, `enable-fullscreen-fix.cmd` and `restore-fullscreen-fix.cmd` (they run `install-nocturnemod.ps1` and `set-nocturnefullscreenfix.ps1`), a `start-here.txt` with the same steps, the technical notes, and a `payload` folder with the three files the installer copies: `nocturneplus.dll` (the bepinex plugin), `nocturneplus.melonloader.dll` (the melonloader mod) and `bepinex-il2cpp-x64-788.zip` (the unchanged official bepinex loader). `sha256sums.txt` has the hashes of every file, and `source`, `licenses` and `third-party-source` hold the source code and licenses.
3. double-click `install.cmd`. it finds the game in your steam libraries by itself. if it can't, or it finds more than one copy, it asks for the folder: in steam, go to nocturne > manage > browse local files and paste the path of the folder that holds `nocturne.exe`.
4. read what it prints. it runs all its checks before it writes anything to the game folder, and if copying fails partway, it takes back the files it copied and puts back any older copy it moved or turned off. at the end it says what it installed and waits for a key press.
5. launch nocturne through steam. the first launch can take longer and may need internet access while the loader prepares files. let it finish.
6. open options > gameplay for the mod's settings. the hit sound and miss sound settings are in options > audio. [gameplay settings](#gameplay-settings) goes through every row.

the 2.8.0 installer HASN'T BEEN RUN ON A GAME FOLDER yet: it was only checked to parse, and the part that turns off the old copy ran against made-up copies (see [what was tested](#what-was-tested)).

this package supports windows x64, steam nocturne 1.0.1, build 25487568. these installers don't support macos, linux or steam deck. the installer checks the game files and steam's build number, and it refuses unknown builds or conflicting loader files without changing anything. it checks the game's code and its original layout files, so if you used an earlier experimental layout patch, restore those files first. a steam update needs another compatibility check: if the installer refuses your game after one, use a package updated for that version. the installer doesn't change your saves, scores, preferences, other mods or display settings.

if access is denied, right-click the `.cmd` file and run it as administrator. if the mod's rows are missing from options, make sure the zip was fully extracted before you ran `install.cmd`, and look in `bepinex\logoutput.log` or `melonloader\latest.log` in the game folder. when the mod starts, the log gets the line `Nocturne+ 2.8.0 loaded; its settings are in Options > Gameplay and Options > Audio.`

`install.cmd` passes extra options on to the installer: `-loader` (below), and `-gamepath "<game folder>"`, which skips the search. the [technical notes](TECHNICAL-NOTES.md) have the full powershell commands, including a preview that runs the checks and changes nothing.

### which loader it uses

the installer picks the loader for you:

- if melonloader 0.7.3 or newer is installed and turned on, the mod goes into the game's `mods` folder as `mods\nocturneplus.melonloader.dll`. the zip doesn't include melonloader. melonloader uses the system .net 6 runtime and downloads it if it's missing. a melonloader install adds no bepinex files.
- if there's no melonloader, or it won't start (its proxy dll, normally `version.dll`, is missing, or `userdata\loader.cfg` turns it off), the installer adds the bundled bepinex 6 loader (build 788) where it's missing and puts the plugin at `bepinex\plugins\nocturneplus\nocturneplus.dll`. the loader's files are `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, `changelog.txt`, and the `bepinex` and `dotnet` folders. the bundled loader includes its own .net runtime. loader files that already match are kept, and so are your `bepinex\config` files and your `doorstop_config.ini`.
- a turned-on melonloader older than 0.7.3 stops the installer. update melonloader to 0.7.3 or newer, then run `install.cmd` again. a melonloader older than 0.6 stops it even when it's turned off: update it, or remove it and use bepinex.

when it installs for bepinex, the installer also stops, and overwrites nothing, if the game folder already has different bepinex loader files (a different `changelog.txt` or `.xml` file is just kept). this package needs bepinex il2cpp x64 build 788, so check what your other mods need before you replace that setup. a `doorstop_config.ini` that turns doorstop off, or that doesn't point at the bundled bepinex and its `dotnet` runtime, stops it too.

only one copy of the mod runs at a time. if you switch loaders, run `install.cmd` again and it disables the copy for the other loader (it's kept as a `.disabled-` file). the other loader's own files stay where they are. to choose yourself, run `install.cmd -loader bepinex` or `install.cmd -loader melonloader` from a command prompt in the extracted folder.

DON'T KEEP BEPINEX AND MELONLOADER in the same game folder. both hook the same unity startup call, so only one of them starts, and a turned-on melonloader takes it. the installer won't add bepinex to a game where melonloader is turned on, not even with `-loader bepinex`: install for melonloader, or remove melonloader first.

melonloader users can also install by hand: copy `nocturneplus.melonloader.dll` from the zip's `payload` folder into the game's `mods` folder, and take out `nocturneflatscroll.melonloader.dll` if an older version is there. a copy by hand skips all of the installer's checks.

### upgrading from an older version

run `install.cmd` from the new zip, the same way as a first install. it replaces an older copy of nocturne+ and keeps the old dll beside the new one as a `.backup-` file. running it again on the same version changes nothing. the first 2.8.0 test build upgrades like any other copy. that build had one switch for quick save and one for quick load. the new quick save & load switch starts on if either of them was on.

before 2.8.0 the mod was called nocturne but better (and before that nocturne flat scroll), and its files were `nocturneflatscroll.dll` in `bepinex\plugins\nocturneflatscroll` and `nocturneflatscroll.melonloader.dll` in `mods`. `install.cmd` turns that old copy off (it keeps it as a `.disabled-` file) and puts in the new one, for either loader, so the old and the new dll don't both start. it knows every released build of the old files. an unknown file at any of the mod's dll paths, old name or new, stops the installer (and `uninstall.cmd`) before anything changes. the installer only accepts the dlls it knows, so a build you made yourself has to be copied by hand. turning off the old copy HASN'T BEEN TRIED IN THE GAME yet: it only ran with made-up copies (see [what was tested](#what-was-tested)).

your settings, scores, custom charts, custom battles, arcade gear and hub key carry over. the new version still reads the old settings keys and the same `nocturnebutbetter` data folder.

### optional fullscreen flicker fix

for flickering black bars, close the game and run `enable-fullscreen-fix.cmd`. this applies a reversible directx 11 preference: it changes three bytes and four padding bytes in the game's `nocturne_data\globalgamemanagers` so the game prefers directx 11, and it keeps the original beside it as `globalgamemanagers.nocturne-fullscreen-original-25487568.bak`. run `restore-fullscreen-fix.cmd` to undo it. saves and preferences aren't touched. nocturne+ works independently of this fix, and the installer works with the fix on or off.

the fix stopped flickering on the original test pc under nocturne 1.0.0; other display and gpu combinations haven't been verified. like the installer, it only works on steam build 25487568 and changes nothing on any other build. game updates and steam's verify integrity can replace the patched file, so run `enable-fullscreen-fix.cmd` again after one. after an update that changes the build, the package has to be checked again first.

it doesn't ask for the game folder. if it can't find the game, or it finds more than one copy, run it from a command prompt with the folder: `enable-fullscreen-fix.cmd -gamepath "<game folder>"`.

### remove it

close the game and run `uninstall.cmd`. like the installer, it asks for the game folder if it can't find exactly one copy, and `uninstall.cmd -gamepath "<game folder>"` skips the search. it disables the mod for both loaders, and an old copy from before 2.8.0 too. each dll is renamed to a `.disabled-` file, not deleted. it keeps the loaders (other mods may use them), other mods, saved preferences, saves and scores. it doesn't touch the mod's `nocturnebutbetter` folder in `appdata\locallow` either, so your custom charts and custom battles stay, along with the arcade gear, the enemy art cache and your hub key (see [where the files are](#where-the-files-are)). it skips the game build check, so it works after a steam update too. restore the fullscreen fix separately with `restore-fullscreen-fix.cmd` if you enabled it. running `install.cmd` later puts the mod back.

## make your own custom battle

a custom battle is your own song with your own charts, against an enemy you set up. custom battles are ARCADE ONLY: they never turn up among the story's fights, only in the arcade (the one on the main menu, or the story's arcade cabinet). this is the short path from a song file to playing your battle in the arcade. [custom battles in depth](#custom-battles-in-depth) has every option and limit.

before you start, you need:

- nocturne+ installed (see [install](#install)).
- a song file: `.ogg`, `.mp3`, `.wav`, `.flac`, `.m4a` or `.wma`, up to 512 mb.
  - the mod reads `.ogg` (ogg vorbis, not opus) and `.wav` itself.
  - the others go through windows' own media decoders. on a windows n edition without the media feature pack, convert them to `.ogg` (vorbis) or `.wav` first.
- a save. the arcade on the main menu NEEDS A SAVE: like "continue", it only works once there's a save to continue from.
- the title screen. the battle creator also opens from options during the story, but its tests only run from the TITLE SCREEN, so start there.

1. open the battle creator. on the title screen's main menu, choose "custom charts" (right below options). it opens options on its custom charts tab. click "battle creator", the second row.
   - the creator's list starts with "new battle...", "new battle from an osu!mania beatmap (.osz)...", "import a .nbbbattle file..." and "open the battles folder". your own battles follow.
   - click a row, or use up, down and enter. esc closes the creator.

2. start a new battle. choose "new battle...", pick your song in the windows file picker, then choose "4 lanes" or "5 lanes: the middle lane is played with the attack key".
   - four lanes are played with the lane keys (d f j k by default).
   - in five lanes your own attacks are off, so the enemy only takes damage from player attack events you add in the chart editor (step 9).
   - the lane count CAN'T CHANGE LATER: for the other number, make another battle.

   or take the shortcut: choose "new battle from an osu!mania beatmap (.osz)..." (tagged "beta, not recommended"), pick the `.osz`, check the summary it shows, and choose "make the battle". it makes the battle from the osu!mania `.osz`, with its song, its 4-key or 5-key charts, its tempo and its speed changes, and opens it on its charts page with the charts in it, all of them editable. osu!mania charts may not play well as battles, so TEST PLAY EVERY CHART (step 11). see [importing an osu!mania beatmap (beta)](#importing-an-osumania-beatmap-beta).

3. get to know the battle's pages. the creator makes a folder named after the song, copies the song into it, and opens the new battle on its info page, with an empty chart at 120 bpm and a mantis as the enemy. the battle's pages are down the left: info, song, charts, enemy, art, gear & level and dialogue.
   - tab and shift+tab move between them.
   - up, down and enter reach any button.
   - ctrl+s saves and esc goes back.
   - the song page shows the song's length, offset, bpm and lanes. "replace the song..." there swaps in another file (the charts stay as they are, so check their timing afterwards).

4. name it. on the info page, click a field to type in it: "title", "artist", "charter" and "lore".
   - the title starts as the song's file name. about 16 letters fit on the arcade card, and the line under the field says when yours doesn't.
   - in the lore, shift+enter starts a new line. the lore shows in the arcade's box when the battle is selected.

5. make its arcade card. the card is on the right of the info page. "choose image..." takes a `.png` or `.jpg` of up to 16 mb. a square picture fits best: 76 x 76 for pixel art or 456 x 456 for drawings and photos. without one, the arcade shows a plain card.

6. open the chart editor. on the charts page, click a difficulty: beginner, novice, adept, expert, elite or zen. the chart editor opens with your song ("edit charts" on the song page opens it too). a difficulty with no chart yet says it "isn't charted yet": click "start empty".

7. set the timing. a new battle starts at 120 bpm with beat 0 at the start of the song. on the editor's timing tab:
   - click "set bpm" and type the song's tempo, or play the song (space) and press t on the beats ("tap tempo"), then enter to use the bpm it hears (shift+enter keeps two decimals).
   - move the playhead to the song's first beat (click or drag on the timeline at the bottom, or use the arrow keys) and click "first beat here".
   - turn on "loop 2 bars" to hear the metronome over the music.
   - move every beat with "-10 ms", "-1 ms", "+1 ms" and "+10 ms" until the clicks sit on the music. `[` and `]` move it 1 ms, and with shift, 10 ms.

   do this before you chart, so the beat snap matches the music. tempo changes move the notes with the beats on every difficulty.

8. place the notes. on the compose tab, space plays and pauses.
   - pick a tool: "note" (2), "hold" (3, press and drag up), "mine" (4) or "select" (1).
   - click in a lane to place a note on the beat snap (1/4 at first). right click deletes one.
   - the mouse wheel moves one snap, and ctrl+z undoes.

9. add player attacks. in five lanes your own attacks are off, so the enemy only takes damage from PLAYER ATTACK EVENTS. on the events tab, move the playhead and click "player attack" to add one. a player attack hits the enemy in four lanes too.

10. chart more difficulties, if you want to. ctrl+pgup and ctrl+pgdn switch difficulty, and an empty one can start as a copy of another with "copy from". you don't have to chart all six: in the arcade, a difficulty without its own chart plays the nearest charted one.

11. test the chart. press f5 (or click "test" at the top) to play the difficulty you're on from two bars before the playhead, or shift+f5 to play it from the start.
    - it's a real battle with your unsaved notes, but you can't lose it and nothing is saved.
    - the pause menu's "back to the editor" ends it early.
    - back in the editor, the status line shows how you did, and f5 tests again.

12. save the charts. ctrl+s in the editor saves every difficulty into the battle's chart file, and at least one needs notes. esc (or "exit", top right) takes you back to the battle creator. with unsaved changes it asks first: "save" (enter), "discard" (d) or "cancel" (esc).

13. pick the enemy. on the enemy page, click "looks like" to choose the game enemy that stands in. the picker shows each enemy's own stats. with "game enemy's art", the battle looks and fights like that enemy.
    - under stats you can set hp, damage, attack windup, energy per miss and passive energy. a blank field keeps the enemy's own number.
    - the scripted bosses only show with "advanced bosses: on", and they may not play well.
    - on the right, "info boxes: this battle's own" lets you write an enemy name and up to 3 boxes for the top right of the battle. with "info boxes: the enemy's own", the battle shows the stand-in's.

14. add your own art, if you want to. for an enemy that looks like your own art, click "custom art" on the enemy page instead. it then fights like the stand-in, with its attacks, sounds and stats (the scripted bosses can't take custom art). on the art page, click "choose..." next to idle, say what it is ("image", "gif", "video" or "sprite sheet"), then pick the file.
    - only the idle is needed: without an attack or hurt the idle shows instead, and without a defeat the hurt one does.
    - an mp4 HAS NO SEE-THROUGH PARTS and shows as a rectangle. use "turn into frames..." on it, then set "see-through colour" to corner colour to cut out a flat background.

15. set gear and level, if you want to. on the gear & level page, "player's own gear" lets players fight with what they have on, and "set gear for this battle" gives them exactly the weapon, armor, head, off hand, amulet and consumable you pick. "player's own level" and "set level for this battle" (1 to 20) work the same way.
    - their own gear and level come back afterwards.
    - if the page says the game's items aren't loaded, load a save and try again.
    - "players see in the arcade" shows what the arcade will tell them.

16. add dialogue, if you want to. the dialogue page has a tab for each moment: before, during, after a win and after a loss, plus speakers for characters of your own.
    - click "add line" (or press insert). its "text" opens for typing at once: type the line (up to 150 letters) and press enter.
    - then click "speaker" to pick who says it: karma, one of the game's characters, the narrator, or your own speaker. a new line starts with the chosen line's speaker, or the last one you used (the narrator at first).
    - a line during the song gets its time in "when".
    - "reply" (ctrl+r) adds the other side's answer.
    - to add your own character, click "new speaker..." on the speakers tab, pick a `.png` or `.jpg` (up to 4 mb) and type its name.

17. test the whole battle. a test uses your unsaved changes in the creator too, like a new title, enemy or dialogue. open a difficulty from the charts page and press shift+f5, or click "test in a battle" on the dialogue page.
    - from the start, a test plays the dialogue before the song, the lines during it and the lines after a win.
    - lines after a loss never play in a test, since a test can't be lost. to see them, click "play" in the dialogue page's preview.
    - the editor's setup tab turns "dialogue in tests" off if you only want the song.

18. save the battle. click "save" on the bottom bar, or press ctrl+s. the panel on the left says "ready for the arcade" once a difficulty is charted and nothing is wrong.
    - if something is wrong, it lists up to three problems in plain words.
    - an amber * in the top bar means something isn't saved.
    - esc with unsaved changes asks "save", "don't save" or "cancel".

19. close the creator. press esc to go back to the list of battles, then esc again (or "close") to close the creator, and leave options.

20. play it. on the title screen's main menu, choose "arcade". your battle is on the tab called "custom", after the game's own chapters, sorted by title, with all six difficulties.
    - the arcade reads the battles folder again every time it opens, so a battle you just saved is there.
    - the story's arcade cabinet lists it too.
    - scores go into your latest save's `.score` file, and your place in the story doesn't change.
    - if your battle is missing, open the battle creator: its list says what's wrong with it.

next, [custom battles in depth](#custom-battles-in-depth) goes through every page, and [share your battle on the hub](#share-your-battle-on-the-hub) shows how to upload it so other players can download it.

## share your battle on the hub

the online hub (see [get custom battles](#get-custom-battles)) takes custom battles you made and packs of custom difficulties for the game's songs. an upload is live on the hub THE MOMENT IT FINISHES. there are NO ACCOUNTS: the hub knows your uploads by this pc's hub key (see [my uploads and your hub key](#my-uploads-and-your-hub-key)).

check these first:

- the battle needs a title and at least one charted difficulty.
- upload only what you made. songs, pictures and videos usually belong to someone else, so include them only if you're allowed to share them.
- uploading works only from the title screen, and only while the online hub setting (the last row of options > custom charts) is on. it's on to start with.

to upload a battle:

1. open the hub. there are two ways in:
   - from the battle creator (custom charts on the main menu, then battle creator): open your battle and choose "upload to the hub..." on the bottom bar. it saves the battle first and opens the hub on its upload tab.
   - from the title screen: choose get custom battles, the box above get soundtrack.

   the creator's button only shows while the hub is on. if you opened the creator from options during the story, it says "uploading works from the title screen". when you close the hub, you're back in the creator on the battle as it was.
2. the first time the hub opens, either way, a "before you start" notice shows right away, before anything else (what it says is in [get custom battles](#get-custom-battles)). "ok, go to the hub" goes on. "not now" or esc closes the hub without contacting it (from the creator you're back in the creator), and the notice shows again next time.
3. from the title screen, go to the upload tab (tab and shift+tab switch tabs, or lb and rb on a controller) and choose "upload a battle" with enter, or click "choose a battle...". from the creator, skip this step and the next: once the hub has connected, it goes straight to that battle's details (step 5).
4. pick the battle. "upload which battle?" lists your battles, with the highlighted one's card and its charter beside the list, and typing letters jumps to a title. choosing one opens its details. nothing is sent yet. a battle that can't go up says "(can't be uploaded)", and its hint says why:
   - "it came from the hub".
   - "it's a zip: unpack it in the battle creator first".
   - "nothing in it is charted yet".
   - "it can't be read" or "it doesn't load", with the problem.
   - "it's a copy of another battle": choose it in the battle creator and pick "make it a separate battle" to give it its own id.
5. fill in the details. the title, artist and charter come from the battle, so change them in the battle creator. "description" is optional, up to 1000 characters (shift+enter starts a new line).
6. at your first upload, also fill in "your name on the hub". it shows with your uploads, followed by a 4-character tag, like `Name #7K2M`, and it starts as the charter name the chart editor remembers.
   - it needs 1 to 32 characters with a letter or digit in it (not admin, owner, moderator or hub).
   - the game asks for it ONLY THIS ONCE and has no place to change it later.
7. choose "check it" to go on, or "back" (esc) to drop the upload. on a controller the form starts on "check it", since typing needs a keyboard.
8. wait for the check. "check it" checks and packs the battle on your pc from a copy (your own files are only read), and esc stops it. what the check does is listed after these steps.
9. read the summary. headed "ready to upload", it shows the size, the number of files, the difficulties, the lanes, the length and what's inside. if something is wrong, it's headed "it can't be uploaded yet" and lists the reasons. choose "continue to the rules" (it only shows when the battle can be uploaded), or "back to the details" (esc).
10. read the rules. "before you upload" shows BEFORE EVERY UPLOAD. it says:
    - upload only what you made (a battle from an osu! beatmap counts only if you mapped it or the mapper allowed it).
    - include songs, pictures and videos only if you're allowed to share them. if a song's owner asks, the battle is taken down.
    - your upload goes live straight away.
    - a hub key with 3 uploads removed for copyright can't upload any more.
    - what the hub stores (see [what the hub stores](#what-the-hub-stores)).
    - where takedown requests go.
11. choose "i made this and i have the right to share it: upload". "back" is picked first, and the upload button is on the row below it, so the click that opened the rules, or a double click, can't press it.
12. wait for the upload. the "uploading" screen shows its progress. when it finishes, the page moves to my uploads with your battle picked and says it's on the hub now.
    - esc or "stop the upload" asks first: "keep uploading" goes on, and "stop the upload" stops it with nothing published.
    - if it fails, or the hub refuses it, you're back on the summary, which says why.
13. back up your hub key. after your first upload the hub offers "back up my hub key now..." or "later". choose "back up my hub key now...": it opens a save window, "back up your hub key", with `nocturne hub key.txt` as the name to start with. save it somewhere private, not in a synced folder like onedrive. the hub offers this only once, so after "later" (or esc), use "back up key..." on the my uploads tab yourself.

once it's up, it shows in browse within a minute. a new uploader's picture shows after the hub's owner checks it or after 24 hours, and once your uploads have been on the hub for a week with no strikes, new pictures usually show at once.

your hub key is how the hub knows your uploads, and ONLY THAT KEY can change or delete them, so BACK IT UP after your first upload (step 13). [my uploads and your hub key](#my-uploads-and-your-hub-key) says how to keep the backup and how to bring the key back or replace it.

what the check does:

- only the files the battle uses go in. the rest are listed under "left out:".
- the hub takes up to 100 mb, songs as `.ogg`, `.wav` or `.mp3`, pictures as `.png`, `.jpg` (or `.jpeg`) or `.gif`, and videos as webm (vp8). each file is judged by its name and by what's in it: a `.ogg` has to be ogg vorbis inside, a `.wav` pcm or float, and a picture a real png, jpeg or gif.
- a file the battle uses that the hub doesn't take stops the upload: convert a `.flac`, `.m4a` or `.wma` song to `.ogg` or `.mp3`, and make an mp4 enemy video into frames with the turn into frames button on the creator's art page (or convert it to webm vp8). a file the battle doesn't use is only left out.
- pictures lose their hidden details, like where a photo was taken or a phone's motion-photo clip stuck after the picture, without being re-encoded. they're listed under "cleaned:".
- a path to a windows user folder in a chart, a json file or a song's metadata (like the xmp that adobe's editors write), or your windows user name in a song's tags, STOPS THE UPLOAD, and the summary says where it is. song tags without one stay, and they're listed under "kept as it is:".
- the card becomes a small picture for the listing. a battle without a card gets a tile with its title.

the hub's limits (its owner can change them):

- a new hub key can upload 2 things a day for its first 2 days (and 100 mb a day), then 10 (and 500 mb a day). the upload tab shows today's count, like "today: 1 of 2 uploads."
- a key can have 50 entries on the hub, and one upload at a time.

when an upload can't start, the upload tab says why, like "uploads are closed right now. downloads still work." some refusals from the hub:

- "you've uploaded as much as the hub allows today". try again tomorrow.
- "someone else's entry on the hub already has this battle". if it's yours, report that entry with "this is mine" as the note. if you made your own version of it, it needs its own battle id first. the battle creator offers "make it a separate battle" only for a folder that has the same id as another battle folder on your pc, so copy its folder in windows explorer, choose the one the arcade skips in the battle creator, pick "make it a separate battle" (see [sharing battles as files](#sharing-battles-as-files)) and upload that one.
- "the hub's owner removed this battle for copyright, so it can't be uploaded again with this key".
- "a hub key can have 50 entries on the hub". delete one from the hub first (my uploads).

to upload a new version later:

1. open my uploads. it lists what you uploaded with its state on the hub. a removed entry says why, and an entry whose files changed since you uploaded it says "changed since your last upload".
2. pick the entry and press enter, or choose "upload a new version...".
   - a battle: if its folder is still where you uploaded it from, it goes straight to the details. if not, pick the battle it's a new version of. it has to be the same battle, with the same battle id.
   - a pack: the song list opens, headed "new version of" and the pack's title, with the old pack title filled in. pick its difficulties, then go on as for a first upload.
3. the old description is filled in, and the details say which version it becomes (like "v2 becomes v3"). the check, the rules and the upload go as above, and players who installed it see an update.

choosing a battle in "upload a battle" that you uploaded from this pc, and that's still on the hub, does the same. its hint says so, like "you uploaded this (v2); this makes v3."

"delete from the hub..." on my uploads removes an entry for everyone after two questions. players who downloaded it keep their copy, and your own battle on your pc isn't touched (more in [my uploads and your hub key](#my-uploads-and-your-hub-key)).

you can also upload a pack of custom difficulties you made for the game's own songs. a pack's rules:

- every difficulty in it has the same lanes, 4 or 5, so upload the 5-lane ones as a separate pack.
- a difficulty that "plays its own song file" can't go in: make it a custom battle to share it.
- up to 40 songs, and 20 difficulties a song.

to upload a pack:

1. on the upload tab, choose "upload custom difficulties" with enter, or click "choose difficulties...". only custom difficulties you made are offered, not the hub's downloads or the game's own charts.
2. "upload custom difficulties: pick a song" lists the songs, each with how many of its difficulties are picked. choose a song, then choose its difficulties to put them in the pack or take them out ("[x]" marks the ones in). "back to the songs" goes back, and more songs can go in the same pack (up to 40).
3. choose "next: the pack's title" at the bottom of the song list. the details have "pack title" (up to 100 characters, and with one song it starts as the song's name followed by "difficulties") and the description. at your first upload they also ask for "your name on the hub" (step 6 of the battle steps). the pack's charter is the charter of its difficulties when they all have the same one, and your name on the hub when they don't.
4. then "check it", the rules and the upload go as for a battle (steps 7 to 13). a path from your windows user folder in a chart stops it too, and the message says where it is, so you can take it out in the chart editor.

the hub doesn't take osu!mania `.osz` files. the upload tab's last row, "an osu!mania beatmap you made (.osz)", only says so: turn the beatmap into a battle in the battle creator first ([importing an osu!mania beatmap (beta)](#importing-an-osumania-beatmap-beta)), then upload that battle. upload only beatmaps you mapped, or ones the mapper allowed.

you can also share a battle without the hub, as one `.nbbbattle` file: see [sharing battles as files](#sharing-battles-as-files). custom difficulties share as `.nbbchart` files (see [custom difficulties and the chart editor](#custom-difficulties-and-the-chart-editor)).

## get custom battles

get custom battles, the box above get soundtrack on the title screen, opens the online hub: a place to find, download and share custom battles and custom difficulties for the game's songs, a bit like osu!'s beatmap listing. with the keyboard or a controller, the box is the next stop down from quit. the hub is a small server run for this mod. it isn't run by or connected to the makers of nocturne. it's new, so it may be empty at first.

THE MOD CONTACTS THE HUB ONLY WHILE ITS PAGE IS OPEN, and never at startup. the first time the page opens, a short notice ("before you start") says what that means: browsing and downloading send requests to the hub and nothing about you is stored, downloads are checked and never run as programs, content is made by players (r reports anything that breaks the rules), and on busy days the hub can be down until 00:00 utc. "ok, go to the hub" connects. "not now", or esc, closes the page without contacting the hub, and the notice shows again next time.

online hub, the last row of options > custom charts, is on to start with. off turns the hub off: the box goes away, so does the battle creator's upload to the hub... button, and the mod never contacts the hub at all.

the page opens only from the title screen, never in the story or a battle, and it closes by itself if the title screen goes away under it. the battle creator's upload to the hub... button opens it too (with the creator opened from options on the title screen), straight on the upload tab. the upload steps are in [share your battle on the hub](#share-your-battle-on-the-hub).

the page has four tabs: browse, installed, my uploads and upload. tab and shift+tab switch them, or lb and rb on a controller. everything works with the mouse. with the keyboard, up and down move in the list, right goes to the buttons beside it, enter (or a double click on a row) does the row's main thing, and esc goes back or closes. on browse, up from the top row goes to the search box and filters, and down comes back. a controller does the same with the d-pad or the left stick, a and b. b also leaves a text box, since typing needs a keyboard. the bottom bar shows the keys for the tab you're on, and the top right says how the link is, like "hub: online", "hub: update the mod" or "hub: can't be used now".

on the real hub, the page has only been opened, searched and clicked through so far (it was empty). NO UPLOAD OR DOWNLOAD has been tried on it yet (see [what was tested](#what-was-tested)).

### browse

the search box looks through titles, artists, charters, the song names of difficulty packs and descriptions. ctrl+f or / starts typing (so does clicking the box), and the list follows a second after you stop once the last word has 3 letters. enter searches right away and keeps it, and esc goes back to the search you kept. a search takes up to 4 words (one-letter words are dropped), and the last one also matches the start of a word once it has 3 letters, so "moo" finds moonlit. a search shows at most 192 results, taken from its best matches. the hub takes about 10 searches a minute from one address, and every page of results counts. changing a filter during a search waits a moment, so clicking through its choices costs one search. when the hub asks you to slow down, the list says "slow down a little.", counts down and tries again by itself.

next to the search box are type (all, battles, or difficulties for the game's songs), lanes (any, 4 or 5) and sort (newest, title, best match while searching, and most downloaded when the hub counts downloads). t, l and s change them from the keyboard, and with shift they go backwards. the page remembers them for next time. "more by this uploader" (m) lists one person's uploads, newest first unless you search. clicking the chip it adds clears it, and so does m again. the list loads 24 at a time as you scroll.

each row shows the entry's picture (or a tile with its first letters), the title and artist, who charted it and who uploaded it (their name and a 4-character tag), its difficulties as coloured chips (green for levels 1 to 3, blue 4 to 6, orange 7 to 9, red 10 and up), what it brings (sets your gear, sets your level, dialogue, video), whether it's a battle or difficulties and for how many lanes, its size and downloads, and where it stands on your pc: installed, in use, update, yours, you have it, needs a newer mod, or failed. a pack's row names the songs it's for. the panel on the right adds the description, its length, bpm and version, a table of the difficulties with their levels (and a battle's note counts), and what's inside. a long description has a read the description button. needs a newer mod means the entry uses something this version of the mod can't read, so update the mod to play it. your own entries are marked "yours", with a "show in my uploads" button.

enter or download gets it, ONE DOWNLOAD AT A TIME, with a bar at the bottom. the drive needs twice the download's size free. esc stops it: it asks first, with "keep downloading", "stop the download" or "stop it and close the hub". a stopped download leaves nothing behind. once it's installing, it can't be stopped. a download that gets nothing for 30 seconds stops, and you can try again.

every download is CHECKED IN FULL before it's installed: its size and sha-256 have to match its listing, the zip and every file in it are checked, and each song, picture and video is judged by what's in it, not by its name. then the mod's own loader has to take it, and so does the game: its chart reader for a battle, its custom chart check for a pack. nothing is unpacked and NOTHING DOWNLOADED IS EVER RUN. a download that fails a check installs nothing, the row shows failed with a try again button, and r reports it as broken with what was found.

a battle lands in `custombattles\downloaded` as the `.nbbbattle` file it is, and the arcade's custom tab shows it the next time the arcade opens. a difficulty pack lands in `customcharts\downloaded` as a `.nbbchart` file, and its "use it now" button lists its difficulties, right after it installs and whenever you press it later: a song that has no custom pick yet plays the pack's first difficulty for it right away, and a song you already picked something for keeps it until you choose one of the pack's in the list. the list marks the current pick "(plays now)". options > custom charts turns them off like any other custom difficulty. a battle you already have (your own, or a copy you added) shows "you have it", and the hub never installs a second copy of it.

### installed

everything the hub installed, with its picture, sorted by title, and it works without the internet. when the hub answers, each entry says whether it's up to date, has an update (the tab counts them, like "installed 12 (3 updates)"), or was removed from the hub, and a removed entry says why. an entry can also be deleted from the hub by its uploader or be under review there. in all of these, your copy stays installed and keeps working. the panel says what an update changes, like "v3 to v4: 31 mb (was 24 mb), adds a video." enter or update to v... gets it, and that needs the hub. an update keeps the file's name, so a battle keeps its scores and a pack keeps its picks, and the old copy goes to the recycle bin (on a drive without one, the hub's own untouched copy is deleted). on an entry with no update, enter opens "use it now" for a pack.

del or delete sends the file to the recycle bin after you confirm (del does the same for an installed entry on browse). on a drive without one, a download you haven't changed can be deleted for good after a second question, and a changed one is left for you to delete in explorer. an update never deletes a changed file for good either: it goes to the recycle bin, and on a drive without one the update waits until you move the file out. the hub only deletes inside its two downloaded folders. a file you delete in explorer, or a hub battle you unpack in the battle creator, drops off the list the next time the page opens or on f5.

### my uploads and your hub key

there are NO ACCOUNTS. the hub knows your uploads by this pc's hub key, a random key the mod makes at your first upload or report. it's kept in `nocturnebutbetter\hub\identity.json`, encrypted to your windows account, and it's sent only with the requests that need it. it never goes in the log. the hub keeps a scrambled form of it, never the key. only that key can CHANGE OR DELETE YOUR UPLOADS, so BACK IT UP after your first upload. the mod offers that right after the upload, and the buttons are on the my uploads tab:

- back up key... saves it as a text file (`nocturne hub key.txt` unless you name it). keep that file PRIVATE, since it works like a password, and not in a synced folder like onedrive.
- use a saved key... brings it back on another pc or after reinstalling windows. it needs the hub, and the hub only knows keys that have uploaded something. only use a key you backed up yourself, since whoever made the file can also control those uploads. the key the pc had is kept in the hub folder.
- make a new key... replaces a key that was shared by mistake. your uploads stay yours, and the old key stops working at once. back up the new one afterwards.

a key made on another pc or windows install can't be opened here. my uploads then says the key can't be read. use a saved key brings back your backed-up key, and your uploads with it. make a new key only starts a fresh key that isn't tied to your old uploads. either way, the old file is kept.

my uploads lists what you uploaded (up to 200, newest change first) with its state on the hub: on the hub, under review, removed or deleted. a removed entry says why. an entry whose files changed since you uploaded it says so. the panel also shows its downloads, whether its picture shows yet, and how many uploads you have left today. upload a new version... (enter) sends the same battle again as its next version (a battle has to keep its battle id), with the same steps as a first upload (see [share your battle on the hub](#share-your-battle-on-the-hub)). delete from the hub... (del) removes it for everyone after two questions. it can't be undone, but you can upload it again later as a new entry. players who downloaded it keep their copy, and your own battle on your pc isn't touched.

### reports

r or report..., on browse and installed, asks why (copyright, offensive, offensive picture, broken, malicious, spam or other) and takes a note if you want one, up to 500 characters. it needs no upload first: if the pc has no hub key yet, one is made on the spot. the key goes with the report (the hub keeps only its scrambled form), so a second report of the same entry with the same key says "you already reported this." the hub's owner reads every report, and nothing is hidden by reports alone. the hub takes 20 reports a day from one hub key and 50 from one address unless its owner changed that, and after that it asks you to try again tomorrow. reports go to the hub, so they need it to be up.

### when the hub can't be used

without the internet, the page says it can't reach the hub, and f5 or try again asks again. on browse and installed, f5 also reads the downloaded folders again. when the hub doesn't answer within 20 seconds, the page says so, and f5 tries again. on busy days the hub can be down until 00:00 utc: it runs on cloudflare's free plan, whose daily allowance anyone can use up, and the page says how long until it resets. an older mod than the hub takes can't browse, download, update or upload until it's updated, and the top right says "hub: update the mod". reports, delete from the hub and the key buttons still work then. the hub's owner can also close uploads, and downloads still work while they're closed. in all of these, installed and delete keep working.

### what the hub stores

the files you upload, the details in their listings, the name you choose, and the scrambled form of your hub key. reports keep their reason, their note and the reporter's scrambled key, until 90 days after the hub's owner deals with them. downloads are counted with a scrambled form of the address that's changed every day, and the old one is deleted after 2 days. counts of reports and new hub keys per scrambled address are deleted every day. no account, e-mail, steam id, ip address or windows user name is stored. cloudflare itself sees ip addresses, since it runs the network.

on your pc, the hub's own files are in `...\nocturnebutbetter\hub`: the key (`identity.json`, and after you use a saved key the one it replaced as `identity-replaced-<date>.json`), the list of what it installed (`installed.json`), your uploads (`uploads.json`), the page's settings (`settings.json`: the notice, the filters and the key backup offer) and the installed entries' pictures (`thumbs`). a `work` folder holds downloads and uploads in progress, and anything in it over an hour old is removed when the page opens. a damaged file is kept as `<name>.broken-<date>.json`, and a damaged `installed.json` is made again from the downloaded folders. the downloads themselves are in `custombattles\downloaded` and `customcharts\downloaded`. player prefs hold only the online hub setting and the picks made with "use it now". uninstalling keeps all of it (see [remove it](#remove-it)).

## gameplay settings

open options > gameplay, from the main menu or the pause menu. the mod adds eleven rows right above speed mod, in this order: note scrolling, receptor height, note size, lane spacing, note skin, note flares, timing bar, timing bar position, enemy attack opacity, infinite consumables (arcade) and all items (arcade gear). three more sit at the bottom of the page: quick save & load, quick save key and quick load key, which are explained under [quick save and quick load](#quick-save-and-quick-load). [custom difficulties](#custom-difficulties-and-the-chart-editor) have their own page, and the [battle creator](#the-battle-creator) opens from it.

the hit sound and miss sound settings are in options > audio, under sound effects: hit sound, hit sound volume, miss sound and miss sound volume, right after the ui volume slider.

press left or right to change a value, and hold to keep changing it. left and right stop at the ends of a range, while clicking a row steps to the next value and wraps around to the start. note scrolling and note skin go round in both directions, and the on/off rows flip on any press. every setting is saved on that pc as soon as you change it, and upgrading from an earlier version keeps your saved settings.

note scrolling:

- default keeps the original perspective track and hud.
- 2d downscroll uses a flat track with notes moving downward.
- 2d upscroll uses a flat track with notes moving upward.

both flat modes keep the game's artwork, icons, and vertical meter text. player meters sit lower left and enemy meters upper right, each pair close to the chart. the enemy's armor badge, the shield with the damage a hit needs to get through its armor, sits beside the top of its health bar with the enemy's statuses under it, and it keeps the game's own animations. when the enemy info boxes in the top right corner would cover it, and the game's show enemy info setting is on, the badge and the statuses move down to just below them. if the boxes would also cover the top of the enemy's meters, which can happen with five lanes, wide lane spacing, big notes, or a squarer window like 16:10 or 4:3, the meters move down with the badge, and get smaller if they'd otherwise end up lower than the player's, though never below half size. under a very tall stack of boxes the enemy's statuses get smaller too, again never below half size, so all sixteen still fit on screen. default mode puts the game's own badge, statuses and meters back.

receptor height moves the receptors in from their edge of the screen in both 2d modes. it goes from -10% to +30% of the screen height in 1% steps, and 0% is the original spot. a positive value raises the receptors in downscroll and lowers them in upscroll, so the same number works in both directions. negative values move them closer to the edge. in upscroll the receptors start lower, 33% of the screen height below the top, so values above about +17% take them past the middle. the latency calibration and difficulty previews move with it. the setting has no effect in default mode.

note size makes the notes and receptors in both 2d modes smaller or larger, from 50% to 150% in 5% steps. hold notes, the hit flashes, and the key and auto labels under each receptor follow along, and 100% gives back the exact original sizes. lane spacing moves the 2d lanes closer together or further apart, from 60% to 150%, also in 5% steps. the lane backgrounds narrow with smaller notes or closer lanes, so neighbouring lanes keep a gap, and the health and energy meters stay next to the outer lanes. very large notes on close lanes can overlap their neighbours; that's left to you. both settings leave default mode and the audio calibration screen alone. the options previews show them too, with their spacing capped at 110% because there's no more room beside them.

note skin:

- default keeps the game's bar notes.
- circle draws round notes and receptors.
- arrow draws arrows that point left, down, up, and right, like a dance game. a five-lane chart gets a diamond in the middle lane.

the skins use the same colors the game gives its own notes, and the receptors still flash when you hit. hold notes get a narrower trail to match, 70% of the width. the latency calibration preview in options uses the skin too. both skins work in all three scrolling modes, and in 2d upscroll the arrows point the same way on screen as they do in downscroll. the mod draws the skins itself when the game starts, so no image files come with them.

note flares turns off the burst that plays on a receptor when you hit a note or hold one. it starts on and works in every scrolling mode. mine explosions still show, because they tell you that you hit a mine and they carry the mine's sound. the latency calibration screen keeps its flares too. switching it, from the pause menu for example, also changes hold flares already on screen.

timing bar shows how early or late each hit was, and it starts off. it moves with the receptors. in default and 2d downscroll it sits behind them at the bottom of the screen, or in front of them when there's no room behind. in 2d downscroll, if your character would cover its middle, it moves above their head or below their feet, and in front of the receptors if neither fits. in 2d upscroll the enemy stands above the receptors, so timing bar position lets you choose where the bar goes. below enemy, the default, puts it just below the receptors, where only the incoming notes pass over it, and keeps it clear of your character's head at high receptor heights. above enemy puts it along the top of the screen, above the enemy, drawn in front of the fighters so a tall enemy can't hide it. up there it doesn't shake with the field. the position setting only changes 2d upscroll.

early hits land on the rabbit's side on the left and late hits on the turtle's side on the right. the colored bands are the game's okay, good, great, and perfect windows. those windows are narrower on the late side, so the bands sit left of the centre. each tick takes the color of its judgement and fades over 4 seconds, at most 32 show at once, and the white arrow follows your recent average. only taps and the starts of holds count, so misses, auto-played lanes, and hold releases are left off. the bar starts empty in each new battle.

the game's own note colors row, further down the same page, now has a preview under it: your four lanes with a hold, the middle note of five-lane charts, and a mine, drawn in your note skin. it changes as soon as you pick another palette. mines keep the same red whatever the palette, and the enemy's health bar and attacked lanes use that red too, so a palette whose notes look like the mine is the one that will trip you up in a fight. kimothy's edge lanes and chaos come closest.

the note colors row also has a palette of the mod's own, akuma, at the end of the list after the game's six. it's made for controllers and guitar controllers. each lane takes the color of the button it's bound to, like the frets on a guitar: a is green, b is red, x is blue and y is yellow. the middle lane of five-lane charts is orange, like a guitar's fifth fret. the game's default controller layout (lb, d-pad right, x, rb) comes out green, red, blue, yellow, and a lane on any other button, like a shoulder button or the d-pad, keeps its color from that layout. so rebinding one lane never changes another, but two lanes can end up the same color.

on a keyboard the lanes are ALWAYS green, red, blue, yellow, whatever your controller bindings are, and if you switch between the keyboard and a controller the colors switch with you from the next note. notes already on screen keep theirs. when you rebind the controller, the colors change to match within a second, in the preview too. buttons count by their place on the pad, so on a playstation pad cross is green, circle red, square blue and triangle yellow, and a guitar controller's frets count as a, b, y, x and lb. the red is a darker crimson than the mines' red, so red notes and mines stay easy to tell apart. like the game's palettes, akuma colors the notes and holds, and the receptors, flares and mines look the same as always. if you take the mod out while akuma is picked, the game goes back to karma.

enemy attack opacity makes the enemy see-through while it attacks, so the notes behind it stay readable. it goes from 0% (invisible) to 100% (unchanged, the default) in 10% steps and works in every scrolling mode. most attacks are drawn as part of the enemy's own animation, like the firefly's beam, so the whole enemy fades for the length of the attack and comes back when the attack ends, taking about a tenth of a second each way. its shadow and any sidekicks fade with it. effects that only show up as attacks, like the vines that grow over the lanes, stay faded the whole time they're on screen. the game's own flashes and tints still show, and a defeated enemy is left alone so its death plays normally.

infinite consumables (arcade) starts off. when it's on, using a consumable in an arcade battle doesn't use it up. the game's own limits stay, so you still get one use per battle, and the cooldown still applies. it works in the main menu's arcade and in the story's arcade cabinet, for the game's songs and for custom battles that use your own gear. a [custom battle that sets your gear](#gear-and-level) uses up its own consumable as usual and never touches yours. the [arcade gear](#gear-in-the-arcade) keeps its own copy of your items, and that copy follows the setting too: with it off, each use counts it down for the rest of your visit, so every later battle has one fewer.

all items (arcade gear), the last of the mod's eleven rows, starts off. when it's on, the [arcade's gear page](#gear-in-the-arcade) lists every item in the game, not just the ones your save owns. a battle with an item your save doesn't own SAVES NO SCORE and counts for NO ACHIEVEMENTS, and the arcade says so before you start it. it only changes the main menu's arcade gear. if a game hook it needs couldn't be installed, the gear page says so and lists only the items your save owns.

in options > audio, hit sound plays a short tick when you hit a note, and it starts off. hit sound volume sets how loud the tick is, from 5% to 100% in 5% steps (80% to start with). it stops at 100% because the click is already close to the game's audio limiter, and more would only come out about 10% louder. turning hit sound on plays a tick a moment later, after the menu's own click. changing the volume plays one a moment later too, in place of the slider's usual click, so you can hear the new level. the volume plays its tick even while hit sound is off, and holding a direction only plays the last value. the tick is the game's own menu click, so the game's sound effect volume sliders apply to it as well. it plays for taps and the starts of holds that you hit, once for a chord. misses already have the game's own sound, and auto-played lanes and hold releases stay quiet. the tick plays when the game judges your press, a frame or so after the key goes down, plus your audio output delay. if the game's click can't be played, hit sound turns itself off until you restart the game.

miss sound, also in options > audio, turns the game's miss sound on or off. it's the same setting as note miss sounds in options > gameplay, so changing one changes the other. turning it on plays a miss a moment later. miss sound volume goes from 10% to 300% in 10% steps, and 100% is the game's normal level. at any other level the mod plays the miss itself, from its own sound source turned up or down, so the music and the other sounds don't change. at those levels a failed hold plays one miss, although the game sends two. changing the volume plays a miss a moment later so you can hear it, even while miss sound is off, and the game's sound effect volumes still apply. critical misses are already louder than normal ones, and the game's audio limiter stops them getting much louder past about 160%.

reset to default in options > gameplay puts the fourteen gameplay rows back: note scrolling to default, receptor height to 0%, note size and lane spacing to 100%, note skin to default, note flares on, timing bar off, timing bar position to below enemy, enemy attack opacity to 100%, infinite consumables and all items off, quick save & load off, and the quick save and quick load keys to f5 and f9. the game's own reset there also turns miss sounds back on and picks karma for the note colors. like the game's own sound settings, the rest of the audio page has no reset, so the hit sound settings and the miss sound volume stay as they are.

at the "press any key" screen before a battle, alt, tab, and the windows key DON'T COUNT, so alt-tabbing away or opening the start menu won't start the fight. alt or windows held together with another key doesn't count either, since those are windows shortcuts, and altgr counts as alt. tab only blocks on its own, so tab with another key still counts. no key counts while the game window isn't focused, and a shortcut that a hotkey tool or macro sends all at once is still caught. any other key, or a controller button, still starts it. other "press any key" screens, like the title screen, are unchanged. this filter is always on and has no row.

in arcade and high scores, the game marks an encounter you've mastered with gold notes and two sparkles on its card. a chapter button gets the same once every encounter in that chapter has its sparkles: its label turns gold and the two sparkles sit on its top-left and bottom-right corners, for the difficulty you're looking at. a song in the chapter that has no card yet, like one that's still locked, keeps the chapter from turning gold.

## quick save and quick load

quick save & load is one switch at the bottom of options > gameplay, with the quick save key and quick load key rows right under it. it STARTS OFF, and it turns both keys on or off. you can change the keys with the switch on or off.

- the quick save key, f5 to start with, lets you save the story ANYWHERE YOU CAN WALK AROUND. it saves the way the game's own autosave does, into the autosave (`prodautosave.sav`), so the save slots you saved by hand are NEVER WRITTEN OVER.
- the quick load key, f9 to start with, lets you GO BACK TO YOUR LATEST SAVE, the way the game over screen's quick load does. that's your last quick save, unless the game has saved since (an autosave, or a save you made at a save point). a custom battle's [set gear](#gear-and-level) never carries into a quick load.

2.8.0's quick save and quick load HAVEN'T BEEN TRIED IN THE GAME yet (see [what was tested](#what-was-tested)).

to change a key:

1. click its row (quick save key or quick load key). the row shows "press a key..." and a message at the top of the screen says, for example, "quick save key: press a key (esc keeps f5)". the menus underneath don't move, select or go back while it waits.
2. press the new key, with ctrl, shift or alt if you like. the message shows the new key, like "quick save key: ctrl+f6", and it's saved right away.

esc, a left or right mouse click, waiting 10 seconds, or leaving the game window keeps the old key, and the message says "kept the old key". only keyboard keys count, and esc, the windows keys, and ctrl, shift or alt on their own can't be picked. a key works only with exactly the ctrl, shift and alt it was set with: f5 on its own doesn't fire on shift+f5, and ctrl+f5 doesn't fire on plain f5.

left and right on the row step through f1 to f12, and wrap around from f12 to f1. from a key that isn't one of those, right goes to f1 and left to f12. a key picked this way has no ctrl, shift or alt.

one key does one thing, so giving quick load the quick save key takes it off quick save, and that goes for stepping onto it with left and right too. the quick save key row then shows "(none)" until you give it a key again. reset to default in options > gameplay turns quick save & load off and puts the keys back to f5 and f9. the switch and the keys are saved on that pc.

a short message at the top of the screen says what happened: "quick saved", "quick loading...", or why it can't. neither works on the title, in a battle, during a cutscene or dialogue, while the game is fading between screens or paused, in the arcade, or during a chart editor test. the mod's own pages keep their keys, so f5 still tests a chart in the chart editor.

when it can't, the message is "can't quick save" or "can't quick load" and then the reason:

- "in the arcade": the story's arcade, or a song played from the arcade on the main menu. in that arcade's own menus, which sit over the title, the key does nothing, as on the title.
- "during a battle".
- "between screens": the game is loading between screens.
- "during a cutscene".
- "while the game is paused".
- "right now": the game is busy with something else, or it wouldn't let its own pause menu open at that moment (it says no during cutscenes and fades).

the other messages:

- "nothing to quick load: there's no save yet".
- "can't quick save: the game's saves aren't ready".
- "quick load isn't available here": the game over screen, whose quick load it uses, isn't loaded.
- "quick save didn't work (the game said no)".
- "quick save failed (see the log)" and "quick load failed (see the log)".

sometimes a key does nothing and no message shows: when the switch is off, when the game window isn't in focus, on the title, and while one of the mod's own pages is open. those pages are the chart editor (a test played from it counts too), the [battle creator](#the-battle-creator), [gear in the arcade](#gear-in-the-arcade) and [get custom battles](#get-custom-battles). the keys also work at most once every 1.5 seconds.

quick save and quick load use the game's own save and load, which the mod looks up when the game starts. nothing in the game is patched for them. if this version of the game doesn't have one of them, that key only says so ("quick save isn't available: the game's autosave can't be called" or "quick load isn't available: the game's quick load isn't there") and the log says why, and the rest of the mod works as usual. the log is `bepinex\logoutput.log` or `melonloader\latest.log` in the game folder.

## custom difficulties and the chart editor

a custom difficulty is a chart for one of the game's own songs, made by you or by someone else. you can play other people's, and make new ones in the game. everything for it is on its own page: press custom charts on the main menu (right below options), or open the custom charts tab in options (after accessibility). the page shows only its eight rows, with the first one selected. while a battle's song is playing the page turns itself off, so in a fight that tab shows the usual gameplay rows. opening the page reads the custom charts folder again, so files you dropped in by hand show up.

to make and play your first custom difficulty:

1. on the title screen, press custom charts (or open options and the custom charts tab).
2. click chart editor.
3. pick a song: type to filter the list, then click it or press enter. pick a melody if it asks, then start from "new empty chart", a copy of one of the game's difficulties, or one of your custom charts.
4. space plays the music. pick the note, hold or mine tool and click in a lane to place a note. right click deletes.
5. if you like, give the chart a name (f2) and yourself as the author (f3) on the setup tab.
6. press f5 to try it in a real battle from just before the playhead, or shift+f5 from the start. this only works when you opened the editor from the title screen.
7. press ctrl+s. that saves it and picks it for the song.
8. press esc (or exit) to leave. your next fight on that song, in the story or the arcade and on any difficulty, plays your chart. the custom difficulty row, or the custom entry on the difficulty screen, switches it off or to another chart.
9. to share it, press ctrl+e in the editor. that writes one `.nbbchart` file, which the other person adds with import custom chart.

the eight rows, from the top:

- chart editor. "open..." opens the [chart editor](#the-chart-editor) on its song list, with the song from the custom chart song row highlighted.
- battle creator. "open..." opens the [battle creator](#the-battle-creator) for [custom battles](#custom-battles-in-depth).
- import custom chart. click "choose file..." and a normal windows file picker opens in your downloads folder. pick a `.nbbchart` pack (a `.zip` pack works too) or an `.sm` file. the mod checks that the song exists and isn't part of a longer fight, that the chart has the same number of lanes as the song, and that it has a usable tempo (`#BPMS`) and notes that fit, then copies the file into its custom charts folder. if something's wrong, the row says "failed:" and what, like "there is no song named ...". importing the same charts twice just says "already imported". otherwise the row says "imported 1 chart" or "imported n charts", and the custom chart song row jumps to that song.
- custom chart song. the songs you have custom charts for, sorted by the game's own song names (like `Firefly - 1` or `Ant`), or "no charts yet".
- custom difficulty. off, or one of that song's custom charts, shown as "name by author" (just the name when it has no author). the name is the one the chart was given, or the game's difficulty and level when it has none, like "zen 12". when one is picked it plays instead of the game's chart for that song, WHATEVER DIFFICULTY YOU SELECT. off gives you the game's charts back. the pick is saved per song on that pc (`NocturneFlatScroll.CustomChart.<song>.v1`).
- export custom charts. left and right choose "save this song..." or "save all songs...", and clicking opens a save dialog on the desktop. "save this song" takes every custom chart of the song in the custom chart song row, "save all songs" takes every custom chart you have, and they all end up in ONE `.nbbchart` FILE, so the person you send it to imports everything with one click. the row then says "saved n to" and the file name, or "nothing to export".
- custom chart folder. left and right choose "open" or "write game charts", and clicking does the one shown. "open" shows the folder in windows explorer (and makes it first if it isn't there). "write game charts" saves the game's own charts into a `_game charts` folder inside it, as `.sm` files for other editors or to start new charts from, and then opens that folder. each file gets `#NBBSONG` and `#NBBMELODY` lines at the top and is called `<song>.sm`, or `<song> - melody n.sm` on songs with more than one melody. the row says "wrote n charts" (on game 1.0.1 that's 177 charts for 103 songs). the mod never loads charts from `_game charts` itself.
- online hub. on (the default) shows [get custom battles](#get-custom-battles) on the title screen. off hides it, and then the mod never contacts the hub.

reset to default in options doesn't change which custom charts you picked, or the online hub switch.

the game's difficulty screen (options > gameplay > change difficulty, or the pause menu in a fight) gets a custom entry under zen. it shows the custom chart picked for the song ("custom: off" when there's none), and left and right switch between that song's charts and off. in a fight it's about the song you're fighting, and the change starts on your next try. anywhere else it's about the song picked on the custom charts page. if you came to the difficulty screen from options, selecting the entry takes you to that page. in a fight, or at the difficulty prompt of a new game or the gauntlet, selecting it steps to the next chart instead. in a custom battle it says "custom battles play their own charts".

the song's own enemy attacks and lane changes stay in by default, so a custom chart fights the same enemy the same way. a chart can bring its own events instead (see the [technical notes](TECHNICAL-NOTES.md)). on songs with more than one melody, the melody the chart was made for plays from start to end, so the music always matches the notes. the game's songs have 4 lanes, or 5 on a few, and a custom chart needs the same number as its song. when a chart's `#MUSIC` names a song file that's there (next to the `.sm`, or inside its pack), that file plays instead of the game's music. charts copied from the game name files that aren't there, so they keep the game's music.

custom chart scores are SAVED SEPARATELY. they never replace your high scores or melody unlocks on the game's own charts, and each custom chart keeps its own best. when the game checks achievements after a fight, it reads the song's own scores, so a custom chart's score doesn't count as the song's, not even towards its trophy rank.

boss fights that are split into parts (the m1 to m3 boss songs and gauntlets 1 to 4) CAN'T TAKE CUSTOM CHARTS YET. their parts share one score, so a custom part would mix into the game's high score, and import refuses them too. every other song works, including the realm bosses and the island enemies. custom battles play their own charts and take no custom charts. if a chart can't be read when the battle starts, or has the wrong number of lanes, the game's own chart plays instead and the log says why.

a `.nbbchart` pack is a zip with a `manifest.json` and one or more `.sm` files, and each difficulty in a file is one custom difficulty. import copies a pack (or a `.zip`) into the custom charts folder as `<name>.nbbchart`, and an `.sm` into a folder named after its song, adding " (2)", " (3)" and so on when the name is taken. a loose `.sm` needs a `#NBBSONG:<song name>;` line, or has to sit in a folder named after the song. an `.sm` over 8 mb (loose or inside a pack) is refused, and so is a pack whose `manifest.json` is over 8 mb or that was made for a newer version of the mod. an exported pack holds one `.sm` per file, song and melody it came from, each with its own timing and credit, and the song, melody and events go in its `manifest.json`. the editor's bookmarks don't go in a pack. an export is written to a temporary file first, so a failed export leaves nothing behind.

the folder is `%userprofile%\appdata\locallow\pracystudios\nocturne\nocturnebutbetter\customcharts`. the mod reads every `.sm` and `.nbbchart` in it and its subfolders, except `_game charts`, and skips (and logs) a file it can't read. you can also drop `.nbbchart` and `.sm` files into it yourself; they show up the next time you open the custom charts page. the pick and the best score go with the chart's file path in the folder and its place in that file, so renaming or moving a chart's file (or reordering the charts in it) starts it over with no pick and no score. uninstalling keeps the folder.

### the chart editor

the editor works like osu!mania's. open it with the chart editor row on the custom charts page (while it's open, the game's menus underneath are locked), then:

1. pick a song. the list has every game song with a chart, with its lanes and melodies, except the fights that share a score, custom battles, and the game's calibration and fishing test tracks. type to filter, up and down move one, page up and page down move ten, and a click or enter picks. esc closes the editor.
2. on a song with more than one melody, pick a melody. each melody has its own music.
3. start from "new empty chart", a copy of one of the game's difficulties (like "copy of the game's zen 12"), or one of your custom charts for that song and melody. esc or backspace goes back a step.

the song's own music plays in time with the notes, and its tempo and enemy events come along. the music loads in the background: "loading music...", then its length, or "no music:" and why (`Roach - 1` has no music in the editor). a new chart is called "new chart", and a copy is named after its difficulty, like "zen 12 edit". the author starts as the one you typed last time, which is remembered on that pc. starting from one of your custom charts keeps its name and author. a custom battle's charts open in the same editor from the battle creator (see [charting and testing a battle](#charting-and-testing-a-battle)).

the screen is laid out like osu!'s editor, and it all stays on screen whatever shape your window is. along the top: the tabs (compose, timing, events, setup, keys), the song, melody and chart name with an amber * while something is unsaved, and the test, save, export and exit buttons. the toolbox is on the left. the panel on the right is for the tab you're on, with the beat, the bpm, the scroll speed, the snap, the note counts, the selection, the events and the music at its top. in the middle are the lanes, with event flags and labels to their left, scroll speed lines marked like "x2", bookmarks as short blue marks, and the music's waveform on the right. taps are blue, holds violet and mines red. the bottom bar has play/pause (space), the time, and the timeline: click or drag on it to jump. the timeline marks bookmarks in blue, scroll speed changes in green, events in amber and a battle's dialogue lines in violet. messages show on a status line just above the bottom bar. every button can be clicked, and the tools, the compose buttons, test, save, export and play/pause show their key.

- the toolbox. the tools are select (1), note (2), hold (3) and mine (4), and note is the one you start with. below them: the snap (1/1, 1/2, 1/3, 1/4, 1/6, 1/8, 1/12 and 1/16, so triplets too, starting at 1/4), the zoom (21% to 429%, starting at 100%), the playback speed (25%, 50%, 75% or 100%, starting at 100%), note ticks (on to start with), a metronome (off), the waveform (on), the music volume (100%) and the tick volume (45%). the ticks click at 2 khz on each note, and the metronome at 1 khz on each beat. the mouse wheel moves through the song a snap at a time (ctrl+wheel changes the snap, shift+wheel zooms).
- compose. pick a tool and click in a lane to place on the snap (not on top of another note). drag up with the hold tool to draw a hold; letting go where you started makes a tap. right click deletes a note, or the whole selection when that note is part of one. with the select tool, click a note (ctrl+click or shift+click adds or removes one) or drag a box, then drag the selection to move it, or use alt+arrows to move it by a snap or a lane. esc first drops a drag in progress, then clears the selection, and only then leaves the editor. the panel has copy, cut, paste, delete, mirror, reverse, snap to grid, select all, undo and redo. paste puts the notes at the current time, on the snap, and only takes notes copied from a chart with the same number of lanes. moved, pasted, mirrored or reversed notes replace whatever they land on and become the selection. mirror with nothing selected mirrors the whole chart, and reverse needs at least 2 notes selected. undo goes back up to 200 steps, and covers notes, events and scroll speeds.
- timing. the song's tempo changes are listed here (on the game's songs you can see them but not change them). "bookmark here" adds a bookmark at the playhead, on the snap, or removes the one there, and other buttons clear them all or jump to the previous or next one. bookmarks show on the timeline and are saved in the chart. for a scroll speed change, press "scroll speed here", type a number (x0.5, x2, anything between x0.05 and x20; the x and a comma for the point are fine) and press enter. from that beat on, notes scroll faster or slower WITHOUT MOVING IN TIME, like slider velocity in osu!mania. "remove speed here" takes out the change in effect at the playhead. the lanes in the editor show the speed changes, and they play in battle too.
- events. the enemy's scripted events for the song: lane layout changes, its props and animations, helper attacks, vines and other combat effects, camera moves and text. a chart STARTS WITH THE SONG'S OWN EVENTS, so the enemy plays exactly as usual. the list shows seven at a time, with their times. click one to pick it and jump there. "move here" and "copy here" put it at the playhead, "earlier" and "later" move it a snap, "shorter" and "longer" change it by 0.25 s, and "delete" removes it. "add here" adds a copy of the picked event at the playhead, and "edit text" lets you type what an event does: a verb and its values (verbs include `ColumnLayout`, `SpawnPrefab`, `EnemyAnimation` and `ShowText`), up to 160 letters. "song's events" puts them all back. only a chart whose events you changed carries its own.
- setup. the chart's name (f2) and author (f3): click one, type (up to 40 letters) and press enter, or esc to cancel. `:` and `;` turn into spaces, and an empty name becomes "new chart". below them are save, export pack, and open folder, which opens the custom charts folder.
- keys. every editor key can be changed: click an action, then press its new key (with ctrl, shift or alt if you like). that key becomes the action's only key, and comes off any other action that had it. esc keeps the old key, and "reset all keys" puts them all back. a key works only with exactly the ctrl, shift and alt it was set with. keys are saved on your pc (`NocturneFlatScroll.EditorKeys.v1`).

the main keys to start with:

- space: play and pause.
- up and down: forward and back a snap. right and left: a beat. page up and page down: a measure. home: the start. end: the last note.
- tab and shift+tab: finer and coarser snap. `=` and `-`: zoom in and out. `,` and `.`: slower and faster playback.
- 1 to 4, or q, w, e and r: the select, note, hold and mine tools.
- t: note ticks on or off (on a battle's timing tab, t taps the tempo instead). m: metronome on or off.
- ctrl+b: add or remove a bookmark. b and shift+b: next and previous bookmark.
- ctrl+z: undo. ctrl+y or ctrl+shift+z: redo.
- ctrl+a, ctrl+c, ctrl+x and ctrl+v: select all, copy, cut, and paste at the current time. delete or backspace: delete the selection.
- ctrl+h: mirror left and right. ctrl+j: reverse in time. ctrl+g: snap the selection to the grid.
- alt+up and alt+down: move the selection later and earlier. alt+left and alt+right: move it a lane.
- ctrl+s: save. ctrl+e: export as a pack. f2: name the chart. f3: set the author.
- f5: test from here. shift+f5: test from the start.

the keys tab lists all 50, with the ones only a custom battle's charts use (see [charting and testing a battle](#charting-and-testing-a-battle)).

the test button in the top bar, or f5, plays the chart in a real battle from just before the playhead: two bars earlier, kept between 1.5 and 4 seconds. notes before the playhead are left out, and so are events before the start, except lane layouts, camera moves and props, which move to the start. shift+f5, or shift+click, plays it from the start. it works on the game's songs and on custom battles, and it uses what's in the editor, saved or not. a game song tested from partway plays the editor's own music, and from the start it plays the game's (or the chart's own `#MUSIC` file). when the editor has no music for the song, f5 tests from the start with the game's music instead, and the status line says so.

a test starts the way the arcade starts a battle, with the game's sting, fade and ready prompt, on your story save's difficulty (or the arcade's last one). a custom battle's test plays the difficulty tab you're on. you start at full health and you can't lose a test, consumables are off, and the enemy can't be defeated. NOTHING IS SAVED: the results screen and the score are skipped, saves are blocked, a test doesn't count towards achievements, and the latest save is read from disk again afterwards. the pause menu's exit button reads "back to the editor". when the battle ends you're back on the same tab, with the playhead where you pressed test, your undo and your unsaved changes, and the status line says how it went, like "test finished: 96.4%, full combo. f5 tests again from here."

tests ONLY RUN FROM THE TITLE SCREEN, so open the editor from the main menu's custom charts button or from options there, and not while the arcade on the main menu is open. the status line also says when a test can't start yet: while you're typing, while the title screen is still starting or the game is fading back in, while the music is still loading, when there are no notes (or none after the playhead), or when the music ends before the playhead. if the mod couldn't hook the parts of the game a test needs, test only says it isn't available, and the log says why. if a test doesn't start and the game behind stays dark, restart the game.

ctrl+s saves the chart as a custom difficulty and PICKS IT FOR THAT SONG, so your next fight on that song plays it. it needs at least one note. the chart goes into the custom charts folder, in a folder named after the song, as `<name>.sm`. a chart the editor saved before is saved over. an imported or hand-made chart is left alone, and your edit goes into a new file. export (ctrl+e) saves first if needed and then writes one `.nbbchart` file with this chart's notes, events and scroll speeds, ready to send. its save dialog starts on the desktop, and the other person imports it from the custom charts page. esc (or exit) leaves. if a custom difficulty isn't saved, the status line says so first, and pressing esc or exit again leaves without saving. a custom battle's charts ask instead: "save" (enter), "discard" (d) or "cancel" (esc).

a custom battle's charts save into the battle instead. in them, a five-lane battle's middle lane is labelled attack, with your own attack key under it, and dialogue lines said during the song get a lane of their own on the left. [charting and testing a battle](#charting-and-testing-a-battle) has the rest.

saved charts are normal stepmania `.sm` files. you can also edit them in arrowvortex, stepmania, or a text editor: keep the `#NBBSONG` and `#NBBMELODY` lines at the top and the song's `#BPMS`. for the game's songs the game ignores `#OFFSET`, and so does the editor. a custom battle's `#OFFSET` counts, and so does a custom chart's when its own `#MUSIC` file plays (see [charting and testing a battle](#charting-and-testing-a-battle)). scroll speed changes are the standard `#SCROLLS` tag, the editor's bookmarks are `#NBBBOOKMARKS`, and `#NBBEVENTS:chart` makes a chart play its own events. the [technical notes](TECHNICAL-NOTES.md) have the rest of the format.

## the arcade on the main menu

the title screen's main menu gets an arcade button. the game has one already, hidden in its release builds, and the mod shows it with its own help text: "play the songs you've unlocked. scores go to your latest save; your place in the story doesn't change." it NEEDS A SAVE: like continue, it only works once there's a save to continue from, because the arcade keeps its progress in that save. the button only shows when every game hook the arcade needs is in place.

clicking it opens the arcade over the title on your latest save, with the songs you've unlocked there, that save's stats and scores, and the custom tab. it doesn't run the game's own arcade code, which would move your story save to the arcade in the overworld and reload it. if the game's saves aren't ready, there's no save yet, or the latest save or its scores can't be read, the arcade stays closed and the log says why.

scores go into that save's `.score` file (`prodslotn.score`, where n is the slot number), the same way the story's arcade cabinet writes them, so steam cloud syncs them too. a save without a slot number counts as slot 1. your place in the story NEVER CHANGES:

- while the arcade is open, the mod refuses every write to the story's `.sav` files (`prodautosave.sav`, `prodslot0001.sav` and so on). the game's autosave does nothing, and a full save writes only the scores.
- the pause menu's retry and the game over screen's continue would load the story, so both are blocked. a lost song goes straight back to the arcade menu, and if the game over screen does show, its continue button is off.
- when you leave, the mod reads your latest save from disk again, so nothing else the arcade changed sticks (play time, stagger time, items you used). the scores stay, since they're already in the `.score` file.
- it notes the size, time and sha-256 of each story save file when the arcade opens, and logs a warning if any of them changed when it closes.

the visit ends when you close the arcade menu (starting a song doesn't count), or when something on the title menu loads or starts the story, like continue or new game. the story then starts from what's on disk.

custom battles are ARCADE ONLY: they never show up in the story. a custom battle is your own song with your own charts, in up to six difficulties, against an enemy you set up, with gear, a level and dialogue if you want them. you make them in [the battle creator](#the-battle-creator) (the guided path is [make your own custom battle](#make-your-own-custom-battle)) and share them as one `.nbbbattle` file (see [sharing battles as files](#sharing-battles-as-files)). the battles in your battles folder ([where the files are](#where-the-files-are)) show up on the arcade's custom tab.

they get a chapter called "custom battles" after all the game's own chapters, on a tab called "custom" whose label shrinks to fit on one line. it's in the arcade, whether you opened it from the main menu or the story's arcade cabinet, and in high scores.

- the tab only shows when at least one custom battle loads.
- it's always unlocked, and so is every battle in it. each battle has one melody, so the results screen never announces a second one.
- the game's discovery percentage and trophy ranks leave it out, and custom battles change neither.
- battles are sorted by title (then by id). the folder is read again every time an arcade or high scores screen opens, so a battle you just saved or dropped in is there the next time. a battle whose files haven't changed isn't read again.
- every battle offers all six difficulties, and one without its own chart plays the nearest charted one (the easier one when two are as near).
- a battle that can't be read is left out, and the battle creator's list says what's wrong with it.

a battle that sets gear or a level gets an amber tag on its card, right under the melody row: "set gear, level 12", "set gear" or "set level 12". selecting a battle shows a box under the score on the right: what the battle sets, if it sets anything (see [gear and level](#gear-and-level)), then its lore, or who made the song and the charts ("music by ..." and "chart by ...") when it has none. the text shrinks, down to a limit, so what the battle sets shows whole (if it still doesn't fit, its list of items is cut short), and the lore is cut short or left out when there's no room. the game has this box but keeps it hidden. the mod shows it for custom battles, and for any song while [all items](#gear-in-the-arcade) keeps scores out of your save.

the song plays through the mod's own player, without the game's audio engine, and the battle ends a beat after the last note. when the first note comes early in the song, the notes start up to 3 s before the song, so they scroll in from the far end of the lane instead of showing up halfway down it. five-lane battles use the game's centred five-lane layout. custom battles never count towards achievements and give no xp. the arcade keeps their scores under each battle's id, which never changes, so a battle keeps its scores through edits and new titles.

### gear in the arcade

the arcade you open from the main menu has ITS OWN GEAR: a weapon, armor, head, off hand, amulet and consumable that you pick there, kept apart for each save slot. the story's own arcade cabinet keeps your story gear. if you never pick anything, nothing changes and the arcade uses your story gear too.

its title bar gets a gear control on the right that says which gear the next battle uses:

- "gear: story": every slot uses your story gear.
- "gear: arcade": at least one slot uses a pick you made here (an item, or "(empty)").
- "gear: story\*": no slot uses one of your picks right now, but this save slot has picks that aren't used. they can be from an earlier game in the slot or from a file made by a newer version of the mod, or they can be items your save doesn't own, items the game doesn't have, or items for another slot. it also reads this when this save slot has picks but your save or the game's items can't be read.
- "gear: arcade (all items: scores aren't saved)", with the part in brackets in amber: a slot holds an item your save doesn't own (see all items below).

a small dim "g" follows the label, or "view" when you're using a controller.

click the control, press g, or press view on a controller (the select or share button) to open the arcade gear page. it opens when no song is running, the arcade itself is taking input, no other page of the mod is open, and no chart editor test is running. the control isn't one of the game's buttons, so the arcade's keyboard and controller navigation never land on it. if the arcade's title bar isn't laid out the way the mod expects, the control doesn't show, but g or view still opens the page. without the mod's battle gear hooks there's no gear control, g and view do nothing, and the arcade uses your story gear.

the page is a full-screen list over the arcade, headed "arcade gear (save slot 1)" (with your save's slot number). its rows:

- "weapon", "armor", "head", "off hand", "amulet" and "consumable", each with what the next battle uses there, like "consumable: mega potion x2", or "(empty)".
- "use the gear i set before", only when this save slot's gear is from an earlier game (see below).
- "use my story gear in every slot".
- "done".

if the game's items aren't loaded yet or your save can't be read, the page lists only "done" and says why.

an amber tag after a slot row says where that slot's gear comes from:

- "arcade": your pick is used.
- "arcade, not owned": your pick is an item your save doesn't own, allowed by all items.
- "arcade, used up this visit": your consumable has none left this visit, so the slot is empty.
- the item's name and "not owned: story gear": your save doesn't own your pick, so your story gear is used.
- "no such item: story gear": the game has no such item, or it goes in another slot.
- "used up this visit": your story consumable has none left this visit.
- no tag: plain story gear.

the line above the list explains the highlighted row: an item's description and its id, "nothing in this slot.", or why your story gear is used instead. the note under the rows gives your level and health upgrades from your save, says which items are listed, and says "your story save never changes: this gear is used only in this arcade."

to change a slot:

1. choose it. its picker opens, like "weapon for the arcade (save slot 1)", with the highlight on what's chosen now.
2. pick "story gear: ..." for whatever your story has equipped in that slot, now and later, "(empty)" to leave the slot empty, or one of the slot's items. items are sorted by name, and only the ones your save owned when the arcade opened are listed (every item in the game with all items on). the game's test items show "(test item)" after the name, and a consumable shows how many you have left this visit, like "x2". tags say "current", "not owned" or "used up this visit". if what's chosen now isn't in the list (not owned, or no such item), it gets its own row right after "(empty)", tagged "current, not owned" or "current, no such item", so opening a picker just to look changes nothing.
3. your pick is saved to the file at once, and you're back on the slots. choosing the row that's already current changes nothing. if the file can't be written, your pick is still used until you leave the arcade.

"use my story gear in every slot" asks "use your story gear in every slot?", with "keep my arcade gear" highlighted first and "use my story gear" under it. "use my story gear" removes all of this save slot's picks, so every slot uses your story gear again. your story save doesn't change either way.

the page works with the mouse, the keyboard and a controller:

- up and down move the highlight, page up and page down move 10 rows, and the mouse wheel scrolls three rows a notch. on a controller the d-pad or the left stick moves it, and holding repeats.
- enter, z or a click picks, and so does a on a controller (cross on a playstation pad).
- esc, x or a right click goes back, and so does b on a controller (circle). on the slots screen, going back closes the page, like "done".

the button at the bottom right reads "done" on the slots and "back" on a picker, with its key after it: "esc/x", or "b" with a controller. while the page is open the arcade underneath can't move, and when it closes the arcade is on the same chapter, at the same scroll, with the same selection. the page closes by itself when you leave the arcade, a song starts, or the arcade stops taking input.

every battle in that arcade uses this gear: the game's songs, and custom battles that don't set gear. a custom battle that sets its own gear still uses its own (see [gear and level](#gear-and-level)). with the arcade gear, your health upgrades and pet are always your save's own, and so is your level, except in a custom battle that sets a level: that one plays at the battle's level, with your arcade gear. achievements count as usual, since the items are yours (custom battles never count towards achievements). your own gear is back after every battle, whether you win, lose or quit. if the arcade gear can't be put in, you fight with your story gear and the log says so.

your story save NEVER CHANGES. the page only reads it, and a battle runs on a copy of your items. the gear is kept in `nocturnebutbetter\arcadegear.json`, beside your custom battles (see [where the files are](#where-the-files-are)), one set for each save slot. it's outside the game's save folder, so steam cloud and the game's erase options don't touch it.

only items your save owned when the arcade opened can be used. an item your save doesn't own falls back to your story gear, and the page says why. the pick is kept and comes back if your save gets the item, or with all items on. the same goes for an item the game doesn't have, or one in the wrong slot: your story gear is used, and your choice is kept.

the game allows one consumable use per battle. with [infinite consumables](#gameplay-settings) off, a consumable you use in the arcade is gone until you leave the arcade, as it always was, so every later battle in that visit has one fewer. once none are left, its slot is empty for the rest of the visit. it doesn't fall back to your story consumable. it's full again the next time you open the arcade. with [infinite consumables](#gameplay-settings) on, using one doesn't use it up.

a new game in the same save slot starts with story gear. the mod notes your save's play time when a slot's picks start, and when the loaded save has more than 1 s less play time than that, the picks are from an earlier game and aren't used. the page says so for 12 s, with both play times, and offers "use the gear i set before" to bring the old picks back for this game. changing any slot instead starts over: the earlier game's other picks go, and only your new pick is kept.

with all items (arcade gear) on in [options > gameplay](#gameplay-settings), the pickers list every item in the game, test items too. a battle with an item your save doesn't own SAVES NO SCORE and COUNTS FOR NO ACHIEVEMENTS: the results screen still shows how you did, but the score, and any full combo or melody it unlocked, never reaches your `.score` file. the arcade tells you before you play:

- the gear control reads "gear: arcade (all items: scores aren't saved)".
- the box on the right says "all items: scores aren't saved." for every song the gear applies to, the game's own included. a custom battle that sets its own gear doesn't get it, since its gear wins.
- the slot's tag reads "arcade, not owned", and the page's note says "all items: in a battle with an item this save doesn't own, scores aren't saved and achievements don't count."

with only owned items in the slots, scores and achievements count as usual, even with the setting on. a consumable your save doesn't own gives you one per battle, so it never runs out. if you turn all items off again, those picks fall back to your story gear until you turn it back on or your save gets the item. all items needs a few game hooks, and if one couldn't be installed, the page says so and lists only the items your save owns.

if `arcadegear.json` can't be read, the arcade uses your story gear and leaves the file alone until you change a slot (or choose "use my story gear"). that moves it aside as `arcadegear.json.bad` and starts a new one. a file made by a newer version of the mod is left as it is: the arcade uses your story gear, and the slots can't be changed. a file over 1 mb isn't read. the [technical notes](TECHNICAL-NOTES.md) describe the file's format.

## custom battles in depth

this is the full reference for the battle creator. for the short version, see [make your own custom battle](#make-your-own-custom-battle).

### the battle creator

open the custom charts page (custom charts on the main menu, right below options, or the custom charts tab in options) and choose battle creator, the second row. it won't open while the chart editor is open, and the game's menus underneath are locked until you close it. its list starts with:

- new battle... pick a song file (`.ogg`, `.mp3`, `.wav`, `.flac`, `.m4a` or `.wma`, up to 512 mb), then 4 or 5 lanes. in five lanes the middle lane is played with the attack key. the lane count CAN'T CHANGE LATER: for the other number, make another battle. the creator makes a folder named after the song (with " (2)" and so on when that name is taken), copies the song into it, and starts an empty chart at 120 bpm with a mantis as the enemy. the title starts as the song file's name, and the charter as the charter name you last typed. the new battle opens on its info page.
- new battle from an osu!mania beatmap (.osz)..., tagged "beta, not recommended" (see [below](#importing-an-osumania-beatmap-beta)).
- import a .nbbbattle file... unpacks a shared battle into a new folder you can edit.
- open the battles folder, in windows explorer. it makes the folder first if there isn't one yet.

your battles follow, each with its artist, lane count, charted difficulties, what it sets, and how many problems it has. highlight one and the hint line names its first problem. click one, or use up, down and enter. f5 reads the folder again and esc closes the creator. folders come first, by title, then zips, then the battles you got from the hub. a zip can't be edited as it is, so choosing one offers to unpack it into a battle folder you can edit. a battle whose `battle.json` can't be read says so in its row. when the game's own chart reader finds nothing playable in a battle's chart, the arcade skips that battle and the list says why.

the mod reads `.ogg` and `.wav` songs itself. `.mp3`, `.flac`, `.m4a` and `.wma` go through windows' own media decoders, so on a windows n edition without the media feature pack, convert those to `.ogg` or `.wav`.

a battle has seven pages down the left, which tab and shift+tab cycle through:

- info. the title (about 16 letters fit on the arcade card, and the field tells you when yours doesn't), the artist, the charter, and the lore (up to 600 letters, and shift+enter starts a new line). the lore shows in the arcade's box, under what the battle sets. about 5 lines fit there, fewer when what the battle sets takes up part of the box. a "play from" field and a "play 10 s" button let you listen to part of the song. the [arcade card](#the-arcade-card) is on the right.
- song. the song file, its length, the offset, the bpm and the lanes, with "replace the song..." (the charts stay as they are, so check their timing afterwards) and "edit charts". the bpm and the offset are set in the chart editor, on its timing tab.
- charts. the six difficulties, beginner, novice, adept, expert, elite and zen, with their note counts (taps, holds and rolls, not mines). click one to chart it in the chart editor. in the arcade, a difficulty without its own chart plays the nearest one.
- enemy, then art. see [the enemy and its art](#the-enemy-and-its-art).
- gear & level. see [gear and level](#gear-and-level).
- dialogue. see [dialogue](#dialogue).

the panel on the left also shows the lanes, the charted difficulties, either "ready for the arcade" or up to three problems in plain words, and "unsaved changes" when there are some. the top bar has the title and the folder name, with an amber * while something is unsaved. the bottom bar has save (ctrl+s, which works even while you're typing in a field), export .nbbbattle..., open folder, delete battle, upload to the hub... (only while the [hub](#share-your-battle-on-the-hub) is on), and back (esc). up, down and enter walk through the page's buttons and then the bottom bar's.

a field that gets a value it can't take (like letters for hp) stays open and says why, and nothing changes until you fix it or press esc. a comma works as the decimal point too.

back with unsaved changes asks whether to save, and "don't save" sends any song, pictures, art or speakers' pictures you added since the last save to the recycle bin. the creator has no undo, except on the dialogue page.

save writes `battle.json` to a temporary file first and then swaps it in, and it keeps any keys it doesn't know. if saving fails, the creator stays open with your changes. after a save, the songs, pictures, art and speakers' pictures you added, replaced or removed while editing go to the recycle bin once the saved battle no longer uses them, and only ever from its `audio`, `images`, `art` and `portraits` folders (a speaker's picture that the dialogue's undo can still bring back waits until you leave the battle). a save leaves the other files in the folder alone. delete battle asks twice and then moves the whole folder to the recycle bin. the creator NEVER DELETES ANYTHING FOR GOOD: if windows can't recycle something, it stays where it is.

the battle creator also opens from options during the story, but its tests only run from the title screen.

### charting and testing a battle

a battle's charts open in the same [chart editor](#the-chart-editor): click a difficulty on the charts page, or "edit charts" on the song page. the battle's own song plays. the editor adds these for battles:

- a difficulty bar with the six tabs, beginner to zen. a green dot marks the charted ones. an empty tab offers "start empty" or "copy from" another tab, and ctrl+pgup and ctrl+pgdn switch tabs. every tab shares the tempo, the events and the scroll speeds.
- lanes. four-lane battles are played with the lane keys (d f j k unless you changed them). in five-lane battles the middle lane is played with the attack key, and the editor labels that lane attack, with your own attack key under it (space by default). your own attacks are off in five lanes, so the enemy ONLY TAKES DAMAGE from player attack events. the events tab's "player attack" button adds them.
- timing against the song: "set bpm", "tempo change here" and "remove tempo change", "first beat here", and buttons that move every beat 1 or 10 ms against the music (`[` and `]` move it 1 ms, and with shift, 10 ms). tap tempo (t) listens to your taps and offers the bpm it hears, rounded (enter) or with two decimals (shift+enter). "loop 2 bars" plays two bars over and over with the metronome, so you can move the beats until the clicks sit on the music. the timing tab warns you when beat 0 comes before the song starts (a positive offset), since notes before 0:00 can't be played, and the creator lists it as a problem when there are notes there.
- events. they're the battle's own, and a new battle has none. "add here" and "player attack" make one at the playhead, and a player attack hits the enemy in four lanes too. enemy animations and combat effects use the stand-in enemy's own. events that spawn a prop or show text show nothing in a custom battle.
- a dialogue lane. lines said during the song show in a violet lane on the left and as marks on the timeline, and a line that stops the song is drawn across the lanes. the events tab switches between events and dialogue. there you can add a line at the playhead, edit its text and speaker, move it, make it show longer or shorter, make it stop the song, copy or delete it, or open it in the creator. every difficulty shares the lines.
- the setup tab has save, open folder, start the tab's chart empty or delete it, copy from another tab, and "dialogue in tests" on or off.

ctrl+s saves every difficulty into the battle's chart file (at least one needs notes), and, when you changed the dialogue here, `battle.json` too, which also saves your other unsaved changes in the creator. exporting is the creator's job.

test (f5, or shift+f5 from the start) plays the tab that's showing in a real battle, with your unsaved notes and events and your unsaved changes in the creator, like a new title, enemy or dialogue. from the start it plays the dialogue before the song, every line during it, and the lines after a win. from partway it starts two bars before the playhead, plays the lines said during the song from there, then the lines after a win. lines after a loss never play in a test, since a test can't be lost. the dialogue page has its own test buttons too.

### the enemy and its art

the enemy page picks a game enemy to stand in. with "game enemy's art", the battle looks and fights like that enemy. with "custom art", it fights like that enemy, with its attacks, sounds and stats, and looks like your own pictures or videos from the art page. the picker lists each enemy's own stats. the scripted bosses (yako, nocturne, caged wei, sue, winged wei, kitsune and ladybug) only show with "advanced bosses: on", with a warning that they may not play well, and they can't be used with custom art. a new battle starts with the mantis.

under "stats" you can set hp, damage, attack windup, energy per miss and passive energy. a blank field keeps the enemy's own number. the info boxes in the top right of the battle can be the enemy's own or the battle's own: an enemy name and up to three boxes, each with a title and up to three lines of text. the enemy name only shows there. in a custom battle the stand-in isn't a boss, has no special attack, and drops nothing.

custom art has four animations: idle, attack, hurt and defeat. only the idle is needed. without an attack or hurt animation the idle shows instead, and without a defeat the hurt one does. choose... asks what kind of file it is first, and then the windows picker lists only that kind:

- image. one still `.png` or `.jpg`, shown as it is. an image is never cut into frames.
- gif. an animated `.gif`, played with its own timing.
- video. an `.mp4` (h.264) or `.webm` (vp8) video.
- sprite sheet. one `.png` or `.jpg` with the frames in a grid, read left to right, then down, like the game's own. the creator guesses the grid, and you can set the columns, rows, first frame and number of frames.

each animation then shows only the settings that apply to it: frames a second or speed, how long a still picture shows, where the attack's hit lands (worked out for you, or on a frame or time you pick; the parry window opens a quarter of a second before it), its size compared with the idle, where it sits, a see-through colour (the corner colour, green, blue, black, white or magenta, with a range), mirroring, and whether the defeat loops. the preview shows the enemy on the battle's screen or close up, plays and steps through each animation (space, left and right), marks the hit, and lets you drag the art into place. the whole enemy has its own size, place, mirror, crisp pixels and shadow settings.

the creator checks each file from its first bytes before it copies it into the battle's `art` folder, and it says in plain words why it refuses one: webp, bmp, tiff and heic files, a file of the wrong kind, one that's too big, sideways or upside-down phone videos, and 10-bit video. pictures and gifs can be up to 32 mb, and videos up to 256 mb and 1920 x 1080. an idle video can run up to 60 seconds, and hurt and defeat videos up to 10. an attack video can be longer, but only its first 6 seconds play.

VIDEOS HAVE NO SEE-THROUGH PARTS unless they're a webm with transparency, so an mp4 shows as a rectangle. gifs and pngs don't have that problem, and turn into frames... fixes it for any video. it makes the video a sprite sheet at 12, 15 or 24 frames a second, so a see-through colour works and it plays on any pc. it shows its progress, and esc (or leaving the page) stops it with nothing changed. the sheet goes in the battle's `art` folder with up to 240 frames. after that, one click on the corner colour cuts out a flat background like a green screen. "back to the video" undoes it, with the video's settings as they were, until you save.

in the arcade, the art starts loading when the battle's card stays highlighted for a moment, which gives it time to be ready for the first fight, videos included. if it can't load, the fight says "custom art couldn't load" (or "custom art wasn't ready yet", and the next fight tries again), and the enemy looks like its stand-in.

### the arcade card

the info page's right column shows the battle's arcade card at the arcade's own size, made the same way the arcade makes it, so what you see there is what the arcade shows. the card's picture slot is SQUARE. the best picture is a square one, 76 x 76 for pixel art or 456 x 456 for drawings and photos. see-through parts show the menu behind, and the corners are rounded off. choose image... takes a `.png` or `.jpg` of up to 16 mb and copies it into the battle's `images` folder. remove takes it off, and the arcade shows a plain card.

for a picture that isn't square, fit chooses between filling the square (what a battle's first picture starts with; a new picture keeps the fit you chose) and showing the whole picture with see-through bars. crop moves the square across the picture, in 10% steps or to a number you type, and you can drag the picture as well. crisp pixels is on, off, or auto, which keeps small pictures (up to 128 pixels across the slot) crisp and smooths bigger ones. the arcade keeps a card at 512 x 512 at most.

### gear and level

the gear & level page sets what the player fights with:

- gear. "player's own gear", or "set gear for this battle" with a weapon, armor, head, off hand, amulet and consumable, each empty or one of the game's items. "show test items" adds the game's test items to the pickers. the items only list once the game has loaded them, so if the page says they aren't loaded, load a save and try again.
- health upgrades, with set gear. the player's own, or a number from 0 to 99.
- level. "player's own level", or "set level for this battle", from 1 to 20. it starts at your level when a save is loaded, and the page shows what the level adds over level 1. level changes strength, regen and critical. gear that sets a stat outright (like the pool noodle) wins over the level.

in a battle that sets gear, the player has exactly those items, and empty slots stay empty. key items, followers, the pet and money stay theirs. their gear, health upgrades and level COME BACK AFTERWARDS however the battle ends: a win, a loss, or quitting from the pause menu. nothing is saved and no xp is earned. the game allows one consumable use per battle. a set-gear battle uses up its own consumable, never the player's, and [infinite consumables](#gameplay-settings) doesn't apply to it.

in the arcade from the main menu, "your gear" is the [arcade gear](#gear-in-the-arcade) you picked there: a battle that doesn't set gear uses it, and it's what comes back after a battle that does.

players see what a battle sets before they start. in the arcade, the box on the right says it, like "sets your level and gear. level 12 (yours: 8). gear: only ancient katana, alloy vest, potion. health upgrades: 0. yours come back afterward." ("only" means some slots are left empty), and the card gets an amber tag under its melody row: "set gear, level 12", "set gear" or "set level 12". the page shows the same text under "players see in the arcade".

### dialogue

a battle can talk the way the game's bosses do. the dialogue page has a tab for each moment:

- before. lines before the song starts. the ready prompt waits while they run, and you press a key for each next line, like the game's own talks.
- during. lines said while the song plays, each at its own time. the game's dialogue box comes and goes by itself and the notes keep coming, so these lines ignore keys. the box's background turns see-through while they show, so you can still read the notes behind it. the text, the name tag and the face stay solid. a line can stop the song instead, like a boss's talk between songs: the song and the notes stop, you read the lines at your own pace, and the song goes on a second after the last one. the pause menu can't open during a stop.
- after a win. lines after the last note, before the results.
- after a loss. lines when you're beaten. the notes freeze and the song stops, then the lines run before the battle ends.
- speakers. your own speakers.

a line is said by karma (the player, right after "new speaker..." in the speaker picker, standing on the left), by one of the game's characters with their own faces, by the narrator (a narration box with no picture), or by one of your own speakers. type in the picker to jump to a name. each line has an expression, a side, a name tag (up to 24 letters, or the speaker's own), and up to 150 letters of text. a line during the song has a time (type one like 1:02.5 or 62.5, or a beat like beat 96, which moves with the notes) and how long it shows. other lines wait for a key, or go on by themselves after a set time, and then a key can't skip them.

reply (ctrl+r) adds a line right after the chosen one, said by the other side: karma answers anyone else, and when karma speaks, whoever spoke before her (not counting the narrator) answers. the other buttons add, copy, move (a beat earlier or later for lines during the song), delete and undo lines. their keys, while you're not typing: insert adds, ctrl+d copies, delete deletes, ctrl+up and ctrl+down move, ctrl+z and ctrl+y undo and redo, and space plays the preview.

the preview draws the box the way the game does, in the game's fonts, with play and a close-up view. while it plays, enter goes on and esc stops it. "test in a battle" and "test from this line" run the battle through the chart editor's test and come back to the page. the chart editor's [dialogue lane](#charting-and-testing-a-battle) edits the lines said during the song too.

your own speakers get a name, a picture (`.png` or `.jpg`, up to 4 mb and 2048 pixels a side), more pictures for other expressions, a side, a mirror switch and a nudge. pictures up to 256 x 240 show pixel for pixel, tiny ones are scaled up by a whole number (up to 4 times), and bigger ones are scaled down to fit. a battle can have 12 speakers of its own. deleting one that has lines asks whether to delete those lines too or give them to the narrator. a battle holds up to 60 lines before the song, 200 during it, and 30 each after a win and after a loss.

the lines never reach your save: the game doesn't count them as scenes you've seen. a line that can't be read is left out, and if the dialogue fails some other way, the battle plays without it.

### importing an osu!mania beatmap (beta)

"new battle from an osu!mania beatmap (.osz)..." in the battle creator's list makes a battle from an osu!mania `.osz` file: its song, its 4-key or its 5-key charts, its tempo and its speed changes. a battle has one lane count, so when the beatmap has both you pick one, and import it again for the others. it's marked BETA, NOT RECOMMENDED, because osu!mania charts are made for osu! and may not play well as battles:

- the game's own timing windows and health apply, in place of osu!'s judgement and hp drain.
- notes are put on the game's beat grid, which can move them a little, and holds too short for the grid become taps.
- osu! has no attacks. in five lanes the import adds a player attack every 8 bars (you can turn that off), and the enemy only takes damage from those.
- a battle has one set of speed changes for all its difficulties, so only one difficulty's speed changes come over.
- apart from generated test beatmaps, only five real beatmap sets have been tried, and only outside the game.

TEST PLAY EVERY CHART before you share the battle.

the `.osz` is only read, never changed. after "reading the beatmap...", a summary shows what the battle will get: the lanes, each difficulty and the slot it goes in (choose one to move it or leave it out), whose speed changes to use (or none), the tempo, the song and the card, the player attacks for five lanes, a warning when notes come in the first 1.5 seconds (choose it to leave them out), and a list of what was changed or left out. only 4-key and 5-key difficulties come over, up to six. "make the battle" builds it and opens it on the charts page, where everything is editable like in any other battle. the background picture becomes the card if it's a png or jpeg. the background video, hit sounds and the storyboard are left out. a broken beatmap is refused with the reason, like "can't import that beatmap: it isn't a .osz file (it can't be opened as a zip)."

### sharing battles as files

export .nbbbattle..., on the creator's bottom bar, saves the battle and writes it as ONE `.nbbbattle` FILE with everything in it: the song, the charts, the pictures, the art and the dialogue. it can't be saved inside the battles folder. anyone with the mod can import it with "import a .nbbbattle file..." in the battle creator, which unpacks it into a new folder they can edit. they can also drop the file, or a battle's folder, into their battles folder. the arcade plays a `.nbbbattle` there as it is, and the creator lists it as a zip and offers to unpack it when they choose it (the zip then goes to the recycle bin).

every battle has an id that never changes, and its scores are kept under it. importing a battle you already have makes it a separate one with a new id. a folder copied in windows explorer keeps its id, so the arcade shows only one of the two. choosing the one the arcade skips (the later one by name) in the battle creator offers "make it a separate battle", which gives it a new id so the arcade shows both, or "edit it as it is". the scores stay with the one the arcade showed.

to share a battle online instead, see [share your battle on the hub](#share-your-battle-on-the-hub).

### where the files are

custom battles live in `%userprofile%\appdata\locallow\pracystudios\nocturne\nocturnebutbetter\custombattles`, which the mod makes when it needs it. "open the battles folder" in the battle creator opens it. a battle is a folder with a `battle.json` in it, or a `.nbbbattle` file. the arcade also looks one folder deeper, so you can group battles in folders. battles you get from the hub go in `custombattles\downloaded`.

a battle the creator made has:

- `battle.json`: the title, song, enemy, gear, level, card and dialogue. you can edit it by hand, and the [technical notes](TECHNICAL-NOTES.md) list every key.
- `charts/song.sm`: every difficulty's chart in one stepmania file.
- `audio`: the song.
- `images`: the card picture.
- `art`: the enemy's art, and any sprite sheets made with turn into frames.
- `portraits`: your speakers' pictures.

a `.creator-work` folder inside the battles folder holds battles while they're being made or unpacked, and the creator cleans it out when it opens. a battle is only moved into place once it's complete, so one that fails halfway leaves nothing behind. videos from zipped battles are copied to `...\nocturnebutbetter\cache\enemyart` before they play, and that folder is trimmed to 1 gb. custom battle scores are kept in your save's `.score` file. the arcade's gear is in `...\nocturnebutbetter\arcadegear.json`. uninstalling keeps all of it.

## the title screen

the title screen and the nocturne card in the startup intro read "nocturne+". a + follows the logo, just after its last letter and raised a little, in the game's menu font and off-white like the logo. the title's logo changes as the story goes on, and the + follows whichever one is showing. on the intro card it fades in and out with the logo.

at the bottom right of the title's menu, the game's version ("nocturne 1.0.1") gets two more lines in exactly its style: "nocturne+ 2.8.0" and "by terrydavisgaming". they're part of that same text, so they only show where it does, and leaving the title puts the game's text back. none of it shows during play. if the longer text would come within half a line of the bottom of the screen, it moves up to make room. if the mod can't find the game's version there, it adds nothing and the log says so.

like the rest of 2.8.0's changes, this HASN'T BEEN TRIED IN THE GAME yet (see [what was tested](#what-was-tested)).

## what was tested

2.8.0's changes HAVEN'T BEEN TRIED IN THE GAME yet. those changes are the new name and its files, the + and the version on [the title screen](#the-title-screen), [quick save and quick load](#quick-save-and-quick-load), and the way the installer handles the old `nocturneflatscroll` copy. everything else is the same as in 2.7.0. the 2.8.0 installer was only checked to parse, and the part that turns off the old copy ran with made-up copies. the whole installer HASN'T BEEN RUN ON A GAME FOLDER yet. the 2.8.0 dlls were built without the game, against copies of the game's interfaces rebuilt from 2.7.0's dlls. built that way, 2.7.0's source comes out byte for byte the same as the published 2.7.0 dlls, so everything 2.7.0 did works the same. the [technical notes](TECHNICAL-NOTES.md) explain how.

version 2.7.0 was tested in the game on nocturne 1.0.1 with bepinex 6.0.0-be.788 and with melonloader 0.7.3. that took 33 runs with qa builds (the release source plus test hooks): 19 on bepinex and 14 on melonloader. every run took a snapshot of the player's plugin, settings and saves first and put it back afterwards. no run changed a story `.sav` file, and no melonloader run logged a "native->managed trampoline" error. everything below ran on both loaders unless it says otherwise.

- [gear in the arcade](#gear-in-the-arcade): 170 of 170 checks passed on bepinex and 124 of 124 on melonloader, plus 7 of 7 on each after the game was started again. the page opened with a click, with g and with a pad's view button, and gear was picked with the mouse, the keys and a simulated pad. the firefly song was fought on the picked gear, with quits from the pause menu, and a battle that sets its own gear used that gear. on bepinex, two custom battles were also fought on the picked gear: one that sets the level, which played at that level, and one that ended in a loss. a consumable used in one battle was gone in the next. while all items was off, an item the save doesn't own fell back to story gear. with all items on, fighting the firefly song with an item the save doesn't own left the scores alone and skipped the achievement check, while the other battles' scores went into the save's `.score` file. the arcade under the page didn't move, and the story's gear was the same afterwards. on bepinex the test runner lost track of one of the 79 inputs it sent. that was a fault in the runner, and the game's own checks all passed.
- [get custom battles](#get-custom-battles), against a stand-in for the hub: the hub's own server code ran on the test pc, with stand-ins for cloudflare's database and file storage. 128 of 129 checks passed on bepinex and 71 of 71 on melonloader. the one that failed was the first key press of the run, down from quit game on the title screen, which left the selection where it was. the title test passed when it ran again by itself, and in the melonloader run.
  - bepinex covered browsing and searching (for "café" and for japanese text too), the filters and sorts, "more by this uploader", downloading a battle and a pack and playing both, a damaged download and one cut short (both were refused, and the damaged one was reported, where a second report said "already"), esc stopping a download, new versions published during the run and updated in place, "use it now", deleting, the installed and browse tabs with the hub stopped, the hub's slow down, too busy and too old answers, and a title full of the game's text formatting tags, which showed as plain text. it also covered uploading a battle, a pack and a battle from the battle creator (each with the rules and the summary first), a new version, "delete from the hub", reports, backing up, restoring and replacing the hub key, and online hub off.
  - melonloader covered the page and its notice, downloads, updates, the damaged download and its report, deleting, a first upload with its key backup, playing a download, and online hub off and on.
  - the mod sent no request before the notice was accepted or while online hub was off, and no log held a hub key.
  - the server's 162 offline tests passed, and so did the hub client's 350 offline checks.
  - against the real hub at `hub.nocturnbutbetter.com`, both loaders opened the page over https, connected, showed the empty list, searched and opened every tab. NO UPLOAD OR DOWNLOAD has been tried on the real hub yet.
- the armor badge in 2d: 7 battles on each loader, with the game's antlion fight and a five-lane custom battle. they ran in default, 2d downscroll and 2d upscroll, with bigger notes, wider lanes and a higher receptor height, with a switch to default and back during the fight, and with show enemy info off. in the 2d modes the badge showed whenever the enemy had armor and was never under the info boxes, and it moved below them with the meters. after a switch to default, the badge and the statuses were back at the game's own spot, within 0.02% of the screen height. the default view is the game's own, so with five lanes the game's info boxes still cover the badge there.
- akuma: 20 battles on each loader, in 4 and 5 lanes, all three scroll modes and all three skins. 18 of them used akuma, with the pad's bindings changed and switches between the keyboard and the pad during the song. all 2,477 notes on bepinex and 2,480 on melonloader had the color that akuma's rules in [gameplay settings](#gameplay-settings) give their lane, apart from 7 notes on bepinex that came right after a rebind and still had the old color, which the once-a-second reread of the bindings allows. all 225 middle notes in five lanes were orange, the mines looked the same as with karma, and the preview matched in 8 layouts. the crimson measured 16 in delta e 2000 from the nearest mine color. picking karma on the pause screen gave the last two battles the game's own colors.
- the tests from 2.6.x, run again:
  - the chart editor's test: all six runs on bepinex, two on melonloader.
  - set gear 226 of 226, and the level 233 of 233.
  - the battle creator's art page 85 of 85.
  - dialogue in the arcade: lines during the song came within 11 ms of their time.
  - the dialogue page 93 of 93.
  - custom enemy art in its 10 test battles.
  - five quits from the pause menu in a row, with no options error.
  - enemy attack opacity at 50%: 12 of 12 attacks faded after a custom enemy's health reached 0.
  - note speed with speed mod at 170 in 2d upscroll: every battle's first notes came in at their normal speed. one five-lane battle had a single 61 ms frame near its start on both loaders. the clock ran up to 40 ms ahead for that frame, while the notes stayed within 3% of their mid-song speed.
  - on bepinex, also the `.osz` import with 136 of 136 checks, the loss lines, a dialogue file, five lanes and broken entries in dialogue, turn into frames 29 of 29, and the arcade card pictures 81 of 81.

the 2.7.0 installer's checks covered both loaders, switching between them, upgrades from the published 2.1.2, 2.2.0, 2.4.1, 2.5.0, 2.6.0, 2.6.1 and 2.6.2, and removal, all in test copies of the game files.

version 2.6.2 changed the installer and the way custom songs start. the installer passed 81 checks in test copies of the game files. 32 of them were new ones for finding the game: fake steam folders that list a library on a missing drive (the error players saw), an offline network share, folders that are gone, folder names with square brackets and letters like ü, steam's old library file format, and locked or unreadable steam files. in each case it found the one test game, and the same checks fail on 2.6.1's installer. it also installed and uninstalled without being told the game folder, and it upgraded the published 2.6.1 and 2.6.0. the real installer upgraded the test pc's own game from 2.6.1.

two custom battles on mp3 songs were played twice each on bepinex. one had 169 speed changes and its first note at 0:00, the other had an `#OFFSET`. the notes started 2.02 s and 0.61 s before the song, and the start logs showed steady frames (the longest was 35 ms), with the song and the notes within 30 ms of each other. the stutter tests (250 ms and 700 ms frames forced at the start) were run on 2.6.1. there the notes lurched to 1.3 to 2.3 times their speed for a moment, in the game's own firefly song too. 2.6.2's change was checked against a model of the game's clock with the same stutters, not in the game. the creator's new "tempo change here" wasn't tried in the game.

version 2.6.1 was tested in the game on nocturne 1.0.1 with bepinex 6.0.0-be.788 and with melonloader 0.7.3, the same way as 2.6.0 below. each fix got a test that goes through the player's own path, and each test was run on the old code first to show that it catches the bug. these ran on both loaders unless it says otherwise.

- quitting from the pause menu: five fights in a row, each paused the way esc does and left with the pause menu's exit button, one of them through the pause menu's gameplay page. every later fight had its music and moving notes, and options > gameplay logged no errors. on 2.6.0 the same test stopped at the second fight, which had no music.
- timing with speed mod at 170 in 2d upscroll: the notes moved at a steady speed from the start. in battles with an `#OFFSET` of -2, -1.53 and -0.2 s (and +0.5 s on bepinex), the first note reached the receptors within 5 ms of where the chart editor puts it. 2.6.0 ignored the offset, so its notes were that far off the music, and a battle whose first note is on beat 0 started with notes already at the receptors. a battle whose first note is 0.2 s in started its notes 1.32 s before the song, and they scrolled in from the far end of the lane. bepinex also ran it in the default 3d view.
- enemy attack opacity at 50%: 12 of 12 attacks faded after a custom enemy's health reached 0 (0 of 12 with 2.6.0's code), and the firefly fight's enemy still played its death before the results.
- custom enemy art in the 10 test battles: the hurt picture showed when the enemy was hit, and the defeat picture only when the enemy died at the end.
- the 2.6.0 tests again: set gear 228 of 228 and the level 233 of 233, dialogue in the arcade, and the chart editor's test (all six runs on bepinex, two on melonloader). on bepinex also the battle creator's `.osz` import with all its checks (its 0.3 s offset battle now started 1.64 s early and put its notes where the editor does), the loss and dialogue file runs, and the dialogue page's 93 of 93. on melonloader also the creator's art page.
- no run changed a `.sav` file, and no melonloader run logged a "native->managed trampoline" error.

turn into frames and the arcade card pictures weren't run again for 2.6.1, since nothing they use changed.

version 2.6.0 was tested in the game on nocturne 1.0.1 with bepinex 6.0.0-be.788 and with melonloader 0.7.3. the melonloader test ran in a separate copy of the game folder. every run saved the player's plugin, settings and saves first and put them back afterwards, and checked that the story's `.sav` files hadn't changed.

custom battles, on both loaders unless it says otherwise:

- four-lane and five-lane battles played to the end of their songs in the main menu's arcade. their scores went into the save's `ProdSlotN.score`, and the `.sav` files stayed the same.
- set gear and infinite consumables passed 228 of 228 checks: a win, a retry, quitting from the pause menu, a real loss, extra health, a save check in the middle of a battle, and infinite consumables on and off.
- the per-battle level and the arcade's box passed 233 of 233 checks.
- the battle creator passed 155 of 155 checks. every page opened, and the checks covered the pickers, play 10 s, save, export, the charts, the art page and the `.osz` import. 20 broken beatmaps were refused in plain words. imported battles played in the arcade, and a five-lane one was beaten with the player attacks the import adds.
- custom enemy art was checked in 10 test battles: a gif, a sprite sheet, a png, an mp4, a webm, a big video, broken files, a zip, and art switched off. videos showed on the first fight.
- turn into frames passed 29 of 29 checks. a green-screen mp4 was cut out with the corner colour, esc stopped it, and "back to the video" brought the video back.
- arcade card pictures passed 81 of 81 checks on 16 cards: square, tall and wide pictures, tiny pixel art, a 4000 x 3000 picture, crops, and bad values in `battle.json`.
- the chart editor's test ran from the playhead and from the start, on battles and on the game's songs. each time the score was skipped, nothing was saved, and the editor came back. bepinex ran all six test runs. melonloader ran two of them, both from the playhead: a battle and a game song.
- dialogue: lines before the song held the ready prompt, lines during the song came within 6 ms of their time in a see-through box, and lines that stop the song paused it and started it again. win lines came before the results, and a real loss played its loss lines with the music stopped. those ran on both loaders. on bepinex it also worked in downscroll and upscroll, from a separate dialogue file, in five lanes, and in the chart editor's test from the start and from partway, and a battle with 10 broken dialogue entries still loaded and played. the story's seen-scene flags didn't change.
- the creator's dialogue page passed 93 of 93 checks: every tab, adding, copying, replying and undoing lines, the speaker picker with karma at the top and typing to jump, faces and sides, a new speaker with a picture, save and reopen, and the chart editor's dialogue lane. its preview put the portraits where the game does.
- on melonloader, no run logged a "native->managed trampoline" error.

2.5.0's [custom difficulties and chart editor](#custom-difficulties-and-the-chart-editor) were tested on both loaders. the custom charts page opened from the main menu and from the custom entry on the difficulty screen. the editor was driven with real key presses and mouse clicks, and its firefly music matched a reference render to the sample. a chart saved in it played in the firefly battle instead of the game's chart, with its score kept apart. a pack was imported, exported and imported again, and the real windows file pickers opened in front of the game. "write game charts" wrote 177 charts for 103 songs, and an `.sm` with no song was refused with a message saying how to name one. a chapter button turned gold with its sparkles once every card in the chapter showed theirs.

the earlier features were tested on 2.4.x, on both loaders, in the firefly battle with the game's auto-play. that covered the circle and arrow skins in all three scroll modes across the range of receptor heights, note sizes and lane spacings, the game's move from four lanes to five and back, and the default layout coming back exactly (70 of 70 values). the timing bar in each of its spots, note flares off and on, and enemy attack opacity at 0%, 30% and 60% all worked. the hit sound and miss sound were recorded from the pc's audio output, and each level came out as set. on bepinex, lanes pressed through the game's own input check counted as player taps. at the ready screen, tab, windows, both alt keys and alt+j didn't start the fight, and neither did tab or windows sent the way a macro sends them. j on its own did. the note colors preview matched the game's own colors in every palette and skin.

some things haven't been tried. for 2.7.0:

- the real hub was only browsed (it was empty). NO UPLOAD OR DOWNLOAD has been tried on it yet.
- no real controller was used. the gear page, the hub page and akuma got a simulated pad, and in akuma's runs most switches between the keyboard and the pad were made by setting the game's control scheme directly, the way the game does when it sees another device.
- akuma's colors were read from the notes as the game drew them, since screenshots couldn't tell (flares, attacks and the enemy draw over the lanes).
- custom battles haven't been played with a real keyboard or a controller (the tests use auto-play or press lanes through the game's own input check), and nobody has played them by hand from the story's arcade cabinet.
- very long songs haven't been tried in the arcade.
- the `.osz` import hasn't met real beatmaps in the game, only the generated test set and five real sets checked outside it.

from earlier versions:

- custom charts haven't been tried on five-lane songs, with their own events, or from packs made in other tools.
- the editor hasn't been used with a controller.
- the chapter badge was checked by lighting a chapter's cards on screen, without really mastering a chapter.
- no controller was plugged in for the ready screen test.
- the skins haven't been tried on a five-lane chart.
- only the firefly battle was played for the display and sound settings, so the vines, other enemies' attack effects, mines, and critical misses at high volume weren't seen or heard in a fight.

## source and licenses

the build instructions for both loaders and the compatibility details are in the [technical notes](TECHNICAL-NOTES.md). the online hub's server, with its api notes and the owner's runbook, is in the [`server`](server) folder of this repository. the release zip leaves it out.

the package has no game assets, no game-generated assemblies, no saves and no account data. you need your own installed copy of nocturne.

this is an unofficial community mod. the [mod license](LICENSE-MOD.txt) and the [third-party notices](THIRD-PARTY-NOTICES.txt) cover the licensing.
