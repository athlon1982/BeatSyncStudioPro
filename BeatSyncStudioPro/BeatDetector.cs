using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// A small home-grown beat tracker (no external DSP libraries): decode the
    /// song, build a bass-weighted onset envelope, autocorrelate it to find
    /// the beat period (55–180 BPM), then align the phase so grid lines land
    /// on the strongest onsets. Good on steady 4/4 material — drums-forward
    /// hip-hop and country included. Confidence is reported honestly.
    /// </summary>
    public static class BeatDetector
    {
        public static (double Bpm, double OffsetSeconds, double Confidence) Detect(byte[] bytes, string soundType)
        {
            var samples = DecodeMono(bytes, soundType);
            if (samples.Length < 44100 * 5) return (0, 0, 0);

            const int frame = 512;
            int frames = samples.Length / frame;
            var onset = new double[frames];
            double prevE = 0;
            double lp = 0;
            for (int f = 0; f < frames; f++)
            {
                double e = 0;
                int baseIdx = f * frame;
                for (int i = 0; i < frame; i++)
                {
                    double x = samples[baseIdx + i];
                    lp += 0.18 * (x - lp);           // crude low-pass: kicks and bass hits dominate
                    e += lp * lp + 0.25 * x * x;
                }
                e /= frame;
                onset[f] = Math.Max(0, e - prevE);
                prevE = e;
            }

            double frameDur = frame / 44100.0;
            int lagMin = Math.Max(4, (int)(60.0 / 180 / frameDur));
            int lagMax = (int)(60.0 / 55 / frameDur);
            var scores = new double[lagMax + 1];
            double bestScore = -1;
            int bestLag = 0;
            for (int lag = lagMin; lag <= lagMax; lag++)
            {
                double s = 0;
                for (int i = 0; i + lag < frames; i++) s += onset[i] * onset[i + lag];
                scores[lag] = s;
                if (s > bestScore) { bestScore = s; bestLag = lag; }
            }
            if (bestLag == 0 || bestScore <= 0) return (0, 0, 0);

            // Prefer the fundamental period over its double.
            int half = bestLag / 2;
            if (half >= lagMin && scores[half] > bestScore * 0.82) bestLag = half;

            double periodSec = bestLag * frameDur;
            double bpm = 60.0 / periodSec;

            // Phase: the offset within one period whose grid points collect the most onset energy.
            double bestPhaseScore = -1;
            int bestPhase = 0;
            for (int p = 0; p < bestLag; p++)
            {
                double s = 0;
                for (int i = p; i < frames; i += bestLag) s += onset[i];
                if (s > bestPhaseScore) { bestPhaseScore = s; bestPhase = p; }
            }
            double offset = bestPhase * frameDur;

            double totalOnset = 0;
            foreach (var o in onset) totalOnset += o;
            double ratio = totalOnset > 0 ? bestPhaseScore * bestLag / totalOnset : 0; // 1.0 = random
            double confidence = Math.Clamp((ratio - 1) / 2.5, 0, 1);

            return (Math.Round(bpm * 10) / 10, offset, confidence);
        }

        private static float[] DecodeMono(byte[] bytes, string soundType)
        {
            IWaveProvider reader = soundType.ToLower() == "wav"
                ? new WaveFileReader(new MemoryStream(bytes))
                : (IWaveProvider)new Mp3FileReader(new MemoryStream(bytes));
            ISampleProvider provider = new WdlResamplingSampleProvider(reader.ToSampleProvider(), 44100);
            var all = new List<float>();
            var buffer = new float[44100 * 2];
            int read;
            while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
            {
                int channels = provider.WaveFormat.Channels;
                for (int i = 0; i + channels - 1 < read; i += channels)
                {
                    float sum = 0;
                    for (int c = 0; c < channels; c++) sum += buffer[i + c];
                    all.Add(sum / channels);
                }
            }
            return all.ToArray();
        }
    }
}
