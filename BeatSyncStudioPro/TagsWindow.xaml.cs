using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// v3.53: shows everything TagReader found inside one audio file — the
    /// text tags plus the embedded cover art, big. Opened from a deck's
    /// Tags button.
    /// </summary>
    public partial class TagsWindow : Window
    {
        public TagsWindow(TagInfo tags, string fileLabel, double totalSeconds)
        {
            InitializeComponent();

            if (tags.Picture != null && tags.Picture.Length > 0)
            {
                try
                {
                    var bmp = new BitmapImage();
                    using (var ms = new MemoryStream(tags.Picture))
                    {
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.StreamSource = ms;
                        bmp.EndInit();
                    }
                    bmp.Freeze();
                    CoverImage.Source = bmp;
                    CoverImage.Visibility = Visibility.Visible;
                    NoCoverText.Visibility = Visibility.Collapsed;
                }
                catch { /* keep the "no cover art" placeholder */ }
            }

            AddRow("Title", tags.Title);
            AddRow("Artist", tags.Artist);
            AddRow("Album", tags.Album);
            AddRow("Year", tags.Year);
            AddRow("Genre", tags.Genre);
            AddRow("BPM (tag)", tags.Bpm);
            AddRow("Key (tag)", tags.KeyTag);
            AddRow("Format", tags.Format);
            if (totalSeconds > 0)
                AddRow("Length", $"{(int)(totalSeconds / 60)}:{(int)(totalSeconds % 60):00}");
            AddRow("File", fileLabel);
            if (RowsPanel.Children.Count == 0)
                AddRow("Tags", "Cover art only — no text tags in this file.");
        }

        private void AddRow(string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var row = new Grid { Margin = new Thickness(0, 0, 0, 7) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x93, 0xA6)),
                FontSize = 12.5
            });
            var val = new TextBlock
            {
                Text = value,
                Foreground = Brushes.White,
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(val, 1);
            row.Children.Add(val);
            RowsPanel.Children.Add(row);
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
