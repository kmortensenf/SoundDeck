using System;

namespace SoundDeck
{
    /// <summary>
    /// Removes background noise from the microphone before sounds are mixed in, so Discord's own noise suppression
    /// (Krisp), which also strips sound effects and music, can be turned off.
    ///
    /// How: a short-time FFT (1024 samples, 50% overlap) with a Wiener-style gain per frequency bin.
    /// The noise level per bin is tracked continuously (minimum tracking), the gain uses the
    /// decision-directed SNR estimate, which keeps "musical noise" down. On top, a gate fades the
    /// mic down between words, and a DC blocker removes offset and sub-bass. Adds about 21 ms of delay.
    /// </summary>
    class NoiseSuppressor
    {
        const int N = 1024, Hop = N / 2, Bins = N / 2 + 1;
        const float MinGain = 0.1f;       // -20 dB: deeper than this sounds watery
        const float OverSubtract = 1.5f;  // treat noise as a bit louder than measured
        const float GateFloor = 0.06f;    // about -24 dB between words
        static readonly float[] Window = MakeWindow();

        readonly float[] inBuf = new float[N];
        int inFill = N - Hop;
        readonly float[] overlap = new float[N];
        readonly float[] outRing = new float[N * 4];
        int outRead, outCount;

        readonly float[] re = new float[N], im = new float[N];
        readonly float[] power = new float[Bins], noise = new float[Bins], prevGain = new float[Bins], prevSnr = new float[Bins];
        int frames;
        float gate = 1f;
        int hold;
        float dcIn, dcOut;

        /// <summary>Processes interleaved stereo 48 kHz samples in place (the result is mono on both channels).</summary>
        public void Process(short[] stereo, int count)
        {
            int n = count / 2;
            for (int f = 0; f < n; f++)
            {
                // DC blocker (one-pole high-pass at ~20 Hz): removes offset without touching deep voices
                float x = (stereo[f * 2] + stereo[f * 2 + 1]) / 65536f;
                dcOut = x - dcIn + 0.9974f * dcOut;
                dcIn = x;
                inBuf[inFill++] = dcOut;
                if (inFill == N)
                {
                    ProcessBlock();
                    Array.Copy(inBuf, Hop, inBuf, 0, N - Hop);
                    inFill = N - Hop;
                }
            }
            for (int f = 0; f < n; f++)
            {
                float v = 0;
                if (outCount > 0)
                {
                    v = outRing[outRead];
                    outRead = (outRead + 1) % outRing.Length;
                    outCount--;
                }
                float s = v * 32768f;
                short o = s > 32767f ? (short)32767 : s < -32768f ? (short)-32768 : (short)s;
                stereo[f * 2] = o;
                stereo[f * 2 + 1] = o;
            }
        }

        void ProcessBlock()
        {
            for (int i = 0; i < N; i++) { re[i] = inBuf[i] * Window[i]; im[i] = 0; }
            Fft(re, im, false);

            frames++;
            float speech = 0, floor = 0;
            for (int k = 0; k < Bins; k++)
            {
                float p = re[k] * re[k] + im[k] * im[k];
                power[k] = frames == 1 ? p : 0.8f * power[k] + 0.2f * p;
                // the noise floor follows the quietest level per bin, creeping up slowly so it can adapt
                if (frames == 1) noise[k] = power[k];
                else noise[k] = Math.Min(noise[k] * 1.004f + 1e-12f, power[k]);
                if (frames < 10) noise[k] = Math.Max(noise[k], power[k] * 0.5f); // settling in

                float post = p / (OverSubtract * noise[k] + 1e-12f);
                float prior = 0.98f * prevGain[k] * prevGain[k] * prevSnr[k] + 0.02f * Math.Max(post - 1, 0);
                float g = Math.Max(MinGain, prior / (1 + prior));
                prevGain[k] = g;
                prevSnr[k] = post;
                re[k] *= g;
                im[k] *= g;

                if (k >= 7 && k <= 73) { speech += power[k]; floor += noise[k]; } // ~300–3400 Hz, the voice band
            }

            // gate: open when the voice band clearly rises above the noise, close softly after a short hold
            bool voice = speech > 4f * floor;
            if (voice) hold = 18; // ~190 ms
            else if (hold > 0) hold--;
            float target = hold > 0 ? 1f : GateFloor;
            gate += (target - gate) * (target > gate ? 0.7f : 0.15f);

            for (int k = 1; k < N / 2; k++) { re[N - k] = re[k]; im[N - k] = -im[k]; } // keep the spectrum real
            Fft(re, im, true);

            for (int i = 0; i < N; i++) overlap[i] += re[i] * Window[i] * gate;
            for (int i = 0; i < Hop; i++)
            {
                if (outCount == outRing.Length) { outRead = (outRead + 1) % outRing.Length; outCount--; }
                outRing[(outRead + outCount) % outRing.Length] = overlap[i];
                outCount++;
            }
            Array.Copy(overlap, Hop, overlap, 0, N - Hop);
            Array.Clear(overlap, N - Hop, Hop);
        }

        /// <summary>Square-root Hann: applied before and after the FFT it adds up to exactly 1 at 50% overlap.</summary>
        static float[] MakeWindow()
        {
            float[] w = new float[N];
            for (int i = 0; i < N; i++) w[i] = (float)Math.Sqrt(0.5 * (1 - Math.Cos(2 * Math.PI * i / N)));
            return w;
        }

        /// <summary>In-place radix-2 FFT; the inverse is scaled by 1/N.</summary>
        static void Fft(float[] re, float[] im, bool inverse)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    float t = re[i]; re[i] = re[j]; re[j] = t;
                    t = im[i]; im[i] = im[j]; im[j] = t;
                }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = 2 * Math.PI / len * (inverse ? 1 : -1);
                float wr = (float)Math.Cos(ang), wi = (float)Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    float cr = 1, ci = 0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int a = i + k, b = i + k + len / 2;
                        float xr = re[b] * cr - im[b] * ci, xi = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - xr; im[b] = im[a] - xi;
                        re[a] += xr; im[a] += xi;
                        float nr = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr;
                        cr = nr;
                    }
                }
            }
            if (inverse) for (int i = 0; i < n; i++) { re[i] /= n; im[i] /= n; }
        }
    }
}
