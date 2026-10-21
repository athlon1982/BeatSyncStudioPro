using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;

namespace BeatSyncStudioPro
{
    public class SongEntry
    {
        public string Title { get; set; } = "";
        public string Url { get; set; } = "";
        public string Source { get; set; } = "Suno";
    }

    /// <summary>
    /// My Songs: the user signs into their OWN Suno / SoundCloud account in
    /// the embedded player (the sites handle the login — the app never sees
    /// a password, and the session persists on this PC). Song links are read
    /// off the page being viewed into a clickable list; opening a song pulls
    /// its cover and lyrics from the page when they're there. Nothing is
    /// preloaded and no account's songs are baked into the app: every user
    /// sees their own library. A personal quick list (added by hand) is kept
    /// locally. Neither service offers an API for this, so everything works
    /// by reading the signed-in pages themselves, and degrades gracefully.
    /// </summary>
    public partial class MySongsWindow : Window
    {
        private readonly List<SongEntry> _songs = new List<SongEntry>();
        private readonly List<SongEntry> _found = new List<SongEntry>();
        private bool _playerReady;

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BeatSyncStudioPro", "my-songs.json");

        // v3.4 briefly shipped with the developer's own songs seeded as a
        // first-run list. That was wrong for a shared app — those entries
        // are cleaned out of any saved quick list on sight.
        private static readonly HashSet<string> LegacySeedUrls = new HashSet<string>
        {
            "https://suno.com/song/73d174f4-bebc-4f1d-b19c-6b9472c728d6",
            "https://suno.com/song/e08e0c28-7fb5-452b-b311-809e779fa7ed",
            "https://suno.com/song/13a0894d-0899-4122-aa6f-35995d1dc814",
            "https://suno.com/song/6887c53f-42bf-4c0b-a78b-0bf4188b435c",
            "https://suno.com/song/8db98159-3c55-49c4-9fb1-65e068eaffab",
            "https://suno.com/song/99b6771f-05fb-4012-9e0d-f91cbf191612",
            "https://suno.com/song/d962cde5-0839-44a8-8036-13c0596b7478",
            "https://suno.com/song/50b68f04-c409-449a-8eea-79119081e03d",
            "https://suno.com/song/d19a4bfe-a9b0-4540-bc0f-fb90127caa53",
            "https://suno.com/song/1a9180d1-9b5b-4bde-b067-06be67e0bee0",
            "https://suno.com/song/6ff04104-83a9-4834-8c04-0b57be507825",
            "https://suno.com/song/08933eb3-7d64-40ad-bc0b-da2fc0939800",
            "https://suno.com/song/961e3cbc-07aa-4c2d-9389-ab1b5484c5f3",
            "https://suno.com/song/393d36a7-2f9e-4125-a62d-1318f600a0f9",
            "https://suno.com/song/2a5e51e1-4780-453b-88bc-3bd174baa7ed",
            "https://suno.com/song/8e0b2f49-3479-43e7-af91-7df1cbd6a8b0",
            "https://suno.com/song/3ab57c6c-79a0-4f35-acf9-3227fde1e8fc"
        };

        public MySongsWindow()
        {
            InitializeComponent();
            LoadSongs();
            RefreshList();
            Loaded += async (s, e) =>
            {
                try
                {
                    string userData = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "BeatSyncStudioPro", "WebView2");
                    Directory.CreateDirectory(userData);
                    var env = await CoreWebView2Environment.CreateAsync(null, userData);
                    await Player.EnsureCoreWebView2Async(env);
                    _playerReady = true;
                    Player.CoreWebView2.NavigationCompleted += async (s2, args) =>
                    {
                        if (args.IsSuccess)
                        {
                            await ScrapeDetailsAsync();
                            await ScrapeListAsync();
                        }
                    };
                    Navigate("https://suno.com");
                }
                catch
                {
                    Player.Visibility = Visibility.Collapsed;
                    FallbackText.Visibility = Visibility.Visible;
                }
            };
        }

        // ---------- quick list (the user's own, saved locally) ----------

        private void LoadSongs()
        {
            _songs.Clear();
            try
            {
                if (File.Exists(FilePath))
                {
                    var loaded = JsonSerializer.Deserialize<List<SongEntry>>(File.ReadAllText(FilePath));
                    if (loaded != null)
                    {
                        foreach (var entry in loaded)
                            if (!LegacySeedUrls.Contains(entry.Url)) _songs.Add(entry);
                        if (loaded.Count != _songs.Count) SaveSongs(); // legacy seeds were present — persist the cleanup
                    }
                }
            }
            catch { }
        }

