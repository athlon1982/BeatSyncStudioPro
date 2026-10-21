using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace BeatSyncStudioPro
{
    /// <summary>One queued export: a snapshot export job plus its output path.</summary>
    public class QueueEntry : INotifyPropertyChanged
    {
        public string Label { get; set; } = "";
        public string OutputPath { get; set; } = "";
        public ExportJob Job { get; set; } = new ExportJob();

        private string _status = "Queued";
        public string Status
        {
            get => _status;
            set { _status = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status))); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>
    /// Render queue: stack up multiple exports of the current project (each
    /// with its own bundle/name/format), then render them back to back.
    /// </summary>
    public partial class RenderQueueWindow : Window
    {
        private readonly MainWindow _owner;
        private readonly ObservableCollection<QueueEntry> _entries = new ObservableCollection<QueueEntry>();
        private CancellationTokenSource? _cts;

        public RenderQueueWindow(MainWindow owner)
        {
            InitializeComponent();
            _owner = owner;
            QueueList.ItemsSource = _entries;
        }

        private void AddEntry_Click(object sender, RoutedEventArgs e)
        {
            if (_owner.TimelineBlocks.Count == 0)
            {
                MessageBox.Show("Add some clips to the timeline first, then queue exports of the project.", "Render Queue");
                return;
            }
            var dialog = new SaveFileDialog
            {
                Title = "Queue an export",
                Filter = "WAV audio|*.wav|MP3 audio|*.mp3",
                FileName = "BeatSync Mix.wav",
                DefaultExt = ".wav"
            };
            if (dialog.ShowDialog() != true) return;

            var options = new ExportOptionsWindow(
                Path.GetFileNameWithoutExtension(dialog.FileName), _owner.ProjectBpm) { Owner = this };
            if (options.ShowDialog() != true) return;

            var job = _owner.BuildExportJobSnapshot();
            job.MasteringPreset = options.Preset;
            job.MasteringIntensity = options.Intensity;
            job.NormalizeLoudness = options.NormalizeLoudness;
            string finalPath = _owner.BuildExportPath(dialog.FileName, options);

            string summary = options.Preset == "none" ? "no mastering" : $"{options.Preset} {options.Intensity * 100:0}%";
            if (options.NormalizeLoudness) summary += " + loudness";
            _entries.Add(new QueueEntry
            {
                Label = $"{Path.GetFileName(finalPath)}  —  {summary}",
                OutputPath = finalPath,
                Job = job
            });
        }

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            if (QueueList.SelectedItem is QueueEntry entry) _entries.Remove(entry);
        }

        private void ClearDone_Click(object sender, RoutedEventArgs e)
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (_entries[i].Status.StartsWith("✅")) _entries.RemoveAt(i);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

        private async void RenderAll_Click(object sender, RoutedEventArgs e)
        {
            var pending = _entries.Where(en => en.Status == "Queued" || en.Status.StartsWith("❌") || en.Status == "Cancelled").ToList();
            if (pending.Count == 0)
            {
                StatusText.Text = "Nothing to render — add an export first.";
                return;
            }
            _cts = new CancellationTokenSource();
            RenderButton.IsEnabled = false;
            CancelButton.IsEnabled = true;
            int done = 0;
            foreach (var entry in pending)
            {
                if (_cts.IsCancellationRequested) break;
                StatusText.Text = $"Rendering {entry.Label}";
                var progress = new Progress<double>(p => entry.Status = $"Rendering… {p * 100:0}%");
                try
                {
                    await Task.Run(() => ExportRenderer.Render(entry.Job, entry.OutputPath, progress, _cts.Token));
                    entry.Status = "✅ Done";
                    done++;
                }
                catch (OperationCanceledException)
                {
                    entry.Status = "Cancelled";
                    break;
                }
                catch (Exception ex)
                {
                    entry.Status = "❌ " + ex.Message;
                }
            }
            RenderButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            StatusText.Text = _cts.IsCancellationRequested
                ? $"Cancelled — {done} export(s) finished."
                : $"✅ Queue finished — {done} of {pending.Count} rendered.";
        }
    }
}
