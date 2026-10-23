using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// Internet Radio window (v3.99): listen to online radio stations
    /// through the DJ mix. Ships with Michael's local Rutland stations
    /// (Rock 94.5 WDVT and Z97.1 WZRT, direct streams from the stations'
    /// own web players) plus Rad-Dad Radio. Any station with a direct
    /// stream URL can be added — it plays over the mix, lands in
    /// recordings and the live stream, and shows the station's own
    /// now-playing song title when the stream provides one.
    /// </summary>
    public sealed class RadioWindow : Window
    {
        private readonly RadioPlayer _player;
        private List<RadioStation> _stations;
        private readonly ListBox _list = new ListBox();
        private readonly TextBlock _nowPlaying = new TextBlock();
        private readonly TextBlock _status = new TextBlock();
        private readonly TextBox _nameBox = new TextBox();
        private readonly TextBox _urlBox = new TextBox();
        private readonly TextBox _searchBox = new TextBox();
        private readonly ListBox _results = new ListBox();
        private readonly List<DirectoryStation> _resultStations = new List<DirectoryStation>();

        private static readonly Brush Panel = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x3C));
        private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54));
        private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0xA0, 0xA5, 0xB5));

        public RadioWindow(RadioPlayer player)
        {
            _player = player;
            _stations = RadioStore.Load();
            Title = "📡 Internet Radio — Listen Live";
            Width = 560;
            Height = 640;
            MinWidth = 460;
            MinHeight = 420;
            Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0E, 0x14));
            Foreground = Brushes.White;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var root = new StackPanel { Margin = new Thickness(14) };
            root.Children.Add(new TextBlock
            {
                Text = "Listen to live radio through the DJ mix — it plays over the board, and lands in your recordings and stream like everything else. Double-click a station to play it.",
                Foreground = Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
            });

            _list.Height = 170;
            _list.MouseDoubleClick += (s, e) => PlaySelected();
            root.Children.Add(_list);

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            var play = new Button
            {
                Content = "▶ Play", Padding = new Thickness(16, 8, 16, 8),
                Background = Green, Foreground = Brushes.White, BorderThickness = new Thickness(0),
                FontWeight = FontWeights.Bold, Cursor = System.Windows.Input.Cursors.Hand
            };
            play.Click += (s, e) => PlaySelected();
            btnRow.Children.Add(play);
            var stop = new Button
            {
                Content = "⏹ Stop", Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(8, 0, 0, 0),
                Background = Panel, Foreground = Brushes.White, BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            stop.Click += (s, e) => _player.Stop();
            btnRow.Children.Add(stop);
            var remove = new Button
            {
                Content = "Remove station", Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(8, 0, 0, 0),
                Background = Panel, Foreground = Brushes.White, BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            remove.Click += (s, e) => RemoveSelected();
            btnRow.Children.Add(remove);
            root.Children.Add(btnRow);

            var volRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            volRow.Children.Add(new TextBlock { Text = "Radio volume", Foreground = Muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            var vol = new Slider { Minimum = 0, Maximum = 1.2, Value = _player.Output.Volume, Width = 180, VerticalAlignment = VerticalAlignment.Center };
            vol.ValueChanged += (s, e) => _player.Output.Volume = (float)vol.Value;
            volRow.Children.Add(vol);
            root.Children.Add(volRow);

            _nowPlaying.FontSize = 14;
            _nowPlaying.FontWeight = FontWeights.Bold;
            _nowPlaying.Foreground = Brushes.White;
            _nowPlaying.TextWrapping = TextWrapping.Wrap;
            _nowPlaying.Margin = new Thickness(0, 14, 0, 0);
            root.Children.Add(_nowPlaying);
            _status.Foreground = Muted;
            _status.FontSize = 12;
            _status.TextWrapping = TextWrapping.Wrap;
            _status.Margin = new Thickness(0, 4, 0, 0);
            root.Children.Add(_status);

            var addPanel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
            addPanel.Children.Add(new TextBlock { Text = "ADD A STATION", FontSize = 11, Foreground = Muted, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) });
            _nameBox.Padding = new Thickness(6, 4, 6, 4);
            _nameBox.Margin = new Thickness(0, 0, 0, 6);
            _nameBox.ToolTip = "Station name, as it should show in the list.";
            _nameBox.Text = "";
            addPanel.Children.Add(Labeled("Name", _nameBox));
            _urlBox.Padding = new Thickness(6, 4, 6, 4);
            _urlBox.ToolTip = "The station's direct stream address (ends in .aac, .mp3, or looks like .../stream). Most station websites hide it inside their Listen Live player.";
            addPanel.Children.Add(Labeled("Stream URL", _urlBox));
            var add = new Button
            {
                Content = "➕ Add station", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = Panel, Foreground = Brushes.White, BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            add.Click += (s, e) => AddStation();
            addPanel.Children.Add(add);
            root.Children.Add(addPanel);

            // ---- Find stations (v3.100): the verified Vermont directory
            // built into the app, plus the free Radio Browser online
            // directory for anything else, anywhere. ----
            var findPanel = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
            findPanel.Children.Add(new TextBlock { Text = "FIND STATIONS — VERMONT DIRECTORY + ONLINE SEARCH", FontSize = 11, Foreground = Muted, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
            findPanel.Children.Add(new TextBlock
            {
                Text = "Every Vermont station in the built-in directory was stream-tested live. Type a name, a town, or a style (country, jazz, rock...) and search — or just press Search to browse all of Vermont. Double-click a result to add it to your stations.",
                Foreground = Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
            });
            var searchRow = new StackPanel { Orientation = Orientation.Horizontal };
            _searchBox.Width = 300;
            _searchBox.Padding = new Thickness(6, 4, 6, 4);
            _searchBox.ToolTip = "Station name, Vermont town, or a format like country or jazz. Leave empty to browse all of Vermont.";
            _searchBox.KeyDown += (s2, e2) => { if (e2.Key == System.Windows.Input.Key.Enter) _ = RunSearch(); };
            searchRow.Children.Add(_searchBox);
            var searchBtn = new Button
            {
                Content = "🔍 Search", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0),
                Background = Green, Foreground = Brushes.White, BorderThickness = new Thickness(0),
                FontWeight = FontWeights.Bold, Cursor = System.Windows.Input.Cursors.Hand
            };
            searchBtn.Click += async (s2, e2) => await RunSearch();
            searchRow.Children.Add(searchBtn);
            findPanel.Children.Add(searchRow);
            _results.Height = 150;
            _results.Margin = new Thickness(0, 8, 0, 0);
            _results.MouseDoubleClick += (s2, e2) => AddSelectedResult();
            findPanel.Children.Add(_results);
            var addResult = new Button
            {
                Content = "➕ Add selected to My Stations", Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = Panel, Foreground = Brushes.White, BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            addResult.Click += (s2, e2) => AddSelectedResult();
            findPanel.Children.Add(addResult);
            root.Children.Add(findPanel);

            Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            RefreshList();
            RefreshState();
            _player.StateChanged += OnPlayerState;
            Closed += (s, e) => _player.StateChanged -= OnPlayerState;
        }

        private static StackPanel Labeled(string label, TextBox box)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            row.Children.Add(new TextBlock { Text = label, Foreground = Muted, FontSize = 12, Width = 76, VerticalAlignment = VerticalAlignment.Center });
            box.Width = 400;
            row.Children.Add(box);
            return row;
        }

        private void OnPlayerState() => Dispatcher.BeginInvoke(new Action(RefreshState));

        private void RefreshList()
        {
            _list.Items.Clear();
            foreach (var st in _stations) _list.Items.Add(st.Name);
        }

        private void RefreshState()
        {
            if (_player.IsPlaying)
            {
                _nowPlaying.Text = "🔊 " + _player.CurrentStation +
                    (string.IsNullOrEmpty(_player.NowPlayingTitle) ? "" : "\n♪ " + _player.NowPlayingTitle);
            }
            else _nowPlaying.Text = "";
            _status.Text = _player.Status;
        }

        private void PlaySelected()
        {
            int i = _list.SelectedIndex;
            if (i < 0 || i >= _stations.Count) { _status.Text = "Pick a station in the list first."; return; }
            _player.Play(_stations[i]);
        }

        private void RemoveSelected()
        {
            int i = _list.SelectedIndex;
            if (i < 0 || i >= _stations.Count) return;
            if (_player.IsPlaying && _player.CurrentStation == _stations[i].Name) _player.Stop();
            _stations.RemoveAt(i);
            RadioStore.Save(_stations);
            RefreshList();
        }

        private async System.Threading.Tasks.Task RunSearch()
        {
            string q = _searchBox.Text.Trim();
            _resultStations.Clear();
            _results.Items.Clear();
            foreach (var st in RadioDirectory.SearchLocal(q)) AddResult(st);
            _status.Text = _resultStations.Count > 0
                ? $"Found {_resultStations.Count} in the built-in Vermont directory — checking the online directory too…"
                : "Checking the online directory…";
            try
            {
                var online = (q.Length == 0 || q.Equals("vermont", StringComparison.OrdinalIgnoreCase))
                    ? await RadioBrowser.VermontOnline()
                    : await RadioBrowser.SearchByName(q);
                int before = _resultStations.Count;
                foreach (var st in online) AddResult(st);
                _status.Text = _resultStations.Count > 0
                    ? $"Found {_resultStations.Count} station(s). Double-click one to add it to your stations."
                    : "No stations found — try a different name or town.";
                if (_resultStations.Count == before && before == 0) { /* status already set */ }
            }
            catch
            {
                if (_resultStations.Count == 0) _status.Text = "Online search didn't answer — the built-in Vermont directory still works offline.";
            }
        }

        private void AddResult(DirectoryStation st)
        {
            foreach (var existing in _resultStations)
                if (existing.Url == st.Url) return;
            _resultStations.Add(st);
            _results.Items.Add(st.Display);
        }

        private void AddSelectedResult()
        {
            int i = _results.SelectedIndex;
            if (i < 0 || i >= _resultStations.Count) { _status.Text = "Pick a search result first."; return; }
            var st = _resultStations[i];
            string storeName = st.City.Length > 0 ? st.Name + " (" + st.City + ")" : st.Name;
            foreach (var existing in _stations)
                if (existing.Url == st.Url) { _status.Text = $"'{st.Name}' is already in your stations."; return; }
            _stations.Add(new RadioStation { Name = storeName, Url = st.Url });
            RadioStore.Save(_stations);
            RefreshList();
            _list.SelectedIndex = _stations.Count - 1;
            _status.Text = $"Added '{st.Name}' to your stations — double-click it in your list to play.";
        }

        private void AddStation()
        {
            string name = _nameBox.Text.Trim();
            string url = _urlBox.Text.Trim();
            if (name.Length == 0) { _status.Text = "Give the station a name first."; return; }
            if (!RadioStore.IsValidStreamUrl(url)) { _status.Text = "That stream URL doesn't look right — it should start with http:// or https://."; return; }
            _stations.Add(new RadioStation { Name = name, Url = url });
            RadioStore.Save(_stations);
            RefreshList();
            _list.SelectedIndex = _stations.Count - 1;
            _nameBox.Text = "";
            _urlBox.Text = "";
            _status.Text = $"Added '{name}'. Double-click it to play.";
        }
    }
}
