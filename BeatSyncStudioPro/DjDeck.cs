using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
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

        /// <summary>Auto-gain trim (v3.68): loudness-match factor the panel
        /// computes at load time; 1.0 = no trim. Applied to every output
        /// sample in Read, upstream of the channel strip's manual gain.</summary>
        public double AutoGain { get; set; } = 1.0;

        // ---- scratch + level (UI-driven) ----
        private volatile bool _grabbed;
        private double _scratchRate;
        private double _releaseRate;
        private double _spinUp = 1;
        private volatile float _level;

        /// <summary>Recent output peak for the beat visuals; reading it decays it.</summary>
        public float Level { get { float v = _level; _level = v * 0.80f; return v; } }

        /// <summary>The hand is on the record: the deck's advance comes from ScratchRate.</summary>
        public void GrabRecord() { _grabbed = true; _scratchRate = 0; lock (_lock) { _fx = DeckFx.None; } }

        // Slip mode (v3.29): while the hand works the record, a shadow clock
        // keeps the song's true place; on release the deck lands there.
        private double _slipPositionFrames;
        private bool _slipping;
        private bool _slipMode;
        public bool SlipMode
        {
            get => _slipMode;
            set { lock (_lock) { _slipMode = value; if (!value) _slipping = false; } }
        }
        public bool IsSlipping { get { lock (_lock) return _slipping; } }

        /// <summary>Hand off: the record spins back up to normal speed from the release rate.</summary>
        public void ReleaseRecord() { _grabbed = false; _releaseRate = _scratchRate; _spinUp = 0; }

        /// <summary>Hand speed in playback-rate units (1 = normal, negative = backwards).</summary>
        public double ScratchRate { set => _scratchRate = value; }

        // ---- one-tap transition FX (v3.90) ----
        // Spinback: a rate script inside Read whips the record backwards
        // (accelerating reverse) and cuts the deck. Echo Out: the deck
        // keeps playing into a beat-synced feedback delay while its dry
        // level fades out, the echoes ring down, then the deck stops.
        private enum DeckFx { None, Spinback, EchoOut }
        private DeckFx _fx = DeckFx.None;
        private long _fxFrames;
        private float[]? _echoBuf;
        private int _echoPtr;

        public bool FxActive { get { lock (_lock) return _fx != DeckFx.None; } }

        public void StartSpinback()
        {
            lock (_lock)
            {
                if (!IsLoaded || !IsPlaying) return;
                _fx = DeckFx.Spinback;
                _fxFrames = 0;
                _spinUp = 1;
                _grabbed = false;
            }
        }

        public void StartEchoOut()
        {
            lock (_lock)
            {
                if (!IsLoaded || !IsPlaying) return;
                double delaySec = Bpm > 0 ? Math.Clamp(60.0 / Bpm * 0.75, 0.08, 1.0) : 0.38;
                int delayFrames = Math.Max(2, (int)(44100 * delaySec));
                _echoBuf = new float[delayFrames * 2];
                _echoPtr = 0;
                _fx = DeckFx.EchoOut;
                _fxFrames = 0;
                _spinUp = 1;
                _grabbed = false;
            }
        }

        public void CancelFx() { lock (_lock) { _fx = DeckFx.None; } }

        private double SpinbackRate(double t)
        {
            if (t < 0.12)
            {
                double k = t / 0.12;
                double start = Tempo * Bend;
                return start + (-1.5 - start) * k;
            }
            if (t < 0.55)
            {
                double k = (t - 0.12) / 0.43;
                return -1.5 + (-6.0 + 1.5) * k;
            }
            return -6.0;
        }

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
                _slipping = false;
                _fx = DeckFx.None;
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
                AutoGain = 1.0;
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
            // grab the reference under the lock, then run the whole pass lock-free:
            // _samples is only ever swapped wholesale (Load/SwapBuffers), never written
            // in place, so the snapshot we hold stays valid while playback continues
            float[] samples;
            lock (_lock) { samples = _samples; }
            int frames = samples.Length / 2;
            var l = new float[frames];
            var r = new float[frames];
            for (int f = 0; f < frames; f++) { l[f] = samples[f * 2]; r[f] = samples[f * 2 + 1]; }
            return (l, r);
        }

        /// <summary>Load the 4 stem WAVs the separator wrote ("name (stem).wav") as this deck's stem buffers.</summary>
        public bool LoadStemsFromFolder(string folder, string baseName)
        {
            try
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
            catch
            {
                return false;
            }
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
            // grab the reference under the lock, then run the whole pass lock-free:
            // _samples is only ever swapped wholesale (Load/SwapBuffers), never written
            // in place, so the snapshot we hold stays valid while playback continues
            float[] samples;
            lock (_lock) { samples = _samples; }
            if (samples.Length == 0 || columns <= 0) return peaks;
            long totalFrames = samples.Length / 2;
            int framesPerColumn = Math.Max(1, (int)(totalFrames / columns));
            for (int c = 0; c < columns; c++)
            {
                long start = (long)c * framesPerColumn;
                long end = Math.Min(totalFrames, start + framesPerColumn);
                float max = 0;
                for (long f = start; f < end; f += 4) // stride for speed on long songs
                {
                    float v = Math.Abs(samples[f * 2]) + Math.Abs(samples[f * 2 + 1]);
                    if (v > max) max = v;
                }
                peaks[c] = Math.Min(1f, max * 0.5f);
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

                if (_grabbed && !_slipping && SlipMode)
                {
                    _slipping = true;
                    _slipPositionFrames = _positionFrames;
                }
                else if (!_grabbed && _slipping)
                {
                    _slipping = false;
                    _positionFrames = Math.Clamp(_slipPositionFrames, 0, Math.Max(0, TotalFrames - 1));
                    _spinUp = 1;   // the song never stopped - no spin-up ramp
                }
                if (_slipping)
                {
                    _slipPositionFrames += frames * Tempo * Bend;
                    if (LoopBeats > 0 && EffectiveBpm > 0 && _loopStartSeconds >= 0)
                    {
                        double loopLenFrames = LoopBeats * 60.0 / EffectiveBpm * 44100.0;
                        double loopStartFrames = _loopStartSeconds * 44100.0;
                        if (_slipPositionFrames - loopStartFrames >= loopLenFrames)
                            _slipPositionFrames = loopStartFrames;
                    }
                    if (_slipPositionFrames > TotalFrames) _slipPositionFrames = TotalFrames;
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
                    if (_fx == DeckFx.Spinback)
                    {
                        double fxT = _fxFrames / 44100.0;
                        if (fxT >= 0.8)
                        {
                            _fx = DeckFx.None;
                            IsPlaying = false;
                            Array.Clear(buffer, offset + f * 2, (frames - f) * 2);
                            return count;
                        }
                        step = SpinbackRate(fxT);
                        _fxFrames++;
                    }
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
                        if (_fx == DeckFx.Spinback) { _fx = DeckFx.None; IsPlaying = false; }
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
                    if (AutoGain != 1.0)
                    {
                        float ag = (float)AutoGain;
                        buffer[offset + f * 2] *= ag;
                        buffer[offset + f * 2 + 1] *= ag;
                    }
                    float a0 = Math.Abs(buffer[offset + f * 2]);
                    float a1 = Math.Abs(buffer[offset + f * 2 + 1]);
                    if (a0 > peak) peak = a0;
                    if (a1 > peak) peak = a1;
                    _positionFrames += step;
                }
                if (_fx == DeckFx.EchoOut && _echoBuf != null)
                {
                    bool stopNow = false;
                    for (int f = 0; f < frames; f++)
                    {
                        double fxT = _fxFrames / 44100.0;
                        if (fxT >= 2.8)
                        {
                            Array.Clear(buffer, offset + f * 2, (frames - f) * 2);
                            stopNow = true;
                            break;
                        }
                        float feed = fxT < 0.30 ? (float)(1.0 - fxT / 0.30) : 0f;
                        for (int ch = 0; ch < 2; ch++)
                        {
                            int bi = offset + f * 2 + ch;
                            float inp = buffer[bi] * feed;
                            float d = _echoBuf[_echoPtr + ch];
                            _echoBuf[_echoPtr + ch] = inp + d * 0.55f;
                            buffer[bi] = inp + d;
                        }
                        _echoPtr += 2;
                        if (_echoPtr >= _echoBuf.Length) _echoPtr = 0;
                        _fxFrames++;
                    }
                    if (stopNow) { _fx = DeckFx.None; IsPlaying = false; }
                }
                if (peak > _level) _level = peak;
                return count;
            }
        }
    }

    /// <summary>
    /// Pass-through that writes everything passing through it to a WAV file while armed.
    /// A dedicated background thread owns the disk I/O: Read only queues a copy of each
    /// block, so no disk work and no disk failure can ever reach the audio callback.
    /// </summary>
    public class RecordingSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly ConcurrentQueue<float[]> _pending = new ConcurrentQueue<float[]>();
        private WaveFileWriter? _writer;   // owned by the writer thread once Start hands it over
        private Thread? _writerThread;
        private volatile bool _recording;
        private volatile bool _stopRequested;

        public RecordingSampleProvider(ISampleProvider source)
        {
            _source = source;
            WaveFormat = source.WaveFormat;
        }

        public WaveFormat WaveFormat { get; }
        public bool IsRecording => _recording;

        public void Start(string path)
        {
            Stop();                                   // start() while already recording = stop first
            _pending.Clear();
            _writer = new WaveFileWriter(path, WaveFormat);
            _stopRequested = false;
            _recording = true;
            _writerThread = new Thread(WriterLoop) { IsBackground = true };
            _writerThread.Start();
        }

        public void Stop()
        {
            if (!_recording) return;
            _stopRequested = true;                    // the writer thread drains the queue, then exits
            try { _writerThread?.Join(2000); } catch { }   // bounded wait - never hang on a stuck writer
            try { _writer?.Dispose(); } catch { }     // safe: the writer thread is done with it
            _writer = null;
            _recording = false;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            if (_recording && read > 0)
            {
                try
                {
                    var block = new float[read];      // copy the just-read samples and queue them...
                    Array.Copy(buffer, offset, block, 0, read);
                    _pending.Enqueue(block);          // ...no disk I/O and nothing throws here
                }
                catch { }
            }
            return read;
        }

        private void WriterLoop()
        {
            // keep going until a stop is requested AND everything queued is on disk
            while (!_stopRequested || !_pending.IsEmpty)
            {
                if (_pending.TryDequeue(out var block))
                {
                    try { _writer?.WriteSamples(block, 0, block.Length); }
                    catch
                    {
                        // disk full, drive removed, ...: end recording quietly instead of
                        // letting the failure propagate out of the audio callback
                        try { _writer?.Dispose(); } catch { }
                        _writer = null;
                        _stopRequested = true;
                        _recording = false;
                        return;
                    }
                }
                else Thread.Sleep(5);
            }
        }
    }
}
