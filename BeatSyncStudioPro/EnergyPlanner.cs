using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NAudio.Wave;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// Set energy analysis (v3.89): a per-song energy score (0..1) for
    /// the Set Builder's energy planner. The score blends the song's
    /// actual loudness (RMS over the whole file, decoded with the same
    /// reader the beat detector uses) with its tempo, and is cached in
    /// energy-cache.json keyed by path + file size + mtime, so a song
    /// is only ever decoded once. -1 means "couldn't analyze".
    /// </summary>
    public static class EnergyPlanner
    {
        private sealed class Entry
        {
            public long Size { get; set; }
            public long MtimeUtcTicks { get; set; }
            public double Energy { get; set; }
        }

        private static string FilePath => Path.Combine(SongLibraryStore.AppDir, "energy-cache.json");
        private static readonly object _lock = new object();
        private static Dictionary<string, Entry>? _cache;

        private static Dictionary<string, Entry> Cache
        {
            get
            {
                lock (_lock)
                {
                    if (_cache == null)
                    {
                        _cache = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
                        try
                        {
                            if (File.Exists(FilePath))
                            {
                                var loaded = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath));
                                if (loaded != null)
                                    _cache = new Dictionary<string, Entry>(loaded, StringComparer.OrdinalIgnoreCase);
                            }
                        }
                        catch { }
                    }
                    return _cache;
                }
            }
        }

        private static void SaveCache()
        {
            try
            {
                Directory.CreateDirectory(SongLibraryStore.AppDir);
                Dictionary<string, Entry> snapshot;
                lock (_lock) snapshot = new Dictionary<string, Entry>(_cache!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(snapshot));
            }
            catch { }
        }

        public static bool TryGet(string path, out double energy)
        {
            energy = -1;
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return false;
                lock (_lock)
                {
                    if (Cache.TryGetValue(path, out var e) && e.Size == fi.Length && e.MtimeUtcTicks == fi.LastWriteTimeUtc.Ticks)
                    {
                        energy = e.Energy;
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>Decode + score a song (background-thread friendly).
        /// Returns the energy (0..1) or -1 when the file can't be read.</summary>
        public static async Task<double> AnalyzeAsync(string path, double bpm)
        {
            if (TryGet(path, out double cached)) return cached;
            double energy = await Task.Run(() => DecodeAndScore(path, bpm));
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists)
                {
                    lock (_lock)
                    {
                        Cache[path] = new Entry { Size = fi.Length, MtimeUtcTicks = fi.LastWriteTimeUtc.Ticks, Energy = energy };
                    }
                    SaveCache();
                }
            }
            catch { }
            return energy;
        }

        private static double DecodeAndScore(string path, double bpm)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length > 220_000_000) return -1;
                byte[] bytes = File.ReadAllBytes(path);
                string type = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
                double sumSq = 0;
                long count = 0;
                using (var reader = AudioReaderFactory.Open(bytes, type))
                {
                    var samples = reader.ToSampleProvider();
                    var buf = new float[16384];
                    int read;
                    while ((read = samples.Read(buf, 0, buf.Length)) > 0)
                    {
                        for (int i = 0; i < read; i++) sumSq += (double)buf[i] * buf[i];
                        count += read;
                    }
                }
                if (count == 0) return -1;
                double rms = Math.Sqrt(sumSq / count);
                if (rms <= 0) return 0;
                double rmsDb = 20 * Math.Log10(rms);
                double loud = Math.Clamp((rmsDb + 45.0) / 33.0, 0.0, 1.0);   // -45 dB -> 0, -12 dB -> 1
                if (bpm > 0)
                {
                    double tempo = Math.Clamp((bpm - 70.0) / 90.0, 0.0, 1.0); // 70 BPM -> 0, 160 BPM -> 1
                    return Math.Clamp(0.65 * loud + 0.35 * tempo, 0.0, 1.0);
                }
                return loud;
            }
            catch { return -1; }
        }

        // ---------- Camelot compatibility (for chart dot colors) ----------

        private static readonly Regex CamelotRe = new Regex(@"^\s*(\d{1,2})\s*([ABab])\s*$", RegexOptions.Compiled);

        /// <summary>2 = harmonic (same, relative, or one step on the
        /// wheel), 1 = workable (same number other letter is 2; here:
        /// two steps or same letter far), 0 = clash, -1 = unknown.</summary>
        public static int KeyCompatibility(string prevKey, string nextKey)
        {
            if (!TryParseCamelot(prevKey, out int pn, out char pl)) return -1;
            if (!TryParseCamelot(nextKey, out int nn, out char nl)) return -1;
            if (pn == nn) return 2;                                   // same number: identical or relative major/minor
            int diff = Math.Abs(pn - nn);
            diff = Math.Min(diff, 12 - diff);                         // wheel wraps at 12
            if (diff == 1 && pl == nl) return 2;                      // one step, same letter: perfect fifth move
            if (diff == 1 || diff == 2) return 1;
            return 0;
        }

        private static bool TryParseCamelot(string key, out int num, out char letter)
        {
            num = 0; letter = 'A';
            if (string.IsNullOrWhiteSpace(key)) return false;
            var m = CamelotRe.Match(key);
            if (!m.Success) return false;
            num = int.Parse(m.Groups[1].Value);
            letter = char.ToUpperInvariant(m.Groups[2].Value[0]);
            return num >= 1 && num <= 12;
        }
    }
}
