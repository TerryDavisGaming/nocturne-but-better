using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Video;
using Object = UnityEngine.Object;

namespace NocturneFlatScroll;

/// <summary>
/// Custom enemy art: a custom battle whose enemy is in custom mode looks like the player's own
/// pictures, sprite sheets, GIFs or videos. This half loads an enemy's art for a fight: the
/// files are read and decoded on worker threads (ArtLoader), and the Unity steps (decoding PNGs
/// and JPEGs, uploading textures, making sprites and video players) run on the main thread, one
/// a frame while a fight is coming, or all at once when it starts. One set is loaded at a time
/// for battles; the battle creator's preview loads its own the same way (BattleCreator.ArtPreview.cs).
/// The battle half (EnemyArt.Battle.cs) shows it on the enemy.
/// </summary>
internal static partial class EnemyArt
{
    /// <summary>How long a fight's start waits for its art.</summary>
    private const float CollectSeconds = 5f;
    /// <summary>
    /// How much longer a battle's first fight waits for its idle video to be prepared once the rest
    /// is loaded. Short: the video player may need the frames the wait holds up to get ready.
    /// </summary>
    private const float IdleVideoWait = 0.5f;
    /// <summary>How long a video may take to get ready, in frames after a fight's start.</summary>
    private const float PrepareSeconds = 8f;
    /// <summary>A warm start nobody fights is let go after this long.</summary>
    private const float UnusedSeconds = 60f;
    private const long CacheBytes = 1024L * 1024 * 1024;

    /// <summary>One animation as the battle shows it: a sprite per frame, or one sprite over a video.</summary>
    internal sealed class Clip
    {
        internal string Name = "";
        internal ArtAnimationSpec Spec = null!;
        internal Sprite[] Frames = Array.Empty<Sprite>();
        internal float[] Holds = Array.Empty<float>();
        internal float Length;
        internal bool Loop, Flip;
        internal double Hit = -1, Parry = -1;
        internal VideoArt? Video;
        /// <summary>The idle's visible width in game pixels, for the shadow.</summary>
        internal float VisibleWidth;
        /// <summary>
        /// For the creator's preview: the texture the frames are in (the atlas, or a video's render
        /// texture), each frame's rect in it, the pivot (the feet) as a share of a frame from its
        /// bottom-left, and game pixels per stored pixel.
        /// </summary>
        internal Texture? Texture;
        internal int TextureW, TextureH;
        internal (int X, int Y, int W, int H)[] Rects = Array.Empty<(int, int, int, int)>();
        internal float PivotX, PivotY, Scale = 1f;

        internal bool Usable => Video == null || !Video.Failed;
    }

    /// <summary>Whether the idle can be shown: loaded (a video once it's prepared), still loading, or failed.</summary>
    internal enum IdleLoad { Ready, Loading, Failed }

    /// <summary>A video animation: its player renders into a texture that is copied into its sprite's texture.</summary>
    internal sealed class VideoArt
    {
        internal VideoPlayer Player = null!;
        internal RenderTexture Target = null!;
        internal Texture2D Copy = null!;
        internal string File = "";
        /// <summary>Ready to play (seen by the callback or by polling), or given up on.</summary>
        internal bool Prepared, Failed;
        /// <summary>The prepare callback came.</summary>
        internal bool Called;
        internal bool Playing;
        /// <summary>A frame was copied since the player last started, so its sprite can show.</summary>
        internal bool Fresh;
        /// <summary>A frame was ever copied into its sprite's texture (until then it's clear).</summary>
        internal bool Shown;
        internal string? Error;
        /// <summary>When the player was made, and when its time to get ready started (real seconds).</summary>
        internal float MadeAt, WaitFrom;
        internal long LastFrame = -1;
        // The callbacks are set as fields (the add_ methods throw); the delegates are kept alive here.
        internal VideoPlayer.EventHandler? OnPrepared;
        internal VideoPlayer.ErrorEventHandler? OnError;
    }

