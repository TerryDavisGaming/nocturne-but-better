using System.Runtime.InteropServices;

namespace NocturneFlatScroll;

/// <summary>
/// The chart editor's own music player. The game's Wwise music can't seek or slow down, so the
/// editor decodes the song itself and plays it through the Windows waveOut API from a background
/// thread: play, pause, seek, playback speed (by resampling, so slower also sounds lower), and a
/// tick mixed in at note times. Unity's own audio is switched off in this game.
/// </summary>
internal sealed class EditorAudio : IDisposable
{
    private const int BufferCount = 4;
    private const int BufferFrames = 1024;
    private const uint WaveMapper = 0xFFFFFFFF;
    private const int CallbackEvent = 0x50000;
    private const uint WhdrDone = 0x1;

    private readonly object gate = new();
    private readonly short[] pcm;          // interleaved stereo, 16-bit
    private readonly int frames;
    private readonly int rate;
    private readonly AutoResetEvent bufferDone = new(false);
    private readonly IntPtr[] headers = new IntPtr[BufferCount];
    private readonly IntPtr[] data = new IntPtr[BufferCount];
    private readonly bool[] queued = new bool[BufferCount];
    private readonly Thread thread;
    private IntPtr device;
    private volatile bool running = true;

    // Guarded by gate.
    private bool playing;
    private double speed = 1;
    private double sourceFrame;            // next source frame the fill thread will read
    private double clockStartSeconds;      // source time when the device was last reset
    private double clockSpeed = 1;
    private double[] ticks = Array.Empty<double>();   // note ticks (2 kHz)
    private double[] beats = Array.Empty<double>();   // metronome beats (1 kHz)
    private int nextTick, nextBeat;
    private float tickVolume = 0.5f;
    private float musicVolume = 1f;

    internal double Length => frames / (double)rate;
    /// <summary>Why playback stopped on its own, if the device failed.</summary>
    internal volatile string? Failed;
    internal bool Playing { get { lock (gate) return playing; } }
    internal double Speed { get { lock (gate) return speed; } }

