using System;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// Offline pitch shift that preserves duration (v3.28), used by DJ key
    /// sync: resample to move the pitch, then WSOLA time-stretch back to the
    /// original length. Clean-room WSOLA: Hann-windowed grains overlap-added
    /// at the synthesis hop, each grain's analysis position refined by a
    /// cross-correlation search so waveforms line up (no echo/doubling).
    /// Works on interleaved stereo float buffers at 44.1kHz.
    /// </summary>
    public static class PitchShifter
    {
        public static float[] Shift(float[] interleaved, double semitones)
        {
            if (interleaved.Length < 8192 || Math.Abs(semitones) < 0.01) return interleaved;
            double ratio = Math.Pow(2.0, semitones / 12.0);   // pitch multiplier
            var pitched = Resample(interleaved, ratio);        // pitch ×ratio, length ÷ratio
            var restored = WsolaStretch(pitched, ratio);       // length ×ratio, pitch kept
            int frames = interleaved.Length / 2;
            var result = new float[frames * 2];
            int copy = Math.Min(restored.Length, result.Length);
            Array.Copy(restored, result, copy);
            return result;
        }

        /// <summary>Linear-interpolation resample: step through the input at `ratio`, so pitch scales by ratio and length by 1/ratio.</summary>
        private static float[] Resample(float[] input, double ratio)
        {
            int inFrames = input.Length / 2;
            int outFrames = (int)(inFrames / ratio);
            var output = new float[outFrames * 2];
            for (int f = 0; f < outFrames; f++)
            {
                double pos = f * ratio;
                int i0 = (int)pos;
                int i1 = Math.Min(i0 + 1, inFrames - 1);
                double frac = pos - i0;
                output[f * 2] = (float)(input[i0 * 2] * (1 - frac) + input[i1 * 2] * frac);
                output[f * 2 + 1] = (float)(input[i0 * 2 + 1] * (1 - frac) + input[i1 * 2 + 1] * frac);
            }
            return output;
        }

        /// <summary>WSOLA time-stretch by `factor` (output length = input × factor), pitch unchanged.</summary>
        private static float[] WsolaStretch(float[] input, double factor)
        {
            int inFrames = input.Length / 2;
            int outFrames = (int)(inFrames * factor);
            var output = new float[outFrames * 2];
            var weights = new float[outFrames];

            const int W = 2048;              // grain length (frames)
            const int Hs = 512;              // synthesis hop
            double Ha = Hs / factor;         // nominal analysis hop
            int search = W / 2;              // WSOLA tolerance either side of nominal

            var window = new double[W];
            for (int i = 0; i < W; i++) window[i] = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (W - 1)));

            double analysisPos = 0;
            int synthPos = 0;
            while (synthPos < outFrames)
            {
                int nominal = (int)Math.Round(analysisPos);
                int best = nominal;
                if (synthPos > 0)
                {
                    // find the grain position whose start best continues the
                    // waveform already laid down in the overlap region
                    int from = Math.Max(0, nominal - search);
                    int to = Math.Min(inFrames - W, nominal + search);
                    double bestScore = double.NegativeInfinity;
                    int overlap = Math.Min(W - Hs, outFrames - synthPos);
                    if (overlap > 64 && to >= from)
                    {
                        for (int cand = from; cand <= to; cand += 4)
                        {
                            double score = 0;
                            for (int i = 0; i < overlap; i += 8)
                            {
                                double a = (output[(synthPos + i) * 2] + output[(synthPos + i) * 2 + 1]) * 0.5;
                                double b = (input[(cand + i) * 2] + input[(cand + i) * 2 + 1]) * 0.5;
                                score += a * b;
                            }
                            if (score > bestScore) { bestScore = score; best = cand; }
                        }
                    }
                }
                best = Math.Clamp(best, 0, Math.Max(0, inFrames - 1));
                for (int i = 0; i < W && synthPos + i < outFrames; i++)
                {
                    int src = best + i;
                    if (src >= inFrames) break;
                    double w = window[i];
                    output[(synthPos + i) * 2] += (float)(input[src * 2] * w);
                    output[(synthPos + i) * 2 + 1] += (float)(input[src * 2 + 1] * w);
                    weights[synthPos + i] += (float)w;
                }
                synthPos += Hs;
                analysisPos += Ha;
            }
            for (int f = 0; f < outFrames; f++)
            {
                float wgt = weights[f];
                if (wgt > 1e-4f)
                {
                    output[f * 2] /= wgt;
                    output[f * 2 + 1] /= wgt;
                }
            }
            return output;
        }
    }
}
