using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using NAudio.Wave;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// Internet Archive search panel — one half of the unified 🔎 Search
    /// Sounds &amp; Music window (v3.59; the standalone Archive window
    /// from v3.58 folded in here). Searches archive.org's huge free
    /// FULL-LENGTH music library — live concerts (the Live Music
    /// Archive), netlabel albums, community uploads — previews tracks,
    /// and adds them to the library. No account and no API key: the
    /// Archive's search + metadata APIs are open. An Archive "item" is
    /// usually an album or a show with many files, so results are the
    /// individual MP3 tracks inside the top-matching items, each with
    /// its license and creator shown for credit.
    /// </summary>
    public partial class ArchiveSearchControl : UserControl
    {
        private readonly MainWindow _owner;
        private readonly List<IaTrack> _results = new List<IaTrack>();
        private WaveOutEvent? _previewOut;
        private MediaFoundationReader? _previewReader;
        private int _previewIndex = -1;
        private int _searchGen;

        private record IaTrack(string Identifier, string ItemTitle, string Creator, string TrackName, double Seconds, string License, string Url);

        public ArchiveSearchControl(MainWindow owner)
        {
            InitializeComponent();
            _owner = owner;
        }

        // ---------- search ----------

        private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync();

        private async void QueryBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) await SearchAsync();
        }

        private string ScopeClause() => ScopePicker.SelectedIndex switch
        {
            1 => " AND collection:etree",
            2 => " AND collection:netlabels",
            3 => " AND collection:opensource_audio",
            _ => ""
        };

        private async Task SearchAsync()
        {
            string q = QueryBox.Text.Trim();
            if (q.Length == 0) { StatusText.Text = "Type what you're hunting for — an artist, a song title, 'bluegrass live', 'vinyl crackle'…"; return; }
            int gen = ++_searchGen;
            SearchButton.IsEnabled = false;
            ResultsList.Items.Clear();
            _results.Clear();
            StatusText.Text = "Searching the Internet Archive…";
            try
            {
                using var http = NewHttp();
                string query = $"({q}) AND mediatype:audio" + ScopeClause();
                string url = "https://archive.org/advancedsearch.php?q=" + Uri.EscapeDataString(query)
                    + "&fl[]=identifier&fl[]=title&fl[]=creator&fl[]=licenseurl"
                    + "&sort[]=downloads+desc&rows=15&output=json";
                string body = await http.GetStringAsync(url);
                var docs = new List<(string Id, string Title, string Creator, string License)>();
                long numFound = 0;
                using (var doc = JsonDocument.Parse(body))
                {
                    var resp = doc.RootElement.GetProperty("response");
                    numFound = resp.TryGetProperty("numFound", out var nf) ? nf.GetInt64() : 0;
                    foreach (var d in resp.GetProperty("docs").EnumerateArray())
                    {
                        string id = Str(d, "identifier");
                        if (id.Length == 0) continue;
                        docs.Add((id, Str(d, "title"), Str(d, "creator"), LicenseShort(Str(d, "licenseurl"))));
                    }
                }
                if (docs.Count == 0)
                {
                    StatusText.Text = "No matches — try different words, or widen the scope to All audio.";
                    return;
                }
                StatusText.Text = $"{numFound:N0} items match — pulling the tracks out of the top {docs.Count}…";
                // Each item is an album/show: fetch its file list and take
                // its MP3 tracks. A few at a time so the Archive isn't
                // hammered and results stream in steadily.
                using var gate = new SemaphoreSlim(5);
                var found = new List<IaTrack>[docs.Count];
                var tasks = docs.Select(async (d, i) =>
                {
                    await gate.WaitAsync();
                    try { found[i] = await FetchTracksAsync(http, d); }
                    catch { found[i] = new List<IaTrack>(); }
                    finally { gate.Release(); }
                }).ToList();
                await Task.WhenAll(tasks);
                if (gen != _searchGen) return;
                foreach (var list in found)
                {
                    foreach (var t in list)
                    {
                        if (_results.Count >= 48) break;
                        _results.Add(t);
                        ResultsList.Items.Add(RowText(t));
                    }
                    if (_results.Count >= 48) break;
                }
                StatusText.Text = _results.Count == 0
                    ? "Those items had no MP3 tracks in them — try different words."
                    : $"{_results.Count} tracks. Click one to select, ▶ Preview to hear it, ⬇ to add it to your library.";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Search failed: " + ex.Message;
            }
            finally { if (gen == _searchGen) SearchButton.IsEnabled = true; }
        }

        private async Task<List<IaTrack>> FetchTracksAsync(HttpClient http, (string Id, string Title, string Creator, string License) item)
        {
            var tracks = new List<IaTrack>();
            string body = await http.GetStringAsync("https://archive.org/metadata/" + Uri.EscapeDataString(item.Id));
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("files", out var files)) return tracks;
            string license = item.License;
            if (doc.RootElement.TryGetProperty("metadata", out var md))
            {
                string ml = Str(md, "licenseurl");
                if (ml.Length > 0) license = LicenseShort(ml);
            }
            foreach (var f in files.EnumerateArray())
            {
                if (tracks.Count >= 4) break;
                string format = Str(f, "format");
                string name = Str(f, "name");
                if (name.Length == 0) continue;
                // MP3s only (the app's playback sweet spot); skip the
                // Archive's auto-generated sample clips.
                if (!format.Contains("MP3") || format.Contains("Sample")) continue;
                if (!name.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)) continue;
                double seconds = 0;
                double.TryParse(Str(f, "length"), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out seconds);
                string trackName = Path.GetFileNameWithoutExtension(name);
                string display = item.Title.Length > 0 && !trackName.Contains(item.Title, StringComparison.OrdinalIgnoreCase)
                    ? item.Title + " — " + trackName
                    : trackName;
                string fileUrl = "https://archive.org/download/" + Uri.EscapeDataString(item.Id) + "/" + Uri.EscapeDataString(name);
                tracks.Add(new IaTrack(item.Id, item.Title, item.Creator, display, seconds, license, fileUrl));
            }
            return tracks;
        }

        private static string RowText(IaTrack t)
        {
            string dur = t.Seconds > 0 ? $"{(int)(t.Seconds / 60)}:{(int)(t.Seconds % 60):00}" : "?:??";
            string by = t.Creator.Length > 0 ? $" · by {t.Creator}" : "";
            return $"{t.TrackName}  ·  {dur}  ·  {t.License}{by}";
        }

        private static string Str(JsonElement el, string name)
        {
            if (!el.TryGetProperty(name, out var v)) return "";
            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() ?? "",
                JsonValueKind.Number => v.GetRawText(),
                JsonValueKind.Array => v.GetArrayLength() > 0 && v[0].ValueKind == JsonValueKind.String ? v[0].GetString() ?? "" : "",
                _ => ""
            };
        }

        private static string LicenseShort(string url) => url switch
        {
            var s when s.Contains("publicdomain") => "Public domain",
            var s when s.Contains("by-nc-sa") => "CC-BY-NC-SA (credit, non-commercial)",
            var s when s.Contains("by-nc") => "CC-BY-NC (credit, non-commercial)",
            var s when s.Contains("by-sa") => "CC-BY-SA (credit, share-alike)",
            var s when s.Contains("/by/") => "CC-BY (credit the author)",
            _ => "see archive.org"
        };

        private static HttpClient NewHttp()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
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
                StatusText.Text = $"▶ {_results[idx].TrackName} (streaming from archive.org — give it a second to buffer)";
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
                StatusText.Text = $"Downloading {t.TrackName}… watch it in the ⬇ Downloads window (View menu).";
                byte[] bytes = await DownloadManager.DownloadBytesAsync(t.Url, t.TrackName);
                string fileName = Sanitize(t.TrackName) + ".mp3";
                string category = SoundCategorizer.Infer(t.TrackName + " " + QueryBox.Text);
                string stored = _owner.AddToLibraryOnly(bytes, fileName, category);
                StatusText.Text = $"✅ Added '{stored}' to your library under {SoundCategorizer.Icon(category)} {category}. License: {t.License}; from archive.org/details/{t.Identifier}" + (t.Creator.Length > 0 ? $" — credit: {t.Creator}." : ".");
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
            return clean.Length == 0 ? "archive-track" : (clean.Length > 60 ? clean.Substring(0, 60) : clean);
        }
    }
}
