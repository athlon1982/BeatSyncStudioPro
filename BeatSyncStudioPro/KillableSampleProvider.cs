using System;
using NAudio.Wave;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// Wraps a timeline voice so Stop can actually silence it. Kill()
    /// starts a ~30 ms fade (no click) and then ends the stream, which
    /// also makes the mixer drop the input. Without this, one-shot clips
    /// handed to the mixer always ring out to their natural end.
    /// </summary>
    public class KillableSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _inner;
        private volatile bool _killed;
        private int _fadeLeft = -1; // samples remaining in the kill fade
        private float _fadeStep;
        private float _fadeGain = 1f;

        /// <summary>Raised once when the wrapped stream ends (naturally or killed).</summary>
        public event Action? Ended;

        public KillableSampleProvider(ISampleProvider inner) => _inner = inner;

        public WaveFormat WaveFormat => _inner.WaveFormat;

        public void Kill() => _killed = true;

        public int Read(float[] buffer, int offset, int count)
        {
            if (_fadeLeft == 0)
            {
                Ended?.Invoke();
                return 0;
            }
            int read = _inner.Read(buffer, offset, count);
            if (read == 0)
            {
                Ended?.Invoke();
                return 0;
            }
            if (_killed && _fadeLeft < 0)
            {
                int fadeSamples = Math.Max(1, WaveFormat.SampleRate * WaveFormat.Channels * 30 / 1000);
                _fadeLeft = fadeSamples;
                _fadeStep = 1f / fadeSamples;
                _fadeGain = 1f;
            }
            if (_fadeLeft > 0)
            {
                int n = Math.Min(read, _fadeLeft);
                for (int i = 0; i < n; i++)
                {
                    buffer[offset + i] *= _fadeGain;
                    _fadeGain -= _fadeStep;
                    if (_fadeGain < 0) _fadeGain = 0;
                }
                _fadeLeft -= n;
                if (_fadeLeft == 0 && n < read)
                {
                    // Zero the rest of this buffer and report only the faded part
                    Array.Clear(buffer, offset + n, read - n);
                    return n;
                }
            }
            return read;
        }
    }
}