        private void SaveSongs()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(_songs, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        private void RefreshList()
        {
            SongsList.Items.Clear();
            foreach (var song in _songs) SongsList.Items.Add(song);
            SongsList.DisplayMemberPath = "Title";
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            string url = UrlBox.Text.Trim();
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("Paste a full link first (https://suno.com/song/… or a SoundCloud track link).");
                return;
            }
            string source = url.Contains("soundcloud.com") ? "SoundCloud" : url.Contains("suno.com") ? "Suno" : "Web";
            string title = TitleBox.Text.Trim();
            if (title.Length == 0) title = source + " song";
            _songs.Add(new SongEntry { Title = title, Url = url, Source = source });
            SaveSongs();
            RefreshList();
            SongsList.SelectedIndex = _songs.Count - 1;
            TitleBox.Text = "";
            UrlBox.Text = "";
        }

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            if (SongsList.SelectedItem is SongEntry entry)
            {
                _songs.Remove(entry);
                SaveSongs();
                RefreshList();
            }
        }

        // ---------- player navigation ----------

        private void Navigate(string url)
        {
            if (_playerReady && Player.CoreWebView2 != null)
                Player.CoreWebView2.Navigate(url);
        }

        private static string EmbedUrl(SongEntry entry)
        {
            if (entry.Source == "SoundCloud" || entry.Url.Contains("soundcloud.com"))
                return "https://w.soundcloud.com/player/?url=" + Uri.EscapeDataString(entry.Url) + "&auto_play=false";
            return entry.Url;
        }

        private void SongsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SongsList.SelectedItem is SongEntry entry)
            {
                DetailTitle.Text = entry.Title;
                Navigate(EmbedUrl(entry));
            }
        }

        private void FoundList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FoundList.SelectedItem is SongEntry entry)
            {
                DetailTitle.Text = entry.Title;
                Navigate(EmbedUrl(entry));
            }
        }

        private void NavSuno_Click(object sender, RoutedEventArgs e) => Navigate("https://suno.com");
        private void NavSunoLibrary_Click(object sender, RoutedEventArgs e) => Navigate("https://suno.com/me");
        private void NavSoundCloud_Click(object sender, RoutedEventArgs e) => Navigate("https://soundcloud.com");

        private async void RefreshFound_Click(object sender, RoutedEventArgs e) => await ScrapeListAsync();

        private void OpenBrowser_Click(object sender, RoutedEventArgs e)
        {
            string url = Player.CoreWebView2?.Source ?? "https://suno.com";
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show("Could not open the browser: " + ex.Message); }
        }

        // ---------- page scraping (details + song links) ----------

        private async Task<string> RunScriptAsync(string script)
        {
            if (!_playerReady || Player.CoreWebView2 == null) return "";
            try
            {
                string raw = await Player.CoreWebView2.ExecuteScriptAsync(script);
                return JsonSerializer.Deserialize<string>(raw) ?? "";
            }
            catch { return ""; }
        }

        private async Task ScrapeDetailsAsync()
        {
            const string script = @"(() => {
  const meta = (p) => { const el = document.querySelector('meta[property=""' + p + '""]'); return el ? (el.content || '') : ''; };
  let lyrics = '';
  try {
    const nd = document.getElementById('__NEXT_DATA__');
    if (nd) {
      const m = nd.textContent.match(/""lyrics""\s*:\s*""((?:\\.|[^""\\])*)""/);
      if (m) lyrics = JSON.parse('""' + m[1] + '""');
    }
  } catch (e) {}
  if (!lyrics) {
    const el = document.querySelector('[class*=""lyric"" i], [data-testid*=""lyric"" i]');
    if (el) lyrics = el.innerText || '';
  }
  return JSON.stringify({ title: meta('og:title') || document.title, image: meta('og:image'), lyrics: lyrics });
})()";
            string json = await RunScriptAsync(script);
            if (json.Length == 0) return;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                string title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                string image = root.TryGetProperty("image", out var im) ? im.GetString() ?? "" : "";
                string lyrics = root.TryGetProperty("lyrics", out var ly) ? ly.GetString() ?? "" : "";
                string url = Player.CoreWebView2?.Source ?? "";
                bool isSongPage = url.Contains("/song/") || (url.Contains("soundcloud.com") && url.Split('/').Length >= 5);
                if (!isSongPage) return;
                if (title.Length > 0) DetailTitle.Text = title;
                if (lyrics.Length > 0) DetailLyrics.Text = lyrics;
                else DetailLyrics.Text = "This page didn't include lyrics.";
                if (image.Length > 0)
                {
                    try { CoverImage.Source = new BitmapImage(new Uri(image)); }
                    catch { CoverImage.Source = null; }
                }
            }
            catch { }
        }

        private async Task ScrapeListAsync()
        {
            const string script = @"(() => {
  const out = []; const seen = new Set();
  const skip = ['you','stream','discover','upload','search','pages','settings','messages','notifications','charts','people'];
  document.querySelectorAll('a[href]').forEach(a => {
    const href = a.href.split('#')[0];
    let isSong = /suno\.com\/song\/[0-9a-f-]+/i.test(href);
    if (!isSong && href.indexOf('soundcloud.com/') >= 0) {
      try {
        const parts = new URL(href).pathname.split('/').filter(Boolean);
        if (parts.length === 2 && skip.indexOf(parts[0]) < 0) isSong = true;
      } catch (e) {}
    }
    if (isSong && !seen.has(href)) {
      seen.add(href);
      let title = (a.innerText || a.getAttribute('aria-label') || '').trim().split('\n')[0].slice(0, 80);
      out.push({ url: href, title: title });
    }
  });
  return JSON.stringify(out.slice(0, 60));
})()";
            string json = await RunScriptAsync(script);
            if (json.Length == 0) return;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var fresh = new List<SongEntry>();
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    string url = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                    string title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    if (url.Length == 0) continue;
                    if (title.Length == 0)
                    {
                        string tail = url.TrimEnd('/').Split('/')[^1];
                        title = url.Contains("suno.com") ? "Suno song " + tail.Substring(0, Math.Min(8, tail.Length)) : tail.Replace('-', ' ');
                    }
                    fresh.Add(new SongEntry
                    {
                        Title = title,
                        Url = url,
                        Source = url.Contains("soundcloud.com") ? "SoundCloud" : "Suno"
                    });
                }
                if (fresh.Count > 0)
                {
                    _found.Clear();
                    _found.AddRange(fresh);
                    FoundList.Items.Clear();
                    foreach (var song in _found) FoundList.Items.Add(song);
                    FoundList.DisplayMemberPath = "Title";
                }
            }
            catch { }
        }
    }
}
