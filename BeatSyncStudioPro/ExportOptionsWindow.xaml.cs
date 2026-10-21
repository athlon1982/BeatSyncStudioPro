using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// Export choices: one-click bundles that fill in a mastering preset,
    /// intensity, loudness flag and a filename pattern with $tokens.
    /// The last-used choices are remembered between sessions.
    /// </summary>
    public partial class ExportOptionsWindow : Window
    {
        private readonly string _projectName;
        private readonly double _projectBpm;
        private bool _loading = true;

        public string Preset { get; private set; } = "none";
        public double Intensity { get; private set; } = 0.7;
        public bool NormalizeLoudness { get; private set; }
        public string FilenamePattern { get; private set; } = "$project";
        public string Bundle { get; private set; } = "custom";

        public ExportOptionsWindow(string projectName, double projectBpm)
        {
            InitializeComponent();
            _projectName = projectName;
            _projectBpm = projectBpm;

            IntensitySlider.ValueChanged += (s, e) => IntensityLabel.Text = $"{IntensitySlider.Value:0}%";
            PatternBox.TextChanged += (s, e) => UpdatePreview();
            foreach (var rb in new[] { PresetNone, PresetUniversal, PresetFire, PresetClarity, PresetTape })
                rb.Checked += (s, e) => UpdatePreview();

            LoadRemembered();
            _loading = false;
            UpdatePreview();
        }

        // ---------- bundles ----------

        private void BundlePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            if (BundlePicker.SelectedItem is not ComboBoxItem item) return;
            switch (item.Tag?.ToString())
            {
                case "streaming": ApplyBundle("universal", 70, true); break;
                case "loud": ApplyBundle("fire", 90, true); break;
                case "vocal": ApplyBundle("clarity", 80, true); break;
                case "warm": ApplyBundle("tape", 60, false); break;
                case "clean": ApplyBundle("none", 70, false); break;
            }
            UpdatePreview();
        }

        private void ApplyBundle(string preset, double intensity, bool loudness)
        {
            SetMasteringRadio(preset);
            IntensitySlider.Value = intensity;
            LoudnessCheck.IsChecked = loudness;
        }

        private void SetMasteringRadio(string preset)
        {
            PresetNone.IsChecked = preset == "none";
            PresetUniversal.IsChecked = preset == "universal";
            PresetFire.IsChecked = preset == "fire";
            PresetClarity.IsChecked = preset == "clarity";
            PresetTape.IsChecked = preset == "tape";
        }

        private string SelectedPreset() =>
            PresetUniversal.IsChecked == true ? "universal" :
            PresetFire.IsChecked == true ? "fire" :
            PresetClarity.IsChecked == true ? "clarity" :
            PresetTape.IsChecked == true ? "tape" : "none";

        // ---------- filename pattern ----------

        private void UpdatePreview()
        {
            string expanded = ExpandPattern(PatternBox.Text ?? "$project", _projectName, _projectBpm, SelectedPreset());
            PatternPreview.Text = $"Will save as: {expanded}.wav / .mp3";
        }

        /// <summary>Expands $project, $date, $time, $bpm and $preset tokens into a safe filename (no extension).</summary>
        public static string ExpandPattern(string pattern, string project, double bpm, string preset)
        {
            var now = DateTime.Now;
            string s = (pattern ?? "$project")
                .Replace("$project", project)
                .Replace("$date", now.ToString("yyyy-MM-dd"))
                .Replace("$time", now.ToString("HHmm"))
                .Replace("$bpm", bpm > 0 ? bpm.ToString("0") : "nobpm")
                .Replace("$preset", preset);
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '-');
            return s.Trim();
        }

        // ---------- remembered settings ----------

        private static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BeatSyncStudioPro", "export-settings.json");

        private class SavedOptions
        {
            public string Preset { get; set; } = "none";
            public double Intensity { get; set; } = 0.7;
            public bool Loudness { get; set; }
            public string Pattern { get; set; } = "$project";
            public string Bundle { get; set; } = "custom";
        }

        private void LoadRemembered()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                var saved = JsonSerializer.Deserialize<SavedOptions>(File.ReadAllText(SettingsPath));
                if (saved == null) return;
                SetMasteringRadio(saved.Preset);
                IntensitySlider.Value = Math.Clamp(saved.Intensity, 0, 1) * 100;
                LoudnessCheck.IsChecked = saved.Loudness;
                PatternBox.Text = saved.Pattern;
                foreach (ComboBoxItem item in BundlePicker.Items)
                {
                    if (item.Tag?.ToString() == saved.Bundle) { BundlePicker.SelectedItem = item; break; }
                }
            }
            catch { }
        }

        private void SaveRemembered()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                var saved = new SavedOptions
                {
                    Preset = Preset,
                    Intensity = Intensity,
                    Loudness = NormalizeLoudness,
                    Pattern = FilenamePattern,
                    Bundle = Bundle
                };
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(saved));
            }
            catch { }
        }

        // ---------- buttons ----------

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            Preset = SelectedPreset();
            Intensity = IntensitySlider.Value / 100.0;
            NormalizeLoudness = LoudnessCheck.IsChecked == true;
            FilenamePattern = PatternBox.Text?.Trim() ?? "$project";
            Bundle = (BundlePicker.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "custom";
            SaveRemembered();
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
