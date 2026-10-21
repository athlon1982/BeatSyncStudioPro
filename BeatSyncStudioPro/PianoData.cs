using System;
using System.Collections.Generic;

namespace BeatSyncStudioPro
{
    /// <summary>One note on the piano roll: a step, a MIDI pitch, a length in steps.</summary>
    public class PianoNote
    {
        public int Step { get; set; }
        public int Midi { get; set; }
        public int LengthSteps { get; set; } = 1;
    }

    /// <summary>A piano-roll melody: 32 steps (2 bars of 16ths), one instrument. Saved in the project.</summary>
    public class PianoPattern
    {
        public string Name { get; set; } = "Melody 1";
        public double Bpm { get; set; } = 95;
        public string Instrument { get; set; } = "keys";
        public List<PianoNote> Notes { get; set; } = new List<PianoNote>();
    }

    /// <summary>Small synthesized melodic instruments for the piano roll (44.1 kHz stereo).</summary>
    public static class SynthTones
    {
        public static readonly (string Key, string Label)[] Instruments =
        {
            ("keys", "🎹 Keys"),
            ("bass", "🎸 Sub Bass"),
            ("pluck", "🪕 Pluck"),
            ("808tone", "🔊 Melodic 808"),
        };

        public static double Freq(int midi) => 440.0 * Math.Pow(2, (midi - 69) / 12.0);

        /// <summary>Render one note. Length is the gate time; a short decay tail is added.</summary>
        public static float[] Note(string instrument, int midi, double gateSeconds)
        {
            double freq = Freq(midi);
            double total = gateSeconds + 0.18;
            int n = (int)(total * 44100);
            var mono = new float[n];
            for (int i = 0; i < n; i++)
            {
                double t = i / 44100.0;
                double gate = t < gateSeconds ? 1.0 : Math.Max(0, 1 - (t - gateSeconds) / 0.18);
                double v;
                switch (instrument)
                {
                    case "bass":
                        v = (Math.Sin(2 * Math.PI * freq * t) + 0.3 * Math.Sin(4 * Math.PI * freq * t))
                            * Math.Min(1, t * 120) * Math.Exp(-t * 1.2) * 0.85;
                        break;
                    case "pluck":
                        v = (Math.Sin(2 * Math.PI * freq * t) * 0.7 + Math.Sign(Math.Sin(2 * Math.PI * freq * t)) * 0.18)
                            * Math.Exp(-t * 9) * 0.7;
                        break;
                    case "808tone":
                        double f = freq * (1 + 0.03 * Math.Exp(-t * 30));
                        v = Math.Sin(2 * Math.PI * f * t) * Math.Exp(-t * 2.0) * 0.9;
                        break;
                    default: // keys
                        v = (Math.Sin(2 * Math.PI * freq * t)
                             + 0.4 * Math.Sin(4 * Math.PI * freq * t)
                             + 0.15 * Math.Sin(6 * Math.PI * freq * t))
                            * Math.Min(1, t * 200) * Math.Exp(-t * 3.2) * 0.6;
                        break;
                }
                mono[i] = (float)(v * gate);
            }
            var stereo = new float[n * 2];
            for (int i = 0; i < n; i++)
            {
                stereo[i * 2] = mono[i];
                stereo[i * 2 + 1] = mono[i];
            }
            return stereo;
        }
    }
}
