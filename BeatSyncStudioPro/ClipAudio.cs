using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// The ONE way to open audio bytes for decoding (audit Bug 1, v3.60).
    /// WAV and MP3 read straight from memory; everything else (OGG, FLAC,
    /// M4A, WMA…) goes through Media Foundation via a temp file — the same
    /// route the DJ decks and sampler use — because the old habit of
    /// handing every non-WAV file to the MP3 reader made those formats
    /// import fine and then silently never play. The temp file is deleted
    /// when the returned reader is disposed.
    /// </summary>
    public static class AudioReaderFactory
    {
        public static WaveStream Open(byte[] bytes, string soundType)
        {
            string t = (soundType ?? "").Trim().ToLowerInvariant();
            if (t == "wav") return new WaveFileReader(new MemoryStream(bytes));
            if (t == "mp3") return new Mp3FileReader(new MemoryStream(bytes));
            if (t == "ogg")
            {
                // Media Foundation has NO Vorbis decoder unless the optional
                // "Web Media Extensions" Store package is installed (it isn't
                // on the dev PC — MF fails with 0xC00D36C4), so OGG goes
                // through the pure-managed NVorbis decoder. If the file is
                // really Opus-in-OGG, NVorbis throws and MF gets its chance.
                try { return new NAudio.Vorbis.VorbisWaveReader(new MemoryStream(bytes)); }
                catch { }
            }
            string ext = t.Length > 0 && t.All(char.IsLetterOrDigit) ? t : "audio";
            string temp = Path.Combine(Path.GetTempPath(), "beatsync-open-" + Guid.NewGuid().ToString("N") + "." + ext);
            File.WriteAllBytes(temp, bytes);
            try { return new TempFileWaveStream(temp); }
            catch
            {
                try { File.Delete(temp); } catch { }
                // Last resort, matching the old behavior for odd files.
                return new Mp3FileReader(new MemoryStream(bytes));
            }
        }

        private sealed class TempFileWaveStream : WaveStream
        {
            private readonly MediaFoundationReader _inner;
            private readonly string _path;

            public TempFileWaveStream(string path)
            {
                _path = path;
                _inner = new MediaFoundationReader(path);
            }

            public override WaveFormat WaveFormat => _inner.WaveFormat;
            public override long Length => _inner.Length;
            public override long Position { get => _inner.Position; set => _inner.Position = value; }
            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    try { _inner.Dispose(); } catch { }
                    try { File.Delete(_path); } catch { }
                }
                base.Dispose(disposing);
            }
        }
    }

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
            IWaveProvider reader = AudioReaderFactory.Open(bytes, soundType);
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

    /// <summary>
    /// WSOLA time-stretching + the "keep pitch" segment builder: same window
    /// math as BuildSegment, but the window is stretched (ratio 1/speed) by
    /// waveform-similarity overlap-add instead of rate-sampled, so the pitch
    /// stays put while the length changes.
    /// </summary>
    public static class TimeStretch
    {
        public static ISampleProvider BuildSegmentStretched(byte[] bytes, string soundType, double speed,
            double sourceStartSeconds, double lengthSeconds, double extraSkipSeconds, bool reverse, bool normalize)
        {
            IWaveProvider reader = AudioReaderFactory.Open(bytes, soundType);
            ISampleProvider provider = new WdlResamplingSampleProvider(reader.ToSampleProvider(), 44100).ToStereo();

            var all = new List<float>();
            var buf = new float[44100 * 2];
            int read;
            while ((read = provider.Read(buf, 0, buf.Length)) > 0)
                for (int i = 0; i < read; i++) all.Add(buf[i]);
            var source = all.ToArray();
            long srcFrames = source.Length / 2;

            // Same window as BuildSegment: first output frame hears source
            // frame (sourceStart + extraSkip*speed)*44100, span = length*speed.
            long winStart = Math.Clamp((long)((sourceStartSeconds + extraSkipSeconds * speed) * 44100.0), 0, Math.Max(0, srcFrames - 1));
            long winFrames = Math.Max(0, (long)(lengthSeconds * 44100.0 * speed));
            var windowData = new float[winFrames * 2];
            long copyFrames = Math.Min(winFrames, Math.Max(0, srcFrames - winStart));
            if (copyFrames > 0) Array.Copy(source, winStart * 2, windowData, 0, copyFrames * 2);

            var segment = Wsola(windowData, 1.0 / Math.Max(0.05, speed));

            long outFrames = segment.Length / 2;
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

        /// <summary>WSOLA stretch of interleaved-stereo 44.1 kHz audio. ratio = output/input length.</summary>
        public static float[] Wsola(float[] stereo, double ratio)
        {
            int inFrames = stereo.Length / 2;
            ratio = Math.Clamp(ratio, 0.25, 4.0);
            if (inFrames == 0) return stereo;
            int outFrames = Math.Max(1, (int)(inFrames * ratio));

            const int W = 2048;
            const int Hs = 512;
            if (inFrames <= W * 2 || Math.Abs(ratio - 1) < 0.001)
            {
                // Degenerate cases: linear resample to the target length.
                var naive = new float[outFrames * 2];
                for (int f = 0; f < outFrames; f++)
                {
                    double srcPos = f / ratio;
                    long idx = Math.Min((long)srcPos, inFrames - 1);
                    double frac = srcPos - idx;
                    int i0 = (int)(idx * 2);
                    int i1 = Math.Min(i0 + 2, stereo.Length - 2);
                    naive[f * 2] = (float)(stereo[i0] * (1 - frac) + stereo[i1] * frac);
                    naive[f * 2 + 1] = (float)(stereo[i0 + 1] * (1 - frac) + stereo[i1 + 1] * frac);
                }
                return naive;
            }

            var output = new float[outFrames * 2];
            var norm = new float[outFrames];
            var win = new float[W];
            for (int i = 0; i < W; i++) win[i] = (float)(0.5 * (1 - Math.Cos(2 * Math.PI * i / (W - 1))));

            var mono = new float[inFrames];
            for (int i = 0; i < inFrames; i++) mono[i] = (stereo[i * 2] + stereo[i * 2 + 1]) * 0.5f;

            double Ha = Hs / ratio;
            const int searchRange = 384;
            const int corrLen = 512;

            void Place(int inPos, int outPos)
            {
                int frames = Math.Min(W, outFrames - outPos);
                for (int i = 0; i < frames; i++)
                {
                    int src = inPos + i;
                    if (src >= inFrames) break;
                    float w = win[i];
                    output[(outPos + i) * 2] += stereo[src * 2] * w;
                    output[(outPos + i) * 2 + 1] += stereo[src * 2 + 1] * w;
                    norm[outPos + i] += w;
                }
            }

            int prevPos = 0;
            Place(0, 0);
            int outPos = Hs;
            while (outPos + W <= outFrames)
            {
                double nominalD = prevPos + Ha;
                int nominal = (int)Math.Round(nominalD);
                int lo = Math.Max(0, nominal - searchRange);
                int hi = Math.Min(inFrames - W, nominal + searchRange);
                if (hi < lo) hi = lo;
                // Target: what the previous segment contributed to the overlap
                // zone — its frames [Hs .. Hs+corrLen) in source terms.
                int targetStart = prevPos + Hs;
                int best = lo;
                double bestScore = double.NegativeInfinity;
                if (targetStart + corrLen <= inFrames)
                {
                    for (int c = lo; c <= hi; c++)
                    {
                        double score = DotMono(mono, c, targetStart, corrLen);
                        if (score > bestScore) { bestScore = score; best = c; }
                    }
                }
                else
                {
                    best = Math.Clamp(nominal, lo, hi);
                }
                Place(best, outPos);
                prevPos = best;
                outPos += Hs;
            }
            // Tail: fill any remainder with a final window at the proportional spot.
            if (outPos < outFrames)
            {
                int tailIn = Math.Clamp((int)(outPos / ratio), 0, Math.Max(0, inFrames - 1));
                Place(tailIn, outPos);
            }

            for (int f = 0; f < outFrames; f++)
            {
                if (norm[f] > 1e-4f)
                {
                    output[f * 2] /= norm[f];
                    output[f * 2 + 1] /= norm[f];
                }
            }
            return output;
        }

        private static double DotMono(float[] mono, int aStart, int bStart, int length)
        {
            double sum = 0;
            int i = 0;
            int vecLen = System.Numerics.Vector<float>.Count;
            if (System.Numerics.Vector.IsHardwareAccelerated)
            {
                var acc = System.Numerics.Vector<float>.Zero;
                for (; i + vecLen <= length; i += vecLen)
                {
                    var va = new System.Numerics.Vector<float>(mono, aStart + i);
                    var vb = new System.Numerics.Vector<float>(mono, bStart + i);
                    acc += va * vb;
                }
                for (int k = 0; k < vecLen; k++) sum += acc[k];
            }
            for (; i < length; i++) sum += mono[aStart + i] * mono[bStart + i];
            return sum;
        }
    }

    /// <summary>One volume-automation breakpoint on a clip (time from the clip's audible start).</summary>
    public class EnvelopePoint
    {
        public double TimeSeconds { get; set; }
        public double Gain { get; set; } = 1.0;
    }

    /// <summary>Applies a clip's volume envelope: linear gain between breakpoints.</summary>
    public class EnvelopeSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly List<EnvelopePoint> _points;
        private long _frame;

        public EnvelopeSampleProvider(ISampleProvider source, List<EnvelopePoint> points)
        {
            _source = source;
            _points = points.OrderBy(p => p.TimeSeconds).ToList();
            WaveFormat = source.WaveFormat;
        }

        public WaveFormat WaveFormat { get; }

        private double GainAt(double t)
        {
            if (_points.Count == 0) return 1.0;
            if (t <= _points[0].TimeSeconds) return _points[0].Gain;
            for (int i = 1; i < _points.Count; i++)
            {
                if (t <= _points[i].TimeSeconds)
                {
                    var a = _points[i - 1];
                    var b = _points[i];
                    double span = Math.Max(1e-6, b.TimeSeconds - a.TimeSeconds);
                    return a.Gain + (b.Gain - a.Gain) * ((t - a.TimeSeconds) / span);
                }
            }
            return _points[_points.Count - 1].Gain;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            int channels = WaveFormat.Channels;
            int frames = read / channels;
            double rate = WaveFormat.SampleRate;
            for (int f = 0; f < frames; f++)
            {
                double g = GainAt(_frame / rate);
                int idx = offset + f * channels;
                for (int c = 0; c < channels; c++) buffer[idx + c] *= (float)g;
                _frame++;
            }
            return read;
        }
    }

    /// <summary>Noise gate cleanup: smoothly silences audio below a threshold (dB).</summary>
    public class GateSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly double _threshold;
        private double _gain = 1;
        private double _level;

        public GateSampleProvider(ISampleProvider source, double thresholdDb)
        {
            _source = source;
            _threshold = Math.Pow(10, thresholdDb / 20.0);
            WaveFormat = source.WaveFormat;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            int channels = WaveFormat.Channels;
            int frames = read / channels;
            double openCoeff = 1 - Math.Exp(-1.0 / (0.002 * WaveFormat.SampleRate));
            double closeCoeff = 1 - Math.Exp(-1.0 / (0.080 * WaveFormat.SampleRate));
            for (int f = 0; f < frames; f++)
            {
                int idx = offset + f * channels;
                double peak = 0;
                for (int c = 0; c < channels; c++) peak = Math.Max(peak, Math.Abs(buffer[idx + c]));
                _level = Math.Max(peak, _level * 0.99995);
                double target = _level >= _threshold ? 1 : 0;
                double coeff = target > _gain ? openCoeff : closeCoeff;
                _gain += (target - _gain) * coeff;
                for (int c = 0; c < channels; c++) buffer[idx + c] *= (float)_gain;
            }
            return read;
        }
    }
}
