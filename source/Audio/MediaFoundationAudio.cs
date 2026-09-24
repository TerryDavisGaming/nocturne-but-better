using System.Runtime.InteropServices;

namespace NocturneFlatScroll;

/// <summary>
/// Decodes audio with Windows Media Foundation's Source Reader: FLAC, MP3, AAC (M4A/MP4), WMA
/// and the other formats Windows has decoders for. The file is read from memory (a shell memory
/// stream wrapped as an MF byte stream), so files inside chart packs work too. The COM calls go
/// through the objects' vtables directly, with no interface declarations. The output is 16-bit
/// PCM exactly as MF's decoders produce it; <see cref="AudioFile"/> corrects the start and end.
/// Runs on any thread; it initializes COM (multithreaded) for the call if the thread hasn't.
/// </summary>
internal static class MediaFoundationAudio
{
    internal sealed class Decoded
    {
        internal StereoPcm Pcm = null!;
        internal int Rate;
        internal int Channels;
        internal long FirstTimestamp;   // 100 ns units, of the first sample MF returned
    }

    private const uint MfVersion = 0x00020070;       // MF_VERSION (Windows 7 and later)
    private const uint MfStartupLite = 0x1;          // no sockets
    private const int CoinitMultithreaded = 0x0;
    private const uint FirstAudioStream = 0xFFFFFFFD;
    private const uint AllStreams = 0xFFFFFFFE;
    private const uint MediaSource = 0xFFFFFFFF;
    private const uint ReaderError = 0x1, ReaderEndOfStream = 0x2, ReaderTypeChanged = 0x20;
    private const ushort VtUi8 = 21;

    private static readonly Guid IidAttributes = new("2cd2d921-c447-44a7-a13c-4adabfc247e3");
    private static readonly Guid ByteStreamContentType = new("fc358289-3cb6-460c-a424-b6681260375a");
    private static readonly Guid ByteStreamOriginName = new("fc358288-3cb6-460c-a424-b6681260375a");
    private static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid AudioMajor = new("73647561-0000-0010-8000-00AA00389B71");
    private static readonly Guid PcmSubtype = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid BitsPerSample = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    private static readonly Guid NumChannels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid SamplesPerSecond = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid BlockAlignment = new("322de230-9eeb-43bd-ab7a-ff412251541d");
    private static readonly Guid ChannelMask = new("55fb5765-644a-4caf-8479-938983bb1588");
    private static readonly Guid Duration = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");

    // IUnknown
    private delegate int QueryInterfaceFn(IntPtr self, ref Guid iid, out IntPtr result);
    private delegate uint ReleaseFn(IntPtr self);
    // IMFAttributes (slots 7, 21, 24, 25)
    private delegate int GetUInt32Fn(IntPtr self, ref Guid key, out uint value);
    private delegate int SetUInt32Fn(IntPtr self, ref Guid key, uint value);
    private delegate int SetGuidFn(IntPtr self, ref Guid key, ref Guid value);
    private delegate int SetStringFn(IntPtr self, ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    // IMFSourceReader (slots 4, 6, 7, 9, 12)
    private delegate int SetStreamSelectionFn(IntPtr self, uint stream, int selected);
    private delegate int GetCurrentMediaTypeFn(IntPtr self, uint stream, out IntPtr type);
    private delegate int SetCurrentMediaTypeFn(IntPtr self, uint stream, IntPtr reserved, IntPtr type);
    private delegate int ReadSampleFn(IntPtr self, uint stream, uint control, out uint actualStream, out uint flags, out long timestamp, out IntPtr sample);
    private delegate int GetPresentationAttributeFn(IntPtr self, uint stream, ref Guid key, IntPtr value);
    // IMFSample (slot 41) and IMFMediaBuffer (slots 3, 4)
    private delegate int ConvertToContiguousBufferFn(IntPtr self, out IntPtr buffer);
    private delegate int LockFn(IntPtr self, out IntPtr data, out uint maxLength, out uint currentLength);
    private delegate int UnlockFn(IntPtr self);

    [DllImport("ole32.dll", ExactSpelling = true)] private static extern int CoInitializeEx(IntPtr reserved, int coInit);
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern void CoUninitialize();
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern int PropVariantClear(IntPtr value);
    [DllImport("shlwapi.dll", ExactSpelling = true)] private static extern IntPtr SHCreateMemStream(byte[] data, uint size);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFShutdown();
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateMediaType(out IntPtr type);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateMFByteStreamOnStream(IntPtr stream, out IntPtr byteStream);
    [DllImport("mfreadwrite.dll", ExactSpelling = true)] private static extern int MFCreateSourceReaderFromByteStream(IntPtr byteStream, IntPtr attributes, out IntPtr reader);

    /// <summary>Delegates for vtable slots, made once per function pointer.</summary>
    private sealed class Vtables
    {
        private readonly Dictionary<IntPtr, Delegate> made = new();

        internal T Get<T>(IntPtr obj, int slot) where T : Delegate
        {
            IntPtr fn = Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size);
            if (made.TryGetValue(fn, out var d) && d is T typed) return typed;
            typed = Marshal.GetDelegateForFunctionPointer<T>(fn);
            made[fn] = typed;
            return typed;
        }

        internal void Release(ref IntPtr obj)
        {
            if (obj == IntPtr.Zero) return;
            Get<ReleaseFn>(obj, 2)(obj);
            obj = IntPtr.Zero;
        }
    }

