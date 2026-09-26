# nocturne but better 2.6.0

formerly nocturne flat scroll. the files, the plugin id, and the saved settings keep that name, so upgrades from earlier versions keep working.

the package targets windows x64, steam app 1374860, nocturne 1.0.1, build 25487568, unity 2022.3.62f2. 2.6.0 adds custom battles: arcade-only battles with their own song, charts, enemy, gear, level, and dialogue, made in an in-game battle creator and played from an arcade button on the main menu. it also fixes the results screen's high-score info, which 2.5.0 garbled in every battle. the installer pins game code, metadata, and original layout assets. it refuses unknown versions rather than assuming an update is compatible. previous experimental static layout patches must be restored before using this portable package.

## loaders

the mod ships as two dlls built from the same source:

- `payload\nocturneflatscroll.dll` is the bepinex plugin. it installs as `bepinex\plugins\nocturneflatscroll\nocturneflatscroll.dll` and uses the official bepinex 6.0.0-be.788+5b766a3 windows x64 il2cpp loader, which the package bundles unchanged. the bundled loader includes its .net runtime.
- `payload\nocturneflatscroll.melonloader.dll` is the melonloader mod. it installs as `mods\nocturneflatscroll.melonloader.dll` and needs melonloader 0.7.3 or newer, which you install yourself. melonloader uses the system .net 6 runtime and downloads it if it's missing.

the installer's `-loader` option chooses between them. `auto`, the default, uses melonloader when it will start: `melonloader\net6\melonloader.dll` exists, a melonloader proxy dll such as `version.dll` is in the game folder, and `userdata\loader.cfg` doesn't turn it off. otherwise it uses bepinex. it refuses to add bepinex to a game with an enabled melonloader, because both loaders hook the same unity startup call and only one of them starts. it also refuses melonloader older than 0.7.3. installing for one loader disables this mod's copy for the other, so only one copy runs.

game-specific interop files are generated on the recipient's pc during the first launch with either loader and are not included. a .net sdk is only needed to rebuild the source.

the optional fullscreen script applies a small, reversible byte patch to the recipient's `nocturne_data\globalgamemanagers` to prefer directx 11. it validates both the input and resulting sha-256 hashes. the 1.0.1 update left the file the same size with the same graphics api bytes at the same offsets (only its build guid text changed), so the patch is the same three bytes plus four padding bytes, with new pinned hashes. no original or modified game asset is distributed with this package.

## settings

options > gameplay gets ten rows above speed mod: note scrolling, receptor height, note size, lane spacing, note skin, note flares, timing bar, timing bar position, enemy attack opacity, and infinite consumables (arcade). the seven custom chart rows follow them, but they only show on the custom charts page (see below); their lists are rebuilt each time the page refreshes. options > audio gets hit sound, hit sound volume, miss sound, and miss sound volume under sound effects, after ui. each switch is a copy of the gameplay page's own on/off switch (the one "note miss sounds" uses) and each slider a copy of the audio page's ui volume slider, added when the page opens and linked into its up and down navigation. the page's slider label shows the slider's fraction of its range, which is only the volume when the range ends at 100, so the copies get their own label text. the audio page has no reset button, so neither do these rows.

note scrolling cycles between default, 2d downscroll, and 2d upscroll. default keeps the original track and hud. the two flat modes keep the game's artwork and vertical bars, with player meters lower left and enemy meters upper right, close to the chart.

receptor height runs from -10% to +30% in 1% steps. it moves the flat field along its own vertical axis, which faces the combat camera, so the receptors change height without changing depth or lane width. one step is 1.9527 field units, which is 1% of the screen height at the combat camera's distance of 169.11 units with its 60° field of view. positive values move the receptors in from their edge of the screen: up in downscroll, down in upscroll. in upscroll the receptors start lower, at 33% of the screen height below the top, so values above about +17% take them past the middle. the calibration and difficulty previews use a 360-unit-high canvas, so there a step is 3.6 units. left and right stop at the ends; clicking the row cycles through every value.

lane spacing (60% to 150%) multiplies each lane's x position around the field's centre line. the game positions lanes with an animator on `FieldPivot` (`ColumnsAnimationController`), which moves them during the battle intro, the four-to-five lane change, chart events, and pull attacks, so the mod applies the factor every frame and treats any x it did not write itself as the game's new position. leaving 2d, or hiding a battle, puts the game's positions back. the lane background, its dots, and the press beams are drawn at min(size, spacing) of their width, so they never grow past the notes or into each other.

note size (50% to 150%) scales the parts the game never scales itself: the note heads (including mines and fakes), the hold end cap and start cap, the hold lines' width, the hold offsets and the note mask, the receptor border, decal and glow, and the mod's skinned receptors. every size is written from the value captured before the mod changed anything, so 100% and default mode give back the exact original values. the game's key-press pulse on the receptor's centre dot keeps working because only its parent is scaled. the lanes' press beams keep the game's own height animation and only start at the larger receptor's edge. hit flares are resized as the game plays them, and the key and auto labels move away from larger or skinned receptors. the hud meters leave room for receptors above 100%. menu previews get both settings around their own lane centre, with spacing capped at 110%. default mode and the audio calibration screen are left alone.

note skin swaps the game's shapes-drawn notes and receptors for sprites the mod draws when the game starts: signed-distance masks rendered to mipmapped textures, so no image files ship with it. the skinned note keeps the game's sorting order and takes its colors from the same `CombatNoteColorSet` the game applies to its own notes. the receptors copy the game's fill, border, and center colors each frame, and a skin-shaped glow replaces the rectangular hit glow. hold trails are drawn at 70% width. arrows point left, down, up, and right; an odd lane count gets a diamond in the middle lane. the flat upscroll field is mirrored, so the mod swaps the up and down arrows there to keep them pointing the same way on screen. picking default turns the game's renderers back on and restores the hold widths. the menu previews get skinned receptors too, and so does the audio calibration screen, which keeps its own layout. only the latency calibration preview was checked in-game.

note flares uses the same `NocturneCombatNoteColumnBehaviour.NoteFlareSingle` and `NoteFlareHold` postfixes that size the flares. when it's off, the lane's newest flare (`NoteFlarePool.LastNoteFlare`) gets a scale of zero. that hides it while its animator keeps running, so the pool and the pairing of hold starts and ends work as usual. mine flares (`isMine`) and calibration flares (`isCalibration`) stay visible; the mine flare's animation is also what plays the mine sound. switching the setting also updates hold flares already on screen. the game has its own hidden `ShowNoteFlares` setting, but it also drops the mine explosion and its sound, so the mod doesn't use it.

enemy attack opacity runs every frame after the game's animators. once a second, and straight away when a new enemy, piece of enemy art, sidekick, or attack object appears, it collects the enemy's sprite renderers under `CombatEnemyView.enemyParent` (the art and its shadow, not the hit effects beside it), the sidekicks' renderers, the attack objects under `CharacterFieldView/CameraPlanePrefabParent` (skipping the player's helpers), and `FieldPivot/Canvas/Vines`. the enemy counts as attacking while the game's `isAttacking` flag is set or its animator's current state, or the next one during a transition, has the `Attack` tag. the enemy, its shadow, and its sidekicks ease to the setting over 0.12 s; attack objects and the vines, which get a canvas group of the mod's own, take it straight away. only alpha changes, and a colour the game sets itself (stuns, hits) becomes the new base. sprites whose shader is `Shader Graphs/DissolveSpriteV2`, which ignores vertex alpha, borrow a copy of the enemy's `enemySpriteMaterialWithOcclusion` while faded and get their own material back afterwards. a hit during the fade makes the game copy whatever material the sprite has for its flash (`renderer.material`); a copy of the borrowed material still counts as borrowed, so the sprite's own material comes back when the attack ends. once the enemy is defeated (`murdered`), its sprites get their alpha back and keep the material they have, because the game's death effect may already be running on it. at 100% nothing is written, and anything still faded is put back. a custom battle's enemy art counts as attacking while its attack animation shows.

infinite consumables (arcade) starts off. the game uses the equipped consumable through `CombatSimulator.UseConsumable`, and its effect takes the item out of the inventory with `PlayingInventoryManager.RemoveItem`. while that runs in an arcade battle (`ArcadeUtility.IsRunning`) with the setting on, a prefix on `RemoveItem` skips the removal from the player's own inventory. the game's one use per battle and its cooldown still apply. it covers the game's songs and custom battles that use the player's own gear, from the main-menu arcade or the story's cabinet. a battle with set gear uses up its own consumables as usual and never the player's (see custom battles).

timing bar hooks `CombatManagerV3.OnNoteJudged`. it counts taps and hold starts whose result source is a player tap, and skips misses, auto-played lanes, scripted input, and judging after the fight is decided. hold releases are left out: holding past the end passes on its own, so a release can't be late, and letting go early reports the rest of the hold as the offset, so they would only pull the average early. the bar spans ±0.17 s. the bands are the game's okay, good, great, and perfect windows, which are narrower on the late side, so the bands sit left of center. ticks use the color of their judgement zone and fade over 4 s; the marker follows an exponential average with a weight of 0.15 per hit. the bar is a child of the note field, so it follows the scroll mode, the receptor height, and the default perspective track. the fighters come from a second, orthographic camera drawn after the note field, so nothing on the field can draw over them, and the bar is placed around them instead:

- default: 27 field units behind the receptors, pulled in to stay on screen, or in front of them when there's no room behind.
- 2d downscroll: behind the receptors, 27 units plus 12 per 100% of extra note size. if that spot overlaps the player sprite's band (7% to 24% of the view height, at the centre), the bar moves above the head when there's room below the receptors, otherwise below the feet.
- 2d upscroll, "below enemy": in front of the receptors, on the side the notes come from, at the receptor's reach plus 5 units (17 units for the skins at 100%). the enemy stands above the receptors there. the bar draws under the notes, and it stops above the player's head at high receptor heights, going back behind the receptors only if the receptors themselves reach that far.
- 2d upscroll, "above enemy": the bar is rebuilt as a child of the fighters' camera (`Camera (Combat Orthographic)`, found through the battle's `CharacterFieldView` canvas) on its `CombatOrthographic` layer, sorted on `Combat` at orders 20 to 28: over the fighters (1 to 4) and under the game's hud (100). it is scaled by 2 × orthographic size ÷ 195.27 so it keeps the field size, and centred 5.5 field units below the top of the screen with the average marker underneath. it no longer follows the field's shake there.

