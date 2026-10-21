using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using NAudio.Wave;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// Freesound browser: search freesound.org's huge library of licensed
    /// samples from inside the app, preview them, and add them to the
    /// library (drum kits, patterns, timeline). Uses the user's own free
    /// Freesound API key (freesound.org/apiv2/apply), stored encrypted.
    /// Adds are the high-quality preview MP3; every result shows its
    /// license and author so credit is one copy-paste away.
    /// </summary>
    public partial class FreesoundWindow : Window
    {
        private readonly MainWindow _owner;
        private readonly List<FsSound> _results = new List<FsSound>();
        private WaveOutEvent? _previewOut;
        private MediaFoundationReader? _previewReader;
        private int _previewIndex = -1;

        private record FsSound(int Id, string Name, string User, double Seconds, string License, string PreviewUrl);

        private static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BeatSyncStudioPro", "freesound-settings.json");

        public FreesoundWindow(MainWindow owner)
        {
            InitializeComponent();
            _owner = owner;
            KeyBox.Password = LoadKey();
            Closed += (s, e) => StopPreview();
            if (KeyBox.Password.Length == 0)
                StatusText.Text = "Add your free Freesound API key to start — it's in the 🔑 Connect tab (the client secret they give you).";
        }

        // ---------- key storage (DPAPI, same pattern as the AI keys) ----------

        private static string LoadKey()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return "";
                using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                if (!doc.RootElement.TryGetProperty("key", out var k) || k.GetString() is not string enc || enc.Length == 0) return "";
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(enc), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch { return ""; }
        }

        private void Signup_Click(object sender, RoutedEventArgs e)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://freesound.org/apiv2/apply") { UseShellExecute = true }); }
            catch { }
            StatusText.Text = "Opened freesound.org in your browser — sign up there (free), create an API key on that page, then paste the client secret here and Save Key.";
        }

        private void SaveKey_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                string enc = KeyBox.Password.Length == 0 ? "" :
                    Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(KeyBox.Password.Trim()), null, DataProtectionScope.CurrentUser));
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { key = enc }));
                StatusText.Text = KeyBox.Password.Length > 0 ? "Key saved ✓ — search away." : "Key cleared.";
            }
            catch (Exception ex) { StatusText.Text = "Couldn't save the key: " + ex.Message; }
        }

        // ---------- search ----------

        private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync();

        private async void QueryBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) await SearchAsync();
        }

        private async Task SearchAsync()
        {
            string key = KeyBox.Password.Trim();
            if (key.Length == 0) { StatusText.Text = "Add your Freesound API key first (🔑 Connect tab)."; return; }
            string q = QueryBox.Text.Trim();
            if (q.Length == 0) { StatusText.Text = "Type what you're hunting for — 'kick drum', 'vinyl crackle', 'rain'…"; return; }

            SearchButton.IsEnabled = false;
            StatusText.Text = "Searching Freesound…";
            try
            {
                using var http = NewHttp(key);
                string url = "https://freesound.org/apiv2/search/text/?query=" + Uri.EscapeDataString(q)
                    + "&fields=id,name,username,duration,license,previews&page_size=30&sort=rating_desc";
                string body = await http.GetStringAsync(url);
                _results.Clear();
                using var doc = JsonDocument.Parse(body);
                foreach (var r in doc.RootElement.GetProperty("results").EnumerateArray())
                {
                    string preview = "";
                    if (r.TryGetProperty("previews", out var pv) && pv.TryGetProperty("preview-hq-mp3", out var hq))
                        preview = hq.GetString() ?? "";
                    if (preview.Length == 0) continue;
                    _results.Add(new FsSound(
                        r.GetProperty("id").GetInt32(),
                        r.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        r.TryGetProperty("username", out var u) ? u.GetString() ?? "" : "",
                        r.TryGetProperty("duration", out var d) ? d.GetDouble() : 0,
                        LicenseShort(r.TryGetProperty("license", out var l) ? l.GetString() ?? "" : ""),
                        preview));
                }
                ResultsList.Items.Clear();
                foreach (var s in _results)
                    ResultsList.Items.Add($"{s.Name}  ·  {s.Seconds:0.0}s  ·  {s.License}  ·  by {s.User}");
                StatusText.Text = _results.Count == 0
                    ? "No matches with previews — try different words."
                    : $"{_results.Count} sounds. Click one to select, ▶ Preview to hear it, ⬇ to add it to your library.";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Search failed: " + ex.Message + " (a 401 usually means the key isn't right — it's the client secret from freesound.org/apiv2/apply).";
            }
            finally { SearchButton.IsEnabled = true; }
        }

        private static string LicenseShort(string url) => url switch
        {
            var s when s.Contains("publicdomain") => "CC0 (free, no credit needed)",
            var s when s.Contains("/by/") => "CC-BY (credit the author)",
            var s when s.Contains("by-nc") => "CC-BY-NC (credit, non-commercial)",
            var s when s.Contains("by-sa") => "CC-BY-SA (credit, share-alike)",
            _ => "see freesound.org"
        };

        private static HttpClient NewHttp(string key)
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Token " + key);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("BeatSyncStudioPro");
            return http;
        }

        // ---------- preview + add ----------

        private void StopPreview()
        {
            try { _previewOut?.Stop(); _previewOut?.Dispose(); _previewReader?.Dispose(); } catch { }
            _previewOut = null; _previewReader = null; _previewIndex = -1;
        }

        private void Preview_Click(object sender, RoutedEventArgs e)
        {
            int idx = ResultsList.SelectedIndex;
            if (idx < 0 || idx >= _results.Count) { StatusText.Text = "Select a sound first."; return; }
            if (_previewIndex == idx && _previewOut != null) { StopPreview(); StatusText.Text = "Preview stopped."; return; }
            StopPreview();
            try
            {
                _previewReader = new MediaFoundationReader(_results[idx].PreviewUrl);
                _previewOut = new WaveOutEvent();
                _previewOut.Init(_previewReader);
                _previewOut.Play();
                _previewIndex = idx;
                StatusText.Text = $"▶ {_results[idx].Name}";
            }
            catch (Exception ex) { StatusText.Text = "Preview failed: " + ex.Message; }
        }

        private async void Add_Click(object sender, RoutedEventArgs e)
        {
            int idx = ResultsList.SelectedIndex;
            if (idx < 0 || idx >= _results.Count) { StatusText.Text = "Select a sound first."; return; }
            var s = _results[idx];
            try
            {
                StatusText.Text = $"Downloading {s.Name}… watch it in the ⬇ Downloads window (View menu).";
                byte[] bytes = await DownloadManager.DownloadBytesAsync(s.PreviewUrl, s.Name);
                string fileName = Sanitize(s.Name) + ".mp3";
                string category = SoundCategorizer.Infer(s.Name + " " + QueryBox.Text);
                string stored = _owner.AddToLibraryOnly(bytes, fileName, category);
                StatusText.Text = $"✅ Added '{stored}' to your library under {SoundCategorizer.Icon(category)} {category} — find it with the library's category dropdown. License: {s.License}; sound by {s.User} on freesound.org (sound #{s.Id}).";
            }
            catch (OperationCanceledException) { StatusText.Text = "Download cancelled."; }
            catch (Exception ex) { StatusText.Text = "Add failed: " + ex.Message; }
        }

        private static string Sanitize(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in name)
                sb.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
            string clean = sb.ToString().Trim();
            return clean.Length == 0 ? "freesound" : (clean.Length > 60 ? clean.Substring(0, 60) : clean);
        }
    }
}
