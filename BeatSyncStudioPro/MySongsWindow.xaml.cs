using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace BeatSyncStudioPro
{
    public class SongEntry
    {
        public string Title { get; set; } = "";
        public string Url { get; set; } = "";
        public string Source { get; set; } = "Suno";
    }

    /// <summary>
    /// My Songs: Michael's published songs (Suno / SoundCloud links) in one
    /// panel, played through an embedded browser for listening and reference.
    /// Streaming pages are never pulled into the timeline — Suno has no API
    /// and downloads are Michael's call, never the app's.
    /// </summary>
    public partial class MySongsWindow : Window
    {
        private readonly List<SongEntry> _songs = new List<SongEntry>();
        private bool _playerReady;

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BeatSyncStudioPro", "my-songs.json");

        private static readonly (string Title, string Url)[] Defaults =
        {
            ("14 Years", "https://suno.com/song/73d174f4-bebc-4f1d-b19c-6b9472c728d6"),
            ("Muddy Roads & Maple Gold", "https://suno.com/song/e08e0c28-7fb5-452b-b311-809e779fa7ed"),
            ("Boss T", "https://suno.com/song/13a0894d-0899-4122-aa6f-35995d1dc814"),
            ("Blacked", "https://suno.com/song/6887c53f-42bf-4c0b-a78b-0bf4188b435c"),
            ("Blacked (Version 2)", "https://suno.com/song/8db98159-3c55-49c4-9fb1-65e068eaffab"),
            ("Haunted Hollow", "https://suno.com/song/99b6771f-05fb-4012-9e0d-f91cbf191612"),
            ("No Sleep Till Feeding", "https://suno.com/song/d962cde5-0839-44a8-8036-13c0596b7478"),
            ("Green Mountain Winter", "https://suno.com/song/50b68f04-c409-449a-8eea-79119081e03d"),
            ("Raised by the Pines", "https://suno.com/song/d19a4bfe-a9b0-4540-bc0f-fb90127caa53"),
            ("Dirt Road Royalty", "https://suno.com/song/1a9180d1-9b5b-4bde-b067-06be67e0bee0"),
            ("We Together", "https://suno.com/song/6ff04104-83a9-4834-8c04-0b57be507825"),
            ("Guardrail Blues", "https://suno.com/song/08933eb3-7d64-40ad-bc0b-da2fc0939800"),
            ("Unlimited Flow", "https://suno.com/song/961e3cbc-07aa-4c2d-9389-ab1b5484c5f3"),
            ("Gridlock in the Green Mountains", "https://suno.com/song/393d36a7-2f9e-4125-a62d-1318f600a0f9"),
            ("Green Mountain State", "https://suno.com/song/2a5e51e1-4780-453b-88bc-3bd174baa7ed"),
            ("Pittsford Haunted House", "https://suno.com/song/8e0b2f49-3479-43e7-af91-7df1cbd6a8b0"),
            ("Six-Seven", "https://suno.com/song/3ab57c6c-79a0-4f35-acf9-3227fde1e8fc")
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
                    await Player.EnsureCoreWebView2Async();
                    _playerReady = true;
                    if (SongsList.SelectedItem is SongEntry entry) Play(entry);
                }
                catch
                {
                    Player.Visibility = Visibility.Collapsed;
                    FallbackText.Visibility = Visibility.Visible;
                }
            };
        }

        private void LoadSongs()
        {
            _songs.Clear();
            try
            {
                if (File.Exists(FilePath))
                {
                    var loaded = JsonSerializer.Deserialize<List<SongEntry>>(File.ReadAllText(FilePath));
                    if (loaded != null) { _songs.AddRange(loaded); return; }
                }
            }
            catch { }
            foreach (var (title, url) in Defaults)
                _songs.Add(new SongEntry { Title = title, Url = url, Source = "Suno" });
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

        private static string EmbedUrl(SongEntry entry)
        {
            if (entry.Source == "SoundCloud" || entry.Url.Contains("soundcloud.com"))
                return "https://w.soundcloud.com/player/?url=" + Uri.EscapeDataString(entry.Url) + "&auto_play=false";
            return entry.Url;
        }

        private void Play(SongEntry entry)
        {
            NowText.Text = $"♪ {entry.Title} ({entry.Source})";
            if (_playerReady && Player.CoreWebView2 != null)
                Player.CoreWebView2.Navigate(EmbedUrl(entry));
        }

        private void SongsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SongsList.SelectedItem is SongEntry entry) Play(entry);
        }

        private void OpenBrowser_Click(object sender, RoutedEventArgs e)
        {
            if (SongsList.SelectedItem is SongEntry entry)
            {
                try { Process.Start(new ProcessStartInfo(entry.Url) { UseShellExecute = true }); }
                catch (Exception ex) { MessageBox.Show("Could not open the browser: " + ex.Message); }
            }
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
                NowText.Text = "Pick a song on the left";
            }
        }
    }
}
