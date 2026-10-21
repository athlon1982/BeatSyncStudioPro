using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// Startup splash screen. Shows for a few seconds (or until clicked),
    /// then hands off to the main studio window.
    /// </summary>
    public partial class SplashScreen : Window
    {
        private readonly DispatcherTimer _timer;
        private bool _shown;

        public SplashScreen()
        {
            InitializeComponent();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.8) };
            _timer.Tick += (s, e) => ShowMainWindow();
            _timer.Start();
        }

        private void ShowMainWindow()
        {
            if (_shown) return;
            _shown = true;
            _timer.Stop();

            var main = new MainWindow();
            main.Show();
            Close();
        }

        private void Splash_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Click the splash to skip straight into the studio
            ShowMainWindow();
        }
    }
}
