using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace SoundDeck
{
    /// <summary>Somewhere captured audio goes (interleaved floats in the source's own format).</summary>
    interface IAudioSink
    {
        void SetFormat(int channels, int sampleRate);
        void Append(float[] src, int n);
    }

    /// <summary>
    /// Instant replay: always keeps the last <see cref="Seconds"/> seconds of the PC's audio in memory,
    /// so a funny moment can be saved after it happened. Nothing is written to disk until the user saves.
    /// </summary>
    class ReplayBuffer : IAudioSink, IDisposable
    {
        public const int Seconds = 30;

        readonly object sync = new object();
        float[] ring = new float[WinMM.SampleRate * 2 * Seconds];
        int write, filled, channels = 2, rate = WinMM.SampleRate;
        readonly LoopbackCapture capture;

        public ReplayBuffer() { capture = new LoopbackCapture(this); }

        public void SetFormat(int ch, int sampleRate)
        {
            lock (sync)
            {
                channels = ch;
                rate = sampleRate;
                ring = new float[ch * sampleRate * Seconds];
                write = filled = 0;
            }
        }

        public void Append(float[] src, int n)
        {
            lock (sync)
            {
                for (int i = 0; i < n; i++)
                {
                    ring[write] = src[i];
                    write = (write + 1) % ring.Length;
                }
                filled = Math.Min(ring.Length, filled + n);
            }
        }

        /// <summary>The buffered audio, oldest first, as 48 kHz stereo.</summary>
        public short[] Snapshot()
        {
            float[] linear;
            int ch, r;
            lock (sync)
            {
                linear = new float[filled];
                int start = (write - filled + ring.Length) % ring.Length;
                for (int i = 0; i < filled; i++) linear[i] = ring[(start + i) % ring.Length];
                ch = channels;
                r = rate;
            }
            return AudioDecoder.ToStereo48k(linear, linear.Length, ch, r);
        }

        public void Dispose() { capture.Dispose(); }
    }

    /// <summary>Growing buffer of captured audio (interleaved floats in the source's own format) plus peaks for drawing.</summary>
    class Recording : IAudioSink
    {
        public const int MaxSeconds = 300;

        readonly object sync = new object();
        float[] buf = new float[WinMM.SampleRate * 2 * 10];
        int count;
        int channels = 2, rate = WinMM.SampleRate;
        readonly List<float> peaks = new List<float>();   // one value per 20 ms, 0..1
        float curPeak;
        int curCount, peakEvery = WinMM.SampleRate * 2 / 50;

        public volatile float Level;   // most recent peak, for the level meter

        public void SetFormat(int ch, int sampleRate)
        {
            lock (sync)
            {
                channels = ch;
                rate = sampleRate;
                peakEvery = Math.Max(1, sampleRate * ch / 50);
            }
        }

        public double Seconds { get { lock (sync) return (double)count / channels / rate; } }
        public bool Full { get { return Seconds >= MaxSeconds; } }

        public void Append(float[] src, int n)
        {
            lock (sync)
            {
                if ((double)count / channels / rate >= MaxSeconds) return;
                if (count + n > buf.Length) Array.Resize(ref buf, Math.Max(buf.Length * 2, count + n));
                Array.Copy(src, 0, buf, count, n);
                count += n;
                for (int i = 0; i < n; i++)
                {
                    float a = Math.Abs(src[i]);
                    if (a > curPeak) curPeak = a;
                    if (++curCount >= peakEvery)
                    {
                        peaks.Add(Math.Min(1f, curPeak));
                        Level = curPeak;
                        curPeak = 0;
                        curCount = 0;
                    }
                }
            }
        }

        public void AppendShorts(short[] src, int n)
        {
            float[] f = new float[n];
            for (int i = 0; i < n; i++) f[i] = src[i] / 32768f;
            Append(f, n);
        }

        public List<float> Peaks()
        {
            lock (sync) return new List<float>(peaks);
        }

        public short[] ToStereo48k()
        {
            lock (sync) return AudioDecoder.ToStereo48k(buf, count, channels, rate);
        }

        /// <summary>Writes 48 kHz stereo 16-bit samples as a .wav file.</summary>
        public static void WriteWav(string path, short[] samples, int offset, int length)
        {
            using (BinaryWriter w = new BinaryWriter(File.Create(path)))
            {
                int bytes = length * 2;
                w.Write(new[] { 'R', 'I', 'F', 'F' }); w.Write(36 + bytes);
                w.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' }); w.Write(16);
                w.Write((short)1); w.Write((short)2); w.Write(WinMM.SampleRate); w.Write(WinMM.SampleRate * 4);
                w.Write((short)4); w.Write((short)16);
                w.Write(new[] { 'd', 'a', 't', 'a' }); w.Write(bytes);
                byte[] raw = new byte[bytes];
                Buffer.BlockCopy(samples, offset * 2, raw, 0, bytes);
                w.Write(raw);
            }
        }
    }

    // ---- WASAPI interop (only what loopback capture needs). Unused vtable slots are placeholders.

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints();
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate([In] ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out int frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out int padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService([In] ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out int frames, out int flags, out long devicePosition, out long qpcPosition);
        [PreserveSig] int ReleaseBuffer(int frames);
        [PreserveSig] int GetNextPacketSize(out int frames);
    }

    /// <summary>
    /// Records whatever is playing on the default speakers ("what you hear") using WASAPI loopback.
    /// All COM work happens on its own MTA thread.
    /// </summary>
    class LoopbackCapture : IDisposable
    {
        const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x20000;
        const int AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
        static readonly Guid FloatSubtype = new Guid("00000003-0000-0010-8000-00AA00389B71");

        readonly IAudioSink target;
        readonly Thread thread;
        readonly ManualResetEvent ready = new ManualResetEvent(false);
        volatile bool running = true;
        Exception startError;

        public LoopbackCapture(IAudioSink target)
        {
            this.target = target;
            thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Name = "SoundDeck loopback";
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            ready.WaitOne(5000);
            if (startError != null) throw startError;
        }

        void Run()
        {
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            IAudioClient client = null;
            IAudioCaptureClient capture = null;
            IntPtr fmt = IntPtr.Zero;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                Check(enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 0 /* eConsole */, out device), "no playback device");
                Guid iidClient = typeof(IAudioClient).GUID;
                object o;
                Check(device.Activate(ref iidClient, 23 /* CLSCTX_ALL */, IntPtr.Zero, out o), "couldn't open the playback device");
                client = (IAudioClient)o;
                Check(client.GetMixFormat(out fmt), "couldn't read the audio format");

                int tag = (ushort)Marshal.ReadInt16(fmt, 0);
                int ch = Marshal.ReadInt16(fmt, 2);
                int rate = Marshal.ReadInt32(fmt, 4);
                int bits = Marshal.ReadInt16(fmt, 14);
                bool isFloat = tag == 3;
                if (tag == 0xFFFE)
                {
                    byte[] g = new byte[16];
                    Marshal.Copy(new IntPtr(fmt.ToInt64() + 24), g, 0, 16);
                    isFloat = new Guid(g) == FloatSubtype;
                }
                if (!isFloat && bits != 16) throw new Exception("unsupported speaker format (" + bits + "-bit)");
                target.SetFormat(ch, rate);

                Check(client.Initialize(0 /* shared */, AUDCLNT_STREAMFLAGS_LOOPBACK, 2000000 /* 200 ms */, 0, fmt, IntPtr.Zero), "couldn't start capturing");
                Guid iidCapture = typeof(IAudioCaptureClient).GUID;
                Check(client.GetService(ref iidCapture, out o), "couldn't start capturing");
                capture = (IAudioCaptureClient)o;
                Check(client.Start(), "couldn't start capturing");
                ready.Set();

                float[] f = new float[0];
                short[] s = new short[0];
                while (running)
                {
                    Thread.Sleep(10);
                    int packet;
                    if (capture.GetNextPacketSize(out packet) != 0) break;
                    while (packet > 0 && running)
                    {
                        IntPtr data;
                        int frames, flags;
                        long pos, qpc;
                        if (capture.GetBuffer(out data, out frames, out flags, out pos, out qpc) != 0) break;
                        int n = frames * ch;
                        if (f.Length < n) f = new float[n];
                        if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0) Array.Clear(f, 0, n);
                        else if (isFloat) Marshal.Copy(data, f, 0, n);
                        else
                        {
                            if (s.Length < n) s = new short[n];
                            Marshal.Copy(data, s, 0, n);
                            for (int i = 0; i < n; i++) f[i] = s[i] / 32768f;
                        }
                        capture.ReleaseBuffer(frames);
                        target.Append(f, n);
                        if (capture.GetNextPacketSize(out packet) != 0) break;
                    }
                }
                client.Stop();
            }
            catch (Exception ex)
            {
                startError = ex;
                ready.Set();
            }
            finally
            {
                if (fmt != IntPtr.Zero) Marshal.FreeCoTaskMem(fmt);
                foreach (object c in new object[] { capture, client, device, enumerator })
                    if (c != null) Marshal.ReleaseComObject(c);
            }
        }

        static void Check(int hr, string what)
        {
            if (hr != 0) throw new Exception(what + " (0x" + hr.ToString("X8") + ")");
        }

        public void Dispose()
        {
            running = false;
            thread.Join(1000);
        }
    }
}
