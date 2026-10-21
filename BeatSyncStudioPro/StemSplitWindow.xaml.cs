using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// Stem Splitter window: HTDemucs (ONNX) separation of a timeline clip or
    /// any audio file into drums/bass/other/vocals WAVs on disk.
    /// </summary>
    public partial class StemSplitWindow : Window
    {
        private readonly MainWindow _owner;
        private readonly List<FixedBlock> _clips = new List<FixedBlock>();
        private string? _filePath;
        private CancellationTokenSource? _cts;

        private static string ModelDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BeatSyncStudioPro", "models");
        private static string ModelPath => Path.Combine(ModelDir, "htdemucs.onnx");
        private const string ModelUrl = "https://huggingface.co/smank/htdemucs-onnx/resolve/main/htdemucs.onnx";

        public StemSplitWindow(MainWindow owner)
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
            ClipPicker.SelectionChanged += (s, e) => { _filePath = null; FileLabel.Text = ""; };
            StatusText.Text = File.Exists(ModelPath)
                ? "Model ready. Pick a source and hit Split Stems."
                : "First use downloads the HTDemucs model (~304 MB, one time).";
        }

        private void PickFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Audio files|*.wav;*.mp3;*.flac;*.m4a;*.ogg|All files|*.*",
                Title = "Pick a song to split"
            };
            if (dlg.ShowDialog() != true) return;
            _filePath = dlg.FileName;
            ClipPicker.SelectedIndex = -1;
            FileLabel.Text = "File: " + dlg.FileName;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

        private async void Split_Click(object sender, RoutedEventArgs e)
        {
            FixedBlock? clip = null;
            if (_filePath == null)
            {
                if (ClipPicker.SelectedIndex < 0 || ClipPicker.SelectedIndex >= _clips.Count)
                {
                    StatusText.Text = "Pick a timeline clip or a file first.";
                    return;
                }
                clip = _clips[ClipPicker.SelectedIndex];
            }

            SplitButton.IsEnabled = false;
            CancelButton.IsEnabled = true;
            BusyBar.Visibility = Visibility.Visible;
            BusyBar.Value = 0;
            ResultList.Items.Clear();
            _cts = new CancellationTokenSource();
            try
            {
                var compute = ComputeSettings.Load();
                if (compute.Mode == "Online")
                {
                    await SplitOnlineAsync(clip, compute);
                    return;
                }

                if (!File.Exists(ModelPath))
                {
                    var ask = MessageBox.Show(
                        "Stem splitting needs the HTDemucs AI model (~304 MB). Download it once now?",
                        "Download model", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (ask != MessageBoxResult.Yes)
                    {
                        StatusText.Text = "Model not downloaded — splitting skipped.";
                        return;
                    }
                    await DownloadModelAsync(_cts.Token);
                }

                StatusText.Text = "Reading audio…";
                string baseName;
                string outFolder;
                float[] left, right;
                if (_filePath != null)
                {
                    string fp = _filePath;
                    (left, right) = await Task.Run(() => StemSeparator.DecodeFile(fp));
                    baseName = Path.GetFileNameWithoutExtension(fp);
                    outFolder = Path.Combine(Path.GetDirectoryName(fp) ?? "", baseName + " stems");
                }
                else
                {
                    var c = clip!;
                    (left, right) = await Task.Run(() => StemSeparator.DecodeBytes(c.RawBytes!, c.SoundType));
                    baseName = Path.GetFileNameWithoutExtension(c.Filename);
                    string music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
                    outFolder = Path.Combine(music, "BeatSync Stems", baseName);
                }

                StatusText.Text = $"Splitting {baseName}… compute: {ComputeSettings.Load().Describe()}. This can take a few minutes.";
                var progress = new Progress<double>(p =>
                {
                    BusyBar.Value = p * 100;
                    StatusText.Text = $"Splitting {baseName}… {p * 100:0}%";
                });
                await StemSeparator.SplitAsync(left, right, ModelPath, outFolder, baseName, progress, _cts.Token);

                foreach (var stem in StemSeparator.StemNames)
                    ResultList.Items.Add(Path.Combine(outFolder, $"{baseName} ({stem}).wav"));
                StatusText.Text = $"✅ Done (ran on {StemSeparator.LastComputeUsed}) — stems are in:\n{outFolder}\nImport them from the library (Import Audio) to drop them on tracks.";
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "Split cancelled.";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Split failed: " + ex.Message;
            }
            finally
            {
                SplitButton.IsEnabled = true;
                CancelButton.IsEnabled = false;
                BusyBar.Visibility = Visibility.Collapsed;
                BusyBar.IsIndeterminate = false;
                _cts?.Dispose();
                _cts = null;
            }
        }

        /// <summary>☁️ Online route: the chosen cloud provider's Demucs does the heavy lifting.</summary>
        private async Task SplitOnlineAsync(FixedBlock? clip, ComputeSettings compute)
        {
            bool hf = compute.OnlineProvider == "huggingface";
            bool fal = compute.OnlineProvider == "fal";
            string key = hf ? compute.GetHfKey() : fal ? compute.GetFalKey() : compute.GetOnlineKey();
            if (key.Length == 0)
            {
                StatusText.Text = hf
                    ? "☁️ Online compute needs your Hugging Face token first — open the AI Lab, set Compute to ☁️ Online, pick Hugging Face, and save your token there."
                    : fal
                    ? "☁️ Online compute needs your fal.ai API key first — open the AI Lab, set Compute to ☁️ Online, pick fal.ai, and save your key there."
                    : "☁️ Online compute needs your Replicate API key first — open the AI Lab, set Compute to ☁️ Online, and save your key there.";
                return;
            }

            byte[] bytes;
            string baseName, outFolder, ext;
            if (_filePath != null)
            {
                bytes = await File.ReadAllBytesAsync(_filePath, _cts!.Token);
                baseName = Path.GetFileNameWithoutExtension(_filePath);
                outFolder = Path.Combine(Path.GetDirectoryName(_filePath) ?? "", baseName + " stems");
                ext = Path.GetExtension(_filePath).TrimStart('.');
            }
            else
            {
                var c = clip!;
                bytes = c.RawBytes!;
                baseName = Path.GetFileNameWithoutExtension(c.Filename);
                ext = c.SoundType;
                string music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
                outFolder = Path.Combine(music, "BeatSync Stems", baseName);
            }

            BusyBar.IsIndeterminate = true;
            var stems = hf
                ? await OnlineStems.RunHfDemucsAsync(bytes, ext, key, msg => StatusText.Text = msg, _cts.Token)
                : fal
                ? await FalOnline.RunDemucsAsync(bytes, ext, key, msg => StatusText.Text = msg, _cts.Token)
                : await OnlineStems.RunDemucsAsync(bytes, ext, key, msg => StatusText.Text = msg, _cts.Token);
            Directory.CreateDirectory(outFolder);
            foreach (var kv in stems)
            {
                string file = Path.Combine(outFolder, $"{baseName} ({kv.Key}).{kv.Value.Ext}");
                await File.WriteAllBytesAsync(file, kv.Value.Data, _cts.Token);
                ResultList.Items.Add(file);
            }
            StatusText.Text = $"✅ Done (☁️ {(hf ? "Hugging Face" : fal ? "fal.ai" : "Replicate")}) — stems are in:\n{outFolder}\nImport them from the library (Import Audio) to drop them on tracks.";
        }

        private async Task DownloadModelAsync(CancellationToken token)
        {
            // Runs through the Downloads manager now — progress, speed, ETA,
            // pause and cancel live in View → Downloads.
            StatusText.Text = "Downloading the HTDemucs model… watch it in View → ⬇ Downloads.";
            await DownloadManager.DownloadToFileAsync(ModelUrl, "HTDemucs AI model (stem splitter)", ModelPath,
                item => Dispatcher.Invoke(() =>
                {
                    BusyBar.Value = item.Percent;
                    StatusText.Text = $"Downloading the HTDemucs model… {item.Detail}";
                }));
            StatusText.Text = "Model downloaded.";
        }
    }
}