    /// <summary>One enemy's loaded art: a battle's, or the battle creator's preview.</summary>
    internal sealed class ArtSet : IArtLoadHost
    {
        /// <summary>The battle it's loaded for; null for the creator's preview.</summary>
        internal readonly CustomBattles.Battle? Battle;
        internal readonly string Title;
        /// <summary>The game enemy it looks like when its idle can't be used, for messages.</summary>
        internal readonly string LookName;
        internal readonly EnemyArtSpec Spec;
        internal readonly PackageFiles Files;
        internal readonly ArtLoader Loader;
        internal readonly CancellationTokenSource Cancel = new();
        internal readonly Stopwatch Clock = Stopwatch.StartNew();
        internal readonly Dictionary<string, Clip> Clips = new(StringComparer.OrdinalIgnoreCase);
        internal readonly List<Object> Made = new();
        internal Task Work = Task.CompletedTask;
        internal bool Disposed, Reported, Used;
        internal int Textures, Sprites, Videos;
        internal float WarmedAt;
        /// <summary>
        /// Pictures already decoded, by file and stamp, shared between loads (the creator's preview
        /// loads again after each change); null keeps none.
        /// </summary>
        internal Dictionary<string, Picture>? Pictures;
        /// <summary>How the cleanup line starts in the log.</summary>
        internal string LogName = "Enemy art";

        internal ArtSet(CustomBattles.Battle battle, string? cache)
            : this(battle.Title, battle.Package.Art!, battle.Package.Files, battle.Package.EnemyPlaceholder, cache)
        {
            Battle = battle;
        }

        internal ArtSet(string title, EnemyArtSpec spec, PackageFiles files, string lookName, string? cache)
        {
            Title = title;
            LookName = lookName;
            Spec = spec;
            Files = files;
            Loader = new ArtLoader(spec, files, this, cache);
        }

        internal ArtLoadResult Result => Loader.Result;
        internal bool Done => Work.IsCompleted;
        /// <summary>The idle loaded (a video's player was made; it may still be getting ready).</summary>
        internal bool IdleLoaded => Clips.TryGetValue("idle", out var idle) && idle.Usable;

        internal IdleLoad IdleState
        {
            get
            {
                if (!Clips.TryGetValue("idle", out var idle)) return Done ? IdleLoad.Failed : IdleLoad.Loading;
                var v = idle.Video;
                return v == null || (v.Prepared && !v.Failed) ? IdleLoad.Ready : v.Failed ? IdleLoad.Failed : IdleLoad.Loading;
            }
        }

        /// <summary>Why the idle isn't ready, for the log: "idle: ..." when it failed, "its idle ..." while it loads.</summary>
        internal string IdleWhy()
        {
            if (Result.Failed.TryGetValue("idle", out var failed)) return "idle: " + failed;
            if (Clips.TryGetValue("idle", out var idle) && idle.Video is { } v)
                return v.Failed ? $"idle: {v.File} can't play ({v.Error ?? "it wasn't ready in time"})" : $"its idle video {v.File} wasn't ready yet";
            return Done ? "its idle couldn't be loaded" : $"its idle took longer than {CollectSeconds:0} s to load";
        }

        /// <summary>A video still getting ready counts its time again from now (a fight's start held up the frames it needs).</summary>
        internal void RestartVideoClocks()
        {
            float now = Time.realtimeSinceStartup;
            foreach (var clip in Clips.Values)
                if (clip.Video is { Prepared: false, Failed: false } v) v.WaitFrom = now;
        }

        /// <summary>Starts loading on worker threads; the main-thread steps wait for the pump.</summary>
        internal void Begin() => Work = Task.Run(() => Loader.Run(Cancel.Token));

        /// <summary>The key a decoded picture is kept under: its file, and its size and time.</summary>
        internal string PictureKey(string file) => file + "|" + Files.Stamp(file);

        Task<Picture> IArtLoadHost.DecodePicture(ArtAnimationSpec a, byte[] file, CancellationToken cancel)
        {
            var kept = Pictures;
            string key = kept == null ? "" : PictureKey(a.File);
            if (kept != null)
                lock (kept)
                    if (kept.TryGetValue(key, out var known)) return Task.FromResult(known);
            return OnMain(() =>
            {
                if (Disposed) throw new OperationCanceledException();
                var picture = ReadPicture(file, a.File);
                if (kept != null) lock (kept) kept[key] = picture;
                return picture;
            }, cancel);
        }

        string? IArtLoadHost.VideoUnsupported(VideoFacts facts) =>
            facts.Container == "mp4" && !MediaFoundation.Value
                ? "needs Windows Media Foundation, which this PC doesn't have (Windows N needs the Media Feature Pack), so use a VP8 WebM"
                : null;

        Task IArtLoadHost.Frames(PackedAnimation packed, bool smooth, CancellationToken cancel) =>
            OnMain(() => { Upload(packed, smooth); return true; }, cancel);

