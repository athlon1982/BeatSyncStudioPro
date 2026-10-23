using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace BeatSyncStudioPro
{
    /// <summary>One song inside a crate (a snapshot of its library data,
    /// so the crate stays right even if the library is rescanned).</summary>
    public sealed class CrateItem
    {
        public string Path { get; set; } = "";
        public string Title { get; set; } = "";
        public double Bpm { get; set; }
        public string Key { get; set; } = "";
        public double Seconds { get; set; }

        [JsonIgnore] public string BpmText => Bpm > 0 ? $"{Bpm:0.#}" : "?";
        [JsonIgnore] public string LengthText => Seconds > 0 ? $"{(int)Seconds / 60}:{(int)Seconds % 60:D2}" : "-";
    }

    public sealed class SetCrate
    {
        public string Name { get; set; } = "";
        public DateTime CreatedUtc { get; set; }
        public List<CrateItem> Items { get; set; } = new List<CrateItem>();
    }

    public static class SetCrateStore
    {
        private static string FilePath => System.IO.Path.Combine(SongLibraryStore.AppDir, "set-crates.json");

        public static List<SetCrate> Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<List<SetCrate>>(File.ReadAllText(FilePath)) ?? new List<SetCrate>();
            }
            catch { }
            return new List<SetCrate>();
        }

        public static void Save(List<SetCrate> crates)
        {
            try
            {
                Directory.CreateDirectory(SongLibraryStore.AppDir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(crates, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }

    /// <summary>
    /// 🧺 Set Builder (v3.78): plan a DJ set as a "crate" — an ordered
    /// list of songs from the Song Library — then queue the whole crate
    /// onto a deck in one click and let Auto-play / Cruise perform it.
    /// Crates persist in set-crates.json.
    /// </summary>
    public partial class SetBuilderWindow : Window
    {
        private readonly MainWindow _owner;
        private readonly List<SetCrate> _crates;
        private readonly ObservableCollection<CrateItem> _items = new ObservableCollection<CrateItem>();
        private SetCrate? _current;
        private List<LibrarySong> _library = new List<LibrarySong>();

        public SetBuilderWindow(MainWindow owner)
        {
            InitializeComponent();
            _owner = owner;
            _crates = SetCrateStore.Load();
            ItemGrid.ItemsSource = _items;
            RefreshCrates(selectFirst: true);
            RefreshLibrary();
            UpdateSummary();
        }

        // ---------- crates ----------

        private void RefreshCrates(bool selectFirst = false)
        {
            CrateList.ItemsSource = null;
            CrateList.ItemsSource = _crates.Select(c => c.Name).ToList();
            if (selectFirst && _crates.Count > 0) CrateList.SelectedIndex = 0;
        }

        private void CrateList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int i = CrateList.SelectedIndex;
            _current = i >= 0 && i < _crates.Count ? _crates[i] : null;
            ShowCurrent();
        }

        private void ShowCurrent()
        {
            _items.Clear();
            if (_current != null)
                foreach (var it in _current.Items) _items.Add(it);
            CrateNameText.Text = _current == null ? "Pick or create a crate" : $"🧺 {_current.Name}";
            UpdateSummary();
        }

        private void SaveAndSync()
        {
            if (_current != null)
            {
                _current.Items = _items.ToList();
                SetCrateStore.Save(_crates);
            }
            UpdateSummary();
        }

        private void UpdateSummary()
        {
            if (_current == null) { SummaryText.Text = $"{_crates.Count} crate(s)"; return; }
            double total = _items.Sum(i => i.Seconds);
            var bpms = _items.Where(i => i.Bpm > 0).Select(i => i.Bpm).ToList();
            string range = bpms.Count > 0 ? $" · {bpms.Min():0}–{bpms.Max():0} BPM" : "";
            SummaryText.Text = $"{_items.Count} song(s) · {(int)total / 60}:{(int)total % 60:D2} total{range}";
        }

        private void NewCrate_Click(object sender, RoutedEventArgs e)
        {
            string? name = Prompt("New crate", "Name this crate (e.g. Friday Warm-Up):", "Set " + DateTime.Now.ToString("MMM d"));
            if (name == null) return;
            var crate = new SetCrate { Name = name, CreatedUtc = DateTime.UtcNow };
            _crates.Add(crate);
            SetCrateStore.Save(_crates);
            RefreshCrates();
            CrateList.SelectedIndex = _crates.Count - 1;
            StatusText.Text = $"Created '{name}' — add songs from the library on the right.";
        }

        private void DeleteCrate_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            if (MessageBox.Show($"Delete the crate '{_current.Name}'? (The songs themselves are untouched.)", "Delete crate",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            string name = _current.Name;
            _crates.Remove(_current);
            _current = null;
            SetCrateStore.Save(_crates);
            RefreshCrates(selectFirst: true);
            if (_crates.Count == 0) ShowCurrent();
            StatusText.Text = $"Deleted '{name}'.";
        }

        // ---------- library ----------

        private void RefreshLibrary()
        {
            _library = SongLibraryStore.Load();
            ApplyLibFilter();
        }

        private void ApplyLibFilter()
        {
            string q = LibFilter.Text.Trim();
            var view = _library.Where(s => q.Length == 0
                || s.DisplayTitle.Contains(q, StringComparison.OrdinalIgnoreCase)
                || s.Artist.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            LibList.ItemsSource = view;
        }

        private void LibFilter_TextChanged(object sender, TextChangedEventArgs e) => ApplyLibFilter();

        private void AddSelected_Click(object sender, RoutedEventArgs e) => AddSongs(LibList.SelectedItems.Cast<LibrarySong>().ToList());

        private void LibList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (LibList.SelectedItem is LibrarySong s) AddSongs(new List<LibrarySong> { s });
        }

        private void AddSongs(List<LibrarySong> songs)
        {
            if (_current == null) { StatusText.Text = "Create or pick a crate first (left side)."; return; }
            int added = 0, dupes = 0;
            foreach (var s in songs)
            {
                if (_items.Any(i => string.Equals(i.Path, s.Path, StringComparison.OrdinalIgnoreCase))) { dupes++; continue; }
                _items.Add(new CrateItem { Path = s.Path, Title = s.DisplayTitle, Bpm = s.Bpm, Key = s.Key, Seconds = s.Seconds });
                added++;
            }
            SaveAndSync();
            StatusText.Text = $"Added {added} song(s)" + (dupes > 0 ? $", skipped {dupes} already in the crate." : ".");
        }

        // ---------- ordering ----------

        private void MoveSelected(int delta)
        {
            int i = ItemGrid.SelectedIndex;
            if (i < 0) return;
            int j = i + delta;
            if (j < 0 || j >= _items.Count) return;
            _items.Move(i, j);
            ItemGrid.SelectedIndex = j;
            SaveAndSync();
        }

        private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
        private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

        private void RemoveItem_Click(object sender, RoutedEventArgs e)
        {
            int i = ItemGrid.SelectedIndex;
            if (i < 0) return;
            _items.RemoveAt(i);
            SaveAndSync();
            StatusText.Text = "Removed from the crate (the song stays in your library).";
        }

        private void SortBpm_Click(object sender, RoutedEventArgs e)
        {
            var sorted = _items.OrderBy(i => i.Bpm > 0 ? i.Bpm : double.MaxValue).ToList();
            _items.Clear();
            foreach (var it in sorted) _items.Add(it);
            SaveAndSync();
            StatusText.Text = "Sorted slowest → fastest. Songs without an analyzed BPM went last — analyze them in the Song Library to place them properly.";
        }

        // ---------- send to decks ----------

        private void SendToDeck(bool deckA)
        {
            if (_current == null || _items.Count == 0) { StatusText.Text = "The crate is empty — add songs first."; return; }
            int sent = 0, missing = 0;
            foreach (var item in _items)
            {
                if (!File.Exists(item.Path)) { missing++; continue; }
                _owner.SendSongToDeck(deckA, item.Path, queueOnly: true);
                sent++;
            }
            StatusText.Text = $"Queued {sent} song(s) on Deck {(deckA ? "A" : "B")}'s track list, in crate order"
                + (missing > 0 ? $" — {missing} file(s) not found on disk anymore." : ".")
                + " Press play / Auto-play / Cruise over there to run the set.";
        }

        private void SendA_Click(object sender, RoutedEventArgs e) => SendToDeck(true);
        private void SendB_Click(object sender, RoutedEventArgs e) => SendToDeck(false);

        // ---------- tiny prompt dialog ----------

        private string? Prompt(string title, string label, string initial)
        {
            var win = new Window
            {
                Title = title, Width = 360, Height = 168, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x2B)),
                Foreground = System.Windows.Media.Brushes.White
            };
            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
            var box = new TextBox { Text = initial, Margin = new Thickness(0, 8, 0, 10), Padding = new Thickness(6, 4, 6, 4) };
            panel.Children.Add(box);
            var ok = new Button { Content = "Create", Padding = new Thickness(14, 5, 14, 5), HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true };
            ok.Click += (s, e) => { win.DialogResult = true; };
            panel.Children.Add(ok);
            win.Content = panel;
            box.Loaded += (s, e) => { box.Focus(); box.SelectAll(); };
            return win.ShowDialog() == true && box.Text.Trim().Length > 0 ? box.Text.Trim() : null;
        }
    }
}
