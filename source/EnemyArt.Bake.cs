using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Video;
using Object = UnityEngine.Object;

namespace NocturneFlatScroll;

// The battle creator's "Turn into frames" (BattleCreator.ArtBake.cs): a video animation plays once
// into a render texture the size of the sheet's frames, and each frame the plan asks for is read
// back as the video passes it (media.md 6.6). The player is set up like the preview's (on the same
// hidden host, callbacks set as fields), but shows every frame however slow the game is. Only the
// creator uses it; never a battle.
internal static partial class EnemyArt
{
    /// <summary>A video being read into frames. Main thread only: Watch it while it gets ready, then Update it every frame.</summary>
    internal sealed class VideoFrames
    {
        /// <summary>How long it may take to get ready, and to move on once it plays (real seconds).</summary>
        private const float ReadySeconds = PrepareSeconds, StuckSeconds = 8f;

        internal readonly string File;
        internal readonly int Width, Height;
        private readonly RenderTexture? target;
        private readonly Texture2D? readable;
        private readonly VideoPlayer? player;
        private readonly byte[] bottomUp, frame;
        // The callbacks are set as fields (the add_ methods throw); the delegates are kept alive here.
        private readonly VideoPlayer.EventHandler? onPrepared;
        private readonly VideoPlayer.ErrorEventHandler? onError;
        private readonly float madeAt;
        private double[] readAt = Array.Empty<double>();
        private bool called, prepared, played, shownBefore, disposed;
        private float movedAt;
        private double lastTime = -1;
        private long lastShown = -1;
        private string? error;

        /// <summary>Frames read so far.</summary>
        internal int Read;
        /// <summary>Frames read late, as their own went by while the game couldn't keep up; and frames past the video's end (its last again).</summary>
        internal int Busy, PastEnd;
        internal string? Error => error;
        internal bool Prepared => prepared;
        internal bool Done => readAt.Length > 0 && Read >= readAt.Length;
        /// <summary>How long the player says the video plays, once it's ready (for a file that doesn't say).</summary>
        internal double Length => prepared && player ? player!.length : 0;

