using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace BeatSyncStudioPro
{
    /// <summary>Plays a float array (interleaved stereo, 44100 Hz) as a sample provider.</summary>
    public class ArraySampleProvider : ISampleProvider
    {
        private readonly float[] _samples;
        private int _position;

        public ArraySampleProvider(float[] samples)
        {
            _samples = samples;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int available = _samples.Length - _position;
            int toCopy = Math.Min(available, count);
            if (toCopy <= 0) return 0;
            Array.Copy(_samples, _position, buffer, offset, toCopy);
            _position += toCopy;
            return toCopy;
        }
    }

    /// <summary>Linear fade-in / fade-out across a stream of known length — used for auto-crossfades.</summary>
    public class AutoFadeSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly long _fadeInFrames;
        private readonly long _fadeOutFrames;
        private readonly long _totalFrames;
        private long _frame;

        public AutoFadeSampleProvider(ISampleProvider source, double fadeInSeconds, double fadeOutSeconds, double totalSeconds)
        {
            _source = source;
            WaveFormat = source.WaveFormat;
            int sr = WaveFormat.SampleRate;
            _fadeInFrames = (long)(fadeInSeconds * sr);
            _fadeOutFrames = (long)(fadeOutSeconds * sr);
            _totalFrames = (long)(totalSeconds * sr);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            int channels = WaveFormat.Channels;
            int frames = read / channels;
            for (int f = 0; f < frames; f++)
            {
                double gain = 1.0;
                if (_fadeInFrames > 0 && _frame < _fadeInFrames)
                    gain = (double)_frame / _fadeInFrames;
                if (_fadeOutFrames > 0 && _frame > _totalFrames - _fadeOutFrames)
                    gain = Math.Min(gain, Math.Max(0.0, (double)(_totalFrames - _frame) / _fadeOutFrames));
                if (gain < 1.0)
                {
                    for (int c = 0; c < channels; c++)
                        buffer[offset + f * channels + c] *= (float)gain;
                }
                _frame++;
            }
            return read;
        }
    }

    /// <summary>Shared per-clip audio building blocks for live playback and export.</summary>
    public static class ClipAudio
    {
        /// <summary>
        /// Builds a clip's audible segment fully in memory: decode, resample to
        /// 44100 stereo, apply speed, slice the (possibly nudged) window, then
        /// optionally reverse it and/or normalize it to about -0.5 dB peak.
        /// Used whenever Reverse or Normalize is on (both need the whole segment).
        /// </summary>
        public static ISampleProvider BuildSegment(byte[] bytes, string soundType, double speed,
            double sourceStartSeconds, double lengthSeconds, double extraSkipSeconds, bool reverse, bool normalize)
        {
            IWaveProvider reader = soundType.ToLower() == "wav"
                ? new WaveFileReader(new MemoryStream(bytes))
                : (IWaveProvider)new Mp3FileReader(new MemoryStream(bytes));
            ISampleProvider provider = new WdlResamplingSampleProvider(reader.ToSampleProvider(), 44100).ToStereo();

            var all = new List<float>();
            var buf = new float[44100 * 2];
            int read;
            while ((read = provider.Read(buf, 0, buf.Length)) > 0)
                for (int i = 0; i < read; i++) all.Add(buf[i]);
            var source = all.ToArray();
            long srcFrames = source.Length / 2;

            // Output-domain position: output second t hears source second t*speed.
            // The window starts at SourceStartSeconds/speed in output terms.
            double startFrameD = (sourceStartSeconds / Math.Max(0.01, speed) + extraSkipSeconds) * 44100.0;
            long startFrame = Math.Clamp((long)startFrameD, 0, Math.Max(0, srcFrames - 1));
            long outFrames = Math.Max(0, (long)(lengthSeconds * 44100.0));
            var segment = new float[outFrames * 2];
            for (long f = 0; f < outFrames; f++)
            {
                double srcPos = (startFrame + f) * speed;
                long idx = (long)srcPos;
                if (idx >= srcFrames) break;
                double frac = srcPos - idx;
                int i0 = (int)(idx * 2);
                int i1 = Math.Min(i0 + 2, source.Length - 2);
                segment[f * 2] = (float)(source[i0] * (1 - frac) + source[i1] * frac);
                segment[f * 2 + 1] = (float)(source[i0 + 1] * (1 - frac) + source[i1 + 1] * frac);
            }

            if (reverse)
            {
                for (long f = 0; f < outFrames / 2; f++)
                {
                    long a = f * 2, b = (outFrames - 1 - f) * 2;
                    (segment[a], segment[b]) = (segment[b], segment[a]);
                    (segment[a + 1], segment[b + 1]) = (segment[b + 1], segment[a + 1]);
                }
            }

            if (normalize)
            {
                float peak = 0;
                for (int i = 0; i < segment.Length; i++)
                {
                    float a = Math.Abs(segment[i]);
                    if (a > peak) peak = a;
                }
                if (peak > 0.001f)
                {
                    float gain = 0.95f / peak;
                    for (int i = 0; i < segment.Length; i++) segment[i] *= gain;
                }
            }

            return new ArraySampleProvider(segment);
        }

        /// <summary>
        /// Auto-crossfade lengths for one clip: where a same-lane neighbour
        /// overlaps this clip's start, fade in across the overlap; where one
        /// overlaps its end, fade out. Capped at half the clip / 2 seconds.
        /// </summary>
        public static (double FadeIn, double FadeOut) ComputeFades(
            IReadOnlyList<(int Lane, double Start, double Length)> clips, int index)
        {
            var me = clips[index];
            double myStart = me.Start, myEnd = me.Start + me.Length;
            double fin = 0, fout = 0;
            for (int i = 0; i < clips.Count; i++)
            {
                if (i == index) continue;
                var o = clips[i];
                if (o.Lane != me.Lane) continue;
                double oStart = o.Start, oEnd = o.Start + o.Length;
                if (oStart < myStart && oEnd > myStart && oEnd <= myEnd + 0.001)
                    fin = Math.Max(fin, oEnd - myStart);
                if (oStart >= myStart - 0.001 && oStart < myEnd && oEnd > myEnd)
                    fout = Math.Max(fout, myEnd - oStart);
            }
            double cap = Math.Min(2.0, me.Length / 2.0);
            return (Math.Min(fin, cap), Math.Min(fout, cap));
        }
    }
}
