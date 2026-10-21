using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Windows;
using NAudio.Wave;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// The built-in Sound Pack browser. The pack is a single compressed
    /// file (Assets/soundpack.zip) sitting next to the app — entries are
    /// read straight out of the zip for preview, and only copied into the
    /// user's library (and from there into projects) when asked.
    /// </summary>
    public partial class SoundPackWindow : Window
    {
        private readonly MainWindow _owner;
        private readonly List<(string Category, string Name, string Entry)> _items = new List<(string, string, string)>();
        private WaveOutEvent? _previewOut;
        private WaveFileReader? _previewReader;
        private MemoryStream? _previewStream;
        private int _previewIndex = -1;

        private static string PackPath => Path.Combine(AppContext.BaseDirectory, "Assets", "soundpack.zip");

        public SoundPackWindow(MainWindow owner)
        {
            InitializeComponent();
            _owner = owner;
            Closed += (s, e) => StopPreview();
            LoadPack();
        }

        private void LoadPack()
        {
            PackList.Items.Clear();
            _items.Clear();
            if (!File.Exists(PackPath))
            {
                StatusText.Text = "The sound pack file wasn't found next to the app (Assets\\soundpack.zip).";
                return;
            }
            using var zip = ZipFile.OpenRead(PackPath);
            foreach (var entry in zip.Entries.Where(en => en.Name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                                            .OrderBy(en => en.FullName))
            {
                string category = entry.FullName.Contains('/') ? entry.FullName.Substring(0, entry.FullName.IndexOf('/')) : "Sounds";
                _items.Add((category, Path.GetFileNameWithoutExtension(entry.Name), entry.FullName));
            }
            string lastCat = "";
            foreach (var it in _items)
            {
                if (it.Category != lastCat) { PackList.Items.Add($"—— {it.Category} ——"); lastCat = it.Category; }
                PackList.Items.Add("🎵 " + it.Name);
            }
            StatusText.Text = $"{_items.Count} sounds in the pack ({new FileInfo(PackPath).Length / 1048576.0:0.0} MB, compressed). Select one and Preview or Add it.";
        }

        /// <summary>Maps a ListBox row (which includes category header rows) back to an item index, or -1.</summary>
        private int SelectedItemIndex()
        {
            int sel = PackList.SelectedIndex;
            if (sel < 0) return -1;
            int itemIdx = -1, row = -1;
            string lastCat = "";
            foreach (var it in _items)
            {
                if (it.Category != lastCat) { row++; lastCat = it.Category; }
                row++; itemIdx++;
                if (row == sel) return itemIdx;
            }
            return -1;
        }

        private byte[] ReadEntry(string entryName)
        {
            using var zip = ZipFile.OpenRead(PackPath);
            var entry = zip.GetEntry(entryName) ?? throw new InvalidOperationException("Pack entry missing: " + entryName);
            using var ms = new MemoryStream();
            using var es = entry.Open();
            es.CopyTo(ms);
            return ms.ToArray();
        }

        private void StopPreview()
        {
            try { _previewOut?.Stop(); _previewOut?.Dispose(); _previewReader?.Dispose(); _previewStream?.Dispose(); } catch { }
            _previewOut = null; _previewReader = null; _previewStream = null; _previewIndex = -1;
        }

        private void Preview_Click(object sender, RoutedEventArgs e)
        {
            int idx = SelectedItemIndex();
            if (idx < 0) { StatusText.Text = "Select a sound first (the —— rows are just category headers)."; return; }
            if (_previewIndex == idx && _previewOut != null) { StopPreview(); StatusText.Text = "Preview stopped."; return; }
            StopPreview();
            try
            {
                _previewStream = new MemoryStream(ReadEntry(_items[idx].Entry));
                _previewReader = new WaveFileReader(_previewStream);
                _previewOut = new WaveOutEvent();
                _previewOut.Init(_previewReader);
                _previewOut.Play();
                _previewIndex = idx;
                StatusText.Text = $"▶ {_items[idx].Category} / {_items[idx].Name}";
            }
            catch (Exception ex) { StatusText.Text = "Preview failed: " + ex.Message; }
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            int idx = SelectedItemIndex();
            if (idx < 0) { StatusText.Text = "Select a sound first."; return; }
            try
            {
                string stored = _owner.AddToLibraryOnly(ReadEntry(_items[idx].Entry), _items[idx].Name + ".wav");
                StatusText.Text = $"✅ '{stored}' is in your library — find it in the asset list, the pattern channel pickers, and your kits.";
            }
            catch (Exception ex) { StatusText.Text = "Add failed: " + ex.Message; }
        }

        private void AddCategory_Click(object sender, RoutedEventArgs e)
        {
            int idx = SelectedItemIndex();
            if (idx < 0) { StatusText.Text = "Select any sound in the category you want."; return; }
            string cat = _items[idx].Category;
            int added = 0;
            try
            {
                foreach (var it in _items.Where(i => i.Category == cat))
                {
                    _owner.AddToLibraryOnly(ReadEntry(it.Entry), it.Name + ".wav");
                    added++;
                }
                StatusText.Text = $"✅ Added all {added} {cat} sounds to your library.";
            }
            catch (Exception ex) { StatusText.Text = $"Added {added}, then hit a problem: " + ex.Message; }
        }
    }
}
