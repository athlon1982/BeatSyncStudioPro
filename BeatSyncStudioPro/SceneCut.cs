using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// One scene in Scene Cut/Paste mode (v3.117): a named slice of a video
    /// file. SourcePath empty = the main video loaded in the window; a path
    /// here means this scene comes from a different clip (e.g. the Flow mud
    /// clip or the Kling finale clip), so a lyric move can pull in outside
    /// footage without touching the main video.
    /// </summary>
    public class SceneCutScene
    {
        public string Name = "Scene";
        public double StartSeconds;
        public double EndSeconds;
        public string SourcePath = ""; // empty = main video

        public double Duration => Math.Max(0, EndSeconds - StartSeconds);
        public string SourceLabel(string mainVideo) =>
            string.IsNullOrWhiteSpace(SourcePath) ? "" : " [" + Path.GetFileName(SourcePath) + "]";

        public override string ToString() =>
            Name + "  " + StartSeconds.ToString("0.###", CultureInfo.InvariantCulture) + "s-" +
            EndSeconds.ToString("0.###", CultureInfo.InvariantCulture) + "s  (" +
            Duration.ToString("0.###", CultureInfo.InvariantCulture) + "s)";
    }

    /// <summary>
    /// Scene Cut export (v3.117). Renders the scene list in order with one
    /// ffmpeg pass, then muxes the FULL original song audio untouched on
    /// top, cut to the scene total. The song stays locked: reordering
    /// scenes never re-times the audio, it only changes which picture
    /// plays under it. If the scene total does not match the locked audio
    /// length the caller is warned first (see SceneCutWindow) and the
    /// audio is cut to the scene total on export — never silently padded.
    /// </summary>
    public static class SceneCutExporter
    {
        public static string? ResolveFfmpeg()
        {
            if (FfmpegTool.IsInstalled) return FfmpegTool.FfmpegPath;
            return StreamEngine.FindFfmpeg();
        }

        public static double TotalSeconds(IList<SceneCutScene> scenes)
        {
            double t = 0;
            foreach (var s in scenes) t += s.Duration;
            return t;
        }

        /// <summary>Locked audio length of a file, from its audio track.</summary>
        public static double? AudioLengthSeconds(string path)
        {
            try
            {
                using var reader = new NAudio.Wave.MediaFoundationReader(path);
                return reader.TotalTime.TotalSeconds;
            }
            catch { return null; }
        }

        public static async Task ExportAsync(
            string mainVideo, IList<SceneCutScene> scenes, string outputMp4,
            bool cropToPortrait, IProgress<double>? progress, CancellationToken token)
        {
            string? ffmpeg = ResolveFfmpeg();
            if (ffmpeg == null)
                throw new InvalidOperationException(
                    "ffmpeg is not installed yet — open Go Live once and use its Download ffmpeg button, or run Export Video once, then try Scene Cut export again.");
            if (scenes.Count == 0)
                throw new InvalidOperationException("Add at least one scene before exporting.");
            foreach (var s in scenes)
            {
                string src = string.IsNullOrWhiteSpace(s.SourcePath) ? mainVideo : s.SourcePath;
                if (!File.Exists(src))
                    throw new InvalidOperationException("Scene '" + s.Name + "' points at a missing file: " + src);
                if (s.EndSeconds <= s.StartSeconds)
                    throw new InvalidOperationException("Scene '" + s.Name + "' has an end at or before its start.");
            }

            string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
            double total = TotalSeconds(scenes);
            string tmpDir = Path.Combine(Path.GetTempPath(), "beatsync-scenecut-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpDir);
            string videoOnly = Path.Combine(tmpDir, "video-only.mp4");
            try
            {
                // One input per scene (same file may repeat), accurate trim
                // in the filter so a scene starts exactly where typed.
                var args = new System.Text.StringBuilder("-y ");
                for (int i = 0; i < scenes.Count; i++)
                {
                    string src = string.IsNullOrWhiteSpace(scenes[i].SourcePath) ? mainVideo : scenes[i].SourcePath;
                    args.Append("-i \"" + src + "\" ");
                }
                var filter = new System.Text.StringBuilder();
                for (int i = 0; i < scenes.Count; i++)
                {
                    string vf = cropToPortrait
                        ? $"crop=trunc(ih*9/16/2)*2:ih,scale=1080:1920,format=yuv420p"
                        : "scale=trunc(iw/2)*2:trunc(ih/2)*2,format=yuv420p";
                    filter.Append($"[{i}:v]trim=start={F(scenes[i].StartSeconds)}:end={F(scenes[i].EndSeconds)},setpts=PTS-STARTPTS,{vf}[v{i}];");
                }
                for (int i = 0; i < scenes.Count; i++) filter.Append($"[v{i}]");
                filter.Append($"concat=n={scenes.Count}:v=1:a=0[v]");
                args.Append($"-filter_complex \"{filter}\" -map \"[v]\" -c:v libx264 -preset veryfast -crf 18 -r 24 -progress pipe:1 -nostats \"{videoOnly}\"");

                await RunFfmpegAsync(ffmpeg, args.ToString(), total, progress, token);

                // Mux the FULL original audio untouched, cut to scene total.
                string muxArgs = $"-y -i \"{videoOnly}\" -i \"{mainVideo}\" -map 0:v -map 1:a -c copy -t {F(total)} \"{outputMp4}\"";
                await RunFfmpegAsync(ffmpeg, muxArgs, total, null, token);
                progress?.Report(1.0);
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        private static async Task RunFfmpegAsync(
            string ffmpeg, string args, double totalSeconds,
            IProgress<double>? progress, CancellationToken token)
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = new Process { StartInfo = psi };
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
                if (progress != null && outLine.StartsWith("out_time_ms=") &&
                    long.TryParse(outLine.Substring("out_time_ms=".Length), out var micros) &&
                    totalSeconds > 0)
                {
                    progress.Report(Math.Clamp(micros / 1_000_000.0 / totalSeconds, 0, 1));
                }
            }
            await proc.WaitForExitAsync();
            await stderrTask;
            if (proc.ExitCode != 0)
                throw new InvalidOperationException("ffmpeg exited with code " + proc.ExitCode + ". " + stderr.ToString());
        }
    }
}
