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
    /// Jamendo search panel — part of the unified 🔎 Search Sounds &amp;
    /// Music window (v3.66). Searches jamendo.com's library of free,
    /// Creative Commons-licensed full songs by independent artists, using
    /// the user's own free Jamendo client ID (the Freesound key pattern:
    /// DPAPI-encrypted in jamendo-settings.json, edited from the hub's
    /// 🔑 API Keys tab, KeyProvider lets an unsaved edit count at once).
    /// Previews stream from Jamendo; ⬇ Add downloads the track (the
    /// artist-approved download URL when Jamendo offers one) into the
    /// library, categorized like the Archive adds.
    /// </summary>
    public partial class JamendoSearchControl : UserControl
    {
        private readonly MainWindow _owner;
        private readonly List<JmTrack> _results = new List<JmTrack>();
        private WaveOutEvent? _previewOut;
        private MediaFoundationReader? _previewReader;
        private int _previewIndex = -1;

        /// <summary>Set by the hub: returns the key tab's current text.</summary>
        public Func<string>? KeyProvider { get; set; }

        private record JmTrack(int Id, string Name, string Artist, double Seconds, string License, string StreamUrl, string DownloadUrl);

        public JamendoSearchControl(MainWindow owner)
        {
            InitializeComponent();
            _owner = owner;
            if (LoadKey().Length == 0)
                StatusText.Text = "Add your free Jamendo client ID to start — it's in this window's 🔑 API Keys tab (from devportal.jamendo.com, free).";
        }

        // ---------- key storage (DPAPI, same pattern as the Freesound key) ----------

        private static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BeatSyncStudioPro", "jamendo-settings.json");

        public static string LoadKey()
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

        public static void SaveKey(string key)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            string enc = key.Length == 0 ? "" :
                Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), null, DataProtectionScope.CurrentUser));
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { key = enc }));
        }

        private string CurrentKey()
        {
            try { string live = KeyProvider?.Invoke() ?? ""; if (live.Trim().Length > 0) return live.Trim(); } catch { }
            return LoadKey();
        }

        // ---------- search ----------

        private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync();

        private async void QueryBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) await SearchAsync();
        }

        // ---------- hub integration (v3.71 unified search) ----------

        /// <summary>Run a search driven by the hub's single search box.
        /// Returns how many results landed (0 also when no client ID is
        /// saved or the query is empty — this panel's status line says why).</summary>
        public async Task<int> HubSearchAsync(string q)
        {
            QueryBox.Text = q;
            _results.Clear();
            ResultsList.Items.Clear();
            await SearchAsync();
            return _results.Count;
        }

        /// <summary>Hide this panel's own header + search row — the hub's
        /// unified search box drives the searching instead.</summary>
        public void HideOwnChrome()
        {
            HeadText.Visibility = Visibility.Collapsed;
            QueryRow.Visibility = Visibility.Collapsed;
        }

        private async Task SearchAsync()
        {
            string key = CurrentKey();
            if (key.Length == 0) { StatusText.Text = "Add your Jamendo client ID first (🔑 API Keys tab in this window)."; return; }
            string q = QueryBox.Text.Trim();
            if (q.Length == 0) { StatusText.Text = "Type a song, an artist, or a mood — 'upbeat indie', 'chill piano', 'workout'…"; return; }

            SearchButton.IsEnabled = false;
            StatusText.Text = "Searching Jamendo…";
            try
            {
                using var http = NewHttp();
                string url = "https://api.jamendo.com/v3.0/tracks/?client_id=" + Uri.EscapeDataString(key)
                    + "&format=json&limit=30&audioformat=mp32&search=" + Uri.EscapeDataString(q);
                string body = await http.GetStringAsync(url);
                _results.Clear();
                using var doc = JsonDocument.Parse(body);
                // Jamendo reports a bad client ID as HTTP 200 with a failed header.
                if (doc.RootElement.TryGetProperty("headers", out var hdr)
                    && hdr.TryGetProperty("status", out var st) && st.GetString() == "failed")
                {
                    string msg = hdr.TryGetProperty("error_message", out var em) ? em.GetString() ?? "unknown error" : "unknown error";
                    StatusText.Text = "Jamendo said no: " + msg + " (a wrong client ID is the usual cause — check the 🔑 API Keys tab).";
                    return;
                }
                if (!doc.RootElement.TryGetProperty("results", out var results)) { StatusText.Text = "Jamendo sent back something unexpected — try again."; return; }
                foreach (var r in results.EnumerateArray())
                {
                    string stream = r.TryGetProperty("audio", out var a) ? a.GetString() ?? "" : "";
                    if (stream.Length == 0) continue;
                    string download = r.TryGetProperty("audiodownload", out var ad) ? ad.GetString() ?? "" : "";
                    _results.Add(new JmTrack(
                        r.TryGetProperty("id", out var idv) && idv.ValueKind == JsonValueKind.Number ? idv.GetInt32() : 0,
                        r.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        r.TryGetProperty("artist_name", out var an) ? an.GetString() ?? "" : "",
                        r.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0,
                        LicenseShort(r.TryGetProperty("license_ccurl", out var l) ? l.GetString() ?? "" : ""),
                        stream,
                        download));
                }
                ResultsList.Items.Clear();
                foreach (var t in _results)
                    ResultsList.Items.Add($"{t.Artist} — {t.Name}  ·  {t.Seconds / 60:0}:{t.Seconds % 60:00}  ·  {t.License}");
                StatusText.Text = _results.Count == 0
                    ? "No matches — try different words."
                    : $"{_results.Count} songs. Click one to select, ▶ Preview to hear it, ⬇ to add it to your library.";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Search failed: " + ex.Message;
            }
            finally { SearchButton.IsEnabled = true; }
        }

        private static string LicenseShort(string url)
        {
            string s = url.ToLowerInvariant();
            if (s.Contains("publicdomain") || s.Contains("/zero/")) return "CC0 (free, no credit needed)";
            if (s.Contains("by-nc-sa")) return "CC-BY-NC-SA (credit, non-commercial, share-alike)";
            if (s.Contains("by-nc-nd")) return "CC-BY-NC-ND (credit, non-commercial, no derivatives)";
            if (s.Contains("by-nc")) return "CC-BY-NC (credit, non-commercial)";
            if (s.Contains("by-sa")) return "CC-BY-SA (credit, share-alike)";
            if (s.Contains("by-nd")) return "CC-BY-ND (credit, no derivatives)";
            if (s.Contains("/by/") || s.Contains("by/")) return "CC-BY (credit the artist)";
            return url.Length == 0 ? "see jamendo.com" : "CC license (see jamendo.com)";
        }

        private static HttpClient NewHttp()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("BeatSyncStudioPro");
            return http;
        }

        // ---------- preview + add ----------

        public void StopPreview()
        {
            try { _previewOut?.Stop(); _previewOut?.Dispose(); _previewReader?.Dispose(); } catch { }
            _previewOut = null; _previewReader = null; _previewIndex = -1;
        }

        private void Preview_Click(object sender, RoutedEventArgs e)
        {
            int idx = ResultsList.SelectedIndex;
            if (idx < 0 || idx >= _results.Count) { StatusText.Text = "Select a song first."; return; }
            if (_previewIndex == idx && _previewOut != null) { StopPreview(); StatusText.Text = "Preview stopped."; return; }
            StopPreview();
            try
            {
                _previewReader = new MediaFoundationReader(_results[idx].StreamUrl);
                _previewOut = new WaveOutEvent();
                _previewOut.Init(_previewReader);
                _previewOut.Play();
                _previewIndex = idx;
                StatusText.Text = $"▶ {_results[idx].Artist} — {_results[idx].Name} (streaming from jamendo.com — give it a second to buffer)";
            }
            catch (Exception ex) { StatusText.Text = "Preview failed: " + ex.Message; }
        }

        private async void Add_Click(object sender, RoutedEventArgs e)
        {
            int idx = ResultsList.SelectedIndex;
            if (idx < 0 || idx >= _results.Count) { StatusText.Text = "Select a song first."; return; }
            var t = _results[idx];
            string url = t.DownloadUrl.Length > 0 ? t.DownloadUrl : t.StreamUrl;
            try
            {
                StatusText.Text = $"Downloading {t.Name}… watch it in the ⬇ Downloads window (View menu).";
                byte[] bytes = await DownloadManager.DownloadBytesAsync(url, t.Name);
                string fileName = Sanitize((t.Artist.Length > 0 ? t.Artist + " - " : "") + t.Name) + ".mp3";
                string stored = _owner.AddSongToLibrary(bytes, fileName);
                StatusText.Text = stored.Length > 0
                    ? $"✅ Added '{stored}' to your 🎵 Song Library — it's in the track list now, ready for the decks. License: {t.License}; by {t.Artist} on jamendo.com (track #{t.Id}) — credit the artist when you use it."
                    : "Add failed: the song couldn't be saved into the library folder.";
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
            return clean.Length == 0 ? "jamendo" : (clean.Length > 60 ? clean.Substring(0, 60) : clean);
        }
    }
}