the spot is chosen twice a second, or at once after a setting changes, and each test leans 3 units toward the current spot, so the field's hit shake (about 1 unit on screen) can't make the bar jump between spots. behind the receptors, the bar also keeps clear of the key and auto labels.

the average marker always sits on the side away from the receptors. the bar keeps a length of at least 30 units when the lanes are close.

hit sound hooks the same `CombatManagerV3.OnNoteJudged` call and plays for player taps and hold starts whose tap result is a hit, at most once per frame so a chord makes one tick. unity's own audio is switched off in this game (`m_DisableAudio`), so the tick is a wwise event the game already ships: `SFX_Global_Menu_Select` (id 1218145993, 33 ms) from `SFX_Global_Menu.bnk`, which the game loads at start-up. the mod posts it on its own emitter object and sets that emitter's output bus volume to the hit sound volume for each default listener, so the game's music is untouched. the game's sfx and ui sfx volumes still apply. wwise applies gains above 1.0 (up to 16), but the click already reaches the master bus's peak limiter less than 1 db under its -1 dbfs threshold, so higher gains come out only about 10% louder, and the setting stops at 100%. if the event can't be posted, the mod reloads the bank once, then turns the sound off for the session. the menu plays the same click when a value changes, so the preview tick waits 0.2 s, and holding a direction previews only the final value.

miss sound volume prefixes `AudioController.PlayMissSound`, the only place the game plays `SFX_Global_Combat_NoteMiss` and `SFX_Global_Combat_CritNoteMiss`. at 100%, or with miss sounds off, the game's own code runs unchanged. otherwise the mod posts the same event on its own emitter, with that emitter's output bus volume set to the setting for each default listener, and skips the game's post. a failed hold makes the game post the miss twice in one call, so the mod posts at most once per frame. the game sets its `Combat_SFX_MissCharge` rtpc globally, so the mod's emitter gets the same pitch and volume change. the normal miss reaches the master limiter 12 to 23 db under its threshold, so 300% stays clean; the critical miss has about 4 db of room in its loudest state, so it stops getting louder above about 160%. the switch is the game's own `NocturneSettings.PlayMissedSoundEffects` (playerprefs `PlayMissSoundEffects`), which the gameplay page's reset turns back on. changing the volume previews a normal miss 0.2 s later, loading `SFX_Global_Combat.bnk` once if it isn't loaded.

the note colors preview is a row of the mod's own, 400 x 48 canvas units, placed right after the game's `StateToggle_NoteStyle` row (`GameplayOptionsMenu.noteStyleButton`) in the page's scroll list, so it scrolls, clips, and fades with the page in the main menu and the pause menu. it isn't selectable and blocks no pointer input, and it hides with the note colors row. it draws lanes in the current column style (`ColumnStyleManager.CurrentColumnColors`), a four-lane chord from `NoteStyleManager.GetColorsForColumn(i, 4)` with a hold in the first lane, the five-lane middle note from `GetColorsForColumn(2, 5)`, and a mine in the prefab's fixed colors (#d64734 body with pink marks). the notes use the mod's circle and arrow sprites, or bar sprites drawn the same way from the game's bar note measurements for the default skin. color1 tints the body, color2 the accents and the hold body, and color3 the line work, as on the game's notes. a postfix on `NoteStyleManager.SetStyle`, which the row calls on every change and the gameplay reset calls too, redraws it straight away. the mine stands in for the game's reds: the enemy health bar, attacked lanes, and receptor marks are all within a few steps of it in color distance.

the "press any key" screen before a battle (`ReadyCountdownView.Update`) asks `NocturneInput.Global.AnyKeyDown`, which reports the game's `Global/AnyKey` input action. that action is bound to `<Keyboard>/anyKey` and 16 controller buttons. a postfix turns the result false when the window isn't focused, when left or right alt or windows is held, or when tab is the only key down. altgr counts as held alt. the postfix reads `wasPressedThisFrame` on those five keys every frame the prompt waits, which makes the input system track their presses, so a key that goes down and back up inside one update (a hotkey tool or macro sending a whole shortcut at once) is still caught. nothing else calls that method, so the title screen, the idle video, dialogue, and results screens are unchanged.

the title text hooks `MainMenu.Activate` and the intro coroutine `TitleScreen._RunTitleSequence_d__17.MoveNext`. it adds textmeshpro labels under each logo, copies the menu's pixel font, and on the intro card matches the logo image's alpha every frame.

the settings live in unity playerprefs under `NocturneFlatScroll.Mode.v3`, `NocturneFlatScroll.ReceptorHeight.v1`, `NocturneFlatScroll.NoteSize.v1`, `NocturneFlatScroll.LaneSpacing.v1`, `NocturneFlatScroll.NoteSkin.v1`, `NocturneFlatScroll.NoteFlares.v1`, `NocturneFlatScroll.TimingBar.v1`, `NocturneFlatScroll.TimingBarPosition.v1`, `NocturneFlatScroll.EnemyAttackOpacity.v1`, `NocturneFlatScroll.InfiniteArcadeConsumables.v1`, `NocturneFlatScroll.HitSound.v1`, `NocturneFlatScroll.HitSoundVolume.v1`, and `NocturneFlatScroll.MissSoundVolume.v1`. an older `NocturneFlatScroll.Direction.v2` setting is migrated when present. reset to default on the gameplay page sets default, 0%, 100%, 100%, default, on, off, below enemy, 100%, and off. installing on another pc does not transfer settings or saves.

## custom difficulties

the game keeps every chart as a stepmania `.sm` text asset on its `SongData` (`beatmaps`, one per melody) and parses it at the start of each battle with `Gameframe.SMReader.NotesLoaderSM.LoadFromText`, inside the private `WwiseConductor.CreateBeatmap(SongData, int, int, int)`. the song's six difficulties are the six `.sm` difficulty slots: edit is beginner, beginner is novice, easy is adept, medium is expert, hard is elite, and challenge is zen. songs with three melodies splice two of them together at `SongSplitMeasure`, and a wwise switch picks the matching music.

a prefix on `CreateBeatmap` hands the game a chart the mod builds instead, when the song has a custom difficulty picked. the chosen `#NOTES` block is copied into all six slots, so it plays whatever difficulty is selected, and the chart's `#ATTACKS` (the game's events: lane layouts, prefabs, combat effects, hard cuts) are replaced with the song's own unless the chart asks to keep its own. the result goes through the game's own parser. if anything fails, the prefix logs it and lets the game build its own chart. a prefix on `WwiseConductor.Initialize` sets both melodies to the chart's melody on multi-melody songs, so the music and the splice point match the chart, and checks the lane count (4, or 5 for the gauntlets and glaucus) again.

the same `Initialize` prefix points the song's score key at `NocturneButBetter/<chart key>` for the battle: it sets the song's `overrideHighScoreKey` and `highScoreKey`, which the game's `SongData.HighScoreKey` reads, and a postfix on `CombatManagerV3.ExitCombat` puts the song's own key back once the score is recorded. the game records the score as usual, so a custom chart's scores are kept apart from the game's. `ScoreManager.TryRecordScore` and `IsNewHighScore` aren't patched. `TryRecordScore` returns a small struct (`ValueTuple<bool, int>`), which il2cppinterop's patch trampoline returns as a pointer, and 2.5.0's patch on it gave the results screen a garbage "new high score" flag and previous score in every battle. the game's end-of-battle achievement check (`AchievementManager.CombatEnded`) reads every arcade song's scores through the same key, so a prefix gives the song its own key back for that check and a postfix puts the custom key back for the results screen. a custom chart's clear never counts towards the song's trophy rank.

the score redirect only applies to the playing song's own `SongData.HighScoreKey`. the multi-part fights (the m1 to m3 boss songs and gauntlets 1 to 4) share one `HighScoreKey` across their parts (`overrideHighScoreKey`), and each part starts its own `Initialize`, so a custom part can't be told apart from the rest of the fight when the score is recorded. those songs refuse custom charts for now, at import and at battle start. songs that override the key without sharing it, like the realm bosses and island enemies, work.

