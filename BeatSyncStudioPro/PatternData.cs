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

        /// <summary>Per-step velocity, 0–127 (100 = full). Older projects may lack it — readers must guard.</summary>
        public int[] Velocities { get; set; } = { 100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100 };

        public double VelocityAt(int step)
        {
            if (Velocities == null || step < 0 || step >= Velocities.Length) return 1.0;
            return Math.Clamp(Velocities[step], 1, 127) / 100.0;
        }
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
            ("builtin:openhat", "🥁 Open Hat (built-in)"),
            ("builtin:clap", "👏 Clap (built-in)"),
            ("builtin:808", "🔊 808 (built-in)"),
            ("builtin:rimshot", "🥁 Rimshot (built-in)"),
            ("builtin:shaker", "🥁 Shaker (built-in)"),
            ("builtin:tomlow", "🥁 Low Tom (built-in)"),
            ("builtin:tomhigh", "🥁 High Tom (built-in)"),
            ("builtin:cowbell", "🔔 Cowbell (built-in)"),
            ("builtin:crash", "💥 Crash (built-in)"),
        };

        public static float[] Get(string key)
        {
            return key switch
            {
                "builtin:snare" => Snare(),
                "builtin:hat" => Hat(),
                "builtin:openhat" => OpenHat(),
                "builtin:clap" => Clap(),
                "builtin:808" => Eight08(),
                "builtin:rimshot" => Rimshot(),
                "builtin:shaker" => Shaker(),
                "builtin:tomlow" => Tom(150, 82),
                "builtin:tomhigh" => Tom(215, 128),
                "builtin:cowbell" => Cowbell(),
                "builtin:crash" => Crash(),
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

        private static float[] OpenHat()
        {
            int n = (int)(0.35 * 44100);
            var m = new float[n];
            var rnd = new Random(21);
            double prev = 0;
            for (int i = 0; i < n; i++)
            {
                double t = i / 44100.0;
                double white = rnd.NextDouble() * 2 - 1;
                double hp = white - prev;
                prev = white;
                m[i] = (float)(hp * Math.Exp(-t * 16) * 0.42);
            }
            return Stereo(m);
        }

        private static float[] Rimshot()
        {
            int n = (int)(0.08 * 44100);
            var m = new float[n];
            var rnd = new Random(31);
            for (int i = 0; i < n; i++)
            {
                double t = i / 44100.0;
                double click = (rnd.NextDouble() * 2 - 1) * Math.Exp(-t * 160) * 0.5;
                double ping = Math.Sin(2 * Math.PI * 1720 * t) * Math.Exp(-t * 90) * 0.5;
                m[i] = (float)(click + ping);
            }
            return Stereo(m);
        }

        private static float[] Shaker()
        {
            int n = (int)(0.22 * 44100);
            var m = new float[n];
            var rnd = new Random(55);
            double prev = 0;
            for (int i = 0; i < n; i++)
            {
                double t = i / 44100.0;
                double white = rnd.NextDouble() * 2 - 1;
                double hp = white - prev;
                prev = white;
                double env = Math.Min(1, t / 0.03) * Math.Exp(-t * 14);
                double wobble = 0.7 + 0.3 * Math.Sin(2 * Math.PI * 26 * t);
                m[i] = (float)(hp * env * wobble * 0.4);
            }
            return Stereo(m);
        }

        private static float[] Tom(double fromHz, double toHz)
        {
            int n = (int)(0.30 * 44100);
            var m = new float[n];
            double phase = 0;
            for (int i = 0; i < n; i++)
            {
                double t = i / 44100.0;
                double freq = toHz + (fromHz - toHz) * Math.Exp(-t * 22);
                phase += 2 * Math.PI * freq / 44100;
                m[i] = (float)(Math.Sin(phase) * Math.Exp(-t * 11) * 0.8);
            }
            return Stereo(m);
        }

        private static float[] Cowbell()
        {
            int n = (int)(0.28 * 44100);
            var m = new float[n];
            for (int i = 0; i < n; i++)
            {
                double t = i / 44100.0;
                double v = Math.Sin(2 * Math.PI * 545 * t) * 0.6
                         + Math.Sin(2 * Math.PI * 815 * t) * 0.4;
                m[i] = (float)(v * Math.Exp(-t * 15) * 0.5);
            }
            return Stereo(m);
        }

        private static float[] Crash()
        {
            int n = (int)(1.40 * 44100);
            var m = new float[n];
            var rnd = new Random(77);
            double prev = 0;
            for (int i = 0; i < n; i++)
            {
                double t = i / 44100.0;
                double white = rnd.NextDouble() * 2 - 1;
                double hp = white - prev;
                prev = white;
                double shimmer = Math.Sin(2 * Math.PI * 6230 * t) * 0.08
                               + Math.Sin(2 * Math.PI * 8450 * t) * 0.06;
                m[i] = (float)((hp * 0.5 + shimmer) * Math.Exp(-t * 3.4) * 0.6);
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
