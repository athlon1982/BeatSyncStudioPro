using System;
using System.Collections.Generic;

namespace BeatSyncStudioPro
{
    /// <summary>One row of a pattern: a sound plus its 16 steps (one bar of 16ths).</summary>
    public class PatternChannel
    {
        /// <summary>"builtin:kick" etc., or a library sound name (a ZipSounds key).</summary>
        public string Sound { get; set; } = "builtin:kick";
        public bool[] Steps { get; set; } = new bool[16];
    }

    /// <summary>A step-sequencer pattern. Saved inside the project file.</summary>
    public class PatternData
    {
        public string Name { get; set; } = "Pattern 1";
        public double Bpm { get; set; } = 120;
        public List<PatternChannel> Channels { get; set; } = new List<PatternChannel>();
    }

    /// <summary>
    /// Tiny synthesized one-shot drums, so patterns play even with an empty
    /// library. All render at 44.1 kHz stereo (interleaved floats).
    /// </summary>
    public static class SynthDrums
    {
        public static readonly (string Key, string Label)[] BuiltIns =
        {
            ("builtin:kick", "🥁 Kick (built-in)"),
            ("builtin:snare", "🥁 Snare (built-in)"),
            ("builtin:hat", "🥁 Hi-Hat (built-in)"),
            ("builtin:clap", "👏 Clap (built-in)"),
            ("builtin:808", "🔊 808 (built-in)"),
        };

        public static float[] Get(string key)
        {
            return key switch
            {
                "builtin:snare" => Snare(),
                "builtin:hat" => Hat(),
                "builtin:clap" => Clap(),
                "builtin:808" => Eight08(),
                _ => Kick()
            };
        }

        private static float[] Stereo(float[] mono)
        {
            var s = new float[mono.Length * 2];
            for (int i = 0; i < mono.Length; i++)
            {
                s[i * 2] = mono[i];
                s[i * 2 + 1] = mono[i];
            }
            return s;
        }

        private static float[] Kick()
        {
            int n = (int)(0.30 * 44100);
            var m = new float[n];
            double phase = 0;
            for (int i = 0; i < n; i++)
            {
                double t = i / 44100.0;
                double freq = 45 + 105 * Math.Exp(-t * 40);
                phase += 2 * Math.PI * freq / 44100;
                m[i] = (float)(Math.Sin(phase) * Math.Exp(-t * 14) * 0.95);
            }
            return Stereo(m);
        }

        private static float[] Snare()
        {
            int n = (int)(0.18 * 44100);
            var m = new float[n];
            var rnd = new Random(42);
            for (int i = 0; i < n; i++)
            {
                double t = i / 44100.0;
                double noise = (rnd.NextDouble() * 2 - 1) * Math.Exp(-t * 30) * 0.6;
                double tone = Math.Sin(2 * Math.PI * 190 * t) * Math.Exp(-t * 45) * 0.4;
                m[i] = (float)(noise + tone);
            }
            return Stereo(m);
        }

        private static float[] Hat()
        {
            int n = (int)(0.06 * 44100);
            var m = new float[n];
            var rnd = new Random(7);
            double prev = 0;
            for (int i = 0; i < n; i++)
            {
                double t = i / 44100.0;
                double white = rnd.NextDouble() * 2 - 1;
                double hp = white - prev; // crude high-pass
                prev = white;
                m[i] = (float)(hp * Math.Exp(-t * 90) * 0.5);
            }
            return Stereo(m);
        }

        private static float[] Clap()
        {
            int n = (int)(0.25 * 44100);
            var m = new float[n];
            var rnd = new Random(99);
            for (int i = 0; i < n; i++)
            {
                double t = i / 44100.0;
                double env = Math.Exp(-t * 18);
                // three quick bursts then a tail
                if (t < 0.012 || (t > 0.02 && t < 0.032) || (t > 0.04 && t < 0.052)) env = 1.0;
                m[i] = (float)((rnd.NextDouble() * 2 - 1) * env * 0.55);
            }
            return Stereo(m);
        }

        private static float[] Eight08()
        {
            int n = (int)(0.60 * 44100);
            var m = new float[n];
            double phase = 0;
            for (int i = 0; i < n; i++)
            {
                double t = i / 44100.0;
                double freq = 55 + 55 * Math.Exp(-t * 25);
                phase += 2 * Math.PI * freq / 44100;
                m[i] = (float)(Math.Sin(phase) * Math.Exp(-t * 5.5) * 0.9);
            }
            return Stereo(m);
        }
    }
}
