using System;
using System.Windows;

namespace BeatSyncStudioPro
{
    /// <summary>Beat grid editor: type a BPM/offset, or detect them from the first song clip.</summary>
    public partial class BeatGridWindow : Window
    {
        private readonly MainWindow _owner;
        public double Bpm { get; private set; }
        public double OffsetSeconds { get; private set; }

        public BeatGridWindow(MainWindow owner)
        {
            InitializeComponent();
            _owner = owner;
            BpmBox.Text = owner.ProjectBpm > 0 ? owner.ProjectBpm.ToString("0.0") : "";
            OffsetBox.Text = owner.ProjectBpm > 0 ? owner.BeatOffsetSeconds.ToString("0.00") : "0";
        }

        private async void Detect_Click(object sender, RoutedEventArgs e)
        {
            DetectButton.IsEnabled = false;
            DetectStatus.Text = "Listening for the beat…";
            var result = await _owner.DetectBeatsForGridAsync();
            DetectButton.IsEnabled = true;
            if (result == null)
            {
                DetectStatus.Text = "No audio clip found, or no steady beat detected. You can still type a BPM in by hand.";
                return;
            }
            BpmBox.Text = result.Value.Bpm.ToString("0.0");
            OffsetBox.Text = result.Value.OffsetTimeline.ToString("0.00");
            DetectStatus.Text = $"Detected {result.Value.Bpm:0.0} BPM — confidence {result.Value.Confidence * 100:0}%. " +
                (result.Value.Confidence < 0.35 ? "That's shaky — trust your ears and nudge if it looks off." : "Looks solid.");
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            double bpm = 0;
            if (!string.IsNullOrWhiteSpace(BpmBox.Text) &&
                (!double.TryParse(BpmBox.Text, out bpm) || bpm < 0 || bpm > 300))
            {
                MessageBox.Show("BPM must be a number between 0 and 300 (0 clears the grid).", "Beat Grid");
                return;
            }
            double offset = 0;
            if (!string.IsNullOrWhiteSpace(OffsetBox.Text) && !double.TryParse(OffsetBox.Text, out offset))
            {
                MessageBox.Show("The first-beat position must be a number of seconds.", "Beat Grid");
                return;
            }
            Bpm = bpm;
            OffsetSeconds = Math.Max(0, offset);
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
