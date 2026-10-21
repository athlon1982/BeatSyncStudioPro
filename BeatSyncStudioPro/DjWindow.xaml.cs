using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// The DJ decks screen: two decks sharing one crossfader mixer, routed
    /// into the app's main engine. Decks load from the asset library or from
    /// files; each has transport, CUE, tempo + bend, SYNC, 4 hot cues, beat
    /// loops, gain and the full track FX. The mix can be recorded to WAV.
    /// </summary>
    public partial class DjWindow : Window
    {
        private readonly MainWindow _owner;
        private readonly DjDeck _deckA = new DjDeck();
        private readonly DjDeck _deckB = new DjDeck();
        private readonly TrackFxSettings _fxA = new TrackFxSettings();
        private readonly TrackFxSettings _fxB = new TrackFxSettings();
        private readonly VolumeSampleProvider _xfadeA;
        private readonly VolumeSampleProvider _xfadeB;
        private readonly VolumeSampleProvider _masterGain;
        private readonly RecordingSampleProvider _recorder;
        private readonly DeckPanel _panelA;
        private readonly DeckPanel _panelB;
        private readonly DispatcherTimer _timer;
        private TrackFxWindow? _fxWindow;
        private string _fxTarget = "";

        public DjWindow(MainWindow owner)
        {
            InitializeComponent();
            _owner = owner;

            var format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
            var djMaster = new MixingSampleProvider(format) { ReadFully = true };
            var chainA = BuildDeckChain(_deckA, _fxA, djMaster);
            var chainB = BuildDeckChain(_deckB, _fxB, djMaster);
            _xfadeA = chainA.Xfade;
            _xfadeB = chainB.Xfade;
            _masterGain = new VolumeSampleProvider(djMaster) { Volume = 0.9f };
            _recorder = new RecordingSampleProvider(_masterGain);
            _owner.AttachDjMixer(_recorder);

            _panelA = new DeckPanel(this, _deckA, () => _deckB, _fxA, "DECK A",
                new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54)), DeckHostA, chainA.Channel);
            _panelB = new DeckPanel(this, _deckB, () => _deckA, _fxB, "DECK B",
                new SolidColorBrush(Color.FromRgb(0xB3, 0x88, 0xFF)), DeckHostB, chainB.Channel);

            XfadeSlider.ValueChanged += (s, e) => UpdateCrossfader();
            CurvePicker.SelectionChanged += (s, e) => UpdateCrossfader();
            DjMasterSlider.ValueChanged += (s, e) => _masterGain.Volume = (float)DjMasterSlider.Value;
            UpdateCrossfader();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _timer.Tick += (s, e) => { _panelA.Refresh(); _panelB.Refresh(); };
            _timer.Start();
            Closed += (s, e) => { _timer.Stop(); _recorder.Stop(); };
        }

        private (VolumeSampleProvider Xfade, VolumeSampleProvider Channel) BuildDeckChain(
            DjDeck deck, TrackFxSettings fx, MixingSampleProvider master)
        {
            var eq = new EqSampleProvider(deck, fx);
            var delay = new DelaySampleProvider(eq, fx);
            var reverb = new ReverbSampleProvider(delay, fx);
            var channel = new VolumeSampleProvider(reverb) { Volume = 1.0f };
            var xfade = new VolumeSampleProvider(channel) { Volume = 0.707f };
            master.AddMixerInput(xfade);
            return (xfade, channel);
        }

        private void UpdateCrossfader()
        {
            double x = XfadeSlider.Value / 1000.0;
            double gA, gB;
            switch (CurvePicker.SelectedIndex)
            {
                case 0: // Smooth (linear)
                    gA = 1 - x; gB = x;
                    break;
                case 2: // Cut
                    gA = x <= 0.45 ? 1 : x >= 0.55 ? 0 : 1 - (x - 0.45) / 0.1;
                    gB = x >= 0.55 ? 1 : x <= 0.45 ? 0 : (x - 0.45) / 0.1;
                    break;
                default: // Constant power
                    gA = Math.Cos(x * Math.PI / 2);
                    gB = Math.Sin(x * Math.PI / 2);
                    break;
            }
            _xfadeA.Volume = (float)gA;
            _xfadeB.Volume = (float)gB;
        }

        public void SetStatus(string text) => DjStatus.Text = text;

        public void OpenFx(TrackFxSettings fx, string name)
        {
            if (_fxWindow == null || _fxTarget != name)
            {
                _fxWindow?.Close();
                _fxWindow = new TrackFxWindow(fx, name) { Owner = this };
                _fxWindow.Closed += (s, e) => { _fxWindow = null; _fxTarget = ""; };
                _fxTarget = name;
                _fxWindow.Show();
            }
            else
            {
                _fxWindow.Activate();
            }
        }

        private void RecordButton_Click(object sender, RoutedEventArgs e)
        {
            if (_recorder.IsRecording)
            {
                _recorder.Stop();
                RecordButton.Content = "🔴 Record Mix";
                RecordButton.Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x3C));
                SetStatus("Mix recording saved.");
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = "Record your DJ mix",
                Filter = "WAV audio|*.wav",
                FileName = "BeatSync DJ Mix.wav",
                DefaultExt = ".wav"
            };
            if (dialog.ShowDialog() != true) return;
            try
            {
                _recorder.Start(dialog.FileName);
                RecordButton.Content = "⏹ Stop Recording";
                RecordButton.Background = new SolidColorBrush(Color.FromRgb(0xA1, 0x24, 0x24));
                SetStatus($"Recording your mix to {System.IO.Path.GetFileName(dialog.FileName)}…");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not start recording: " + ex.Message, "Record Mix");
            }
        }

        internal static string FmtTime(double seconds)
        {
            if (seconds < 0 || double.IsNaN(seconds)) seconds = 0;
            var ts = TimeSpan.FromSeconds(seconds);
            return $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
        }

        // =========================
        // One deck's control panel
        // =========================

        private class DeckPanel
        {
            private readonly DjWindow _window;
            private readonly DjDeck _deck;
            private readonly Func<DjDeck> _other;
            private readonly TrackFxSettings _fx;
            private readonly string _name;
            private readonly SolidColorBrush _accent;
            private readonly VolumeSampleProvider _channelGain;

            private readonly TextBlock _title;
            private readonly Image _waveImage;
            private readonly Canvas _waveCanvas;
            private readonly Line _playheadLine;
            private readonly TextBlock _timeText;
            private readonly ComboBox _loadPicker;
            private readonly Button _playButton;
            private readonly Slider _tempoSlider;
            private readonly TextBlock _tempoLabel;
            private readonly TextBox _bpmBox;
            private readonly TextBlock _effBpmText;
            private readonly Button[] _hotButtons = new Button[4];
            private readonly Button[] _loopButtons = new Button[4];
            private readonly List<DateTime> _taps = new List<DateTime>();

            private static readonly Brush PanelBtn = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x3C));
            private static readonly Brush MutedText = new SolidColorBrush(Color.FromRgb(0xA0, 0xA5, 0xB5));

            public DeckPanel(DjWindow window, DjDeck deck, Func<DjDeck> other, TrackFxSettings fx, string name,
                SolidColorBrush accent, Border host, VolumeSampleProvider channelGain)
            {
                _window = window;
                _deck = deck;
                _other = other;
                _fx = fx;
                _name = name;
                _accent = accent;
                _channelGain = channelGain;

                var root = new StackPanel();

                _title = new TextBlock { Text = $"{name} — empty", FontSize = 14, FontWeight = FontWeights.Bold, Foreground = accent, Margin = new Thickness(0, 0, 0, 6) };
                root.Children.Add(_title);

                // Waveform + playhead + scrub
                var waveGrid = new Grid { Height = 74, Background = new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x1E)) };
                _waveImage = new Image { Stretch = Stretch.Fill };
                waveGrid.Children.Add(_waveImage);
                _waveCanvas = new Canvas { Background = Brushes.Transparent };
                _playheadLine = new Line { Y1 = 0, Y2 = 74, Stroke = accent, StrokeThickness = 2 };
                _waveCanvas.Children.Add(_playheadLine);
                _waveCanvas.MouseLeftButtonDown += Wave_Mouse;
                _waveCanvas.MouseMove += Wave_Mouse;
                waveGrid.Children.Add(_waveCanvas);
                root.Children.Add(waveGrid);

                _timeText = new TextBlock { Text = "0:00 / 0:00", FontSize = 11, Foreground = MutedText, Margin = new Thickness(0, 4, 0, 2) };
                root.Children.Add(_timeText);

                // Load row
                var loadRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
                _loadPicker = new ComboBox { Width = 230, VerticalAlignment = VerticalAlignment.Center };
                _loadPicker.DropDownOpened += (s, e) => FillLoadPicker();
                FillLoadPicker();
                loadRow.Children.Add(_loadPicker);
                var loadBtn = MakeButton("Load", 58);
                loadBtn.Margin = new Thickness(6, 0, 0, 0);
                loadBtn.Click += (s, e) => LoadFromPicker();
                loadRow.Children.Add(loadBtn);
                var fileBtn = MakeButton("📁 File…", 70);
                fileBtn.Margin = new Thickness(6, 0, 0, 0);
                fileBtn.Click += (s, e) => LoadFromFile();
                loadRow.Children.Add(fileBtn);
                root.Children.Add(loadRow);

                // Transport row
                var transport = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2) };
                _playButton = MakeButton("▶ PLAY", 92);
                _playButton.Background = accent;
                _playButton.Click += (s, e) => PlayPause();
                transport.Children.Add(_playButton);
                var cueBtn = MakeButton("CUE", 64);
                cueBtn.Margin = new Thickness(6, 0, 0, 0);
                cueBtn.Click += (s, e) => Cue();
                transport.Children.Add(cueBtn);
                var syncBtn = MakeButton("SYNC", 64);
                syncBtn.Margin = new Thickness(6, 0, 0, 0);
                syncBtn.Click += (s, e) => SyncToOther();
                transport.Children.Add(syncBtn);
                var fxBtn = MakeButton("🎛️ FX", 64);
                fxBtn.Margin = new Thickness(6, 0, 0, 0);
                fxBtn.Click += (s, e) => _window.OpenFx(_fx, _name);
                transport.Children.Add(fxBtn);
                var pflBtn = MakeButton("🎧 PFL", 64);
                pflBtn.Margin = new Thickness(6, 0, 0, 0);
                pflBtn.IsEnabled = false;
                pflBtn.ToolTip = "Headphone cueing needs a second audio output — it unlocks when the hardware is there.";
                transport.Children.Add(pflBtn);
                root.Children.Add(transport);

                // Tempo row
                var tempoRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
                tempoRow.Children.Add(new TextBlock { Text = "Tempo", Foreground = MutedText, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Width = 42 });
                var bendDown = MakeButton("−", 34);
                bendDown.PreviewMouseLeftButtonDown += (s, e) => _deck.Bend = 0.98;
                bendDown.PreviewMouseLeftButtonUp += (s, e) => _deck.Bend = 1.0;
                bendDown.MouseLeave += (s, e) => _deck.Bend = 1.0;
                tempoRow.Children.Add(bendDown);
                _tempoSlider = new Slider { Minimum = -8, Maximum = 8, Value = 0, Width = 170, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) };
                _tempoSlider.ValueChanged += (s, e) => SetTempo(_tempoSlider.Value);
                tempoRow.Children.Add(_tempoSlider);
                var bendUp = MakeButton("+", 34);
                bendUp.PreviewMouseLeftButtonDown += (s, e) => _deck.Bend = 1.02;
                bendUp.PreviewMouseLeftButtonUp += (s, e) => _deck.Bend = 1.0;
                bendUp.MouseLeave += (s, e) => _deck.Bend = 1.0;
                tempoRow.Children.Add(bendUp);
                _tempoLabel = new TextBlock { Text = "+0.0%", FontSize = 11, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), Width = 44 };
                tempoRow.Children.Add(_tempoLabel);
                var resetBtn = MakeButton("0%", 36);
                resetBtn.Click += (s, e) => _tempoSlider.Value = 0;
                tempoRow.Children.Add(resetBtn);
                root.Children.Add(tempoRow);

                // BPM row
                var bpmRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
                bpmRow.Children.Add(new TextBlock { Text = "BPM", Foreground = MutedText, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Width = 42 });
                _bpmBox = new TextBox { Width = 62, VerticalAlignment = VerticalAlignment.Center, Background = PanelBtn, Foreground = Brushes.White, BorderThickness = new Thickness(0), Padding = new Thickness(6, 3, 6, 3) };
                _bpmBox.LostFocus += (s, e) => ApplyBpmBox();
                _bpmBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) ApplyBpmBox(); };
                bpmRow.Children.Add(_bpmBox);
                var tapBtn = MakeButton("TAP", 52);
                tapBtn.Margin = new Thickness(6, 0, 0, 0);
                tapBtn.Click += (s, e) => TapTempo();
                bpmRow.Children.Add(tapBtn);
                _effBpmText = new TextBlock { Text = "", FontSize = 11, Foreground = MutedText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
                bpmRow.Children.Add(_effBpmText);
                root.Children.Add(bpmRow);

                // Hot cues
                var hotRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
                hotRow.Children.Add(new TextBlock { Text = "Hot Cue", Foreground = MutedText, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Width = 48 });
                for (int i = 0; i < 4; i++)
                {
                    int idx = i;
                    var b = MakeButton($"{i + 1}", 44);
                    b.Margin = new Thickness(4, 0, 0, 0);
                    b.Click += (s, e) => HotCue(idx);
                    b.PreviewMouseRightButtonDown += (s, e) => { _deck.HotCues[idx] = null; e.Handled = true; };
                    _hotButtons[i] = b;
                    hotRow.Children.Add(b);
                }
                hotRow.Children.Add(new TextBlock { Text = "(right-click clears)", Foreground = MutedText, FontSize = 9, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
                root.Children.Add(hotRow);

                // Loops
                var loopRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
                loopRow.Children.Add(new TextBlock { Text = "Loop", Foreground = MutedText, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Width = 48 });
                int[] beats = { 4, 8, 16, 0 };
                for (int i = 0; i < 4; i++)
                {
                    int b = beats[i];
                    var btn = MakeButton(b == 0 ? "Off" : $"{b}", 44);
                    btn.Margin = new Thickness(4, 0, 0, 0);
                    btn.Click += (s, e) => SetLoop(b);
                    _loopButtons[i] = btn;
                    loopRow.Children.Add(btn);
                }
                root.Children.Add(loopRow);

                // Gain
                var gainRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
                gainRow.Children.Add(new TextBlock { Text = "Gain", Foreground = MutedText, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Width = 48 });
                var gain = new Slider { Minimum = 0, Maximum = 1.25, Value = 1.0, Width = 200, VerticalAlignment = VerticalAlignment.Center };
                gain.ValueChanged += (s, e) => _channelGain.Volume = (float)gain.Value;
                gainRow.Children.Add(gain);
                root.Children.Add(gainRow);

                host.Child = root;
            }

            private Button MakeButton(string text, double width) => new Button
            {
                Content = text,
                Width = width,
                Height = 30,
                Background = PanelBtn,
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                FontWeight = FontWeights.SemiBold
            };

            private void FillLoadPicker()
            {
                var names = _window._owner.ZipSounds.Keys.OrderBy(k => k).ToList();
                var current = _loadPicker.SelectedItem as string;
                _loadPicker.ItemsSource = names;
                if (current != null && names.Contains(current)) _loadPicker.SelectedItem = current;
                if (_loadPicker.SelectedIndex < 0 && names.Count > 0) _loadPicker.SelectedIndex = 0;
            }

            private void LoadFromPicker()
            {
                if (_loadPicker.SelectedItem is not string name) { _window.SetStatus("The library is empty — import a song in the main window first, or use 📁 File…"); return; }
                if (!_window._owner.ZipSounds.TryGetValue(name, out var bytes)) return;
                LoadBytes(bytes, name);
            }

            private void LoadFromFile()
            {
                var dialog = new OpenFileDialog { Title = $"Load a song on {_name}", Filter = "Audio|*.mp3;*.wav" };
                if (dialog.ShowDialog() != true) return;
                try { LoadBytes(File.ReadAllBytes(dialog.FileName), dialog.FileName); }
                catch (Exception ex) { MessageBox.Show("Could not load that file: " + ex.Message, _name); }
            }

            private void LoadBytes(byte[] bytes, string name)
            {
                try
                {
                    _deck.Load(bytes, name);
                    _tempoSlider.Value = 0;
                    _bpmBox.Text = "";
                    _title.Text = $"{_name} — {_deck.Title}";
                    RenderWaveform();
                    _window.SetStatus($"{_name}: {_deck.Title} loaded ({FmtTime(_deck.TotalSeconds)}). Set the BPM to unlock SYNC and loops.");
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Could not decode that song: " + ex.Message, _name);
                }
            }

            private void RenderWaveform()
            {
                var peaks = _deck.ComputePeaks(920);
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    var pen = new Pen(_accent, 1);
                    for (int x = 0; x < peaks.Length; x++)
                    {
                        double h = Math.Max(1, peaks[x] * 70);
                        dc.DrawLine(pen, new Point(x, 37 - h / 2), new Point(x, 37 + h / 2));
                    }
                }
                var bmp = new RenderTargetBitmap(920, 74, 96, 96, PixelFormats.Pbgra32);
                bmp.Render(visual);
                _waveImage.Source = bmp;
            }

            private void Wave_Mouse(object sender, MouseEventArgs e)
            {
                if (e.LeftButton != MouseButtonState.Pressed || !_deck.IsLoaded) return;
                double w = _waveCanvas.ActualWidth;
                if (w <= 0) return;
                double frac = Math.Clamp(e.GetPosition(_waveCanvas).X / w, 0, 1);
                _deck.PositionSeconds = frac * _deck.TotalSeconds;
            }

            private void PlayPause()
            {
                if (!_deck.IsLoaded) return;
                if (!_deck.IsPlaying && _deck.PositionSeconds >= _deck.TotalSeconds - 0.05)
                    _deck.PositionSeconds = 0;
                _deck.IsPlaying = !_deck.IsPlaying;
            }

            private void Cue()
            {
                if (!_deck.IsLoaded) return;
                if (_deck.IsPlaying)
                {
                    _deck.PositionSeconds = _deck.CueSeconds;
                    _deck.IsPlaying = false;
                }
                else
                {
                    _deck.CueSeconds = _deck.PositionSeconds;
                    _window.SetStatus($"{_name}: cue point set at {FmtTime(_deck.CueSeconds)}.");
                }
            }

            private void SyncToOther()
            {
                var otherDeck = _other();
                if (_deck.Bpm <= 0 || otherDeck.EffectiveBpm <= 0)
                {
                    _window.SetStatus("SYNC needs a BPM on both decks — type it or TAP it in.");
                    return;
                }
                double needed = otherDeck.EffectiveBpm / _deck.Bpm;
                needed = Math.Clamp(needed, 0.92, 1.08);
                _tempoSlider.Value = (needed - 1) * 100;
                _window.SetStatus($"{_name}: synced to {otherDeck.EffectiveBpm:0.0} BPM.");
            }

            private void SetTempo(double percent)
            {
                _deck.Tempo = 1 + percent / 100.0;
                _tempoLabel.Text = $"{percent:+0.0;-0.0;0.0}%";
            }

            private void ApplyBpmBox()
            {
                if (double.TryParse(_bpmBox.Text, out double bpm) && bpm >= 40 && bpm <= 240)
                {
                    _deck.Bpm = bpm;
                    _bpmBox.Text = $"{bpm:0}";
                }
                else if (string.IsNullOrWhiteSpace(_bpmBox.Text))
                {
                    _deck.Bpm = 0;
                }
            }

            private void TapTempo()
            {
                var now = DateTime.Now;
                _taps.RemoveAll(t => (now - t).TotalSeconds > 2.5);
                _taps.Add(now);
                if (_taps.Count >= 3)
                {
                    double avg = (_taps[^1] - _taps[0]).TotalSeconds / (_taps.Count - 1);
                    if (avg > 0)
                    {
                        double bpm = Math.Round(60.0 / avg * 2) / 2;
                        _deck.Bpm = bpm;
                        _bpmBox.Text = $"{bpm:0}";
                    }
                }
            }

            private void HotCue(int idx)
            {
                if (!_deck.IsLoaded) return;
                if (_deck.HotCues[idx] is double pos)
                {
                    _deck.PositionSeconds = pos;
                }
                else
                {
                    _deck.HotCues[idx] = _deck.PositionSeconds;
                    _window.SetStatus($"{_name}: hot cue {idx + 1} set at {FmtTime(_deck.PositionSeconds)}.");
                }
            }

            private void SetLoop(int beats)
            {
                if (!_deck.IsLoaded) return;
                if (beats == 0) { _deck.StopLoop(); return; }
                if (_deck.Bpm <= 0)
                {
                    _window.SetStatus($"{_name}: loops need the BPM first — type it or TAP it in.");
                    return;
                }
                _deck.BeginLoop(beats);
                _window.SetStatus($"{_name}: looping {beats} beats from here.");
            }

            public void Refresh()
            {
                if (_deck.IsLoaded)
                {
                    _timeText.Text = $"{FmtTime(_deck.PositionSeconds)} / {FmtTime(_deck.TotalSeconds)}";
                    double w = _waveCanvas.ActualWidth;
                    if (w > 0)
                    {
                        double x = _deck.TotalSeconds > 0 ? _deck.PositionSeconds / _deck.TotalSeconds * w : 0;
                        _playheadLine.X1 = x;
                        _playheadLine.X2 = x;
                    }
                }
                _playButton.Content = _deck.IsPlaying ? "⏸ PAUSE" : "▶ PLAY";
                _effBpmText.Text = _deck.EffectiveBpm > 0 ? $"→ {_deck.EffectiveBpm:0.0} BPM" : "";
                for (int i = 0; i < 4; i++)
                    _hotButtons[i].Background = _deck.HotCues[i] != null ? (Brush)_accent : PanelBtn;
                int[] beats = { 4, 8, 16, 0 };
                for (int i = 0; i < 4; i++)
                    _loopButtons[i].Background = _deck.LoopBeats == beats[i] && (beats[i] != 0 || _deck.LoopBeats == 0) ? (Brush)_accent : PanelBtn;
            }
        }
    }
}
