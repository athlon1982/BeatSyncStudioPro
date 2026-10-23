using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using NAudio.Wave;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// Audius search panel — part of the unified 🔎 Search Sounds &amp;
    /// Music window (v3.79). Searches audius.co, the artist-run streaming
    /// co-op, through its public API. Unlike most sources the Audius API
    /// needs NO key or account at all — just an app_name parameter — so
    /// this panel works out of the box. Previews stream (the stream
    /// endpoint redirects to a content node, so the final URL is
    /// resolved first); ⬇ Add downloads the track — the artist's own
    /// download endpoint where the artist enabled downloads, otherwise
    /// the stream copy — and files it into the library, categorized.
    /// </summary>
    public partial class AudiusSearchControl : UserControl
    {
        private const string AppName = "BeatSyncStudioPro";
        private const string ApiBase = "https://discoveryprovider.audius.co/v1";

        private readonly MainWindow _owner;
        private readonly List<AuTrack> _results = new List<AuTrack>();
        private WaveOutEvent? _previewOut;
        private MediaFoundationReader? _previewReader;
        private int _previewIndex = -1;

        private record AuTrack(string Id, string Title, string Artist, double Seconds, string Genre, bool Downloadable, string OrigExt);

        public AudiusSearchControl(MainWindow owner)
        {
            InitializeComponent();
            _owner = owner;
        }

        private static string StreamUrlFor(string id) => $"{ApiBase}/tracks/{id}/stream?app_name={AppName}";
        private static string DownloadUrlFor(string id) => $"{ApiBase}/tracks/{id}/download?app_name={AppName}";

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
            string q = QueryBox.Text.Trim();
            if (q.Length == 0) { StatusText.Text = "Type a song, an artist, or a genre — 'hip-hop', 'lofi', 'electronic'…"; return; }

            SearchButton.IsEnabled = false;
            StatusText.Text = "Searching Audius…";
            try
            {
                using var http = NewHttp();
                string url = $"{ApiBase}/tracks/search?query={Uri.EscapeDataString(q)}&limit=30&app_name={AppName}";
                string body = await http.GetStringAsync(url);
                _results.Clear();
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("data", out var data)) { StatusText.Text = "Audius sent back something unexpected — try again."; return; }
                foreach (var r in data.EnumerateArray())
                {
                    string id = r.TryGetProperty("id", out var idv) ? idv.GetString() ?? "" : "";
                    if (id.Length == 0) continue;
                    string artist = "";
                    if (r.TryGetProperty("user", out var u))
                        artist = (u.TryGetProperty("name", out var un) ? un.GetString() : null)
                              ?? (u.TryGetProperty("handle", out var uh) ? uh.GetString() : null) ?? "";
                    string origExt = "";
                    if (r.TryGetProperty("orig_filename", out var of) && of.GetString() is string fn)
                    {
                        string ext = Path.GetExtension(fn).ToLowerInvariant();
                        if (ext is ".mp3" or ".wav" or ".flac" or ".m4a" or ".ogg") origExt = ext;
                    }
                    _results.Add(new AuTrack(
                        id,
                        r.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                        artist,
                        r.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0,
                        r.TryGetProperty("genre", out var g) ? g.GetString() ?? "" : "",
                        r.TryGetProperty("is_downloadable", out var dl) && dl.ValueKind == JsonValueKind.True,
                        origExt));
                }
                ResultsList.Items.Clear();
                foreach (var t in _results)
                    ResultsList.Items.Add($"{t.Artist} — {t.Title}  ·  {t.Seconds / 60:0}:{t.Seconds % 60:00}{(t.Genre.Length > 0 ? "  ·  " + t.Genre : "")}{(t.Downloadable ? "  ·  ⬇ artist download" : "")}");
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

        /// <summary>The stream endpoint 302s to a content node; resolve
        /// the final URL up front so MediaFoundationReader opens the
        /// real file instead of choking on the redirect.</summary>
        private async Task<string> ResolveStreamUrlAsync(string id)
        {
            try
            {
                using var http = NewHttp();
                using var req = new HttpRequestMessage(HttpMethod.Get, StreamUrlFor(id));
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                return resp.RequestMessage?.RequestUri?.ToString() ?? StreamUrlFor(id);
            }
            catch { return StreamUrlFor(id); }
        }

        private async void Preview_Click(object sender, RoutedEventArgs e)
        {
            int idx = ResultsList.SelectedIndex;
            if (idx < 0 || idx >= _results.Count) { StatusText.Text = "Select a song first."; return; }
            if (_previewIndex == idx && _previewOut != null) { StopPreview(); StatusText.Text = "Preview stopped."; return; }
            StopPreview();
            try
            {
                StatusText.Text = "Buffering the preview…";
                string url = await ResolveStreamUrlAsync(_results[idx].Id);
                _previewReader = new MediaFoundationReader(url);
                _previewOut = new WaveOutEvent();
                _previewOut.Init(_previewReader);
                _previewOut.Play();
                _previewIndex = idx;
                StatusText.Text = $"▶ {_results[idx].Artist} — {_results[idx].Title} (streaming from audius.co)";
            }
            catch (Exception ex) { StatusText.Text = "Preview failed: " + ex.Message; }
        }

        private async void Add_Click(object sender, RoutedEventArgs e)
        {
            int idx = ResultsList.SelectedIndex;
            if (idx < 0 || idx >= _results.Count) { StatusText.Text = "Select a song first."; return; }
            var t = _results[idx];
            string url = t.Downloadable ? DownloadUrlFor(t.Id) : StreamUrlFor(t.Id);
            string ext = t.Downloadable && t.OrigExt.Length > 0 ? t.OrigExt : ".mp3";
            try
            {
                StatusText.Text = $"Downloading {t.Title}… watch it in the ⬇ Downloads window (View menu).";
                byte[] bytes = await DownloadManager.DownloadBytesAsync(url, t.Title);
                string fileName = Sanitize((t.Artist.Length > 0 ? t.Artist + " - " : "") + t.Title) + ext;
                string category = SoundCategorizer.Infer(t.Title + " " + QueryBox.Text);
                string stored = _owner.AddToLibraryOnly(bytes, fileName, category);
                StatusText.Text = $"✅ Added '{stored}' to your library under {SoundCategorizer.Icon(category)} {category}. By {t.Artist} on audius.co"
                    + (t.Downloadable ? " — the artist-enabled download." : " — stream copy (the artist hasn't enabled downloads for this one).");
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
            return clean.Length == 0 ? "audius" : (clean.Length > 60 ? clean.Substring(0, 60) : clean);
        }
    }
}
