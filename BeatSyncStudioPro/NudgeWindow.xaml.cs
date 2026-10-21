using System;
using System.Windows;
using System.Windows.Controls;

namespace BeatSyncStudioPro
{
    /// <summary>Small dialog for a clip's audio nudge, in milliseconds (±5000).</summary>
    public partial class NudgeWindow : Window
    {
        public int ValueMs { get; private set; }

        public NudgeWindow(int currentMs)
        {
            InitializeComponent();
            ValueMs = currentMs;
            MsBox.Text = currentMs.ToString();
            MsBox.SelectAll();
            MsBox.Focus();
        }

        private void Adjust_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && int.TryParse(b.Tag?.ToString(), out int delta))
            {
                int current = int.TryParse(MsBox.Text, out int v) ? v : 0;
                MsBox.Text = Math.Clamp(current + delta, -5000, 5000).ToString();
            }
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(MsBox.Text, out int v))
            {
                MessageBox.Show("Enter a whole number of milliseconds (e.g. -120 or 250).", "Nudge Audio");
                return;
            }
            ValueMs = Math.Clamp(v, -5000, 5000);
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
