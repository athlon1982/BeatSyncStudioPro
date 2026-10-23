using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
        private readonly AudiusSearchControl _au;
        private readonly PixabaySearchControl _px;
        private readonly MixkitPanel _mk;

        public SearchHubWindow(MainWindow owner, int startTab)
        {
            InitializeComponent();
            _fs = new FreesoundSearchControl(owner);
            _ia = new ArchiveSearchControl(owner);
            _jm = new JamendoSearchControl(owner);
            _au = new AudiusSearchControl(owner);
            _px = new PixabaySearchControl(owner);
            _mk = new MixkitPanel(owner);
            FsTab.Content = _fs;
            IaTab.Content = _ia;
            JamTab.Content = _jm;
            AuTab.Content = _au;
            PxTab.Content = _px;
            MkTab.Content = _mk;
            KeyBox.Password = FreesoundSearchControl.LoadKey();
            _fs.KeyProvider = () => KeyBox.Password;
            JamKeyBox.Password = JamendoSearchControl.LoadKey();
            _jm.KeyProvider = () => JamKeyBox.Password;
            PxKeyBox.Password = PixabaySearchControl.LoadKey();
            _px.KeyProvider = () => PxKeyBox.Password;
            // v3.71: the hub's one search box drives all three panels, so
            // each panel's own header + search row hides itself.
            _fs.HideOwnChrome();
            _ia.HideOwnChrome();
            _jm.HideOwnChrome();
            _au.HideOwnChrome();
            _px.HideOwnChrome();
            RefreshAccountStatus();
            Tabs.SelectedIndex = startTab is 1 or 2 or 3 or 4 or 5 ? startTab : 0;
            Closed += (s, e) => { _fs.StopPreview(); _ia.StopPreview(); _jm.StopPreview(); _au.StopPreview(); _px.StopPreview(); };
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
                KeyStatusText.Text = KeyBox.Password.Length > 0 ? "Key saved ✓ — the search box at the top of this window now hunts Freesound too." : "Key cleared.";
                RefreshAccountStatus();
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
                JamKeyStatusText.Text = JamKeyBox.Password.Length > 0 ? "Client ID saved ✓ — the search box at the top of this window now hunts Jamendo too." : "Client ID cleared.";
                RefreshAccountStatus();
            }
            catch (Exception ex) { JamKeyStatusText.Text = "Couldn't save the client ID: " + ex.Message; }
        }

        private void PixabaySignup_Click(object sender, RoutedEventArgs e)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://pixabay.com/api/docs/") { UseShellExecute = true }); }
            catch { }
            PxKeyStatusText.Text = "Opened pixabay.com/api/docs in your browser — sign in there (free account), and your API key is shown right on that page. Copy it, paste it here, Save Key.";
        }

        private void SavePxKey_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                PixabaySearchControl.SaveKey(PxKeyBox.Password.Trim());
                PxKeyStatusText.Text = PxKeyBox.Password.Length > 0 ? "Key saved ✓ — the search box at the top of this window now hunts Pixabay too." : "Key cleared.";
                RefreshAccountStatus();
            }
            catch (Exception ex) { PxKeyStatusText.Text = "Couldn't save the key: " + ex.Message; }
        }

        // ---------- v3.71: unified search + account status ----------

        private void RefreshAccountStatus()
        {
            bool fsOk = FreesoundSearchControl.LoadKey().Length > 0;
            FsAcctStatus.Text = fsOk
                ? "✅ Connected — your key is saved on this PC."
                : "⚠️ Not connected yet — paste your free API key below and press Save Key.";
            bool jmOk = JamendoSearchControl.LoadKey().Length > 0;
            JmAcctStatus.Text = jmOk
                ? "✅ Connected — your client ID is saved on this PC."
                : "⚠️ Not connected yet — paste your free client ID below and press Save ID.";
            bool pxOk = PixabaySearchControl.LoadKey().Length > 0;
            PxAcctStatus.Text = pxOk
                ? "✅ Connected — your key is saved on this PC."
                : "⚠️ Not connected yet — paste your free API key below and press Save Key.";
        }

        private async void HubSearch_Click(object sender, RoutedEventArgs e) => await HubSearchAsync();

        private async void HubQueryBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) await HubSearchAsync();
        }

        private async Task HubSearchAsync()
        {
            string q = HubQueryBox.Text.Trim();
            if (q.Length == 0)
            {
                HubStatusText.Text = "Type what you're hunting for first — a sound ('kick drum', 'vinyl crackle'), an artist, a song title…";
                return;
            }
            int scope = HubScopePicker.SelectedIndex;
            bool wantFs = scope is 0 or 1;
            bool wantIa = scope is 0 or 2 or 6;
            bool wantJm = scope is 0 or 3 or 6;
            bool wantAu = scope is 0 or 4 or 6;
            bool wantPx = scope is 0 or 5 or 6;

            HubSearchButton.IsEnabled = false;
            HubStatusText.Text = "Searching…";
            int fsN = -1, iaN = -1, jmN = -1, auN = -1, pxN = -1;
            try { if (wantFs) fsN = await _fs.HubSearchAsync(q); } catch { fsN = 0; }
            try { if (wantIa) iaN = await _ia.HubSearchAsync(q); } catch { iaN = 0; }
            try { if (wantJm) jmN = await _jm.HubSearchAsync(q); } catch { jmN = 0; }
            try { if (wantAu) auN = await _au.HubSearchAsync(q); } catch { auN = 0; }
            try { if (wantPx) pxN = await _px.HubSearchAsync(q); } catch { pxN = 0; }
            HubSearchButton.IsEnabled = true;

            var parts = new List<string>();
            if (fsN >= 0) parts.Add($"Freesound: {fsN}");
            if (iaN >= 0) parts.Add($"Archive: {iaN}");
            if (jmN >= 0) parts.Add($"Jamendo: {jmN}");
            if (auN >= 0) parts.Add($"Audius: {auN}");
            if (pxN >= 0) parts.Add($"Pixabay: {pxN}");
            string summary = string.Join(" · ", parts) + " — results are in the tabs below.";
            if (wantFs && fsN == 0 && FreesoundSearchControl.LoadKey().Length == 0)
                summary += " Freesound found nothing because no API key is saved yet — add your free key in 🔑 Accounts & Keys.";
            if (wantJm && jmN == 0 && JamendoSearchControl.LoadKey().Length == 0)
                summary += " Jamendo found nothing because no client ID is saved yet — add your free ID in 🔑 Accounts & Keys.";
            if (wantPx && pxN == 0 && PixabaySearchControl.LoadKey().Length == 0)
                summary += " Pixabay found nothing because no API key is saved yet — add your free key in 🔑 Accounts & Keys.";
            HubStatusText.Text = summary;

            // Land on the first tab that found something.
            if (fsN > 0) Tabs.SelectedIndex = 0;
            else if (iaN > 0) Tabs.SelectedIndex = 1;
            else if (jmN > 0) Tabs.SelectedIndex = 2;
            else if (auN > 0) Tabs.SelectedIndex = 3;
            else if (pxN > 0) Tabs.SelectedIndex = 4;
            else if (wantFs) Tabs.SelectedIndex = 0;
            else if (wantIa) Tabs.SelectedIndex = 1;
            else if (wantJm) Tabs.SelectedIndex = 2;
            else if (wantAu) Tabs.SelectedIndex = 3;
            else if (wantPx) Tabs.SelectedIndex = 4;
        }
    }
}
