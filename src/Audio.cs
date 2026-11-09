using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace SoundDeck
{
    /// <summary>An entry in a device combo box. Id is the WinMM device index, -1 = default device, -2 = none.</summary>
    public class DeviceItem
    {
        public string Name;
        public int Id;
        public DeviceItem(string name, int id) { Name = name; Id = id; }
        // WinMM cuts names at 31 characters; spell out the common VB-CABLE ones in full.
        public override string ToString()
        {
            if (Name.StartsWith("CABLE Input (VB-Audio Virtual C")) return "CABLE Input (VB-Audio Virtual Cable)";
            return Name;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WAVEFORMATEX
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
    struct WAVEHDR
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WAVEOUTCAPS
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
        public uint dwFormats;
        public ushort wChannels;
        public ushort wReserved1;
        public uint dwSupport;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WAVEINCAPS
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
        public uint dwFormats;
        public ushort wChannels;
        public ushort wReserved1;
    }

    static class WinMM
    {
        // Everything inside the app runs at 48 kHz, 16-bit, stereo.
        public const int SampleRate = 48000;
        public const int Channels = 2;

        public const uint CALLBACK_EVENT = 0x50000;
        public const uint WAVE_MAPPED = 0x4;
        public const int WHDR_DONE = 1;

        public static readonly int HdrSize = Marshal.SizeOf(typeof(WAVEHDR));
        static readonly int FlagsOffset = (int)Marshal.OffsetOf(typeof(WAVEHDR), "dwFlags");
        static readonly int RecordedOffset = (int)Marshal.OffsetOf(typeof(WAVEHDR), "dwBytesRecorded");

        [DllImport("winmm.dll")] public static extern int waveOutGetNumDevs();
        [DllImport("winmm.dll", EntryPoint = "waveOutGetDevCapsW", CharSet = CharSet.Unicode)]
        public static extern int waveOutGetDevCaps(IntPtr id, out WAVEOUTCAPS caps, int size);
        [DllImport("winmm.dll")] public static extern int waveOutOpen(out IntPtr hwo, IntPtr id, ref WAVEFORMATEX fmt, IntPtr callback, IntPtr inst, uint flags);
        [DllImport("winmm.dll")] public static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] public static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] public static extern int waveOutWrite(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] public static extern int waveOutReset(IntPtr hwo);
        [DllImport("winmm.dll")] public static extern int waveOutClose(IntPtr hwo);

        [DllImport("winmm.dll")] public static extern int waveInGetNumDevs();
        [DllImport("winmm.dll", EntryPoint = "waveInGetDevCapsW", CharSet = CharSet.Unicode)]
        public static extern int waveInGetDevCaps(IntPtr id, out WAVEINCAPS caps, int size);
        [DllImport("winmm.dll")] public static extern int waveInOpen(out IntPtr hwi, IntPtr id, ref WAVEFORMATEX fmt, IntPtr callback, IntPtr inst, uint flags);
        [DllImport("winmm.dll")] public static extern int waveInPrepareHeader(IntPtr hwi, IntPtr hdr, int size);
        [DllImport("winmm.dll")] public static extern int waveInUnprepareHeader(IntPtr hwi, IntPtr hdr, int size);
        [DllImport("winmm.dll")] public static extern int waveInAddBuffer(IntPtr hwi, IntPtr hdr, int size);
        [DllImport("winmm.dll")] public static extern int waveInStart(IntPtr hwi);
        [DllImport("winmm.dll")] public static extern int waveInReset(IntPtr hwi);
        [DllImport("winmm.dll")] public static extern int waveInClose(IntPtr hwi);

        public static WAVEFORMATEX Format()
        {
            WAVEFORMATEX f = new WAVEFORMATEX();
            f.wFormatTag = 1; // PCM
            f.nChannels = Channels;
            f.nSamplesPerSec = SampleRate;
            f.wBitsPerSample = 16;
            f.nBlockAlign = (ushort)(Channels * 2);
            f.nAvgBytesPerSec = (uint)(SampleRate * Channels * 2);
            return f;
        }

        public static List<string> OutputNames()
        {
            List<string> names = new List<string>();
            int n = waveOutGetNumDevs();
            for (int i = 0; i < n; i++)
            {
                WAVEOUTCAPS c;
                if (waveOutGetDevCaps(new IntPtr(i), out c, Marshal.SizeOf(typeof(WAVEOUTCAPS))) == 0) names.Add(c.szPname);
                else names.Add("Output " + i);
            }
            return names;
        }

        public static List<string> InputNames()
        {
            List<string> names = new List<string>();
            int n = waveInGetNumDevs();
            for (int i = 0; i < n; i++)
            {
                WAVEINCAPS c;
                if (waveInGetDevCaps(new IntPtr(i), out c, Marshal.SizeOf(typeof(WAVEINCAPS))) == 0) names.Add(c.szPname);
                else names.Add("Input " + i);
            }
            return names;
        }

        public static IntPtr AllocHeader(int bytes, out IntPtr data)
        {
            data = Marshal.AllocHGlobal(bytes);
            IntPtr h = Marshal.AllocHGlobal(HdrSize);
            WAVEHDR w = new WAVEHDR();
            w.lpData = data;
            w.dwBufferLength = (uint)bytes;
            Marshal.StructureToPtr(w, h, false);
            return h;
        }

        public static bool IsDone(IntPtr hdr) { return (Marshal.ReadInt32(hdr, FlagsOffset) & WHDR_DONE) != 0; }
        public static int BytesRecorded(IntPtr hdr) { return Marshal.ReadInt32(hdr, RecordedOffset); }
    }

    class Voice
    {
        public short[] Data;
        public int Start, End;    // the part of Data to play (in samples), for trimmed sounds
        public int Pos;
        public object Tag;
        public float Gain = 1f;   // per-sound volume
        public bool Loop;
    }

    /// <summary>A continuously running output stream that mixes playing sounds (and optionally a live microphone).</summary>
    class OutputDevice : IDisposable
    {
        const int Frames = 960;   // 20 ms per buffer
        const int BufCount = 5;   // ~100 ms queued: enough headroom that a busy CPU doesn't cause crackles

        // Soft limiter: instead of hard-clipping when sounds + voice add up past full scale,
        // turn the gain down smoothly (instant attack, ~150 ms release).
        const float LimitAt = 30000f;
        static readonly float Release = (float)Math.Exp(-1.0 / (0.15 * WinMM.SampleRate));
        float limGain = 1f;

        readonly IntPtr hwo;
        readonly IntPtr[] hdrs = new IntPtr[BufCount];
        readonly IntPtr[] datas = new IntPtr[BufCount];
        readonly AutoResetEvent evt = new AutoResetEvent(false);
        readonly List<Voice> voices = new List<Voice>();
        readonly Thread thread;
        volatile bool running;

        public volatile float Volume = 1f;
        public volatile float MicVolume = 1f;
        public volatile MicCapture Mic;

        public OutputDevice(int deviceId)
        {
            WAVEFORMATEX f = WinMM.Format();
            uint flags = WinMM.CALLBACK_EVENT | (deviceId >= 0 ? WinMM.WAVE_MAPPED : 0);
            int r = WinMM.waveOutOpen(out hwo, new IntPtr(deviceId), ref f, evt.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, flags);
            if (r != 0) throw new Exception("could not open output device (WinMM error " + r + ")");

            int bytes = Frames * WinMM.Channels * 2;
            for (int i = 0; i < BufCount; i++)
            {
                hdrs[i] = WinMM.AllocHeader(bytes, out datas[i]);
                WinMM.waveOutPrepareHeader(hwo, hdrs[i], WinMM.HdrSize);
            }

            running = true;
            thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Priority = ThreadPriority.AboveNormal;
            thread.Name = "SoundDeck output";
            thread.Start();
        }

        public void Play(short[] data, object tag, float gain)
        {
            Play(data, tag, gain, 0, data.Length, false);
        }

        /// <summary>Plays samples [start, end) of data; with loop, repeats until stopped.</summary>
        public void Play(short[] data, object tag, float gain, int start, int end, bool loop)
        {
            start = Math.Max(0, Math.Min(start, data.Length)) & ~1; // whole stereo frames
            end = Math.Max(start, Math.Min(end, data.Length)) & ~1;
            Voice v = new Voice();
            v.Data = data;
            v.Start = v.Pos = start;
            v.End = end;
            v.Tag = tag;
            v.Gain = gain;
            v.Loop = loop && end > start;
            lock (voices) voices.Add(v);
        }

        public void StopAll()
        {
            lock (voices) voices.Clear();
        }

        public void Stop(object tag)
        {
            lock (voices) voices.RemoveAll(v => v.Tag == tag);
        }

        /// <summary>Tag of each playing sound → how far through it is (0..1). The furthest-along copy wins.</summary>
        public Dictionary<object, float> Progress()
        {
            Dictionary<object, float> r = new Dictionary<object, float>();
            lock (voices)
            {
                foreach (Voice v in voices)
                {
                    float p = v.End <= v.Start ? 1f : (float)(v.Pos - v.Start) / (v.End - v.Start);
                    float old;
                    if (!r.TryGetValue(v.Tag, out old) || p > old) r[v.Tag] = p;
                }
            }
            return r;
        }

        void Run()
        {
            int n = Frames * WinMM.Channels;
            short[] pcm = new short[n];
            float[] mix = new float[n];
            short[] mic = new short[n];

            for (int i = 0; i < BufCount; i++)
            {
                Fill(pcm, mix, mic);
                Marshal.Copy(pcm, 0, datas[i], n);
                WinMM.waveOutWrite(hwo, hdrs[i], WinMM.HdrSize);
            }

            int next = 0;
            while (running)
            {
                evt.WaitOne(50);
                while (running && WinMM.IsDone(hdrs[next]))
                {
                    Fill(pcm, mix, mic);
                    Marshal.Copy(pcm, 0, datas[next], n);
                    WinMM.waveOutWrite(hwo, hdrs[next], WinMM.HdrSize);
                    next = (next + 1) % BufCount;
                }
            }
        }

        void Fill(short[] pcm, float[] mix, short[] mic)
        {
            int n = pcm.Length;
            Array.Clear(mix, 0, n);

            float vol = Volume;
            lock (voices)
            {
                for (int v = voices.Count - 1; v >= 0; v--)
                {
                    Voice vc = voices[v];
                    short[] d = vc.Data;
                    float g = vol * vc.Gain;
                    int done = 0;
                    while (done < n)
                    {
                        int cnt = Math.Min(n - done, vc.End - vc.Pos);
                        int p = vc.Pos;
                        for (int k = 0; k < cnt; k++) mix[done + k] += d[p + k] * g;
                        vc.Pos += cnt;
                        done += cnt;
                        if (vc.Pos < vc.End) continue;
                        if (vc.Loop) vc.Pos = vc.Start; // wrap around and keep filling this buffer
                        else break;
                    }
                    if (vc.Pos >= vc.End && !vc.Loop) voices.RemoveAt(v);
                }
            }

            MicCapture m = Mic;
            if (m != null && m.Read(mic, n))
            {
                float mv = MicVolume;
                for (int k = 0; k < n; k++) mix[k] += mic[k] * mv;
            }

            for (int k = 0; k < n; k += 2)
            {
                float peak = Math.Max(Math.Abs(mix[k]), Math.Abs(mix[k + 1]));
                float target = peak > LimitAt ? LimitAt / peak : 1f;
                limGain = target < limGain ? target : target + (limGain - target) * Release;
                pcm[k] = ToShort(mix[k] * limGain);
                pcm[k + 1] = ToShort(mix[k + 1] * limGain);
            }
        }

        static short ToShort(float s)
        {
            if (s > 32767f) return 32767;
            if (s < -32768f) return -32768;
            return (short)s;
        }

        public void Dispose()
        {
            running = false;
            evt.Set();
            thread.Join(1000);
            WinMM.waveOutReset(hwo);
            for (int i = 0; i < BufCount; i++)
            {
                WinMM.waveOutUnprepareHeader(hwo, hdrs[i], WinMM.HdrSize);
                Marshal.FreeHGlobal(hdrs[i]);
                Marshal.FreeHGlobal(datas[i]);
            }
            WinMM.waveOutClose(hwo);
        }
    }

    /// <summary>Captures a microphone into a small ring buffer that an OutputDevice reads from.</summary>
    class MicCapture : IDisposable
    {
        const int Frames = 480;   // 10 ms per buffer
        const int BufCount = 8;
        const int MaxBuffered = WinMM.SampleRate * WinMM.Channels / 8;         // 125 ms: beyond this, drop audio to keep latency low
        const int TrimTo = WinMM.SampleRate * WinMM.Channels / 25;            // 40 ms
        const int Margin = WinMM.SampleRate * WinMM.Channels / 50;            // 20 ms jitter cushion after an underrun

        readonly IntPtr hwi;
        readonly IntPtr[] hdrs = new IntPtr[BufCount];
        readonly IntPtr[] datas = new IntPtr[BufCount];
        readonly AutoResetEvent evt = new AutoResetEvent(false);
        readonly Thread thread;
        volatile bool running;

        readonly short[] ring = new short[WinMM.SampleRate * WinMM.Channels];
        int readPos, count;
        bool priming = true;

        /// <summary>Optional tap that receives every captured buffer (48 kHz stereo), e.g. for recording.</summary>
        public volatile Action<short[], int> OnData;

        /// <summary>Optional background-noise removal, applied before the voice is mixed with sounds.</summary>
        public volatile NoiseSuppressor Suppressor;

        public MicCapture(int deviceId)
        {
            WAVEFORMATEX f = WinMM.Format();
            uint flags = WinMM.CALLBACK_EVENT | (deviceId >= 0 ? WinMM.WAVE_MAPPED : 0);
            int r = WinMM.waveInOpen(out hwi, new IntPtr(deviceId), ref f, evt.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, flags);
            if (r != 0) throw new Exception("could not open microphone (WinMM error " + r + ")");

            int bytes = Frames * WinMM.Channels * 2;
            for (int i = 0; i < BufCount; i++)
            {
                hdrs[i] = WinMM.AllocHeader(bytes, out datas[i]);
                WinMM.waveInPrepareHeader(hwi, hdrs[i], WinMM.HdrSize);
                WinMM.waveInAddBuffer(hwi, hdrs[i], WinMM.HdrSize);
            }

            running = true;
            thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Priority = ThreadPriority.AboveNormal;
            thread.Name = "SoundDeck mic";
            thread.Start();
            WinMM.waveInStart(hwi);
        }

        void Run()
        {
            short[] tmp = new short[Frames * WinMM.Channels];
            int next = 0;
            while (running)
            {
                evt.WaitOne(50);
                while (running && WinMM.IsDone(hdrs[next]))
                {
                    int samples = Math.Min(tmp.Length, WinMM.BytesRecorded(hdrs[next]) / 2);
                    Marshal.Copy(datas[next], tmp, 0, samples);
                    NoiseSuppressor ns = Suppressor;
                    if (ns != null) ns.Process(tmp, samples);
                    Write(tmp, samples);
                    Action<short[], int> tap = OnData;
                    if (tap != null) tap(tmp, samples);
                    WinMM.waveInAddBuffer(hwi, hdrs[next], WinMM.HdrSize);
                    next = (next + 1) % BufCount;
                }
            }
        }

        void Write(short[] src, int n)
        {
            lock (ring)
            {
                int len = ring.Length;
                for (int k = 0; k < n; k++) ring[(readPos + count + k) % len] = src[k];
                count += n;
                if (count > MaxBuffered)
                {
                    int drop = count - TrimTo;
                    readPos = (readPos + drop) % len;
                    count -= drop;
                }
            }
        }

        /// <summary>Fills dst with n samples if enough are buffered; otherwise returns false and consumes nothing.
        /// After running dry it waits for an extra cushion so one late mic buffer doesn't cause a run of dropouts.</summary>
        public bool Read(short[] dst, int n)
        {
            lock (ring)
            {
                if (count < n || (priming && count < n + Margin)) { priming = true; return false; }
                priming = false;
                int len = ring.Length;
                for (int k = 0; k < n; k++) dst[k] = ring[(readPos + k) % len];
                readPos = (readPos + n) % len;
                count -= n;
                return true;
            }
        }

        public void Dispose()
        {
            running = false;
            evt.Set();
            thread.Join(1000);
            WinMM.waveInReset(hwi);
            for (int i = 0; i < BufCount; i++)
            {
                WinMM.waveInUnprepareHeader(hwi, hdrs[i], WinMM.HdrSize);
                Marshal.FreeHGlobal(hdrs[i]);
                Marshal.FreeHGlobal(datas[i]);
            }
            WinMM.waveInClose(hwi);
        }
    }
}
