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
            // No preset ceiling on length — any bar count the user asks for,
            // bounded only by a ~20-minute safety limit so a typo can't
            // allocate gigabytes.
            int maxBars = Math.Max(1, (int)(1200.0 / (stepDur * 16)));
            bars = Math.Clamp(bars, 1, Math.Min(999, maxBars));
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
        /// <summary>Michael's own styles — always kept separate from the general styles.</summary>
        public static readonly (string Key, string Label, double Bpm)[] MyStyles =
        {
            ("boomrap", "👑 Boom-Rap — My Style (Rad-Dad)", 95),
        };

        public static readonly (string Key, string Label, double Bpm)[] Styles =
        {
            ("trap", "🔥 Trap", 140),
            ("house", "🏠 House", 122),
            ("funk", "🎷 Funk", 102),
            ("rock", "🎸 Rock", 120),
            ("dembow", "🌴 Dembow / Reggaeton", 88),
            ("lofi", "🌧️ Lo-Fi", 82),
            ("drill", "🗡️ UK Drill", 140),
            ("dancehall", "🌺 Dancehall", 102),
            ("westcoast", "🌴 West Coast / G-Funk", 96),
            ("techno", "⚙️ Techno", 130),
            ("dnb", "⚡ Drum & Bass", 174),
            ("phonk", "🚗 Phonk", 140),
            ("dubstep", "🔊 Dubstep", 140),
            ("synthwave", "🌆 Synthwave", 100),
            ("afrobeat", "🥁 Afrobeat", 105),
            ("country", "🤠 Country Train", 110),
        };

        public static (string Key, string Label, double Bpm) Find(string key)
        {
            foreach (var s in MyStyles) if (s.Key == key) return s;
            foreach (var s in Styles) if (s.Key == key) return s;
            return MyStyles[0];
        }

        public static PatternData Generate(string style, double bpm, double density, bool humanize, int seed)
        {
            var rng = new Random(seed);
            var p = new PatternData { Name = "Generated Beat", Bpm = bpm };
            p.Name = style switch
            {
                "boomrap" => "Boom-Rap Beat",
                "trap" => "Trap Beat",
                "house" => "House Beat",
                "funk" => "Funk Beat",
                "rock" => "Rock Beat",
                "dembow" => "Dembow Beat",
                "lofi" => "Lo-Fi Beat",
                "drill" => "Drill Beat",
                "dancehall" => "Dancehall Beat",
                "westcoast" => "West Coast Beat",
                "techno" => "Techno Beat",
                "dnb" => "Drum & Bass Beat",
                "phonk" => "Phonk Beat",
                "dubstep" => "Dubstep Beat",
                "synthwave" => "Synthwave Beat",
                "afrobeat" => "Afrobeat Beat",
                "country" => "Country Train Beat",
                _ => "Generated Beat"
            };
            PatternChannel kick = Ch("builtin:kick"), snare = Ch("builtin:snare"),
                            hat = Ch("builtin:hat"), clap = Ch("builtin:clap"), b808 = Ch("builtin:808"),
                            ohat = Ch("builtin:openhat"), rim = Ch("builtin:rimshot"),
                            shaker = Ch("builtin:shaker"), crash = Ch("builtin:crash");

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
                    foreach (var s in new[] { 2, 6, 10, 14 }) Hit(ohat, rng, s, 85);
                    if (Chance(0.6)) Hit(hat, rng, 15, 70);
                    p.Channels.AddRange(new[] { kick, clap, ohat, hat });
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
                    if (Chance(0.8)) Hit(crash, rng, 0, 80);
                    var rockCh = new List<PatternChannel> { kick, snare, hat };
                    AddUsed(rockCh, crash);
                    p.Channels.AddRange(rockCh);
                    break;

                case "dembow":
                    foreach (var s in new[] { 0, 6, 12 }) Hit(kick, rng, s, 110); // 3+3+2 feel against the snare pattern
                    foreach (var s in new[] { 3, 6, 11, 14 }) Hit(snare, rng, s, 95);
                    for (int s = 0; s < 16; s += 2) Hit(hat, rng, s, 80);
                    Hit(b808, rng, 0, 105);
                    p.Channels.AddRange(new[] { kick, snare, hat, b808 });
                    break;

                case "lofi":
                    Hit(kick, rng, 0); if (Chance(0.8)) Hit(kick, rng, 10);
                    Hit(snare, rng, 4); Hit(snare, rng, 12);
                    for (int s = 0; s < 16; s++) Hit(hat, rng, s, s % 2 == 0 ? 80 : 55);
                    foreach (var s in new[] { 2, 6, 10, 14 }) Hit(shaker, rng, s, 55);
                    Hit(b808, rng, 0, 95);
                    p.Channels.AddRange(new[] { kick, snare, hat, shaker, b808 });
                    break;

                case "drill":
                    Hit(kick, rng, 0); if (Chance(0.8)) Hit(kick, rng, 10);
                    Hit(snare, rng, 8); if (Chance(0.5)) Hit(snare, rng, 15, 70);
                    for (int s = 0; s < 16; s++) Hit(hat, rng, s, s % 4 == 0 ? 95 : 70);
                    if (Chance(0.7)) Hit(hat, rng, 14, 85);
                    Hit(b808, rng, 0, 115); if (Chance(0.8)) Hit(b808, rng, 6, 100); if (Chance(0.7)) Hit(b808, rng, 11, 105);
                    p.Channels.AddRange(new[] { kick, snare, hat, b808 });
                    break;

                case "dancehall":
                    Hit(kick, rng, 0); Hit(kick, rng, 8);
                    foreach (var s in new[] { 3, 6, 11, 14 }) Hit(rim, rng, s, 100);
                    for (int s = 0; s < 16; s += 2) Hit(shaker, rng, s, 70);
                    Hit(b808, rng, 0, 105); if (Chance(0.6)) Hit(b808, rng, 8, 95);
                    p.Channels.AddRange(new[] { kick, rim, shaker, b808 });
                    break;

                case "westcoast":
                    Hit(kick, rng, 0); Hit(kick, rng, 8); if (Chance(0.7)) Hit(kick, rng, 10);
                    Hit(snare, rng, 4); Hit(snare, rng, 12);
                    Hit(clap, rng, 4, 65); Hit(clap, rng, 12, 65);
                    for (int s = 0; s < 16; s++) Hit(hat, rng, s, s % 4 == 0 ? 95 : 65);
                    if (Chance(0.7)) Hit(ohat, rng, 14, 75);
                    Hit(b808, rng, 0, 110); if (Chance(0.7)) Hit(b808, rng, 8, 100);
                    p.Channels.AddRange(new[] { kick, snare, clap, hat, ohat, b808 });
                    break;

                case "techno":
                    foreach (var s in new[] { 0, 4, 8, 12 }) Hit(kick, rng, s, 118);
                    foreach (var s in new[] { 2, 6, 10, 14 }) Hit(ohat, rng, s, 90);
                    for (int s = 0; s < 16; s++) if (s % 2 == 1) Hit(hat, rng, s, 50);
                    if (Chance(0.6)) Hit(rim, rng, 7, 70); if (Chance(0.6)) Hit(rim, rng, 15, 70);
                    var techCh = new List<PatternChannel> { kick, ohat, hat };
                    AddUsed(techCh, rim);
                    p.Channels.AddRange(techCh);
                    break;

                case "dnb":
                    Hit(kick, rng, 0); Hit(kick, rng, 10);
                    Hit(snare, rng, 4); Hit(snare, rng, 12);
                    for (int s = 0; s < 16; s++) Hit(hat, rng, s, s % 2 == 0 ? 85 : 60);
                    if (Chance(0.7)) Hit(ohat, rng, 14, 70);
                    Hit(b808, rng, 0, 105); if (Chance(0.6)) Hit(b808, rng, 11, 95);
                    p.Channels.AddRange(new[] { kick, snare, hat, ohat, b808 });
                    break;

                case "phonk": // drift phonk — cowbell leads, memphis bounce
                {
                    var bell = Ch("builtin:cowbell");
                    Hit(kick, rng, 0); Hit(kick, rng, 6); if (Chance(0.8)) Hit(kick, rng, 10);
                    Hit(snare, rng, 4); Hit(snare, rng, 12);
                    foreach (var s in new[] { 0, 3, 6, 8, 11, 14 }) Hit(bell, rng, s, 95);
                    if (Chance(0.7)) Hit(bell, rng, 15, 80);
                    for (int s = 0; s < 16; s += 2) Hit(hat, rng, s, 75);
                    Hit(b808, rng, 0, 115); if (Chance(0.7)) Hit(b808, rng, 6, 105);
                    p.Channels.AddRange(new[] { kick, snare, bell, hat, b808 });
                    break;
                }

                case "dubstep": // half-time slam with wobble 808s
                    Hit(kick, rng, 0); if (Chance(0.6)) Hit(kick, rng, 7);
                    Hit(snare, rng, 8);
                    Hit(b808, rng, 0, 115); Hit(b808, rng, 3, 100); Hit(b808, rng, 6, 110); Hit(b808, rng, 10, 100); if (Chance(0.7)) Hit(b808, rng, 13, 105);
                    for (int s = 0; s < 16; s += 4) Hit(hat, rng, s, 70);
                    if (Chance(0.8)) Hit(crash, rng, 0, 85);
                    p.Channels.AddRange(new[] { kick, snare, b808, hat, crash });
                    break;

                case "synthwave": // four-on-the-floor, gated 80s drive
                    foreach (var s in new[] { 0, 4, 8, 12 }) Hit(kick, rng, s, 110);
                    Hit(snare, rng, 4); Hit(snare, rng, 12);
                    for (int s = 0; s < 16; s += 2) Hit(hat, rng, s, 85);
                    foreach (var s in new[] { 2, 6, 10, 14 }) Hit(ohat, rng, s, 65);
                    Hit(b808, rng, 0, 100); Hit(b808, rng, 8, 100);
                    p.Channels.AddRange(new[] { kick, snare, hat, ohat, b808 });
                    break;

                case "afrobeat": // log-drum pulse, cross-stick and shaker
                    Hit(kick, rng, 0); Hit(kick, rng, 5); Hit(kick, rng, 8); if (Chance(0.7)) Hit(kick, rng, 13);
                    foreach (var s in new[] { 2, 7, 10, 15 }) Hit(rim, rng, s, 90);
                    for (int s = 0; s < 16; s++) Hit(shaker, rng, s, s % 4 == 0 ? 85 : 60);
                    Hit(b808, rng, 0, 105); if (Chance(0.8)) Hit(b808, rng, 8, 100);
                    p.Channels.AddRange(new[] { kick, rim, shaker, b808 });
                    break;

                case "country": // train beat — snare chugs on every quarter
                    Hit(kick, rng, 0); Hit(kick, rng, 8);
                    foreach (var s in new[] { 0, 4, 8, 12 }) Hit(snare, rng, s, 95);
                    foreach (var s in new[] { 2, 6, 10, 14 }) Hit(snare, rng, s, 55);
                    for (int s = 0; s < 16; s += 2) Hit(hat, rng, s, 70);
                    if (Chance(0.7)) Hit(rim, rng, 15, 60);
                    p.Channels.AddRange(new[] { kick, snare, hat, rim });
                    break;

                default: // boomrap — Michael's style
                    Hit(kick, rng, 0); if (Chance(0.85)) Hit(kick, rng, 7); Hit(kick, rng, 8);
                    if (Chance(0.5)) Hit(kick, rng, 11);
                    Hit(snare, rng, 4); Hit(snare, rng, 12);
                    for (int s = 0; s < 16; s += 2) Hit(hat, rng, s, 95);
                    if (Chance(0.5)) Hit(hat, rng, 15, 75);
                    if (Chance(0.4)) Hit(rim, rng, 15, 50); // ghost pickup
                    Hit(b808, rng, 0, 110); if (Chance(0.8)) Hit(b808, rng, 8, 105);
                    var boomCh = new List<PatternChannel> { kick, snare, hat };
                    AddUsed(boomCh, rim);
                    boomCh.Add(b808);
                    p.Channels.AddRange(boomCh);
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

        /// <summary>Adds a channel only if at least one of its steps is on.</summary>
        private static void AddUsed(List<PatternChannel> list, PatternChannel c)
        {
            foreach (var s in c.Steps) if (s) { list.Add(c); return; }
        }

        private static void Hit(PatternChannel c, Random rng, int step, int velocity = 100)
        {
            c.Steps[step] = true;
            c.Velocities[step] = velocity;
        }
    }
}
