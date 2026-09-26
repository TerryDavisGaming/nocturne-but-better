using HarmonyLib;
using UnityEngine;

namespace NocturneFlatScroll;

/// <summary>
/// Plays a battle's music from a file with the mod's own player instead of Wwise: a custom
/// song's audio, or the song file a custom chart names with #MUSIC (StepMania's tag), or in a
/// test play the chart editor's own decoded song, which can start partway through. The
/// conductor starts its music in PlayWWiseTrack. For these battles the mod starts its player
/// there instead, fades out the music from before the battle with Wwise's silence event, and
/// gives the conductor a playing id of its own. Every frame, just before the conductor moves the
/// notes (UpdateBeatmapPosition), the mod sets its clock from the player the way the game sets it
/// from Wwise's position, so the notes follow the file. No Wwise cue is involved: the battle ends a beat after the
/// chart's last note, and the song fades when the game's victory, pre-end or leave-combat music
/// would start. The conductor's clock is the song file's own time, as it is the Wwise track's
/// time for the game's songs, so the file is never shifted here. The game ignores the chart's
/// #OFFSET, so for these battles it is baked into the chart the game reads (ChartOffset: beat 0 at
/// clock time -OFFSET, as in StepMania). A battle whose first note comes early starts its clock
/// before 0 (ChartSwap's lead-in): the music from before the battle fades as its notes start, and
/// the song still starts at clock time -0.1 s.
/// </summary>
internal static class CustomMusic
{
    /// <summary>A song file for one battle.</summary>
    internal sealed class Source
    {
        internal string Name = "";
        /// <summary>The file and its size and time, so a retry reuses the decoded song.</summary>
        internal string Key = "";
        /// <summary>Reads the file; runs on a worker thread.</summary>
        internal Func<byte[]> Read = () => Array.Empty<byte>();
        /// <summary>
        /// Song already decoded (the chart editor's, for a test play) with the clock time of its
        /// first sample; used instead of <see cref="Read"/>. Such a song has no key, so it isn't kept.
        /// </summary>
        internal Func<(short[] Stereo, int Rate, double Origin)>? Decoded;
    }

    private sealed class Song
    {
        internal short[] Stereo = Array.Empty<short>();
        internal int Rate;
        internal double Origin;   // clock time of the first sample
    }

    // A decoded song with its player, whose sound device is already open.
    private sealed class Loaded
    {
        internal Song Song = null!;
        internal EditorAudio Player = null!;
    }

    // A song decoding for a battle, until the conductor starts its music.
    private sealed class Pending
    {
        internal Source Source = null!;
        internal Task<Loaded> Loading = null!;
        internal IntPtr Conductor;
        internal bool CustomBattle;          // no Wwise music to fall back on
        internal float WaitingSince = -1f; // when the conductor first wanted to start
        internal bool StartCue;            // whether the conductor was waiting for Wwise's start cue
        internal bool HeldCombat;          // the wait set waitForStartCue, so combat waits too
        internal bool Waited;
        internal bool Silenced;            // the music from before the battle was faded as the notes started
        internal int LeadInFrames;         // frames the notes moved before the song started (a lead-in)
        internal double LongestLeadInFrame;
    }

    // The conductor ignores positions for a playing id of 0, and real Wwise ids count up from 1.
    private const uint FakePlayingId = 0xC0570000;
    // The conductor stops following positions past the playing segment's length; a song has none.
    private const double NoSegmentEnd = 1e6;
    // How long the chart waits at its start for a song that's still decoding: a custom battle has
    // no other music, a custom chart falls back to the game's.
    private const float MaxSongLoadWait = 20f;
    private const float MaxChartLoadWait = 8f;
    private const string SilenceEvent = "MX_Silence_O05_I05";
    private const float EndFade = 1f;
    private const float UnloadFade = 0.4f;
    // About six minutes of 44.1 kHz stereo; longer songs are decoded again for a retry.
    private const int MaxCachedSamples = 32 * 1024 * 1024;
    // After a long frame, the chart's clock aims at most this far ahead of the song, or two usual
    // frames, whichever is more (see BeatmapPrefix).
    private const double MinFrameLead = 1.0 / 30;
    // When the song's start report is logged, in seconds after it started.
    private const double ReportAfter = 5;

