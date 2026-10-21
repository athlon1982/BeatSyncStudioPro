using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
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
                new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54)), DeckHostA, chainA.Channel, chainA.Tap);
            _panelB = new DeckPanel(this, _deckB, () => _deckA, _fxB, "DECK B",
                new SolidColorBrush(Color.FromRgb(0xB3, 0x88, 0xFF)), DeckHostB, chainB.Channel, chainB.Tap);

            XfadeSlider.ValueChanged += (s, e) => UpdateCrossfader();
            CurvePicker.SelectionChanged += (s, e) => UpdateCrossfader();
            DjMasterSlider.ValueChanged += (s, e) => _masterGain.Volume = (float)DjMasterSlider.Value;
            UpdateCrossfader();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _timer.Tick += (s, e) => { _panelA.Refresh(); _panelB.Refresh(); };
            _timer.Start();

            // Never open larger than the screen's work area (scaled displays
            // were clipping the window on the right and bottom)
            Loaded += (s, e) =>
            {
                var wa = SystemParameters.WorkArea;
                MaxWidth = wa.Width;
                MaxHeight = wa.Height;
                if (Width > wa.Width) Width = wa.Width;
                if (Height > wa.Height) Height = wa.Height;
                Left = wa.Left + Math.Max(0, (wa.Width - ActualWidth) / 2);
                Top = wa.Top + Math.Max(0, (wa.Height - ActualHeight) / 2);
            };
            Closed += (s, e) => { _timer.Stop(); _recorder.Stop(); };
        }

        private (VolumeSampleProvider Xfade, VolumeSampleProvider Channel, AudioTapSampleProvider Tap) BuildDeckChain(
            DjDeck deck, TrackFxSettings fx, MixingSampleProvider master)
        {
            var tap = new AudioTapSampleProvider(deck);
            var eq = new EqSampleProvider(tap, fx);
            var delay = new DelaySampleProvider(eq, fx);
            var reverb = new ReverbSampleProvider(delay, fx);
            var channel = new VolumeSampleProvider(reverb) { Volume = 1.0f };
            var xfade = new VolumeSampleProvider(channel) { Volume = 0.707f };
            master.AddMixerInput(xfade);
            return (xfade, channel, tap);
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
            private readonly RotateTransform _platterSpin = new RotateTransform();

            // waveform ring + scratch state
            private const int RingBars = 120;
            private readonly Line[] _ringLines = new Line[RingBars];
            private readonly float[] _ringPeaks = new float[RingBars];
            private readonly double[] _ringLens = new double[RingBars];
            private Brush? _ringDim;
            private DropShadowEffect? _haloFx;
            private Button? _scratchBtn;
            private bool _scratchOn;
            private bool _dragging;
            private double _lastAngle;
            private long _lastDragMs;
            private readonly List<(long Ms, double HandPos)> _dragTrail = new List<(long, double)>();
            private double _handPos;
            private readonly AudioTapSampleProvider _tap;
            private ComboBox _visualPicker = null!;
            private Border _visualBorder = null!;
            private Image _visualImage = null!;
            private ComboBox _platterPicker = null!;
            private ComboBox _colorPicker = null!;
            private Grid? _vinylView;
            private Grid? _cassetteView;
            private RotateTransform? _reelSpinL, _reelSpinR;
            private Ellipse? _tapeL, _tapeR;
            private SolidColorBrush? _visualBrush, _visualBrush2;
            private double _hueShift;

            private bool IsDeckB => _name.EndsWith("B");

            /// <summary>Color themes for the deck visuals (null primary = the deck's own accent).</summary>
            private static readonly (string Name, Color? Primary, Color? Secondary, double Hue)[] VisualPalettes =
            {
                ("Deck Color", null, null, 0),
                ("👑 Gold", Color.FromRgb(0xF5, 0xC5, 0x42), Color.FromRgb(0xFF, 0x8A, 0x3D), 40),
                ("🟢 Green", Color.FromRgb(0x1D, 0xB9, 0x54), Color.FromRgb(0x8A, 0xFF, 0x5A), 130),
                ("🟣 Purple", Color.FromRgb(0xB3, 0x88, 0xFF), Color.FromRgb(0xFF, 0x6E, 0xC7), 265),
                ("🔵 Ice", Color.FromRgb(0x4F, 0xC3, 0xF7), Color.FromRgb(0xB3, 0xE5, 0xFC), 200),
                ("🟠 Sunset", Color.FromRgb(0xFF, 0x70, 0x43), Color.FromRgb(0xFF, 0xC8, 0x3D), 20),
                ("🩷 Pink", Color.FromRgb(0xFF, 0x6E, 0xC7), Color.FromRgb(0xF5, 0xC5, 0x42), 320),
                ("⚪ White", Color.FromRgb(0xF5, 0xF5, 0xF5), Color.FromRgb(0x9E, 0x9E, 0x9E), 0),
            };

            private void ApplyPalette(string name)
            {
                foreach (var p in VisualPalettes)
                {
                    if (p.Name != name) continue;
                    _visualBrush = p.Primary.HasValue ? new SolidColorBrush(p.Primary.Value) : null;
                    _visualBrush2 = p.Secondary.HasValue ? new SolidColorBrush(p.Secondary.Value) : null;
                    _hueShift = p.Hue;
                    return;
                }
                _visualBrush = null; _visualBrush2 = null; _hueShift = 0;
            }
            private float _specPeak = 0.2f;
            private readonly float[] _peakCaps = new float[48];
            private readonly float[] _dotPos = new float[48];
            private readonly float[] _dotVel = new float[48];
            private readonly List<float[]> _waterHistory = new List<float[]>();
            private readonly List<string> _queuePaths = new List<string>();
            private int _queueIndex = -1;
            private ListBox _queueList = null!;
            private TextBlock _queueCount = null!;
            private CheckBox _autoNextBox = null!;
            private bool _wasPlayingLastTick;

            private static readonly Brush PanelBtn = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x3C));
            private static readonly Brush MutedText = new SolidColorBrush(Color.FromRgb(0xA0, 0xA5, 0xB5));

            public DeckPanel(DjWindow window, DjDeck deck, Func<DjDeck> other, TrackFxSettings fx, string name,
                SolidColorBrush accent, Border host, VolumeSampleProvider channelGain, AudioTapSampleProvider tap)
            {
                _window = window;
                _deck = deck;
                _other = other;
                _fx = fx;
                _name = name;
                _accent = accent;
                _channelGain = channelGain;
                _tap = tap;

                var root = new StackPanel();

                _title = new TextBlock { Text = $"{name} — empty", FontSize = 14, FontWeight = FontWeights.Bold, Foreground = accent, Margin = new Thickness(0, 0, 0, 6) };
                root.Children.Add(_title);

                // The platter is built at a fixed 198px — a Viewbox scales it
                // down so the whole deck fits smaller / scaled-up screens.
                root.Children.Add(new Viewbox
                {
                    Child = BuildPlatter(),
                    Height = 168,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center
                });

                _scratchBtn = MakeButton("✋ Scratch: OFF", 170);
                _scratchBtn.HorizontalAlignment = HorizontalAlignment.Center;
                _scratchBtn.Margin = new Thickness(0, 0, 0, 4);
                _scratchBtn.ToolTip = "Scratch mode: grab the vinyl and move it with the mouse. Off by default so the record can't be grabbed by accident.";
                _scratchBtn.Click += (s, e) => SetScratch(!_scratchOn);
                root.Children.Add(_scratchBtn);

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

                // Live visual for THIS deck's own sound: spectrum / waveform / off
                var visRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
                visRow.Children.Add(new TextBlock { Text = "Visual", Foreground = MutedText, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Width = 42 });
                _visualPicker = new ComboBox { Width = 150, VerticalAlignment = VerticalAlignment.Center };
                _visualPicker.Items.Add(new ComboBoxItem { Content = "📊 Spectrum" });
                _visualPicker.Items.Add(new ComboBoxItem { Content = "🌈 Rainbow Spectrum" });
                _visualPicker.Items.Add(new ComboBoxItem { Content = "📈 Spectrum + Peaks" });
                _visualPicker.Items.Add(new ComboBoxItem { Content = "💜 Mirrored Spectrum" });
                _visualPicker.Items.Add(new ComboBoxItem { Content = "⭕ Radial Spectrum" });
                _visualPicker.Items.Add(new ComboBoxItem { Content = "🎚️ VU Bars" });
                _visualPicker.Items.Add(new ComboBoxItem { Content = "🌋 Waterfall" });
                _visualPicker.Items.Add(new ComboBoxItem { Content = "🌊 Wave Fill" });
                _visualPicker.Items.Add(new ComboBoxItem { Content = "〰️ Waveform" });
                _visualPicker.Items.Add(new ComboBoxItem { Content = "💡 Level + Bass" });
                _visualPicker.Items.Add(new ComboBoxItem { Content = "✨ Falling Dots" });
                _visualPicker.Items.Add(new ComboBoxItem { Content = "Off" });
                _visualPicker.SelectedIndex = 0;
                _visualPicker.SelectionChanged += (s, e) =>
                {
                    if (_visualBorder != null)
                        _visualBorder.Visibility = _visualPicker.SelectedIndex == _visualPicker.Items.Count - 1 ? Visibility.Collapsed : Visibility.Visible;
                };
                visRow.Children.Add(_visualPicker);
                visRow.Children.Add(new TextBlock { Text = "(this deck's own sound)", Foreground = MutedText, FontSize = 9, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
                root.Children.Add(visRow);

                // Style row: platter (vinyl / cassette) + visual colors
                var styleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
                styleRow.Children.Add(new TextBlock { Text = "Style", Foreground = MutedText, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Width = 42 });
                _platterPicker = new ComboBox { Width = 110, VerticalAlignment = VerticalAlignment.Center };
                _platterPicker.Items.Add(new ComboBoxItem { Content = "🎵 Vinyl" });
                _platterPicker.Items.Add(new ComboBoxItem { Content = "📼 Cassette" });
                string savedPlatter = IsDeckB ? DjSettings.Current.PlatterB : DjSettings.Current.PlatterA;
                _platterPicker.SelectedIndex = savedPlatter == "Cassette" ? 1 : 0;
                _platterPicker.SelectionChanged += (s, e) =>
                {
                    bool cassette = _platterPicker.SelectedIndex == 1;
                    SetPlatterStyle(cassette);
                    if (IsDeckB) DjSettings.Current.PlatterB = cassette ? "Cassette" : "Vinyl";
                    else DjSettings.Current.PlatterA = cassette ? "Cassette" : "Vinyl";
                    DjSettings.Current.Save();
                };
                styleRow.Children.Add(_platterPicker);
                styleRow.Children.Add(new TextBlock { Text = "Colors", Foreground = MutedText, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0) });
                _colorPicker = new ComboBox { Width = 130, VerticalAlignment = VerticalAlignment.Center };
                string savedColor = IsDeckB ? DjSettings.Current.ColorB : DjSettings.Current.ColorA;
                int colorIdx = 0;
                for (int i = 0; i < VisualPalettes.Length; i++)
                {
                    _colorPicker.Items.Add(new ComboBoxItem { Content = VisualPalettes[i].Name });
                    if (VisualPalettes[i].Name == savedColor) colorIdx = i;
                }
                _colorPicker.SelectedIndex = colorIdx;
                ApplyPalette(savedColor);
                _colorPicker.SelectionChanged += (s, e) =>
                {
                    if (_colorPicker.SelectedItem is ComboBoxItem item)
                    {
                        string name = item.Content?.ToString() ?? "Deck Color";
                        ApplyPalette(name);
                        if (IsDeckB) DjSettings.Current.ColorB = name; else DjSettings.Current.ColorA = name;
                        DjSettings.Current.Save();
                    }
                };
                styleRow.Children.Add(_colorPicker);
                root.Children.Add(styleRow);
                _visualBorder = new Border { Height = 54, Margin = new Thickness(0, 4, 0, 0), Background = new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x1E)), CornerRadius = new CornerRadius(4) };
                _visualImage = new Image { Stretch = Stretch.Fill };
                _visualBorder.Child = _visualImage;
                root.Children.Add(_visualBorder);

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

                // Track list for this deck: queue songs, step through them, mix on
                var qHead = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
                qHead.Children.Add(new TextBlock { Text = "🎧 TRACK LIST", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = (Brush)_accent });
                _queueCount = new TextBlock { Text = "", Foreground = MutedText, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
                qHead.Children.Add(_queueCount);
                root.Children.Add(qHead);
                _queueList = new ListBox
                {
                    Height = 60,
                    Margin = new Thickness(0, 4, 0, 0),
                    Background = new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x1E)),
                    Foreground = Brushes.White,
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33))
                };
                _queueList.MouseDoubleClick += (s, e) =>
                {
                    if (_queueList.SelectedIndex >= 0) LoadQueueItem(_queueList.SelectedIndex, true);
                };
                root.Children.Add(_queueList);
                var qBtns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
                Button QBtn(string label)
                {
                    return new Button
                    {
                        Content = label,
                        Padding = new Thickness(10, 4, 10, 4),
                        Margin = new Thickness(0, 0, 6, 0),
                        Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x3C)),
                        Foreground = Brushes.White,
                        BorderThickness = new Thickness(0),
                        Cursor = Cursors.Hand,
                        FontSize = 11
                    };
                }
                var addBtn = QBtn("➕ Add Songs…");
                addBtn.Click += (s, e) => AddToQueue();
                var prevBtn = QBtn("⏮ Prev");
                prevBtn.Click += (s, e) => { if (_queueIndex > 0) LoadQueueItem(_queueIndex - 1, true); };
                var nextBtn = QBtn("⏭ Next");
                nextBtn.Click += (s, e) => { if (_queueIndex + 1 < _queuePaths.Count) LoadQueueItem(_queueIndex + 1, true); };
                var removeBtn = QBtn("🗑 Remove");
                removeBtn.Click += (s, e) => RemoveFromQueue();
                qBtns.Children.Add(addBtn);
                qBtns.Children.Add(prevBtn);
                qBtns.Children.Add(nextBtn);
                qBtns.Children.Add(removeBtn);
                root.Children.Add(qBtns);
                _autoNextBox = new CheckBox
                {
                    Content = "Auto-play the next track when this song ends",
                    Foreground = MutedText,
                    FontSize = 11,
                    Margin = new Thickness(0, 6, 0, 0)
                };
                root.Children.Add(_autoNextBox);

                host.Child = root;
            }

            /// <summary>
            /// The turntable: a neon-ringed vinyl platter in the deck's accent
            /// color (the splash-screen look) that spins at 33⅓ RPM while the
            /// deck plays, with a label, groove rings, a marker dot, a spindle
            /// and a decorative tonearm. Clicking the vinyl plays/pauses.
            /// </summary>
            private Grid BuildPlatter()
            {
                var host = new Grid
                {
                    Height = 198,
                    Margin = new Thickness(0, 2, 0, 6),
                    Cursor = Cursors.Hand,
                    Background = Brushes.Transparent,
                    ToolTip = "Click the vinyl to play / pause"
                };
                host.MouseLeftButtonDown += Platter_MouseDown;
                host.MouseMove += Platter_MouseMove;
                host.MouseLeftButtonUp += Platter_MouseUp;

                var halo = new Ellipse
                {
                    Width = 190, Height = 190,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    Fill = new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0C)),
                    Stroke = _accent, StrokeThickness = 3,
                    Effect = _haloFx = new DropShadowEffect { Color = _accent.Color, BlurRadius = 26, ShadowDepth = 0, Opacity = 0.85 }
                };
                host.Children.Add(halo);

                _vinylView = new Grid();
                var vinyl = new Grid { Width = 180, Height = 180, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                _platterSpin.CenterX = 90;
                _platterSpin.CenterY = 90;
                vinyl.RenderTransform = _platterSpin;

                var grad = new RadialGradientBrush
                {
                    Center = new Point(0.38, 0.38), GradientOrigin = new Point(0.38, 0.38), RadiusX = 0.75, RadiusY = 0.75
                };
                grad.GradientStops.Add(new GradientStop(Color.FromRgb(0x3A, 0x3A, 0x42), 0));
                grad.GradientStops.Add(new GradientStop(Color.FromRgb(0x14, 0x14, 0x18), 0.55));
                grad.GradientStops.Add(new GradientStop(Color.FromRgb(0x05, 0x05, 0x07), 1));
                vinyl.Children.Add(new Ellipse
                {
                    Width = 180, Height = 180,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    Fill = grad
                });
                foreach (double d in new[] { 150.0, 128.0, 106.0 })
                {
                    vinyl.Children.Add(new Ellipse
                    {
                        Width = d, Height = d,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                        Stroke = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)), StrokeThickness = 5
                    });
                }
                vinyl.Children.Add(new Ellipse
                {
                    Width = 62, Height = 62,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    Fill = _accent, Opacity = 0.92
                });
                vinyl.Children.Add(new TextBlock
                {
                    Text = _name.Length > 0 ? _name.Substring(_name.Length - 1) : "",
                    FontSize = 22, FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x0B, 0x0E, 0x14)),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                });
                vinyl.Children.Add(new Ellipse
                {
                    Width = 10, Height = 10, Fill = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 8, 0, 0)
                });
                _vinylView.Children.Add(vinyl);

                // waveform ring: the song's peaks as bars inside the neon rim;
                // played bars light up, the playhead bar pulses with the beat
                _ringDim = new SolidColorBrush(Color.FromArgb(0x4D, _accent.Color.R, _accent.Color.G, _accent.Color.B));
                var ring = new Canvas
                {
                    Width = 196, Height = 196, IsHitTestVisible = false,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                };
                for (int i = 0; i < RingBars; i++)
                {
                    var line = new Line
                    {
                        Stroke = _ringDim, StrokeThickness = 2.4,
                        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
                    };
                    PositionRingLine(line, i * 2 * Math.PI / RingBars - Math.PI / 2, 3);
                    _ringLines[i] = line;
                    _ringLens[i] = 3;
                    ring.Children.Add(line);
                }
                _vinylView.Children.Add(ring);

                _vinylView.Children.Add(new Ellipse
                {
                    Width = 12, Height = 12,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    Fill = new SolidColorBrush(Color.FromRgb(0xC0, 0xC6, 0xD0)),
                    Stroke = new SolidColorBrush(Color.FromRgb(0x60, 0x66, 0x72)), StrokeThickness = 1
                });

                // tonearm: pivot disc top-right, arm angled in toward the record
                _vinylView.Children.Add(new Ellipse
                {
                    Width = 30, Height = 30,
                    Fill = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x20)),
                    Stroke = _accent, StrokeThickness = 1.5,
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 2, 6, 0)
                });
                _vinylView.Children.Add(new Border
                {
                    Width = 9, Height = 92, CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(Color.FromRgb(0x8A, 0x92, 0xA0)),
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 12, 16, 0),
                    RenderTransform = new RotateTransform(24, 4.5, 6)
                });
                host.Children.Add(_vinylView);

                _cassetteView = BuildCassetteView();
                host.Children.Add(_cassetteView);
                string saved = IsDeckB ? DjSettings.Current.PlatterB : DjSettings.Current.PlatterA;
                SetPlatterStyle(saved == "Cassette");
                return host;
            }

            private void SetPlatterStyle(bool cassette)
            {
                if (_vinylView != null) _vinylView.Visibility = cassette ? Visibility.Collapsed : Visibility.Visible;
                if (_cassetteView != null) _cassetteView.Visibility = cassette ? Visibility.Visible : Visibility.Collapsed;
            }

            /// <summary>
            /// The cassette alternative to the vinyl platter: a tape body with a
            /// window, two reels that spin with playback, and tape that visibly
            /// moves from the left reel to the right as the song plays.
            /// </summary>
            private Grid BuildCassetteView()
            {
                var wrap = new Grid { Visibility = Visibility.Collapsed };
                var canvas = new Canvas { Width = 196, Height = 198, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                wrap.Children.Add(canvas);

                var body = new Border
                {
                    Width = 188, Height = 122, CornerRadius = new CornerRadius(10),
                    Background = new SolidColorBrush(Color.FromRgb(0x17, 0x1A, 0x21)),
                    BorderBrush = _accent, BorderThickness = new Thickness(2)
                };
                Canvas.SetLeft(body, 4); Canvas.SetTop(body, 38);
                canvas.Children.Add(body);

                var label = new Border
                {
                    Width = 172, Height = 30, CornerRadius = new CornerRadius(5),
                    Background = new SolidColorBrush(Color.FromArgb(0x3D, _accent.Color.R, _accent.Color.G, _accent.Color.B))
                };
                Canvas.SetLeft(label, 12); Canvas.SetTop(label, 46);
                canvas.Children.Add(label);
                var labelText = new TextBlock
                {
                    Text = $"📼 DECK {_name.Substring(_name.Length - 1)} · CHROME 90",
                    FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
                    Width = 172, TextAlignment = TextAlignment.Center
                };
                Canvas.SetLeft(labelText, 12); Canvas.SetTop(labelText, 53);
                canvas.Children.Add(labelText);

                var window = new Border
                {
                    Width = 150, Height = 54, CornerRadius = new CornerRadius(6),
                    Background = new SolidColorBrush(Color.FromRgb(0x05, 0x07, 0x0A)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x42, 0x52)), BorderThickness = new Thickness(1)
                };
                Canvas.SetLeft(window, 23); Canvas.SetTop(window, 84);
                canvas.Children.Add(window);

                Grid MakeReel(double left, bool isLeft)
                {
                    var reel = new Grid { Width = 46, Height = 46 };
                    var tape = new Ellipse
                    {
                        Width = isLeft ? 42 : 26, Height = isLeft ? 42 : 26,
                        Fill = new SolidColorBrush(Color.FromRgb(0x2E, 0x20, 0x16)),
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                    };
                    if (isLeft) _tapeL = tape; else _tapeR = tape;
                    reel.Children.Add(tape);
                    var spokes = new Grid { Width = 46, Height = 46 };
                    for (int s = 0; s < 3; s++)
                    {
                        var spoke = new Border
                        {
                            Width = 40, Height = 5, CornerRadius = new CornerRadius(2),
                            Background = new SolidColorBrush(Color.FromRgb(0xC0, 0xC6, 0xD0)),
                            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                            RenderTransform = new RotateTransform(s * 60, 20, 2.5)
                        };
                        spokes.Children.Add(spoke);
                    }
                    spokes.Children.Add(new Ellipse
                    {
                        Width = 10, Height = 10, Fill = new SolidColorBrush(Color.FromRgb(0x8A, 0x92, 0xA0)),
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                    });
                    var spin = new RotateTransform();
                    spin.CenterX = 23; spin.CenterY = 23;
                    spokes.RenderTransform = spin;
                    if (isLeft) _reelSpinL = spin; else _reelSpinR = spin;
                    reel.Children.Add(spokes);
                    Canvas.SetLeft(reel, left); Canvas.SetTop(reel, 88);
                    canvas.Children.Add(reel);
                    return reel;
                }
                MakeReel(42, true);
                MakeReel(108, false);

                // tape path line between the reels
                var path = new Border { Width = 66, Height = 3, Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x20, 0x16)) };
                Canvas.SetLeft(path, 65); Canvas.SetTop(path, 132);
                canvas.Children.Add(path);

                // head opening + screws along the bottom edge
                var headSlot = new Border { Width = 62, Height = 12, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0D, 0x11)) };
                Canvas.SetLeft(headSlot, 67); Canvas.SetTop(headSlot, 146);
                canvas.Children.Add(headSlot);
                foreach (double sx in new[] { 12.0, 52.0, 138.0, 178.0 })
                {
                    var screw = new Ellipse { Width = 6, Height = 6, Fill = new SolidColorBrush(Color.FromRgb(0x8A, 0x92, 0xA0)) };
                    Canvas.SetLeft(screw, sx); Canvas.SetTop(screw, 148);
                    canvas.Children.Add(screw);
                }
                return wrap;
            }

            private static void PositionRingLine(Line line, double angle, double len)
            {
                double cx = 98, cy = 98, outer = 93;
                double dx = Math.Cos(angle), dy = Math.Sin(angle);
                line.X1 = cx + dx * (outer - len);
                line.Y1 = cy + dy * (outer - len);
                line.X2 = cx + dx * outer;
                line.Y2 = cy + dy * outer;
            }

            private void RenderPlatterRing()
            {
                var peaks = _deck.ComputePeaks(RingBars);
                for (int i = 0; i < RingBars; i++) _ringPeaks[i] = peaks[i];
            }

            private void SetScratch(bool on)
            {
                _scratchOn = on;
                if (_scratchBtn != null)
                {
                    _scratchBtn.Content = on ? "✋ Scratch: ON" : "✋ Scratch: OFF";
                    _scratchBtn.Background = on ? (Brush)_accent : PanelBtn;
                }
                if (!on && _dragging)
                {
                    _dragging = false;
                    _deck.ReleaseRecord();
                }
            }

            private double PointerAngle(MouseEventArgs e, Grid host)
            {
                var p = e.GetPosition(host);
                return Math.Atan2(p.Y - host.ActualHeight / 2, p.X - host.ActualWidth / 2);
            }

            private void Platter_MouseDown(object sender, MouseButtonEventArgs e)
            {
                if (!_scratchOn) { PlayPause(); return; }
                if (!_deck.IsLoaded) return;
                var host = (Grid)sender;
                _dragging = true;
                host.CaptureMouse();
                _lastAngle = PointerAngle(e, host);
                _lastDragMs = Environment.TickCount64;
                _handPos = _deck.PositionSeconds;
                _dragTrail.Clear();
                _dragTrail.Add((Environment.TickCount64, _handPos));
                _deck.GrabRecord();
            }

            private void Platter_MouseMove(object sender, MouseEventArgs e)
            {
                if (!_dragging) return;
                var host = (Grid)sender;
                double angle = PointerAngle(e, host);
                double delta = angle - _lastAngle;
                while (delta > Math.PI) delta -= 2 * Math.PI;
                while (delta < -Math.PI) delta += 2 * Math.PI;
                _lastAngle = angle;
                long now = Environment.TickCount64;
                _lastDragMs = now;
                double seconds = delta / (2 * Math.PI) * 1.8; // one revolution = 1.8 s of audio
                _handPos = Math.Clamp(_handPos + seconds, 0, Math.Max(0, _deck.TotalSeconds - 0.05));
                // Hand speed measured over a trailing ~100 ms window — a steady
                // drag reads as its true speed (slow drag = slow-motion sound),
                // not as a string of near-zero instants the deck mutes.
                _dragTrail.Add((now, _handPos));
                while (_dragTrail.Count > 2 && now - _dragTrail[0].Ms > 100) _dragTrail.RemoveAt(0);
                var first = _dragTrail[0];
                double span = (now - first.Ms) / 1000.0;
                if (span > 0.02)
                    _deck.ScratchRate = Math.Clamp((_handPos - first.HandPos) / span, -10, 10);
            }

            private void Platter_MouseUp(object sender, MouseButtonEventArgs e)
            {
                if (!_dragging) return;
                _dragging = false;
                ((Grid)sender).ReleaseMouseCapture();
                if (Environment.TickCount64 - _lastDragMs > 80) _deck.ScratchRate = 0;
                _deck.ReleaseRecord();
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

            /// <summary>Auto-gained spectrum bands (0..1) — raw FFT values run small.</summary>
            private double[] ScaledBands()
            {
                var bands = _tap.GetSpectrum(48);
                var scaled = new double[48];
                float frameMax = 0;
                for (int i = 0; i < 48; i++) frameMax = Math.Max(frameMax, bands[i]);
                _specPeak = Math.Max(Math.Max(frameMax, _specPeak * 0.994f), 0.12f);
                for (int i = 0; i < 48; i++)
                    scaled[i] = Math.Clamp(Math.Sqrt(bands[i] / _specPeak) * 0.95, 0, 1);
                return scaled;
            }

            private static void DrawMeter(DrawingContext dc, double y, float value, Brush fill, int W)
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x24, 0x29, 0x36)), null, new Rect(0, y, W, 16));
                double w = Math.Clamp(value, 0f, 1f) * W;
                if (w > 1) dc.DrawRectangle(fill, null, new Rect(0, y, w, 16));
            }

            private void DrawDeckVisual(DrawingContext dc, int mode, int W, int H)
            {
                SolidColorBrush viz = _visualBrush ?? _accent;
                SolidColorBrush viz2 = _visualBrush2 ?? new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x3D));
                // Wave-based modes draw from the scope trace (flat line when idle)
                if (mode == 7 || mode == 8)
                {
                    var wave = _tap.GetWaveform(220);
                    if (mode == 8)
                    {
                        var pen = new Pen(viz, 1.2);
                        Point prev = new Point(0, H / 2.0 - wave[0] * (H / 2.0 - 2));
                        for (int p = 1; p < wave.Length; p++)
                        {
                            var pt = new Point(p * 2, H / 2.0 - wave[p] * (H / 2.0 - 2));
                            dc.DrawLine(pen, prev, pt);
                            prev = pt;
                        }
                    }
                    else
                    {
                        var geo = new StreamGeometry();
                        using (var ctx = geo.Open())
                        {
                            ctx.BeginFigure(new Point(0, H), true, true);
                            for (int p = 0; p < wave.Length; p++)
                                ctx.LineTo(new Point(p * 2, H / 2.0 - wave[p] * (H / 2.0 - 2)), true, false);
                            ctx.LineTo(new Point(W, H), true, false);
                        }
                        geo.Freeze();
                        var fill = new SolidColorBrush(viz.Color) { Opacity = 0.45 };
                        dc.DrawGeometry(fill, new Pen(viz, 1.2), geo);
                    }
                    return;
                }

                if (!_deck.IsLoaded)
                {
                    if (mode == 9)
                    {
                        DrawMeter(dc, 6, 0, (Brush)viz, W);
                        DrawMeter(dc, 30, 0, viz2, W);
                    }
                    else
                    {
                        var dim = new SolidColorBrush(viz.Color) { Opacity = 0.45 };
                        dc.DrawRectangle(dim, null, new Rect(0, H - 4, W, 2));
                    }
                    return;
                }

                if (mode == 9)
                {
                    DrawMeter(dc, 6, _tap.Level, (Brush)viz, W);
                    DrawMeter(dc, 30, _tap.BassLevel, viz2, W);
                    return;
                }

                var v = ScaledBands();
                double bw = W / 48.0;

                switch (mode)
                {
                    case 0: // Spectrum bars in the deck color
                        for (int i = 0; i < 48; i++)
                        {
                            double h = Math.Max(1, v[i] * (H - 4));
                            dc.DrawRectangle((Brush)viz, null, new Rect(i * bw + 1, H - h, bw - 2, h));
                        }
                        break;

                    case 1: // Rainbow LED blocks — hue sweeps red→orange across the bands
                    {
                        const int segH = 4, segGap = 2, rows = 8;
                        for (int i = 0; i < 48; i++)
                        {
                            int lit = (int)Math.Round(v[i] * rows);
                            double hue = (360.0 - (i / 47.0) * 325.0 + _hueShift + 360.0) % 360.0;
                            var brush = new SolidColorBrush(HslToRgb(hue, 1.0, 0.55));
                            for (int r = 0; r < lit; r++)
                            {
                                double y = H - 2 - (r + 1) * segH - r * segGap;
                                dc.DrawRectangle(brush, null, new Rect(i * bw + 1, y, bw - 2, segH));
                            }
                        }
                        break;
                    }

                    case 2: // Spectrum + falling white peak caps
                        for (int i = 0; i < 48; i++)
                        {
                            double h = Math.Max(1, v[i] * (H - 6));
                            dc.DrawRectangle((Brush)viz, null, new Rect(i * bw + 1, H - h, bw - 2, h));
                            _peakCaps[i] = Math.Max((float)v[i], _peakCaps[i] - 0.008f);
                            double capY = H - 2 - _peakCaps[i] * (H - 6);
                            dc.DrawRectangle(Brushes.White, null, new Rect(i * bw + 1, capY, bw - 2, 2));
                        }
                        break;

                    case 3: // Mirrored spectrum — bars grow both ways from the center line
                        for (int i = 0; i < 48; i++)
                        {
                            double half = v[i] * (H / 2.0 - 2);
                            dc.DrawRectangle((Brush)viz, null, new Rect(i * bw + 1, H / 2.0 - half, bw - 2, half * 2));
                        }
                        break;

                    case 4: // Radial spectrum — bars radiating from a center circle
                    {
                        var center = new Point(W / 2.0, H / 2.0);
                        var pen = new Pen(viz, 2.5);
                        dc.DrawEllipse(null, new Pen(viz, 1), center, 8, 8);
                        for (int i = 0; i < 48; i++)
                        {
                            double a = i / 48.0 * Math.PI * 2 - Math.PI / 2;
                            double r0 = 10, r1 = 10 + v[i] * 15;
                            dc.DrawLine(pen,
                                new Point(center.X + Math.Cos(a) * r0, center.Y + Math.Sin(a) * r0),
                                new Point(center.X + Math.Cos(a) * r1, center.Y + Math.Sin(a) * r1));
                        }
                        break;
                    }

                    case 5: // VU bars — wide LED bars, green/yellow/red zones
                    {
                        const int segH = 4, segGap = 2, rows = 8;
                        double vw = W / 24.0;
                        for (int i = 0; i < 24; i++)
                        {
                            double val = (v[i * 2] + v[i * 2 + 1]) / 2;
                            int lit = (int)Math.Round(val * rows);
                            for (int r = 0; r < lit; r++)
                            {
                                double frac = (double)r / rows;
                                var brush = new SolidColorBrush(frac < 0.55 ? Color.FromRgb(0x1D, 0xB9, 0x54)
                                    : frac < 0.8 ? Color.FromRgb(0xFF, 0xC8, 0x3D)
                                    : Color.FromRgb(0xFF, 0x52, 0x52));
                                double y = H - 2 - (r + 1) * segH - r * segGap;
                                dc.DrawRectangle(brush, null, new Rect(i * vw + 1, y, vw - 2, segH));
                            }
                        }
                        break;
                    }

                    case 6: // Waterfall — newest frame on top, history scrolls down and fades
                    {
                        var frame = new float[48];
                        for (int i = 0; i < 48; i++) frame[i] = (float)v[i];
                        _waterHistory.Insert(0, frame);
                        while (_waterHistory.Count > 12) _waterHistory.RemoveAt(_waterHistory.Count - 1);
                        double cellH = H / 12.0;
                        for (int r = 0; r < _waterHistory.Count; r++)
                        {
                            var row = _waterHistory[r];
                            for (int i = 0; i < 48; i++)
                            {
                                if (row[i] < 0.04) continue;
                                var brush = new SolidColorBrush(viz.Color) { Opacity = Math.Clamp(row[i], 0f, 1f) * (1 - r / 16.0) };
                                dc.DrawRectangle(brush, null, new Rect(i * bw + 0.5, r * cellH + 0.5, bw - 1, cellH - 1));
                            }
                        }
                        break;
                    }

                    case 10: // Falling dots — dim bars with bright dots that drop with gravity
                        for (int i = 0; i < 48; i++)
                        {
                            double h = Math.Max(1, v[i] * (H - 6));
                            var dimBar = new SolidColorBrush(viz.Color) { Opacity = 0.35 };
                            dc.DrawRectangle(dimBar, null, new Rect(i * bw + 1, H - h, bw - 2, h));
                            if (v[i] >= _dotPos[i]) { _dotPos[i] = (float)v[i]; _dotVel[i] = 0; }
                            else
                            {
                                _dotVel[i] += 0.012f;
                                _dotPos[i] -= _dotVel[i];
                                if (_dotPos[i] < (float)v[i]) _dotPos[i] = (float)v[i];
                            }
                            double dy = H - 3 - _dotPos[i] * (H - 6);
                            dc.DrawEllipse(Brushes.White, null, new Point(i * bw + bw / 2, dy), 2.2, 2.2);
                        }
                        break;
                }
            }

            private static Color HslToRgb(double h, double s, double l)
            {
                double c = (1 - Math.Abs(2 * l - 1)) * s;
                double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
                double m = l - c / 2;
                double r, g, b;
                if (h < 60) { r = c; g = x; b = 0; }
                else if (h < 120) { r = x; g = c; b = 0; }
                else if (h < 180) { r = 0; g = c; b = x; }
                else if (h < 240) { r = 0; g = x; b = c; }
                else if (h < 300) { r = x; g = 0; b = c; }
                else { r = c; g = 0; b = x; }
                return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
            }

            private void LoadFromFile()
            {
                var dialog = new OpenFileDialog { Title = $"Load a song on {_name}", Filter = "Audio|*.mp3;*.wav" };
                if (dialog.ShowDialog() != true) return;
                try { LoadBytes(File.ReadAllBytes(dialog.FileName), dialog.FileName); }
                catch (Exception ex) { MessageBox.Show("Could not load that file: " + ex.Message, _name); }
            }

            private bool LoadBytes(byte[] bytes, string name)
            {
                try
                {
                    _deck.Load(bytes, name);
                    _tempoSlider.Value = 0;
                    _bpmBox.Text = "";
                    _title.Text = $"{_name} — {_deck.Title}";
                    RenderWaveform();
                    RenderPlatterRing();
                    _window.SetStatus($"{_name}: {_deck.Title} loaded ({FmtTime(_deck.TotalSeconds)}). Set the BPM to unlock SYNC and loops.");
                    return true;
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Could not decode that song: " + ex.Message, _name);
                    return false;
                }
            }

            private void RefreshQueueList()
            {
                _queueList.Items.Clear();
                for (int i = 0; i < _queuePaths.Count; i++)
                {
                    string marker = i == _queueIndex ? "▶ " : "";
                    _queueList.Items.Add($"{marker}{i + 1}. {System.IO.Path.GetFileName(_queuePaths[i])}");
                }
                if (_queueIndex >= 0 && _queueIndex < _queueList.Items.Count)
                    _queueList.SelectedIndex = _queueIndex;
                _queueCount.Text = _queuePaths.Count == 0 ? "(empty — add songs to build your set)" : $"{_queuePaths.Count} song(s)";
            }

            private void AddToQueue()
            {
                var dialog = new OpenFileDialog
                {
                    Title = $"Add songs to {_name}'s track list",
                    Filter = "Audio|*.mp3;*.wav",
                    Multiselect = true
                };
                if (dialog.ShowDialog() != true) return;
                foreach (var f in dialog.FileNames) _queuePaths.Add(f);
                RefreshQueueList();
                if (_queueIndex < 0 && _queuePaths.Count > 0)
                    _window.SetStatus($"{_name}: {_queuePaths.Count} song(s) queued — double-click one (or press Next) to load it.");
            }

            private void RemoveFromQueue()
            {
                int i = _queueList.SelectedIndex;
                if (i < 0 || i >= _queuePaths.Count) return;
                _queuePaths.RemoveAt(i);
                if (i < _queueIndex) _queueIndex--;
                else if (i == _queueIndex) _queueIndex = -1; // the loaded song keeps playing from memory
                RefreshQueueList();
            }

            private void LoadQueueItem(int i, bool autoplay)
            {
                if (i < 0 || i >= _queuePaths.Count) return;
                try
                {
                    if (!LoadBytes(File.ReadAllBytes(_queuePaths[i]), System.IO.Path.GetFileName(_queuePaths[i]))) return;
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Could not read that file: " + ex.Message, _name);
                    return;
                }
                _queueIndex = i;
                _title.Text = $"{_name} ({i + 1}/{_queuePaths.Count}) — {_deck.Title}";
                RefreshQueueList();
                if (autoplay && !_deck.IsPlaying) PlayPause();
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
                if (_deck.IsPlaying) _window._owner.EnsureOutputRunning();
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
                // hand stopped moving mid-grab = record held still
                if (_dragging && Environment.TickCount64 - _lastDragMs > 70) _deck.ScratchRate = 0;
                // Track-list auto-advance: the deck flips IsPlaying off at the
                // true end of a song — only then (and only near the end, so a
                // manual pause never triggers it) roll into the next track.
                if (_autoNextBox != null && _autoNextBox.IsChecked == true
                    && _wasPlayingLastTick && !_deck.IsPlaying
                    && _queueIndex >= 0 && _queueIndex + 1 < _queuePaths.Count
                    && _deck.IsLoaded && _deck.TotalSeconds > 0
                    && _deck.PositionSeconds >= _deck.TotalSeconds - 1.0)
                {
                    LoadQueueItem(_queueIndex + 1, true);
                }
                _wasPlayingLastTick = _deck.IsPlaying;
                if (_deck.IsLoaded)
                    _platterSpin.Angle = _deck.PositionSeconds * 200.0 % 360.0; // 33⅓ RPM
                if (_deck.IsLoaded && _cassetteView != null && _cassetteView.Visibility == Visibility.Visible)
                {
                    double ang = _deck.PositionSeconds * 150.0 % 360.0;
                    if (_reelSpinL != null) _reelSpinL.Angle = ang;
                    if (_reelSpinR != null) _reelSpinR.Angle = ang;
                    double frac = _deck.TotalSeconds > 0 ? Math.Clamp(_deck.PositionSeconds / _deck.TotalSeconds, 0, 1) : 0;
                    double dl = 42 - 16 * frac, dr = 26 + 16 * frac;
                    if (_tapeL != null) { _tapeL.Width = dl; _tapeL.Height = dl; }
                    if (_tapeR != null) { _tapeR.Width = dr; _tapeR.Height = dr; }
                }
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
                if (_deck.IsLoaded && _ringDim != null)
                {
                    double level = _deck.IsPlaying ? _deck.Level : 0;
                    int current = _deck.TotalSeconds > 0
                        ? Math.Min(RingBars - 1, (int)(_deck.PositionSeconds / _deck.TotalSeconds * RingBars)) : 0;
                    for (int i = 0; i < RingBars; i++)
                    {
                        var line = _ringLines[i];
                        Brush target = i == current ? Brushes.White : i < current ? (Brush)_accent : _ringDim;
                        if (line.Stroke != target) line.Stroke = target;
                        double len = 3 + _ringPeaks[i] * 16;
                        if (i == current) len *= 1 + 0.8 * level;
                        if (Math.Abs(len - _ringLens[i]) > 0.05)
                        {
                            _ringLens[i] = len;
                            PositionRingLine(line, i * 2 * Math.PI / RingBars - Math.PI / 2, len);
                        }
                    }
                    if (_haloFx != null && _deck.IsPlaying)
                    {
                        _haloFx.BlurRadius = 20 + level * 26;
                        _haloFx.Opacity = 0.65 + level * 0.35;
                    }
                }
                if (_visualBorder != null && _visualBorder.Visibility == Visibility.Visible && _visualPicker != null)
                {
                    const int W = 440, H = 54;
                    var visual = new DrawingVisual();
                    using (var dc = visual.RenderOpen())
                    {
                        DrawDeckVisual(dc, _visualPicker.SelectedIndex, W, H);
                    }
                    var bmp = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
                    bmp.Render(visual);
                    _visualImage.Source = bmp;
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
