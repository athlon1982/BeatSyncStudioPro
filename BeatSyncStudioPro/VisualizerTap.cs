using System;
using NAudio.Dsp;
using NAudio.Wave;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// A pass-through on the master bus. It keeps a rolling buffer of the
    /// most recent samples and serves FFT spectrum bands, a scope waveform
    /// and level readings to the Output window's visualizations. It never
    /// alters the audio passing through it.
    /// </summary>
    public class AudioTapSampleProvider : ISampleProvider
    {
        private const int FftSize = 2048;
        private const int MaxBands = 64;

        private readonly ISampleProvider _source;
        private readonly float[] _ring = new float[FftSize];
        private int _writePos;
        private readonly object _lock = new object();

        // v3.56: a one-second interleaved-stereo ring for projectM's PCM
        // feed. Consumers track the frame counter and pull only the frames
        // they haven't fed yet (see ReadStereo).
        private const int StereoCapacityFrames = 44100;
        private readonly float[] _stereoRing = new float[StereoCapacityFrames * 2];
        private long _stereoWritten;
        private readonly float[] _smooth = new float[MaxBands];
        private DateTime _lastFft = DateTime.MinValue;

        public AudioTapSampleProvider(ISampleProvider source)
        {
            _source = source;
            WaveFormat = source.WaveFormat;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            lock (_lock)
            {
                for (int i = 0; i + 1 < read; i += 2)
                {
                    _ring[_writePos] = (buffer[offset + i] + buffer[offset + i + 1]) * 0.5f;
                    _writePos = (_writePos + 1) % FftSize;
                }
                int channels = WaveFormat.Channels;
                if (channels >= 1)
                {
                    int frames = read / channels;
                    for (int f = 0; f < frames; f++)
                    {
                        float l = buffer[offset + f * channels];
                        float r = channels > 1 ? buffer[offset + f * channels + 1] : l;
                        int pos = (int)(_stereoWritten % StereoCapacityFrames) * 2;
                        _stereoRing[pos] = l;
                        _stereoRing[pos + 1] = r;
                        _stereoWritten++;
                    }
                }
            }
            return read;
        }

        /// <summary>Total stereo frames captured since startup (monotonic).</summary>
        public long StereoFramesWritten
        {
            get { lock (_lock) return _stereoWritten; }
        }

        /// <summary>
        /// Copy up to maxFrames interleaved stereo frames starting at
        /// startFrame into dest (sized 2*maxFrames). Returns the number of
        /// frames actually copied; 0 when startFrame is already caught up.
        /// A startFrame older than the ring still holds clamps forward.
        /// </summary>
        public int ReadStereo(long startFrame, float[] dest, int maxFrames)
        {
            lock (_lock)
            {
                long avail = _stereoWritten - startFrame;
                if (avail <= 0) return 0;
                if (avail > StereoCapacityFrames)
                {
                    startFrame = _stereoWritten - StereoCapacityFrames;
                    avail = StereoCapacityFrames;
                }
                int n = (int)Math.Min(avail, maxFrames);
                for (int f = 0; f < n; f++)
                {
                    int pos = (int)((startFrame + f) % StereoCapacityFrames) * 2;
                    dest[f * 2] = _stereoRing[pos];
                    dest[f * 2 + 1] = _stereoRing[pos + 1];
                }
                return n;
            }
        }

        private float[] Snapshot()
        {
            var snap = new float[FftSize];
            lock (_lock)
            {
                for (int i = 0; i < FftSize; i++)
                    snap[i] = _ring[(_writePos + i) % FftSize];
            }
            return snap;
        }

        /// <summary>Peak level of the recent audio (0..1).</summary>
        public float Level
        {
            get
            {
                var s = Snapshot();
                float peak = 0;
                for (int i = 0; i < s.Length; i++)
                {
                    float a = Math.Abs(s[i]);
                    if (a > peak) peak = a;
                }
                return peak;
            }
        }

        /// <summary>Low-band energy — drives the pulse scenes.</summary>
        public float BassLevel
        {
            get
            {
                var bands = GetSpectrum(MaxBands);
                float sum = 0;
                for (int i = 0; i < 4 && i < bands.Length; i++) sum += bands[i];
                return sum / 4f;
            }
        }

        /// <summary>Scope waveform: one signed value per requested point (range roughly -1..1).</summary>
        public float[] GetWaveform(int points)
        {
            var s = Snapshot();
            var wave = new float[points];
            int slice = Math.Max(1, FftSize / points);
            for (int p = 0; p < points; p++)
            {
                int idx = Math.Min(FftSize - 1, p * slice + slice / 2);
                wave[p] = s[idx];
            }
            return wave;
        }

        /// <summary>Log-spaced, smoothed spectrum bands (0..1). Recomputes at most every 25 ms.</summary>
        public float[] GetSpectrum(int bands)
        {
            bands = Math.Min(bands, MaxBands);
            if ((DateTime.Now - _lastFft).TotalMilliseconds < 25)
                return CopyBands(bands);
            _lastFft = DateTime.Now;

            var snap = Snapshot();
            var complex = new Complex[FftSize];
            for (int i = 0; i < FftSize; i++)
            {
                double hann = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (FftSize - 1)));
                complex[i].X = (float)(snap[i] * hann);
                complex[i].Y = 0;
            }
            FastFourierTransform.FFT(true, 11, complex);

            double binHz = 44100.0 / FftSize;
            double minFreq = 30, maxFreq = 16000;
            for (int b = 0; b < bands; b++)
            {
                double f0 = minFreq * Math.Pow(maxFreq / minFreq, (double)b / bands);
                double f1 = minFreq * Math.Pow(maxFreq / minFreq, (double)(b + 1) / bands);
                int i0 = Math.Max(1, (int)(f0 / binHz));
                int i1 = Math.Min(FftSize / 2 - 1, Math.Max(i0 + 1, (int)(f1 / binHz)));
                double sum = 0;
                for (int i = i0; i < i1; i++)
                    sum += Math.Sqrt(complex[i].X * complex[i].X + complex[i].Y * complex[i].Y);
                double mag = sum / (i1 - i0) / (FftSize / 4.0);
                float value = (float)Math.Clamp(Math.Sqrt(mag) * 1.4, 0, 1);
                // fast attack, slower release
                _smooth[b] = value > _smooth[b] ? value : _smooth[b] + (value - _smooth[b]) * 0.3f;
            }
            return CopyBands(bands);
        }

        private float[] CopyBands(int bands)
        {
            var copy = new float[bands];
            Array.Copy(_smooth, copy, bands);
            return copy;
        }
    }
}