before a chart is handed to the game, the mod checks the chosen block: a known chart type, a positive first `#BPMS` value, and note rows exactly as wide as the lane count. after the game's parser runs, the result must have steps and timing data. anything else logs the reason and plays the game's chart. the same check runs at import, so a broken file is refused with the reason. all 941 difficulty blocks in the game's own charts pass it. like stepmania, the reader ends a tag at its `;` or at the next line that starts with `#`, so a missing `;` doesn't swallow the next tag (one of the game's own charts has an `#ATTACKS` without one). files over 8 mb are refused.

charts live in `Application.persistentDataPath/NocturneButBetter/CustomCharts` and are found by scanning it (every subfolder except `_game charts`) the first time they're needed and again whenever the gameplay page or the custom charts page opens. the pick for each song is saved in playerprefs as `NocturneFlatScroll.CustomChart.<song>.v1`, holding the chart's key. song names are the `SongData` asset names, like `Firefly - 1`, `Ant`, or `Gauntlet1`; `_game charts` lists them all. custom battles' own songs aren't game songs: they take no custom charts and aren't in the chart editor's song list.

a loose `.sm` names its song with `#NBBSONG:<song>;` or by sitting in a folder with the song's name. `#NBBMELODY:<1-3>;` picks the melody (1 if missing), and `#NBBEVENTS:chart;` keeps the chart's own `#ATTACKS` instead of the song's. every `#NOTES` block becomes one custom difficulty. its name is the block's description, or the difficulty and meter when that's empty, and `#CREDIT` is the author.

a custom chart can bring its own song too. when its `#MUSIC` names a file that's there, next to the `.sm` or inside its pack, that file plays through the mod's own player instead of the game's music (see music without wwise, under custom battles). charts copied from the game name the game's own source files, which aren't there, so they keep the game's music. with its file playing, a custom chart's `#OFFSET` counts, baked in as a battle's is; on the game's music it stays ignored.

a `.nbbchart` pack is a zip with a `manifest.json` and the `.sm` files it lists:

```json
{
  "format": 1,
  "title": "my charts",
  "author": "someone",
  "charts": [
    { "source": "game", "song": "Firefly - 1", "melody": 1, "events": "song", "file": "charts/firefly-1-m1.sm" }
  ]
}
```

`source` is `game` for the game's own songs; other values are skipped (custom battles have a format of their own, `.nbbbattle`). `events` is `song` or `chart`. packs with a higher `format` are refused. export writes one `.sm` per source file, song, and melody with the picked difficulties, so each keeps its own timing and credit, and the manifest. import checks that every chart names an existing song with the same lane count and passes the check above before copying the file in. song names match the game's without regard to case and are stored in the game's spelling. import also skips a file whose charts (song, melody, events, name, author, and notes) are all there already.

the file pickers are the standard windows open and save dialogs (`GetOpenFileNameW`, `GetSaveFileNameW`), run on their own single-threaded apartment thread and owned by the game window, so the game keeps drawing and the dialog stays in front. the rows check for the result once a frame. each kind of file remembers the folder it was last picked from, per pc.

"write game charts" saves each loaded song's own `.sm` (177 charts for 103 songs on 1.0.1) into `_game charts`, with `#NBBSONG` and `#NBBMELODY` added at the top, as starting points that keep the song's timing.

the custom charts page is the gameplay page (`GameplayOptionsMenu`) in another mode, not a new panel. the main menu gets a copy of its options button, and `OptionsPanel` a copy of its gameplay tab placed after accessibility. both open the gameplay page with a flag set that switches off every other row of its scroll list (the game's and the mod's) and turns on the seven chart rows (chart editor, battle creator, import, song, custom difficulty, export, and folder), linked into up/down navigation of their own. opening any other tab, opening options fresh, or leaving options (checked twice a second while the flag is on) clears it and turns the hidden rows back on, so the gameplay page in the pause menus shows its usual rows.

the difficulty screen (`DifficultyMenu`) gets a copy of its last button after zen. it isn't in the menu's own `buttons` list, so the game's click and preview handling leave it alone. a prefix on `CustomButton.OnMove` turns left and right on it into choosing the song's chart. selecting it outside a fight pops the difficulty screen off the game's menu stack (`PanelStackPresenter` > menu system > current stack) and opens the page once options is back; in a fight it steps to the next chart instead. the song is the one `WwiseConductor` is playing, or the page's song outside a fight. in a custom battle the entry says that custom battles play their own charts. the entry's title is shortened in code rather than with textmeshpro's ellipsis, which blanks a line whose box is shorter than the font's line height.

## chapter badges

in arcade and high scores (`GenericArcadeMenuV2`), `ArcadeSongGroup.SetRanks` shows the card's two star sprites (`starGroup`) and colours full-combo melodies with `fullComboMelodyColor`. the mod records which `ArcadeCategory` each chapter button opens (a postfix on `PopulateCategoryButton`) and, four times a second while one of those screens is open, counts each category's visible cards and how many have `starGroup` on. when all of them do, the chapter button's label takes `fullComboMelodyColor` and gets a copy of a card's star group, with its coloured stars moved to the button's top-left and bottom-right corners at 80% size. otherwise the label gets its own colour back and the copy is hidden. it follows the difficulty tabs, because the game re-ranks the cards when they change.

## chart editor

the editor is a screen-space overlay canvas of its own (sorting order 32000, scaled from 1920x1080 matching width and height equally) with the game's pixel font, drawn from pooled `Image`s and text. the panels are anchored to the screen's edges (a 64 px top bar, a 92 px bottom bar, a 300 px toolbox and a 430 px side panel) and the lanes sit in what's left, so the layout holds at any aspect ratio, and long text wraps or is cut short inside its panel. every button is drawn from a small widget that has its label, its active and visible state and its click as functions, so the same action runs from a click or a key. it reads the keyboard and mouse through the input system's `Keyboard.current` and `Mouse.current`. while it's open the event system's navigation events (move, submit, cancel) are off, and every open `MenuPanel` gets `allowBacktrack` set to false (and `TryBackAction` does nothing), so the menus underneath don't react; they come back once the key that closed the editor is released. (the game's own event system input lock, `GetEventSystemInputLock`, also holds keyboard events back until later frames, so the editor doesn't use it.) the battle creator is built from the same pieces.

keys are a table of 50 actions, each bound to a key plus exact ctrl, shift and alt state, saved in the player prefs as `NocturneFlatScroll.EditorKeys.v1`. rebinding takes the next key that isn't a modifier; a binding another action already had is taken from it.

events are the chart's `#ATTACKS` list (`TIME=s:LEN=s:MODS=verb args`, which is how the game stores its lane layout changes, props, animations, helper attacks, combat effects, camera moves and text). the editor loads the song's own list for the chosen melody and shows it; only when something in it changes does the saved chart get `#NBBEVENTS:chart` and its own `#ATTACKS`. without that tag a custom chart keeps using the song's events in battle, so the song's enemy behaviour is the default and can't drift from the game's.

scroll speed changes are stepmania's `#SCROLLS` tag (`beat=ratio`, ratios kept between 0.05 and 20). the game's note movers (`NocturneXModMover`, `NocturneMModMover`) place each note at a constant times its distance in rows from the current visual row, and nothing reads scroll segments, so for a battle whose chart has `#SCROLLS` (a custom difficulty's or a custom battle's) the mod rescales `GetNotePositionX` and `GetHoldPositionX` with a "displayed row" (the ratio integrated over rows): the distance becomes displayed(target) minus displayed(now). that changes how fast the notes move without moving them in time, and judgement, which is time based, is untouched. `GetTopRows` and `GetBottomRows` get the same mapping so notes still spawn at the edge of the lane in slow sections. the hooks only act on the battle conductor's song position, so the difficulty previews aren't affected, and they're off for every chart without scroll changes. bookmarks are kept in `#NBBBOOKMARKS` (seconds, comma separated).

the chart is held on stepmania's grid of 48 rows a beat (192 a measure) and written back with each measure at the fewest lines (4, 8, 12, 16, 24, 32, 48, 64, 96 or 192) that keep every note on its row; reading that back gives the same notes. times come from the chart's `#BPMS` and `#STOPS`. for a game song, `#OFFSET` is treated as 0, because the game ignores it in those battles: `WwiseConductor` resets the parsed offset and starts its clock at the music's entry cue. saving a game song's chart writes one `#NOTES` block (`dance-single` or `pump-single`, difficulty `Challenge`, the name as description, a meter from notes per second) under the source chart's own tags with `#NBBSONG`, `#NBBMELODY`, `#TITLE` and `#CREDIT` set, into `CustomCharts/<song>/<name>.sm`. the editor marks its files with `#NBBEDITOR`, and only such a file with one difficulty is saved over; an imported or hand-made chart is left alone and the edit goes into a new file. copied notes only paste into a chart with the same number of lanes, and moved, pasted, mirrored or reversed notes replace whatever they land on in their lane. the saved chart becomes the song's custom difficulty.

battle music in this game is wwise interactive music, which the mod can't seek or slow down (`SeekOnEvent` succeeds but doesn't move playlist music), so the editor plays the song itself. `MusicMapData` is a table, generated from the 1.0.1 banks (bank version 145), of the pieces that make up each song and melody: the source (`Media\<id>.wem`, or a range inside a `.bnk`), where it starts on the chart's clock, the trim, the length, and the clip's fade and volume automation. it comes from walking the always-playing music root with the song's switch and state values: its `playEvent` only sets switches and states, the melody is a switch value (`MX_Global_Combat_Melodies`), playlists play segments in order with each entry cue on the previous exit cue, and chart time 0 is the entry cue of the segment holding the first `START` cue. rendered from the table and matched against the charts' note times, 169 of 227 segments line up within 5 ms (median +3 ms), and the rest within about 60 ms, the same timing the game plays them with. `Roach - 1` has no music event and plays no music in the editor. a custom battle's own song file is decoded with the mod's decoders instead, at the file's own rate (see custom battles).

all of the game's battle music is plain 16-bit pcm (48 or 44.1 khz, stereo or mono), so the editor reads the listed ranges straight from the game's files, resamples to 48 khz stereo, applies the fades (a volume curve of 0 to -1 is taken as a gain of 1 to 0), mixes pieces that overlap where one segment's tail runs into the next, and keeps a peak per 10 ms for the waveform. that takes about half a second for a song, on a background thread. it plays through the windows `waveOut` api from a thread of its own (unity's audio is switched off in this game): four 1024-frame buffers, linear resampling for 25% to 100% speed, and short ticks mixed in: 2 khz at note times and 1 khz on beats for the metronome. the editor's clock is `waveOutGetPosition`, so it follows what's being heard.

a custom battle opens in the same editor on its own chart file and song (from the battle creator's charts and song pages, or its dialogue page's test buttons). six difficulty tabs each edit one `#NOTES` block of the battle's one `.sm`; the timing, events, scroll speeds and bookmarks in its header are shared by all of them, and an empty tab can start empty or copy another tab's notes. ctrl+pageup and ctrl+pagedown switch tabs. for a battle, `#OFFSET` counts, and the timing tab can put the first beat at the playhead, move every beat against the music by 1 ms or 10 ms (`[` and `]`, with shift for 10 ms), tap the tempo with `t`, and loop two bars. a battle's chart always carries its own `#ATTACKS`, and the events tab adds `PlayerAttack 1` (0.5 s). in five lanes the middle lane is labelled attack, with the player's own attack key as the game names it. ctrl+s writes every tab into the file (write to a temporary file, then replace), after the battle loader's checks and the game's own chart reader, and needs notes in at least one tab. the battle's dialogue lines during the song show as a violet lane at the left and as markers on the timeline; the events tab switches between events and dialogue, those edits go into the battle creator's draft with the editor's undo, and ctrl+s saves `battle.json` too when they changed. exporting is the battle creator's job.