    internal static Exception Missing() => new NotSupportedException(
        "MP3, FLAC, M4A and WMA songs are decoded by Windows Media Foundation, which this copy of Windows doesn't have " +
        "(Windows N editions need the Media Feature Pack); convert the song to .ogg or .wav");

    private static void Check(int hr, string step)
    {
        if (hr >= 0) return;
        string why = unchecked((uint)hr) switch
        {
            0xC00D36C4 => "Windows doesn't recognise the file as audio it can play",
            0xC00D36B3 => "it has no audio track",
            0xC00D36B4 or 0xC00D5212 => "Windows has no decoder for its audio format",
            0xC00D36E6 => "the file is damaged or not what its contents claim",
            0x8007000E => "not enough memory",
            _ => "Media Foundation failed",
        };
        throw new InvalidDataException($"{why} ({step}: 0x{hr:X8})");
    }

    /// <param name="contentType">A MIME type hint, e.g. "audio/mpeg".</param>
    /// <param name="fileName">A file name hint whose extension matches the format, e.g. "song.mp3".</param>
    internal static Decoded Decode(byte[] bytes, string contentType, string fileName)
    {
        var vt = new Vtables();
        int coInit = CoInitializeEx(IntPtr.Zero, CoinitMultithreaded);
        bool uninitialize = coInit >= 0; // S_OK or S_FALSE; RPC_E_CHANGED_MODE means the thread is already STA
        bool started = false;
        IntPtr stream = IntPtr.Zero, byteStream = IntPtr.Zero, attributes = IntPtr.Zero, reader = IntPtr.Zero,
               wanted = IntPtr.Zero, current = IntPtr.Zero, sample = IntPtr.Zero, buffer = IntPtr.Zero;
        try
        {
            int hr;
            try { hr = MFStartup(MfVersion, MfStartupLite); }
            catch (DllNotFoundException) { throw Missing(); }
            catch (EntryPointNotFoundException) { throw Missing(); }
            Check(hr, "MFStartup");
            started = true;

            stream = SHCreateMemStream(bytes, (uint)bytes.Length);
            if (stream == IntPtr.Zero) throw new OutOfMemoryException("the song couldn't be copied for Media Foundation");
            Check(MFCreateMFByteStreamOnStream(stream, out byteStream), "byte stream");
            // MF picks the file format from the content type or the name's extension.
            var iid = IidAttributes;
            if (vt.Get<QueryInterfaceFn>(byteStream, 0)(byteStream, ref iid, out attributes) >= 0)
            {
                var key = ByteStreamContentType;
                vt.Get<SetStringFn>(attributes, 25)(attributes, ref key, contentType);
                key = ByteStreamOriginName;
                vt.Get<SetStringFn>(attributes, 25)(attributes, ref key, fileName);
            }
            Check(MFCreateSourceReaderFromByteStream(byteStream, IntPtr.Zero, out reader), "open");

            // Only the first audio track, decoded to 16-bit PCM at its own rate and channel count.
            Check(vt.Get<SetStreamSelectionFn>(reader, 4)(reader, AllStreams, 0), "deselect streams");
            Check(vt.Get<SetStreamSelectionFn>(reader, 4)(reader, FirstAudioStream, 1), "select audio");
            Check(MFCreateMediaType(out wanted), "media type");
            {
                Guid k1 = MajorType, v1 = AudioMajor, k2 = Subtype, v2 = PcmSubtype, k3 = BitsPerSample;
                Check(vt.Get<SetGuidFn>(wanted, 24)(wanted, ref k1, ref v1), "media type");
                Check(vt.Get<SetGuidFn>(wanted, 24)(wanted, ref k2, ref v2), "media type");
                Check(vt.Get<SetUInt32Fn>(wanted, 21)(wanted, ref k3, 16), "media type");
            }
            Check(vt.Get<SetCurrentMediaTypeFn>(reader, 7)(reader, FirstAudioStream, IntPtr.Zero, wanted), "choose PCM output");

            var (channels, rate, align, mask) = ReadFormat(vt, reader, ref current);
            var result = new Decoded { Channels = channels, Rate = rate, FirstTimestamp = long.MinValue };
            result.Pcm = new StereoPcm(channels, SpeakerLayouts.Wave(channels, mask), StereoPcm.Plausible(ExpectedFrames(vt, reader, rate), bytes.Length));

            var scratch = Array.Empty<short>();
            int idle = 0;
            while (true)
            {
                Check(vt.Get<ReadSampleFn>(reader, 9)(reader, FirstAudioStream, 0, out _, out uint flags, out long time, out sample), "read");
                if ((flags & ReaderError) != 0) throw new InvalidDataException("Media Foundation stopped with a decoding error");
                if ((flags & ReaderTypeChanged) != 0)
                {
                    var (c2, r2, a2, m2) = ReadFormat(vt, reader, ref current);
                    if ((c2 != channels || r2 != rate) && result.Pcm.Frames > 0)
                        throw new InvalidDataException("the audio format changes part way through the song");
                    if (c2 != channels) result.Pcm = new StereoPcm(c2, SpeakerLayouts.Wave(c2, m2), StereoPcm.Plausible(ExpectedFrames(vt, reader, r2), bytes.Length));
                    channels = c2; rate = r2; align = a2;
                    result.Channels = channels; result.Rate = rate;
                }
                if (sample != IntPtr.Zero)
                {
                    idle = 0;
                    if (result.FirstTimestamp == long.MinValue) result.FirstTimestamp = time;
                    Check(vt.Get<ConvertToContiguousBufferFn>(sample, 41)(sample, out buffer), "sample buffer");
                    Check(vt.Get<LockFn>(buffer, 3)(buffer, out IntPtr data, out _, out uint length), "lock buffer");
                    try
                    {
                        int frames = (int)(length / (uint)align);
                        int shorts = frames * channels;
                        if (scratch.Length < shorts) scratch = new short[Math.Max(shorts, scratch.Length * 2)];
                        Marshal.Copy(data, scratch, 0, shorts);
                        result.Pcm.WriteInterleaved(scratch, frames);
                    }
                    finally { vt.Get<UnlockFn>(buffer, 4)(buffer); }
                    vt.Release(ref buffer);
                    vt.Release(ref sample);
                }
                else if (++idle > 100000)
                {
                    throw new InvalidDataException("Media Foundation stopped returning audio");
                }
                if ((flags & ReaderEndOfStream) != 0) break;
            }
            if (result.FirstTimestamp == long.MinValue) result.FirstTimestamp = 0;
            return result;
        }
        finally
        {
            vt.Release(ref buffer);
            vt.Release(ref sample);
            vt.Release(ref current);
            vt.Release(ref wanted);
            vt.Release(ref reader);
            vt.Release(ref attributes);
            vt.Release(ref byteStream);
            vt.Release(ref stream);
            if (started) MFShutdown();
            if (uninitialize) CoUninitialize();
        }
    }

