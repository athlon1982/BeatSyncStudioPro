using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// Encode window (v3.96): pick a format and encode the song loaded
    /// on one deck — FLAC / MP3 / WAV at the sample rate, bitrate and
    /// bit depth chosen here. The audio is the deck's played buffer, so
    /// for songs streamed from the Suno tab this never uses Suno's
    /// Download action or its quota.
    /// </summary>
    public sealed class EncodeWindow : Window
    {
        private readonly DjDeck _deck;
        private readonly AutoLevelInfo _autoLevel;
        private readonly ComboBox _formatBox = new ComboBox();
        private readonly ComboBox _bitrateBox = new ComboBox();
        private readonly ComboBox _rateBox = new ComboBox();
        private readonly ComboBox _depthBox = new ComboBox();
        private readonly CheckBox _autoLevelBox = new CheckBox();
        private readonly TextBlock _autoLevelInfo = new TextBlock();
        private readonly TextBox _folderBox = new TextBox();
        private readonly TextBlock _status = new TextBlock();
        private readonly Button _encodeBtn = new Button();
        private readonly Button _openBtn = new Button();
        private readonly StackPanel _bitrateRow = new StackPanel();
        private readonly StackPanel _depthRow = new StackPanel();
        private string _lastOutput = "";
        private CancellationTokenSource? _cts;

        public EncodeWindow(DjDeck deck, string deckName, AutoLevelInfo autoLevel)
        {
            _deck = deck;
            _autoLevel = autoLevel;
            Title = "💾 Encode — " + deckName;
            Width = 470;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0E, 0x14));
            Foreground = Brushes.White;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var root = new StackPanel { Margin = new Thickness(16) };
            root.Children.Add(new TextBlock
            {
                Text = "🎵 " + (string.IsNullOrWhiteSpace(deck.Title) ? "(nothing loaded)" : deck.Title),
                FontSize = 15, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            root.Children.Add(new TextBlock
            {
                Text = "Encodes the song as it plays in BeatSync. For a song streamed from the Suno tab, " +
                       "the audio comes from the played stream — Suno's Download button (and your download quota) is never touched.",
                Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA5, 0xB5)),
                FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12)
            });

            _formatBox.ItemsSource = new[] { "FLAC — lossless", "MP3 — small file", "WAV — uncompressed" };
            _formatBox.SelectedIndex = 0;
            root.Children.Add(Row("Format", _formatBox));

            _bitrateBox.ItemsSource = new[] { "320 kbps", "256 kbps", "192 kbps", "128 kbps" };
            _bitrateBox.SelectedIndex = 0;
            _bitrateRow.Children.Add(new TextBlock { Text = "MP3 bitrate", Foreground = Muted, FontSize = 12, Width = 110, VerticalAlignment = VerticalAlignment.Center });
            _bitrateRow.Children.Add(_bitrateBox);
            _bitrateRow.Orientation = Orientation.Horizontal;
            _bitrateRow.Margin = new Thickness(0, 0, 0, 8);
            root.Children.Add(_bitrateRow);

            _rateBox.ItemsSource = new[] { "44,100 Hz (CD / distribution)", "48,000 Hz (video)" };
            _rateBox.SelectedIndex = 0;
            root.Children.Add(Row("Sample rate", _rateBox));

            _depthBox.ItemsSource = new[] { "16-bit", "24-bit" };
            _depthBox.SelectedIndex = 0;
            _depthRow.Children.Add(new TextBlock { Text = "Bit depth", Foreground = Muted, FontSize = 12, Width = 110, VerticalAlignment = VerticalAlignment.Center });
            _depthRow.Children.Add(_depthBox);
            _depthRow.Orientation = Orientation.Horizontal;
            _depthRow.Margin = new Thickness(0, 0, 0, 8);
            root.Children.Add(_depthRow);

            _formatBox.SelectionChanged += (s, e) => RefreshRows();
            RefreshRows();

            // Auto-Level (v3.118): bake the deck's measured trim into the
            // export. Defaults to the deck's auto-gain switch; the user
            // can override it per encode. The source file is never changed.
            var levelRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            _autoLevelBox.Content = "Apply Auto-Level";
            _autoLevelBox.Foreground = Brushes.White;
            _autoLevelBox.FontSize = 12;
            _autoLevelBox.VerticalAlignment = VerticalAlignment.Center;
            _autoLevelBox.IsChecked = _autoLevel.On;
            _autoLevelBox.ToolTip = "Level this export to -14 LUFS using the same measurement as the deck's auto-gain, so it plays at the same volume everywhere. The original file is never changed.";
            levelRow.Children.Add(_autoLevelBox);
            _autoLevelInfo.Foreground = Muted;
            _autoLevelInfo.FontSize = 12;
            _autoLevelInfo.VerticalAlignment = VerticalAlignment.Center;
            _autoLevelInfo.Margin = new Thickness(8, 0, 0, 0);
            _autoLevelInfo.Text = _autoLevel.Measured
                ? $"measured {_autoLevel.Lufs:0.0} LUFS, {_autoLevel.GainDb:+0.0;-0.0} dB"
                : "loudness not measured yet";
            levelRow.Children.Add(_autoLevelInfo);
            root.Children.Add(levelRow);

            var folderRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            folderRow.Children.Add(new TextBlock { Text = "Save to", Foreground = Muted, FontSize = 12, Width = 110, VerticalAlignment = VerticalAlignment.Center });
            _folderBox.Width = 230;
            _folderBox.Padding = new Thickness(6, 4, 6, 4);
            _folderBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "BeatSync Encodes");
            folderRow.Children.Add(_folderBox);
            var browse = new Button { Content = "Browse…", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(8, 0, 0, 0) };
            browse.Click += (s, e) =>
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Where should encodes be saved?" };
                if (dlg.ShowDialog(this) == true) _folderBox.Text = dlg.FolderName;
            };
            folderRow.Children.Add(browse);
            root.Children.Add(folderRow);

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
            _encodeBtn.Content = "💾 Encode";
            _encodeBtn.Padding = new Thickness(16, 8, 16, 8);
            _encodeBtn.Background = new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54));
            _encodeBtn.Foreground = Brushes.White;
            _encodeBtn.BorderThickness = new Thickness(0);
            _encodeBtn.FontWeight = FontWeights.Bold;
            _encodeBtn.Click += Encode_Click;
            btnRow.Children.Add(_encodeBtn);
            _openBtn.Content = "📂 Open Folder";
            _openBtn.Padding = new Thickness(12, 8, 12, 8);
            _openBtn.Margin = new Thickness(8, 0, 0, 0);
            _openBtn.IsEnabled = false;
            _openBtn.Click += (s, e) =>
            {
                try { System.Diagnostics.Process.Start("explorer.exe", "\"" + _folderBox.Text + "\""); } catch { }
            };
            btnRow.Children.Add(_openBtn);
            root.Children.Add(btnRow);

            _status.Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA5, 0xB5));
            _status.FontSize = 12;
            _status.TextWrapping = TextWrapping.Wrap;
            _status.Margin = new Thickness(0, 10, 0, 0);
            _status.Text = _deck.IsLoaded ? "Ready." : "Load a song on this deck first (a local file, or stream one from the Suno tab).";
            _encodeBtn.IsEnabled = _deck.IsLoaded;
            root.Children.Add(_status);

            Content = root;
            Closed += (s, e) => _cts?.Cancel();
        }

        private static SolidColorBrush Muted => new SolidColorBrush(Color.FromRgb(0xA0, 0xA5, 0xB5));

        private StackPanel Row(string label, ComboBox box)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            row.Children.Add(new TextBlock { Text = label, Foreground = Muted, FontSize = 12, Width = 110, VerticalAlignment = VerticalAlignment.Center });
            box.Width = 250;
            row.Children.Add(box);
            return row;
        }

        private void RefreshRows()
        {
            bool mp3 = _formatBox.SelectedIndex == 1;
            bool wav = _formatBox.SelectedIndex == 2;
            _bitrateRow.Visibility = mp3 ? Visibility.Visible : Visibility.Collapsed;
            _depthRow.Visibility = mp3 ? Visibility.Collapsed : Visibility.Visible;
        }

        private async void Encode_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null) { _cts.Cancel(); return; }
            var options = new EncodeOptions
            {
                Format = _formatBox.SelectedIndex switch { 1 => "MP3", 2 => "WAV", _ => "FLAC" },
                Mp3BitrateKbps = _bitrateBox.SelectedIndex switch { 1 => 256, 2 => 192, 3 => 128, _ => 320 },
                SampleRate = _rateBox.SelectedIndex == 1 ? 48000 : 44100,
                BitDepth = _depthBox.SelectedIndex == 1 ? 24 : 16,
                ApplyAutoLevel = _autoLevelBox.IsChecked == true,
                AutoLevelDb = _autoLevel.GainDb
            };
            _cts = new CancellationTokenSource();
            _encodeBtn.Content = "⏹ Cancel";
            var progress = new Progress<string>(p => _status.Text = p);
            try
            {
                var result = await AudioEncoder.EncodeDeckAsync(_deck, _deck.Title, options, _folderBox.Text.Trim(), progress, _cts.Token);
                _status.Text = result.Message;
                if (result.Ok)
                {
                    _lastOutput = result.OutputPath;
                    _openBtn.IsEnabled = true;
                    if (options.ApplyAutoLevel && options.AutoLevelDb != 0)
                        _status.Text = result.Message + " (auto-leveled to -14 LUFS)";
                }
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                _encodeBtn.Content = "💾 Encode";
            }
        }
    }
}
