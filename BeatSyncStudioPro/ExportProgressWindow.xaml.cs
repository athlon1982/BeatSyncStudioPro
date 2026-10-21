using System;
using System.Threading;
using System.Windows;

namespace BeatSyncStudioPro
{
    public partial class ExportProgressWindow : Window
    {
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        public CancellationToken Token => _cts.Token;
        public IProgress<double> Progress { get; }

        public ExportProgressWindow()
        {
            InitializeComponent();
            Progress = new Progress<double>(v =>
            {
                ExportBar.Value = v;
                ExportLabel.Text = $"Rendering your mix… {v * 100:0}%";
            });
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _cts.Cancel();
        }
    }
}
