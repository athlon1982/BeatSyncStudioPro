using System;
using System.Windows;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// The unified 🔎 Search Sounds &amp; Music window (v3.59, Michael's
    /// ask): Freesound and the Internet Archive in ONE window, split by
    /// what you're hunting for — 🔊 Sounds &amp; Samples (short hits,
    /// loops, effects) vs 🎵 Music &amp; Recordings (full songs, live
    /// concerts, albums) — plus the API keys tab. v3.66 adds a third
    /// source, 🎵 Jamendo (free independent-artist songs), on its own
    /// tab. The View menu and command palette open this window on the
    /// matching tab.
    /// </summary>
    public partial class SearchHubWindow : Window
    {
        private readonly FreesoundSearchControl _fs;
        private readonly ArchiveSearchControl _ia;
        private readonly JamendoSearchControl _jm;

        public SearchHubWindow(MainWindow owner, int startTab)
        {
            InitializeComponent();
            _fs = new FreesoundSearchControl(owner);
            _ia = new ArchiveSearchControl(owner);
            _jm = new JamendoSearchControl(owner);
            FsTab.Content = _fs;
            IaTab.Content = _ia;
            JamTab.Content = _jm;
            KeyBox.Password = FreesoundSearchControl.LoadKey();
            _fs.KeyProvider = () => KeyBox.Password;
            JamKeyBox.Password = JamendoSearchControl.LoadKey();
            _jm.KeyProvider = () => JamKeyBox.Password;
            Tabs.SelectedIndex = startTab is 1 or 2 ? startTab : 0;
            Closed += (s, e) => { _fs.StopPreview(); _ia.StopPreview(); _jm.StopPreview(); };
        }

        /// <summary>Jump to a tab when the hub is re-opened from a menu.</summary>
        public void SelectTab(int tab) => Tabs.SelectedIndex = tab;

        private void Signup_Click(object sender, RoutedEventArgs e)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://freesound.org/apiv2/apply") { UseShellExecute = true }); }
            catch { }
            KeyStatusText.Text = "Opened freesound.org in your browser — sign up there (free), create an API key on that page, then paste the client secret here and Save Key.";
        }

        private void SaveKey_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                FreesoundSearchControl.SaveKey(KeyBox.Password.Trim());
                KeyStatusText.Text = KeyBox.Password.Length > 0 ? "Key saved ✓ — head to the 🔊 Sounds & Samples tab and search away." : "Key cleared.";
            }
            catch (Exception ex) { KeyStatusText.Text = "Couldn't save the key: " + ex.Message; }
        }

        private void JamSignup_Click(object sender, RoutedEventArgs e)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://devportal.jamendo.com") { UseShellExecute = true }); }
            catch { }
            JamKeyStatusText.Text = "Opened devportal.jamendo.com in your browser — sign up there (free), create an app, then paste its Client ID here and Save ID.";
        }

        private void SaveJamKey_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                JamendoSearchControl.SaveKey(JamKeyBox.Password.Trim());
                JamKeyStatusText.Text = JamKeyBox.Password.Length > 0 ? "Client ID saved ✓ — head to the 🎵 More Music — Jamendo tab and search away." : "Client ID cleared.";
            }
            catch (Exception ex) { JamKeyStatusText.Text = "Couldn't save the client ID: " + ex.Message; }
        }
    }
}
