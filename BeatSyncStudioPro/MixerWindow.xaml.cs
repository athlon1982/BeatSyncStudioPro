using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// FL-style mixer window: one channel strip per audio track, one for the
    /// VIDEO AUDIO lane, and a master strip. All controls write straight into
    /// MainWindow's per-track chains, so changes are audible immediately.
    /// </summary>
    public partial class MixerWindow : Window
    {
        private readonly MainWindow _owner;
        private static readonly Brush LabelBrush = new SolidColorBrush(Color.FromRgb(0xA0, 0xA5, 0xB5));
        private static readonly Brush StripBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x24, 0x30));

        public MixerWindow(MainWindow owner)
        {
            InitializeComponent();
            _owner = owner;
            RefreshStrips();
        }

        public void RefreshStrips()
        {
            if (StripsPanel == null) return;
            StripsPanel.Children.Clear();

            int count = _owner.AudioTrackCount;
            for (int i = 0; i <= count; i++)
            {
                StripsPanel.Children.Add(BuildStrip(i, i == count));
            }
            StripsPanel.Children.Add(BuildMasterStrip());
        }

        private Border BuildStrip(int index, bool isVideoAudio)
        {
            var panel = new StackPanel { Width = 84 };

            panel.Children.Add(new TextBlock
            {
                Text = isVideoAudio ? "VIDEO AUDIO" : $"TRACK {index + 1}",
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            });

            var volLabel = new TextBlock
            {
                Text = $"{_owner.GetTrackVolume(index) * 100:0}%",
                FontSize = 10,
                Foreground = LabelBrush,
                TextAlignment = TextAlignment.Center
            };

            var fader = new Slider
            {
                Orientation = Orientation.Vertical,
                Minimum = 0,
                Maximum = 1.5,
                Value = _owner.GetTrackVolume(index),
                Height = 130,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 2)
            };
            fader.ValueChanged += (s, e) =>
            {
                _owner.SetTrackVolume(index, fader.Value);
                volLabel.Text = $"{fader.Value * 100:0}%";
            };

            var pan = new Slider
            {
                Minimum = -1,
                Maximum = 1,
                Value = _owner.GetTrackPan(index),
                Margin = new Thickness(2, 0, 2, 0)
            };
            pan.ValueChanged += (s, e) => _owner.SetTrackPan(index, pan.Value);

            panel.Children.Add(fader);
            panel.Children.Add(volLabel);
            panel.Children.Add(new TextBlock
            {
                Text = "PAN",
                FontSize = 9,
                Foreground = LabelBrush,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0)
            });
            panel.Children.Add(pan);

            return new Border
            {
                Child = panel,
                Background = StripBrush,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4, 8, 4, 8),
                Margin = new Thickness(3, 0, 3, 0)
            };
        }

        private Border BuildMasterStrip()
        {
            var panel = new StackPanel { Width = 84 };

            panel.Children.Add(new TextBlock
            {
                Text = "MASTER",
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54)),
                TextAlignment = TextAlignment.Center
            });

            var volLabel = new TextBlock
            {
                Text = $"{_owner.MasterVolume * 100:0}%",
                FontSize = 10,
                Foreground = LabelBrush,
                TextAlignment = TextAlignment.Center
            };

            var fader = new Slider
            {
                Orientation = Orientation.Vertical,
                Minimum = 0,
                Maximum = 1,
                Value = _owner.MasterVolume,
                Height = 130,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 2)
            };
            fader.ValueChanged += (s, e) =>
            {
                _owner.SetMasterVolume(fader.Value);
                volLabel.Text = $"{fader.Value * 100:0}%";
            };

            panel.Children.Add(fader);
            panel.Children.Add(volLabel);
            panel.Children.Add(new TextBlock
            {
                Text = "ALL TRACKS",
                FontSize = 9,
                Foreground = LabelBrush,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0)
            });

            return new Border
            {
                Child = panel,
                Background = new SolidColorBrush(Color.FromRgb(0x14, 0x2A, 0x1E)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4, 8, 4, 8),
                Margin = new Thickness(3, 0, 3, 0)
            };
        }
    }
}