        /// <param name="path">A path the player can open (ArtLoader.PlayablePath).</param>
        internal VideoFrames(string path, string file, int width, int height)
        {
            File = file;
            Width = width;
            Height = height;
            bottomUp = new byte[width * height * 4];
            frame = new byte[width * height * 4];
            string name = $"{CustomBattles.RuntimePrefix}art/frames/{file}";
            try
            {
                target = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32) { name = name + "/video", hideFlags = HideFlags.HideAndDontSave };
                target.Create();
                readable = new Texture2D(width, height, TextureFormat.RGBA32, false) { name = name, hideFlags = HideFlags.HideAndDontSave };
                player = VideoHost().AddComponent<VideoPlayer>();
                player.playOnAwake = false;
                player.source = VideoSource.Url;
                player.url = path.Replace('\\', '/');
                player.renderMode = VideoRenderMode.RenderTexture;
                player.targetTexture = target;
                player.aspectRatio = VideoAspectRatio.Stretch;
                player.audioOutputMode = VideoAudioOutputMode.None;
                player.timeUpdateMode = VideoTimeUpdateMode.GameTime;
                player.isLooping = false;
                // Every frame is shown, however slow the game is, so none is passed before it's read.
                player.skipOnDrop = false;
                player.waitForFirstFrame = true;
                onPrepared = DelegateSupport.ConvertDelegate<VideoPlayer.EventHandler>(new Action<VideoPlayer>(_ => called = true));
                onError = DelegateSupport.ConvertDelegate<VideoPlayer.ErrorEventHandler>(new Action<VideoPlayer, string>((_, message) => error ??= message ?? "the video player failed"));
                player.prepareCompleted = onPrepared;
                player.errorReceived = onError;
                madeAt = movedAt = Time.realtimeSinceStartup;
                player.Prepare();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>While it gets ready: false once it's ready, or failed (Error says why).</summary>
        internal bool Watch()
        {
            if (prepared || error != null) return false;
            if (!player)
            {
                error = "its player was let go";
                return false;
            }
            if (called || player!.isPrepared)
            {
                prepared = true;
                return false;
            }
            if (Time.realtimeSinceStartup - madeAt > ReadySeconds) error = $"it wasn't ready after {ReadySeconds:0} s";
            return error == null;
        }

        /// <summary>
        /// Plays it, to read a frame at each of <paramref name="times"/> (the video's own seconds).
        /// <paramref name="gap"/>, the shortest time between two of them, sets how fast it plays:
        /// slow enough that the game reads each as it passes (two game frames at 60 a second).
        /// Reads closer together than the video's frames share them, so its frames set it then.
        /// </summary>
        internal void Start(double[] times, double gap)
        {
            readAt = times;
            double step = Math.Max(gap, 1 / Math.Max(1.0, player!.frameRate));
            player.playbackSpeed = (float)Math.Clamp(step * 30, 0.25, 1);
            player.Play();
            movedAt = Time.realtimeSinceStartup;
        }

        /// <summary>
        /// Every frame while it plays: reads the frames whose time has come (the one showing is the
        /// nearest to each) and hands each to <paramref name="onFrame"/> (RGBA, top row first; the
        /// buffer is reused). False once it's done, or failed (Error says why).
        /// </summary>
        internal bool Update(Action<int, byte[]> onFrame)
        {
            if (error != null || Done) return false;
            if (!player)
            {
                error = "its player was let go";
                return false;
            }
            float now = Time.realtimeSinceStartup;
            bool playing = player!.isPlaying;
            played |= playing;
            long shown = player.frame;
            double time = player.time;
            if (time != lastTime)
            {
                lastTime = time;
                movedAt = now;
            }
            // A frame is read once the player has shown one for a whole game frame, so its picture is in the texture.
            bool ready = shown >= 0 && shownBefore;
            shownBefore = shown >= 0;
            // More than one of the video's frames showed since the last game frame: the game fell behind.
            bool skipped = lastShown >= 0 && shown > lastShown + 1;
            lastShown = shown;
            if (ready)
            {
                // At its end the player stops; any frames the plan still wants repeat its last one.
                bool ended = played && !playing;
                var (due, late) = ended ? (readAt.Length - Read, 0) : VideoBake.Reads(readAt, Read, time, player.frameRate);
                if (due > 0)
                {
                    ReadTexture();
                    if (ended) PastEnd += due - 1;
                    for (int i = 0; i < due; i++) onFrame(Read++, frame);
                }
                // Reads whose own frame went by unread: the video got ahead of the game, so it slows down.
                if (late > 0 && skipped)
                {
                    Busy += late;
                    player.playbackSpeed = Math.Max(0.1f, player.playbackSpeed * 0.5f);
                }
            }
            if (Done) return false;
            // Stuck: the time hasn't moved for a while, on top of how long one frame shows at this speed.
            float frameShows = 1 / (Math.Max(1f, player.frameRate) * Math.Max(0.1f, player.playbackSpeed));
            if (now - movedAt > StuckSeconds + frameShows) error = shown < 0 ? "it didn't start playing" : $"it stopped moving at {ArtLoadResult.Sec(time)} s";
            return error == null;
        }

        // The render texture's picture into the frame buffer, top row first.
        private void ReadTexture()
        {
            var active = RenderTexture.active;
            try
            {
                RenderTexture.active = target;
                readable!.ReadPixels(new Rect(0, 0, Width, Height), 0, 0, false);
            }
            finally { RenderTexture.active = active; }
            // RGBA, bottom row first; an il2cpp array's elements start after its header (as ReadPicture reads them).
            var pixels = readable.GetPixels32();
            if (pixels == null || pixels.Length != Width * Height) throw new InvalidDataException("its frames couldn't be read back");
            Marshal.Copy(IntPtr.Add(pixels.Pointer, 4 * IntPtr.Size), bottomUp, 0, bottomUp.Length);
            GC.KeepAlive(pixels);
            VideoBake.Flip(bottomUp, Width, Height, frame);
        }

        internal void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try
            {
                if (player) player!.Stop();
                if (target) target!.Release();
            }
            catch (Exception ex) { ModLog.Error($"Battle creator: stopping {File} failed: {ex.Message}"); }
            if (player) Object.Destroy(player);
            if (target) Object.Destroy(target);
            if (readable) Object.Destroy(readable);
        }
    }
}