    private static (int Channels, int Rate, int Align, uint Mask) ReadFormat(Vtables vt, IntPtr reader, ref IntPtr current)
    {
        vt.Release(ref current);
        Check(vt.Get<GetCurrentMediaTypeFn>(reader, 6)(reader, FirstAudioStream, out current), "read the output format");
        IntPtr type = current;
        uint Get(Guid key, uint fallback)
        {
            var k = key;
            return vt.Get<GetUInt32Fn>(type, 7)(type, ref k, out uint v) >= 0 ? v : fallback;
        }
        int channels = (int)Get(NumChannels, 0);
        int rate = (int)Get(SamplesPerSecond, 0);
        int bits = (int)Get(BitsPerSample, 16);
        int align = (int)Get(BlockAlignment, (uint)(channels * 2));
        uint mask = Get(ChannelMask, 0);
        if (channels <= 0 || rate <= 0 || bits != 16 || align != channels * 2)
            throw new InvalidDataException($"Media Foundation gave an unexpected format ({channels} channels, {rate} Hz, {bits}-bit)");
        return (channels, rate, align, mask);
    }

    /// <summary>The length MF reports in frames, plus a second to spare, to size the buffer (0 if it doesn't say).</summary>
    private static long ExpectedFrames(Vtables vt, IntPtr reader, int rate)
    {
        IntPtr value = Marshal.AllocHGlobal(32);
        try
        {
            for (int i = 0; i < 32; i += 8) Marshal.WriteInt64(value, i, 0);
            var key = Duration;
            if (vt.Get<GetPresentationAttributeFn>(reader, 12)(reader, MediaSource, ref key, value) < 0) return 0;
            long duration = Marshal.ReadInt16(value) == VtUi8 ? Marshal.ReadInt64(value, 8) : 0;
            PropVariantClear(value);
            return duration > 0 ? (long)Math.Ceiling(duration / 1e7 * rate) + rate : 0;
        }
        finally { Marshal.FreeHGlobal(value); }
    }
}
