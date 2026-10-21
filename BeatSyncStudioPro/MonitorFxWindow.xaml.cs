using System.Windows;
using System.Windows.Controls;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// The monitor-bus strip: EQ + compressor + stereo width on what the
    /// user hears. Changes apply live through the shared settings object and
    /// never touch exports.
    /// </summary>
    public partial class MonitorFxWindow : Window
    {
        private readonly MonitorFxSettings _settings;
        private bool _loading;

        public MonitorFxWindow(MonitorFxSettings settings)
        {
            InitializeComponent();
            _settings = settings;

            LowSlider.ValueChanged += (s, e) => { if (!_loading) { _settings.Eq.LowDb = LowSlider.Value; LowLabel.Text = $"{LowSlider.Value:+0;-0;0} dB"; } };
            MidSlider.ValueChanged += (s, e) => { if (!_loading) { _settings.Eq.MidDb = MidSlider.Value; MidLabel.Text = $"{MidSlider.Value:+0;-0;0} dB"; } };
            HighSlider.ValueChanged += (s, e) => { if (!_loading) { _settings.Eq.HighDb = HighSlider.Value; HighLabel.Text = $"{HighSlider.Value:+0;-0;0} dB"; } };
            CompCheck.Checked += (s, e) => _settings.CompOn = true;
            CompCheck.Unchecked += (s, e) => _settings.CompOn = false;
            ThrSlider.ValueChanged += (s, e) => { if (!_loading) { _settings.CompThresholdDb = ThrSlider.Value; ThrLabel.Text = $"{ThrSlider.Value:0} dB"; } };
            RatioSlider.ValueChanged += (s, e) => { if (!_loading) { _settings.CompRatio = RatioSlider.Value; RatioLabel.Text = $"{RatioSlider.Value:0.0} : 1"; } };
            MakeupSlider.ValueChanged += (s, e) => { if (!_loading) { _settings.CompMakeupDb = MakeupSlider.Value; MakeupLabel.Text = $"{MakeupSlider.Value:0} dB"; } };
            WidthSlider.ValueChanged += (s, e) => { if (!_loading) { _settings.Width = WidthSlider.Value; WidthLabel.Text = $"{WidthSlider.Value * 100:0}%"; } };

            RefreshFromSettings();
        }

        private void RefreshFromSettings()
        {
            _loading = true;
            LowSlider.Value = _settings.Eq.LowDb;
            MidSlider.Value = _settings.Eq.MidDb;
            HighSlider.Value = _settings.Eq.HighDb;
            CompCheck.IsChecked = _settings.CompOn;
            ThrSlider.Value = _settings.CompThresholdDb;
            RatioSlider.Value = _settings.CompRatio;
            MakeupSlider.Value = _settings.CompMakeupDb;
            WidthSlider.Value = _settings.Width;
            _loading = false;
            LowLabel.Text = $"{_settings.Eq.LowDb:+0;-0;0} dB";
            MidLabel.Text = $"{_settings.Eq.MidDb:+0;-0;0} dB";
            HighLabel.Text = $"{_settings.Eq.HighDb:+0;-0;0} dB";
            ThrLabel.Text = $"{_settings.CompThresholdDb:0} dB";
            RatioLabel.Text = $"{_settings.CompRatio:0.0} : 1";
            MakeupLabel.Text = $"{_settings.CompMakeupDb:0} dB";
            WidthLabel.Text = $"{_settings.Width * 100:0}%";
        }

        private void Preset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b) return;
            switch (b.Tag?.ToString())
            {
                case "vocal":
                    SetAll(-2, 4, 3, true, -20, 2.5, 2, 1.0);
                    break;
                case "bass":
                    SetAll(6, 0, 1, true, -24, 3, 1, 0.9);
                    break;
                case "wide":
                    SetAll(0, 1, 2, false, -18, 3, 0, 1.6);
                    break;
                default: // flat
                    SetAll(0, 0, 0, false, -18, 3, 0, 1.0);
                    break;
            }
            RefreshFromSettings();
        }

        private void SetAll(double low, double mid, double high, bool comp, double thr, double ratio, double makeup, double width)
        {
            _settings.Eq.LowDb = low;
            _settings.Eq.MidDb = mid;
            _settings.Eq.HighDb = high;
            _settings.CompOn = comp;
            _settings.CompThresholdDb = thr;
            _settings.CompRatio = ratio;
            _settings.CompMakeupDb = makeup;
            _settings.Width = width;
        }
    }
}
