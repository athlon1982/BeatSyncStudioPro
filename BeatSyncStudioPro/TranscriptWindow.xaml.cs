using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;

namespace BeatSyncStudioPro
{
    /// <summary>One transcribed line, with its times inside the source clip.</summary>
    public class TranscriptSegment
    {
        public TimeSpan Start { get; set; }
        public TimeSpan End { get; set; }
        public string Text { get; set; } = "";
        public FixedBlock Block { get; set; } = null!;
        public string Display => $"{Start:mm\\:ss}  {Text}";
    }

    /// <summary>
    /// Whisper transcription (whisper.cpp via Whisper.net, MIT). The tiny.en
    /// model (~75 MB) downloads once, on request, into AppData. Clicking a
    /// line sets the A–B loop to that section of the timeline.
    /// </summary>
    public partial class TranscriptWindow : Window
    {
        private readonly MainWindow _owner;
        private readonly List<FixedBlock> _clips = new List<FixedBlock>();
        private readonly List<TranscriptSegment> _segments = new List<TranscriptSegment>();

        private static string ModelDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BeatSyncStudioPro", "models");
        private static string ModelPath => Path.Combine(ModelDir, "ggml-tiny.en.bin");
        private const string ModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.en.bin";

        public TranscriptWindow(MainWindow owner)
        {
            InitializeComponent();
            _owner = owner;
            foreach (var block in owner.TimelineBlocks)
            {
                if (block.IsAudio && block.RawBytes != null && block.RawBytes.Length > 0)
                {
                    _clips.Add(block);
                    ClipPicker.Items.Add(block.Filename);
                }
            }
            if (_clips.Count > 0) ClipPicker.SelectedIndex = 0;
            SegmentList.ItemsSource = _segments;
            StatusText.Text = _clips.Count == 0
                ? "No audio clips on the timeline yet — add a song or vocal clip first."
                : File.Exists(ModelPath)
                    ? "Ready. Pick a clip and hit Transcribe."
                    : "First use downloads the Whisper model (~75 MB, one time).";
        }

        private async void Transcribe_Click(object sender, RoutedEventArgs e)
        {
            if (ClipPicker.SelectedIndex < 0 || ClipPicker.SelectedIndex >= _clips.Count) return;
            var block = _clips[ClipPicker.SelectedIndex];
            TranscribeButton.IsEnabled = false;
            BusyBar.Visibility = Visibility.Visible;
            try
            {
                if (!File.Exists(ModelPath))
                {
                    var ask = MessageBox.Show(
                        "Transcription needs the Whisper 'tiny' speech model (~75 MB). Download it once now?",
                        "Download model", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (ask != MessageBoxResult.Yes)
                    {
                        StatusText.Text = "Model not downloaded — transcription skipped.";
                        return;
                    }
                    await DownloadModelAsync();
                }

                StatusText.Text = "Preparing audio…";
                string wavPath = await Task.Run(() => PrepareAudio(block));

                StatusText.Text = "Transcribing… (a full song can take a minute or two on CPU)";
                _segments.Clear();
                SegmentList.Items.Refresh();
                using var factory = WhisperFactory.FromPath(ModelPath);
                using var processor = factory.CreateBuilder().WithLanguage("en").Build();
                await using var stream = File.OpenRead(wavPath);
                await foreach (var result in processor.ProcessAsync(stream))
                {
                    string text = (result.Text ?? "").Trim();
                    if (text.Length == 0) continue;
                    _segments.Add(new TranscriptSegment
                    {
                        Start = result.Start,
                        End = result.End,
                        Text = text,
                        Block = block
                    });
                }
                SegmentList.Items.Refresh();
                try { File.Delete(wavPath); } catch { }
                StatusText.Text = _segments.Count > 0
                    ? $"Done — {_segments.Count} lines. Click a line to loop that section on the timeline."
                    : "Whisper didn't catch any words in that clip.";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Transcription failed: " + ex.Message;
            }
            finally
            {
                TranscribeButton.IsEnabled = true;
                BusyBar.Visibility = Visibility.Collapsed;
            }
        }

        private async Task DownloadModelAsync()
        {
            Directory.CreateDirectory(ModelDir);
            StatusText.Text = "Downloading the Whisper model…";
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("BeatSyncStudioPro");
            using var response = await http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? 0;
            await using var src = await response.Content.ReadAsStreamAsync();
            await using var dst = File.Create(ModelPath);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await dst.WriteAsync(buffer, 0, read);
                done += read;
                if (total > 0) StatusText.Text = $"Downloading the Whisper model… {done * 100 / total}%";
            }
            StatusText.Text = "Model downloaded.";
        }

        /// <summary>Decode a clip to a 16 kHz mono WAV on disk — the format Whisper wants.</summary>
        private static string PrepareAudio(FixedBlock block)
        {
            string path = Path.Combine(Path.GetTempPath(), "beatsync-transcribe-" + Guid.NewGuid().ToString("N") + ".wav");
            IWaveProvider reader = block.SoundType.ToLower() == "wav"
                ? new WaveFileReader(new MemoryStream(block.RawBytes))
                : (IWaveProvider)new Mp3FileReader(new MemoryStream(block.RawBytes));
            ISampleProvider p = reader.ToSampleProvider();
            if (p.WaveFormat.Channels > 1) p = new StereoToMonoSampleProvider(p);
            if (p.WaveFormat.SampleRate != 16000) p = new WdlResamplingSampleProvider(p, 16000);
            WaveFileWriter.CreateWaveFile16(path, p);
            return path;
        }

        private void SegmentList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SegmentList.SelectedItem is not TranscriptSegment seg) return;
            var block = seg.Block;
            double startSec = block.StartX / FixedBlock.PixelsPerSecond + (seg.Start.TotalSeconds - block.SourceStartSeconds) / Math.Max(0.01, block.Speed);
            double endSec = block.StartX / FixedBlock.PixelsPerSecond + (seg.End.TotalSeconds - block.SourceStartSeconds) / Math.Max(0.01, block.Speed);
            if (endSec <= startSec) endSec = startSec + 1;
            _owner.SetLoopRange(startSec, endSec);
            StatusText.Text = $"Loop set to {startSec:0.0}s – {endSec:0.0}s on the timeline — press Play to hear just that line.";
        }

        private void SaveTxt_Click(object sender, RoutedEventArgs e)
        {
            if (_segments.Count == 0) return;
            var dlg = new SaveFileDialog { Filter = "Text file|*.txt", FileName = "transcript.txt", DefaultExt = ".txt" };
            if (dlg.ShowDialog() != true) return;
            var sb = new StringBuilder();
            foreach (var seg in _segments) sb.AppendLine(seg.Text);
            File.WriteAllText(dlg.FileName, sb.ToString());
        }

        private void SaveSrt_Click(object sender, RoutedEventArgs e)
        {
            if (_segments.Count == 0) return;
            var dlg = new SaveFileDialog { Filter = "Subtitles|*.srt", FileName = "transcript.srt", DefaultExt = ".srt" };
            if (dlg.ShowDialog() != true) return;
            var sb = new StringBuilder();
            for (int i = 0; i < _segments.Count; i++)
            {
                var seg = _segments[i];
                sb.AppendLine((i + 1).ToString());
                sb.AppendLine($"{FormatSrtTime(seg.Start)} --> {FormatSrtTime(seg.End)}");
                sb.AppendLine(seg.Text);
                sb.AppendLine();
            }
            File.WriteAllText(dlg.FileName, sb.ToString());
        }

        private static string FormatSrtTime(TimeSpan t) =>
            $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}";
    }
}