    private static Pending? pending;
    private static WwiseConductor? owner;      // the conductor of the battle the song belongs to
    private static IntPtr customConductor;     // a custom battle's conductor, which has no Wwise cues to take
    private static EditorAudio? player;
    private static WwiseConductor? conductor;  // set while the song plays
    private static double origin;
    private static string playingName = "";
    private static bool paused, silencePosted;
    private static bool held;   // the battle's dialogue keeps the song stopped (the lines after a loss)
    private static float fadeStart = -1f, fadeLength;
    private static (string Key, Song Song)? decoded;   // the last song decoded, for a quick retry
    private static bool reportedError;
    private static bool loggedFollow;   // "the chart follows the song file", once per song
    private static double usualFrame = 1.0 / 60;   // the frame time, smoothed, for MinFrameLead
    private static StartReport? report;            // the song's first seconds, until they're logged

    internal static bool Active => player != null;

    /// <summary>
    /// Keeps the song paused while on, whatever the battle does: the lines after a loss play over a
    /// stopped song, and a song that has ended no longer follows the conductor's pause. Letting go
    /// of the song (Stop) lets go of this too.
    /// </summary>
    internal static void Hold(bool on) => held = on;

    /// <summary>
    /// Whether this conductor's battle is paused (the pause menu, or a dialogue break that stops the
    /// song), from its combat manager, which clears it whenever a battle starts or ends.
    /// AudioController.IsPausedCombat isn't used: the game sets it when pausing and clears it only when
    /// the pause menu resumes, so a quit from the pause menu leaves it on into later battles, where the
    /// song would never play and the chart would keep being pulled back to it.
    /// </summary>
    internal static bool BattlePaused(WwiseConductor? c)
    {
        var m = CombatManager.Instance?.TryCast<CombatManagerV3>();
        if (m == null || !m) return false;
        if (c != null)
        {
            var own = m.conductor;
            if (own == null || !own || own.Pointer != c.Pointer) return false;
        }
        return m.paused;
    }

    /// <summary>Whether <see cref="Hold"/> is on (for QA).</summary>
    internal static bool Held => held;

    internal static void Install(HarmonyLib.Harmony harmony)
    {
        // Everything is looked up first, so a missing method installs nothing.
        var beatmap = Method(typeof(WwiseConductor), "UpdateBeatmapPosition");
        var play = Method(typeof(WwiseConductor), "PlayWWiseTrack");
        var cue = Method(typeof(WwiseConductor), "OnAudioCue");
        var beat = Method(typeof(WwiseConductor), "OnSyncBeat");
        var unload = Method(typeof(WwiseConductor), "UnloadSong");
        var exit = Method(typeof(CombatManagerV3), "ExitCombat");
        var victory = Method(typeof(AudioController), "PlayCombatVictory");
        var preEnd = Method(typeof(AudioController), "PlayCombatPreEnd");
        var leave = Method(typeof(AudioController), "LeaveCombat");
        harmony.Patch(beatmap, prefix: Hook(nameof(BeatmapPrefix)));
        harmony.Patch(play, prefix: Hook(nameof(PlayTrackPrefix)));
        harmony.Patch(cue, prefix: Hook(nameof(CuePrefix)));
        harmony.Patch(beat, prefix: Hook(nameof(BeatPrefix)));
        harmony.Patch(unload, prefix: Hook(nameof(UnloadPrefix)));
        harmony.Patch(exit, postfix: Hook(nameof(ExitPostfix)));
        harmony.Patch(victory, postfix: Hook(nameof(VictoryPostfix)));
        harmony.Patch(preEnd, postfix: Hook(nameof(FadePostfix)));
        harmony.Patch(leave, postfix: Hook(nameof(FadePostfix)));
    }

    private static System.Reflection.MethodInfo Method(Type type, string name) =>
        AccessTools.DeclaredMethod(type, name) ?? throw new MissingMethodException(type.FullName, name);

    private static HarmonyMethod Hook(string name) => new(typeof(CustomMusic), name);

    /// <summary>
    /// How far Wwise's reported position runs ahead of what's heard, less the same for the mod's
    /// player: measured from a loopback recording as 45.5 ms for Wwise and -1.3 ms for the player
    /// (steady to a few ms). Reporting the player this much ahead keeps the game's latency
    /// calibration, which was made against Wwise, right for custom battles. It was measured with the
    /// song started on Wwise's start cue; the PlayWWiseTrack start needs a new loopback measurement.
    /// </summary>
    private const double ClockLead = 0.0468;

    /// <summary>The song's clock time as the conductor's segment time (it adds the finished segments).</summary>
    private static double SegmentTime(EditorAudio p) => p.Time + ClockLead + origin - conductor!.previousSongSegmentTime;

    // ---- preparing -----------------------------------------------------------------------------

