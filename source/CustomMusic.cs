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
/// time for the game's songs. The game's chart reader applies #OFFSET itself (beat 0 comes at
/// clock time -OFFSET, as in StepMania), so the file is never shifted by it here.
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
        internal double Origin;   // clock time of the first sample, after the lead-in silence
    }

    // A song decoding for a battle, until the conductor starts its music.
    private sealed class Pending
    {
        internal Source Source = null!;
        internal Task<Song> Loading = null!;
        internal IntPtr Conductor;
        internal bool CustomBattle;          // no Wwise music to fall back on
        internal float WaitingSince = -1f; // when the conductor first wanted to start
        internal bool StartCue;            // whether the conductor was waiting for Wwise's start cue
        internal bool HeldCombat;          // the wait set waitForStartCue, so combat waits too
        internal bool Waited;
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

    private static Pending? pending;
    private static WwiseConductor? owner;      // the conductor of the battle the song belongs to
    private static IntPtr customConductor;     // a custom battle's conductor, which has no Wwise cues to take
    private static EditorAudio? player;
    private static WwiseConductor? conductor;  // set while the song plays
    private static double origin;
    private static string playingName = "";
    private static bool paused, silencePosted;
    private static float fadeStart = -1f, fadeLength;
    private static (string Key, Song Song)? decoded;   // the last song decoded, for a quick retry
    private static bool reportedError;
    private static bool loggedFollow;   // "the chart follows the song file", once per song

    internal static bool Active => player != null;

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
        Task<Song> loading;
        if (decoded is { } cached && source.Key.Length > 0 && cached.Key == source.Key)
            loading = Task.FromResult(cached.Song);
        else
        {
            var file = source;
            loading = Task.Run(() => Load(file));
        }
        pending = new Pending { Source = source, Loading = loading, Conductor = battle.Pointer, CustomBattle = customBattle };
        ModLog.Info($"Custom music {source.Name}: loading.");
    }

    private static Song Load(Source source)
    {
        if (source.Decoded != null)
        {
            var (pcm, pcmRate, pcmOrigin) = source.Decoded();
            // Copied only when it needs silence in front.
            var (lead, leadOrigin) = LeadIn.Pad(pcm, pcmRate, pcmOrigin);
            return new Song { Stereo = lead, Rate = pcmRate, Origin = leadOrigin };
        }
        byte[] bytes = source.Read();
        var (stereo, rate) = AudioFile.Decode(bytes, source.Name);
        // The file's first sample is at clock time 0; the chart's #OFFSET is the game's to apply.
        var (padded, first) = LeadIn.Pad(stereo, rate, 0);
        return new Song { Stereo = padded, Rate = rate, Origin = first };
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
            pending = null;
            DisposePlayer();
            if (p.HeldCombat) __instance.waitForStartCue = false;
            if (!custom)
            {
                ModLog.Error($"The custom music {p.Source.Name} couldn't play, so the game's music plays: {reason}");
                return true;
            }
            ModLog.Error($"The custom music {p.Source.Name} couldn't play, so the battle has no music: {reason}");
            try { StartWithoutMusic(__instance, p.StartCue); }
            catch (Exception inner) { Report(inner); }
            return false;
        }
    }

    private static void Start(WwiseConductor c, Pending p, Song song)
    {
        pending = null;
        // Kept for a retry of the same song, unless it's very long. A song without a key (the
        // editor's, in a test) is never kept, so its large buffer goes once the battle is over.
        if (p.Source.Key.Length > 0) decoded = song.Stereo.Length <= MaxCachedSamples ? (p.Source.Key, song) : null;
        player = new EditorAudio(song.Stereo, song.Rate);
        origin = song.Origin;
        conductor = c;
        playingName = p.Source.Name;
        paused = false;
        fadeStart = -1f;
        double now = c.songPosition != null ? c.songPosition.RawTime : 0;
        player.SetMusicVolume(Volume());
        player.Seek(now - origin);   // at least StartMargin into the file, thanks to the lead-in silence
        player.Play();

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
        PostSilence();
        if (p.StartCue) InvokeStartCue(c);
        ModLog.Info($"Custom music {playingName} started at song time {now:0.000} ({player.Length:0.0}s).");
    }

    /// <summary>A custom battle whose file failed still plays its chart, on the game's own clock.</summary>
    private static void StartWithoutMusic(WwiseConductor c, bool startCue)
    {
        c.waitForStartCue = false;
        c.waitForEndCue = false;
        c.playingWwiseTrack = true;
        PostSilence();
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
    private static void BeatmapPrefix(WwiseConductor __instance, double deltaTime)
    {
        var p = player;
        if (p == null) return;
        try
        {
            var c = conductor;
            if (c == null || !c || !__instance || c.Pointer != __instance.Pointer) return;
            // SongUpdate also calls this before the music starts.
            if (!c.playingWwiseTrack || c.currentPlayingId != FakePlayingId) return;
            double position = SegmentTime(p);
            double before = c.currentWwiseTrackTime - deltaTime;   // the clock before SongUpdate's step
            if (Math.Abs(position - before) > 1.0) return;          // the game ignores jumps over a second
            if (c.skipNextSongPosition)
            {
                if (!((double)0.05f > Math.Abs(position - c.activePlayingDuration))) c.skipNextSongPosition = false;
                return;
            }
            if (position > c.activePlayingDuration) return;
            var song = c.songPosition;
            if (song == null) return;
            c.currentWwiseTrackTime = position;
            double drift = position + c.previousSongSegmentTime - song.RawTime;
            c.timeDrift = drift;
            if (Math.Abs((float)drift) > 0.5f) c.OnLargeTimeDrift?.Invoke();
            if (!loggedFollow)
            {
                loggedFollow = true;
                ModLog.Info($"Custom music {playingName}: the chart follows the song file (clock {position:0.000}, drift {drift:0.0000}).");
            }
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
            // The file ended (the player stops by itself at its end).
            if (!paused && !p.Playing)
            {
                Stop();
                return;
            }
            // A song paused when its battle ends (quit from the pause menu) stays paused while it
            // fades, rather than playing on for a moment as the game unpauses.
            bool pause = (paused && (!live || fadeStart >= 0)) || (live && (AudioController.IsPausedCombat || c.Paused));
            if (pause != paused)
            {
                paused = pause;
                if (pause) p.Pause(); else p.Play();
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
            if (pending != null && __instance && pending.Conductor == __instance.Pointer) pending = null;
            if (player != null && conductor && __instance && conductor!.Pointer == __instance.Pointer) FadeOut(UnloadFade);
        }
        catch (Exception ex) { Report(ex); }
    }

    // Leaving combat ends everything, and takes back the silence if no victory music did.
    private static void ExitPostfix()
    {
        Stop();
        if (!silencePosted) return;
        silencePosted = false;
        try { AkSoundEngine.SetState("Global_Silence", "None"); }
        catch (Exception ex) { Report(ex); }
    }

    private static void Stop()
    {
        DisposePlayer();
        pending = null;
        owner = null;
        customConductor = IntPtr.Zero;
    }

    private static void DisposePlayer()
    {
        if (player != null)
        {
            try { player.Dispose(); } catch { }
            player = null;
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
}
