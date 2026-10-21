using System;
using NAudio.Dsp;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// Musical key detection (v3.28), clean-room implementation of the
    /// standard chromagram + Krumhansl-Schmuckler profile method: STFT the
    /// song, fold energy into 12 pitch classes, correlate against the
    /// published major/minor key profiles, best correlation wins.
    /// Also owns the Camelot wheel tables and compatibility rules.
    /// </summary>
    public static class KeyDetector
    {
        public sealed record KeyResult(int PitchClass, bool IsMinor, string KeyName, string Camelot, double Confidence);

        // Krumhansl-Kessler profiles (pitch class C = index 0)
        private static readonly double[] MajorProfile = { 6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88 };
        private static readonly double[] MinorProfile = { 6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17 };

        private static readonly string[] MajorNames = { "C", "D♭", "D", "E♭", "E", "F", "F♯", "G", "A♭", "A", "B♭", "B" };
        private static readonly string[] MinorNames = { "C", "C♯", "D", "E♭", "E", "F", "F♯", "G", "G♯", "A", "B♭", "B" };
        private static readonly string[] MajorCamelot = { "8B", "3B", "10B", "5B", "12B", "7B", "2B", "9B", "4B", "11B", "6B", "1B" };
        private static readonly string[] MinorCamelot = { "5A", "12A", "7A", "2A", "9A", "4A", "11A", "6A", "1A", "8A", "3A", "10A" };

        public static string KeyNameFor(int pitchClass, bool isMinor)
            => (isMinor ? MinorNames : MajorNames)[Mod12(pitchClass)] + (isMinor ? " min" : " maj");

        public static string CamelotFor(int pitchClass, bool isMinor)
            => (isMinor ? MinorCamelot : MajorCamelot)[Mod12(pitchClass)];

        public static int Mod12(int v) { int r = v % 12; return r < 0 ? r + 12 : r; }

        /// <summary>Camelot compatibility: same code, ±1 wheel number (same letter), or relative major/minor (same number, other letter).</summary>
        public static bool CamelotCompatible(string? a, string? b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            if (a == b) return true;
            if (!TryParseCamelot(a, out int na, out char la) || !TryParseCamelot(b, out int nb, out char lb)) return false;
            if (la == lb) { int d = Mod12(na - nb); return d == 1 || d == 11; }   // one step either way around the wheel
            return na == nb;   // relative major/minor
        }

        public static bool TryParseCamelot(string code, out int number, out char letter)
        {
            number = 0; letter = '?';
            if (string.IsNullOrEmpty(code) || code.Length < 2) return false;
            letter = code[^1];
            return (letter == 'A' || letter == 'B') && int.TryParse(code[..^1], out number);
        }

        /// <summary>Detect the key of a mono 44.1kHz signal. Returns null when there isn't enough audio to judge.</summary>
        public static KeyResult? Detect(float[] mono)
        {
            const int FftSize = 8192;
            const int Log2 = 13;
            const int SampleRate = 44100;
            if (mono == null || mono.Length < FftSize * 4) return null;

            var chroma = new double[12];
            var fft = new Complex[FftSize];
            var hann = new double[FftSize];
            for (int i = 0; i < FftSize; i++) hann[i] = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (FftSize - 1)));

            int hop = FftSize / 2;
            long windows = 0;
            for (int start = 0; start + FftSize <= mono.Length; start += hop)
            {
                for (int i = 0; i < FftSize; i++) { fft[i].X = (float)(mono[start + i] * hann[i]); fft[i].Y = 0; }
                FastFourierTransform.FFT(true, Log2, fft);
                for (int bin = 1; bin < FftSize / 2; bin++)
                {
                    double freq = bin * (double)SampleRate / FftSize;
                    if (freq < 50 || freq > 5000) continue;
                    double mag = Math.Sqrt(fft[bin].X * fft[bin].X + fft[bin].Y * fft[bin].Y);
                    if (mag < 1e-6) continue;
                    double midi = 69 + 12 * Math.Log2(freq / 440.0);
                    int pc = Mod12((int)Math.Round(midi));
                    chroma[pc] += mag;
                }
                windows++;
            }
            if (windows == 0) return null;

            double best = double.NegativeInfinity;
            int bestPc = 0; bool bestMinor = false;
            for (int pc = 0; pc < 12; pc++)
            {
                double cMaj = Pearson(Rotate(chroma, pc), MajorProfile);
                if (cMaj > best) { best = cMaj; bestPc = pc; bestMinor = false; }
                double cMin = Pearson(Rotate(chroma, pc), MinorProfile);
                if (cMin > best) { best = cMin; bestPc = pc; bestMinor = true; }
            }
            return new KeyResult(bestPc, bestMinor, KeyNameFor(bestPc, bestMinor), CamelotFor(bestPc, bestMinor), best);
        }

        /// <summary>Rotate so the candidate tonic sits at index 0 (profile order starts at the tonic).</summary>
        private static double[] Rotate(double[] chroma, int tonic)
        {
            var r = new double[12];
            for (int i = 0; i < 12; i++) r[i] = chroma[Mod12(tonic + i)];
            return r;
        }

        private static double Pearson(double[] a, double[] b)
        {
            double ma = 0, mb = 0;
            for (int i = 0; i < 12; i++) { ma += a[i]; mb += b[i]; }
            ma /= 12; mb /= 12;
            double num = 0, da = 0, db = 0;
            for (int i = 0; i < 12; i++)
            {
                double xa = a[i] - ma, xb = b[i] - mb;
                num += xa * xb; da += xa * xa; db += xb * xb;
            }
            double den = Math.Sqrt(da * db);
            return den < 1e-12 ? 0 : num / den;
        }
    }
}