test (the top bar's button, f5 from the playhead, shift+f5 from the start) plays the chart being edited in a real battle and comes back to the editor. it works for custom battles and for game songs, and only from the title screen. the battle starts the way the arcade menu starts one (`SceneTransitionController.GoToCombat`), without the menu showing, as an arcade battle that can't be lost (`CombatFailType.NoFail`, consumables off, the enemy can't be defeated). `ChartSwap` hands it the editor's unsaved chart; a battle's test also takes the creator's unsaved `battle.json` and dialogue. a test from the playhead starts the battle's clock two bars (8 beats) earlier, kept between 1.5 and 4 s before the playhead and rounded down to a whole millisecond: the `WwiseConductor.Initialize` prefix passes a negative start delay. notes before the playhead (with 10 ms of slack) are left out, and events before the clock start are dropped, except lane layouts, camera moves and props, which move to the clock start. a game song tested from the playhead plays the editor's own mix; from the start it plays the game's wwise music. nothing reaches a save: the results and score step (`CombatManagerV3.ProcessResults`) is skipped, `SaveFileManager.SaveScores` and every write to the story's `.sav` files are refused, achievements are held back, and the latest save is read again afterwards. the pause menu's exit to arcade reads "back to the editor" during a test. the editor watches the game's state (it turns `Combat` once the battle is on, and back when it's left) and shows itself again on the same tab, playhead, selection and undo history. with "dialogue in tests" on (playerprefs `NocturneFlatScroll.EditorTestDialogue`), a test from the start plays the lines before the song, every line during it and the win lines; a test from the playhead plays no lines before the song, only the lines during the song from its clock start on, then the win lines. a test can't be lost, so loss lines never play in one.

## custom battles

a custom battle is an arcade-only battle with its own song, charts, enemy, and optionally set gear, a set level and dialogue. the battle creator makes and edits them, and they can be written by hand.

### folder layout

battles live in `Application.persistentDataPath/NocturneButBetter/CustomBattles`, made when it's needed. a battle is a folder with a `battle.json`, or a `.nbbbattle` zip of one. the scan looks in that folder and one level of subfolders below it, so battles can be grouped: `.nbbbattle` files first, then folders, each sorted by name. a subfolder counts as a battle when it holds `battle.json`. in a zip, `battle.json` is at the root or in its one top folder. of two battles with the same `id`, the first by path loads and the other is skipped (logged).

the battle creator lays a battle out like this:

```
CustomBattles/
  My Battle/
    battle.json
    charts/song.sm            every difficulty, in one .sm
    audio/<song file>
    images/<card picture>     the arcade card's picture
    art/<files>               custom enemy art, and <anim>-frames.png from turn into frames
    portraits/<pictures>      pictures of the battle's own dialogue speakers
  Shared Battle.nbbbattle     a zip of a folder like the one above
  .creator-work/              the creator's staging folder, never loaded
```

a hand-made battle can also keep its enemy or its dialogue in a json file of its own, like `enemy/enemy.json`, named from `battle.json`. the creator names a new battle's folder after its song file, and an imported one's after its title: characters windows refuses become `_`, and the name is at most 60 characters, with " (2)" and so on when it's taken. videos inside a zip, or at paths the video player can't open, are copied to `Application.persistentDataPath/NocturneButBetter/Cache/EnemyArt`, which is trimmed to 1 gb the first time it's used.

every path in `battle.json` is a file inside the battle, written with `/` or `\`. a path that starts with `/`, has a `:` (a drive letter), a `..` part, or a control character in it doesn't count. in a zip, names are matched without regard to case.

### battle.json

`battle.json` is utf-8, with or without a byte order mark, at most 1 mb. keys are read in any letter case. `//` comments and trailing commas are allowed, and numbers may be written as text; `"NaN"` and `"Infinity"` are left out with a problem. unknown keys are ignored by the loader and kept by the battle creator. a problem is logged as `Custom battle <title>: <problem>.` and shown in the creator, and the battle still loads. a refusal is logged as `Skipping custom battle <path>: <reason>`, and the battle isn't in the arcade. a value of the wrong json type in a text or number key (text where a number goes, or the other way round) makes `battle.json` unreadable, which is a refusal.

```json
{
  "format": 2,
  "kind": "battle",
  "id": "5f0c1c8e-2b7a-4d4e-9a0f-3d2f8c6b1a77",
  "title": "My Battle",
  "artist": "Someone",
  "author": "Me",
  "lore": "",
  "card": "images/card.png",
  "cardFit": "fill",
  "audio": "audio/song.ogg",
  "previewStart": 0,
  "lanes": 4,
  "chart": "charts/song.sm",
  "enemy": { "mode": "placeholder", "placeholder": "EnemyData_Mantis" },
  "gear": { "mode": "player" },
  "level": { "mode": "set", "value": 12 }
}
```

| key | type and default | limits, and what a bad value does |
|---|---|---|
| `format` | number, required | must be `2`. a higher number is refused as made for a newer version; anything else is refused. |
| `kind` | text, required | must be `"battle"`, in any case; anything else is refused. |
| `id` | text, required | a guid, like `"5f0c1c8e-2b7a-4d4e-9a0f-3d2f8c6b1a77"`. scores are kept by it, so it never changes. not a guid: refused. |
| `title` | text; default the folder's or zip's name | line breaks become spaces. the arcade card shows about 16 letters; the creator takes up to 80. |
| `artist` | text; default empty | line breaks become spaces. |
| `author` | text; default empty | who made the charts (the creator's "charter"). line breaks become spaces. |
| `lore` | text; default empty | several lines are fine. shown in the arcade's box on the right under what the battle sets; empty shows "music by ..." and "chart by ..." instead. the creator takes up to 600 characters. |
| `card` | path; default none | the arcade card's picture: a png or jpeg (by its first bytes), at most 16 mb, not decoded over 8192 px a side. outside the battle or missing: a plain card, with a problem. any other kind of file, or one that won't decode: a plain card, logged. |
| `cardFit` | `"fill"` or `"fit"`; default `"fit"` | fill: the largest square of the picture fills the card's square slot, and the rest is cut off. fit: the whole picture, with see-through bars where it isn't square. anything else: fit, with a problem. the creator writes `"fill"` with a battle's first picture. |
| `cardFocus` | `[x, y]`, each 0 to 1; default `[0.5, 0.5]` | where the filled square sits: 0 is the left or top edge, 1 the right or bottom. only the long side's number moves it. out of range: kept in range; not two numbers: the middle; both with a problem. |
| `cardSmooth` | `true` or `false`; default worked out | true draws the card smooth, false crisp. missing: crisp when the side across the slot (the short side for fill, the long side for fit) is at most 128 px, else smooth. anything else: worked out, with a problem. |
| `audio` | path; default the chart's `#MUSIC` | the song, at most 512 mb. when neither names a file inside the battle, or the file is missing: refused. |
| `previewStart` | seconds; default 0 | a negative value counts as 0. the creator's "play 10 s" button starts here; nothing in the arcade reads it yet. |
| `lanes` | 4 or 5; default the lane count of the chart's first block with notes, else 4 | anything else is refused. |
| `chart` | path; default `"charts/song.sm"` | the `.sm` with every difficulty, at most 8 mb. outside the battle or missing: refused. the creator and the chart editor only edit it when it's an `.sm` file inside the battle that no other key (the song, card, enemy or dialogue file, or an art file) also names. |
| `enemy` | object, or a path to a json file holding one; default: `EnemyData_Mantis` with its own stats | see below. a file or object that can't be read: mantis with its own stats, with a problem. |
| `gear` | object; default the player's own gear | see below. can't be read: the player's own gear, with a problem. |
| `level` | number or object; default the player's own level | see below. |
| `dialogue` | object, or a path to a json file holding one; default none | see dialogue. it never refuses the battle; at worst the battle plays without it. |
| `source` | object | written by the osu!mania import: `kind` (`"osu!mania"`), `file`, `beatmapSetId` (only when above 0), `mapper`, and `version`. the loader doesn't read it. |

`enemy`:

| key | type and default | limits, and what a bad value does |
|---|---|---|
| `mode` | `"placeholder"` or `"custom"`; default placeholder | placeholder: the game enemy's look. custom: it fights like the placeholder and looks like its own `art`. |
| `placeholder` | a game `EnemyData` asset name, the `EnemyData_` start optional; default `EnemyData_Mantis` | the enemy the battle copies: its attacks, sounds and abilities. one the game doesn't have: mantis stands in (logged). `EnemyData_GenericEnemy` and `EnemyData_Roach` have no art: mantis, with a problem. the scripted bosses `EnemyData_Yako`, `EnemyData_Nocturne`, `EnemyData_CagedWei`, `EnemyData_Sue`, `EnemyData_WingedWei`, `EnemyData_Kitsune` and `EnemyData_Ladybug` need `advanced`; without it, mantis, with a problem. |
| `advanced` | `true` or `false`; default false | lets those seven bosses stand in. they may not play well. never with custom art: mantis fights instead, with a problem. |
| `name` | text | the title of the battle's own info boxes that have none. the game shows the enemy's name nowhere else. the creator takes up to 60 characters. |
| `stats` | object; each missing value keeps the placeholder's own | `hp` 1 to 100000, `damage` 0 to 1000, `passiveEnergyCharge` 0 to 10000, `energyChargeOnMiss` 0 to 1000, `attackWindupTime` 0.05 to 60 s (the creator's hp, damage, passive energy, energy per miss, and attack windup). out of range: kept in range. not a finite number: left out, with a problem. |
| `info` | list of `{"title": ..., "description": ...}`; default the placeholder's own boxes | the boxes at the battle's top right. the first 3 show (more: a problem). a box without a title takes `name`. the game hides a box without a description, and shows 3 lines of one (about 33 characters a line). |
| `art` | object | the custom art (see the enemy and its art). it's read on its own, so a mistake in it can't cost the enemy its stats. |
| `rig` | text | not used: custom art always shows on the game's mantis rig. noted as a problem in custom mode. |

every custom battle's enemy is a copy of its placeholder under an id of its own (`NocturneButBetter/enemy/<id>`): not a boss, no xp, no special attack, one energy bar segment, an empty loot table, and its own sound hooks (`combatInitialized`, `combatSongStart`) emptied so they can't start music over the song. in five lanes, its lane-changing effects and column animation triggers are left out (logged), because they're made for four lanes.

`gear`:

| key | type and default | limits, and what a bad value does |
|---|---|---|
| `mode` | `"player"` or `"set"`; default player | player: the player's own gear. set: exactly the items below, for this battle only. anything else: the player's own, with a problem. the creator writes just `{"mode": "player"}` for the player's own. |
| `mainHand`, `body`, `head`, `offHand`, `amulet` | an item id, like `"DBA1"` (an `ItemData_` asset name works too); missing or null: an empty slot | the creator's weapon, armor, head, off hand and amulet. an id the game doesn't have, or an item for another slot: that slot stays empty (logged). |
| `consumable` | `{"item": "<id>", "count": N}` | count 1 to 99, default 1 (out of range: kept in range, with a problem). the game allows one consumable use per battle, so the creator doesn't show or write the count. |
| `extraHealth` | whole number 0 to 99; missing or null: the player's own | health upgrades (the game's `Item_ExtraHealth`), only with `"mode": "set"`. out of range: kept in range, with a problem. |

`level`:

| written as | what it means |
|---|---|
| missing, `null`, or `{"mode": "player"}` | the player's own level. |
| `{"mode": "set", "value": 12}` | level 12 for this battle. outside 1 to 20: kept in range, with a problem. |
| `12` | short for the line above. `12.0` counts; `12.5` is the player's own, with a problem. |
| `{"value": 12}` with no mode, or `"set"` with no value | the player's own level, with a problem. a level the battle's maker didn't clearly choose is never used. |
| anything else | the player's own level, with a problem. |

### the chart

a battle's chart is one stepmania `.sm` file with a `#NOTES` block for each charted difficulty, `dance-single` for 4 lanes or `pump-single` for 5. the block's difficulty name picks its slot, the same way the game's own charts do: `Edit` is beginner, `Beginner` is novice, `Easy` is adept, `Medium` is expert, `Hard` is elite, and `Challenge` is zen.

- blocks without notes are skipped. a block with a difficulty name the game doesn't have, the wrong lane count, or a second block for a slot is left out (the first one plays), with a problem. so is a block whose note rows aren't as wide as the lane count, or a chart without a positive first `#BPMS` value.
- with no playable block, the battle is refused. the game's own chart reader must also read the result, or the battle is skipped.
- the game gets exactly six blocks. a slot without a chart of its own plays the nearest charted one, the easier one when two are as near, so all six arcade difficulties work.
- the battle runs until a beat after the last note of its longest difficulty, whichever difficulty plays: a postfix on `WwiseConductor.InitializeNoteField` raises the note field's `maxTapRow`.
- `#OFFSET` counts in a battle, as in stepmania: beat 0 is at song time minus `#OFFSET`. the game itself IGNORES `#OFFSET` in battles (`WwiseConductor.InitializeSong` sets the song position's offset to 0, so beat 0 is at clock 0), so the mod bakes it into the chart the game reads (`ChartOffset`). notes and timing tags move so that the chart's first row at or after 0:00 becomes row 0, a stop there covers the time from 0:00 to that row (no tempo change, so speed mod's top tempo never rises), and the chart says `#OFFSET:0`. the clock stays the song file's time, so `#ATTACKS`, dialogue times and a test's start stay in song seconds. more than 600 s either way is refused. a positive offset with notes before 0:00 is a problem: notes more than 0.5 ms before 0:00 can't be played, so they're left out, and the loader, the battle creator and the chart editor's save say how many. 2.6.0 didn't bake it, so every battle with an `#OFFSET` played its notes that far off the music.
- `#BPMS` and `#STOPS` are the timing. `#SCROLLS` holds scroll speed changes (`beat=ratio`, x0.05 to x20), which play as they do for custom difficulties, and `#NBBBOOKMARKS` the editor's bookmarks. `#MUSIC` names the song when `battle.json` has no `audio`, and `#TITLE`, `#ARTIST` and `#CREDIT` are kept. `#NBBSONG`, `#NBBMELODY` and `#NBBEVENTS` are for custom difficulties and don't apply here: a battle has one melody, and its events are always its own.
- `#ATTACKS` holds the battle's events, shared by every difficulty, in the game's format (`TIME=s:LEN=s:MODS=verb args`). `PlayerAttack <charges> [damage]` makes the player attack, as in the game's own charts; the editor's "player attack" button adds `PlayerAttack 1`, 0.5 s long. in five lanes the middle lane is played with the attack key and the player's own attacks are off, so `PlayerAttack` events are the only way to hurt the enemy. `EnemyAnimation` and `TriggerCombatEffect` use the placeholder enemy's own animations and effects. `SpawnPrefab` and `ShowText` show nothing, since a custom battle has no song props or text of its own.

### scores

a custom battle's runtime `SongData` is named `NocturneButBetter/battle/<id>`, with the id as a lowercase guid, and the same text is its `highScoreKey` (with `overrideHighScoreKey` set). so its scores follow the id, whatever its folder, title or files are called, and the arcade's high scores list it like any song. a battle has one melody: `ArcadeUtility.GetUnlockedMelodyCount` answers 1, and a postfix on `ScoreManager.GetUnlockedMelodiesForSong` keeps these keys at 1, so the results screen never announces a second melody.

a folder copied in windows explorer keeps its id, so the arcade loads only the first of the two. choosing the one the arcade skips (the later one by name) in the battle creator offers to make it a separate battle, which gives it a new id and a fresh set of scores; the scores stay with the one the arcade shows. importing a `.nbbbattle` whose id is already there gives it a new id too.

in the main-menu arcade the game writes scores to the latest save's slot `.score` file (`ProdSlotN.score`). custom difficulties keep theirs under `NocturneButBetter/<chart key>` (see custom difficulties). chart editor tests record nothing.

custom battles never count towards achievements, since anyone can write one: a prefix on `AchievementManager.CombatEnded` skips the end-of-battle check for them, and one on `AchievementManager.UnlockAchievement` holds unlocks back while one runs. the same goes for every chart editor test.

### the main-menu arcade

the game's main menu already has an arcade button (`MainMenu.arcadeButton`) that its `BuildFlagObject` switches off in standard builds. the mod sets `ExistsInStandardBuilds` and shows it, with its own help text. the game makes it clickable only when there's a save to continue, like continue, and that rule is kept, since arcade progress goes into that save. a prefix on `MainMenu.Arcade` never runs the game's handler, which would move the story save to `Slums_Arcade` and reload it. it starts a session on the latest save read from disk (`LoadLatestGameData`) and calls `MainMenu.SongTesting()`, which pushes the arcade menu over the title the way the game's debug song testing button does.

the session reads that save's slot `.score` file itself first. when the autosave is newer than every slot save, the game loads it with whatever scores are already in memory, and after a fresh start those are a new game's empty scores. a save without a slot counts as slot 1. scores go to that `.score` file as the story's arcade cabinet writes them, so they sync through steam cloud.

while a session runs, nothing writes the story's `.sav` files. `SaveFileManager.SaveGameData` saves only the scores, `AutoSave` (both overloads) and `ClearAutoSave` do nothing, and `SaveLoadManager.Save`, `SaveWithMethod` and `DeleteSave` refuse `ProdAutoSave.sav` and `ProdSlotN.sav`. each refusal is logged. the size, time and sha-256 of those files are noted when the session starts and compared when it ends, and a change logs `Arcade: WARNING: story save files changed`. the pause menu's retry and game over's continue both load the story, so they're blocked, and game over's continue button is switched off. the session ends when the arcade menu closes, and before anything loads or starts the story. it reads the latest save from disk again, which drops play time, stagger time and items used from memory, and it puts a set-gear battle's gear back if that was ever missed.

a prefix on `GenericArcadeMenuV2.Activate` reads the custom battles folder again every time an arcade or high scores screen opens. a battle whose files haven't changed isn't read again: its fingerprint is its location plus the size and time of every file it uses. when at least one battle loads, one more `ArcadeCategory` goes after all the game's chapters, named "custom battles" with the short name "custom" on its tab, and its cards are sorted by title. it's marked debug-only like the game's own test chapter, so the discovery percentage and the trophy ranks skip it, and a prefix on `ShouldShowCategory` shows it anyway. its songs are always unlocked, with one melody. the story's arcade cabinet uses the same screens, so it lists custom battles too. each battle is a runtime `SongData` (`ScriptableObject.CreateInstance`, with empty wwise objects and cues, one beatmap holding the six-block chart, and no items), the enemy copy described above, and an `ArcadeSongInfo` whose lore is the battle's. in five lanes, when the battle asks for no lane layout of its own, a postfix on `CombatNoteFieldView.ShowColumns` gives the lanes the game's centred five-lane layout (`Five Instant`), and a prefix on `RemoveColumn` skips a lane index the note field doesn't have.

for a custom battle, the arcade shows a box the screen has under the score but normally hides: what the battle sets (its level and yours, its gear with "only" when it leaves slots empty, its health upgrades, and that yours come back afterwards), then as much of the lore as fits. the text shrinks until what the battle sets fits whole, and the lore is cut word by word or left out. a battle that sets gear or level also gets an amber tag on its card, like "set gear, level 12".

### music without wwise

a custom battle's song, a custom chart's `#MUSIC` file, and a chart editor test's music play through the mod's own player, the chart editor's `waveOut` player, instead of wwise. the song decodes on a worker thread when the battle's chart is built. a prefix on `WwiseConductor.PlayWWiseTrack` starts the player where the conductor would start its music, posts wwise's `MX_Silence_O05_I05` so the music from before the battle fades, and gives the conductor a playing id of its own. every frame, a prefix on `WwiseConductor.UpdateBeatmapPosition`, which the game calls right after its own clock step, sets the conductor's clock from the player with the same checks and drift the game uses for wwise, so the notes follow the file. the game's rule puts its clock one frame ahead of the song, so after a long frame (a hitch) the notes jumped up to twice that frame's length and then back. for these battles the drift is cut after a long frame, so the clock aims at most 1/30 s (or two usual frames) ahead of the song. with steady frames nothing changes. there's no patch on `AudioController.TryGetSongPosition`: melonloader's il2cppinterop can't call a patched method with an `out double` from native code, so any patch there breaks every battle. the player's time is reported 46.8 ms ahead (wwise's reported position runs 45.5 ms ahead of what's heard, and the player's 1.3 ms behind), so the game's latency calibration, made against wwise, stays right. that was measured by loopback recording with an earlier way of starting the song, and hasn't been measured again since. each song also logs three lines about its start: the frame it started on and its lead-in, then, 5 s in (or when it stops sooner), the longest frames, the drift and how the game took it up, the fastest the notes moved against real time, and the sound device's time against real time. that costs a timer read a frame for those 5 s and nothing after.

the player follows the game's pause and its master, music and combat music volumes. it fades over 1 s when the game's victory, pre-end or leave-combat music would start, and stops when the battle is left or the song unloads. a custom battle's chart waits at most 20 s at its start for a song that's still decoding, and a custom chart 8 s. a custom battle's song that fails leaves the battle silent, with its chart still playing on the game's own clock; a custom chart's falls back to the game's music.

the song starts with the battle's clock, and the game's own charts start about 2 s in, so a song file's first note could be halfway down the lane when the notes first showed. now, when the first note would come into view less than 0.75 s after the clock starts (worked out from the note speed settings and the chart's own tempo, stops and `#SCROLLS`), a prefix on `WwiseConductor.Initialize` passes a positive `startDelay`, up to 3 s, so the clock starts before the song. until the song starts, the clock moves by each whole frame, and the battle's first frames can be slow, so the 0.75 s leaves room for a long first frame (2.6.1 used 0.25 s, and a frame longer than that showed the first note partway down the lane). the notes scroll in during the game's own pre-roll, and the music from before the battle fades as they start. events at or before 0:00 that set how the battle looks (lane layouts, camera, props) move to the clock start; attacks and text keep their times. the player still starts at clock -0.1 s. a test from partway in doesn't get this.

the format comes from the file's first bytes, not its name. wav and ogg vorbis are decoded by the mod's own code. flac, mp3, m4a (aac), wma and a few more go through windows media foundation's source reader, read from memory, and the start and end of mp3 and aac are corrected so the song lines up the way ffmpeg's gapless decode does. windows n editions need the media feature pack for those; without it, the message says to convert the song to `.ogg` or `.wav`.

### the enemy and its art

with `"mode": "custom"`, the enemy fights like its placeholder (its attacks, sounds and stats) and looks like its own art, shown on the game's mantis rig (`Mantis_Art`). without a usable idle it looks like the placeholder.

`enemy.art` has four animations and some keys for the whole enemy:

| key | type and default | what it does |
|---|---|---|
| `idle` | an animation, or just a file name; required | loops. without a usable idle the enemy looks like its placeholder. |
| `attack` | an animation, or a file name | plays once for each attack, 0.2 to 6 s long. missing: the idle stands in, 0.5 s long with the hit at 0.3 s. |
| `hurt` | an animation, or a file name | plays once when the enemy is hit. missing: the idle. |
| `defeat` | an animation, or a file name | holds its last frame unless it loops. missing: the hurt, else the idle holds. |
| `size` | 8 to 480; default worked out from the idle | the idle frame's height in game pixels. |
| `offset` | `[x, y]`, each within ±200 game pixels | moves the whole enemy. |
| `flip` | `true` or `false`; default false | mirrors the whole enemy. |
| `smooth` | `true` or `false`; default worked out | missing: crisp when every frame is drawn at a whole number of game pixels per pixel. |
| `shadow` | `true` or `false`; default true | the enemy's shadow. |

`windup`, `stunned`, `staggered`, `victory` and `intro` are reserved: kept in the file, not used yet. an animation's keys, used only where they apply:

| key | applies to | values and default |
|---|---|---|
| `file` | all | a path inside the battle; required. |
| `kind` | all | `"image"` (or `"picture"`, `"still"`), `"sheet"` (or `"spritesheet"`, `"sprite sheet"`), `"gif"` (or `"animated"`), `"video"`. the file's first bytes decide what it is; `kind` only chooses between a still picture and a sprite sheet for a png or jpeg. a picture is cut into frames only with `"sheet"`. |
| `columns`, `rows` | sheet | 1 to 64 each; frames are read left to right, then down, like the game's own sheets, and must be at least 4 px. |
| `first`, `frames` | sheet | the first cell (from 0; default 0) and how many (default: up to the last cell with anything in it). |
| `fps` | sheet, gif | 1 to 60. a sheet's default is 10; a gif's is its own timing. |
| `times` | sheet, gif | ms per frame, 10 to 10000 each, one per frame, at most 1000; overrides `fps`. |
| `speed` | gif, video | 0.1 to 10 (1 is 100%). |
| `seconds` | still picture (not the idle) | how long it shows, 0.05 to 10; default 1. |
| `loop` | defeat | `true` loops it. the idle always loops; the attack and hurt play once. |
| `hitFrame` | attack (sheet, gif) | the frame (from 0) the hit lands on. |
| `hitTime` | attack (picture, video) | seconds into the attack, in the file's own time, 0 to 600. |
| `scale` | not the idle | 0.05 to 20; size compared to the idle's pixels. |
| `offset` | all | `[x, y]`, each within ±200 game pixels. |
| `flip` | all | mirrors this animation. |
| `feet` | all | `[x, y]` in the frame's pixels from its top left; default the bottom centre of the picture's visible part (alpha 16 and up). a video with no `feet` stands on its frame's bottom edge. |
| `keyColor` | picture, sheet, gif | a see-through colour: `"#RRGGBB"`, or `"corner"` for the top-left pixel's colour. not on videos. |
| `keyRange` | with `keyColor` | 0 to 1; default 0.15. |

a number out of range is kept in range and a wrong value is left out, each noted as a problem; a broken animation is dropped and its stand-in plays. without `hitFrame` or `hitTime`, the hit lands at 0.57 of the attack's length on a frame's start, at least 0.25 s in for attacks of 0.5 s or longer and at most 5.5 s in. the parry window opens 0.25 s before the hit.

formats and limits:

- pictures and sprite sheets: png or jpeg, at most 32 mb and 4096 px a side.
- gifs: at most 32 mb and 2048 px a side; the first 1000 frames and 128 million decoded pixels are read.
- videos: at most 256 mb and 1920 x 1080. mp4 (h.264, 8-bit 4:2:0, or mpeg-4 part 2) plays through windows media foundation, and webm through unity's own decoder, which only takes vp8. h.265, av1, vp9, and 10-bit, 4:2:2 or 4:4:4 h.264 are refused, and so is a video stored sideways or upside down with a note to turn it (as upright phone videos are). an idle video can be at most 60 s long at its speed, and a hurt or defeat video 10 s; a longer one is refused and its stand-in plays. an attack video is read for its first 6 s. an attack or hurt video must say how long it plays. videos have no see-through pixels, except a webm with alpha, so an mp4 shows as a rectangle.
- webp, bmp, tiff and heic are refused.
- an animation keeps at most 240 frames; more are thinned evenly and it keeps its length.

loading starts when the battle's card stays highlighted in the arcade for 0.2 s (`GenericArcadeMenuV2.ArcadeSongGroup_OnSelected`, or a click on it), and for a chart editor test. files are read and decoded on worker threads and turned into textures on the main thread, a step a frame. the fight's start waits at most 5 s for the rest of the art, and doesn't wait for an idle video, which has 8 s to get ready. a load nobody fights is let go after 60 s. art that can't load shows the combat text "custom art couldn't load"; art still loading shows "custom art wasn't ready yet", and the next fight tries again. the hooks are `CombatEnemyView.Initialize` (a prefix and a postfix that switch the enemy to the rig), postfixes on `PlayAttack` and `PlayHit`, and one on `IsPlayingAttackAnimation` that adds a custom attack while it shows. the hit calls the game's own `CombatManagerV3.CombatAnimationHit` and `CombatSimulator.OpenRiposteWindow`.

turn into frames, on the battle creator's art page, makes a video animation a sprite sheet, so a see-through colour works on it. it plays the video in the game's video player and reads 12, 15 or 24 frames a second of the battle's time, at most 240 frames (thinned evenly, with `times` written instead of `fps`), in a sheet at most 4096 px a side, as `art/<anim>-frames.png` (a free name, never over another file). frames are at most 720 px tall and 8 source pixels per game pixel at the size the battle draws them. the animation's settings carry over, a written `hitTime` becomes the nearest `hitFrame`, and other animations' `scale` changes to keep their size. esc, or leaving the page, stops it with nothing changed. until the next save, "back to the video" puts the animation back exactly as it was. when at least 85% of the first frame's edge is close to its top-left pixel, the video is taken as standing on a flat background (a green screen, say), and the range for its corner colour is remembered for one click on the see-through colour.

### gear, level, and consumables

a set-gear battle swaps the game's inventory manager in a prefix on `CombatManagerV3.StartCombat`: the battle runs on one that reads a throwaway save holding the battle's gear. everything that isn't gear (key items, followers, the pet, money) is copied from the player's. the player's own save objects are never written to, and the game's saves always write its own save object, so a save during the battle still holds the player's gear. a postfix on `ExitCombat` puts the player's manager back on every way out (a win, a loss, quitting from the pause menu) and works out their stats again from their own gear. backstops do the same before a save, before anything loads, on the title's continue, load, new game and gauntlet, and when the arcade closes.

a set-level battle answers `LevelProgression.GetLevel` with its level (a postfix), so the game's own stat updates use it. the game keeps no level in the save, only xp, and turns the level into strength, regen and critical; gear that sets a stat outright, like the pool noodle, still wins. the level is kept within the game's own table (20 levels in 1.0.1). nothing of the player's is written, arcade battles never award xp, and the player's stats are worked out again from their own xp when the battle ends and in the backstops.

infinite consumables (arcade) never applies to a set-gear battle: its own consumables are used up, and the player's are never touched there.

### dialogue

`battle.json`'s `dialogue` is an object, or the path of a json file inside the battle holding one, with the same reading rules as `battle.json`. it has the battle's own `speakers` and four lists of lines:

| section | when it plays | at most |
|---|---|---|
| `before` | after the battle fades in, before the ready prompt, one line at a time | 60 lines |
| `during` | while the song plays, in time order (lines at the same time keep their written order) | 200 lines |
| `afterWin` | after the last note of a won battle, before the victory and the results | 30 lines |
| `afterLoss` | when the player is beaten, after the notes freeze and the song stops, before the battle ends | 30 lines |

```json
"dialogue": {
  "speakers": {
    "warden": {
      "name": "The Warden",
      "portrait": "portraits/warden.png",
      "expressions": { "angry": "portraits/warden-angry.png" },
      "side": "right"
    }
  },
  "before": [
    { "speaker": "Karma", "expression": "Determined1", "text": "Out of my way." },
    { "speaker": "warden", "text": "You dare come into my hive?" },
    { "speaker": "Narrator", "text": "The hive falls silent.", "duration": 3 }
  ],
  "during": [
    { "time": 42.5, "speaker": "warden", "expression": "angry", "text": "Enough!" },
    { "beat": 192, "speaker": "Karma", "text": "Not yet...", "duration": 4 },
    { "beat": 256, "speaker": "warden", "text": "Then face my true form!", "pause": true }
  ],
  "afterWin": [ { "speaker": "warden", "expression": "angry", "text": "Impossible..." } ],
  "afterLoss": [ { "speaker": "warden", "text": "Back to the dirt with you." } ]
}
```

a line's keys:

| key | values and default |
|---|---|
| `speaker` | a key of `speakers`; `"Narrator"` (the narration box, no picture); or a game character's id in any letter case, like `"Karma"` (the player's character), `"Kimothy"` or `"NPC_Abbot"`. a `speakers` key wins over a game id spelled the same. missing: the narrator. an id the game doesn't have: the line shows as the narrator (logged). |
| `text` | 1 to 150 characters. line breaks become spaces, and `<`, `>`, `{` and `}` are taken out, since the game's box reads them as tags and values. longer text is cut to 150; empty text leaves the line out. |
| `expression` | a game character: one of its emotions, like `"Determined1"`. one of the battle's speakers: a key of its `expressions`. default: the default face (a game character's `Neutral1`, else its first). an unknown one shows the default face. |
| `name` | the name tag for this line only, up to 24 characters. |
| `side` | `"left"` or `"right"`; default the speaker's own. karma and young karma stand left, other game characters right, and the battle's speakers where their `side` says. |
| `duration` | 1 to 15 s (kept in range). lines before, after, and those that stop the song move on by themselves after it, and a key can't skip them; without it they wait for a key. a line during the song that doesn't stop it shows this long; without it, 1.5 s plus 0.05 s a letter, kept between 2.5 and 8 s. |
| `time` | `during` only: seconds on the song's clock, the chart editor's clock (beat 0 is at minus `#OFFSET`). it stays put when the tempo changes. |
| `beat` | `during` only, instead of `time`: it moves with the notes when the timing changes. with both, the beat is used. |
| `pause` | `during` only: `true` stops the song and the notes for this line. lines that stop the song at the same time are one stop. |

a line during the song needs a `time` or a `beat`, at or after 0 and no later than the battle's end (a beat after the last note), or it's left out; a line after that belongs in `afterWin`. two things are only noted: a line during the song that starts before the one before it ends cuts that one short, and a stop with a note in the next 2 beats sends those notes at once when the song goes on. `time`, `beat` and `pause` on lines outside `during` are ignored, with a problem.

a speaker's keys (at most 12 speakers; a key is 1 to 24 letters, digits, spaces, `-` or `_`, unique ignoring case, and not `Narrator`):

| key | values and default |
|---|---|
| `name` | the name tag, up to 24 characters; missing: the key shows. |
| `portrait` | the default face: a png or jpeg inside the battle (the creator copies it into `portraits/`), at most 4 mb and 2048 px a side. missing or unusable: the speaker shows without a picture. |
| `expressions` | more faces, name to file, up to 24, each with a name of 1 to 24 characters. used only when `portrait` is. |
| `side` | `"left"` or `"right"`; default right. |
| `flip` | `true` mirrors the picture (the game already mirrors every portrait on the right). |
| `offset` | `[x, y]` in game pixels, each within ±120; nudges the picture. |

all the speakers' pictures together can be at most 64 mb. a picture up to 256 x 240 shows pixel for pixel, a tiny one (under 96 tall) at the largest whole number up to 4x that keeps it at most 160 tall and 256 wide, and a bigger one is scaled down to fit 256 x 240. every face shows at the default face's size, so one of another shape shows stretched (noted). the name tag is the line's `name`, else the speaker's `name`, else the game's own name for a game character.

nothing in the dialogue refuses the battle. every mistake is noted in plain words and the line or speaker is fixed up or left out, and a speaker that's left out has its lines said by the narrator.

in the battle, a director for each battle with dialogue adds its lines at hold points the game already has. the lines before the song hold back the ready prompt (a prefix on `ReadyCountdownView.ShowGetReadyAndPressKeyToStartText`) and run once the battle has faded in, as one of the game's own data cutscenes of dialogue actions, with the game's keys: confirm, enter or a click finishes the typing, then moves on, and holding confirm or esc skips. a line during the song that doesn't stop it is the game's box in its timed mode: it ignores keys, closes by itself, and freezes with the pause menu. its bubble is raised to 180 game pixels (a postfix on `DialogueStyleNormal.GetDialogueBubbleHeight`) in the game's own view and 2d downscroll and stays low in 2d upscroll, and its background images are drawn at 72% of the alpha the game gives them while it shows, so the notes behind stay readable; the text, name tag and faces stay solid. a stop pauses the combat the way the pause menu does, runs its lines as a block, and lets the song go on 1 s after the last one. the lines after a win or a loss hold the end routine's own wait before the victory or defeat (a postfix on `CombatManagerV3._EndCombatRoutine_b__99_0`). the pause menu can't open during a stop or the lines after the battle (`NocturneGui.CanPauseGameState` and `TogglePause`). every cutscene is repeatable, so the game never marks it as seen, holds only dialogue actions, and has no letterbox. the battle's own speakers are runtime `CharacterData` entries whose faces come from a prefix on `PortraitManager.GetExpressions`; they and their pictures are freed at `ExitCombat`. a failure anywhere drops that battle's dialogue and lets the battle go on.

### the battle creator

the battle creator opens from the custom charts page's "battle creator" row. it edits `battle.json` as a json tree, so keys it doesn't know are kept, and saves it by writing a temporary file and replacing the old one. an enemy or dialogue file that `battle.json` names is edited in place the same way. new and imported battles are put together in `.creator-work` and moved into place when complete; that folder is one level deeper than the scan looks, so a half-made battle is never loaded, and it's cleaned when the creator opens. saving also refreshes the arcade's copy of the battle. the new battle's charter is the chart editor's remembered author (playerprefs `NocturneFlatScroll.EditorAuthor.v1`).

files go to the recycle bin, never away for good: a deleted battle's folder, files in `audio/`, `images/`, `art/` and `portraits/` that the saved battle no longer names, files added since the last save when the creator is left without saving, and a `.nbbbattle` in the battles folder once it's unpacked for editing. this goes through the shell's `IFileOperation`, with a progress sink that stops any delete windows wouldn't send to the recycle bin (a drive without one, or one that's off or too small), so then the file stays and an error shows.

export writes a `.nbbbattle` zip with every file of the battle inside one top folder named like the battle's folder, skipping `.tmp`, `desktop.ini` and `Thumbs.db`. files that are already compressed (ogg, mp3, m4a, flac, wma, png, jpeg, gif, webp, mp4, webm and the like) are stored as they are. it can't be saved inside the battle, or in the battles folder, where the arcade would load the battle twice. import unpacks at most 4000 files and 1 gb in all, with the loader's limits for each file (json 1 mb, charts 8 mb, pictures 16 mb, or 32 mb in `art/` and 4 mb in `portraits/`, anything else 512 mb), and nothing lands outside the new folder.

### the osu!mania import (beta, not recommended)

a new battle can start from an osu!mania beatmap set (`.osz`). the `.osz` is only read, on a worker thread, for at most 30 s. limits: 4000 files and a 16 mb file list in the zip, 100 `.osu` files, 64 mb of `.osu` text, and per difficulty 50,000 notes and 2000 red lines. songs can be up to 60 minutes, and a song over 16 mb that would unpack to more than 20 times its packed size is refused. the charts together must fit the 8 mb chart limit. a refused file gets one plain sentence.

- difficulties: only osu!mania 4k and 5k go in. other key counts and other modes (osu!standard, osu!taiko, osu!catch) are listed as left out with the reason. a battle has one lane count, so with both, the player chooses. each difficulty gets a slot from how many notes a second it has, compared with the game's own charts for each slot, in order and at most six; the player can move one to another slot (a taken slot swaps) or leave it out.
- song and card: the beatmap's song is copied into `audio/` byte for byte, and its background, when it's a png or jpeg, into `images/` as the card, filling the square. osu! and the mod start an mp3 without gapless info about 25 ms apart, so the notes move by that much to match.
- timing: the tempo comes from one difficulty (the one with the most notes, and the summary says which). red lines become `#BPMS`, and beat 0 goes on a beat of the first red line as `#OFFSET`. each red line starts on a whole beat: when it isn't on one, part of the beat before it is stretched (a filler) and gets a speed change that keeps it looking even. a red line within 2 ms of a whole beat snaps to it. tempos outside 40 to 1000 bpm, which osu! maps use as speed effects, keep the tempo around them and become speed changes.
- notes: each note keeps its osu! time, and its row is the nearest 1/48 beat. notes more than 2 ms off are counted as moved and listed. two notes on one row of a lane become one, a hold ends at least a row before the next note in its lane (or becomes a tap), and notes before 0:00 or after the song's end are left out. osu! waits about 2 s before its songs and a battle starts with the song, so the summary flags notes in the first 1.5 s, and the player can leave them out.
- speed changes: green lines (slider velocity) become `#SCROLLS`, relative to osu!'s main tempo so the main part plays at x1, kept to x0.05 to x20 and at most 5000. a battle has one set for all its difficulties, from one difficulty (the player picks whose, or none), and the others' are listed as unused.
- five lanes: osu!'s middle column is the attack lane, and the import adds a `PlayerAttack 1` event (0.5 s) on every 8th bar (beats 32, 64, and so on) from 16 beats after the first note up to the last note, or one at the last note when none fits. the player can turn them off.
- also brought over: the title, the artist, the mapper as the charter, osu!'s bookmarks (`#NBBBOOKMARKS`) and its preview time (`previewStart`), and `source` in `battle.json`. left out: the background video, hit sounds, the storyboard, and osu!'s od and hp drain (the game's own timing windows and health apply).

after the battle is made, the game's own chart reader checks it, and from then on it's an ordinary battle that stays editable in the chart editor.

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

if the folder is protected, open powershell as administrator and rerun the same command. existing compatible loader files and settings are preserved; conflicting loader binaries stop the install. uninstall disables only recognized copies of this mod and keeps saved preferences. each disabled or replaced dll is kept beside the original with a `.disabled-` or `.backup-` suffix. neither the installer nor uninstall touches `Application.persistentDataPath`, so custom charts and custom battles stay where they are.

## rebuilding

launch the supported game once with the loader you want to build for, so it generates the game's interop assemblies. install a .net sdk that can target .net 6, then run one of:

```powershell
# bepinex plugin (default)
dotnet build .\source\nocturneflatscroll.csproj -c Release -p:bepdir="d:\steamlibrary\steamapps\common\nocturne\bepinex"

# melonloader mod
dotnet build .\source\nocturneflatscroll.csproj -c Release -p:loader=melonloader -p:melondir="d:\steamlibrary\steamapps\common\nocturne\melonloader"
```

the output goes to `source\bin\<loader>\Release\net6.0`. keep `Release` capitalized: the dll records the configuration name, so `-c release` builds a working dll with a different hash. built this way, inside or outside a git checkout, both dlls match the packaged ones byte for byte. the source folder has two subfolders, `Art` and `Audio`, which the project compiles with the rest. the project references local loader and game-generated interop assemblies; neither the game nor those generated assemblies are in the source folder. melonloader prefixes the game's namespaces with `il2cpp`, so `source\globalusings.cs` maps the names for each loader. the installer accepts only the packaged dll hashes, so a custom build needs a matching installer hash update or a manual copy.

## scope of validation

version 2.6.0 was tested in-game on nocturne 1.0.1 with bepinex 6.0.0-be.788 and melonloader 0.7.3, the melonloader one in a separate copy of the game folder. every run took a snapshot of the player's plugin, settings and saves first, put that snapshot back afterwards, and checked that the `.sav` files hadn't changed. on melonloader, no run logged a "native->managed trampoline" error.

- custom battles in the main-menu arcade, in 4 and 5 lanes, with songs played to the end: scores went into `ProdSlotN.score` and the `.sav` files didn't change. both loaders.
- set gear and infinite consumables: 228 of 228 checks on both loaders, over a win, a retry, quitting from the pause menu, a real loss, extra health, a save check in the middle of a battle, and infinite consumables on and off.
- per-battle level and the arcade's box: 233 of 233 checks on both loaders.
- the battle creator: 155 of 155 checks on both loaders. every page opened, and they covered the pickers, play 10 s, saving, export, the charts, and the art page. that includes the osu!mania import: 20 broken beatmaps were refused in plain words, and imported battles played in the arcade, where a 5-lane one was beaten with the player attacks the import adds.
- custom enemy art: 10 test battles on both loaders, with a gif, a sprite sheet, a png, an mp4, a webm, a big video, broken files, a battle in a zip, and the art turned off. videos showed on the first fight.
- turn into frames: 29 of 29 checks on both loaders, including a green-screen mp4 cut out with the corner colour, esc stopping it, and "back to the video" bringing the video back.
- arcade card pictures: 81 of 81 checks on 16 cards on both loaders: square, tall and wide pictures, tiny pixel art, a 4000 x 3000 picture, crops, and bad card keys in `battle.json`.
- the chart editor's test, from the playhead and from the start, on custom battles and game songs: the results and score were skipped, nothing was saved, and the editor came back. bepinex ran all six test runs. melonloader ran two, both from the playhead: a custom battle, and a game song playing the editor's own mix.
- dialogue in the arcade, on both loaders: lines before the song held the ready prompt, lines during the song showed within 6 ms of their time in a see-through box, lines that stop the song paused it and let it go on, win lines came before the results, and a real loss played its loss lines with the music stopped. on bepinex it was also checked in downscroll and upscroll, with a dialogue file, in 5 lanes, in the chart editor's test from the start and from partway, and in a battle with 10 broken dialogue entries that still loaded and played. the story's viewed-scene flags were untouched.
- the creator's dialogue page: 93 of 93 checks on both loaders, over every tab, adding, copying, replying and undoing lines, the speaker picker (karma first, typing to jump), faces and sides, a new speaker with a picture, save and reopen, and the chart editor's dialogue lane. the preview's portrait rectangles matched the game's own placement within a game pixel.

not tried for 2.6.0: custom battles played with a real keyboard (the tests use auto-play or the game's own input check), a controller, custom battles by hand in the story's arcade cabinet, very long songs in the arcade, and real osu!mania beatmaps beyond the generated test files and five real sets checked outside the game.

version 2.5.0 was tested in-game on nocturne 1.0.1 with bepinex 6.0.0-be.788 and melonloader 0.7.3, the melonloader one in a separate copy of the game folder. on both loaders a qa build imported a two-chart pack, exported the library and re-imported the export (skipped as already imported), then opened options > gameplay and clicked the import row. it found the dialog among the game process's own top-level windows, confirmed it sat above the game window in z-order, typed a pack path into the file name box (`WM_SETTEXT`), and pressed open (`WM_COMMAND IDOK`); it did the same with a new file name in the export save dialog and cancelled a third dialog (`IDCANCEL`). the row text after each step and the exported file were checked. it then wrote the game charts (177 files, 103 songs) and imported one as six difficulties. a second qa run selected a custom chart through playerprefs, continued a save into the firefly battle with auto-play, and logged the melodies `Initialize` received (0, 0), the title of the chart `CreateBeatmap` returned, and the song id `TryRecordScore` got (`NocturneButBetter/pack:...#0`). the player's saves were put back and checked by hash after each run.

the earlier features were tested on 2.4.x. a qa-only build turned on the game's auto-play for every lane, started the firefly battle, and stepped through twelve combinations of scroll mode, skin, receptor height (-10 to +30), note size (70 to 150) and lane spacing (70 to 150), taking a screenshot and logging the lane positions, receptor scale, label offset, note scale, hold width, and the bar's position of each. one step fired the game's own `Five` and `Default` lane animations to check that spacing follows animated lanes. before the first change and after returning to default, it recorded 70 values the mod touches (lane positions, receptor parts, lane strips, beams, labels, note parts); all 70 matched on both loaders. the timing bar got synthetic hits, since auto-played hits are excluded.

a second qa run on bepinex turned auto-play off and answered the game's `CombatPlayerInput.GetTapDown` for each lane as notes crossed a random point near the receptors. the game judged 319 of those presses as player taps (offsets -12 to +75 ms); the timing bar and the hit sound both reacted to them. the hit sound was also measured on both loaders with a wasapi loopback recording lined up by high-resolution timestamps: at 100%, 50%, 20% and 5% the recorded peaks were exactly 1, 0.5, 0.2 and 0.05 of full, about 56 ms after each post, and gains of 150 to 300% came out only about 10% louder than 100%. on bepinex, the same build opened options > gameplay and logged the seven rows, their navigation links, changed values, and the values after reset to default, then opened the latency calibration preview with the arrow skin at 140% size and 130% spacing (capped to 110% there). on both loaders it opened options > audio and logged the hit sound switch and volume slider below ui, their navigation links, the slider stopping at 5%, and the switch turning the sound off. the qa build is the release source plus test hooks; the release dlls themselves were checked by the installer tests and a real install, not by another game launch.

a later qa build added note flares, enemy attack opacity, and the miss sound, and repeated the twelve-step battle on both loaders with them in the schedule. per step it counted the frames where a lane's newest flare was shown or hidden: with flares off, none were shown, and with them on, none were hidden. the firefly attacked twice per run. each time its sprite and shadow reached the set opacity 80 ms after the attack began and returned when it ended, about 2.7 s later. in the second attack the qa build had given the firefly its `DissolveSpriteV2` material, and the sprite switched to the mod's copy of the occluder material while faded and back afterwards. the vine images under the mod's canvas group reported the group's alpha as their inherited alpha. a test sprite added under the attack-object parent between scans faded on the next frame, and a simulated hit flash during the alpha-blind fade (the qa build read `renderer.material`, as the game's hit does) kept the fade going and still put the original material back when the attack ended. the 70-value restore check still matched. miss sounds were recorded by loopback: menu previews at 300, 200, 50, and 10% peaked at exactly 3, 2, 0.5, and 0.1 times the 100% level. in the battle, with lane 4 left to miss, the median miss at 300% and 200% was 3.3 and 2.1 times the game's own 100% miss, the mod posted every miss at those levels and none at 100%, and with miss sounds off nothing was heard above the background. on both loaders, options > audio showed the four sound rows with working navigation, the miss slider clamped 0 to 10 and labelled 250 and 300 correctly, and its switch changed the game's setting and the gameplay page's row. options > gameplay showed nine rows, and reset put the new ones back to on and 100%.

version 2.2.0 checked the options rows, their limits, hold-to-repeat, reset to default, and persistence between the two loaders. its qa-only build measured the receptor line through the combat camera at the firefly battle ready screen for heights -10, 0, 5, 10, and 30. as a fraction of the screen height above the bottom edge, it sat at:

- downscroll: 0.2006 + 0.01 × height
- upscroll: 0.6662 - 0.01 × height

default mode restored the original perspective track, and the latency calibration preview moved by 20% at +20.

for 2.4.0, a qa build left the battle's ready screen waiting and a script sent real key events to the game window: tab, the windows key (with a masking key so the start menu stayed shut), tab and windows again with no gap between key down and key up, right alt, left alt, alt+j, then j. on both loaders a first postfix logged that the game's any-key check fired for each of them, a last postfix logged the result after the mod's filter, and only j started the countdown. another qa build opened options > gameplay, selected note colors, and stepped through all six palettes in the default, circle, and arrow skins, 18 steps per loader. each step checked that the preview sat right after the row and that its first-lane and middle notes had the colors `GetColorsForColumn` returns, and took a screenshot.

lane presses from a real keyboard or controller were not tried (the simulated presses go through the same judgement code), and the skins and sizes have not been tried on a five-lane chart. only the firefly battle was played with those settings, so the vines, other enemies' attack objects, mines, and critical misses above 100% have not been seen or heard in a fight. the fullscreen fix resolved flicker on the original test pc; results on other display and gpu combinations can differ.

package checks cover windows powershell parsing and isolated installation, upgrade, loader switching, reinstallation, removal, incompatible-file rejection, and reversible display patching. see package-verification.txt for the completed checks.

## integrity

bepinex plugin sha-256:
`8153e76b0600fc4bbceecc8418b6d3ddb1aadbee03ef663278289bf8dc146dbd`

melonloader mod sha-256:
`d7a85b5541ba00292c761ca7fd287c96cd299b894284959dfc1ed5388b4b0fa0`

both dlls are compiled from the same 2.6.0 source with debug symbols disabled, so they contain no development-machine pdb path. the installer also recognizes both 2.5.0 dlls, both 2.4.1 dlls, both 2.4.0 dlls, both 2.3.0 dlls, an earlier 2.3.0 test build, both 2.2.0 dlls, and earlier 2.1.x bepinex builds when upgrading or uninstalling.

official loader archive sha-256:
`f4cc496bd098a0df4164b81e3737297707f13a47c2478dba2f60eefab784817a`

`sha256sums.txt` lists every package file except itself. it checks accidental corruption, not authorship: an unsigned package and its checksum list could both be changed. review the scripts and source if you need to verify their behavior.
