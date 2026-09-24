# nocturne but better 2.4.1

formerly nocturne flat scroll. the files, the plugin id, and the saved settings keep that name, so upgrades from earlier versions keep working.

the package targets windows x64, steam app 1374860, nocturne 1.0.1, build 25487568, unity 2022.3.62f2. 2.4.1 moves the installer and the fullscreen script to that game update; the mod code is the same as 2.4.0. the installer pins game code, metadata, and original layout assets. it refuses unknown versions rather than assuming an update is compatible. previous experimental static layout patches must be restored before using this portable package.

## loaders

the mod ships as two dlls built from the same source:

- `payload\nocturneflatscroll.dll` is the bepinex plugin. it installs as `bepinex\plugins\nocturneflatscroll\nocturneflatscroll.dll` and uses the official bepinex 6.0.0-be.788+5b766a3 windows x64 il2cpp loader, which the package bundles unchanged. the bundled loader includes its .net runtime.
- `payload\nocturneflatscroll.melonloader.dll` is the melonloader mod. it installs as `mods\nocturneflatscroll.melonloader.dll` and needs melonloader 0.7.3 or newer, which you install yourself. melonloader uses the system .net 6 runtime and downloads it if it's missing.

the installer's `-loader` option chooses between them. `auto`, the default, uses melonloader when it will start: `melonloader\net6\melonloader.dll` exists, a melonloader proxy dll such as `version.dll` is in the game folder, and `userdata\loader.cfg` doesn't turn it off. otherwise it uses bepinex. it refuses to add bepinex to a game with an enabled melonloader, because both loaders hook the same unity startup call and only one of them starts. it also refuses melonloader older than 0.7.3. installing for one loader disables this mod's copy for the other, so only one copy runs.

game-specific interop files are generated on the recipient's pc during the first launch with either loader and are not included. a .net sdk is only needed to rebuild the source.

the optional fullscreen script applies a small, reversible byte patch to the recipient's `nocturne_data\globalgamemanagers` to prefer directx 11. it validates both the input and resulting sha-256 hashes. the 1.0.1 update left the file the same size with the same graphics api bytes at the same offsets (only its build guid text changed), so the patch is the same three bytes plus four padding bytes, with new pinned hashes. no original or modified game asset is distributed with this package.

## settings

options > gameplay gets nine rows above speed mod: note scrolling, receptor height, note size, lane spacing, note skin, note flares, timing bar, timing bar position, and enemy attack opacity. options > audio gets hit sound, hit sound volume, miss sound, and miss sound volume under sound effects, after ui. each switch is a copy of the gameplay page's own on/off switch (the one "note miss sounds" uses) and each slider a copy of the audio page's ui volume slider, added when the page opens and linked into its up and down navigation. the page's slider label shows the slider's fraction of its range, which is only the volume when the range ends at 100, so the copies get their own label text. the audio page has no reset button, so neither do these rows.

note scrolling cycles between default, 2d downscroll, and 2d upscroll. default keeps the original track and hud. the two flat modes keep the game's artwork and vertical bars, with player meters lower left and enemy meters upper right, close to the chart.

receptor height runs from -10% to +30% in 1% steps. it moves the flat field along its own vertical axis, which faces the combat camera, so the receptors change height without changing depth or lane width. one step is 1.9527 field units, which is 1% of the screen height at the combat camera's distance of 169.11 units with its 60° field of view. positive values move the receptors in from their edge of the screen: up in downscroll, down in upscroll. in upscroll the receptors start lower, at 33% of the screen height below the top, so values above about +17% take them past the middle. the calibration and difficulty previews use a 360-unit-high canvas, so there a step is 3.6 units. left and right stop at the ends; clicking the row cycles through every value.

lane spacing (60% to 150%) multiplies each lane's x position around the field's centre line. the game positions lanes with an animator on `FieldPivot` (`ColumnsAnimationController`), which moves them during the battle intro, the four-to-five lane change, chart events, and pull attacks, so the mod applies the factor every frame and treats any x it did not write itself as the game's new position. leaving 2d, or hiding a battle, puts the game's positions back. the lane background, its dots, and the press beams are drawn at min(size, spacing) of their width, so they never grow past the notes or into each other.

note size (50% to 150%) scales the parts the game never scales itself: the note heads (including mines and fakes), the hold end cap and start cap, the hold lines' width, the hold offsets and the note mask, the receptor border, decal and glow, and the mod's skinned receptors. every size is written from the value captured before the mod changed anything, so 100% and default mode give back the exact original values. the game's key-press pulse on the receptor's centre dot keeps working because only its parent is scaled. the lanes' press beams keep the game's own height animation and only start at the larger receptor's edge. hit flares are resized as the game plays them, and the key and auto labels move away from larger or skinned receptors. the hud meters leave room for receptors above 100%. menu previews get both settings around their own lane centre, with spacing capped at 110%. default mode and the audio calibration screen are left alone.

