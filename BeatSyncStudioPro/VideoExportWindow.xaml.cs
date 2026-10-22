using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// Video-export pipeline: (ffmpeg download if needed) → render the mix to
    /// a temp WAV with the normal audio engine (mastering included) → ffmpeg
    /// muxes the timeline video + mix into the final MP4.
    /// </summary>
    public partial class VideoExportWindow : Window
    {
        private readonly MainWindow _owner;
        private readonly ExportJob _job;
        private readonly FixedBlock _videoBlock;
        private readonly string _outputPath;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private bool _done;

        public VideoExportWindow(MainWindow owner, ExportJob job, FixedBlock videoBlock, string outputPath)
        {
            InitializeComponent();
            _owner = owner;
            _job = job;
            _videoBlock = videoBlock;
            _outputPath = outputPath;
            Loaded += async (s, e) => await RunAsync();
        }

        private void SetProgress(double fraction, string phase)
        {
            Bar.Value = Math.Clamp(fraction * 100, 0, 100);
            PhaseText.Text = phase;
        }

        private async Task RunAsync()
        {
            string tempWav = Path.Combine(Path.GetTempPath(), "beatsync-videomix-" + Guid.NewGuid().ToString("N") + ".wav");
            try
            {
                if (!FfmpegTool.IsInstalled)
                {
                    var answer = MessageBox.Show(
                        "Video export uses ffmpeg (the standard video tool). It isn't bundled with BeatSync — download it once now? (about 30 MB, free)",
                        "🎬 Export Video", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (answer != MessageBoxResult.Yes)
                    {
                        Finish("Cancelled — ffmpeg is required for video export. Audio export works without it.");
                        return;
                    }
                    var dlProgress = new Progress<double>(p => SetProgress(p * 0.15, "Downloading ffmpeg… (one-time)"));
                    await FfmpegTool.DownloadAsync(dlProgress, _cts.Token);
                }

                var renderProgress = new Progress<double>(p => SetProgress(0.15 + p * 0.45, "Rendering your mix… (this is the audio engine, mastering included)"));
                await Task.Run(() => ExportRenderer.Render(_job, tempWav, renderProgress, _cts.Token));

                double startOffset = _videoBlock.StartX / FixedBlock.PixelsPerSecond;
                double videoLen = _videoBlock.LengthSeconds > 0 ? _videoBlock.LengthSeconds : 60;
                var muxProgress = new Progress<double>(p => SetProgress(0.6 + p * 0.4, "Encoding the MP4… (H.264 video + your mix)"));
                string encoderLabel = await FfmpegTool.MuxAsync(
                    _owner.VideoPath!, _videoBlock.SourceStartSeconds, _videoBlock.Speed, startOffset,
                    tempWav, _outputPath, startOffset + videoLen, muxProgress, _cts.Token);

                SetProgress(1, "Done!");
                Finish($"✅ Your video is exported:\n{_outputPath}\nEncoded with: {encoderLabel}");
            }
            catch (OperationCanceledException)
            {
                try { if (File.Exists(_outputPath)) File.Delete(_outputPath); } catch { }
                Finish("Export cancelled — no video was saved.");
            }
            catch (Exception ex)
            {
                Finish("Video export failed: " + ex.Message);
            }
            finally
            {
                try { if (File.Exists(tempWav)) File.Delete(tempWav); } catch { }
            }
        }

        private void Finish(string message)
        {
            _done = true;
            ResultText.Text = message;
            CancelButton.IsEnabled = false;
            CloseButton.IsEnabled = true;
            if (message.StartsWith("✅")) PhaseText.Text = "Complete.";
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (!_done) _cts.Cancel();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_done) _cts.Cancel();
            base.OnClosing(e);
        }
    }
}
