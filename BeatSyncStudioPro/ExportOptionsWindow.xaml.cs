using System.Windows;

namespace BeatSyncStudioPro
{
    /// <summary>Mastering choices for one export: a preset, one intensity slider, loudness normalize.</summary>
    public partial class ExportOptionsWindow : Window
    {
        public string Preset { get; private set; } = "none";
        public double Intensity { get; private set; } = 0.7;
        public bool NormalizeLoudness { get; private set; }

        public ExportOptionsWindow()
        {
            InitializeComponent();
            IntensitySlider.ValueChanged += (s, e) => IntensityLabel.Text = $"{IntensitySlider.Value:0}%";
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            Preset =
                PresetUniversal.IsChecked == true ? "universal" :
                PresetFire.IsChecked == true ? "fire" :
                PresetClarity.IsChecked == true ? "clarity" :
                PresetTape.IsChecked == true ? "tape" : "none";
            Intensity = IntensitySlider.Value / 100.0;
            NormalizeLoudness = LoudnessCheck.IsChecked == true;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
