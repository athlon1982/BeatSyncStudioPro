using System;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// Tunable microphone auto-duck controller (v3.92). The detector is
    /// deliberately separate from the DJ window's timer so its timing can be
    /// tested: speech starts a hold window, the music moves quickly to the
    /// selected depth, brief pauses between words do not let the music surge
    /// back, and after the hold expires the music releases smoothly.
    /// </summary>
    public sealed class DuckingController
    {
        private float _factor = 1f;
        private float _holdSeconds;

        public bool IsDucking { get; private set; }
        public float Factor => _factor;

        public float Update(bool enabled, bool micLive, float micLevel, float musicLevel,
            float duckDepth, float elapsedSeconds, float threshold = 0.03f, float holdSeconds = 0.8f)
        {
            if (!enabled || !micLive)
            {
                IsDucking = false;
                _holdSeconds = 0;
                _factor += (1f - _factor) * 0.25f;
                if (Math.Abs(1f - _factor) < 0.001f) _factor = 1f;
                return musicLevel * _factor;
            }

            if (micLevel >= threshold) _holdSeconds = Math.Max(0, holdSeconds);
            else if (_holdSeconds > 0) _holdSeconds -= Math.Max(0, elapsedSeconds);

            IsDucking = _holdSeconds > 0;
            float target = IsDucking ? Math.Clamp(duckDepth, 0.05f, 1f) : 1f;
            float step = target < _factor ? 0.55f : 0.10f;
            _factor += (target - _factor) * step;
            if (Math.Abs(target - _factor) < 0.001f) _factor = target;
            return musicLevel * _factor;
        }

        public void Reset()
        {
            _factor = 1f;
            _holdSeconds = 0;
            IsDucking = false;
        }
    }
}