    /// <summary>
    /// The file a custom chart names with #MUSIC, next to the chart or in its pack, or null.
    /// Charts copied from the game name its own source files, which aren't there; those keep the
    /// game's music.
    /// </summary>
    internal static Source? SourceFor(CustomCharts.CustomChart chart)
    {
        var music = chart.Chart.GetTag("MUSIC")?.Trim();
        if (string.IsNullOrEmpty(music)) return null;
        var name = PackageFiles.SafeName(music!);
        if (name == null) return null;
        PackageFiles files;
        if (chart.PackEntry == null)
            files = PackageFiles.Folder(Path.GetDirectoryName(chart.SourceFile) ?? CustomCharts.Folder);
        else
        {
            string entry = chart.PackEntry;
            files = PackageFiles.Zip(chart.SourceFile, entry.Contains('/') ? entry.Substring(0, entry.LastIndexOf('/')) : "", rootFallback: true);
        }
        string stamp;
        try
        {
            if (!files.Exists(name)) return null;
            stamp = files.Stamp(name);
        }
        catch { return null; }
        return new Source
        {
            Name = $"{music} for {chart.DisplayName}",
            Key = $"{files.Describe(name)}|{stamp}",
            Read = () => files.ReadAllBytes(name, BattlePackage.MaxAudioBytes)
        };
    }

    /// <summary>
    /// Called by ChartSwap when a conductor starts a song: drops what an earlier song on that
    /// conductor left. Another conductor starting (a menu's, say) leaves a running battle's song alone.
    /// </summary>
    internal static void Reset(WwiseConductor starting)
    {
        try
        {
            if (owner != null && owner && starting && owner.Pointer != starting.Pointer && owner.initializedSong && !owner.endedSong) return;
        }
        catch { }
        Stop();
    }

    /// <summary>
    /// Called by ChartSwap when the battle's chart is built: starts decoding the song file, which
    /// plays when the conductor starts its music. <paramref name="customBattle"/> means the song has
    /// no Wwise music, so a file that fails leaves the battle silent rather than on the game's music.
    /// </summary>
    internal static void Prepare(Source? source, WwiseConductor battle, bool customBattle)
    {
        Stop();
        if (!battle) return;
        owner = battle;
        if (customBattle) customConductor = battle.Pointer;
        if (source == null) return;
        var cached = decoded is { } last && source.Key.Length > 0 && last.Key == source.Key ? last.Song : null;
        var file = source;
        // The sound device opens here too (about 0.1 s), off the main thread, so the battle doesn't
        // freeze when the music starts.
        var loading = Task.Run(() => Open(cached ?? Load(file)));
        pending = new Pending { Source = source, Loading = loading, Conductor = battle.Pointer, CustomBattle = customBattle };
        ModLog.Info($"Custom music {source.Name}: loading.");
    }

    private static Song Load(Source source)
    {
        if (source.Decoded != null)
        {
            // The editor's buffer itself, not a copy.
            var (pcm, pcmRate, pcmOrigin) = source.Decoded();
            return new Song { Stereo = pcm, Rate = pcmRate, Origin = pcmOrigin };
        }
        byte[] bytes = source.Read();
        var (stereo, rate) = AudioFile.Decode(bytes, source.Name);
        // The file's first sample is at clock time 0; the chart's #OFFSET is baked into its notes.
        return new Song { Stereo = stereo, Rate = rate, Origin = 0 };
    }

    // The player plays silence before the file's first sample, so the song can start before it
    // without a padded copy of the whole song.
    private static Loaded Open(Song song) =>
        new() { Song = song, Player = new EditorAudio(song.Stereo, song.Rate, LeadIn.Before(song.Origin)) };

    // A song that won't be played: its player is closed once it's open (off the main thread).
    private static void Drop(Pending? p)
    {
        if (p == null) return;
        p.Loading.ContinueWith(t =>
        {
            if (t.Status != TaskStatus.RanToCompletion) return;
            try { t.Result.Player.Dispose(); }
            catch { }
        }, TaskScheduler.Default);
    }

    // ---- starting ------------------------------------------------------------------------------