note skin swaps the game's shapes-drawn notes and receptors for sprites the mod draws when the game starts: signed-distance masks rendered to mipmapped textures, so no image files ship with it. the skinned note keeps the game's sorting order and takes its colors from the same `CombatNoteColorSet` the game applies to its own notes. the receptors copy the game's fill, border, and center colors each frame, and a skin-shaped glow replaces the rectangular hit glow. hold trails are drawn at 70% width. arrows point left, down, up, and right; an odd lane count gets a diamond in the middle lane. the flat upscroll field is mirrored, so the mod swaps the up and down arrows there to keep them pointing the same way on screen. picking default turns the game's renderers back on and restores the hold widths. the menu previews get skinned receptors too, and so does the audio calibration screen, which keeps its own layout. only the latency calibration preview was checked in-game.

note flares uses the same `NocturneCombatNoteColumnBehaviour.NoteFlareSingle` and `NoteFlareHold` postfixes that size the flares. when it's off, the lane's newest flare (`NoteFlarePool.LastNoteFlare`) gets a scale of zero. that hides it while its animator keeps running, so the pool and the pairing of hold starts and ends work as usual. mine flares (`isMine`) and calibration flares (`isCalibration`) stay visible; the mine flare's animation is also what plays the mine sound. switching the setting also updates hold flares already on screen. the game has its own hidden `ShowNoteFlares` setting, but it also drops the mine explosion and its sound, so the mod doesn't use it.

enemy attack opacity runs every frame after the game's animators. once a second, and straight away when a new enemy, piece of enemy art, sidekick, or attack object appears, it collects the enemy's sprite renderers under `CombatEnemyView.enemyParent` (the art and its shadow, not the hit effects beside it), the sidekicks' renderers, the attack objects under `CharacterFieldView/CameraPlanePrefabParent` (skipping the player's helpers), and `FieldPivot/Canvas/Vines`. the enemy counts as attacking while the game's `isAttacking` flag is set or its animator's current state, or the next one during a transition, has the `Attack` tag. the enemy, its shadow, and its sidekicks ease to the setting over 0.12 s; attack objects and the vines, which get a canvas group of the mod's own, take it straight away. only alpha changes, and a colour the game sets itself (stuns, hits) becomes the new base. sprites whose shader is `Shader Graphs/DissolveSpriteV2`, which ignores vertex alpha, borrow a copy of the enemy's `enemySpriteMaterialWithOcclusion` while faded and get their own material back afterwards. a hit during the fade makes the game copy whatever material the sprite has for its flash (`renderer.material`); a copy of the borrowed material still counts as borrowed, so the sprite's own material comes back when the attack ends. once the enemy is defeated (`murdered`), its sprites get their alpha back and keep the material they have, because the game's death effect may already be running on it. at 100% nothing is written, and anything still faded is put back.

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

the settings live in unity playerprefs under `NocturneFlatScroll.Mode.v3`, `NocturneFlatScroll.ReceptorHeight.v1`, `NocturneFlatScroll.NoteSize.v1`, `NocturneFlatScroll.LaneSpacing.v1`, `NocturneFlatScroll.NoteSkin.v1`, `NocturneFlatScroll.NoteFlares.v1`, `NocturneFlatScroll.TimingBar.v1`, `NocturneFlatScroll.TimingBarPosition.v1`, `NocturneFlatScroll.EnemyAttackOpacity.v1`, `NocturneFlatScroll.HitSound.v1`, `NocturneFlatScroll.HitSoundVolume.v1`, and `NocturneFlatScroll.MissSoundVolume.v1`. an older `NocturneFlatScroll.Direction.v2` setting is migrated when present. reset to default on the gameplay page sets default, 0%, 100%, 100%, default, on, off, below enemy, and 100%. installing on another pc does not transfer settings or saves.

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

version 2.4.1 was tested in-game on bepinex 6.0.0-be.788 and melonloader 0.7.3, the melonloader one in a separate copy of the game folder. a qa-only build turned on the game's auto-play for every lane, started the firefly battle, and stepped through twelve combinations of scroll mode, skin, receptor height (-10 to +30), note size (70 to 150) and lane spacing (70 to 150), taking a screenshot and logging the lane positions, receptor scale, label offset, note scale, hold width, and the bar's position of each. one step fired the game's own `Five` and `Default` lane animations to check that spacing follows animated lanes. before the first change and after returning to default, it recorded 70 values the mod touches (lane positions, receptor parts, lane strips, beams, labels, note parts); all 70 matched on both loaders. the timing bar got synthetic hits, since auto-played hits are excluded.

