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
    /// Pixabay search panel — part of the unified 🔎 Search Sounds &amp;
    /// Music window (v3.80). Searches pixabay.com's royalty-free music /
    /// audio catalog through its API (the /api/audio/ endpoint takes the
    /// user's free Pixabay API key — DPAPI-encrypted in
    /// pixabay-settings.json, edited from the hub's 🔑 API Keys tab, the
    /// Jamendo/Freesound KeyProvider pattern). Pixabay doesn't publish
    /// the audio response schema in its public docs, so the parser reads
    /// the envelope (hits/results/data) and the audio URL defensively
    /// from the candidate fields the Pixabay API family uses; if a
    /// response ever arrives in a shape this doesn't recognize, the
    /// status line says exactly that instead of faking results.
    /// Pixabay Content License: free to use, attribution not required.
    /// </summary>
    public partial class PixabaySearchControl : UserControl
    {
        private readonly MainWindow _owner;
        private readonly List<PxTrack> _results = new List<PxTrack>();
        private WaveOutEvent? _previewOut;
        private MediaFoundationReader? _previewReader;
        private int _previewIndex = -1;

        /// <summary>Set by the hub: returns the key tab's current text.</summary>
        public Func<string>? KeyProvider { get; set; }

        private record PxTrack(int Id, string Title, string Artist, double Seconds, string Url);

        public PixabaySearchControl(MainWindow owner)
        {
            InitializeComponent();
            _owner = owner;
            if (LoadKey().Length == 0)
                StatusText.Text = "Add your free Pixabay API key to start — it's in this window's 🔑 API Keys tab (shown on pixabay.com/api/docs when you're signed in — free).";
        }

        // ---------- key storage (DPAPI, same pattern as Freesound/Jamendo) ----------

        private static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BeatSyncStudioPro", "pixabay-settings.json");

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

        public async Task<int> HubSearchAsync(string q)
        {
            QueryBox.Text = q;
            _results.Clear();
            ResultsList.Items.Clear();
            await SearchAsync();
            return _results.Count;
        }

        public void HideOwnChrome()
        {
            HeadText.Visibility = Visibility.Collapsed;
            QueryRow.Visibility = Visibility.Collapsed;
        }

        private async Task SearchAsync()
        {
            string key = CurrentKey();
            if (key.Length == 0) { StatusText.Text = "Add your Pixabay API key first (🔑 API Keys tab in this window)."; return; }
            string q = QueryBox.Text.Trim();
            if (q.Length == 0) { StatusText.Text = "Type a mood, a genre, or an instrument — 'cinematic piano', 'upbeat corporate', 'dark trap'…"; return; }

            SearchButton.IsEnabled = false;
            StatusText.Text = "Searching Pixabay…";
            try
            {
                using var http = NewHttp();
                string url = "https://pixabay.com/api/audio/?key=" + Uri.EscapeDataString(key)
                    + "&q=" + Uri.EscapeDataString(q) + "&per_page=30";
                string body = await http.GetStringAsync(url);
                _results.Clear();
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                JsonElement? hits = null;
                foreach (string field in new[] { "hits", "results", "data" })
                    if (root.TryGetProperty(field, out var arr) && arr.ValueKind == JsonValueKind.Array) { hits = arr; break; }
                if (hits == null)
                {
                    StatusText.Text = "Pixabay answered, but not in the shape this panel recognizes — tell Jarvis what you searched and he'll match the panel to Pixabay's reply. (Nothing was added or changed.)";
                    return;
                }
                int unparseable = 0;
                foreach (var r in hits.Value.EnumerateArray())
                {
                    string audio = FindAudioUrl(r);
                    if (audio.Length == 0) { unparseable++; continue; }
                    string title = Str(r, "title") ?? Str(r, "name") ?? "";
                    if (title.Length == 0)
                    {
                        string tags = Str(r, "tags") ?? "";
                        title = tags.Length > 0 ? tags.Split(',')[0].Trim() : Path.GetFileNameWithoutExtension(new Uri(audio).LocalPath);
                    }
                    _results.Add(new PxTrack(
                        Num(r, "id") is double idd ? (int)idd : 0,
                        title,
                        Str(r, "user") ?? Str(r, "artist") ?? Str(r, "author") ?? "",
                        Num(r, "duration") ?? Num(r, "duration_seconds") ?? 0,
                        audio));
                }
                ResultsList.Items.Clear();
                foreach (var t in _results)
                    ResultsList.Items.Add($"{(t.Artist.Length > 0 ? t.Artist + " — " : "")}{t.Title}  ·  {t.Seconds / 60:0}:{t.Seconds % 60:00}  ·  Pixabay License");
                StatusText.Text = _results.Count == 0
                    ? (unparseable > 0
                        ? "Pixabay sent results but none carried a playable audio link this panel recognizes — tell Jarvis and he'll match the panel to Pixabay's reply."
                        : "No matches — try different words.")
                    : $"{_results.Count} tracks. Click one to select, ▶ Preview to hear it, ⬇ to add it to your library.";
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("400") || ex.Message.Contains("401") || ex.Message.Contains("403"))
            {
                StatusText.Text = "Pixabay rejected the API key — check the key in the 🔑 API Keys tab (it lives on pixabay.com/api/docs when you're signed in).";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Search failed: " + ex.Message;
            }
            finally { SearchButton.IsEnabled = true; }
        }

        /// <summary>Find an audio-file URL on a hit, checking the fields
        /// the Pixabay API family uses (flat strings first, then a
        /// nested { url } object).</summary>
        private static string FindAudioUrl(JsonElement r)
        {
            foreach (string field in new[] { "audio", "url", "previewUrl", "preview_url", "download_url" })
            {
                if (!r.TryGetProperty(field, out var v)) continue;
                if (v.ValueKind == JsonValueKind.String && v.GetString() is string s && s.Length > 0) return s;
                if (v.ValueKind == JsonValueKind.Object && v.TryGetProperty("url", out var inner)
                    && inner.ValueKind == JsonValueKind.String && inner.GetString() is string si && si.Length > 0) return si;
            }
            return "";
        }

        private static string? Str(JsonElement r, string field)
            => r.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static double? Num(JsonElement r, string field)
            => r.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

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
            if (idx < 0 || idx >= _results.Count) { StatusText.Text = "Select a track first."; return; }
            if (_previewIndex == idx && _previewOut != null) { StopPreview(); StatusText.Text = "Preview stopped."; return; }
            StopPreview();
            try
            {
                _previewReader = new MediaFoundationReader(_results[idx].Url);
                _previewOut = new WaveOutEvent();
                _previewOut.Init(_previewReader);
                _previewOut.Play();
                _previewIndex = idx;
                StatusText.Text = $"▶ {_results[idx].Title} (streaming from pixabay.com)";
            }
            catch (Exception ex) { StatusText.Text = "Preview failed: " + ex.Message; }
        }

        private async void Add_Click(object sender, RoutedEventArgs e)
        {
            int idx = ResultsList.SelectedIndex;
            if (idx < 0 || idx >= _results.Count) { StatusText.Text = "Select a track first."; return; }
            var t = _results[idx];
            try
            {
                StatusText.Text = $"Downloading {t.Title}… watch it in the ⬇ Downloads window (View menu).";
                byte[] bytes = await DownloadManager.DownloadBytesAsync(t.Url, t.Title);
                string ext = Path.GetExtension(new Uri(t.Url).LocalPath);
                if (ext is not (".mp3" or ".wav" or ".flac" or ".m4a" or ".ogg")) ext = ".mp3";
                string fileName = Sanitize((t.Artist.Length > 0 ? t.Artist + " - " : "") + t.Title) + ext;
                string category = SoundCategorizer.Infer(t.Title + " " + QueryBox.Text);
                string stored = _owner.AddToLibraryOnly(bytes, fileName, category);
                StatusText.Text = $"✅ Added '{stored}' to your library under {SoundCategorizer.Icon(category)} {category}. Pixabay Content License (free to use, no credit required){(t.Artist.Length > 0 ? "; by " + t.Artist : "")} on pixabay.com.";
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
            return clean.Length == 0 ? "pixabay" : (clean.Length > 60 ? clean.Substring(0, 60) : clean);
        }
    }
}