    /// <param name="stereo">Interleaved stereo 16-bit samples at <paramref name="sampleRate"/>.</param>
    internal EditorAudio(short[] stereo, int sampleRate)
    {
        pcm = stereo;
        frames = stereo.Length / 2;
        rate = sampleRate;
        var format = new WaveFormatEx
        {
            wFormatTag = 1, nChannels = 2, nSamplesPerSec = (uint)rate, wBitsPerSample = 16,
            nBlockAlign = 4, nAvgBytesPerSec = (uint)rate * 4
        };
        int result = waveOutOpen(out device, WaveMapper, ref format, bufferDone.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CallbackEvent);
        if (result != 0) throw new InvalidOperationException($"the sound device couldn't be opened (waveOut error {result})");
        for (int i = 0; i < BufferCount; i++)
        {
            data[i] = Marshal.AllocHGlobal(BufferFrames * 4);
            headers[i] = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHdr>());
            var header = new WaveHdr { lpData = data[i], dwBufferLength = BufferFrames * 4 };
            Marshal.StructureToPtr(header, headers[i], false);
            waveOutPrepareHeader(device, headers[i], Marshal.SizeOf<WaveHdr>());
        }
        thread = new Thread(Run) { IsBackground = true, Name = "NocturneButBetter editor audio" };
        thread.Start();
    }

    /// <summary>The source time being heard now, in seconds.</summary>
    internal double Time
    {
        get
        {
            lock (gate)
            {
                if (!playing) return Math.Min(Length, sourceFrame / rate);
                var mm = new MmTime { wType = 2 }; // TIME_SAMPLES
                waveOutGetPosition(device, ref mm, Marshal.SizeOf<MmTime>());
                return Math.Min(Length, clockStartSeconds + mm.value / (double)rate * clockSpeed);
            }
        }
    }

    internal void Play()
    {
        lock (gate)
        {
            if (playing) return;
            if (sourceFrame >= frames - 1) sourceFrame = 0;
            ResetDevice(sourceFrame / rate);
            playing = true;
        }
        bufferDone.Set();
    }

    internal void Pause()
    {
        lock (gate)
        {
            if (!playing) return;
            double now = CurrentTimeLocked();
            playing = false;
            waveOutReset(device);
            for (int i = 0; i < BufferCount; i++) queued[i] = false;
            sourceFrame = now * rate;
        }
    }

    internal void Seek(double seconds)
    {
        lock (gate)
        {
            sourceFrame = Math.Clamp(seconds, 0, Length) * rate;
            if (playing) ResetDevice(sourceFrame / rate);
        }
        bufferDone.Set();
    }

    internal void SetSpeed(double value)
    {
        lock (gate)
        {
            double now = CurrentTimeLocked();
            speed = value;
            sourceFrame = now * rate;
            if (playing) ResetDevice(now);
        }
        bufferDone.Set();
    }

    /// <summary>
    /// Times (seconds, sorted) for the note ticks, like osu!'s hitsounds in the editor, and for the
    /// metronome's lower click.
    /// </summary>
    internal void SetTicks(double[] noteTimes, double[] beatTimes, float volume)
    {
        lock (gate)
        {
            ticks = noteTimes;
            beats = beatTimes;
            tickVolume = volume;
            nextTick = FirstAfter(ticks, sourceFrame / rate);
            nextBeat = FirstAfter(beats, sourceFrame / rate);
        }
    }

    internal void SetMusicVolume(float volume) { lock (gate) musicVolume = volume; }

    private double CurrentTimeLocked()
    {
        if (!playing) return sourceFrame / rate;
        var mm = new MmTime { wType = 2 };
        waveOutGetPosition(device, ref mm, Marshal.SizeOf<MmTime>());
        return Math.Min(Length, clockStartSeconds + mm.value / (double)rate * clockSpeed);
    }

    private void ResetDevice(double seconds)
    {
        waveOutReset(device);
        for (int i = 0; i < BufferCount; i++) queued[i] = false;
        clockStartSeconds = seconds;
        clockSpeed = speed;
        nextTick = FirstAfter(ticks, seconds);
        nextBeat = FirstAfter(beats, seconds);
        activeTicks.Clear();
    }

    private static int FirstAfter(double[] times, double seconds)
    {
        int index = Array.BinarySearch(times, seconds);
        return index >= 0 ? index : ~index;
    }

    private void Run()
    {
        var buffer = new short[BufferFrames * 2];
        while (running)
        {
            bufferDone.WaitOne(20);
            lock (gate)
            {
                if (!running) break;
                for (int i = 0; i < BufferCount; i++)
                {
                    if (queued[i])
                    {
                        uint flags = (uint)Marshal.ReadInt32(headers[i], FlagsOffset);
                        if ((flags & WhdrDone) == 0) continue;
                        queued[i] = false;
                    }
                    if (!playing) continue;
                    Fill(buffer);
                    Marshal.Copy(buffer, 0, data[i], buffer.Length);
                    int result = waveOutWrite(device, headers[i], Marshal.SizeOf<WaveHdr>());
                    if (result != 0)
                    {
                        // The device went away (unplugged, say): stop rather than wait forever.
                        playing = false;
                        Failed = $"the sound device stopped (waveOut error {result})";
                        waveOutReset(device);
                        for (int j = 0; j < BufferCount; j++) queued[j] = false;
                        break;
                    }
                    queued[i] = true;
                }
            }
        }
    }

    // Offset of WAVEHDR.dwFlags: lpData (pointer), dwBufferLength, dwBytesRecorded, dwUser (pointer).
    private static readonly int FlagsOffset = (int)Marshal.OffsetOf<WaveHdr>(nameof(WaveHdr.dwFlags));

    /// <summary>Resamples the next stretch of music at the current speed and mixes in the ticks.</summary>
    private void Fill(short[] buffer)
    {
        double step = speed;
        for (int f = 0; f < BufferFrames; f++)
        {
            double pos = sourceFrame + f * step;
            int i0 = (int)pos;
            float l = 0, r = 0;
            if (i0 >= 0 && i0 < frames - 1)
            {
                float frac = (float)(pos - i0);
                l = pcm[i0 * 2] + (pcm[i0 * 2 + 2] - pcm[i0 * 2]) * frac;
                r = pcm[i0 * 2 + 1] + (pcm[i0 * 2 + 3] - pcm[i0 * 2 + 1]) * frac;
            }
            buffer[f * 2] = (short)(l * musicVolume);
            buffer[f * 2 + 1] = (short)(r * musicVolume);
        }
        // Ticks that started in an earlier buffer ring on.
        for (int t = activeTicks.Count - 1; t >= 0; t--)
        {
            var (k0, freq) = activeTicks[t];
            int k = k0;
            for (int f = 0; f < BufferFrames && k < TickFrames; f++, k++) AddTickSample(buffer, f, k, freq);
            if (k >= TickFrames) activeTicks.RemoveAt(t);
            else activeTicks[t] = (k, freq);
        }
        double startSeconds = sourceFrame / rate;
        double endSeconds = (sourceFrame + BufferFrames * step) / rate;
        while (nextTick < ticks.Length && ticks[nextTick] < endSeconds)
        {
            double t = ticks[nextTick++];
            if (t >= startSeconds) MixTick(buffer, (int)((t - startSeconds) * rate / step), 2000);
        }
        while (nextBeat < beats.Length && beats[nextBeat] < endSeconds)
        {
            double t = beats[nextBeat++];
            if (t >= startSeconds) MixTick(buffer, (int)((t - startSeconds) * rate / step), 1000);
        }
        sourceFrame += BufferFrames * step;
        if (sourceFrame >= frames) playing = false;
    }

    // A short click (decaying 2 kHz tone); one that starts near a buffer's end carries on in the next.
    private const int TickFrames = 1200;
    private readonly List<(int Progress, int Freq)> activeTicks = new();

    private void MixTick(short[] buffer, int at, int freq)
    {
        for (int f = at; f < BufferFrames && f - at < TickFrames; f++) AddTickSample(buffer, f, f - at, freq);
        if (at + TickFrames > BufferFrames) activeTicks.Add((BufferFrames - at, freq));
    }

    private void AddTickSample(short[] buffer, int frame, int k, int freq)
    {
        float env = (float)Math.Exp(-k / 120.0);
        float s = (float)Math.Sin(2 * Math.PI * freq * k / rate) * env * 12000f * tickVolume;
        buffer[frame * 2] = (short)Math.Clamp(buffer[frame * 2] + s, short.MinValue, short.MaxValue);
        buffer[frame * 2 + 1] = (short)Math.Clamp(buffer[frame * 2 + 1] + s, short.MinValue, short.MaxValue);
    }

    public void Dispose()
    {
        running = false;
        bufferDone.Set();
        thread.Join(); // it waits at most 20 ms at a time
        lock (gate)
        {
            if (device != IntPtr.Zero)
            {
                waveOutReset(device);
                for (int i = 0; i < BufferCount; i++)
                {
                    waveOutUnprepareHeader(device, headers[i], Marshal.SizeOf<WaveHdr>());
                    Marshal.FreeHGlobal(headers[i]);
                    Marshal.FreeHGlobal(data[i]);
                }
                waveOutClose(device);
                device = IntPtr.Zero;
            }
        }
        bufferDone.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHdr
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    // MMTIME is 12 bytes: the union includes an 8-byte SMPTE member. A smaller size is refused.
    [StructLayout(LayoutKind.Explicit, Size = 12)]
    private struct MmTime
    {
        [FieldOffset(0)] public uint wType;
        [FieldOffset(4)] public uint value;
    }

    [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr hwo, uint device, ref WaveFormatEx format, IntPtr callback, IntPtr instance, int flags);
    [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr hwo, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr hwo);
    [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr hwo);
    [DllImport("winmm.dll")] private static extern int waveOutGetPosition(IntPtr hwo, ref MmTime time, int size);
}
