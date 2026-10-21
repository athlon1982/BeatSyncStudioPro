using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// Startup hub: one-click templates (track count + beat grid + follow-up
    /// action), recent projects, and a don't-show-again switch.
    /// </summary>
    public partial class StartScreenWindow : Window
    {
        private readonly MainWindow _owner;
        private readonly List<string> _recent;
        private Action? _pending;

        public StartScreenWindow(MainWindow owner)
        {
            InitializeComponent();
            _owner = owner;
            _recent = MainWindow.GetRecentProjects();
            RecentList.ItemsSource = _recent.ConvertAll(p => Path.GetFileName(p));
            NoRecentText.Visibility = _recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RecentList.Visibility = _recent.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            ShowAtStartupCheck.IsChecked = owner.StartScreenEnabled;
            Closed += (s, e) => _pending?.Invoke();
        }

        private void RunAndClose(Action action)
        {
            _pending = action;
            Close();
        }

        private void Blank_Click(object sender, RoutedEventArgs e) => Close();

        private void BeatSession_Click(object sender, RoutedEventArgs e) =>
            RunAndClose(() => _owner.StartNewProject(4, 95, false, false));

        private void SongVocals_Click(object sender, RoutedEventArgs e) =>
            RunAndClose(() => _owner.StartNewProject(3, 90, false, false));

        private void VideoSoundtrack_Click(object sender, RoutedEventArgs e) =>
            RunAndClose(() => _owner.StartNewProject(2, 0, false, true));

        private void DjPrep_Click(object sender, RoutedEventArgs e) =>
            RunAndClose(() => _owner.StartNewProject(2, 120, true, false));

        private void OpenProject_Click(object sender, RoutedEventArgs e) =>
            RunAndClose(() => _owner.BtnOpenProjectPublic());

        private void RecentList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            int idx = RecentList.SelectedIndex;
            if (idx < 0 || idx >= _recent.Count) return;
            string path = _recent[idx];
            RunAndClose(() => _owner.OpenProjectFromPath(path));
        }

        private void ShowAtStartup_Changed(object sender, RoutedEventArgs e) =>
            _owner.StartScreenEnabled = ShowAtStartupCheck.IsChecked == true;

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
