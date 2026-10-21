using System;
using NAudio.Dsp;
using NAudio.Wave;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// Per-track effect settings. One instance per mixer chain, shared live
    /// between the UI and the audio providers — moving a slider in the FX
    /// window changes the sound immediately, no re-wiring.
    /// </summary>
    public class TrackFxSettings
    {
        public bool EqOn { get; set; } = true;
        public double LowDb { get; set; }
        public double MidDb { get; set; }
        public double HighDb { get; set; }

        public bool DelayOn { get; set; }
        public double DelayMs { get; set; } = 375;
        public double DelayFeedback { get; set; } = 0.35;
        public double DelayMix { get; set; } = 0.25;

        public bool ReverbOn { get; set; }
        public double ReverbSize { get; set; } = 0.7;
        public double ReverbDamp { get; set; } = 0.5;
        public double ReverbMix { get; set; } = 0.3;
    }

    /// <summary>
    /// Stereo balance/pan for stereo sources. NAudio's PanningSampleProvider
    /// only accepts mono input and throws on stereo — this is the stereo-safe
    /// equivalent used by the mixer chains.
    /// </summary>
    public class StereoPanSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;

        public StereoPanSampleProvider(ISampleProvider source)
        {
            _source = source;
            WaveFormat = source.WaveFormat;
        }

        public WaveFormat WaveFormat { get; }

        /// <summary>-1 = full left, 0 = center, +1 = full right.</summary>
        public float Pan { get; set; }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            float pan = Math.Clamp(Pan, -1f, 1f);
            if (Math.Abs(pan) < 0.001f || WaveFormat.Channels != 2) return read;

            float leftGain = pan <= 0 ? 1f : 1f - pan;
            float rightGain = pan >= 0 ? 1f : 1f + pan;
            for (int i = 0; i + 1 < read; i += 2)
            {
                buffer[offset + i] *= leftGain;
                buffer[offset + i + 1] *= rightGain;
            }
            return read;
        }
    }

    /// <summary>Three-band EQ (low shelf / peaking / high shelf) built on NAudio biquads.</summary>
    public class EqSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly TrackFxSettings _settings;
        private BiQuadFilter[] _low = Array.Empty<BiQuadFilter>();
        private BiQuadFilter[] _mid = Array.Empty<BiQuadFilter>();
        private BiQuadFilter[] _high = Array.Empty<BiQuadFilter>();
        private double _lastLow = double.NaN, _lastMid = double.NaN, _lastHigh = double.NaN;

        public EqSampleProvider(ISampleProvider source, TrackFxSettings settings)
        {
            _source = source;
            _settings = settings;
            WaveFormat = source.WaveFormat;
            BuildFilters();
        }

        public WaveFormat WaveFormat { get; }

        private void BuildFilters()
        {
            int channels = WaveFormat.Channels;
            int sr = WaveFormat.SampleRate;
            _low = new BiQuadFilter[channels];
            _mid = new BiQuadFilter[channels];
            _high = new BiQuadFilter[channels];
            for (int c = 0; c < channels; c++)
            {
                _low[c] = BiQuadFilter.LowShelf(sr, 200, 1.0f, (float)_settings.LowDb);
                _mid[c] = BiQuadFilter.PeakingEQ(sr, 1000, 0.8f, (float)_settings.MidDb);
                _high[c] = BiQuadFilter.HighShelf(sr, 4000, 1.0f, (float)_settings.HighDb);
            }
            _lastLow = _settings.LowDb;
            _lastMid = _settings.MidDb;
            _lastHigh = _settings.HighDb;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            if (!_settings.EqOn) return read;

            if (Math.Abs(_settings.LowDb - _lastLow) > 0.01 ||
                Math.Abs(_settings.MidDb - _lastMid) > 0.01 ||
                Math.Abs(_settings.HighDb - _lastHigh) > 0.01)
            {
                BuildFilters();
            }

            int channels = WaveFormat.Channels;
            for (int i = 0; i < read; i++)
            {
                int ch = i % channels;
                float s = _low[ch].Transform(buffer[offset + i]);
                s = _mid[ch].Transform(s);
                buffer[offset + i] = _high[ch].Transform(s);
            }
            return read;
        }
    }

    /// <summary>Simple feedback delay (echo).</summary>
    public class DelaySampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly TrackFxSettings _settings;
        private readonly float[] _buffer;
        private int _writePos;

        public DelaySampleProvider(ISampleProvider source, TrackFxSettings settings)
        {
            _source = source;
            _settings = settings;
            WaveFormat = source.WaveFormat;
            _buffer = new float[WaveFormat.SampleRate * 2 * WaveFormat.Channels]; // up to 2 seconds
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            if (!_settings.DelayOn) return read;

            int channels = WaveFormat.Channels;
            int delaySamples = (int)(_settings.DelayMs / 1000.0 * WaveFormat.SampleRate) * channels;
            delaySamples = Math.Clamp(delaySamples, channels, _buffer.Length - channels);
            float feedback = (float)Math.Clamp(_settings.DelayFeedback, 0, 0.85);
            float mix = (float)Math.Clamp(_settings.DelayMix, 0, 1);

            for (int i = 0; i < read; i++)
            {
                int readPos = _writePos - delaySamples;
                if (readPos < 0) readPos += _buffer.Length;
                float delayed = _buffer[readPos];
                float input = buffer[offset + i];
                buffer[offset + i] = input * (1 - mix) + delayed * mix;
                _buffer[_writePos] = input + delayed * feedback;
                _writePos = (_writePos + 1) % _buffer.Length;
            }
            return read;
        }
    }

    /// <summary>Freeverb-style algorithmic reverb (8 comb filters + 4 allpasses per channel).</summary>
    public class ReverbSampleProvider : ISampleProvider
    {
        private class Comb
        {
            private readonly float[] _buf;
            private int _idx;
            private float _store;

            public Comb(int size) { _buf = new float[size]; }

            public float Process(float input, float feedback, float damp)
            {
                float output = _buf[_idx];
                _store = output * (1 - damp) + _store * damp;
                _buf[_idx] = input + _store * feedback;
                _idx = (_idx + 1) % _buf.Length;
                return output;
            }
        }

        private class AllPass
        {
            private readonly float[] _buf;
            private int _idx;

            public AllPass(int size) { _buf = new float[size]; }

            public float Process(float input)
            {
                float bufOut = _buf[_idx];
                float output = -input + bufOut;
                _buf[_idx] = input + bufOut * 0.5f;
                _idx = (_idx + 1) % _buf.Length;
                return output;
            }
        }

        private static readonly int[] CombTunings = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
        private static readonly int[] AllPassTunings = { 556, 441, 341, 225 };
        private const int StereoSpread = 23;

        private readonly ISampleProvider _source;
        private readonly TrackFxSettings _settings;
        private readonly Comb[][] _combs;
        private readonly AllPass[][] _allPass;

        public ReverbSampleProvider(ISampleProvider source, TrackFxSettings settings)
        {
            _source = source;
            _settings = settings;
            WaveFormat = source.WaveFormat;
            int channels = WaveFormat.Channels;
            double scale = WaveFormat.SampleRate / 44100.0;
            _combs = new Comb[channels][];
            _allPass = new AllPass[channels][];
            for (int c = 0; c < channels; c++)
            {
                int spread = c == 1 ? StereoSpread : 0;
                _combs[c] = new Comb[CombTunings.Length];
                for (int i = 0; i < CombTunings.Length; i++)
                    _combs[c][i] = new Comb((int)((CombTunings[i] + spread) * scale));
                _allPass[c] = new AllPass[AllPassTunings.Length];
                for (int i = 0; i < AllPassTunings.Length; i++)
                    _allPass[c][i] = new AllPass((int)((AllPassTunings[i] + spread) * scale));
            }
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            if (!_settings.ReverbOn) return read;

            int channels = WaveFormat.Channels;
            float feedback = (float)(0.7 + Math.Clamp(_settings.ReverbSize, 0, 1) * 0.28);
            float damp = (float)(Math.Clamp(_settings.ReverbDamp, 0, 1) * 0.4);
            float mix = (float)Math.Clamp(_settings.ReverbMix, 0, 1);

            for (int i = 0; i < read; i++)
            {
                int c = i % channels;
                float input = buffer[offset + i];
                float wet = 0;
                foreach (var comb in _combs[c]) wet += comb.Process(input * 0.5f, feedback, damp);
                wet *= 0.2f;
                foreach (var ap in _allPass[c]) wet = ap.Process(wet);
                buffer[offset + i] = input * (1 - mix) + wet * mix * 2f;
            }
            return read;
        }
    }

    // =========================
    // MONITOR BUS (what you hear — never printed to exports)
    // =========================

    /// <summary>Monitor chain settings: 3-band EQ (reuses TrackFxSettings) + compressor + stereo width.</summary>
    public class MonitorFxSettings
    {
        public TrackFxSettings Eq { get; } = new TrackFxSettings();
        public bool CompOn { get; set; }
        public double CompThresholdDb { get; set; } = -18;
        public double CompRatio { get; set; } = 3;
        public double CompMakeupDb { get; set; }
        public double Width { get; set; } = 1.0;
    }

    /// <summary>Simple feed-forward compressor for the monitor bus.</summary>
    public class CompressorSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly MonitorFxSettings _settings;
        private double _env;
        private double _gainSmooth = 1.0;

        public CompressorSampleProvider(ISampleProvider source, MonitorFxSettings settings)
        {
            _source = source;
            _settings = settings;
            WaveFormat = source.WaveFormat;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            if (!_settings.CompOn) return read;
            int channels = WaveFormat.Channels;
            int sr = WaveFormat.SampleRate;
            double attack = Math.Exp(-1.0 / (0.003 * sr));
            double release = Math.Exp(-1.0 / (0.240 * sr));
            double thr = Math.Pow(10, _settings.CompThresholdDb / 20.0);
            double ratio = Math.Max(1.0, _settings.CompRatio);
            double makeup = Math.Pow(10, _settings.CompMakeupDb / 20.0);
            int frames = read / channels;
            for (int f = 0; f < frames; f++)
            {
                double peak = 0;
                for (int c = 0; c < channels; c++)
                    peak = Math.Max(peak, Math.Abs(buffer[offset + f * channels + c]));
                double coeff = peak > _env ? attack : release;
                _env = coeff * _env + (1 - coeff) * peak;
                double targetGain = 1.0;
                if (_env > thr && _env > 1e-6)
                    targetGain = Math.Pow(thr / _env, 1.0 - 1.0 / ratio);
                _gainSmooth += (targetGain - _gainSmooth) * 0.02;
                double g = _gainSmooth * makeup;
                for (int c = 0; c < channels; c++)
                    buffer[offset + f * channels + c] *= (float)g;
            }
            return read;
        }
    }

    /// <summary>Mid/side stereo width for the monitor bus (1.0 = unchanged, 0 = mono, 2 = extra wide).</summary>
    public class StereoWidthSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly MonitorFxSettings _settings;

        public StereoWidthSampleProvider(ISampleProvider source, MonitorFxSettings settings)
        {
            _source = source;
            _settings = settings;
            WaveFormat = source.WaveFormat;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            double w = _settings.Width;
            if (Math.Abs(w - 1.0) < 0.001 || WaveFormat.Channels != 2) return read;
            for (int i = 0; i + 1 < read; i += 2)
            {
                double l = buffer[offset + i], r = buffer[offset + i + 1];
                double mid = (l + r) * 0.5, side = (l - r) * 0.5 * w;
                buffer[offset + i] = (float)(mid + side);
                buffer[offset + i + 1] = (float)(mid - side);
            }
            return read;
        }
    }
}
