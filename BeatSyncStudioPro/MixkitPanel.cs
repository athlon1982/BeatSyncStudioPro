using System;
using System.Windows;
using System.Windows.Controls;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// Mixkit panel (v3.81) — an honest placeholder, not a search
    /// control. Verified 2026-10-22 against primary sources: Mixkit
    /// offers NO API (mixkit.co/api and /developers are 404,
    /// api.mixkit.co doesn't exist), and its User Terms expressly
    /// prohibit scripts/bots scraping or downloading its catalog. So
    /// this panel explains that and offers the legitimate route: open
    /// Mixkit in the browser, download there manually, import the
    /// files through the Song Library / Sound Library importers.
    /// HubSearchAsync returns 0 — the panel is intentionally NOT part
    /// of the unified search fan-out; it exists as a tab + Tools entry
    /// so the answer lives where a user would look for it.
    /// </summary>
    public partial class MixkitPanel : UserControl
    {
        public MixkitPanel(MainWindow owner)
        {
            InitializeComponent();
        }

        public void StopPreview() { /* nothing plays here */ }

        private void OpenMixkit_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://mixkit.co/free-stock-music/") { UseShellExecute = true });
                StatusText.Text = "Opened mixkit.co/free-stock-music in your browser — download what you like there, then import it via Tools → 🎵 Song Library (Import) or the Sound Library.";
            }
            catch (Exception ex) { StatusText.Text = "Couldn't open the browser: " + ex.Message; }
        }
    }
}
