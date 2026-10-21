using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace BeatSyncStudioPro
{
    /// <summary>One clip in an export job (a snapshot of a timeline block).</summary>
    public class ExportClip
    {
        public byte[]? RawBytes;
        public string SoundType = "";
        public string? FilePath;            // used for the video's own soundtrack
        public double StartSeconds;
        public double Speed = 1.0;
        public double SourceStartSeconds;
        public double LengthSeconds;        // timeline window length; <= 0 means the whole sound
        public double Volume = 1.0;
        public int ChainIndex;
        public bool Reverse;
        public bool Normalize;
        public int NudgeMs;
        public double FadeInSeconds;
        public double FadeOutSeconds;

        /// <summary>When the clip's audio actually starts in the export (nudge applied, never negative).</summary>
        public double DelaySeconds => Math.Max(0, StartSeconds + NudgeMs / 1000.0);

        /// <summary>Extra source audio to skip because a negative nudge pulled the start before zero.</summary>
        public double ExtraSkipSeconds => Math.Max(0, -(StartSeconds + NudgeMs / 1000.0));
    }

    /// <summary>One mixer chain in an export job (a snapshot of a track strip).</summary>
    public class ExportChain
    {
        public double Volume = 1.0;
        public double Pan;
        public TrackFxSettings Fx = new TrackFxSettings();
    }

    public class ExportJob
    {
        public List<ExportClip> Clips = new List<ExportClip>();
        public List<ExportChain> Chains = new List<ExportChain>();
        public double TotalSeconds;

        // Mastering (applied to the whole mix, after the track chains)
        public string MasteringPreset = "none";   // none | universal | fire | clarity | tape
        public double MasteringIntensity = 0.7;   // 0..1 — the one slider
        public bool NormalizeLoudness;            // bring the export to LoudnessTargetLufs
        public double LoudnessTargetLufs = -14;   // streaming standard
    }

    /// <summary>BandLab-style mastering recipes, scaled by one intensity value.</summary>
    public static class MasteringRecipes
    {
        public static MonitorFxSettings Build(string preset, double intensity)
        {
            var s = new MonitorFxSettings();
            double i = Math.Clamp(intensity, 0, 1);
            (double Lo, double Mid, double Hi, double Thr, double Ratio, double Makeup, double Width) r = preset switch
            {
                "universal" => (1, 0.5, 1.5, -16, 2.5, 1.5, 1.05),
                "fire" => (3, 0, 4, -20, 4, 3, 1.10),
                "clarity" => (-2, 3, 2, -18, 2, 1, 1.0),
                "tape" => (2, 0.5, -3, -14, 2, 1, 0.9),
                _ => (0, 0, 0, 0, 1, 0, 1.0)
            };
            s.Eq.LowDb = r.Lo * i;
            s.Eq.MidDb = r.Mid * i;
            s.Eq.HighDb = r.Hi * i;
            s.CompOn = i > 0.02;
            s.CompThresholdDb = r.Thr * i;
            s.CompRatio = 1 + (r.Ratio - 1) * i;
            s.CompMakeupDb = r.Makeup * i;
            s.Width = 1 + (r.Width - 1) * i;
            return s;
        }
    }

    /// <summary>Plays silence until the clip's start time, then the source.</summary>
    public class StartDelayedSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private long _framesRemaining;

        public StartDelayedSampleProvider(ISampleProvider source, double delaySeconds)
        {
            _source = source;
            WaveFormat = source.WaveFormat;
            _framesRemaining = (long)(delaySeconds * WaveFormat.SampleRate);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int channels = WaveFormat.Channels;
            int written = 0;
            if (_framesRemaining > 0)
            {
                int frames = count / channels;
                int silentFrames = (int)Math.Min(frames, _framesRemaining);
                int silentSamples = silentFrames * channels;
                Array.Clear(buffer, offset, silentSamples);
                _framesRemaining -= silentFrames;
                written += silentSamples;
                if (written >= count) return written;
            }
            int read = _source.Read(buffer, offset + written, count - written);
            return written + read;
        }
    }

    /// <summary>
    /// Offline mixdown: rebuilds the exact live playback chain (decode, speed,
    /// trim window, clip volume, then per-track EQ/delay/reverb, pan and fader)
    /// and renders it to a WAV file — or an MP3 via Media Foundation.
    /// </summary>
    public static class ExportRenderer
    {
        public static void Render(ExportJob job, string outputPath, IProgress<double>? progress, CancellationToken token)
        {
            var format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
            var root = new MixingSampleProvider(format) { ReadFully = true };

            var chainMixers = new List<MixingSampleProvider>();
            foreach (var chain in job.Chains)
            {
                var chainMixer = new MixingSampleProvider(format) { ReadFully = true };
                var eq = new EqSampleProvider(chainMixer, chain.Fx);
                var delay = new DelaySampleProvider(eq, chain.Fx);
                var reverb = new ReverbSampleProvider(delay, chain.Fx);
                var pan = new StereoPanSampleProvider(reverb) { Pan = (float)chain.Pan };
                var gain = new VolumeSampleProvider(pan) { Volume = (float)chain.Volume };
                root.AddMixerInput(gain);
                chainMixers.Add(chainMixer);
            }

            foreach (var clip in job.Clips)
            {
                var provider = BuildClipProvider(clip);
                if (provider == null || chainMixers.Count == 0) continue;
                int ci = Math.Clamp(clip.ChainIndex, 0, chainMixers.Count - 1);
                chainMixers[ci].AddMixerInput(new StartDelayedSampleProvider(provider, clip.DelaySeconds));
            }

            // Mastering chain on the whole mix (EQ -> comp -> width), if chosen
            ISampleProvider masterOut = root;
            if (!string.IsNullOrEmpty(job.MasteringPreset) && job.MasteringPreset != "none" && job.MasteringIntensity > 0.01)
            {
                var m = MasteringRecipes.Build(job.MasteringPreset, job.MasteringIntensity);
                masterOut = new StereoWidthSampleProvider(
                    new CompressorSampleProvider(new EqSampleProvider(root, m.Eq), m), m);
            }

            bool mp3 = outputPath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase);
            bool needsPost = mp3 || job.NormalizeLoudness;
            string wavPath = needsPost
                ? Path.Combine(Path.GetTempPath(), "beatsync-export-" + Guid.NewGuid().ToString("N") + ".wav")
                : outputPath;
            string? gainedPath = null;
            try
            {
                var waveProvider = masterOut.ToWaveProvider16();
                long totalBytes = (long)(job.TotalSeconds * 44100) * 4; // stereo, 16-bit
                long writtenBytes = 0;
                var buffer = new byte[44100 * 4];
                using (var writer = new WaveFileWriter(wavPath, waveProvider.WaveFormat))
                {
                    while (writtenBytes < totalBytes)
                    {
                        token.ThrowIfCancellationRequested();
                        int want = (int)Math.Min(buffer.Length, totalBytes - writtenBytes);
                        int read = waveProvider.Read(buffer, 0, want);
                        if (read <= 0) break;
                        writer.Write(buffer, 0, read);
                        writtenBytes += read;
                        progress?.Report(Math.Min(1.0, (double)writtenBytes / totalBytes) * (needsPost ? 0.85 : 1.0));
                    }
                }

                string finalWav = wavPath;
                if (job.NormalizeLoudness)
                {
                    token.ThrowIfCancellationRequested();
                    var (lufs, peak) = MeasureLoudness(wavPath);
                    double gainDb = Math.Clamp(job.LoudnessTargetLufs - lufs, -24, 24);
                    double peakDb = peak > 0 ? 20 * Math.Log10(peak) : -80;
                    if (peakDb + gainDb > -1.0) gainDb = -1.0 - peakDb; // keep 1 dB of true-peak headroom
                    if (Math.Abs(gainDb) > 0.1)
                    {
                        gainedPath = Path.Combine(Path.GetTempPath(), "beatsync-gained-" + Guid.NewGuid().ToString("N") + ".wav");
                        ApplyGain(wavPath, gainedPath, gainDb, token);
                        finalWav = gainedPath;
                    }
                    progress?.Report(0.95);
                }

                if (mp3)
                {
                    token.ThrowIfCancellationRequested();
                    using var reader = new WaveFileReader(finalWav);
                    MediaFoundationEncoder.EncodeToMp3(reader, outputPath, 192000);
                }
                else if (finalWav != outputPath)
                {
                    File.Copy(finalWav, outputPath, true);
                }
                if (wavPath != outputPath) { try { File.Delete(wavPath); } catch { } }
                if (gainedPath != null) { try { File.Delete(gainedPath); } catch { } }
                progress?.Report(1.0);
            }
            catch
            {
                if (token.IsCancellationRequested)
                {
                    try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
                }
                try { if (wavPath != outputPath && File.Exists(wavPath)) File.Delete(wavPath); } catch { }
                throw;
            }
        }

        /// <summary>One stage of the BS.1770 K-weighting filter (direct form I biquad).</summary>
        private struct Biquad
        {
            private readonly double _b0, _b1, _b2, _a1, _a2;
            private double _x1, _x2, _y1, _y2;

            public Biquad(double b0, double b1, double b2, double a1, double a2)
            {
                _b0 = b0; _b1 = b1; _b2 = b2; _a1 = a1; _a2 = a2;
                _x1 = _x2 = _y1 = _y2 = 0;
            }

            public double Process(double x)
            {
                double y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
                _x2 = _x1; _x1 = x;
                _y2 = _y1; _y1 = y;
                return y;
            }
        }

        /// <summary>
        /// Integrated loudness (BS.1770 K-weighted, ungated — a close
        /// approximation of true LUFS) plus the sample peak of a WAV file.
        /// </summary>
        private static (double Lufs, double Peak) MeasureLoudness(string wavPath)
        {
            using var reader = new WaveFileReader(wavPath);
            ISampleProvider provider = reader.ToSampleProvider();
            if (provider.WaveFormat.Channels == 1) provider = provider.ToStereo();
            var s1L = new Biquad(1.53512485958697, -2.69169618940638, 1.19839281085285, -1.69065929318241, 0.73248077421585);
            var s2L = new Biquad(1, -2, 1, -1.99004745483398, 0.99007225036621);
            var s1R = new Biquad(1.53512485958697, -2.69169618940638, 1.19839281085285, -1.69065929318241, 0.73248077421585);
            var s2R = new Biquad(1, -2, 1, -1.99004745483398, 0.99007225036621);
            double sum = 0;
            long frames = 0;
            double peak = 0;
            var buffer = new float[44100 * 2];
            int read;
            while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (int i = 0; i + 1 < read; i += 2)
                {
                    double l = s2L.Process(s1L.Process(buffer[i]));
                    double r = s2R.Process(s1R.Process(buffer[i + 1]));
                    sum += l * l + r * r;
                    frames++;
                    double a = Math.Abs(buffer[i]);
                    double b = Math.Abs(buffer[i + 1]);
                    if (a > peak) peak = a;
                    if (b > peak) peak = b;
                }
            }
            double mean = frames > 0 ? sum / frames : 0;
            double lufs = mean > 0 ? -0.691 + 10 * Math.Log10(mean) : -70;
            return (lufs, peak);
        }

        /// <summary>Rewrite a WAV with a fixed gain applied (16-bit out).</summary>
        private static void ApplyGain(string inputWav, string outputWav, double gainDb, CancellationToken token)
        {
            using var reader = new WaveFileReader(inputWav);
            ISampleProvider provider = reader.ToSampleProvider();
            var gained = new VolumeSampleProvider(provider) { Volume = (float)Math.Pow(10, gainDb / 20.0) };
            using var writer = new WaveFileWriter(outputWav, new WaveFormat(44100, 16, 2));
            var buffer = new float[44100 * 2];
            var waveProvider = gained.ToWaveProvider16();
            var bytes = new byte[44100 * 4];
            int read;
            while ((read = waveProvider.Read(bytes, 0, bytes.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                writer.Write(bytes, 0, read);
            }
        }

        private static ISampleProvider? BuildClipProvider(ExportClip clip)
        {
            try
            {
                double shiftSec = clip.NudgeMs / 1000.0;
                double extraSkip = clip.ExtraSkipSeconds;
                double audibleLen = clip.LengthSeconds > 0
                    ? Math.Max(0.05, clip.LengthSeconds - Math.Abs(shiftSec))
                    : 0;

                ISampleProvider provider;
                if ((clip.Reverse || clip.Normalize) && clip.RawBytes != null)
                {
                    provider = ClipAudio.BuildSegment(clip.RawBytes, clip.SoundType, clip.Speed,
                        clip.SourceStartSeconds, audibleLen, extraSkip, clip.Reverse, clip.Normalize);
                }
                else
                {
                    IWaveProvider reader;
                    if (clip.RawBytes != null)
                    {
                        reader = clip.SoundType.ToLower() == "wav"
                            ? new WaveFileReader(new MemoryStream(clip.RawBytes))
                            : (IWaveProvider)new Mp3FileReader(new MemoryStream(clip.RawBytes));
                    }
                    else if (clip.FilePath != null)
                    {
                        reader = new MediaFoundationReader(clip.FilePath);
                    }
                    else
                    {
                        return null;
                    }

                    provider = new WdlResamplingSampleProvider(reader.ToSampleProvider(), 44100).ToStereo();

                    if (Math.Abs(clip.Speed - 1.0) > 0.001)
                    {
                        provider = new SpeedSampleProvider(provider, clip.Speed);
                    }

                    if (clip.LengthSeconds > 0)
                    {
                        provider = new OffsetSampleProvider(provider)
                        {
                            SkipOver = TimeSpan.FromSeconds(clip.SourceStartSeconds / clip.Speed + extraSkip),
                            Take = TimeSpan.FromSeconds(audibleLen)
                        };
                    }
                }

                if (clip.FadeInSeconds > 0.005 || clip.FadeOutSeconds > 0.005)
                {
                    double total = audibleLen > 0 ? audibleLen : clip.LengthSeconds;
                    if (total > 0) provider = new AutoFadeSampleProvider(provider, clip.FadeInSeconds, clip.FadeOutSeconds, total);
                }

                return new VolumeSampleProvider(provider) { Volume = (float)clip.Volume };
            }
            catch
            {
                return null;
            }
        }
    }
}