    // The conductor starts its music here, once the chart's clock reaches -0.1 s. For a prepared
    // song the mod starts its player instead and the game's music isn't posted.
    private static bool PlayTrackPrefix(WwiseConductor __instance)
    {
        var p = pending;
        if (p == null || !__instance || __instance.Pointer != p.Conductor) return true;
        try
        {
            if (p.WaitingSince < 0)
            {
                p.WaitingSince = Time.unscaledTime;
                p.StartCue = __instance.waitForStartCue;
            }
            if (!p.Loading.IsCompleted)
            {
                if (Time.unscaledTime - p.WaitingSince > (p.CustomBattle ? MaxSongLoadWait : MaxChartLoadWait))
                    throw new TimeoutException("the song file took too long to load");
                // Not starting the track keeps the chart's clock at its start: the conductor calls
                // again next frame. Combat waits with it.
                if (!p.Waited)
                {
                    p.Waited = true;
                    ModLog.Info($"Custom music {p.Source.Name}: the battle waits for the file to finish loading.");
                }
                if (!__instance.waitForStartCue)
                {
                    __instance.waitForStartCue = true;
                    p.HeldCombat = true;
                }
                return false;
            }
            Start(__instance, p, p.Loading.Result);
            return false;
        }
        catch (Exception ex)
        {
            var reason = ex is AggregateException agg && agg.InnerException != null ? agg.InnerException : ex;
            bool custom = p.CustomBattle;
            if (pending == p)
            {
                pending = null;
                Drop(p);
            }
            DisposePlayer();
            if (p.HeldCombat) __instance.waitForStartCue = false;
            if (!custom)
            {
                ModLog.Error($"The custom music {p.Source.Name} couldn't play, so the game's music plays: {reason}");
                // The silence from the notes' start would keep the game's music quiet.
                if (p.Silenced) TakeBackSilence();
                return true;
            }
            ModLog.Error($"The custom music {p.Source.Name} couldn't play, so the battle has no music: {reason}");
            try { StartWithoutMusic(__instance, p.StartCue, p.Silenced); }
            catch (Exception inner) { Report(inner); }
            return false;
        }
    }

    private static void Start(WwiseConductor c, Pending p, Loaded loaded)
    {
        pending = null;
        player = loaded.Player;
        var song = loaded.Song;
        // Kept for a retry of the same song, unless it's very long. A song without a key (the
        // editor's, in a test) is never kept, so its large buffer goes once the battle is over.
        if (p.Source.Key.Length > 0) decoded = song.Stereo.Length <= MaxCachedSamples ? (p.Source.Key, song) : null;
        origin = song.Origin;
        conductor = c;
        playingName = p.Source.Name;
        paused = false;
        fadeStart = -1f;
        double now = c.songPosition != null ? c.songPosition.RawTime : 0;
        player.SetMusicVolume(Volume());
        // The chart doesn't move on this frame, and the first time it reads the song (next frame)
        // it takes the player's position plus ClockLead. Starting the player that much and a frame
        // before the chart's time makes that reading match the chart, as it does for the rest of
        // the song, rather than pulling the chart about 60 ms over the song's first half second.
        double frame = FrameTime();
        player.Seek(now - origin - ClockLead - frame);   // in the lead-in silence at the song's start
        player.Play();
        usualFrame = Math.Max(frame, 1.0 / 240);
        report = StartReport.Begin(playingName, player);

        // The conductor takes the mod's id as the playing Wwise track. With gotSyncBeat off it
        // doesn't ask Wwise where that id is (BeatmapPrefix sets its clock instead), and with no
        // segment end or start cue to wait for, it follows the song to the chart's end.
        c.waitForStartCue = false;
        c.waitForEndCue = false;
        c.currentPlayingId = FakePlayingId;
        c.gotSyncBeat = false;
        loggedFollow = false;
        c.skipNextSongPosition = false;
        c.activePlayingDuration = NoSegmentEnd;
        c.currentWwiseTrackTime = SegmentTime(player);
        c.playingWwiseTrack = true;
        if (!p.Silenced) PostSilence();
        if (p.StartCue) InvokeStartCue(c);
        ModLog.Info($"Custom music {playingName} started at song time {now:0.000} ({player.Length:0.0}s).");
        StartReport.LogStart(playingName, p);
    }

    // The time until the next frame, from the last one's; a hitch doesn't count as a frame.
    private static double FrameTime()
    {
        try { return Math.Clamp(Time.unscaledDeltaTime, 0f, 1f / 30f); }
        catch { return 1.0 / 60; }
    }

    /// <summary>A custom battle whose file failed still plays its chart, on the game's own clock.</summary>
    private static void StartWithoutMusic(WwiseConductor c, bool startCue, bool silenced)
    {
        c.waitForStartCue = false;
        c.waitForEndCue = false;
        c.playingWwiseTrack = true;
        if (!silenced) PostSilence();
        if (startCue) InvokeStartCue(c);
    }

    // Songs with Wwise music announce their start cue, and the battle's start-cue sound hook runs
    // on it; the song the mod plays instead announces it the same way.
    private static void InvokeStartCue(WwiseConductor c)
    {
        try { c.OnSongStartCue?.Invoke(); }
        catch (Exception ex) { Report(ex); }
    }

