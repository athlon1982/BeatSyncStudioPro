using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// Scene Cut / Paste (v3.117): a list of named video slices, reordered
    /// with Cut/Copy/Paste/Delete/Move Up/Move Down, then exported in one
    /// ffmpeg pass with the full original song audio muxed untouched.
    /// The list is the whole model — no drag-and-drop timeline, on purpose:
    /// a list cannot silently drop a frame the way a dragged clip can.
    /// </summary>
    public partial class SceneCutWindow : Window
    {
        private readonly ObservableCollection<SceneCutScene> _scenes = new ObservableCollection<SceneCutScene>();
        private SceneCutScene? _clipboard;
        private double? _lockedAudioSeconds;

        public SceneCutWindow()
        {
            InitializeComponent();
            SceneList.ItemsSource = _scenes;
            SceneList.SelectionChanged += (s, e) => LoadSelectedIntoEditors();
            UpdateTotals();
        }

        private string MainVideo => VideoBox.Text.Trim();

        private void RefreshList()
        {
            // ObservableCollection moves already refresh; this forces the
            // ToString text to redraw after an edit.
            SceneList.Items.Refresh();
            UpdateTotals();
        }

        private void UpdateTotals()
        {
            double total = SceneCutExporter.TotalSeconds(_scenes);
            TotalText.Text = "Scenes: " + _scenes.Count + "   Total: " + total.ToString("0.###", CultureInfo.InvariantCulture) + "s";
            if (_lockedAudioSeconds.HasValue)
            {
                TotalText.Text += "   Locked song audio: " + _lockedAudioSeconds.Value.ToString("0.###", CultureInfo.InvariantCulture) + "s";
                double diff = total - _lockedAudioSeconds.Value;
                WarnText.Text = Math.Abs(diff) < 0.05
                    ? "✓ Total matches the locked song audio — the song will play untouched, start to finish."
                    : "⚠ Total is " + Math.Abs(diff).ToString("0.###", CultureInfo.InvariantCulture) + "s " +
                      (diff < 0 ? "shorter" : "longer") + " than the locked song audio. On export the audio will be cut to the scene total — add or trim a scene to match it exactly.";
            }
            else
            {
                WarnText.Text = "Load a main video to read its locked song-audio length, or type scenes and check the total yourself.";
            }
        }

        private void BrowseVideo_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Main video",
                Filter = "Video files|*.mp4;*.mov;*.mkv;*.webm|All files|*.*"
            };
            if (dlg.ShowDialog() != true) return;
            VideoBox.Text = dlg.FileName;
            _lockedAudioSeconds = SceneCutExporter.AudioLengthSeconds(dlg.FileName);
            if (!_lockedAudioSeconds.HasValue)
                StatusText.Text = "Could not read this file's audio length — the total warning will stay off. Export still works.";
            UpdateTotals();
        }

        private void BrowseSource_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Clip for this scene",
                Filter = "Video files|*.mp4;*.mov;*.mkv;*.webm|All files|*.*"
            };
            if (dlg.ShowDialog() == true) SourceBox.Text = dlg.FileName;
        }

        private void AddScene_Click(object sender, RoutedEventArgs e)
        {
            if (!double.TryParse(StartBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double start) ||
                !double.TryParse(EndBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double end))
            {
                MessageBox.Show("Start and end must be numbers in seconds (e.g. 49 and 57).", "Scene Cut");
                return;
            }
            if (end <= start)
            {
                MessageBox.Show("End must be after start.", "Scene Cut");
                return;
            }
            _scenes.Add(new SceneCutScene
            {
                Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "Scene " + (_scenes.Count + 1) : NameBox.Text.Trim(),
                StartSeconds = start,
                EndSeconds = end,
                SourcePath = SourceBox.Text.Trim()
            });
            RefreshList();
        }

        private SceneCutScene? Selected => SceneList.SelectedItem as SceneCutScene;

        private void LoadSelectedIntoEditors()
        {
            var s = Selected;
            if (s == null) return;
            NameBox.Text = s.Name;
            StartBox.Text = s.StartSeconds.ToString(CultureInfo.InvariantCulture);
            EndBox.Text = s.EndSeconds.ToString(CultureInfo.InvariantCulture);
            SourceBox.Text = s.SourcePath;
        }

        private void Cut_Click(object sender, RoutedEventArgs e)
        {
            var s = Selected;
            if (s == null) return;
            _clipboard = new SceneCutScene { Name = s.Name, StartSeconds = s.StartSeconds, EndSeconds = s.EndSeconds, SourcePath = s.SourcePath };
            _scenes.Remove(s);
            StatusText.Text = "Cut '" + s.Name + "' — use Paste to put it back anywhere in the list.";
            RefreshList();
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            var s = Selected;
            if (s == null) return;
            _clipboard = new SceneCutScene { Name = s.Name, StartSeconds = s.StartSeconds, EndSeconds = s.EndSeconds, SourcePath = s.SourcePath };
            StatusText.Text = "Copied '" + s.Name + "'.";
        }

        private void Paste_Click(object sender, RoutedEventArgs e)
        {
            if (_clipboard == null)
            {
                MessageBox.Show("Nothing on the clipboard yet — Cut or Copy a scene first.", "Scene Cut");
                return;
            }
            int at = SceneList.SelectedIndex >= 0 ? SceneList.SelectedIndex + 1 : _scenes.Count;
            _scenes.Insert(at, new SceneCutScene
            {
                Name = _clipboard.Name, StartSeconds = _clipboard.StartSeconds,
                EndSeconds = _clipboard.EndSeconds, SourcePath = _clipboard.SourcePath
            });
            SceneList.SelectedIndex = at;
            RefreshList();
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            var s = Selected;
            if (s == null) return;
            _scenes.Remove(s);
            RefreshList();
        }

        private void MoveUp_Click(object sender, RoutedEventArgs e)
        {
            int i = SceneList.SelectedIndex;
            if (i <= 0) return;
            _scenes.Move(i, i - 1);
            SceneList.SelectedIndex = i - 1;
            RefreshList();
        }

        private void MoveDown_Click(object sender, RoutedEventArgs e)
        {
            int i = SceneList.SelectedIndex;
            if (i < 0 || i >= _scenes.Count - 1) return;
            _scenes.Move(i, i + 1);
            SceneList.SelectedIndex = i + 1;
            RefreshList();
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            _scenes.Clear();
            RefreshList();
        }

        /// <summary>
        /// Green Mountain State lyric-move order, built from v2d plus the two
        /// outside clips. Load v2d as the main video first; the Flow mud and
        /// Kling finale scenes point at their clip files by name, next to the
        /// main video if you keep them together, or re-point them with the
        /// source Browse button. Totals exactly 236.833s, the v2 length.
        /// </summary>
        private void GmsPreset_Click(object sender, RoutedEventArgs e)
        {
            string dir = string.IsNullOrWhiteSpace(MainVideo) ? "" : Path.GetDirectoryName(MainVideo) ?? "";
            string Near(string file) => string.IsNullOrWhiteSpace(dir) ? file : Path.Combine(dir, file);
            _scenes.Clear();
            _scenes.Add(new SceneCutScene { Name = "Intro to driving", StartSeconds = 0, EndSeconds = 49 });
            _scenes.Add(new SceneCutScene { Name = "Flow mud truck (before barn)", StartSeconds = 0, EndSeconds = 8, SourcePath = Near("flow-mud-clip.mp4") });
            _scenes.Add(new SceneCutScene { Name = "Barn with cows through throne", StartSeconds = 57, EndSeconds = 220 });
            _scenes.Add(new SceneCutScene { Name = "Kling finale cliff", StartSeconds = 0, EndSeconds = 5.042, SourcePath = Near("kling-cliff-candidate.mp4") });
            _scenes.Add(new SceneCutScene { Name = "Crowd cheering (finale)", StartSeconds = 225.042, EndSeconds = 236.833 });
            StatusText.Text = "GMS preset loaded: Flow mud sits before the barn, finale and crowd tail close the video. If the Flow/Kling files live elsewhere, select each scene and fix its source path with Browse.";
            RefreshList();
        }

        private async void Export_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(MainVideo) || !File.Exists(MainVideo))
            {
                MessageBox.Show("Load a main video first.", "Scene Cut");
                return;
            }
            if (_scenes.Count == 0)
            {
                MessageBox.Show("Add at least one scene first.", "Scene Cut");
                return;
            }
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export reordered video",
                Filter = "MP4 video|*.mp4",
                FileName = "BeatSync Scene Cut.mp4",
                DefaultExt = ".mp4"
            };
            if (dlg.ShowDialog() != true) return;
            StatusText.Text = "Exporting... (one ffmpeg pass, then the full song audio is muxed untouched)";
            try
            {
                var progress = new Progress<double>(p => StatusText.Text = "Exporting... " + (p * 100).ToString("0") + "%");
                await SceneCutExporter.ExportAsync(MainVideo, _scenes, dlg.FileName,
                    CropBox.IsChecked == true, progress, CancellationToken.None);
                StatusText.Text = "Done: " + dlg.FileName;
                MessageBox.Show("Exported to " + dlg.FileName, "Scene Cut");
            }
            catch (Exception ex)
            {
                StatusText.Text = "Export failed: " + ex.Message;
                MessageBox.Show("Export failed: " + ex.Message, "Scene Cut");
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
