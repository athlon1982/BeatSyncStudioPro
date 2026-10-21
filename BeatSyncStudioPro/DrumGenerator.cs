using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// Offline pattern renderer shared by the sequencer window, the drum
    /// generator and the starter loop pack: mixes a pattern's steps
    /// (with per-step velocities) into a WAV, sample-accurately.
    /// </summary>
    public static class PatternRenderer
    {
        public static float[]? GetSamples(MainWindow owner, string key, Dictionary<string, float[]> cache)
        {
            if (cache.TryGetValue(key, out var cached)) return cached;
            float[]? result = null;
            try
            {
                if (key.StartsWith("builtin:"))
                {
                    result = SynthDrums.Get(key);
                }
                else if (owner.ZipSounds.TryGetValue(key, out var bytes))
                {
                    string ext = Path.GetExtension(key).TrimStart('.').ToLowerInvariant();
                    var (l, r) = StemSeparator.DecodeBytes(bytes, ext.Length > 0 ? ext : "wav");
                    result = new float[l.Length * 2];
                    for (int i = 0; i < l.Length; i++)
                    {
                        result[i * 2] = l[i];
                        result[i * 2 + 1] = r[i];
                    }
                }
            }
            catch { result = null; }
            if (result != null) cache[key] = result;
            return result;
        }

        public static byte[] RenderWav(MainWindow owner, PatternData pattern, int bars)
        {
            var cache = new Dictionary<string, float[]>();
            double stepDur = 60.0 / pattern.Bpm / 4.0;
            int stepSamples = (int)(stepDur * 44100);
            int totalFrames = stepSamples * 16 * bars;
            var mix = new float[totalFrames * 2];

            for (int bar = 0; bar < bars; bar++)
            {
                for (int step = 0; step < 16; step++)
                {
                    int at = (bar * 16 + step) * stepSamples;
                    foreach (var channel in pattern.Channels)
                    {
                        if (step >= channel.Steps.Length || !channel.Steps[step]) continue;
                        var samples = GetSamples(owner, channel.Sound, cache);
                        if (samples == null) continue;
                        float vel = (float)channel.VelocityAt(step);
                        int frames = Math.Min(samples.Length / 2, totalFrames - at);
                        for (int i = 0; i < frames * 2; i++)
                            mix[at * 2 + i] += samples[i] * vel;
                    }
                }
            }

            double peak = 0;
            foreach (var v in mix) peak = Math.Max(peak, Math.Abs(v));
            if (peak > 0.95)
            {
                float scale = (float)(0.95 / peak);
                for (int i = 0; i < mix.Length; i++) mix[i] *= scale;
            }

            string temp = Path.Combine(Path.GetTempPath(), "beatsync-pattern-" + Guid.NewGuid().ToString("N") + ".wav");
            try
            {
                WaveFileWriter.CreateWaveFile16(temp, new ArraySampleProvider(mix));
                return File.ReadAllBytes(temp);
            }
            finally
            {
                try { File.Delete(temp); } catch { }
            }
        }
    }

    /// <summary>
    /// Style-based drum pattern generator. Every preset produces an editable
    /// PatternData (built-in drums, per-step velocities) — generation is the
    /// starting point, the sequencer is where Michael makes it his.
    /// Density scales how many optional/variation hits appear; humanize adds
    /// velocity variation (the grid itself stays quantized).
    /// </summary>
    public static class DrumGenerator
    {
        public static readonly (string Key, string Label, double Bpm)[] Styles =
        {
            ("boombap", "🥁 Boom-Bap (Rad-Dad style)", 95),
            ("trap", "🔥 Trap", 140),
            ("house", "🏠 House", 122),
            ("funk", "🎷 Funk", 102),
            ("rock", "🎸 Rock", 120),
            ("dembow", "🌴 Dembow / Reggaeton", 88),
        };

        public static PatternData Generate(string style, double bpm, double density, bool humanize, int seed)
        {
            var rng = new Random(seed);
            var p = new PatternData { Name = "Generated Beat", Bpm = bpm };
            PatternChannel kick = Ch("builtin:kick"), snare = Ch("builtin:snare"),
                            hat = Ch("builtin:hat"), clap = Ch("builtin:clap"), b808 = Ch("builtin:808");

            bool Chance(double baseProb) => rng.NextDouble() < baseProb * density;

            switch (style)
            {
                case "trap":
                    Hit(kick, rng, 0); if (Chance(0.8)) Hit(kick, rng, 7); if (Chance(0.7)) Hit(kick, rng, 10);
                    Hit(snare, rng, 8);
                    for (int s = 0; s < 16; s += 2) Hit(hat, rng, s, 95);
                    if (Chance(0.9)) { Hit(hat, rng, 13, 80); Hit(hat, rng, 14, 90); Hit(hat, rng, 15, 85); }
                    if (Chance(0.5)) Hit(hat, rng, 3, 70);
                    Hit(b808, rng, 0, 110); if (Chance(0.7)) Hit(b808, rng, 10, 100);
                    p.Channels.AddRange(new[] { kick, snare, hat, b808 });
                    break;

                case "house":
                    foreach (var s in new[] { 0, 4, 8, 12 }) Hit(kick, rng, s, 115);
                    Hit(clap, rng, 4); Hit(clap, rng, 12);
                    foreach (var s in new[] { 2, 6, 10, 14 }) Hit(hat, rng, s, 90);
                    if (Chance(0.6)) Hit(hat, rng, 15, 70);
                    p.Channels.AddRange(new[] { kick, clap, hat });
                    break;

                case "funk":
                    Hit(kick, rng, 0); Hit(kick, rng, 7); if (Chance(0.8)) Hit(kick, rng, 10); Hit(kick, rng, 12);
                    Hit(snare, rng, 4); Hit(snare, rng, 12);
                    if (Chance(0.9)) Hit(snare, rng, 3, 45);   // ghost
                    if (Chance(0.9)) Hit(snare, rng, 15, 50);  // ghost
                    if (Chance(0.6)) Hit(snare, rng, 10, 40);  // ghost
                    for (int s = 0; s < 16; s++) Hit(hat, rng, s, s % 4 == 0 ? 100 : 75);
                    p.Channels.AddRange(new[] { kick, snare, hat });
                    break;

                case "rock":
                    Hit(kick, rng, 0); Hit(kick, rng, 8); if (Chance(0.6)) Hit(kick, rng, 10);
                    Hit(snare, rng, 4); Hit(snare, rng, 12);
                    for (int s = 0; s < 16; s += 2) Hit(hat, rng, s, 95);
                    p.Channels.AddRange(new[] { kick, snare, hat });
                    break;

                case "dembow":
                    foreach (var s in new[] { 0, 6, 12 }) Hit(kick, rng, s, 110); // 3+3+2 feel against the snare pattern
                    foreach (var s in new[] { 3, 6, 11, 14 }) Hit(snare, rng, s, 95);
                    for (int s = 0; s < 16; s += 2) Hit(hat, rng, s, 80);
                    Hit(b808, rng, 0, 105);
                    p.Channels.AddRange(new[] { kick, snare, hat, b808 });
                    break;

                default: // boombap
                    Hit(kick, rng, 0); if (Chance(0.85)) Hit(kick, rng, 7); Hit(kick, rng, 8);
                    if (Chance(0.5)) Hit(kick, rng, 11);
                    Hit(snare, rng, 4); Hit(snare, rng, 12);
                    for (int s = 0; s < 16; s += 2) Hit(hat, rng, s, 95);
                    if (Chance(0.5)) Hit(hat, rng, 15, 75);
                    Hit(b808, rng, 0, 110); if (Chance(0.8)) Hit(b808, rng, 8, 105);
                    p.Channels.AddRange(new[] { kick, snare, hat, b808 });
                    break;
            }

            if (humanize)
            {
                foreach (var channel in p.Channels)
                {
                    for (int s = 0; s < 16; s++)
                    {
                        if (!channel.Steps[s]) continue;
                        int v = channel.Velocities[s];
                        if (v >= 80) channel.Velocities[s] = Math.Clamp(v + rng.Next(-8, 9), 60, 127);
                        // ghosts stay ghostly
                    }
                }
            }
            return p;
        }

        private static PatternChannel Ch(string sound) => new PatternChannel { Sound = sound };

        private static void Hit(PatternChannel c, Random rng, int step, int velocity = 100)
        {
            c.Steps[step] = true;
            c.Velocities[step] = velocity;
        }
    }
}
