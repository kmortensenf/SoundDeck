using System;
using System.Runtime.InteropServices;

namespace SoundDeck
{
    // Minimal Media Foundation interop. Unused vtable slots are declared as placeholders (never called)
    // so the methods we do call land on the right slot.

    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFAttributes
    {
        void a00(); void a01(); void a02(); void a03();
        void GetUINT32([In] ref Guid key, out int value);
        void a05(); void a06(); void a07(); void a08(); void a09(); void a10(); void a11(); void a12(); void a13(); void a14();
        void a15(); void a16(); void a17();
        void SetUINT32([In] ref Guid key, int value);
        void a19(); void a20();
        void SetGUID([In] ref Guid key, [In] ref Guid value);
        void a22(); void a23(); void a24(); void a25(); void a26(); void a27(); void a28(); void a29();
    }

    [ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFMediaBuffer
    {
        void Lock(out IntPtr buffer, out int maxLength, out int currentLength);
        void Unlock();
        void GetCurrentLength(out int length);
        void SetCurrentLength(int length);
        void GetMaxLength(out int length);
    }

    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFSample
    {
        // IMFAttributes (30 slots)
        void a00(); void a01(); void a02(); void a03(); void a04(); void a05(); void a06(); void a07(); void a08(); void a09();
        void a10(); void a11(); void a12(); void a13(); void a14(); void a15(); void a16(); void a17(); void a18(); void a19();
        void a20(); void a21(); void a22(); void a23(); void a24(); void a25(); void a26(); void a27(); void a28(); void a29();
        // IMFSample
        void s00(); void s01(); void s02(); void s03(); void s04(); void s05(); void s06(); void s07();
        void ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
    }

    [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFSourceReader
    {
        void GetStreamSelection(int streamIndex, [MarshalAs(UnmanagedType.Bool)] out bool selected);
        void SetStreamSelection(int streamIndex, [MarshalAs(UnmanagedType.Bool)] bool selected);
        void GetNativeMediaType(int streamIndex, int typeIndex, out IMFAttributes mediaType);
        void GetCurrentMediaType(int streamIndex, out IMFAttributes mediaType);
        void SetCurrentMediaType(int streamIndex, IntPtr reserved, IMFAttributes mediaType);
        void SetCurrentPosition();
        void ReadSample(int streamIndex, int controlFlags, out int actualStreamIndex, out int streamFlags, out long timestamp, out IMFSample sample);
    }

    static class AudioDecoder
    {
        const int FIRST_AUDIO_STREAM = unchecked((int)0xFFFFFFFD);
        const int ALL_STREAMS = unchecked((int)0xFFFFFFFE);
        const int ENDOFSTREAM = 0x2;

        static Guid MF_MT_MAJOR_TYPE = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        static Guid MF_MT_SUBTYPE = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        static Guid MF_MT_AUDIO_NUM_CHANNELS = new Guid("37e48bf5-645e-4c5b-89de-ada9e29b696a");
        static Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new Guid("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        static Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new Guid("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
        static Guid MFMediaType_Audio = new Guid("73647561-0000-0010-8000-00AA00389B71");
        static Guid MFAudioFormat_Float = new Guid("00000003-0000-0010-8000-00AA00389B71");
        static Guid MFAudioFormat_PCM = new Guid("00000001-0000-0010-8000-00AA00389B71");

        [DllImport("mfplat.dll", ExactSpelling = true)] static extern int MFStartup(int version, int flags);
        [DllImport("mfplat.dll", ExactSpelling = true)] static extern int MFCreateMediaType(out IMFAttributes mediaType);
        [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        static extern int MFCreateSourceReaderFromURL(string url, IntPtr attributes, out IMFSourceReader reader);

        public static void Startup()
        {
            Marshal.ThrowExceptionForHR(MFStartup(0x20070, 0));
        }

        /// <summary>Decodes any file Media Foundation understands into 48 kHz interleaved stereo 16-bit samples.</summary>
        public static short[] Decode(string path)
        {
            IMFSourceReader reader;
            int hr = MFCreateSourceReaderFromURL(path, IntPtr.Zero, out reader);
            if (hr != 0) throw new Exception("unsupported or unreadable file (0x" + hr.ToString("X8") + ")");

            float[] raw = new float[1 << 16];
            int rawCount = 0, channels, rate;
            try
            {
                reader.SetStreamSelection(ALL_STREAMS, false);
                reader.SetStreamSelection(FIRST_AUDIO_STREAM, true);

                bool isFloat = TrySetFormat(reader, MFAudioFormat_Float);
                if (!isFloat && !TrySetFormat(reader, MFAudioFormat_PCM)) throw new Exception("no decodable audio stream");

                IMFAttributes cur;
                reader.GetCurrentMediaType(FIRST_AUDIO_STREAM, out cur);
                int bits = 32;
                cur.GetUINT32(ref MF_MT_AUDIO_NUM_CHANNELS, out channels);
                cur.GetUINT32(ref MF_MT_AUDIO_SAMPLES_PER_SECOND, out rate);
                if (!isFloat) cur.GetUINT32(ref MF_MT_AUDIO_BITS_PER_SAMPLE, out bits);
                Marshal.ReleaseComObject(cur);
                if (channels < 1 || rate < 1) throw new Exception("invalid audio format");

                byte[] bytes = new byte[0];
                while (true)
                {
                    int idx, flags;
                    long ts;
                    IMFSample sample;
                    reader.ReadSample(FIRST_AUDIO_STREAM, 0, out idx, out flags, out ts, out sample);
                    if (sample != null)
                    {
                        IMFMediaBuffer buf;
                        sample.ConvertToContiguousBuffer(out buf);
                        IntPtr p;
                        int max, len;
                        buf.Lock(out p, out max, out len);
                        if (bytes.Length < len) bytes = new byte[len];
                        Marshal.Copy(p, bytes, 0, len);
                        buf.Unlock();
                        Marshal.ReleaseComObject(buf);
                        Marshal.ReleaseComObject(sample);

                        int bps = isFloat ? 4 : bits / 8;
                        int n = len / bps;
                        if (rawCount + n > raw.Length) Array.Resize(ref raw, Math.Max(raw.Length * 2, rawCount + n));
                        for (int i = 0; i < n; i++) raw[rawCount + i] = ToFloat(bytes, i * bps, isFloat, bits);
                        rawCount += n;
                    }
                    if ((flags & ENDOFSTREAM) != 0) break;
                }
            }
            finally
            {
                Marshal.ReleaseComObject(reader);
            }

            return ToStereo48k(raw, rawCount, channels, rate);
        }

        static bool TrySetFormat(IMFSourceReader reader, Guid subtype)
        {
            IMFAttributes mt;
            Marshal.ThrowExceptionForHR(MFCreateMediaType(out mt));
            try
            {
                mt.SetGUID(ref MF_MT_MAJOR_TYPE, ref MFMediaType_Audio);
                mt.SetGUID(ref MF_MT_SUBTYPE, ref subtype);
                reader.SetCurrentMediaType(FIRST_AUDIO_STREAM, IntPtr.Zero, mt);
                return true;
            }
            catch (COMException)
            {
                return false;
            }
            finally
            {
                Marshal.ReleaseComObject(mt);
            }
        }

        static float ToFloat(byte[] b, int o, bool isFloat, int bits)
        {
            if (isFloat) return BitConverter.ToSingle(b, o);
            switch (bits)
            {
                case 8: return (b[o] - 128) / 128f;
                case 16: return BitConverter.ToInt16(b, o) / 32768f;
                case 24: return ((b[o] << 8 | b[o + 1] << 16 | b[o + 2] << 24) >> 8) / 8388608f;
                default: return BitConverter.ToInt32(b, o) / 2147483648f;
            }
        }

        /// <summary>Interleaved float samples (any rate / channel count) → 48 kHz interleaved stereo 16-bit.</summary>
        internal static short[] ToStereo48k(float[] raw, int rawCount, int ch, int rate)
        {
            int inFrames = rawCount / ch;
            if (inFrames == 0) return new short[0];
            int right = ch > 1 ? 1 : 0;
            float[] L = new float[inFrames], R = new float[inFrames];
            for (int i = 0; i < inFrames; i++) { L[i] = raw[i * ch]; R[i] = raw[i * ch + right]; }

            if (rate == WinMM.SampleRate)
            {
                short[] same = new short[inFrames * 2];
                for (int i = 0; i < inFrames; i++) { same[i * 2] = Clamp(L[i]); same[i * 2 + 1] = Clamp(R[i]); }
                return same;
            }

            // Windowed-sinc resampling (Blackman window, 512 precomputed phases). When downsampling,
            // the cutoff drops below the new Nyquist so high frequencies don't alias into grit.
            const int Taps = 16, Phases = 512;
            double cutoff = Math.Min(1.0, (double)WinMM.SampleRate / rate) * 0.95;
            int half = (int)Math.Ceiling(Taps / cutoff);
            int width = 2 * half;
            float[] table = new float[(Phases + 1) * width];
            for (int p = 0; p <= Phases; p++)
            {
                double frac = (double)p / Phases, sum = 0;
                for (int j = 0; j < width; j++)
                {
                    double x = (j - half + 1) - frac;
                    double sinc = x == 0 ? 1 : Math.Sin(Math.PI * cutoff * x) / (Math.PI * cutoff * x);
                    double win = Math.Abs(x) >= half ? 0 : 0.42 + 0.5 * Math.Cos(Math.PI * x / half) + 0.08 * Math.Cos(2 * Math.PI * x / half);
                    table[p * width + j] = (float)(sinc * win);
                    sum += sinc * win;
                }
                for (int j = 0; j < width; j++) table[p * width + j] /= (float)sum;
            }

            long outFrames = (long)inFrames * WinMM.SampleRate / rate;
            short[] o = new short[outFrames * 2];
            double step = (double)rate / WinMM.SampleRate;
            for (long f = 0; f < outFrames; f++)
            {
                double pos = f * step;
                int i0 = (int)pos;
                int b = (int)((pos - i0) * Phases + 0.5) * width;
                int first = i0 - half + 1;
                float l = 0, r = 0;
                for (int j = 0; j < width; j++)
                {
                    int idx = first + j;
                    if (idx < 0 || idx >= inFrames) continue;
                    float c = table[b + j];
                    l += c * L[idx];
                    r += c * R[idx];
                }
                o[f * 2] = Clamp(l);
                o[f * 2 + 1] = Clamp(r);
            }
            return o;
        }

        static short Clamp(float v)
        {
            v *= 32768f; // same scale as reading (sample / 32768), so 16-bit audio round-trips exactly
            if (v > 32767f) return 32767;
            if (v < -32768f) return -32768;
            return (short)Math.Round(v);
        }
    }
}
