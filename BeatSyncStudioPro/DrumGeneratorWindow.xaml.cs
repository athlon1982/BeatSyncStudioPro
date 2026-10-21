using System;
using System.Windows;
using System.Windows.Controls;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// Drum generator front-end: style presets → a new editable pattern,
    /// plus the starter loop pack (pre-rendered loops into the library).
    /// Michael's own styles live in their own picker, separate from the
    /// general styles.
    /// </summary>
    public partial class DrumGeneratorWindow : Window
    {
        private readonly MainWindow _owner;
        private bool _syncing;

        public DrumGeneratorWindow(MainWindow owner)
        {
            InitializeComponent();
            _owner = owner;
            foreach (var (key, label, bpm) in DrumGenerator.MyStyles)
                MyStylePicker.Items.Add(new ComboBoxItem { Content = label, Tag = key });
            foreach (var (key, label, bpm) in DrumGenerator.Styles)
                StylePicker.Items.Add(new ComboBoxItem { Content = label, Tag = key });
            MyStylePicker.SelectedIndex = 0; // his style is the default
            BpmBox.Text = DrumGenerator.MyStyles[0].Bpm.ToString("0");
            DensitySlider.ValueChanged += (s, e) => DensityLabel.Text = $"{DensitySlider.Value:0}%";
        }

        private string? ActiveKey()
        {
            if (MyStylePicker.SelectedItem is ComboBoxItem mine && mine.Tag is string mk) return mk;
            if (StylePicker.SelectedItem is ComboBoxItem other && other.Tag is string ok) return ok;
            return null;
        }

        private void MyStylePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing || MyStylePicker.SelectedItem == null) return;
            _syncing = true;
            StylePicker.SelectedItem = null;
            _syncing = false;
            if (MyStylePicker.SelectedItem is ComboBoxItem item && item.Tag is string key)
                BpmBox.Text = DrumGenerator.Find(key).Bpm.ToString("0");
        }

        private void StylePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing || StylePicker.SelectedItem == null) return;
            _syncing = true;
            MyStylePicker.SelectedItem = null;
            _syncing = false;
            if (StylePicker.SelectedItem is ComboBoxItem item && item.Tag is string key)
                BpmBox.Text = DrumGenerator.Find(key).Bpm.ToString("0");
        }

        private void Generate_Click(object sender, RoutedEventArgs e)
        {
            string? key = ActiveKey();
            if (key == null) { StatusText.Text = "Pick a style first."; return; }
            double bpm = double.TryParse(BpmBox.Text, out var b) && b >= 30 && b <= 300 ? b : DrumGenerator.Find(key).Bpm;
            var pattern = DrumGenerator.Generate(key, bpm, DensitySlider.Value / 100.0, HumanizeCheck.IsChecked == true,
                Environment.TickCount);
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
                    ("boomrap", "Starter Loop - Boom-Rap 95.wav", 101),
                    ("trap", "Starter Loop - Trap 140.wav", 202),
                    ("house", "Starter Loop - House 122.wav", 303),
                    ("funk", "Starter Loop - Funk 102.wav", 404),
                    ("lofi", "Starter Loop - Lo-Fi 82.wav", 505),
                    ("drill", "Starter Loop - Drill 140.wav", 606),
                };
                foreach (var (key, name, seed) in pack)
                {
                    double bpm = DrumGenerator.Find(key).Bpm;
                    var pattern = DrumGenerator.Generate(key, bpm, 0.75, true, seed);
                    byte[] wav = PatternRenderer.RenderWav(_owner, pattern, 4);
                    _owner.AddToLibraryOnly(wav, name);
                }
                StatusText.Text = "✅ Six starter loops are in your library (left sidebar) — drag them onto the timeline.";
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