    // SongUpdate steps the conductor's clock (its inlined UpdateSongPlayingTime) just before it
    // calls UpdateBeatmapPosition. With gotSyncBeat off, that step only added the frame time; this
    // redoes it with the song file's position, the way the game does with Wwise's, before the notes
    // move: the same checks, the same drift for the notes to catch up, the same large-drift event.
    // (Not a patch on AudioController.TryGetSongPosition: MelonLoader's Il2CppInterop can't call a
    // patched method with an out double from native code, so any patch there breaks every battle.)
    // Before the song starts, SongUpdate calls it too, to move the notes of a lead-in (BeforeSong).
    // The game's drift is the song's time less the clock before this frame's step, and the step adds
    // this frame's time on top, so the clock settles one frame ahead of the song, a frame being the
    // one just gone. After a long frame (a hitch) that's far ahead: the notes jump up to twice the
    // frame's length, then back on the next frame. So the clock aims at most MinFrameLead, or two
    // usual frames, ahead of the song. With steady frames that's the game's own lead, so the
    // latency calibration made against Wwise stays right.
    private static void BeatmapPrefix(WwiseConductor __instance, double deltaTime)
    {
        var p = player;
        if (p == null)
        {
            BeforeSong(__instance, deltaTime);
            return;
        }
        try
        {
            var c = conductor;
            if (c == null || !c || !__instance || c.Pointer != __instance.Pointer) return;
            // SongUpdate also calls this before the music starts.
            if (!c.playingWwiseTrack || c.currentPlayingId != FakePlayingId) return;
            double position = SegmentTime(p);
            double before = c.currentWwiseTrackTime - deltaTime;   // the clock before SongUpdate's step
            if (Math.Abs(position - before) > 1.0)                  // the game ignores jumps over a second
            {
                SkipInReport(c, deltaTime);
                return;
            }
            if (c.skipNextSongPosition)
            {
                if (!((double)0.05f > Math.Abs(position - c.activePlayingDuration))) c.skipNextSongPosition = false;
                SkipInReport(c, deltaTime);
                return;
            }
            if (position > c.activePlayingDuration) return;
            var song = c.songPosition;
            if (song == null) return;
            c.currentWwiseTrackTime = position;
            double measured = position + c.previousSongSegmentTime - song.RawTime;
            // The clock then aims at the song plus the shorter of this frame and the lead.
            double excess = deltaTime - Math.Max(MinFrameLead, 2 * usualFrame);
            double drift = measured;
            if (excess > 0)
            {
                drift = measured - excess;
                // GetTimeCorrection moves a drift under 0.1 s by a fixed 5 or 10% of the frame. After a
                // long frame that's more than this small drift, and the clock would end ahead of its lead:
                // it just takes the frame, as the song did.
                if (drift > 0 && drift < 0.1 * Math.Min(deltaTime, 1)) drift = 0;
            }
            usualFrame += (Math.Min(deltaTime, 0.25) - usualFrame) * 0.1;
            c.timeDrift = drift;
            if (Math.Abs((float)measured) > 0.5f) c.OnLargeTimeDrift?.Invoke();
            if (!loggedFollow)
            {
                loggedFollow = true;
                ModLog.Info($"Custom music {playingName}: the chart follows the song file (clock {position:0.000}, drift {measured:0.0000}).");
            }
            // The player's own time is the position less what SegmentTime adds.
            if (report?.Frame(deltaTime, position - ClockLead - origin + c.previousSongSegmentTime, measured, drift, song.RawTime) == true)
                EndReport(stopped: false);
        }
        catch (Exception ex) { Report(ex); }
    }

    // A battle whose clock starts before its song (ChartSwap's lead-in) moves its notes before the
    // conductor starts the music: SongUpdate calls UpdateBeatmapPosition while the clock is before
    // -0.1 s. The music from before the battle fades as those notes start, as it does when the song
    // starts with the clock, rather than playing on under them until the song starts. Its frames
    // are counted for the song's start report.
    private static void BeforeSong(WwiseConductor c, double deltaTime)
    {
        var p = pending;
        if (p == null) return;
        try
        {
            if (!c || c.Pointer != p.Conductor) return;
            p.LeadInFrames++;
            p.LongestLeadInFrame = Math.Max(p.LongestLeadInFrame, deltaTime);
            if (p.Silenced) return;
            p.Silenced = true;
            PostSilence();
        }
        catch (Exception ex) { Report(ex); }
    }

    // Wwise user cues from other events (posted with callbacks) would replace the mod's playing
    // id and the segment times, so the battle's conductor doesn't take any while the mod's song
    // plays, nor during a custom battle, which has no Wwise music of its own.
    private static bool CuePrefix(WwiseConductor __instance) => !OwnsConductor(__instance, customToo: true);

    private static bool BeatPrefix(WwiseConductor __instance) => !OwnsConductor(__instance, customToo: false);

