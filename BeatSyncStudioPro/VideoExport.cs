using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// Video export via ffmpeg. ffmpeg is NOT bundled with the app — it is
    /// downloaded once, on request, into the app's tools folder (the standard
    /// essentials build) and then invoked as a separate program to mux the
    /// timeline video with the rendered mix.
    /// </summary>
    public static class FfmpegTool
    {
        private const string DownloadUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

        public static string ToolsDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BeatSyncStudioPro", "tools");

        public static string FfmpegPath => Path.Combine(ToolsDir, "ffmpeg.exe");

        public static bool IsInstalled => File.Exists(FfmpegPath);

        public static async Task DownloadAsync(IProgress<double> progress, CancellationToken token)
        {
            Directory.CreateDirectory(ToolsDir);
            string zipPath = Path.Combine(Path.GetTempPath(), "beatsync-ffmpeg-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                using (var client = new HttpClient())
                using (var response = await client.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token))
                {
                    response.EnsureSuccessStatusCode();
                    long total = response.Content.Headers.ContentLength ?? -1;
                    using (var src = await response.Content.ReadAsStreamAsync(token))
                    using (var dst = File.Create(zipPath))
                    {
                        var buf = new byte[81920];
                        long done = 0;
                        int read;
                        while ((read = await src.ReadAsync(buf, 0, buf.Length, token)) > 0)
                        {
                            await dst.WriteAsync(buf, 0, read, token);
                            done += read;
                            if (total > 0) progress.Report(done / (double)total * 0.9);
                        }
                    }
                }
                token.ThrowIfCancellationRequested();
                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    ZipArchiveEntry? entry = null;
                    foreach (var candidate in zip.Entries)
                    {
                        if (candidate.FullName.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            entry = candidate;
                            break;
                        }
                    }
                    if (entry == null) throw new InvalidOperationException("ffmpeg.exe was not found inside the downloaded package.");
                    entry.ExtractToFile(FfmpegPath, overwrite: true);
                }
                progress.Report(1);
            }
            finally
            {
                try { File.Delete(zipPath); } catch { }
            }
        }

        private static string CodecArgs(string encoder) => encoder switch
        {
            "h264_nvenc" => "-c:v h264_nvenc -preset p4 -cq 20",
            "h264_qsv" => "-c:v h264_qsv -global_quality 20",
            "h264_amf" => "-c:v h264_amf -quality quality -qp_i 20 -qp_p 20",
            _ => "-c:v libx264 -preset medium -crf 20"
        };

        /// <summary>
        /// Mux: the source video (trimmed + speed-adjusted + offset to its
        /// timeline position) + the rendered mix WAV → MP4 (H.264 + AAC).
        /// The encoder follows the app-wide compute choice (v3.21): GPU /
        /// Auto use the hardware encoder (NVENC / QuickSync / AMF) and fall
        /// back to libx264 automatically if the hardware path fails.
        /// Returns a label naming the encoder that actually produced the file.
        /// </summary>
        public static async Task<string> MuxAsync(
            string videoPath, double sourceStartSeconds, double speed, double startOffsetSeconds,
            string mixWavPath, string outputMp4, double totalSeconds,
            IProgress<double> progress, CancellationToken token)
        {
            string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
            string vf = $"setpts=PTS/{F(Math.Max(0.05, speed))},scale=trunc(iw/2)*2:trunc(ih/2)*2,format=yuv420p";

            async Task<(int Code, string Err)> RunOnce(string encoder)
            {
                string args =
                    $"-y -ss {F(sourceStartSeconds)} -itsoffset {F(startOffsetSeconds)} -i \"{videoPath}\" " +
                    $"-i \"{mixWavPath}\" -map 0:v:0 -map 1:a:0 -vf \"{vf}\" " +
                    $"{CodecArgs(encoder)} -c:a aac -b:a 192k -movflags +faststart " +
                    $"-progress pipe:1 -nostats \"{outputMp4}\"";
                var psi = new ProcessStartInfo
                {
                    FileName = FfmpegPath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var proc = new Process { StartInfo = psi })
                {
                    token.Register(() => { try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { } });
                    proc.Start();
                    var stderr = new System.Text.StringBuilder();
                    var stderrTask = Task.Run(() =>
                    {
                        string? line;
                        while ((line = proc.StandardError.ReadLine()) != null)
                        {
                            if (stderr.Length < 4000) stderr.AppendLine(line);
                        }
                    });
                    string? outLine;
                    while ((outLine = await proc.StandardOutput.ReadLineAsync()) != null)
                    {
                        // ffmpeg -progress: out_time_ms is actually MICROseconds.
                        if (outLine.StartsWith("out_time_ms=") &&
                            long.TryParse(outLine.Substring("out_time_ms=".Length), out var micros) &&
                            totalSeconds > 0)
                        {
                            progress.Report(Math.Clamp(micros / 1_000_000.0 / totalSeconds, 0, 1));
                        }
                    }
                    await proc.WaitForExitAsync();
                    await stderrTask;
                    return (proc.ExitCode, stderr.ToString());
                }
            }

            var (encoder, label) = ComputeSettings.Load().VideoEncoder();
            var (code, err) = await RunOnce(encoder);
            if (code != 0 && encoder != "libx264")
            {
                // Hardware encoding didn't take (driver/build quirk) — the
                // export still has to succeed: redo it on the CPU encoder.
                token.ThrowIfCancellationRequested();
                (code, err) = await RunOnce("libx264");
                if (code == 0) return label + " didn't take — CPU (libx264) did the encode";
            }
            if (code != 0)
                throw new InvalidOperationException("ffmpeg exited with code " + code + ". " + err);
            return label;
        }
    }
}
