using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Imaging;
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
            var found = await Task.Run(() =>
            {
                var list = new List<LibrarySong>();
                try
                {
                    foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    {
                        if (!SongLibraryStore.IsAudioPath(f)) continue;
                        var entry = SongLibraryStore.ScanFile(f);
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
            var found = new List<LibrarySong>();
            foreach (var f in dlg.FileNames)
            {
                var entry = await Task.Run(() => SongLibraryStore.ScanFile(f));
                if (entry != null) found.Add(entry);
            }
            _busy = false;
            AddEntries(found, "the picked files");
        }

        // ---------- analyze ----------

        private async void Analyze_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            var targets = SelectedSongs().Where(s => s.Bpm <= 0 || s.Key.Length == 0).ToList();
            if (targets.Count == 0)
                targets = _songs.Where(s => s.Bpm <= 0 || s.Key.Length == 0).ToList();
            if (targets.Count == 0)
            {
                StatusText.Text = "Every song already has a BPM and a key — nothing to analyze.";
                return;
            }
            _busy = true;
            int done = 0, filled = 0;
            foreach (var t in targets)
            {
                StatusText.Text = $"Analyzing {++done} of {targets.Count}: {t.DisplayTitle}…";
                if (await SongLibraryStore.AnalyzeAsync(t)) filled++;
            }
            _busy = false;
            SongLibraryStore.Save(_songs.ToList());
            SongGrid.Items.Refresh();
            StatusText.Text = $"Analysis done — filled BPM / key on {filled} of {targets.Count} song(s).";
        }

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
    }
}