    private static bool OwnsConductor(WwiseConductor c, bool customToo)
    {
        try
        {
            if (!c) return false;
            IntPtr pointer = c.Pointer;
            if (player != null && conductor && conductor!.Pointer == pointer) return true;
            return customToo && customConductor != IntPtr.Zero && customConductor == pointer;
        }
        catch { return false; }
    }

    // ---- following and stopping ----------------------------------------------------------------

    /// <summary>Called every frame: pause with the game, follow the volume, fade, and end with the file.</summary>
    internal static void Update()
    {
        var p = player;
        if (p == null) return;
        try
        {
            if (!conductor)
            {
                Stop();
                return;
            }
            var c = conductor!;
            bool live = c.initializedSong && !c.endedSong;
            // A conductor reset without its song ending (another song started on it) takes the song down.
            if (!live && !c.endedSong && fadeStart < 0)
            {
                Stop();
                return;
            }
            float fade = 1f;
            if (fadeStart >= 0)
            {
                float t = (Time.unscaledTime - fadeStart) / fadeLength;
                if (t >= 1f)
                {
                    Stop();
                    return;
                }
                fade = 1f - t;
            }
            if (p.Failed != null) throw new InvalidOperationException(p.Failed);
            // The file ended: the player stops by itself once its end has been heard. A pause in the
            // moment after that (the last buffer's silence) leaves it at its end too, and resuming
            // would start the song over, so that ends it as well.
            if ((!paused && !p.Playing) || p.Ended)
            {
                Stop();
                return;
            }
            // A song paused when its battle ends (quit from the pause menu) stays paused while it
            // fades, rather than playing on for a moment as the game unpauses.
            bool pause = held || (paused && (!live || fadeStart >= 0)) || (live && (BattlePaused(c) || c.Paused));
            if (pause != paused)
            {
                paused = pause;
                if (pause) p.Pause(); else p.Play();
                if (pause && report != null) report.Paused = true;
            }
            p.SetMusicVolume(Volume() * fade);
        }
        catch (Exception ex)
        {
            Report(ex);
            Stop();
        }
    }

    /// <summary>Fades the song out over <paramref name="seconds"/>, or sooner if a fade is already ending.</summary>
    private static void FadeOut(float seconds)
    {
        if (player == null) return;
        float now = Time.unscaledTime;
        if (fadeStart >= 0 && fadeStart + fadeLength <= now + seconds) return;
        // A shorter fade carries on down from the current level, reaching silence in the new time.
        float level = fadeStart >= 0 ? Mathf.Clamp(1f - (now - fadeStart) / fadeLength, 0.01f, 1f) : 1f;
        fadeLength = Mathf.Max(0.05f, seconds / level);
        fadeStart = now - (1f - level) * fadeLength;
    }

    // The game's victory music; it also resets the silence the song started with.
    private static void VictoryPostfix()
    {
        silencePosted = false;
        FadeOut(EndFade);
    }

    // The enemy's defeat (pre-end music) and leaving combat.
    private static void FadePostfix() => FadeOut(EndFade);

    // The game unloads the song as the battle ends, just before the results; the song fades
    // quickly there rather than cutting off.
    private static void UnloadPrefix(WwiseConductor __instance)
    {
        try
        {
            if (pending != null && __instance && pending.Conductor == __instance.Pointer)
            {
                Drop(pending);
                pending = null;
            }
            if (player != null && conductor && __instance && conductor!.Pointer == __instance.Pointer) FadeOut(UnloadFade);
        }
        catch (Exception ex) { Report(ex); }
    }

    // Leaving combat ends everything, and takes back the silence if no victory music did.
    private static void ExitPostfix()
    {
        Stop();
        if (silencePosted) TakeBackSilence();
    }

    private static void TakeBackSilence()
    {
        silencePosted = false;
        try { AkSoundEngine.SetState("Global_Silence", "None"); }
        catch (Exception ex) { Report(ex); }
    }

    private static void Stop()
    {
        DisposePlayer();
        Drop(pending);
        pending = null;
        owner = null;
        customConductor = IntPtr.Zero;
    }

    private static void DisposePlayer()
    {
        // A song that stops within its first seconds still reports them.
        EndReport(stopped: true);
        if (player != null)
        {
            // Stopping and closing the sound device take up to tens of ms, so they're done on a worker.
            var old = player;
            player = null;
            old.CloseInBackground();
            ModLog.Info($"Custom music {playingName} stopped.");
        }
        // The conductor goes back to its own clock rather than asking for a song that's gone.
        try
        {
            if (conductor && conductor!.currentPlayingId == FakePlayingId)
            {
                conductor.currentPlayingId = 0;
                conductor.gotSyncBeat = false;
                conductor.activePlayingDuration = 0;
            }
        }
        catch { }
        conductor = null;
        paused = false;
        held = false;
        fadeStart = -1f;
    }

