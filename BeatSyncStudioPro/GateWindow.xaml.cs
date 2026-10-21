using System.Windows;

namespace BeatSyncStudioPro
{
    /// <summary>Noise-gate settings for one clip: on/off + threshold in dB.</summary>
    public partial class GateWindow : Window
    {
        public bool GateEnabled { get; private set; }
        public double ThresholdDb { get; private set; } = -45;

        public GateWindow(bool enabled, double thresholdDb)
        {
            InitializeComponent();
            EnabledCheck.IsChecked = enabled;
            ThresholdSlider.Value = thresholdDb;
            ValueLabel.Text = $"{thresholdDb:0} dB";
            ThresholdSlider.ValueChanged += (s, e) => ValueLabel.Text = $"{ThresholdSlider.Value:0} dB";
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            GateEnabled = EnabledCheck.IsChecked == true;
            ThresholdDb = ThresholdSlider.Value;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
