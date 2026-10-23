using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// 🎵 Song Library window (v3.72): Michael's crate of full songs.
    /// Add a folder, a ZIP, or loose files — every song's ID tags are
    /// read (TagReader), the grid sorts by any column, missing BPM /
    /// key can be analyzed with the deck detectors, and any song loads
    /// straight onto a DJ deck or into a deck's track list.
    /// </summary>
    public partial class SongLibraryWindow : Window
    {
        private readonly MainWindow _owner;
        private readonly ObservableCollection<LibrarySong> _songs = new ObservableCollection<LibrarySong>();
        private readonly ICollectionView _view;
        private bool _busy;

        // Suno tab state
        private readonly ObservableCollection<SunoSong> _sunoSongs = new ObservableCollection<SunoSong>();
        private bool _sunoInitStarted;
        private bool _sunoBrowserReady;
        private bool _pendingSync;
        private int _syncStep;               // 0 idle, 1 = library page loaded, 2 = profile page loaded
        private string _profileHref = "";
        private List<SunoSong> _syncFound = new List<SunoSong>();

        public SongLibraryWindow(MainWindow owner)
        {
            InitializeComponent();
            _owner = owner;
            var loaded = SongLibraryStore.Load();
            int missing = 0;
            foreach (var e in loaded)
            {
                if (File.Exists(e.Path)) _songs.Add(e);
                else missing++;
            }
            _view = CollectionViewSource.GetDefaultView(_songs);
            _view.Filter = FilterPredicate;
            SongGrid.ItemsSource = _view;
            UpdateCount();
            LibDirText.Text = SongLibraryStore.LibraryDir;
            CopyImportsBox.IsChecked = SongLibraryStore.CopyImports;
            foreach (var s in SunoLibraryStore.Load()) _sunoSongs.Add(s);
            SunoGrid.ItemsSource = _sunoSongs;
            UpdateSunoCount();
            if (missing > 0)
                StatusText.Text = $"{missing} song(s) were dropped from the list because their files moved or were deleted. {_songs.Count} song(s) in the library.";
            Closed += (s, e) => SongLibraryStore.Save(_songs.ToList());
        }

        private void UpdateCount() => CountText.Text = $"{_songs.Count} song(s)";

        private bool FilterPredicate(object obj)
        {
            if (obj is not LibrarySong e) return false;
            string f = FilterBox.Text.Trim();
            if (f.Length == 0) return true;
            return e.DisplayTitle.Contains(f, StringComparison.OrdinalIgnoreCase)
                || e.Artist.Contains(f, StringComparison.OrdinalIgnoreCase)
                || e.Album.Contains(f, StringComparison.OrdinalIgnoreCase)
                || e.Genre.Contains(f, StringComparison.OrdinalIgnoreCase)
                || System.IO.Path.GetFileName(e.Path).Contains(f, StringComparison.OrdinalIgnoreCase);
        }

        private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => _view.Refresh();

        // ---------- adding songs ----------

        private void AddEntries(List<LibrarySong> found, string sourceDesc)
        {
            var have = new HashSet<string>(_songs.Select(s => s.Path), StringComparer.OrdinalIgnoreCase);
            int added = 0, dupes = 0;
            foreach (var e in found)
            {
                if (have.Contains(e.Path)) { dupes++; continue; }
                have.Add(e.Path);
                _songs.Add(e);
                added++;
            }
            SongLibraryStore.Save(_songs.ToList());
            UpdateCount();
            StatusText.Text = added > 0
                ? $"Added {added} song(s) from {sourceDesc}" + (dupes > 0 ? $" — {dupes} already in the library, skipped." : ".")
                : $"No new songs from {sourceDesc}" + (dupes > 0 ? $" — {dupes} already in the library." : " — nothing readable found.");
        }

        private async void AddFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            var dlg = new OpenFolderDialog { Title = "Pick the folder your songs live in — everything inside gets scanned" };
            if (dlg.ShowDialog() != true) return;
            _busy = true;
            StatusText.Text = "Scanning folder — reading ID tags…";
            string root = dlg.FolderName;
            bool copy = SongLibraryStore.CopyImports;
            var found = await Task.Run(() =>
            {
                var list = new List<LibrarySong>();
                try
                {
                    foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    {
                        if (!SongLibraryStore.IsAudioPath(f)) continue;
                        string finalPath = copy ? SongLibraryStore.CopyIntoLibrary(f) : f;
                        var entry = SongLibraryStore.ScanFile(finalPath);
                        if (entry != null) list.Add(entry);
                    }
                }
                catch { /* partial scans still count */ }
                return list;
            });
            _busy = false;
            AddEntries(found, $"folder '{System.IO.Path.GetFileName(root)}'");
        }

        private async void AddZip_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            var dlg = new OpenFileDialog
            {
                Title = "Pick a ZIP full of songs",
                Filter = "ZIP archives|*.zip",
                Multiselect = true
            };
            if (dlg.ShowDialog() != true) return;
            _busy = true;
            StatusText.Text = "Unpacking ZIP — reading ID tags…";
            var found = new List<LibrarySong>();
            foreach (var zip in dlg.FileNames)
                found.AddRange(await Task.Run(() => SongLibraryStore.ScanZip(zip)));
            _busy = false;
            AddEntries(found, dlg.FileNames.Length == 1 ? $"ZIP '{System.IO.Path.GetFileName(dlg.FileNames[0])}'" : $"{dlg.FileNames.Length} ZIPs");
        }

        private async void AddFiles_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            var dlg = new OpenFileDialog
            {
                Title = "Pick songs to add",
                Filter = "Audio|*.mp3;*.flac;*.wav;*.ogg;*.m4a;*.aac;*.aif;*.aiff",
                Multiselect = true
            };
            if (dlg.ShowDialog() != true) return;
            _busy = true;
            StatusText.Text = "Reading ID tags…";
            bool copy = SongLibraryStore.CopyImports;
            var found = new List<LibrarySong>();
            foreach (var f in dlg.FileNames)
            {
                var entry = await Task.Run(() =>
                {
                    string finalPath = copy ? SongLibraryStore.CopyIntoLibrary(f) : f;
                    return SongLibraryStore.ScanFile(finalPath);
                });
                if (entry != null) found.Add(entry);
            }
            _busy = false;
            AddEntries(found, "the picked files");
        }

        // ---------- analyze ----------

        private System.Threading.CancellationTokenSource? _scanCts;

        private async void Analyze_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            _busy = true;
            _scanCts = new System.Threading.CancellationTokenSource();
            ScanProgress.Visibility = Visibility.Visible;
            ScanProgress.Value = 0;
            ScanCancelButton.Visibility = Visibility.Visible;
            var progress = new Progress<(int Done, int Total, string Title)>(p =>
            {
                ScanProgress.Value = p.Total > 0 ? p.Done * 100.0 / p.Total : 0;
                StatusText.Text = $"Deep-scanning {p.Done} of {p.Total}: {p.Title}…";
            });
            try
            {
                var r = await LibraryDeepScan.ScanAsync(progress, _scanCts.Token);
                SongGrid.Items.Refresh();
                StatusText.Text = r.Cancelled
                    ? $"Deep-scan stopped — progress kept. {r.Completed} song(s) completed before stopping."
                    : $"Deep-scan done — {r.Completed} song(s) analyzed (BPM {r.BpmFilled} new, key {r.KeyFilled} new, energy ready on {r.EnergyFilled}), " +
                      $"{r.AlreadyDone} already complete, {r.Skipped} skipped (file missing or unreadable).";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Deep-scan hit a problem: " + ex.Message;
            }
            finally
            {
                _busy = false;
                _scanCts?.Dispose();
                _scanCts = null;
                ScanProgress.Visibility = Visibility.Collapsed;
                ScanCancelButton.Visibility = Visibility.Collapsed;
            }
        }

        private void ScanCancel_Click(object sender, RoutedEventArgs e) => _scanCts?.Cancel();

        // ---------- decks ----------

        private List<LibrarySong> SelectedSongs() =>
            SongGrid.SelectedItems.Cast<LibrarySong>().ToList();

        private void SendToDeck(bool deckA, bool queueOnly)
        {
            var sel = SelectedSongs();
            if (sel.Count == 0) { StatusText.Text = "Pick a song first."; return; }
            if (queueOnly)
            {
                int ok = 0;
                foreach (var s in sel) if (_owner.SendSongToDeck(deckA, s.Path, true)) ok++;
                StatusText.Text = ok > 0
                    ? $"Added {ok} song(s) to Deck {(deckA ? "A" : "B")}'s track list — they're queued on the DJ board."
                    : "Couldn't add those to the deck's track list.";
            }
            else
            {
                var s = sel[0];
                StatusText.Text = _owner.SendSongToDeck(deckA, s.Path, false)
                    ? $"Loaded '{s.DisplayTitle}' onto Deck {(deckA ? "A" : "B")} — the DJ board has it."
                    : $"Couldn't load '{s.DisplayTitle}' — the file may have moved.";
            }
        }

        private void LoadDeckA_Click(object sender, RoutedEventArgs e) => SendToDeck(true, false);
        private void LoadDeckB_Click(object sender, RoutedEventArgs e) => SendToDeck(false, false);
        private void QueueDeckA_Click(object sender, RoutedEventArgs e) => SendToDeck(true, true);
        private void QueueDeckB_Click(object sender, RoutedEventArgs e) => SendToDeck(false, true);

        private void SongGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => SendToDeck(true, false);

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            var sel = SelectedSongs();
            if (sel.Count == 0) { StatusText.Text = "Pick the song(s) to remove first."; return; }
            foreach (var s in sel) _songs.Remove(s);
            SongLibraryStore.Save(_songs.ToList());
            UpdateCount();
            StatusText.Text = $"Removed {sel.Count} song(s) from the library list. The files themselves were not deleted.";
        }

        // ---------- the library folder (v3.82) ----------

        private void ChangeDir_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog { Title = "Pick the folder BeatSync keeps its music library in — new songs land here" };
            if (dlg.ShowDialog() != true) return;
            SongLibraryStore.LibraryDir = dlg.FolderName;
            SongLibraryStore.EnsureLibraryDir();
            LibDirText.Text = SongLibraryStore.LibraryDir;
            StatusText.Text = $"Library folder set to {SongLibraryStore.LibraryDir}. Songs already in the library stay where they are; new adds land in the new folder.";
        }

        private void OpenDir_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string dir = SongLibraryStore.EnsureLibraryDir();
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            }
            catch (Exception ex) { StatusText.Text = "Couldn't open the folder: " + ex.Message; }
        }

        private void CopyImports_Changed(object sender, RoutedEventArgs e)
        {
            if (CopyImportsBox.IsChecked.HasValue)
                SongLibraryStore.CopyImports = CopyImportsBox.IsChecked.Value;
        }

        /// <summary>Delete selected songs for real — Michael's rule:
        /// the app ALWAYS asks first, and only files inside the
        /// library folder get deleted; outside files are only
        /// removed from the list.</summary>
        private void DeleteFile_Click(object sender, RoutedEventArgs e)
        {
            var sel = SelectedSongs();
            if (sel.Count == 0) { StatusText.Text = "Pick the song(s) to delete first."; return; }
            int inDir = sel.Count(s => SongLibraryStore.IsInLibraryDir(s.Path));
            int outside = sel.Count - inDir;
            string msg = "";
            if (inDir > 0)
                msg = $"Delete {inDir} song(s) from your library AND delete their files from the library folder?\n\nThis cannot be undone.";
            if (outside > 0)
                msg += (msg.Length > 0 ? "\n\n" : "") + $"{outside} selected song(s) live outside the library folder — those will only be removed from the list; their files stay where they are.";
            if (msg.Length == 0)
                msg = $"Remove {sel.Count} song(s) from the library list? Their files stay where they are.";
            if (MessageBox.Show(this, msg, "Delete from library", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            {
                StatusText.Text = "Delete cancelled — nothing was touched.";
                return;
            }
            int files = 0;
            foreach (var s in sel)
            {
                _songs.Remove(s);
                if (SongLibraryStore.IsInLibraryDir(s.Path))
                {
                    try { File.Delete(s.Path); files++; } catch { }
                }
            }
            SongLibraryStore.Save(_songs.ToList());
            UpdateCount();
            StatusText.Text = files > 0
                ? $"Deleted {sel.Count} song(s) from the library — {files} file(s) deleted from the library folder."
                : $"Removed {sel.Count} song(s) from the library list. No files were deleted (they live outside the library folder).";
        }

        // ---------- details pane ----------

        private void SongGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var s = SongGrid.SelectedItem as LibrarySong;
            if (s == null)
            {
                DTitle.Text = "Pick a song";
                DArtist.Text = DAlbum.Text = DLine.Text = DMix.Text = DPath.Text = "";
                CoverImage.Visibility = Visibility.Collapsed;
                NoCoverText.Visibility = Visibility.Visible;
                return;
            }
            DTitle.Text = s.DisplayTitle;
            DArtist.Text = s.Artist;
            DAlbum.Text = s.Album;
            var line = new List<string>();
            if (s.Genre.Length > 0) line.Add(s.Genre);
            if (s.Year.Length > 0) line.Add(s.Year);
            if (s.Format.Length > 0) line.Add(s.Format);
            DLine.Text = string.Join(" · ", line);
            var mix = new List<string>();
            if (s.Bpm > 0) mix.Add($"{s.Bpm:0} BPM");
            if (s.Key.Length > 0) mix.Add($"Key {s.Key}");
            if (s.Seconds > 0) mix.Add(s.LengthText);
            DMix.Text = string.Join(" · ", mix);
            DPath.Text = s.Path;

            byte[]? pic = null;
            try { pic = TagReader.Parse(File.ReadAllBytes(s.Path), System.IO.Path.GetFileName(s.Path))?.Picture; }
            catch { pic = null; }
            if (pic != null && pic.Length > 0)
            {
                try
                {
                    var bmp = new BitmapImage();
                    using (var ms = new MemoryStream(pic))
                    {
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.StreamSource = ms;
                        bmp.EndInit();
                    }
                    CoverImage.Source = bmp;
                    CoverImage.Visibility = Visibility.Visible;
                    NoCoverText.Visibility = Visibility.Collapsed;
                    return;
                }
                catch { /* fall through to the placeholder */ }
            }
            CoverImage.Source = null;
            CoverImage.Visibility = Visibility.Collapsed;
            NoCoverText.Visibility = Visibility.Visible;
        }

        // ---------- Suno tab (v3.73) ----------

        private void UpdateSunoCount() => SunoCountText.Text = _sunoSongs.Count > 0 ? $"{_sunoSongs.Count} song(s)" : "";

        private void LibTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.Source != LibTabs) return;   // inner grids bubble their own SelectionChanged
            if (LibTabs.SelectedItem == SunoTab) _ = InitSunoBrowserAsync();
        }

        private async Task InitSunoBrowserAsync()
        {
            if (_sunoInitStarted) return;
            _sunoInitStarted = true;
            try
            {
                string userData = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BeatSyncStudioPro", "WebView2");
                Directory.CreateDirectory(userData);
                var env = await CoreWebView2Environment.CreateAsync(null, userData);
                await SunoBrowser.EnsureCoreWebView2Async(env);
                SunoBrowser.CoreWebView2.NavigationCompleted += SunoNavCompleted;
                _sunoBrowserReady = true;
                SunoBrowser.CoreWebView2.Navigate("https://suno.com/me");
                if (_pendingSync)
                {
                    _pendingSync = false;
                    _syncStep = 1;
                    StatusText.Text = "Syncing — reading your Suno library page…";
                }
            }
            catch
            {
                StatusText.Text = "The built-in browser couldn't start, so the Suno tab can't sync. The My Songs window uses the same browser — opening it once usually sorts this out.";
            }
        }

        private void OpenSuno_Click(object sender, RoutedEventArgs e)
        {
            _ = InitSunoBrowserAsync();
            if (_sunoBrowserReady && SunoBrowser.CoreWebView2 != null)
                SunoBrowser.CoreWebView2.Navigate("https://suno.com");
        }

        private void SyncSuno_Click(object sender, RoutedEventArgs e)
        {
            if (_syncStep != 0) { StatusText.Text = "A sync is already running…"; return; }
            _ = InitSunoBrowserAsync();
            if (_sunoBrowserReady && SunoBrowser.CoreWebView2 != null)
            {
                _syncStep = 1;
                StatusText.Text = "Syncing — reading your Suno library page…";
                SunoBrowser.CoreWebView2.Navigate("https://suno.com/me");
            }
            else
            {
                _pendingSync = true;
                StatusText.Text = "Starting the Suno browser — sync will begin when it's ready…";
            }
        }

        private async void SunoNavCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!e.IsSuccess || _syncStep == 0) return;
            try
            {
                if (_syncStep == 1)
                {
                    await Task.Delay(1800);   // let the page's own scripts render the song list
                    var (songs, profile) = await ScrapeSunoPageAsync();
                    _syncFound = songs;
                    _profileHref = profile;
                    if (_profileHref.Length > 1 && SunoBrowser.CoreWebView2 != null)
                    {
                        _syncStep = 2;
                        StatusText.Text = $"Found {songs.Count} song(s) in your library — now checking which ones are published…";
                        SunoBrowser.CoreWebView2.Navigate("https://suno.com" + _profileHref);
                    }
                    else FinishSync(null);
                }
                else if (_syncStep == 2)
                {
                    await Task.Delay(1800);
                    var (pubSongs, _) = await ScrapeSunoPageAsync();
                    FinishSync(new HashSet<string>(pubSongs.Select(s => s.Id), StringComparer.OrdinalIgnoreCase));
                }
            }
            catch { FinishSync(null); }
        }

        private async Task<(List<SunoSong> Songs, string Profile)> ScrapeSunoPageAsync()
        {
            var songs = new List<SunoSong>();
            string profile = "";
            if (SunoBrowser.CoreWebView2 == null) return (songs, profile);
            const string script = @"(() => {
  const out = []; const seen = new Set();
  document.querySelectorAll('a[href*=""/song/""]').forEach(a => {
    const m = a.href.match(/\/song\/([0-9a-f-]{36})/i);
    if (!m || seen.has(m[1])) return;
    seen.add(m[1]);
    let title = '';
    const lines = (a.innerText || '').split('\n').map(s => s.trim()).filter(Boolean);
    if (lines.length) title = lines[0].slice(0, 90);
    let cover = '';
    let img = a.querySelector('img');
    if (!img) { let p = a; for (let i = 0; i < 4 && p; i++) { p = p.parentElement; if (p) { img = p.querySelector('img'); if (img) break; } } }
    if (img) cover = img.src || '';
    if (!title && img && img.alt) title = img.alt.slice(0, 90);
    out.push({ id: m[1], title: title, cover: cover });
  });
  let profile = '';
  const pa = document.querySelector('a[href^=""/@""]');
  if (pa) profile = pa.getAttribute('href') || '';
  return JSON.stringify({ profile: profile, songs: out.slice(0, 120) });
})()";
            try
            {
                string raw = await SunoBrowser.CoreWebView2.ExecuteScriptAsync(script);
                string json = JsonSerializer.Deserialize<string>(raw) ?? "";
                if (json.Length == 0) return (songs, profile);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("profile", out var pf)) profile = pf.GetString() ?? "";
                if (root.TryGetProperty("songs", out var arr))
                {
                    foreach (var item in arr.EnumerateArray())
                    {
                        string id = item.TryGetProperty("id", out var i1) ? i1.GetString() ?? "" : "";
                        if (id.Length == 0) continue;
                        songs.Add(new SunoSong
                        {
                            Id = id,
                            Title = item.TryGetProperty("title", out var t1) ? t1.GetString() ?? "" : "",
                            CoverUrl = item.TryGetProperty("cover", out var c1) ? c1.GetString() ?? "" : ""
                        });
                    }
                }
            }
            catch { /* a page we can't read just yields nothing */ }
            return (songs, profile);
        }

        private void FinishSync(HashSet<string>? publishedIds)
        {
            _syncStep = 0;
            if (_syncFound.Count == 0)
            {
                StatusText.Text = "Sync found no songs. If the browser below is showing a Suno sign-in page, sign in there first, then press Sync again.";
                return;
            }
            var byId = _sunoSongs.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
            int added = 0;
            foreach (var f in _syncFound)
            {
                if (byId.TryGetValue(f.Id, out var existing))
                {
                    if (f.Title.Length > 0) existing.Title = f.Title;
                    if (f.CoverUrl.Length > 0) existing.CoverUrl = f.CoverUrl;
                    if (publishedIds != null)
                    {
                        existing.PublishKnown = true;
                        existing.Published = publishedIds.Contains(f.Id);
                    }
                }
                else
                {
                    if (publishedIds != null)
                    {
                        f.PublishKnown = true;
                        f.Published = publishedIds.Contains(f.Id);
                    }
                    _sunoSongs.Add(f);
                    added++;
                }
            }
            SunoLibraryStore.Save(_sunoSongs.ToList());
            SunoGrid.Items.Refresh();
            UpdateSunoCount();
            StatusText.Text = publishedIds != null
                ? $"Sync done — {_sunoSongs.Count} of your Suno songs are in the library ({_sunoSongs.Count(s => s.Published)} published, {_sunoSongs.Count(s => s.PublishKnown && !s.Published)} drafts{(added > 0 ? $", {added} new" : "")}). Streaming never touches your download quota."
                : $"Sync done — {_sunoSongs.Count} of your Suno songs are in the library{(added > 0 ? $" ({added} new)" : "")}. (Couldn't read your public profile, so published vs draft isn't marked this time.)";
        }

        // ---------- Suno streaming + decks ----------

        private static readonly HttpClient _http = new HttpClient();

        private async Task<byte[]?> FetchSunoBytesAsync(SunoSong song)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, song.StreamUrl);
                    req.Headers.TryAddWithoutValidation("User-Agent",
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
                    if (attempt == 1 && SunoBrowser.CoreWebView2 != null)
                    {
                        try
                        {
                            var cookies = await SunoBrowser.CoreWebView2.CookieManager.GetCookiesAsync("https://suno.com");
                            string header = string.Join("; ", cookies.Select(c => c.Name + "=" + c.Value));
                            if (header.Length > 0) req.Headers.TryAddWithoutValidation("Cookie", header);
                        }
                        catch { /* best effort */ }
                    }
                    using var resp = await _http.SendAsync(req);
                    if (resp.IsSuccessStatusCode) return await resp.Content.ReadAsByteArrayAsync();
                }
                catch { /* try the cookie attempt, then give up */ }
            }
            return null;
        }

        private async void StreamSunoToDeck(bool deckA)
        {
            if (SunoGrid.SelectedItem is not SunoSong song) { StatusText.Text = "Pick a Suno song first."; return; }
            if (_busy) return;
            _busy = true;
            StatusText.Text = $"Streaming '{song.DisplayTitle}' from Suno…";
            byte[]? bytes = await FetchSunoBytesAsync(song);
            _busy = false;
            if (bytes == null)
            {
                StatusText.Text = $"Couldn't stream '{song.DisplayTitle}'. Published songs stream freely; if this is a draft, Suno may only let it play inside Suno itself — the 🎵 My Songs window can play it there.";
                return;
            }
            if (song.Seconds <= 0)
            {
                try
                {
                    using var reader = AudioReaderFactory.Open(bytes, "mp3");
                    song.Seconds = reader.TotalTime.TotalSeconds;
                    SunoLibraryStore.Save(_sunoSongs.ToList());
                    SunoGrid.Items.Refresh();
                }
                catch { }
            }
            StatusText.Text = _owner.SendBytesToDeck(deckA, bytes, song.DisplayTitle + ".mp3")
                ? $"'{song.DisplayTitle}' is on Deck {(deckA ? "A" : "B")} — streamed, not downloaded."
                : $"Couldn't put '{song.DisplayTitle}' on the deck.";
        }

        private void SunoDeckA_Click(object sender, RoutedEventArgs e) => StreamSunoToDeck(true);
        private void SunoDeckB_Click(object sender, RoutedEventArgs e) => StreamSunoToDeck(false);
        private void SunoGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => StreamSunoToDeck(true);

        private async void SunoAnalyze_Click(object sender, RoutedEventArgs e)
        {
            if (SunoGrid.SelectedItem is not SunoSong song) { StatusText.Text = "Pick a Suno song first."; return; }
            if (_busy) return;
            _busy = true;
            StatusText.Text = $"Streaming '{song.DisplayTitle}' once to analyze its BPM + key…";
            byte[]? bytes = await FetchSunoBytesAsync(song);
            if (bytes == null)
            {
                _busy = false;
                StatusText.Text = $"Couldn't stream '{song.DisplayTitle}' for analysis.";
                return;
            }
            if (song.Bpm <= 0)
            {
                var (bpm, _, _) = await Task.Run(() => BeatDetector.Detect(bytes, "mp3"));
                if (bpm >= 50 && bpm <= 200) song.Bpm = Math.Round(bpm * 2) / 2;
            }
            if (song.Key.Length == 0)
            {
                var mono = await Task.Run(() => SongLibraryStore.DecodeMono(bytes, "mp3"));
                if (mono != null)
                {
                    var kr = KeyDetector.Detect(mono);
                    if (kr != null) song.Key = kr.Camelot;
                }
            }
            if (song.Seconds <= 0)
            {
                try
                {
                    using var reader = AudioReaderFactory.Open(bytes, "mp3");
                    song.Seconds = reader.TotalTime.TotalSeconds;
                }
                catch { }
            }
            _busy = false;
            SunoLibraryStore.Save(_sunoSongs.ToList());
            SunoGrid.Items.Refresh();
            StatusText.Text = $"'{song.DisplayTitle}': {(song.Bpm > 0 ? song.Bpm.ToString("0") + " BPM" : "BPM ?")} · key {(song.Key.Length > 0 ? song.Key : "?")}.";
        }

        private void SunoOpenPage_Click(object sender, RoutedEventArgs e)
        {
            if (SunoGrid.SelectedItem is not SunoSong song) { StatusText.Text = "Pick a Suno song first."; return; }
            try { Process.Start(new ProcessStartInfo(song.PageUrl) { UseShellExecute = true }); }
            catch { }
        }

        private void SpotifyDev_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("https://developer.spotify.com/dashboard") { UseShellExecute = true }); }
            catch { }
        }
    }
}
