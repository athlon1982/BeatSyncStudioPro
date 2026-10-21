using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// Drum generator front-end: style presets → a new editable pattern,
    /// plus the starter loop pack (pre-rendered loops into the library).
    /// </summary>
    public partial class DrumGeneratorWindow : Window
    {
        private readonly MainWindow _owner;

        public DrumGeneratorWindow(MainWindow owner)
        {
            InitializeComponent();
            _owner = owner;
            foreach (var (key, label, bpm) in DrumGenerator.Styles)
                StylePicker.Items.Add(new ComboBoxItem { Content = label, Tag = key });
            StylePicker.SelectedIndex = 0;
            BpmBox.Text = (owner.ProjectBpm > 0 ? owner.ProjectBpm : 95).ToString("0");
            DensitySlider.ValueChanged += (s, e) => DensityLabel.Text = $"{DensitySlider.Value:0}%";
        }

        private void StylePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (StylePicker.SelectedItem is ComboBoxItem item && item.Tag is string key)
            {
                var style = DrumGenerator.Styles.FirstOrDefault(s => s.Key == key);
                if (style.Label != null) BpmBox.Text = style.Bpm.ToString("0");
            }
        }

        private void Generate_Click(object sender, RoutedEventArgs e)
        {
            if (StylePicker.SelectedItem is not ComboBoxItem item || item.Tag is not string key) return;
            double bpm = double.TryParse(BpmBox.Text, out var b) && b >= 30 && b <= 300 ? b : 95;
            var pattern = DrumGenerator.Generate(key, bpm, DensitySlider.Value / 100.0, HumanizeCheck.IsChecked == true,
                Environment.TickCount);
            string label = DrumGenerator.Styles.First(s => s.Key == key).Label;
            pattern.Name = $"{label} Beat".Replace("🥁 ", "").Replace("🔥 ", "").Replace("🏠 ", "").Replace("🎷 ", "").Replace("🎸 ", "").Replace("🌴 ", "");
            _owner.Patterns.Add(pattern);
            StatusText.Text = $"✅ '{pattern.Name}' added — opening it in the step sequencer…";
            _owner.OpenPatternsOn(pattern);
            Close();
        }

        private void Pack_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                PackButton.IsEnabled = false;
                StatusText.Text = "Rendering the starter loops…";
                (string Key, string Name, int Seed)[] pack =
                {
                    ("boombap", "Starter Loop - Boom-Bap 95.wav", 101),
                    ("trap", "Starter Loop - Trap 140.wav", 202),
                    ("house", "Starter Loop - House 122.wav", 303),
                    ("funk", "Starter Loop - Funk 102.wav", 404),
                };
                foreach (var (key, name, seed) in pack)
                {
                    double bpm = DrumGenerator.Styles.First(s => s.Key == key).Bpm;
                    var pattern = DrumGenerator.Generate(key, bpm, 0.75, true, seed);
                    byte[] wav = PatternRenderer.RenderWav(_owner, pattern, 4);
                    _owner.AddToLibraryOnly(wav, name);
                }
                StatusText.Text = "✅ Four starter loops are in your library (left sidebar) — drag them onto the timeline.";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Pack failed: " + ex.Message;
            }
            finally
            {
                PackButton.IsEnabled = true;
            }
        }
    }
}