    // Fades the music that played before the battle (menu or overworld) to silence.
    private static void PostSilence()
    {
        try
        {
            var controller = AudioController.Instance;
            if (!controller) return;
            uint id = AkSoundEngine.PostEvent(SilenceEvent, controller.gameObject);
            silencePosted = true;
            if (id == 0) ModLog.Error($"Wwise didn't play {SilenceEvent}, so the music from before the battle may keep playing.");
        }
        catch (Exception ex) { Report(ex); }
    }

    /// <summary>The player's volume from the game's master, music and combat music settings.</summary>
    private static float Volume()
    {
        try { return Mathf.Clamp01(AudioController.MasterVolume) * Mathf.Clamp01(AudioController.MusicVolume) * Mathf.Clamp01(AudioController.CombatMusicVolume); }
        catch { return 1f; }
    }

    private static void Report(Exception ex)
    {
        if (reportedError) return;
        reportedError = true;
        ModLog.Error("Custom music playback failed: " + ex);
    }

    // ---- the start report ------------------------------------------------------------------------

    // A frame whose song position the game doesn't take, for the start report.
    private static void SkipInReport(WwiseConductor c, double deltaTime)
    {
        var r = report;
        if (r == null) return;
        var song = c.songPosition;
        if (r.Skip(deltaTime, song != null ? song.RawTime : double.NaN)) EndReport(stopped: false);
    }

    /// <summary>Logs the song's start report if it hasn't been, and lets it go.</summary>
    private static void EndReport(bool stopped)
    {
        var r = report;
        report = null;
        r?.Log(stopped);
    }

    /// <summary>
    /// What a song's first seconds did to the chart's clock, so a player's log shows why the first
    /// notes looked fast or jerky: long frames, the drift between the clock and the song and how the
    /// game took it up, the fastest the notes moved against real time, and the song's time from the
    /// sound device against real time (a device that starts late, or counts audio as played before
    /// it's heard).
    /// Logged once, <see cref="ReportAfter"/> s after the song starts or when it stops sooner. Until
    /// then a timer read and a few sums a frame, and nothing after.
    /// </summary>
    private sealed class StartReport
    {
        private static readonly double TickSeconds = 1.0 / System.Diagnostics.Stopwatch.Frequency;
        private readonly string name;
        private readonly long started = System.Diagnostics.Stopwatch.GetTimestamp();
        private readonly double from;   // the player's time as it started
        private int frames, longFrames, limited, whole, half, nudged, skipped, still;
        private double longest, longestAt, driftLow = double.NaN, driftHigh = double.NaN, fastest, fastestAt;
        private double offLow = double.NaN, offHigh = double.NaN, offAtOne = double.NaN, off = double.NaN;
        private double clockAtOne = double.NaN, realAtOne, endClock = double.NaN, endReal;
        private double lastClock = double.NaN, lastReal, lastStep, lastSong, movedAt;
        internal bool Paused;   // paused in its first seconds, so its times include the pause

        private StartReport(string name, double from)
        {
            this.name = name;
            this.from = from;
            lastSong = from;
        }

        /// <summary>A report for a song that just started playing on <paramref name="player"/>.</summary>
        internal static StartReport? Begin(string name, EditorAudio player)
        {
            try { return new StartReport(name, player.Time); }
            catch { return null; }
        }

        /// <summary>The first line, as the song starts: the frame it started on, the lead-in and the wait.</summary>
        internal static void LogStart(string name, Pending p)
        {
            try
            {
                ModLog.Info($"Custom music {name}: the song started on a {Time.unscaledDeltaTime * 1000:0} ms frame, " +
                            (p.LeadInFrames > 0 ? $"after {Count(p.LeadInFrames, "frame")} of lead-in (the longest {p.LongestLeadInFrame * 1000:0} ms)" : "with no lead-in") +
                            (p.Waited ? $", after waiting {Time.unscaledTime - p.WaitingSince:0.00} s for the file" : "") +
                            $"; time scale {Time.timeScale:0.##}.");
            }
            catch { }
        }

        private double Seconds() => (System.Diagnostics.Stopwatch.GetTimestamp() - started) * TickSeconds;

        /// <summary>
        /// A frame whose song position the game doesn't take (a jump over a second, or one it skips);
        /// <paramref name="clock"/> is the chart's clock before this frame's step. True once the report is due.
        /// </summary>
        internal bool Skip(double deltaTime, double clock)
        {
            skipped++;
            Length(deltaTime, clock);
            // The clock's next step isn't compared with this frame's.
            lastClock = double.NaN;
            lastReal = Seconds();
            return lastReal >= ReportAfter;
        }