        Task IArtLoadHost.Video(VideoPlan plan, bool smooth, CancellationToken cancel) =>
            OnMain(() => { MakeVideo(plan, smooth); return true; }, cancel);

        private void Upload(PackedAnimation p, bool smooth)
        {
            if (Disposed) throw new OperationCanceledException();
            var a = p.From.Spec;
            var texture = new Texture2D(p.AtlasW, p.AtlasH, TextureFormat.RGBA32, false)
            {
                name = $"{CustomBattles.RuntimePrefix}art/{Title}/{a.Name}",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = smooth ? FilterMode.Bilinear : FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            Made.Add(texture);
            Textures++;
            var pin = GCHandle.Alloc(p.Atlas, GCHandleType.Pinned);
            try { texture.LoadRawTextureData(pin.AddrOfPinnedObject(), p.Atlas.Length); }
            finally { pin.Free(); }
            // Uploaded, and the CPU copy dropped.
            texture.Apply(false, true);
            var frames = new Sprite[p.Rects.Length];
            for (int i = 0; i < frames.Length; i++)
            {
                var r = p.Rects[i];
                var sprite = Sprite.Create(texture, new Rect(r.X, r.Y, r.W, r.H), new Vector2(p.PivotX, p.PivotY), p.Ppu, 0u, SpriteMeshType.FullRect, Vector4.zero);
                sprite.name = texture.name + "/" + i;
                sprite.hideFlags = HideFlags.HideAndDontSave;
                Made.Add(sprite);
                Sprites++;
                frames[i] = sprite;
            }
            var clip = NewClip(p.From, frames);
            (clip.Texture, clip.TextureW, clip.TextureH, clip.Rects) = (texture, p.AtlasW, p.AtlasH, p.Rects);
            (clip.PivotX, clip.PivotY, clip.Scale) = (p.PivotX, p.PivotY, 1f / p.Ppu);
            Clips[a.Name] = clip;
        }

        private Clip NewClip(MeasuredAnimation m, Sprite[] frames)
        {
            var t = m.Timeline;
            var k = Result.K * (m.Spec.Name == "idle" ? 1 : m.Spec.Scale);
            return new Clip
            {
                Name = m.Spec.Name,
                Spec = m.Spec,
                Frames = frames,
                Holds = t.Holds.Length > 0 ? t.Holds : new[] { (float)t.Length },
                Length = (float)t.Length,
                Loop = t.Loop,
                Flip = Spec.Flip ^ m.Spec.Flip,
                Hit = t.Hit,
                Parry = t.Parry,
                VisibleWidth = (float)(m.VisibleW * k),
            };
        }

        private void MakeVideo(VideoPlan plan, bool smooth)
        {
            if (Disposed) throw new OperationCanceledException();
            var m = plan.From;
            var a = m.Spec;
            string name = $"{CustomBattles.RuntimePrefix}art/{Title}/{a.Name}";
            var target = new RenderTexture(plan.TextureW, plan.TextureH, 0, RenderTextureFormat.ARGB32) { name = name + "/video", hideFlags = HideFlags.HideAndDontSave };
            target.Create();
            Made.Add(target);
            var copy = new Texture2D(plan.TextureW, plan.TextureH, TextureFormat.RGBA32, false)
            {
                name = name,
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = smooth || Spec.Smooth == null ? FilterMode.Bilinear : FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            Made.Add(copy);
            Textures += 2;
            // Clear until the first frame comes.
            var clear = new byte[plan.TextureW * plan.TextureH * 4];
            var pin = GCHandle.Alloc(clear, GCHandleType.Pinned);
            try { copy.LoadRawTextureData(pin.AddrOfPinnedObject(), clear.Length); }
            finally { pin.Free(); }
            copy.Apply(false, true);
            var sprite = Sprite.Create(copy, new Rect(0, 0, plan.TextureW, plan.TextureH), new Vector2(plan.PivotX, plan.PivotY), plan.Ppu, 0u, SpriteMeshType.FullRect, Vector4.zero);
            sprite.name = name;
            sprite.hideFlags = HideFlags.HideAndDontSave;
            Made.Add(sprite);
            Sprites++;

            float now = Time.realtimeSinceStartup;
            var video = new VideoArt { Target = target, Copy = copy, File = a.File, MadeAt = now, WaitFrom = now };
            var player = VideoHost().AddComponent<VideoPlayer>();
            Made.Add(player);
            Videos++;
            video.Player = player;
            player.playOnAwake = false;
            player.source = VideoSource.Url;
            player.url = m.VideoPath!.Replace('\\', '/');
            player.renderMode = VideoRenderMode.RenderTexture;
            player.targetTexture = target;
            player.aspectRatio = VideoAspectRatio.Stretch;
            // The game's Unity audio is off; its sounds are Wwise.
            player.audioOutputMode = VideoAudioOutputMode.None;
            // Scaled game time, like the enemy's own animator, so pauses and slow-downs follow.
            player.timeUpdateMode = VideoTimeUpdateMode.GameTime;
            player.isLooping = m.Timeline.Loop;
            player.skipOnDrop = true;
            player.waitForFirstFrame = true;
            if (Math.Abs(a.Speed - 1) > 0.001) player.playbackSpeed = (float)a.Speed;
            video.OnPrepared = DelegateSupport.ConvertDelegate<VideoPlayer.EventHandler>(new Action<VideoPlayer>(_ => video.Called = true));
            video.OnError = DelegateSupport.ConvertDelegate<VideoPlayer.ErrorEventHandler>(new Action<VideoPlayer, string>((_, message) => video.Error ??= message ?? "the video player failed"));
            player.prepareCompleted = video.OnPrepared;
            player.errorReceived = video.OnError;
            player.Prepare();

            var clip = NewClip(m, new[] { sprite });
            clip.Video = video;
            (clip.Texture, clip.TextureW, clip.TextureH, clip.Rects) = (target, plan.TextureW, plan.TextureH, new[] { (0, 0, plan.TextureW, plan.TextureH) });
            (clip.PivotX, clip.PivotY, clip.Scale) = (plan.PivotX, plan.PivotY, 1f / plan.Ppu);
            Clips[a.Name] = clip;
        }

        /// <summary>
        /// Follows the videos getting ready (the callbacks, and polling in case they don't come).
        /// Real time, so a fight's start that waits on the main thread is counted as it passes.
        /// </summary>
        internal void WatchVideos()
        {
            float now = Time.realtimeSinceStartup;
            foreach (var clip in Clips.Values)
            {
                var v = clip.Video;
                if (v == null || v.Failed || !v.Player) continue;
                if (v.Error != null)
                {
                    v.Failed = true;
                    ModLog.Error($"Enemy art for {Title}: video {v.File} can't play ({v.Error}); {VideoStandIn(clip.Name)}.");
                    continue;
                }
                if (v.Prepared) continue;
                if (v.Called || v.Player.isPrepared)
                {
                    v.Prepared = true;
                    ModLog.Info($"Enemy art: video {v.File} prepared in {(now - v.MadeAt) * 1000:0} ms ({v.Player.width} x {v.Player.height}, {ArtLoadResult.Sec(v.Player.length)} s{(v.Called ? "" : "; its callback didn't come")}).");
                }
                else if (now - v.WaitFrom > PrepareSeconds)
                {
                    v.Failed = true;
                    ModLog.Error($"Enemy art for {Title}: video {v.File} wasn't ready after {PrepareSeconds:0} s; {VideoStandIn(clip.Name)}.");
                }
            }
        }

        // What shows instead of an animation whose video can't play.
        private string VideoStandIn(string name)
        {
            if (name != "idle") return EnemyArtReader.StandIn(name, Spec);
            if (Battle == null) return "the preview can't show the enemy";
            return Battle.Look == CustomBattles.ArtLook.Rig ? EnemyArtReader.StandIn(name, Spec) : $"the enemy looks like {LookName}";
        }

        /// <summary>Logs how loading went, once it's done.</summary>
        internal void Report()
        {
            if (Reported || !Done) return;
            Reported = true;
            if (Work.IsFaulted && Work.Exception?.GetBaseException() is not OperationCanceledException)
                ModLog.Error($"Enemy art for {Title}: loading failed: {Work.Exception?.GetBaseException()}");
            var problems = Battle?.Package.Problems ?? new List<string>();
            foreach (var dropped in Spec.Dropped)
                ModLog.Error($"Enemy art for {Title}: {dropped.Key} can't be used ({dropped.Value}); {EnemyArtReader.StandIn(dropped.Key, Spec)}.");
            foreach (var failed in Result.Failed)
                ModLog.Error($"Enemy art for {Title}: {failed.Key} can't be used ({failed.Value}); {(failed.Key == "idle" ? $"the enemy looks like {LookName}" : EnemyArtReader.StandIn(failed.Key, Spec))}.");
            foreach (var note in Result.Notes.Where(n => !problems.Contains(n)).Distinct()) ModLog.Info($"Enemy art for {Title}: {note}.");
            foreach (var error in Result.Errors) ModLog.Error($"Enemy art for {Title}: {error}");
            if (IdleLoaded) ModLog.Info($"Enemy art for {Title}: ready in {Clock.ElapsedMilliseconds} ms: {Result.Describe()}.");
        }

        internal void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            Cancel.Cancel();
            foreach (var clip in Clips.Values)
            {
                var v = clip.Video;
                if (v == null) continue;
                try
                {
                    if (v.Player) v.Player.Stop();
                    if (v.Target) v.Target.Release();
                }
                catch (Exception ex) { ModLog.Error($"Enemy art: stopping {v.File} failed: {ex.Message}"); }
            }
            foreach (var obj in Made)
                if (obj) Object.Destroy(obj);
            Made.Clear();
            Clips.Clear();
            ModLog.Info($"{LogName}: cleaned up {Textures} textures, {Sprites} sprites, {Videos} videos.");
        }
    }

    private static readonly ConcurrentQueue<Action> MainQueue = new();
    private static ArtSet? current;
    private static GameObject? videoHost;
    private static string? cacheFolder;
    private static bool installed, reportedPump;

    /// <summary>Whether Media Foundation (Windows' own video decoders, which MP4s need) is on this PC.</summary>
    private static readonly Lazy<bool> MediaFoundation = new(() =>
    {
        try { return LoadLibraryW("mfplat.dll") != IntPtr.Zero; }
        catch { return false; }
    });

    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryW(string name);

    /// <summary>Runs <paramref name="work"/> on the main thread (the next time the queue is pumped).</summary>
    private static Task<T> OnMain<T>(Func<T> work, CancellationToken cancel)
    {
        // The worker's continuation must not run inside the main thread's pump.
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        MainQueue.Enqueue(() =>
        {
            if (cancel.IsCancellationRequested)
            {
                done.TrySetCanceled();
                return;
            }
            try { done.TrySetResult(work()); }
            catch (Exception ex) { done.TrySetException(ex); }
        });
        return done.Task;
    }

    /// <summary>
    /// Unity's own PNG and JPEG decoder (the one cards use, but readable here: the pixels are read
    /// back), then the pixels as RGBA, top row first. Main thread only; the file's size was checked
    /// from its header first.
    /// </summary>
    internal static Picture ReadPicture(byte[] file, string name)
    {
        var texture = CustomBattles.CardImages.DecodeReadable(file, CustomBattles.RuntimePrefix + "art/" + name, EnemyArtReader.MaxPictureSide, "pictures", out string? why)
            ?? throw new InvalidDataException($"{name} {why}");
        try
        {
            int w = texture.width, h = texture.height;
            // Whatever format the decoder made, GetPixels32 gives RGBA, bottom row first.
            var pixels = texture.GetPixels32();
            if (pixels == null || pixels.Length != w * h) throw new InvalidDataException($"{name} couldn't be read back");
            var rgba = new byte[w * h * 4];
            IntPtr start = IntPtr.Add(pixels.Pointer, 4 * IntPtr.Size);   // an il2cpp array's elements start after its header
            for (int y = 0; y < h; y++) Marshal.Copy(IntPtr.Add(start, (h - 1 - y) * w * 4), rgba, y * w * 4, w * 4);
            GC.KeepAlive(pixels);
            return new Picture { Rgba = rgba, Width = w, Height = h };
        }
        finally { Object.Destroy(texture); }
    }

    /// <summary>Runs up to <paramref name="max"/> main-thread loading steps, for the creator's preview (which loads without a fight).</summary>
    internal static void RunSteps(int max) => Pump(max);

    /// <summary>Runs up to <paramref name="max"/> main-thread steps; false when there were none.</summary>
    private static bool Pump(int max)
    {
        bool any = false;
        for (int i = 0; i < max && MainQueue.TryDequeue(out var step); i++)
        {
            any = true;
            step();
        }
        return any;
    }

    /// <summary>One active hidden object for every video player (a player on an inactive object won't prepare).</summary>
    private static GameObject VideoHost()
    {
        if (videoHost) return videoHost!;
        videoHost = new GameObject("NocturneButBetter enemy videos") { hideFlags = HideFlags.HideAndDontSave };
        Object.DontDestroyOnLoad(videoHost);
        return videoHost;
    }

    /// <summary>
    /// Starts loading a battle's art, so it's ready when its fight starts: from the arcade's click
    /// on the battle, or the chart editor's test. Loading the same battle again does nothing;
    /// another battle's art is let go first. A battle that already looks like its placeholder for
    /// good (or has no rig) loads nothing.
    /// </summary>
    internal static void Warm(CustomBattles.Battle battle, string from)
    {
        if (!installed || battle.Package.Art == null || battle.RigArt == null || battle.Look == CustomBattles.ArtLook.Placeholder) return;
        try
        {
            if (current != null && current.Battle == battle && !current.Disposed) return;
            Start(battle);
            ModLog.Info($"Enemy art for {battle.Title}: warm start from {from}.");
        }
        catch (Exception ex) { ReportPump(ex); }
    }

    private static ArtSet Start(CustomBattles.Battle battle)
    {
        Release(current);
        var set = new ArtSet(battle, CacheFolder()) { WarmedAt = Time.unscaledTime };
        set.Begin();
        current = set;
        return set;
    }

    /// <summary>Where videos that can't play in place are copied; trimmed to CacheBytes the first time it's asked for.</summary>
    internal static string CacheFolder()
    {
        if (cacheFolder != null) return cacheFolder;
        cacheFolder = Path.Combine(Application.persistentDataPath, "NocturneButBetter", "Cache", "EnemyArt");
        string folder = cacheFolder;
        Task.Run(() =>
        {
            try { ArtLoader.TrimCache(folder, CacheBytes); }
            catch (Exception) { }
        });
        return cacheFolder;
    }

    /// <summary>
    /// The battle's art for a fight that is starting: whatever loading is left is done now, for at
    /// most CollectSeconds. Animations that are still loading after that are used once they're ready.
    /// With <paramref name="idleVideo"/> (the look isn't decided yet) an idle video that's still
    /// getting ready is waited for a little longer (IdleVideoWait), within the same limit.
    /// </summary>
    private static ArtSet Collect(CustomBattles.Battle battle, bool idleVideo)
    {
        var set = current != null && current.Battle == battle && !current.Disposed ? current : Start(battle);
        set.Used = true;
        var wait = Stopwatch.StartNew();
        double doneAt = -1;
        while (wait.Elapsed.TotalSeconds < CollectSeconds)
        {
            if (set.Done)
            {
                set.WatchVideos();
                if (doneAt < 0) doneAt = wait.Elapsed.TotalSeconds;
                if (!idleVideo || set.IdleState != IdleLoad.Loading || wait.Elapsed.TotalSeconds - doneAt >= IdleVideoWait) break;
            }
            if (!Pump(int.MaxValue)) Thread.Sleep(1);
        }
        Pump(int.MaxValue);
        set.WatchVideos();
        set.RestartVideoClocks();
        if (idleVideo && doneAt >= 0 && set.Clips.TryGetValue("idle", out var idle) && idle.Video != null)
            ModLog.Info($"Enemy art for {set.Title}: the idle video is {(idle.Video.Prepared ? "prepared" : idle.Video.Failed ? "not playable" : "still getting ready")} " +
                        $"after {wait.ElapsedMilliseconds} ms at the fight's start.");
        set.Report();
        return set;
    }

    /// <summary>Lets go of a battle's art (it's being rebuilt or thrown away), unless a fight is showing it.</summary>
    internal static void Drop(CustomBattles.Battle battle)
    {
        try
        {
            if (current == null || current.Battle != battle) return;
            if (fight != null && fight.Set == current && fight.Alive) return;
            Release(current);
        }
        catch (Exception ex) { ReportPump(ex); }
    }

    private static void Release(ArtSet? set)
    {
        if (set == null) return;
        set.Dispose();
        if (current == set) current = null;
    }

    /// <summary>Every frame: the main-thread loading steps, the videos, and the fight's art.</summary>
    internal static void LateUpdate()
    {
        if (!installed) return;
        try
        {
            // One step a frame, so a battle's transition doesn't hitch.
            Pump(1);
            var set = current;
            if (set != null && !set.Disposed)
            {
                set.Report();
                set.WatchVideos();
                if (!set.Used && fight == null && Time.unscaledTime - set.WarmedAt > UnusedSeconds)
                {
                    ModLog.Info($"Enemy art for {set.Title}: not fought, so it's let go.");
                    Release(set);
                }
            }
            UpdateFight();
        }
        catch (Exception ex) { ReportPump(ex); }
    }

    private static void ReportPump(Exception ex)
    {
        if (reportedPump) return;
        reportedPump = true;
        ModLog.Error("Custom enemy art failed: " + ex);
    }
}
