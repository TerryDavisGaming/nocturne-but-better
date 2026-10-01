using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace NocturnePlus;

/// <summary>
/// Potato: Optimized plus changes you can see, for weak PCs. The game's own lighter Combat Backdrop
/// and Corruption Effects settings (the defaults it picks for graphics cards with 2 GB or less); the
/// game's cheap bloom on every camera; a lower resolution scaled up without blur, in whole steps of
/// the art's 270-line grid; the 2D lights drawn at that grid too (one light pixel for each pixel of
/// art); the menu blur at half resolution; scene and room fades twice as quick, with a scene's
/// fade-in started during the game's own short black hold; a short curtain into arcade battles of
/// the game's songs, and into replays of a custom song that is already decoded; and 30 frames a
/// second while the game is in the background outside battles or from a paused battle. Everything
/// here is kept up once a second (the resolution and lights also on the frame the screen changes)
/// and put back when Potato is left, when the combat backdrop's picture is also let go.
/// </summary>
internal static partial class Performance
{
    // QA builds only: NFS_QA_PERF_SKIP=scale,lights,blur,bloom,fades,fadein,curtain,options,background,
    // pausedbattle leaves those parts of Potato out, for pictures of each on its own; prewarm leaves out
    // Optimized's first-battle warm-up (FirstBattleWarmup), and rgba its one-go pixel copy (SkinSprites).
    private static readonly HashSet<string> QaSkip = new((QaBuild.Env("NFS_QA_PERF_SKIP") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    internal static bool Skip(string part) => QaSkip.Contains(part);

    private static void EnterPotato()
    {
        // Before the first title screen the startup check takes them.
        if (started) TakeGameOptions();
        KeepPotato();
    }

    private static void KeepPotato()
    {
        KeepScreenParts();
        if (!Skip("blur")) Run("the lighter menu blur", KeepBlur, RestoreBlur);
        if (!Skip("bloom")) Run("the bloom cap", KeepBloomCaps, HideBloomCaps);
        if (!Skip("fades")) Run("the quicker fades", KeepFades, RestoreFades);
        if (!Skip("curtain")) Run("the arcade curtain", KeepCurtainListener);
    }

    /// <summary>The parts that follow the screen's height: the lower resolution first, then the lights drawn at it.</summary>
    private static void KeepScreenParts()
    {
        if (!Skip("scale")) Run("the lower resolution", KeepRenderScale, RestoreRenderScale);
        if (!Skip("lights")) Run("the 2D lights", KeepLightScale, RestoreLightScale);
    }

    /// <param name="giveOptions">Put the game's two settings back (only what the mod still owns).</param>
    private static void LeavePotato(bool giveOptions)
    {
        // Undone even for a part that failed this session, since it may have changed something first.
        if (giveOptions && started) Undo("the game's settings", GiveGameOptions);
        Undo("the combat backdrop's picture", FreeBackdrop);
        Undo("the lower resolution", RestoreRenderScale);
        Undo("the 2D lights", RestoreLightScale);
        Undo("the lighter menu blur", RestoreBlur);
        Undo("the bloom cap", HideBloomCaps);
        Undo("the quicker fades", RestoreFades);
        Undo("the background frame rate", RestoreFrameCap);
        // Entering Potato again (or a restart) looks at the screen afresh.
        seenHeight = 0;
    }

    private static void HideBloomCaps() => ShowBloomCaps(false);

    // ---- following the screen ---------------------------------------------------------------------------

    // The screen's height when last looked at. A change (a new window size, Alt+Enter, another
    // monitor) is followed on the next frame, not up to a second later; 0 is a minimized window.
    private static int seenHeight;

    private static void FollowScreenHeight()
    {
        int height = Screen.height;
        if (height <= 0 || height == seenHeight) return;
        seenHeight = height;
        KeepScreenParts();
    }

    // ---- the lower resolution -------------------------------------------------------------------------

    // Screens of 2160 lines and more draw 1080 (half, or less), a whole number of drawn lines per pixel
    // of the art's 480x270 grid. The game's 2D renderer scales that up with a slight blur (qa: a
    // one-pixel ramp on each edge at 4K), whatever the upscaling filter says, so smaller screens,
    // where the step wouldn't be a whole one and the blur would show, keep their full resolution.
    private static UniversalRenderPipelineAsset? scaledAsset;
    private static float savedScale;
    private static UpscalingFilterSelection savedFilter;
    private static int scaledFor;

    // Half a line more, so the engine's rounding down still lands on the whole line.
    internal static float ScaleFor(int height) => height >= 2160 ? (1080 + 0.5f) / height : 1f;

    private static void KeepRenderScale()
    {
        var asset = UniversalRenderPipeline.asset;
        if (asset == null) return;
        // Another asset (the game never swaps it): the old one's values can't be put back on this one.
        if (scaledAsset != null && (!scaledAsset || scaledAsset.Pointer != asset.Pointer)) scaledAsset = null;
        int height = Screen.height;
        float scale = ScaleFor(height);
        if (scale >= 1f)
        {
            RestoreRenderScale();
            return;
        }
        if (scaledAsset == null)
        {
            savedScale = asset.renderScale;
            savedFilter = asset.upscalingFilter;
            scaledAsset = asset;
        }
        if (Math.Abs(asset.renderScale - scale) > 0.0001f) asset.renderScale = scale;
        if (asset.upscalingFilter != UpscalingFilterSelection.Point) asset.upscalingFilter = UpscalingFilterSelection.Point;
        if (scaledFor != height)
        {
            scaledFor = height;
            ModLog.Info($"Performance (Potato): drawing {(int)(height * scale)} lines for the {height}-line screen, scaled up without blur.");
        }
    }

    private static void RestoreRenderScale()
    {
        var asset = scaledAsset;
        scaledAsset = null;
        scaledFor = 0;
        if (asset == null || !asset) return;
        asset.renderScale = savedScale;
        asset.upscalingFilter = savedFilter;
    }

    // ---- the 2D lights ----------------------------------------------------------------------------------

    // The 2D renderer draws each light into a texture of the drawn size times its light scale (0.5 as
    // shipped), read every frame. The art is a 270-line grid and the lights are soft, so one light
    // pixel for each art pixel is enough: their edges soften from about half an art pixel to about
    // one. Never smaller than an eighth, never larger than the game's own. It's runtime state on the
    // renderer's data, which a player build never saves.
    private const float ArtLines = 270f, SmallestLightScale = 0.125f;
    // By the renderer data's pointer: the game's value, and the mod's last one.
    private static readonly Dictionary<IntPtr, (Renderer2DData Data, float Saved, float Set)> LightScales = new();

    /// <summary>The default renderer's 2D data, or null when it isn't the 2D renderer.</summary>
    private static Renderer2DData? LightsData(UniversalRenderPipelineAsset? asset)
    {
        if (asset == null) return null;
        var list = asset.m_RendererDataList;
        int index = asset.m_DefaultRendererIndex;
        if (list == null || index < 0 || index >= list.Length) return null;
        var data = list[index]?.TryCast<Renderer2DData>();
        return data != null && data ? data : null;
    }

    private static void KeepLightScale()
    {
        var asset = UniversalRenderPipeline.asset;
        var data = LightsData(asset);
        if (data == null) return;
        int drawn = (int)(Screen.height * asset!.renderScale);
        if (drawn <= 0) return;
        float current = data.m_LightRenderTextureScale;
        if (!LightScales.TryGetValue(data.Pointer, out var entry)) entry = (data, current, current);
        float scale = Math.Min(entry.Saved, Math.Max(SmallestLightScale, ArtLines / drawn));
        LightScales[data.Pointer] = (data, entry.Saved, scale);
        if (current == scale) return;
        data.m_LightRenderTextureScale = scale;
        ModLog.Info($"Performance (Potato): 2D lights drawn at {scale:0.###} of the {drawn} drawn lines (the game's {entry.Saved:0.###}).");
    }

    private static void RestoreLightScale()
    {
        foreach (var (data, saved, set) in LightScales.Values)
            if (data && Math.Abs(data.m_LightRenderTextureScale - set) < 0.0001f) data.m_LightRenderTextureScale = saved;
        LightScales.Clear();
    }

    // ---- the menu blur ----------------------------------------------------------------------------------

    // The blur behind the menus (two cameras' TranslucentImageSource) works on a copy of the screen;
    // Downsample 1 blurs a half-size copy instead of a full one.
    private static readonly Dictionary<IntPtr, (TranslucentImageSource Source, int Saved)> Blurs = new();
    private static float nextBlurSearch;

    private static void KeepBlur()
    {
        foreach (var key in Blurs.Where(pair => !pair.Value.Source).Select(pair => pair.Key).ToList()) Blurs.Remove(key);
        if (Blurs.Count == 0)
        {
            if (Time.unscaledTime < nextBlurSearch) return;
            nextBlurSearch = Time.unscaledTime + 5f;
            foreach (var source in Resources.FindObjectsOfTypeAll<TranslucentImageSource>())
                if (source && source.gameObject.scene.IsValid()) Blurs[source.Pointer] = (source, source.Downsample);
            if (Blurs.Count == 0) return;
        }
        foreach (var (source, saved) in Blurs.Values)
        {
            int lighter = Math.Max(saved, 1);
            if (source.Downsample != lighter) source.Downsample = lighter;
        }
    }

    private static void RestoreBlur()
    {
        foreach (var (source, saved) in Blurs.Values)
            if (source && source.Downsample == Math.Max(saved, 1)) source.Downsample = saved;
        Blurs.Clear();
    }

    // ---- the bloom cap ----------------------------------------------------------------------------------

    // The game's own cheap bloom (BloomQualityCap, its Cheap Bloom setting) is one global volume on
    // layer 0, which the battle camera doesn't read (it reads layer 10), so the battles' strongest
    // bloom is never capped. Potato has one of the same on each layer, just above the game's.
    private const float CapPriority = 10001f;
    private static readonly int[] CapLayers = { 0, 10 };
    private static readonly GameObject?[] Caps = new GameObject?[2];

    private static void KeepBloomCaps()
    {
        for (int i = 0; i < Caps.Length; i++)
        {
            var cap = Caps[i];
            if (cap == null || !cap) Caps[i] = cap = MakeBloomCap(CapLayers[i]);
            if (!cap.activeSelf) cap.SetActive(true);
        }
    }

    private static void ShowBloomCaps(bool shown)
    {
        foreach (var cap in Caps)
            if (cap != null && cap && cap.activeSelf != shown) cap.SetActive(shown);
    }

    private static GameObject MakeBloomCap(int layer)
    {
        var go = new GameObject("NocturnePlus_PotatoBloom" + layer);
        // The volume goes on the layer it's made on.
        go.layer = layer;
        Object.DontDestroyOnLoad(go);
        try
        {
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = "NocturnePlus_PotatoBloom";
            // Every scene change unloads unused assets; these are only held by the mod.
            profile.hideFlags = HideFlags.DontUnloadUnusedAsset;
            var bloom = profile.Add(Il2CppType.Of<Bloom>(), false).Cast<Bloom>();
            bloom.hideFlags = HideFlags.DontUnloadUnusedAsset;
            // As the game's own cap: a quarter-size start, few passes, no high quality filter.
            bloom.downscale.Override(BloomDownscaleMode.Quarter);
            bloom.maxIterations.Override(CapIterations());
            bloom.highQualityFiltering.Override(false);
            var volume = go.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = CapPriority;
            volume.sharedProfile = profile;
            return go;
        }
        catch
        {
            Object.Destroy(go);
            throw;
        }
    }

    // The game's cap's own number of passes (3), clamped as it clamps it.
    private static int CapIterations()
    {
        foreach (var cap in Resources.FindObjectsOfTypeAll<BloomQualityCap>())
            if (cap) return Math.Clamp(cap.maxIterations, 2, 8);
        return 3;
    }

    // ---- quicker fades ----------------------------------------------------------------------------------

    private const float FadeFactor = 0.5f, ShortestFade = 0.15f;
    private static SceneTransitionController? fadedOn;
    private static float savedRoomFade, savedSceneFade;

    private static float Quicker(float seconds) => seconds <= ShortestFade ? seconds : Math.Max(ShortestFade, seconds * FadeFactor);

    private static void KeepFades()
    {
        var stc = Transitions();
        // Never partway through a change: its fades are timed from these.
        if (stc == null || (int)stc.State != 0) return;
        if (fadedOn == null || !fadedOn || fadedOn.Pointer != stc.Pointer)
        {
            fadedOn = stc;
            savedRoomFade = stc.RoomTransitionTime;
            savedSceneFade = stc.SceneTransitionTime;
            ModLog.Info($"Performance (Potato): scene fades {savedSceneFade:0.##} s -> {Quicker(savedSceneFade):0.##} s, room fades {savedRoomFade:0.##} s -> {Quicker(savedRoomFade):0.##} s.");
        }
        float room = Quicker(savedRoomFade), scene = Quicker(savedSceneFade);
        if (stc.RoomTransitionTime != room) stc.RoomTransitionTime = room;
        if (stc.SceneTransitionTime != scene) stc.SceneTransitionTime = scene;
    }

    private static void RestoreFades()
    {
        var stc = fadedOn;
        fadedOn = null;
        if (stc == null || !stc) return;
        if (stc.RoomTransitionTime == Quicker(savedRoomFade)) stc.RoomTransitionTime = savedRoomFade;
        if (stc.SceneTransitionTime == Quicker(savedSceneFade)) stc.SceneTransitionTime = savedSceneFade;
    }

    // ---- the quicker fade-in ----------------------------------------------------------------------------

    // Once a new scene is in and the player placed, the game holds the black curtain for a fixed
    // 0.25 s (real time), waits for followers and for a cutscene about to start, and only then fades
    // in, through the curtain's own fade (which it skips when the curtain is already lifting). Potato
    // starts that same fade during the hold, when nothing the game waits for is pending, so the room
    // shows up to about a third of a second sooner.
    private const float BlackHold = 0.25f;
    private const int FramesBeforeFadeIn = 2;
    private static int quickFadeFrames;
    private static bool quickFadeTried, quickFadeDecided;
    private static string quickFadeWhy = "";

    private static void BeginQuickFadeIn()
    {
        quickFadeFrames = 0;
        quickFadeTried = quickFadeDecided = false;
        quickFadeWhy = "";
    }

    // The state left 5 (the game's own fade-in ran, or the change ended) before Potato decided.
    private static void EndQuickFadeIn()
    {
        if (quickFadeDecided || !quickFadeTried || !Potato) return;
        quickFadeDecided = true;
        ModLog.Info($"Performance (Potato): the game's own fade-in into {SceneTransitionController.CurrentSceneName}: {(quickFadeWhy.Length > 0 ? quickFadeWhy : "it came first")}.");
    }

    private static void TickQuickFadeIn()
    {
        var stc = transitions;
        if (stc is null) return;
        quickFadeTried = true;
        float held = Time.unscaledTime - stateSince;
        // A frame early: the game's hold is timed on the real clock, which can end it mid-frame.
        if (held + Time.unscaledDeltaTime >= BlackHold)
        {
            quickFadeDecided = true;
            ModLog.Info($"Performance (Potato): the game's own fade-in into {SceneTransitionController.CurrentSceneName}: {(quickFadeWhy.Length > 0 ? quickFadeWhy : "its black hold was over")}.");
            return;
        }
        if (++quickFadeFrames <= FramesBeforeFadeIn) return;
        string why = WhyNoQuickFadeIn(stc, out var curtain);
        if (why.Length > 0)
        {
            quickFadeWhy = why;
            return;
        }
        quickFadeDecided = true;
        // The call the game's own fade makes (its curtain adapter), on the same controller.
        stc.StartCoroutine(curtain!.FadeInFromCurtain(stc.SceneTransitionTime, CurtainController.FadeGroup.BaseCanvas));
        ModLog.Info($"Performance (Potato): the fade-in into {SceneTransitionController.CurrentSceneName} started {held:0.00} s into the game's {BlackHold:0.##} s black hold.");
    }

    /// <summary>Empty when the game would fade in itself once its hold is over, with nothing left to wait for.</summary>
    private static string WhyNoQuickFadeIn(SceneTransitionController stc, out CurtainController? curtain)
    {
        curtain = null;
        // Set when the change began, for a fade the game makes itself; a cutscene that takes over
        // the reveal clears it.
        if (!stc.pendingCutsceneReveal) return "a cutscene reveals the scene";
        if (stc.BlockFadeIn) return "the fade-in is held back";
        if (stc.isLoadingBackground) return "a background is still loading";
        var found = stc.curtainController;
        if (found == null || !found || !found.IsShowingCurtain) return "the curtain isn't down";
        if (found.IsAnimating) return "the curtain is moving";
        var group = found.canvasGroup;
        if (group == null || !group || group.alpha < 0.999f) return "the curtain isn't fully black";
        if (SceneTransitionController.CutsceneManagerBusy) return "a cutscene is running";
        var player = PlayerController.Instance;
        if (player != null && player)
        {
            var followers = FollowerManager.Instance;
            if (followers != null && followers && followers._waitingForRefresh) return "followers are still arriving";
        }
        if (SceneTransitionController.AnyPendingSceneStartCutscene()) return "a cutscene is about to start";
        curtain = found;
        return "";
    }

    // ---- the arcade curtain -----------------------------------------------------------------------------

    // Arcade battles go in through a 1.5 s fade to black and a 1.5 s fade back. A battle's options can
    // ask for its own fade length, which Potato does for arcade battles from the title's arcade that
    // play the game's own music. A custom song file needs the full curtain to load behind, unless it's
    // the song just played (still decoded, so only the sound device opens, off the main thread);
    // custom art always does (its video warms up behind it).
    private const float ArcadeCurtain = 0.35f;
    private static SceneTransitionController? listeningOn;
    private static UnityAction<CombatOptions>? willEnterCombat;
    private static readonly List<Shortened> ShortenedCurtains = new();

    private sealed class Shortened
    {
        internal CombatOptions Options = null!;
        internal bool Alternate, SawBattle;
        internal float Duration, At;
    }

    private static void KeepCurtainListener()
    {
        var stc = Transitions();
        if (stc == null || (listeningOn != null && listeningOn && listeningOn.Pointer == stc.Pointer)) return;
        willEnterCombat ??= DelegateSupport.ConvertDelegate<UnityAction<CombatOptions>>((Action<CombatOptions>)WillEnterCombat)
            ?? throw new InvalidOperationException("couldn't make the battle listener");
        stc.OnWillEnterCombat.AddListener(willEnterCombat);
        listeningOn = stc;
    }

    // Runs inside the game's own start of a battle, before it picks the curtain.
    private static void WillEnterCombat(CombatOptions options)
    {
        try
        {
            if (!Potato || Skip("curtain") || options == null) return;
            if (options.CombatType != CombatType.Arcade || options.useAlternateTransition) return;
            if (GameManager.GameState != GameStates.MainMenu || TestPlay.Active) return;
            var song = options.Song;
            if (song == null || !song) return;
            // Why a custom song gets the short curtain; empty for the game's own music.
            string custom = "";
            if (CustomBattles.Find(song) is { } battle)
            {
                string? full = battle.Package.CustomArt ? "it has custom art"
                    : battle.Music == null ? "it has no song file"
                    : !CustomMusic.IsDecoded(battle.Music.Key) ? "its song isn't decoded yet" : null;
                if (full != null)
                {
                    ModLog.Info($"Performance (Potato): the full curtain into {battle.Title}: {full}.");
                    return;
                }
                custom = " (its song is already decoded)";
            }
            else if (CustomCharts.Selected(song.name) is { } chart && CustomMusic.SourceFor(chart) is { } source)
            {
                if (!CustomMusic.IsDecoded(source.Key))
                {
                    ModLog.Info($"Performance (Potato): the full curtain into {song.name}: its chart's song isn't decoded yet.");
                    return;
                }
                custom = " (its chart's song is already decoded)";
            }
            ShortenedCurtains.Add(new Shortened { Options = options, Alternate = options.useAlternateTransition, Duration = options.transitionDuration, At = Time.unscaledTime });
            options.useAlternateTransition = true;
            options.transitionDuration = ArcadeCurtain;
            ModLog.Info($"Performance (Potato): a {ArcadeCurtain:0.##} s curtain into {song.name}{custom}.");
        }
        catch (Exception ex)
        {
            if (FailedParts.Add("the arcade curtain")) ModLog.Error("Performance: the arcade curtain failed, so it's off for this session: " + ex);
        }
    }

    // The game makes new options for each arcade battle; these are put back anyway once the battle is
    // over, in case something keeps them.
    private static void PutBackCurtains()
    {
        if (ShortenedCurtains.Count == 0) return;
        bool inBattle = GameManager.GameState == GameStates.Combat;
        for (int i = ShortenedCurtains.Count - 1; i >= 0; i--)
        {
            var entry = ShortenedCurtains[i];
            if (inBattle) { entry.SawBattle = true; continue; }
            if (!entry.SawBattle && Time.unscaledTime - entry.At < 120f) continue;
            entry.Options.useAlternateTransition = entry.Alternate;
            entry.Options.transitionDuration = entry.Duration;
            ShortenedCurtains.RemoveAt(i);
        }
    }

    // ---- the frame rate in the background ---------------------------------------------------------------

    private const int BackgroundFrameRate = 30;
    private static bool frameCapped, cappedInBattle;
    private static int savedVsync, savedFrameRate;
    // QA builds hold their own frame rate (QaNoteSpeed); NFS_QA_PERF_BACKGROUND=1 lets this be tested.
    private static readonly bool QaHoldsFrameRate = QaBuild.On && QaBuild.Env("NFS_QA_PERF_BACKGROUND") != "1";

    private static void TickFrameCap()
    {
        bool want = Potato && !QaHoldsFrameRate && !Skip("background") && !Application.isFocused;
        bool inBattle = want && GameManager.GameState == GameStates.Combat;
        if (inBattle) want = PausedBattle();
        if (frameCapped && (QualitySettings.vSyncCount != 0 || Application.targetFrameRate != BackgroundFrameRate))
        {
            // The game set its own (a screen change): those stand.
            frameCapped = false;
            return;
        }
        // Out of the battle (quit from its pause menu) and still in the background.
        if (want && frameCapped) cappedInBattle = inBattle;
        if (want == frameCapped) return;
        if (!want)
        {
            if (cappedInBattle) ModLog.Info($"Performance (Potato): the full frame rate again in the battle ({(Application.isFocused ? "back in the game" : "it's running")}).");
            RestoreFrameCap();
            return;
        }
        savedVsync = QualitySettings.vSyncCount;
        savedFrameRate = Application.targetFrameRate;
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = BackgroundFrameRate;
        frameCapped = true;
        cappedInBattle = inBattle;
        if (inBattle) ModLog.Info($"Performance (Potato): {BackgroundFrameRate} frames a second while in the background from a paused battle.");
    }

    // A battle paused from its pause menu. Its combat manager's own pause ends when the resume
    // countdown does (before this frame's check), while the menu's pause state only ends once the
    // menu has closed, so both must hold: no frame of a running battle is ever capped. The countdown
    // itself runs on real time, with the notes stopped and input locked.
    private static bool PausedBattle() =>
        !Skip("pausedbattle") && UserInterface.PauseState == PauseStates.Paused && CustomMusic.BattlePaused(null);

    private static void RestoreFrameCap()
    {
        if (!frameCapped) return;
        frameCapped = cappedInBattle = false;
        if (QualitySettings.vSyncCount != 0 || Application.targetFrameRate != BackgroundFrameRate) return;
        QualitySettings.vSyncCount = savedVsync;
        Application.targetFrameRate = savedFrameRate;
    }

    // ---- the combat backdrop's picture ------------------------------------------------------------------

    // With Combat Backgrounds on, the game draws the room once into a screen-size picture, which it
    // only lets go of when the component goes (or the screen size changes). Once Potato has given the
    // setting back (off), that picture isn't used again, so it's released (not destroyed): if the
    // setting is turned on later, the game's next capture makes it again and clears it fully first.
    private static void FreeBackdrop()
    {
        if (NocturneSettings.CachedCombatBackdrop) return;
        foreach (var cache in Resources.FindObjectsOfTypeAll<CombatBackdropCache>())
        {
            if (!cache || cache._applied) continue;
            var camera = cache._captureCamera;
            if (camera != null && camera && camera.enabled) continue;
            var picture = cache._renderTexture;
            if (picture == null || !picture || !picture.IsCreated()) continue;
            picture.Release();
            ModLog.Info($"Performance: let go of the combat backdrop's {picture.width}x{picture.height} picture.");
        }
    }
}