        private void Length(double deltaTime, double clock)
        {
            if (deltaTime > 0.1) longFrames++;
            if (deltaTime > longest)
            {
                longest = deltaTime;
                longestAt = clock;
            }
        }

        /// <summary>
        /// A frame the chart followed the song: <paramref name="song"/> is the player's time,
        /// <paramref name="measured"/> the drift the game would take, <paramref name="drift"/> the
        /// drift it was given, and <paramref name="clock"/> the chart's clock before this frame's
        /// step. True once the report is due.
        /// </summary>
        internal bool Frame(double deltaTime, double song, double measured, double drift, double clock)
        {
            double real = Seconds();
            frames++;
            Length(deltaTime, clock);
            if (drift != measured) limited++;
            driftLow = double.IsNaN(driftLow) ? measured : Math.Min(driftLow, measured);
            driftHigh = double.IsNaN(driftHigh) ? measured : Math.Max(driftHigh, measured);
            // The game's GetTimeCorrection: over 0.3 s at once, over 0.1 s by half, from 0.01 s by 10% of the frame.
            double size = Math.Abs(drift);
            if (size > 0.3) whole++;
            else if (size > 0.1) half++;
            else if (size >= 0.01) nudged++;
            // The clock's step on the last frame (the clock now less then) shows over the time that
            // frame took (from the frame before to then), against which 1 is normal speed.
            if (!double.IsNaN(lastClock) && lastStep > 0.001)
            {
                double speed = (clock - lastClock) / lastStep;
                if (speed > fastest)
                {
                    fastest = speed;
                    fastestAt = lastClock;
                }
            }
            lastStep = real - lastReal;
            lastClock = endClock = clock;
            lastReal = endReal = real;
            // The player's time against real time since it started: steady is right (a few ms under 0
            // is the sound device starting), a rise is audio counted as played before it was heard,
            // and a fall or standing still is the device stopping for a moment.
            off = song - from - real;
            if (real <= 1)
            {
                offLow = double.IsNaN(offLow) ? off : Math.Min(offLow, off);
                offHigh = double.IsNaN(offHigh) ? off : Math.Max(offHigh, off);
            }
            else if (double.IsNaN(offAtOne))
            {
                offAtOne = off;
                clockAtOne = clock;
                realAtOne = real;
            }
            // Positions come in steps of up to tens of ms, so it stood still once one lasts over 50 ms.
            if (song != lastSong) movedAt = real;
            else if (real - movedAt > 0.05) still++;
            lastSong = song;
            return real >= ReportAfter;
        }

        internal void Log(bool stopped)
        {
            try
            {
                string first = $"the first {Math.Max(endReal, lastReal):0.0} s{(stopped ? " (it stopped)" : "")}";
                if (frames == 0)
                {
                    ModLog.Info($"Custom music {name}, {first}: the chart never followed the song ({Count(skipped, "song position")} skipped).");
                    return;
                }
                string rate = !double.IsNaN(clockAtOne) && endReal - realAtOne >= 0.5 ? ((endClock - clockAtOne) / (endReal - realAtOne)).ToString("0.000") : "-";
                ModLog.Info($"Custom music {name}, {first}: {Count(frames, "frame")}, the longest {longest * 1000:0} ms at clock {Clock(longestAt)} ({longFrames} over 100 ms); " +
                            $"drift {Ms(driftLow)} to {Ms(driftHigh)} ms, taken up {Count(whole, "time")} at once, {Count(half, "time")} by half and {Count(nudged, "frame")} by 10%; " +
                            $"the clock's lead was held back after {Count(limited, "long frame")}; the notes moved at most {fastest:0.00}x normal speed (at clock {Clock(fastestAt)}); " +
                            $"the clock ran {rate}x real time after the first second; {Count(skipped, "song position")} skipped.");
                ModLog.Info($"Custom music {name}: the sound device's time ran {Ms(offLow)} to {Ms(offHigh)} ms from real time in the first second, " +
                            $"{Ms(offAtOne)} ms at 1 s and {Ms(off)} ms at {endReal:0.0} s; it stood still for {Count(still, "frame")}{(Paused ? "; the song was paused in between" : "")}.");
            }
            catch { }
        }

        private static string Ms(double seconds) => double.IsNaN(seconds) ? "-" : (seconds * 1000).ToString("+0;-0;0");

        private static string Clock(double seconds) => (Math.Abs(seconds) < 0.005 ? 0 : seconds).ToString("0.00");

        private static string Count(int count, string what) => count == 1 ? $"1 {what}" : $"{count} {what}s";
    }
}
