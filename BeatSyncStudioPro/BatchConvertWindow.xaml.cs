using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// Batch converter: turns a list of WAV/MP3 files into WAV or MP3 in one
    /// run, optionally loudness-normalizing each to -14 LUFS on the way.
    /// </summary>
    public partial class BatchConvertWindow : Window
    {
        private readonly ObservableCollection<string> _files = new ObservableCollection<string>();
        private CancellationTokenSource? _cts;

        public BatchConvertWindow()
        {
            InitializeComponent();
            FileList.ItemsSource = _files;
        }

        private void AddFiles_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Audio files|*.wav;*.mp3|All files|*.*",
                Multiselect = true,
                Title = "Add files to convert"
            };
            if (dlg.ShowDialog() != true) return;
            foreach (var f in dlg.FileNames)
            {
                if (!_files.Contains(f)) _files.Add(f);
            }
        }

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            if (FileList.SelectedItem is string f) _files.Remove(f);
        }

        private void Clear_Click(object sender, RoutedEventArgs e) => _files.Clear();

        private void BrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog { Title = "Output folder" };
            if (dlg.ShowDialog() == true) FolderBox.Text = dlg.FolderName;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

        private async void Convert_Click(object sender, RoutedEventArgs e)
        {
            if (_files.Count == 0)
            {
                MessageBox.Show("Add some files first.", "Batch Convert");
                return;
            }
            bool mp3 = FormatMp3.IsChecked == true;
            bool normalize = LoudnessCheck.IsChecked == true;
            string folder = FolderBox.Text.Trim();
            _cts = new CancellationTokenSource();
            ConvertButton.IsEnabled = false;
            CancelButton.IsEnabled = true;

            var files = new string[_files.Count];
            _files.CopyTo(files, 0);
            int done = 0, failed = 0;
            foreach (var input in files)
            {
                if (_cts.IsCancellationRequested) break;
                string name = Path.GetFileNameWithoutExtension(input) + "-converted" + (mp3 ? ".mp3" : ".wav");
                string output = string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)
                    ? Path.Combine(Path.GetDirectoryName(input) ?? "", name)
                    : Path.Combine(folder, name);
                StatusText.Text = $"Converting {Path.GetFileName(input)} → {name}  ({done + failed + 1} of {files.Length})";
                try
                {
                    await Task.Run(() => ConvertFile(input, output, normalize, _cts.Token));
                    done++;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    failed++;
                    StatusText.Text = $"⚠️ {Path.GetFileName(input)} failed: {ex.Message}";
                }
                OverallProgress.Value = 100.0 * (done + failed) / files.Length;
            }

            ConvertButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            StatusText.Text = _cts.IsCancellationRequested
                ? $"Cancelled — {done} file(s) converted."
                : $"✅ Done — {done} converted" + (failed > 0 ? $", {failed} failed." : ".");
        }

        private static void ConvertFile(string input, string output, bool normalize, CancellationToken token)
        {
            bool mp3Out = output.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase);
            string? tempWav = null;
            string workWav = output;
            try
            {
                if (mp3Out || normalize)
                {
                    tempWav = Path.Combine(Path.GetTempPath(), "beatsync-convert-" + Guid.NewGuid().ToString("N") + ".wav");
                    workWav = tempWav;
                }

                // Decode -> 44.1k stereo -> 16-bit WAV
                using (var reader = input.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
                    ? (WaveStream)new WaveFileReader(input)
                    : input.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
                    ? new Mp3FileReader(input)
                    : new MediaFoundationReader(input))
                {
                    ISampleProvider p = reader.ToSampleProvider();
                    if (p.WaveFormat.Channels == 1) p = p.ToStereo();
                    if (p.WaveFormat.SampleRate != 44100) p = new WdlResamplingSampleProvider(p, 44100);
                    WaveFileWriter.CreateWaveFile16(workWav, p);
                }
                token.ThrowIfCancellationRequested();

                string finalWav = workWav;
                if (normalize)
                {
                    var (lufs, peak) = ExportRenderer.MeasureLoudness(workWav);
                    double gainDb = Math.Clamp(-14 - lufs, -24, 24);
                    double peakDb = peak > 0 ? 20 * Math.Log10(peak) : -80;
                    if (peakDb + gainDb > -1.0) gainDb = -1.0 - peakDb;
                    if (Math.Abs(gainDb) > 0.1)
                    {
                        string gained = Path.Combine(Path.GetTempPath(), "beatsync-convert-g-" + Guid.NewGuid().ToString("N") + ".wav");
                        ExportRenderer.ApplyGain(workWav, gained, gainDb, token);
                        if (tempWav != null) { try { File.Delete(tempWav); } catch { } }
                        tempWav = gained;
                        finalWav = gained;
                    }
                }

                if (mp3Out)
                {
                    using var r = new WaveFileReader(finalWav);
                    MediaFoundationEncoder.EncodeToMp3(r, output, 192000);
                }
                else if (finalWav != output)
                {
                    File.Copy(finalWav, output, true);
                }
            }
            finally
            {
                if (tempWav != null) { try { File.Delete(tempWav); } catch { } }
            }
        }
    }
}
