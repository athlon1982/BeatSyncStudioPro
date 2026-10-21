using System;
using System.Collections.Generic;
using NAudio.Wave;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// Plays a sample provider back at a variable speed using linear
    /// interpolation. Pitch shifts along with the speed — like speeding
    /// up or slowing down a record. The source must already be at the
    /// mixer's format (44100 Hz stereo); format conversion happens
    /// before this provider in the chain.
    /// </summary>
    public class SpeedSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly double _speed;
        private readonly List<float> _buffer = new List<float>();
        private double _sourcePosition; // in frames
        private bool _sourceExhausted;

        public SpeedSampleProvider(ISampleProvider source, double speed)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _speed = Math.Clamp(speed, 0.05, 8.0);
            WaveFormat = source.WaveFormat;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int channels = WaveFormat.Channels;
            int framesRequested = count / channels;
            int framesWritten = 0;

            while (framesWritten < framesRequested)
            {
                // Drop long-consumed frames so the buffer stays small.
                int floorPos = (int)Math.Floor(_sourcePosition);
                if (floorPos > 8192)
                {
                    _buffer.RemoveRange(0, floorPos * channels);
                    _sourcePosition -= floorPos;
                    floorPos = 0;
                }

                int needFrame = (int)Math.Ceiling(_sourcePosition) + 1;
                if (!EnsureBuffered(needFrame)) break; // source finished

                int i0 = (int)Math.Floor(_sourcePosition);
                double frac = _sourcePosition - i0;
                int lastFrame = _buffer.Count / channels - 1;

                for (int ch = 0; ch < channels; ch++)
                {
                    float s0 = _buffer[i0 * channels + ch];
                    float s1 = _buffer[Math.Min(i0 + 1, lastFrame) * channels + ch];
                    buffer[offset + framesWritten * channels + ch] = (float)(s0 + (s1 - s0) * frac);
                }

                framesWritten++;
                _sourcePosition += _speed;
            }

            return framesWritten * channels;
        }

        private bool EnsureBuffered(int frameIndex)
        {
            int channels = WaveFormat.Channels;
            while (!_sourceExhausted && _buffer.Count / channels <= frameIndex)
            {
                float[] chunk = new float[4096 * channels];
                int read = _source.Read(chunk, 0, chunk.Length);
                if (read <= 0)
                {
                    _sourceExhausted = true;
                    break;
                }
                for (int i = 0; i < read; i++) _buffer.Add(chunk[i]);
            }
            return _buffer.Count / channels > frameIndex;
        }
    }
}
