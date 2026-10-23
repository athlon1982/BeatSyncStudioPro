using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// Go Live window (v3.88): stream the Output (visuals) window + the
    /// DJ master audio to YouTube, Facebook, TikTok, or any RTMP
    /// address, through the StreamEngine's ffmpeg child process.
    /// Stream keys are saved per destination, DPAPI-encrypted on this
    /// PC only (StreamSettings).
    /// </summary>
    public sealed class StreamWindow : Window
    {
        private readonly StreamEngine _engine;
        private readonly StreamSettings _settings = StreamSettings.Load();

        private readonly ComboBox _destBox = new ComboBox();
        private readonly TextBox _urlBox = new TextBox();
        private readonly PasswordBox _keyBox = new PasswordBox();
        private readonly ComboBox _qualityBox = new ComboBox();
        private readonly Button _goButton = new Button();
        private readonly TextBlock _status = new TextBlock();
        private readonly TextBlock _ffmpegNote = new TextBlock();
        private readonly Button _ffmpegButton = new Button();
        private readonly DispatcherTimer _timer;
        private bool _loading = true;

        private static readonly (string Name, string Url, bool UrlEditable)[] Destinations =
        {
            ("YouTube", "rtmp://a.rtmp.youtube.com/live2", false),
            ("Twitch", "rtmp://live.twitch.tv/app", false),
            ("Facebook", "rtmps://live-api-s.facebook.com:443/rtmp", false),
            ("TikTok", "", true),
            ("Custom RTMP", "", true)
        };

        public StreamWindow(StreamEngine engine)
        {
            _engine = engine;
            Title = "🔴 Go Live — Stream the Visuals";
            Width = 560;
            Height = 600;
            MinWidth = 440;
            MinHeight = 480;
            Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0E, 0x14));
            Foreground = Brushes.White;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var root = new StackPanel { Margin = new Thickness(16) };

            root.Children.Add(new TextBlock
            {
                Text = "The stream shows your Output (visuals) window exactly as it sits on screen, " +
                       "with the DJ mix as the audio. Keep the Output window visible and uncovered while live — " +
                       "anything covering it covers the stream.",
                Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA5, 0xB5)),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            });

            root.Children.Add(Label("Destination"));
            foreach (var d in Destinations) _destBox.Items.Add(d.Name);
            _destBox.SelectedIndex = 0;
            _destBox.Margin = new Thickness(0, 0, 0, 10);
            _destBox.SelectionChanged += (s, e) => { if (!_loading) ApplyDestination(); };
            root.Children.Add(_destBox);

            root.Children.Add(Label("Stream address (RTMP)"));
            _urlBox.Margin = new Thickness(0, 0, 0, 4);
            root.Children.Add(_urlBox);
            root.Children.Add(new TextBlock
            {
                Text = "TikTok: paste the RTMP address from LIVE Studio (eligible accounts only), then the key below. " +
                       "YouTube: address is filled in — just paste your stream key from YouTube Studio. " +
                       "Twitch: key from your Creator Dashboard, Settings, Stream. " +
                       "Facebook: key from Live Producer.",
                Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA5, 0xB5)),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });

            root.Children.Add(Label("Stream key (saved encrypted on this PC)"));
            _keyBox.Margin = new Thickness(0, 0, 0, 10);
            root.Children.Add(_keyBox);

            root.Children.Add(Label("Quality"));
            _qualityBox.Items.Add("720p — 1280×720, 30 fps (recommended)");
            _qualityBox.Items.Add("480p — 854×480, 30 fps (easier on the PC)");
            _qualityBox.SelectedIndex = _settings.Quality720 ? 0 : 1;
            _qualityBox.Margin = new Thickness(0, 0, 0, 14);
            root.Children.Add(_qualityBox);

            _goButton.Height = 42;
            _goButton.FontSize = 16;
            _goButton.FontWeight = FontWeights.Bold;
            _goButton.Foreground = Brushes.White;
            _goButton.BorderThickness = new Thickness(0);
            _goButton.Cursor = System.Windows.Input.Cursors.Hand;
            _goButton.Click += GoButton_Click;
            root.Children.Add(_goButton);

            _status.FontSize = 13;
            _status.TextWrapping = TextWrapping.Wrap;
            _status.Margin = new Thickness(0, 10, 0, 0);
            root.Children.Add(_status);

            _ffmpegNote.Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xC7, 0x66));
            _ffmpegNote.FontSize = 12;
            _ffmpegNote.TextWrapping = TextWrapping.Wrap;
            _ffmpegNote.Margin = new Thickness(0, 12, 0, 0);
            root.Children.Add(_ffmpegNote);

            _ffmpegButton.Content = "⬇ Download ffmpeg (one time, ~110 MB)";
            _ffmpegButton.Padding = new Thickness(12, 6, 12, 6);
            _ffmpegButton.Margin = new Thickness(0, 6, 0, 0);
            _ffmpegButton.Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x3C));
            _ffmpegButton.Foreground = Brushes.White;
            _ffmpegButton.BorderThickness = new Thickness(0);
            _ffmpegButton.Cursor = System.Windows.Input.Cursors.Hand;
            _ffmpegButton.Click += async (s, e) => await DownloadFfmpegAsync();
            root.Children.Add(_ffmpegButton);

            Content = root;

            int sel = 0;
            for (int i = 0; i < Destinations.Length; i++)
                if (Destinations[i].Name == _settings.Destination) sel = i;
            _destBox.SelectedIndex = sel;
            ApplyDestination();
            _loading = false;
            RefreshGoButton();
            RefreshFfmpegNote();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _timer.Tick += (s, e) => RefreshGoButton();
            _timer.Start();
            Closed += (s, e) => _timer.Stop();
        }

        private static TextBlock Label(string text) => new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA5, 0xB5)),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 4)
        };

        private void ApplyDestination()
        {
            int i = Math.Max(0, _destBox.SelectedIndex);
            var d = Destinations[i];
            _urlBox.IsReadOnly = !d.UrlEditable;
            if (d.Name == "Custom RTMP") _urlBox.Text = _settings.CustomUrl;
            else if (d.Name == "TikTok") _urlBox.Text = _settings.TikTokUrl;
            else _urlBox.Text = d.Url;
            _keyBox.Password = _settings.GetKey(d.Name);
        }

        private void SaveForm()
        {
            int i = Math.Max(0, _destBox.SelectedIndex);
            var d = Destinations[i];
            _settings.Destination = d.Name;
            if (d.Name == "Custom RTMP") _settings.CustomUrl = _urlBox.Text.Trim();
            if (d.Name == "TikTok") _settings.TikTokUrl = _urlBox.Text.Trim();
            _settings.Quality720 = _qualityBox.SelectedIndex == 0;
            _settings.SetKey(d.Name, _keyBox.Password);
            _settings.Save();
        }

        private void RefreshFfmpegNote()
        {
            var ff = StreamEngine.FindFfmpeg();
            if (ff != null)
            {
                _ffmpegNote.Text = "";
                _ffmpegButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                _ffmpegNote.Text = "ffmpeg (the stream encoder) isn't installed yet. Download it once and this window is ready to go live.";
                _ffmpegButton.Visibility = Visibility.Visible;
            }
        }

        private void RefreshGoButton()
        {
            if (_engine.IsLive)
            {
                var el = DateTime.UtcNow - _engine.LiveSinceUtc;
                _goButton.Content = "⏹ Stop Stream";
                _goButton.Background = new SolidColorBrush(Color.FromRgb(0xA1, 0x24, 0x24));
                _status.Text = $"🔴 LIVE {el.Hours:D2}:{el.Minutes:D2}:{el.Seconds:D2} • frames sent {_engine.FramesSent} • dropped {_engine.FramesDropped}";
            }
            else
            {
                _goButton.Content = "🔴 Go Live";
                _goButton.Background = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
                if (_engine.LastError != null) _status.Text = _engine.LastError;
            }
        }

        private async void GoButton_Click(object sender, RoutedEventArgs e)
        {
            if (_engine.IsLive)
            {
                _status.Text = "Stopping…";
                await Task.Run(() => _engine.Stop());
                _status.Text = "Stream stopped.";
                RefreshGoButton();
                return;
            }
            SaveForm();
            string url = _urlBox.Text.Trim();
            string key = _keyBox.Password.Trim();
            if (string.IsNullOrEmpty(url)) { _status.Text = "Paste the stream address first."; return; }
            if (string.IsNullOrEmpty(key)) { _status.Text = "Paste your stream key first — it's in your platform's live dashboard."; return; }
            string full = url.TrimEnd('/') + "/" + key;
            bool hd = _qualityBox.SelectedIndex == 0;
            int w = hd ? 1280 : 854, h = hd ? 720 : 480, br = hd ? 4500 : 2500;
            _status.Text = "Starting… (opening the Output window if needed)";
            string? err = await Task.Run(() => _engine.Start(full, w, h, 30, br));
            if (err != null) { _engine.LastError = err; _status.Text = err; }
            else _status.Text = "";
            RefreshGoButton();
        }

        private async Task DownloadFfmpegAsync()
        {
            _ffmpegButton.IsEnabled = false;
            _ffmpegNote.Text = "Downloading ffmpeg…";
            try
            {
                string zipPath = Path.Combine(Path.GetTempPath(), "beatsync-ffmpeg.zip");
                using (var http = new HttpClient())
                {
                    var data = await http.GetByteArrayAsync("https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip");
                    await File.WriteAllBytesAsync(zipPath, data);
                }
                string tools = StreamEngine.ToolsDir;
                Directory.CreateDirectory(tools);
                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    foreach (var entry in zip.Entries)
                    {
                        if (entry.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            entry.ExtractToFile(Path.Combine(tools, "ffmpeg.exe"), true);
                            break;
                        }
                    }
                }
                try { File.Delete(zipPath); } catch { }
                _ffmpegNote.Text = StreamEngine.FindFfmpeg() != null
                    ? "ffmpeg installed — you're ready to go live."
                    : "Download finished but ffmpeg.exe wasn't found in it — tell Jarvis.";
            }
            catch (Exception ex)
            {
                _ffmpegNote.Text = "Download failed: " + ex.Message;
            }
            _ffmpegButton.IsEnabled = true;
            RefreshFfmpegNote();
        }
    }
}
