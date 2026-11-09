using System;
using System.Collections.Generic;

namespace SoundDeck
{
    /// <summary>
    /// Measures how loud a sound is perceived (ITU-R BS.1770 integrated loudness, in LUFS — the measure streaming
    /// services use) and works out the gain that brings it to a common level.
    /// </summary>
    static class Loudness
    {
        public const double TargetLufs = -16;
        const double MaxBoostDb = 12; // don't pump up near-silent clips (and their noise) more than this

        /// <summary>Gain to apply so the sound lands near the target loudness without clipping.</summary>
        public static float GainFor(short[] stereo)
        {
            double lufs = Measure(stereo);
            if (double.IsNegativeInfinity(lufs)) return 1f;
            double gain = Math.Pow(10, Math.Min(TargetLufs - lufs, MaxBoostDb) / 20);
            int peak = 0;
            foreach (short s in stereo) { int a = Math.Abs((int)s); if (a > peak) peak = a; }
            if (peak > 0) gain = Math.Min(gain, 32767.0 / peak); // never boost into clipping
            return (float)gain;
        }

        /// <summary>Integrated loudness of 48 kHz interleaved stereo, or -infinity for silence.</summary>
        public static double Measure(short[] stereo)
        {
            int frames = stereo.Length / 2;
            if (frames == 0) return double.NegativeInfinity;

            // K-weighting at 48 kHz: high-shelf "head" filter, then the RLB high-pass (coefficients from BS.1770)
            Biquad[] shelf = { Biquad.Shelf(), Biquad.Shelf() };
            Biquad[] hp = { Biquad.HighPass(), Biquad.HighPass() };

            // energy per 100 ms segment; 400 ms blocks overlapping by 75% are four consecutive segments
            const int seg = 4800;
            List<double> segments = new List<double>();
            double acc = 0;
            int n = 0;
            for (int f = 0; f < frames; f++)
            {
                for (int c = 0; c < 2; c++)
                {
                    double x = stereo[f * 2 + c] / 32768.0;
                    double y = hp[c].Run(shelf[c].Run(x));
                    acc += y * y;
                }
                if (++n == seg) { segments.Add(acc); acc = 0; n = 0; }
            }

            List<double> blocks = new List<double>();
            if (segments.Count < 4)
            {
                // shorter than one 400 ms block: use the whole clip
                double total = acc;
                foreach (double s in segments) total += s;
                blocks.Add(total / frames);
            }
            else
            {
                for (int i = 0; i + 4 <= segments.Count; i++)
                    blocks.Add((segments[i] + segments[i + 1] + segments[i + 2] + segments[i + 3]) / (seg * 4.0));
            }

            // absolute gate at -70 LUFS, then relative gate 10 LU below the level of what's left
            List<double> loud = blocks.FindAll(p => ToLufs(p) > -70);
            if (loud.Count == 0) return double.NegativeInfinity;
            double threshold = ToLufs(Average(loud)) - 10;
            List<double> gated = loud.FindAll(p => ToLufs(p) > threshold);
            return ToLufs(Average(gated.Count > 0 ? gated : loud));
        }

        static double ToLufs(double meanSquare) { return meanSquare <= 0 ? double.NegativeInfinity : -0.691 + 10 * Math.Log10(meanSquare); }

        static double Average(List<double> v)
        {
            double s = 0;
            foreach (double x in v) s += x;
            return s / v.Count;
        }

        struct Biquad
        {
            double b0, b1, b2, a1, a2, z1, z2;

            public static Biquad Shelf()
            {
                Biquad q = new Biquad();
                q.b0 = 1.53512485958697; q.b1 = -2.69169618940638; q.b2 = 1.19839281085285;
                q.a1 = -1.69065929318241; q.a2 = 0.73248077421585;
                return q;
            }

            public static Biquad HighPass()
            {
                Biquad q = new Biquad();
                q.b0 = 1.0; q.b1 = -2.0; q.b2 = 1.0;
                q.a1 = -1.99004745483398; q.a2 = 0.99007225036621;
                return q;
            }

            public double Run(double x)
            {
                double y = b0 * x + z1;
                z1 = b1 * x - a1 * y + z2;
                z2 = b2 * x - a2 * y;
                return y;
            }
        }
    }
}