a second qa run on bepinex turned auto-play off and answered the game's `CombatPlayerInput.GetTapDown` for each lane as notes crossed a random point near the receptors. the game judged 319 of those presses as player taps (offsets -12 to +75 ms); the timing bar and the hit sound both reacted to them. the hit sound was also measured on both loaders with a wasapi loopback recording lined up by high-resolution timestamps: at 100%, 50%, 20% and 5% the recorded peaks were exactly 1, 0.5, 0.2 and 0.05 of full, about 56 ms after each post, and gains of 150 to 300% came out only about 10% louder than 100%. on bepinex, the same build opened options > gameplay and logged the seven rows, their navigation links, changed values, and the values after reset to default, then opened the latency calibration preview with the arrow skin at 140% size and 130% spacing (capped to 110% there). on both loaders it opened options > audio and logged the hit sound switch and volume slider below ui, their navigation links, the slider stopping at 5%, and the switch turning the sound off. the qa build is the release source plus test hooks; the release dlls themselves were checked by the installer tests and a real install, not by another game launch.

a later qa build added note flares, enemy attack opacity, and the miss sound, and repeated the twelve-step battle on both loaders with them in the schedule. per step it counted the frames where a lane's newest flare was shown or hidden: with flares off, none were shown, and with them on, none were hidden. the firefly attacked twice per run. each time its sprite and shadow reached the set opacity 80 ms after the attack began and returned when it ended, about 2.7 s later. in the second attack the qa build had given the firefly its `DissolveSpriteV2` material, and the sprite switched to the mod's copy of the occluder material while faded and back afterwards. the vine images under the mod's canvas group reported the group's alpha as their inherited alpha. a test sprite added under the attack-object parent between scans faded on the next frame, and a simulated hit flash during the alpha-blind fade (the qa build read `renderer.material`, as the game's hit does) kept the fade going and still put the original material back when the attack ended. the 70-value restore check still matched. miss sounds were recorded by loopback: menu previews at 300, 200, 50, and 10% peaked at exactly 3, 2, 0.5, and 0.1 times the 100% level. in the battle, with lane 4 left to miss, the median miss at 300% and 200% was 3.3 and 2.1 times the game's own 100% miss, the mod posted every miss at those levels and none at 100%, and with miss sounds off nothing was heard above the background. on both loaders, options > audio showed the four sound rows with working navigation, the miss slider clamped 0 to 10 and labelled 250 and 300 correctly, and its switch changed the game's setting and the gameplay page's row. options > gameplay showed nine rows, and reset put the new ones back to on and 100%.

version 2.2.0 checked the options rows, their limits, hold-to-repeat, reset to default, and persistence between the two loaders. its qa-only build measured the receptor line through the combat camera at the firefly battle ready screen for heights -10, 0, 5, 10, and 30. as a fraction of the screen height above the bottom edge, it sat at:

- downscroll: 0.2006 + 0.01 × height
- upscroll: 0.6662 - 0.01 × height

default mode restored the original perspective track, and the latency calibration preview moved by 20% at +20.

for 2.4.0, a qa build left the battle's ready screen waiting and a script sent real key events to the game window: tab, the windows key (with a masking key so the start menu stayed shut), tab and windows again with no gap between key down and key up, right alt, left alt, alt+j, then j. on both loaders a first postfix logged that the game's any-key check fired for each of them, a last postfix logged the result after the mod's filter, and only j started the countdown. another qa build opened options > gameplay, selected note colors, and stepped through all six palettes in the default, circle, and arrow skins, 18 steps per loader. each step checked that the preview sat right after the row and that its first-lane and middle notes had the colors `GetColorsForColumn` returns, and took a screenshot.

lane presses from a real keyboard or controller were not tried (the simulated presses go through the same judgement code), and the skins and sizes have not been tried on a five-lane chart. only the firefly battle was played, so the vines, other enemies' attack objects, mines, and critical misses above 100% have not been seen or heard in a fight. the fullscreen fix resolved flicker on the original test pc; results on other display and gpu combinations can differ.

package checks cover windows powershell parsing and isolated installation, upgrade, loader switching, reinstallation, removal, incompatible-file rejection, and reversible display patching. see package-verification.txt for the completed checks.

## integrity

bepinex plugin sha-256:
`eeca5992cb2a195bcbe9b51bb9160165e166a6b153501e74dab5bd16f6a18b50`

melonloader mod sha-256:
`4a9ca300577ad598e5b2498acde6ed7d65cabe2dcce169832bae9a13a44e4bc0`

both dlls are compiled from the same 2.4.1 source with debug symbols disabled, so they contain no development-machine pdb path. the installer also recognizes both 2.4.0 dlls, both 2.3.0 dlls, an earlier 2.3.0 test build, both 2.2.0 dlls, and earlier 2.1.x bepinex builds when upgrading or uninstalling.

official loader archive sha-256:
`f4cc496bd098a0df4164b81e3737297707f13a47c2478dba2f60eefab784817a`

`sha256sums.txt` lists every package file except itself. it checks accidental corruption, not authorship: an unsigned package and its checksum list could both be changed. review the scripts and source if you need to verify their behavior.
