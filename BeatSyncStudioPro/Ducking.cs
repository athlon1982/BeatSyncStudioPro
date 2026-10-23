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

    /// <summary>
    /// Drops ducking (v3.97): while any radio drop is sounding, the music
    /// side eases toward DropsDepth; when the drop ends it eases back.
    /// Pure easing math so the timing can be unit-tested.
    /// </summary>
    public static class DropsDuck
    {
        public const float Depth = 0.4f;

        public static float Ease(float current, bool dropPlaying)
        {
            float target = dropPlaying ? Depth : 1f;
            float step = target < current ? 0.5f : 0.08f;
            float next = current + (target - current) * step;
            if (System.Math.Abs(target - next) < 0.002f) next = target;
            return next;
        }
    }
}
