using System;
using System.Windows;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// The unified 🔎 Search Sounds &amp; Music window (v3.59, Michael's
    /// ask): Freesound and the Internet Archive in ONE window, split by
    /// what you're hunting for — 🔊 Sounds &amp; Samples (short hits,
    /// loops, effects) vs 🎵 Music &amp; Recordings (full songs, live
    /// concerts, albums) — plus the Freesound key tab. Replaces the two
    /// separate search windows; the View menu and command palette open
    /// this window on the matching tab.
    /// </summary>
    public partial class SearchHubWindow : Window
    {
        private readonly FreesoundSearchControl _fs;
        private readonly ArchiveSearchControl _ia;

        public SearchHubWindow(MainWindow owner, int startTab)
        {
            InitializeComponent();
            _fs = new FreesoundSearchControl(owner);
            _ia = new ArchiveSearchControl(owner);
            FsTab.Content = _fs;
            IaTab.Content = _ia;
            KeyBox.Password = FreesoundSearchControl.LoadKey();
            _fs.KeyProvider = () => KeyBox.Password;
            Tabs.SelectedIndex = startTab is 1 or 2 ? startTab : 0;
            Closed += (s, e) => { _fs.StopPreview(); _ia.StopPreview(); };
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
    }
}
