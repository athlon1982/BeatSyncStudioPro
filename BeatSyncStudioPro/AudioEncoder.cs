using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// Play-and-encode (v3.96): encodes whatever is loaded on a deck —
    /// including a song streamed from the Suno tab — straight from the
    /// deck's in-memory playback buffer to a file in the format the DJ
    /// picks (FLAC / MP3 / WAV, sample rate, MP3 bitrate, bit depth).
    /// Nothing here touches Suno's Download action: the audio comes from
    /// the stream the app already plays, rendered out through ffmpeg.
    /// </summary>
    public sealed class EncodeOptions
    {
        public string Format { get; set; } = "FLAC";       // FLAC | MP3 | WAV
        public int Mp3BitrateKbps { get; set; } = 320;
        public int SampleRate { get; set; } = 44100;       // 44100 | 48000
        public int BitDepth { get; set; } = 16;            // WAV/FLAC: 16 | 24

        /// <summary>Auto-Level (v3.118): bake the deck's measured loudness
        /// trim into the exported file so it lands at -14 LUFS. The
        /// source file is never modified; the trim is the cached gain
        /// from LoudnessCache, applied here as an ffmpeg volume filter.
        /// Off (or a 0 dB gain) leaves the ffmpeg args exactly as before,
        /// so raw encodes are bit-identical to previous versions.</summary>
        public bool ApplyAutoLevel { get; set; }
        public double AutoLevelDb { get; set; }

        public string Extension => Format switch
        {
            "MP3" => ".mp3",
            "WAV" => ".wav",
            _ => ".flac"
        };
    }

    public sealed class EncodeResult
    {
        public bool Ok;
        public string OutputPath = "";
        public string Message = "";
    }

    public static class AudioEncoder
    {
        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "track";
            var invalid = Path.GetInvalidFileNameChars();
            var chars = name.Trim().ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
            string s = new string(chars).Trim();
            return s.Length == 0 ? "track" : s;
        }

        /// <summary>The ffmpeg argument string for a conversion (pure — tested).
        /// With ApplyAutoLevel and a nonzero gain, a volume filter carries
        /// the trim; otherwise the args are exactly the historical ones.</summary>
        public static string BuildFfmpegArgs(string inputWav, string outputPath, EncodeOptions o)
        {
            string ar = o.SampleRate.ToString();
            string codec = o.Format switch
            {
                "MP3" => $"-codec:a libmp3lame -b:a {o.Mp3BitrateKbps}k",
                "WAV" => o.BitDepth == 24 ? "-codec:a pcm_s24le" : "-codec:a pcm_s16le",
                _ => o.BitDepth == 24 ? "-codec:a flac -sample_fmt s32" : "-codec:a flac -sample_fmt s16"
            };
            string filter = o.ApplyAutoLevel && o.AutoLevelDb != 0
                ? $" -af volume={o.AutoLevelDb.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}dB"
                : "";
            return $"-y -hide_banner -loglevel error -i \"{inputWav}\"{filter} -ar {ar} {codec} \"{outputPath}\"";
        }

        /// <summary>
        /// Render the deck's loaded track from memory to a temp float WAV,
        /// then convert with ffmpeg. Runs off the UI thread.
        /// </summary>
        public static async Task<EncodeResult> EncodeDeckAsync(
            DjDeck deck, string title, EncodeOptions options, string outputDir,
            IProgress<string>? progress, CancellationToken ct)
        {
            var result = new EncodeResult();
            string? ffmpeg = StreamEngine.FindFfmpeg();
            if (ffmpeg == null)
            {
                result.Message = "ffmpeg isn't available yet — open Go Live once and use its Download ffmpeg button, then try again.";
                return result;
            }
            if (!deck.IsLoaded)
            {
                result.Message = "Load a song on the deck first.";
                return result;
            }
            string tempWav = Path.Combine(Path.GetTempPath(), "beatsync-encode-" + Guid.NewGuid().ToString("N") + ".wav");
            try
            {
                Directory.CreateDirectory(outputDir);
                string outputPath = Path.Combine(outputDir, SanitizeFileName(title) + options.Extension);
                outputPath = Uniquify(outputPath);

                progress?.Report("Rendering the deck audio…");
                float[] samples = deck.SnapshotInterleaved();
                await Task.Run(() =>
                {
                    using var w = new WaveFileWriter(tempWav, WaveFormat.CreateIeeeFloatWaveFormat(44100, 2));
                    w.WriteSamples(samples, 0, samples.Length);
                }, ct);

                progress?.Report("Encoding to " + options.Format + "…");
                string args = BuildFfmpegArgs(tempWav, outputPath, options);
                var psi = new ProcessStartInfo(ffmpeg, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };
                using var proc = new Process { StartInfo = psi };
                proc.Start();
                string stderr = await proc.StandardError.ReadToEndAsync(ct);
                await proc.WaitForExitAsync(ct);
                if (proc.ExitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                {
                    result.Message = "ffmpeg couldn't encode that: " + (stderr.Length > 300 ? stderr.Substring(0, 300) : stderr);
                    try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
                    return result;
                }
                result.Ok = true;
                result.OutputPath = outputPath;
                result.Message = "Saved " + Path.GetFileName(outputPath) + " (" +
                    (new FileInfo(outputPath).Length / 1024.0).ToString("N0") + " KB) to " + outputDir;
                return result;
            }
            catch (OperationCanceledException)
            {
                result.Message = "Encode cancelled.";
                return result;
            }
            catch (Exception ex)
            {
                result.Message = "Encode failed: " + ex.Message;
                return result;
            }
            finally
            {
                try { if (File.Exists(tempWav)) File.Delete(tempWav); } catch { }
            }
        }

        private static string Uniquify(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path) ?? "";
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            for (int i = 2; i < 100; i++)
            {
                string candidate = Path.Combine(dir, $"{name} ({i}){ext}");
                if (!File.Exists(candidate)) return candidate;
            }
            return path;
        }
    }
}
