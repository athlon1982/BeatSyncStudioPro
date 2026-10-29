using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace BeatSyncStudioPro
{
    /// <summary>One measured track: K-weighted loudness, sample peak, and
    /// the gain that brings it to the Auto-Level target. Stored in the
    /// loudness cache keyed by the SHA256 of the loaded file bytes.</summary>
    public sealed record LoudnessEntry(double Lufs, double Peak, double GainDb, long MeasuredUnix);

    /// <summary>Snapshot of a deck panel's auto-level state, handed to the
    /// encode window so its "Apply Auto-Level" checkbox defaults to the
    /// deck's own auto-gain switch and gain.</summary>
    public sealed record AutoLevelInfo(bool On, double GainDb, double Lufs, bool Measured);

    /// <summary>
    /// Auto-Level (v3.118): loudness measurement shared by playback
    /// auto-gain and the encode-time "Apply Auto-Level" option.
    /// Measurement is the same BS.1770 K-weighted method the batch
    /// converter already uses (ExportRenderer.MeasureLoudness): two
    /// biquads per channel, ungated mean-square, -0.691 offset — run
    /// here on the deck's decoded in-memory buffers instead of a file,
    /// so no subprocess, temp file, or re-decode is needed and playback
    /// and batch results agree. Gain math is the batch converter's
    /// formula exactly: clamp(target - LUFS, +/-24 dB), then cap so the
    /// sample peak lands at -1 dB.
    /// Honest limit: the codebase has no oversampled true-peak
    /// measurement, so the ceiling uses the sample peak; the -1 dB of
    /// headroom absorbs typical intersample overshoot.
    /// </summary>
    public static class LoudnessAnalyzer
    {
        /// <summary>Streaming/distribution target, matching the RouteNote masters.</summary>
        public const double TargetLufs = -14.0;
        /// <summary>Peak ceiling: the loudest sample after gain sits 1 dB below full scale.</summary>
        public const double PeakCeilingDb = -1.0;
        /// <summary>Gain clamp, matching the batch converter.</summary>
        public const double GainClampDb = 24.0;

        /// <summary>One stage of the BS.1770 K-weighting filter (direct form I biquad).
        /// Coefficients identical to ExportRenderer.MeasureLoudness.</summary>
        private struct Biquad
        {
            private readonly double _b0, _b1, _b2, _a1, _a2;
            private double _x1, _x2, _y1, _y2;

            public Biquad(double b0, double b1, double b2, double a1, double a2)
            {
                _b0 = b0; _b1 = b1; _b2 = b2; _a1 = a1; _a2 = a2;
                _x1 = _x2 = _y1 = _y2 = 0;
            }

            public double Process(double x)
            {
                double y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
                _x2 = _x1; _x1 = x;
                _y2 = _y1; _y1 = y;
                return y;
            }
        }

        /// <summary>Integrated loudness (K-weighted, ungated) plus the
        /// sample peak of de-interleaved 44.1 kHz stereo buffers.</summary>
        public static (double Lufs, double Peak) Measure(float[] left, float[] right)
        {
            int n = Math.Min(left.Length, right.Length);
            var s1L = new Biquad(1.53512485958697, -2.69169618940638, 1.19839281085285, -1.69065929318241, 0.73248077421585);
            var s2L = new Biquad(1, -2, 1, -1.99004745483398, 0.99007225036621);
            var s1R = new Biquad(1.53512485958697, -2.69169618940638, 1.19839281085285, -1.69065929318241, 0.73248077421585);
            var s2R = new Biquad(1, -2, 1, -1.99004745483398, 0.99007225036621);
            double sum = 0;
            double peak = 0;
            for (int i = 0; i < n; i++)
            {
                double l = s2L.Process(s1L.Process(left[i]));
                double r = s2R.Process(s1R.Process(right[i]));
                sum += l * l + r * r;
                double a = Math.Abs(left[i]);
                double b = Math.Abs(right[i]);
                if (a > peak) peak = a;
                if (b > peak) peak = b;
            }
            double mean = n > 0 ? sum / n : 0;
            double lufs = mean > 0 ? -0.691 + 10 * Math.Log10(mean) : -70;
            return (lufs, peak);
        }

        /// <summary>Gain (dB) that brings a measurement to the target
        /// without pushing the peak past the ceiling.</summary>
        public static double GainDbFor(double lufs, double peak)
        {
            double gainDb = Math.Clamp(TargetLufs - lufs, -GainClampDb, GainClampDb);
            double peakDb = peak > 0 ? 20 * Math.Log10(peak) : -80;
            if (peakDb + gainDb > PeakCeilingDb) gainDb = PeakCeilingDb - peakDb;
            return gainDb;
        }

        /// <summary>Cache key for a loaded track: lowercase hex SHA256 of the file bytes.</summary>
        public static string HashBytes(byte[] bytes)
        {
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }
    }

    /// <summary>
    /// Persistent loudness cache (v3.118): one entry per track, keyed by
    /// file hash, in %AppData%\BeatSyncStudioPro\loudness-cache.json —
    /// the same settings-file convention as DjSettings and the key
    /// cache. A track is measured once ever; later loads (either deck)
    /// and the encode option reuse the stored gain. Reads/writes are
    /// locked; a corrupt or missing file just starts empty.
    /// </summary>
    public static class LoudnessCache
    {
        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BeatSyncStudioPro", "loudness-cache.json");

        private static readonly object CacheLock = new object();
        private static Dictionary<string, LoudnessEntry>? _map;

        public static bool TryGet(string key, out LoudnessEntry entry)
        {
            lock (CacheLock)
            {
                var map = LoadLocked();
                return map.TryGetValue(key, out entry!);
            }
        }

        public static void Put(string key, LoudnessEntry entry)
        {
            lock (CacheLock)
            {
                var map = LoadLocked();
                map[key] = entry;
                SaveLocked(map);
            }
        }

        private static Dictionary<string, LoudnessEntry> LoadLocked()
        {
            if (_map != null) return _map;
            try
            {
                if (File.Exists(FilePath))
                {
                    var map = JsonSerializer.Deserialize<Dictionary<string, LoudnessEntry>>(File.ReadAllText(FilePath));
                    if (map != null) { _map = map; return _map; }
                }
            }
            catch { }
            _map = new Dictionary<string, LoudnessEntry>();
            return _map;
        }

        private static void SaveLocked(Dictionary<string, LoudnessEntry> map)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }
}
