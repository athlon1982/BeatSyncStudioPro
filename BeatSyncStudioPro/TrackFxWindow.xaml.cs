using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// Per-track effects window (FL-style: opened from the mixer strip).
    /// Every control writes straight into the shared TrackFxSettings, and the
    /// audio providers read it live — tweaks are audible while playing.
    /// </summary>
    public partial class TrackFxWindow : Window
    {
        private readonly TrackFxSettings _fx;
        private static readonly Brush LabelBrush = new SolidColorBrush(Color.FromRgb(0xA0, 0xA5, 0xB5));
        private static readonly Brush SectionBrush = new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54));

        public TrackFxWindow(TrackFxSettings fx, string trackName)
        {
            InitializeComponent();
            _fx = fx;
            FxTitle.Text = $"🎛️ FX — {trackName}";
            BuildControls();
        }

        private void BuildControls()
        {
            // ---------- EQ ----------
            FxPanel.Children.Add(MakeHeader("🎚️ EQUALIZER", () => _fx.EqOn, v => _fx.EqOn = v));
            FxPanel.Children.Add(MakeSlider("Low (bass)", -15, 15, () => _fx.LowDb, v => _fx.LowDb = v, " dB"));
            FxPanel.Children.Add(MakeSlider("Mid", -15, 15, () => _fx.MidDb, v => _fx.MidDb = v, " dB"));
            FxPanel.Children.Add(MakeSlider("High (treble)", -15, 15, () => _fx.HighDb, v => _fx.HighDb = v, " dB"));

            // ---------- DELAY ----------
            FxPanel.Children.Add(MakeHeader("🔁 DELAY (echo)", () => _fx.DelayOn, v => _fx.DelayOn = v));
            FxPanel.Children.Add(MakeSlider("Time", 10, 1000, () => _fx.DelayMs, v => _fx.DelayMs = v, " ms"));
            FxPanel.Children.Add(MakeSlider("Feedback", 0, 0.7, () => _fx.DelayFeedback, v => _fx.DelayFeedback = v, "", true));
            FxPanel.Children.Add(MakeSlider("Mix", 0, 0.8, () => _fx.DelayMix, v => _fx.DelayMix = v, "", true));

            // ---------- REVERB ----------
            FxPanel.Children.Add(MakeHeader("🏛️ REVERB", () => _fx.ReverbOn, v => _fx.ReverbOn = v));
            FxPanel.Children.Add(MakeSlider("Room size", 0, 1, () => _fx.ReverbSize, v => _fx.ReverbSize = v, "", true));
            FxPanel.Children.Add(MakeSlider("Damping", 0, 1, () => _fx.ReverbDamp, v => _fx.ReverbDamp = v, "", true));
            FxPanel.Children.Add(MakeSlider("Mix", 0, 0.8, () => _fx.ReverbMix, v => _fx.ReverbMix = v, "", true));
        }

        private UIElement MakeHeader(string text, Func<bool> get, Action<bool> set)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 4) };
            var check = new CheckBox { IsChecked = get(), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            check.Checked += (s, e) => set(true);
            check.Unchecked += (s, e) => set(false);
            panel.Children.Add(check);
            panel.Children.Add(new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeights.Bold, Foreground = SectionBrush });
            return panel;
        }

        private UIElement MakeSlider(string label, double min, double max, Func<double> get, Action<double> set, string unit, bool percent = false)
        {
            var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(105) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });

            var name = new TextBlock { Text = label, FontSize = 11, Foreground = LabelBrush, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(name, 0);

            var value = new TextBlock { FontSize = 11, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
            Grid.SetColumn(value, 2);

            var slider = new Slider { Minimum = min, Maximum = max, Value = get(), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(slider, 1);

            void ShowValue() => value.Text = percent ? $"{slider.Value * 100:0}%" : $"{slider.Value:0}{unit}";
            ShowValue();
            slider.ValueChanged += (s, e) => { set(slider.Value); ShowValue(); };

            grid.Children.Add(name);
            grid.Children.Add(slider);
            grid.Children.Add(value);
            return grid;
        }
    }
}
