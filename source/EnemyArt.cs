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
/// a frame while a fight is coming, or all at once when it starts. One set is loaded at a time.
/// The battle half (EnemyArt.Battle.cs) shows it on the enemy.
/// </summary>
internal static partial class EnemyArt
{
    /// <summary>How long a fight's start waits for its art.</summary>
    private const float CollectSeconds = 5f;
    /// <summary>How long a video may take to get ready.</summary>
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

        internal bool Usable => Video == null || !Video.Failed;
    }

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
        internal string? Error;
        internal float MadeAt;
        internal long LastFrame = -1;
        // The callbacks are set as fields (the add_ methods throw); the delegates are kept alive here.
        internal VideoPlayer.EventHandler? OnPrepared;
        internal VideoPlayer.ErrorEventHandler? OnError;
    }

    /// <summary>One enemy's loaded art.</summary>
    internal sealed class ArtSet : IArtLoadHost
    {
        internal readonly CustomBattles.Battle Battle;
        internal readonly EnemyArtSpec Spec;
        internal readonly ArtLoader Loader;
        internal readonly CancellationTokenSource Cancel = new();
        internal readonly Stopwatch Clock = Stopwatch.StartNew();
        internal readonly Dictionary<string, Clip> Clips = new(StringComparer.OrdinalIgnoreCase);
        internal readonly List<Object> Made = new();
        internal Task Work = Task.CompletedTask;
        internal bool Disposed, Reported, Used;
        internal int Textures, Sprites, Videos;
        internal float WarmedAt;

        internal ArtSet(CustomBattles.Battle battle, EnemyArtSpec spec, string? cache)
        {
            Battle = battle;
            Spec = spec;
            Loader = new ArtLoader(spec, battle.Package.Files, this, cache);
        }

        internal ArtLoadResult Result => Loader.Result;
        internal bool Done => Work.IsCompleted;
        internal bool IdleReady => Clips.TryGetValue("idle", out var idle) && idle.Usable;
        internal string Title => Battle.Title;

        Task<Picture> IArtLoadHost.DecodePicture(ArtAnimationSpec a, byte[] file, CancellationToken cancel) =>
            OnMain(() => DecodePicture(a, file), cancel);

        string? IArtLoadHost.VideoUnsupported(VideoFacts facts) =>
            facts.Container == "mp4" && !MediaFoundation.Value
                ? "needs Windows Media Foundation, which this PC doesn't have (Windows N needs the Media Feature Pack), so use a VP8 WebM"
                : null;

        Task IArtLoadHost.Frames(PackedAnimation packed, bool smooth, CancellationToken cancel) =>
            OnMain(() => { Upload(packed, smooth); return true; }, cancel);

        Task IArtLoadHost.Video(VideoPlan plan, bool smooth, CancellationToken cancel) =>
            OnMain(() => { MakeVideo(plan, smooth); return true; }, cancel);

        // Unity's own PNG and JPEG decoder (the one cards use), then the pixels as RGBA, top row first.
        private Picture DecodePicture(ArtAnimationSpec a, byte[] file)
        {
            if (Disposed) throw new OperationCanceledException();
            var texture = CustomBattles.CardImages.Decode(file, CustomBattles.RuntimePrefix + "art/" + a.File, EnemyArtReader.MaxPictureSide)
                ?? throw new InvalidDataException($"{a.File} isn't a PNG or JPEG the game can read");
            try
            {
                int w = texture.width, h = texture.height;
                // Whatever format the decoder made, GetPixels32 gives RGBA, bottom row first.
                var pixels = texture.GetPixels32();
                if (pixels == null || pixels.Length != w * h) throw new InvalidDataException($"{a.File} couldn't be read back");
                var rgba = new byte[w * h * 4];
                IntPtr start = IntPtr.Add(pixels.Pointer, 4 * IntPtr.Size);   // an il2cpp array's elements start after its header
                for (int y = 0; y < h; y++) Marshal.Copy(IntPtr.Add(start, (h - 1 - y) * w * 4), rgba, y * w * 4, w * 4);
                GC.KeepAlive(pixels);
                return new Picture { Rgba = rgba, Width = w, Height = h };
            }
            finally { Object.Destroy(texture); }
        }

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
            Clips[a.Name] = NewClip(p.From, frames);
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

            var video = new VideoArt { Target = target, Copy = copy, File = a.File, MadeAt = Time.unscaledTime };
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
            Clips[a.Name] = clip;
        }

        /// <summary>Follows the videos getting ready (the callbacks, and polling in case they don't come).</summary>
        internal void WatchVideos()
        {
            foreach (var clip in Clips.Values)
            {
                var v = clip.Video;
                if (v == null || v.Failed || !v.Player) continue;
                if (v.Error != null)
                {
                    v.Failed = true;
                    ModLog.Error($"Enemy art for {Title}: video {v.File} can't play ({v.Error}); {EnemyArtReader.StandIn(clip.Name, Spec)}.");
                    continue;
                }
                if (v.Prepared) continue;
                if (v.Called || v.Player.isPrepared)
                {
                    v.Prepared = true;
                    ModLog.Info($"Enemy art: video {v.File} prepared in {(Time.unscaledTime - v.MadeAt) * 1000:0} ms ({v.Player.width} x {v.Player.height}, {ArtLoadResult.Sec(v.Player.length)} s{(v.Called ? "" : "; its callback didn't come")}).");
                }
                else if (Time.unscaledTime - v.MadeAt > PrepareSeconds)
                {
                    v.Failed = true;
                    ModLog.Error($"Enemy art for {Title}: video {v.File} wasn't ready after {PrepareSeconds:0} s; {EnemyArtReader.StandIn(clip.Name, Spec)}.");
                }
            }
        }

        /// <summary>Logs how loading went, once it's done.</summary>
        internal void Report()
        {
            if (Reported || !Done) return;
            Reported = true;
            if (Work.IsFaulted && Work.Exception?.GetBaseException() is not OperationCanceledException)
                ModLog.Error($"Enemy art for {Title}: loading failed: {Work.Exception?.GetBaseException()}");
            var problems = Battle.Package.Problems;
            foreach (var dropped in Spec.Dropped)
                ModLog.Error($"Enemy art for {Title}: {dropped.Key} can't be used ({dropped.Value}); {EnemyArtReader.StandIn(dropped.Key, Spec)}.");
            foreach (var failed in Result.Failed)
                ModLog.Error($"Enemy art for {Title}: {failed.Key} can't be used ({failed.Value}); {(failed.Key == "idle" ? $"the enemy looks like {Battle.Package.EnemyPlaceholder}" : EnemyArtReader.StandIn(failed.Key, Spec))}.");
            foreach (var note in Result.Notes.Where(n => !problems.Contains(n)).Distinct()) ModLog.Info($"Enemy art for {Title}: {note}.");
            foreach (var error in Result.Errors) ModLog.Error($"Enemy art for {Title}: {error}");
            if (IdleReady) ModLog.Info($"Enemy art for {Title}: ready in {Clock.ElapsedMilliseconds} ms: {Result.Describe()}.");
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
            ModLog.Info($"Enemy art: cleaned up {Textures} textures, {Sprites} sprites, {Videos} videos.");
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
    /// another battle's art is let go first.
    /// </summary>
    internal static void Warm(CustomBattles.Battle battle, string from)
    {
        if (!installed || battle.Package.Art == null) return;
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
        if (cacheFolder == null)
        {
            cacheFolder = Path.Combine(Application.persistentDataPath, "NocturneButBetter", "Cache", "EnemyArt");
            string folder = cacheFolder;
            Task.Run(() =>
            {
                try { ArtLoader.TrimCache(folder, CacheBytes); }
                catch (Exception) { }
            });
        }
        var set = new ArtSet(battle, battle.Package.Art!, cacheFolder) { WarmedAt = Time.unscaledTime };
        set.Work = Task.Run(() => set.Loader.Run(set.Cancel.Token));
        current = set;
        return set;
    }

    /// <summary>
    /// The battle's art for a fight that is starting: whatever loading is left is done now, for at
    /// most CollectSeconds. Animations that are still loading after that are used once they're ready.
    /// </summary>
    private static ArtSet Collect(CustomBattles.Battle battle)
    {
        var set = current != null && current.Battle == battle && !current.Disposed ? current : Start(battle);
        set.Used = true;
        var wait = Stopwatch.StartNew();
        while (!set.Done && wait.Elapsed.TotalSeconds < CollectSeconds)
            if (!Pump(int.MaxValue)) Thread.Sleep(1);
        Pump(int.MaxValue);
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
