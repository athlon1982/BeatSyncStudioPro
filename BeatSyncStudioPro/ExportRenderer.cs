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
                var pan = new PanningSampleProvider(reverb) { Pan = (float)chain.Pan };
                var gain = new VolumeSampleProvider(pan) { Volume = (float)chain.Volume };
                root.AddMixerInput(gain);
                chainMixers.Add(chainMixer);
            }

            foreach (var clip in job.Clips)
            {
                var provider = BuildClipProvider(clip);
                if (provider == null || chainMixers.Count == 0) continue;
                int ci = Math.Clamp(clip.ChainIndex, 0, chainMixers.Count - 1);
                chainMixers[ci].AddMixerInput(new StartDelayedSampleProvider(provider, clip.StartSeconds));
            }

            bool mp3 = outputPath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase);
            string wavPath = mp3
                ? Path.Combine(Path.GetTempPath(), "beatsync-export-" + Guid.NewGuid().ToString("N") + ".wav")
                : outputPath;
            try
            {
                var waveProvider = root.ToWaveProvider16();
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
                        progress?.Report(Math.Min(1.0, (double)writtenBytes / totalBytes));
                    }
                }

                if (mp3)
                {
                    token.ThrowIfCancellationRequested();
                    using var reader = new WaveFileReader(wavPath);
                    MediaFoundationEncoder.EncodeToMp3(reader, outputPath, 192000);
                    try { File.Delete(wavPath); } catch { }
                }
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

        private static ISampleProvider? BuildClipProvider(ExportClip clip)
        {
            try
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

                ISampleProvider provider = new WdlResamplingSampleProvider(reader.ToSampleProvider(), 44100).ToStereo();

                if (Math.Abs(clip.Speed - 1.0) > 0.001)
                {
                    provider = new SpeedSampleProvider(provider, clip.Speed);
                }

                if (clip.LengthSeconds > 0)
                {
                    provider = new OffsetSampleProvider(provider)
                    {
                        SkipOver = TimeSpan.FromSeconds(clip.SourceStartSeconds / clip.Speed),
                        Take = TimeSpan.FromSeconds(clip.LengthSeconds)
                    };
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
