using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// One DJ deck. The whole song is decoded into memory (44100 Hz stereo),
    /// and playback is a fractional-position read with linear interpolation —
    /// the tempo fader changes speed and pitch together, like vinyl.
    /// Always returns the full requested count (silence when paused/stopped),
    /// so it can sit inside a ReadFully mixer.
    /// </summary>
    public class DjDeck : ISampleProvider
    {
        private readonly object _lock = new object();
        private float[] _samples = Array.Empty<float>();
        private double _positionFrames;
        private double _loopStartSeconds = -1;

        public string Title { get; private set; } = "";

        // Stem playback (v3.25): 4 interleaved buffers in StemSeparator.StemNames
        // order, played through the SAME position clock as the full mix, so
        // tempo, loops and scratching stay sample-accurate. Per-stem gains ramp
        // click-free toward their toggle targets inside Read.
        private float[][]? _stems;
        private readonly float[] _stemTarget = { 1f, 1f, 1f, 1f };
        private readonly float[] _stemGain = { 1f, 1f, 1f, 1f };

        public bool StemModeActive { get { lock (_lock) { return _stems != null; } } }

        /// <summary>Bumps on every Load, so async work (key shifts) can tell the track changed under it.</summary>
        public int LoadGeneration { get; private set; }

        public float[] SnapshotInterleaved()
        {
            lock (_lock) { var copy = new float[_samples.Length]; Array.Copy(_samples, copy, _samples.Length); return copy; }
        }

        public float[][]? SnapshotStemsInterleaved()
        {
            lock (_lock)
            {
                if (_stems == null) return null;
                var result = new float[4][];
                for (int i = 0; i < 4; i++) { result[i] = new float[_stems[i].Length]; Array.Copy(_stems[i], result[i], _stems[i].Length); }
                return result;
            }
        }

        /// <summary>Swap playback buffers (key-shifted copies) without touching position or play state.</summary>
        public void SwapBuffers(float[] newSamples, float[][]? newStems)
        {
            lock (_lock)
            {
                _samples = newSamples;
                if (newStems != null && _stems != null) _stems = newStems;
                if (_positionFrames > TotalFrames) _positionFrames = TotalFrames;
            }
        }
        public bool IsLoaded => _samples.Length > 0;
        public bool IsPlaying { get; set; }

        /// <summary>Tempo fader multiplier (0.92–1.08).</summary>
        public double Tempo { get; set; } = 1.0;
        /// <summary>Momentary pitch-bend multiplier while a bend button is held.</summary>
        public double Bend { get; set; } = 1.0;
        /// <summary>The song's BPM (0 = unknown; set by typing or tapping).</summary>
        public double Bpm { get; set; }
        public double EffectiveBpm => Bpm > 0 ? Bpm * Tempo : 0;

        // ---- scratch + level (UI-driven) ----
        private volatile bool _grabbed;
        private double _scratchRate;
        private double _releaseRate;
        private double _spinUp = 1;
        private volatile float _level;

        /// <summary>Recent output peak for the beat visuals; reading it decays it.</summary>
        public float Level { get { float v = _level; _level = v * 0.80f; return v; } }

        /// <summary>The hand is on the record: the deck's advance comes from ScratchRate.</summary>
        public void GrabRecord() { _grabbed = true; _scratchRate = 0; }

        /// <summary>Hand off: the record spins back up to normal speed from the release rate.</summary>
        public void ReleaseRecord() { _grabbed = false; _releaseRate = _scratchRate; _spinUp = 0; }

        /// <summary>Hand speed in playback-rate units (1 = normal, negative = backwards).</summary>
        public double ScratchRate { set => _scratchRate = value; }

        public double CueSeconds { get; set; }
        public double?[] HotCues { get; } = new double?[4];
        /// <summary>Active loop length in beats (0 = no loop).</summary>
        public int LoopBeats { get; private set; }

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

        private long TotalFrames => _samples.Length / 2;
        public double TotalSeconds => TotalFrames / 44100.0;

        public double PositionSeconds
        {
            get { lock (_lock) return _positionFrames / 44100.0; }
            set { lock (_lock) _positionFrames = Math.Clamp(value, 0, Math.Max(0, TotalSeconds - 0.01)) * 44100.0; }
        }

        public void Load(byte[] bytes, string filename)
        {
            var decoded = Decode(bytes, filename);
            lock (_lock)
            {
                _samples = decoded;
                LoadGeneration++;
                _stems = null;
                for (int i = 0; i < 4; i++) { _stemTarget[i] = 1f; _stemGain[i] = 1f; }
                Title = Path.GetFileNameWithoutExtension(filename);
                _positionFrames = 0;
                IsPlaying = false;
                CueSeconds = 0;
                Array.Clear(HotCues, 0, HotCues.Length);
                LoopBeats = 0;
                _loopStartSeconds = -1;
                Bpm = 0;
                Tempo = 1.0;
                Bend = 1.0;
            }
        }

        /// <summary>Hand the deck 4 stem buffers (StemSeparator.StemNames order); they take over playback on the same clock.</summary>
        public void LoadStems(float[][] stems)
        {
            lock (_lock)
            {
                if (stems.Length != 4) { _stems = null; return; }
                foreach (var b in stems) if (b == null || b.Length < 4) { _stems = null; return; }
                // stems must belong to the currently loaded track (guards a split
                // finishing after the DJ already loaded a different song)
                if (_samples.Length > 0)
                    foreach (var b in stems)
                        if (Math.Abs(b.Length - _samples.Length) > _samples.Length / 20) { _stems = null; return; }
                _stems = stems;
                for (int i = 0; i < 4; i++) { _stemTarget[i] = 1f; _stemGain[i] = 1f; }
            }
        }

        public void ClearStems() { lock (_lock) { _stems = null; } }

        public void SetStemOn(int stemIndex, bool on)
        {
            if (stemIndex < 0 || stemIndex > 3) return;
            _stemTarget[stemIndex] = on ? 1f : 0f;
        }

        /// <summary>The loaded track decoded to separate channels - the input StemSeparator.SplitAsync wants.</summary>
        public (float[] Left, float[] Right) GetChannelBuffers()
        {
            lock (_lock)
            {
                int frames = _samples.Length / 2;
                var l = new float[frames];
                var r = new float[frames];
                for (int f = 0; f < frames; f++) { l[f] = _samples[f * 2]; r[f] = _samples[f * 2 + 1]; }
                return (l, r);
            }
        }

        /// <summary>Load the 4 stem WAVs the separator wrote ("name (stem).wav") as this deck's stem buffers.</summary>
        public bool LoadStemsFromFolder(string folder, string baseName)
        {
            var stems = new float[4][];
            for (int s = 0; s < 4; s++)
            {
                string p = Path.Combine(folder, baseName + " (" + StemSeparator.StemNames[s] + ").wav");
                if (!File.Exists(p)) return false;
                using var reader = new WaveFileReader(p);
                stems[s] = ReadAll(reader);
            }
            LoadStems(stems);
            return true;
        }

        private static float[] Decode(byte[] bytes, string filename)
        {
            string lower = filename.ToLowerInvariant();
            if (lower.EndsWith(".wav"))
                return ReadAll(new WaveFileReader(new MemoryStream(bytes)));
            // Media Foundation first (broad format support, no ACM codec
            // dependency); the classic MP3 reader is the fallback.
            string ext = Path.GetExtension(lower);
            string temp = Path.Combine(Path.GetTempPath(), "beatsync-deck-" + Guid.NewGuid().ToString("N") + ext);
            try
            {
                File.WriteAllBytes(temp, bytes);
                return ReadAll(new MediaFoundationReader(temp));
            }
            catch
            {
                return ReadAll(new Mp3FileReader(new MemoryStream(bytes)));
            }
            finally
            {
                try { File.Delete(temp); } catch { }
            }
        }

        private static float[] ReadAll(IWaveProvider reader)
        {
            try
            {
                ISampleProvider provider = new WdlResamplingSampleProvider(reader.ToSampleProvider(), 44100).ToStereo();
                var all = new List<float>();
                var buffer = new float[44100 * 2];
                int read;
                while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
                {
                    for (int i = 0; i < read; i++) all.Add(buffer[i]);
                }
                if (all.Count < 4410)
                    throw new InvalidOperationException("That file decoded to almost no audio — it may be corrupt or an unsupported format.");
                return all.ToArray();
            }
            finally
            {
                (reader as IDisposable)?.Dispose();
            }
        }

        /// <summary>Peak (max abs, mono-summed) per column for the waveform display.</summary>
        public float[] ComputePeaks(int columns)
        {
            var peaks = new float[columns];
            lock (_lock)
            {
                if (_samples.Length == 0 || columns <= 0) return peaks;
                int framesPerColumn = Math.Max(1, (int)(TotalFrames / columns));
                for (int c = 0; c < columns; c++)
                {
                    long start = (long)c * framesPerColumn;
                    long end = Math.Min(TotalFrames, start + framesPerColumn);
                    float max = 0;
                    for (long f = start; f < end; f += 4) // stride for speed on long songs
                    {
                        float v = Math.Abs(_samples[f * 2]) + Math.Abs(_samples[f * 2 + 1]);
                        if (v > max) max = v;
                    }
                    peaks[c] = Math.Min(1f, max * 0.5f);
                }
            }
            return peaks;
        }

        public void BeginLoop(int beats)
        {
            lock (_lock)
            {
                LoopBeats = beats;
                _loopStartSeconds = _positionFrames / 44100.0;
            }
        }

        public void StopLoop()
        {
            lock (_lock)
            {
                LoopBeats = 0;
                _loopStartSeconds = -1;
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / 2;
            lock (_lock)
            {
                if (!IsPlaying || _samples.Length == 0)
                {
                    Array.Clear(buffer, offset, count);
                    return count;
                }

                double step;
                if (_grabbed)
                {
                    if (Math.Abs(_scratchRate) < 0.02)
                    {
                        // hand holding the record still = silence
                        Array.Clear(buffer, offset, count);
                        return count;
                    }
                    step = _scratchRate;
                }
                else if (_spinUp < 1)
                {
                    _spinUp = Math.Min(1, _spinUp + frames / 44100.0 / 0.3);
                    step = _releaseRate + (Tempo * Bend - _releaseRate) * _spinUp;
                }
                else step = Tempo * Bend;

                float peak = 0;
                for (int f = 0; f < frames; f++)
                {
                    if (!_grabbed && LoopBeats > 0 && EffectiveBpm > 0 && _loopStartSeconds >= 0)
                    {
                        double loopLen = LoopBeats * 60.0 / EffectiveBpm;
                        if (_positionFrames / 44100.0 - _loopStartSeconds >= loopLen)
                            _positionFrames = _loopStartSeconds * 44100.0;
                    }

                    long idx = (long)_positionFrames;
                    if (idx >= TotalFrames)
                    {
                        IsPlaying = false;
                        Array.Clear(buffer, offset + f * 2, (frames - f) * 2);
                        return count;
                    }
                    if (idx < 0)
                    {
                        // scratched back past the start
                        _positionFrames = 0;
                        Array.Clear(buffer, offset + f * 2, (frames - f) * 2);
                        return count;
                    }

                    double frac = _positionFrames - idx;
                    int i0 = (int)(idx * 2);
                    int i1 = Math.Min(i0 + 2, _samples.Length - 2);
                    if (_stems != null)
                    {
                        double mixL = 0, mixR = 0;
                        for (int s = 0; s < 4; s++)
                        {
                            float g = _stemGain[s];
                            float tg = _stemTarget[s];
                            if (g < tg) g = Math.Min(tg, g + 0.004f);
                            else if (g > tg) g = Math.Max(tg, g - 0.004f);
                            _stemGain[s] = g;
                            if (g <= 0.0001f) continue;
                            float[] sb = _stems[s];
                            int si0 = Math.Min(i0, sb.Length - 2);
                            int si1 = Math.Min(si0 + 2, sb.Length - 2);
                            mixL += g * (sb[si0] * (1 - frac) + sb[si1] * frac);
                            mixR += g * (sb[si0 + 1] * (1 - frac) + sb[si1 + 1] * frac);
                        }
                        buffer[offset + f * 2] = (float)mixL;
                        buffer[offset + f * 2 + 1] = (float)mixR;
                    }
                    else
                    {
                        buffer[offset + f * 2] = (float)(_samples[i0] * (1 - frac) + _samples[i1] * frac);
                        buffer[offset + f * 2 + 1] = (float)(_samples[i0 + 1] * (1 - frac) + _samples[i1 + 1] * frac);
                    }
                    float a0 = Math.Abs(buffer[offset + f * 2]);
                    float a1 = Math.Abs(buffer[offset + f * 2 + 1]);
                    if (a0 > peak) peak = a0;
                    if (a1 > peak) peak = a1;
                    _positionFrames += step;
                }
                if (peak > _level) _level = peak;
                return count;
            }
        }
    }

    /// <summary>Pass-through that writes everything passing through it to a WAV file while armed.</summary>
    public class RecordingSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private WaveFileWriter? _writer;

        public RecordingSampleProvider(ISampleProvider source)
        {
            _source = source;
            WaveFormat = source.WaveFormat;
        }

        public WaveFormat WaveFormat { get; }
        public bool IsRecording => _writer != null;

        public void Start(string path)
        {
            Stop();
            _writer = new WaveFileWriter(path, WaveFormat);
        }

        public void Stop()
        {
            _writer?.Dispose();
            _writer = null;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            _writer?.WriteSamples(buffer, offset, read);
            return read;
        }
    }
}
