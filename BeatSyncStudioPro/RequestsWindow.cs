using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// Listener requests window (v3.87): shows the song requests and
    /// shoutouts listeners sent from the public Rad-Dad Radio request
    /// page (served by the Companion server at /request). Requests live
    /// in the CompanionServer (persisted to requests.json); this window
    /// is a live view — refresh timer plus Dismiss / Clear All actions.
    /// </summary>
    public sealed class RequestsWindow : Window
    {
        private readonly CompanionServer _server;
        private readonly ChatRequestMonitor? _chat;
        private TextBox _twitchBox = null!;
        private TextBox _ytVideoBox = null!;
        private PasswordBox _ytKeyBox = null!;
        private TextBlock _chatStatus = null!;
        private Button _chatToggle = null!;
        private readonly StackPanel _list = new StackPanel();
        private readonly TextBlock _empty = new TextBlock();
        private readonly DispatcherTimer _timer;

        public RequestsWindow(CompanionServer server, ChatRequestMonitor? chat = null)
        {
            _server = server;
            _chat = chat;
            Title = "📨 Listener Requests — Rad-Dad Radio";
            Width = 520;
            Height = 560;
            MinWidth = 400;
            MinHeight = 300;
            Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0E, 0x14));
            Foreground = Brushes.White;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var root = new DockPanel { Margin = new Thickness(14) };

            var header = new TextBlock
            {
                Text = "Listeners send these from the request page while the Companion server is running:\n" +
                       "this PC's address, port 8765, /request  (e.g. http://192.168.1.242:8765/request on Wi-Fi).\n" +
                       "Share that link and requests land here live.\n" +
                       "Streaming? Viewers can also type  !request Song Name  in your Twitch or YouTube chat (setup below).",
                Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA5, 0xB5)),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            if (_chat != null)
            {
                var chatPanel = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x12, 0x16, 0x1F)),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(12),
                    Margin = new Thickness(0, 0, 0, 10)
                };
                var cp = new StackPanel();
                cp.Children.Add(new TextBlock { Text = "💬 Chat Requests", FontSize = 14, FontWeight = FontWeights.Bold, Foreground = Brushes.White });
                cp.Children.Add(new TextBlock
                {
                    Text = "Twitch needs only your channel name (read-only, no login). YouTube needs the live video ID and your own free YouTube Data API key (saved encrypted on this PC).",
                    Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA5, 0xB5)), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 8)
                });
                var row1 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
                row1.Children.Add(new TextBlock { Text = "Twitch channel", Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA5, 0xB5)), FontSize = 12, Width = 110, VerticalAlignment = VerticalAlignment.Center });
                _twitchBox = new TextBox { Width = 220, Padding = new Thickness(6, 4, 6, 4), Text = _chat.Settings.TwitchChannel };
                row1.Children.Add(_twitchBox);
                cp.Children.Add(row1);
                var row2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
                row2.Children.Add(new TextBlock { Text = "YouTube video ID", Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA5, 0xB5)), FontSize = 12, Width = 110, VerticalAlignment = VerticalAlignment.Center });
                _ytVideoBox = new TextBox { Width = 220, Padding = new Thickness(6, 4, 6, 4), Text = _chat.Settings.YoutubeVideoId };
                row2.Children.Add(_ytVideoBox);
                cp.Children.Add(row2);
                var row3 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
                row3.Children.Add(new TextBlock { Text = "YouTube API key", Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA5, 0xB5)), FontSize = 12, Width = 110, VerticalAlignment = VerticalAlignment.Center });
                _ytKeyBox = new PasswordBox { Width = 220, Padding = new Thickness(6, 4, 6, 4) };
                row3.Children.Add(_ytKeyBox);
                row3.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(_chat.Settings.GetYoutubeApiKey()) ? "(not saved)" : "(saved — blank keeps it)", Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA5, 0xB5)), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
                cp.Children.Add(row3);
                var row4 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
                _chatToggle = new Button
                {
                    Content = _chat.IsRunning ? "⏹ Stop Chat Requests" : "▶ Start Chat Requests",
                    Padding = new Thickness(12, 6, 12, 6),
                    Background = new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54)),
                    Foreground = Brushes.White, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand
                };
                _chatToggle.Click += (s2, e2) =>
                {
                    if (_chat.IsRunning) _chat.Stop();
                    else
                    {
                        string? key = string.IsNullOrWhiteSpace(_ytKeyBox.Password) ? null : _ytKeyBox.Password;
                        _chat.SaveSettings(_twitchBox.Text, _ytVideoBox.Text, key);
                        _chat.Start();
                    }
                    Refresh();
                };
                row4.Children.Add(_chatToggle);
                _chatStatus = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA5, 0xB5)), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), TextWrapping = TextWrapping.Wrap };
                row4.Children.Add(_chatStatus);
                cp.Children.Add(row4);
                chatPanel.Child = cp;
                DockPanel.SetDock(chatPanel, Dock.Top);
                root.Children.Add(chatPanel);
            }

            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };
            var clearBtn = new Button
            {
                Content = "🧹 Clear All",
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x3C)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            clearBtn.Click += (s, e) =>
            {
                if (_server.PendingRequestCount == 0) return;
                if (MessageBox.Show("Clear ALL listener requests?", "Listener Requests",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    _server.ClearRequests();
                    Refresh();
                }
            };
            footer.Children.Add(clearBtn);
            var closeBtn = new Button
            {
                Content = "Close",
                Padding = new Thickness(12, 6, 12, 6),
                Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x3C)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            closeBtn.Click += (s, e) => Close();
            footer.Children.Add(closeBtn);
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            _empty.Text = "No requests waiting. When a listener sends one, it shows up here.";
            _empty.Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA5, 0xB5));
            _empty.FontSize = 13;
            _empty.TextWrapping = TextWrapping.Wrap;

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = _list
            };
            root.Children.Add(scroll);

            Content = root;
            Refresh();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += (s, e) => Refresh();
            _timer.Start();
            Closed += (s, e) => _timer.Stop();
        }

        private void Refresh()
        {
            if (_chat != null && _chatStatus != null)
            {
                _chatStatus.Text = _chat.StatusText;
                if (_chatToggle != null) _chatToggle.Content = _chat.IsRunning ? "⏹ Stop Chat Requests" : "▶ Start Chat Requests";
            }
            var reqs = _server.SnapshotRequests();
            _list.Children.Clear();
            if (reqs.Count == 0)
            {
                _list.Children.Add(_empty);
                return;
            }
            foreach (var r in reqs)
            {
                var card = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x12, 0x16, 0x1F)),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(12),
                    Margin = new Thickness(0, 0, 0, 8)
                };
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var texts = new StackPanel();
                texts.Children.Add(new TextBlock
                {
                    Text = "🎵 " + r.Song,
                    FontSize = 15,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                    TextWrapping = TextWrapping.Wrap
                });
                string who = string.IsNullOrWhiteSpace(r.Name) ? "Anonymous listener" : r.Name;
                if (!string.IsNullOrWhiteSpace(r.Source) && r.Source != "Web") who += "  •  via " + r.Source;
                else if (r.Source == "Web") who += "  •  via Web";
                texts.Children.Add(new TextBlock
                {
                    Text = who + "  •  " + r.WhenUtc.ToLocalTime().ToString("h:mm tt"),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA5, 0xB5)),
                    FontSize = 12,
                    Margin = new Thickness(0, 2, 0, 0)
                });
                if (!string.IsNullOrWhiteSpace(r.Shoutout))
                {
                    texts.Children.Add(new TextBlock
                    {
                        Text = "“" + r.Shoutout + "”",
                        Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xC7, 0x66)),
                        FontSize = 13,
                        FontStyle = FontStyles.Italic,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 4, 0, 0)
                    });
                }
                Grid.SetColumn(texts, 0);
                grid.Children.Add(texts);

                var dismiss = new Button
                {
                    Content = "✔ Done",
                    Padding = new Thickness(10, 5, 10, 5),
                    VerticalAlignment = VerticalAlignment.Top,
                    Background = new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54)),
                    Foreground = Brushes.White,
                    BorderThickness = new Thickness(0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Tag = r.Id
                };
                dismiss.Click += (s, e) =>
                {
                    if (s is Button b && b.Tag is int id)
                    {
                        _server.DismissRequest(id);
                        Refresh();
                    }
                };
                Grid.SetColumn(dismiss, 1);
                grid.Children.Add(dismiss);

                card.Child = grid;
                _list.Children.Add(card);
            }
        }
    }
}
