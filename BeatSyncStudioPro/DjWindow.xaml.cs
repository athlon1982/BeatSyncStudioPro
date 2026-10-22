using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
        private readonly AudioTapSampleProvider _masterTap;
        private readonly DrawingVisual _masterVisual = new DrawingVisual();
        private bool _syncingChannelUi;

        // v3.42: click the brand-strip spectrum to cycle its density too.
        private static readonly int[] MasterSpectrumSteps = { 32, 64, 96 };
        private int _masterBands = 64;

        private void MasterSpectrum_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            int idx = Array.IndexOf(MasterSpectrumSteps, _masterBands);
            _masterBands = MasterSpectrumSteps[(idx + 1) % MasterSpectrumSteps.Length];
            string label = _masterBands == 32 ? "FAT bars" : _masterBands == 96 ? "FINE bars" : "NORMAL bars";
            SetStatus($"Master spectrum: {label} — {_masterBands} bars. Click it again to change it.");
        }
        private static readonly Brush SpectrumBgBrush = new SolidColorBrush(Color.FromRgb(0x0B, 0x0D, 0x13));
        private static readonly Brush[] SpectrumBrushes = BuildSpectrumBrushes();
        private readonly DeckPanel _panelA;
        private readonly DeckPanel _panelB;
        private readonly DispatcherTimer _timer;
        private TrackFxWindow? _fxWindow;
        // Live mic channel (v3.31)
        private readonly MicChannel _mic = new MicChannel();
        private VolumeSampleProvider _micGain = null!;
        private bool _micOn;
        private float _musicLevel = 0.9f;
        // Phone companion (v3.32)
        private readonly CompanionServer _companion = new CompanionServer();
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _companionQueue = new System.Collections.Concurrent.ConcurrentQueue<string>();
        private volatile string _companionStateJson = "{}";
        private CompanionWindow? _companionWindow;
        internal CompanionServer Companion => _companion;
        internal void StartCompanionServer() => _companion.Start(CompanionServer.LoadSettings().Port);
        internal void StopCompanionServer() => _companion.Stop();
        // Sampler (v3.27)
        private readonly SamplerEngine _sampler = new SamplerEngine();
        private readonly Button[] _padButtons = new Button[SamplerEngine.PadCount];
        private readonly TextBlock[] _padSubs = new TextBlock[SamplerEngine.PadCount];
        private readonly TextBlock[] _padTitles = new TextBlock[SamplerEngine.PadCount];
        private static readonly Brush PadBaseBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x3C));
        private static readonly Brush PadFlashBrush = new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54));
        private static readonly Brush PadSubBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x93, 0xA5));
        private static readonly (string Emoji, string Name, string? PackEntry)[] PadDefs =
        {
            ("\U0001F399\uFE0F", "RAD-DAD!", null),
            ("\U0001F4A5", "Impact", "FX/Impact.wav"),
            ("\U0001F680", "Riser", "FX/Riser.wav"),
            ("\U0001F4C9", "Downlifter", "FX/Downlifter.wav"),
            ("\U0001F941", "Crash", "Drums/Crash 1.wav"),
            ("\U0001F44F", "Clap", "Drums/Clap Big.wav"),
            ("\U0001F50A", "808 Drop", "Drums/808 Long.wav"),
            ("\U0001F514", "Cowbell", "Drums/Cowbell 1.wav"),
        };
        // Cruise mode (v3.26): hands-free auto-DJ across both deck queues
        private bool _cruiseOn;
        private readonly System.Collections.Generic.List<string> _cruisePlaylist = new System.Collections.Generic.List<string>();
        private int _cruiseIndex = -1;
        private DeckPanel? _cruisePanel;
        private DeckPanel? _cruiseFadePanel;
        private bool _cruiseFading;
        private bool _cruiseLoading;
        private int _cruiseGeneration;
        internal sealed record KeyCacheEntry(int Pc, bool Minor);
        private static readonly Brush KeyOkBrush = new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54));
        private static readonly Brush KeyClashBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xC7, 0x66));
        private static readonly Brush KeyNeutralBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
        private double _fadeFrom, _fadeTo, _fadeElapsed, _fadeDuration = 8;
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
            // Final mix = music path (duckable) + mic path (never ducked), so
            // Record Mix captures the voice over the music.
            var finalMix = new MixingSampleProvider(format) { ReadFully = true };
            finalMix.AddMixerInput(_masterGain);
            _micGain = new VolumeSampleProvider(ClipFxChain.Apply(_mic, _mic.Fx)) { Volume = 0f };
            finalMix.AddMixerInput(_micGain);
            _masterTap = new AudioTapSampleProvider(finalMix);
            _recorder = new RecordingSampleProvider(_masterTap);
            _owner.AttachDjMixer(_recorder);
            djMaster.AddMixerInput(_sampler.Output);
            SamplerVolumeKnob.ValueChanged += (s, e) => _sampler.Output.Volume = (float)SamplerVolumeKnob.Value;
            BuildSamplerPads();
            for (int i = 0; i < MicChannel.DeviceCount; i++) MicDevicePicker.Items.Add(MicChannel.DeviceName(i));
            if (MicDevicePicker.Items.Count > 0) MicDevicePicker.SelectedIndex = 0;
            else MicDevicePicker.Items.Add("(no microphone found)");

            _panelA = new DeckPanel(this, _deckA, () => _deckB, _fxA, "DECK A",
                new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54)), DeckHostA, chainA.Channel, chainA.Tap);
            _panelB = new DeckPanel(this, _deckB, () => _deckA, _fxB, "DECK B",
                new SolidColorBrush(Color.FromRgb(0xB3, 0x88, 0xFF)), DeckHostB, chainB.Channel, chainB.Tap);

            XfadeSlider.ValueChanged += (s, e) => UpdateCrossfader();
            CurvePicker.SelectionChanged += (s, e) => UpdateCrossfader();
            DjMasterKnob.ValueChanged += (s, e) => _musicLevel = (float)DjMasterKnob.Value;
            UpdateCrossfader();
            WireChannelStrips();
            MasterSpectrumImage.Source = new DrawingImage(_masterVisual.Drawing);

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _companion.CommandReceived += json => _companionQueue.Enqueue(json);
            _companion.StateJsonProvider = () => _companionStateJson;
            _timer.Tick += (s, e) => { _panelA.Refresh(); _panelB.Refresh(); CruiseTick(); SamplerFlashTick(); MicTick(); MixerTick(); DrainCompanion(); };
            _timer.Start();
            TuneToolTips(this);

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
            Closed += (s, e) => { _timer.Stop(); _recorder.Stop(); _owner.DetachDjMixer(_recorder); _mic.Dispose(); _companion.Stop(); };
        }

        // v3.40: snappy, long-lived hover help on every element in the window.
        private static void TuneToolTips(DependencyObject node)
        {
            if (node is FrameworkElement fe)
            {
                ToolTipService.SetInitialShowDelay(fe, 400);
                ToolTipService.SetShowDuration(fe, 15000);
            }
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++) TuneToolTips(VisualTreeHelper.GetChild(node, i));
        }

        // ===== v3.37 concept board: channel strips + live master spectrum =====
        // The strip gain knob, the strip fader and the deck panel's own gain knob
        // are three surfaces on ONE value (the deck's channel volume); the EQ
        // knobs drive the deck's real TrackFxSettings (the same EQ the FX popup
        // edits, which defaults to on and only bites when a band is off zero).
        private void WireChannelStrips()
        {
            ChAGainKnob.ValueChanged += (s, e) => { if (!_syncingChannelUi) _panelA.SetChannelVolume(ChAGainKnob.Value); };
            ChBGainKnob.ValueChanged += (s, e) => { if (!_syncingChannelUi) _panelB.SetChannelVolume(ChBGainKnob.Value); };
            ChAFader.ValueChanged += (s, e) => { if (!_syncingChannelUi) _panelA.SetChannelVolume(ChAFader.Value / 1000.0 * 1.25); };
            ChBFader.ValueChanged += (s, e) => { if (!_syncingChannelUi) _panelB.SetChannelVolume(ChBFader.Value / 1000.0 * 1.25); };
            ChAHiKnob.ValueChanged += (s, e) => _fxA.HighDb = ChAHiKnob.Value;
            ChAMidKnob.ValueChanged += (s, e) => _fxA.MidDb = ChAMidKnob.Value;
            ChALowKnob.ValueChanged += (s, e) => _fxA.LowDb = ChALowKnob.Value;
            ChBHiKnob.ValueChanged += (s, e) => _fxB.HighDb = ChBHiKnob.Value;
            ChBMidKnob.ValueChanged += (s, e) => _fxB.MidDb = ChBMidKnob.Value;
            ChBLowKnob.ValueChanged += (s, e) => _fxB.LowDb = ChBLowKnob.Value;
        }

        private void MixerTick()
        {
            VuAFill.Height = Math.Clamp(_panelA.TapLevel, 0f, 1f) * 248;
            VuBFill.Height = Math.Clamp(_panelB.TapLevel, 0f, 1f) * 248;
            _syncingChannelUi = true;
            SyncStrip(_panelA, ChAGainKnob, ChAFader);
            SyncStrip(_panelB, ChBGainKnob, ChBFader);
            _syncingChannelUi = false;
            DrawMasterSpectrum();
        }

        private void SyncStrip(DeckPanel panel, KnobControl gainKnob, Slider fader)
        {
            double v = panel.ChannelVolume;
            if (Math.Abs(gainKnob.Value - v) > 0.005) gainKnob.Value = v;
            double f = v / 1.25 * 1000;
            if (Math.Abs(fader.Value - f) > 2) fader.Value = f;
        }

        private void DrawMasterSpectrum()
        {
            double w = MasterSpectrumImage.ActualWidth, h = MasterSpectrumImage.ActualHeight;
            if (w < 10 || h < 10) return;
            var bands = _masterTap.GetSpectrum(_masterBands);
            using var dc = _masterVisual.RenderOpen();
            dc.DrawRectangle(SpectrumBgBrush, null, new Rect(0, 0, w, h));
            double bw = w / bands.Length;
            for (int i = 0; i < bands.Length; i++)
            {
                double bh = Math.Clamp(bands[i], 0.01, 1) * (h - 2);
                var brush = SpectrumBrushes[Math.Min(SpectrumBrushes.Length - 1, (int)(Math.Clamp(bands[i], 0, 1) * SpectrumBrushes.Length))];
                dc.DrawRectangle(brush, null, new Rect(i * bw + 0.5, h - bh, Math.Max(1, bw - 1.5), bh));
            }
        }

        private static Brush[] BuildSpectrumBrushes()
        {
            var list = new System.Collections.Generic.List<Brush>();
            for (int i = 0; i < 24; i++)
            {
                double t = i / 23.0;
                Color c = t < 0.6
                    ? LerpColor(Color.FromRgb(0x1D, 0xB9, 0x54), Color.FromRgb(0xE8, 0xC7, 0x66), t / 0.6)
                    : LerpColor(Color.FromRgb(0xE8, 0xC7, 0x66), Color.FromRgb(0xE0, 0x54, 0xE0), (t - 0.6) / 0.4);
                var b = new SolidColorBrush(c);
                b.Freeze();
                list.Add(b);
            }
            return list.ToArray();
        }

        private static Color LerpColor(Color a, Color b, double t)
        {
            t = Math.Clamp(t, 0, 1);
            return Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
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

        private double CruiseFadeSeconds => CruiseFadePicker.SelectedIndex switch { 0 => 2, 1 => 4, 3 => 12, _ => 8 };

        private void CruiseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_cruiseOn) TurnCruiseOff("\U0001F6A2 Cruise off.");
            else StartCruise();
        }

        private void ChAReset_Click(object sender, RoutedEventArgs e) => ResetChannelStrip(ChAGainKnob, ChAHiKnob, ChAMidKnob, ChALowKnob, ChAFader, "A");
        private void ChBReset_Click(object sender, RoutedEventArgs e) => ResetChannelStrip(ChBGainKnob, ChBHiKnob, ChBMidKnob, ChBLowKnob, ChBFader, "B");

        private void ResetChannelStrip(KnobControl gain, KnobControl hi, KnobControl mid, KnobControl low, Slider fader, string which)
        {
            gain.Value = 1.0;
            hi.Value = 0;
            mid.Value = 0;
            low.Value = 0;
            fader.Value = 800;
            SetStatus($"Mixer: channel {which} reset — gain 1.0, EQ flat, fader at unity.");
        }

        // ===== v3.41: MIX A+B — one button sets up and plays a two-deck mix =====
        private bool _mixBusy;

        private async void MixButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mixBusy) return;
            if (!_panelA.HasTrack || !_panelB.HasTrack)
            {
                SetStatus("\U0001F500 MIX A+B needs a track loaded on BOTH decks first.");
                return;
            }
            _mixBusy = true;
            MixButton.IsEnabled = false;
            FindPairButton.IsEnabled = false;
            try { await RunMixSetupAsync(); }
            finally { _mixBusy = false; MixButton.IsEnabled = true; FindPairButton.IsEnabled = true; }
        }

        private async Task RunMixSetupAsync(bool mashup = false)
        {
            if (_cruiseOn) TurnCruiseOff("\U0001F500 Cruise off — MIX A+B took over.");
            SetStatus("\U0001F500 MIX A+B: working out the BPMs…");
            bool bpmA = await _panelA.EnsureBpmAsync();
            bool bpmB = await _panelB.EnsureBpmAsync();
            if (!bpmA || !bpmB)
            {
                SetStatus($"\U0001F500 MIX A+B stopped — no BPM for {(!bpmA ? "Deck A" : "Deck B")} (auto-detect couldn't find one). Type it in or TAP it, then press MIX A+B again.");
                return;
            }
            // Key detection runs by itself after each load; give it a moment to land.
            for (int i = 0; i < 60 && (!_panelA.KeyReady || !_panelB.KeyReady); i++)
                await Task.Delay(200);
            bool synced = _panelB.TrySyncTempoTo(_deckA.EffectiveBpm, out string how);
            bool keySynced = await _panelB.EnsureKeySyncedAsync();
            if (mashup)
            {
                // The real remix recipe: A's voice over B's beat — no lyric pile-up.
                _panelA.SetStemMix(vocals: true, drums: false, bass: false, other: false);
                _panelB.SetStemMix(vocals: false, drums: true, bass: true, other: true);
            }
            CurvePicker.SelectedIndex = 1;   // Constant Power
            XfadeSlider.Value = 500;          // both decks fully up
            _panelA.MixStopAndRewindToCue();
            _panelB.MixStopAndRewindToCue();
            _panelA.MixPlay();
            _panelB.MixPlay();
            string tempoNote = synced
                ? (how.Length == 0 ? $"Deck B tempo matched to {_deckA.EffectiveBpm:0.0} BPM" : $"Deck B matched at {how} ({_deckB.Bpm:0} → {_deckA.EffectiveBpm:0.0} BPM)")
                : $"⚠ BPMs too far apart to tempo-match ({_deckA.Bpm:0} vs {_deckB.Bpm:0}) — playing unmatched";
            string keyNote = keySynced ? "keys in sync" : (_panelA.KeyReady && _panelB.KeyReady ? "keys left as-is" : "key not detected — no key sync");
            if (mashup)
                SetStatus($"🎤 MASHUP A+B: Deck A's VOCALS over Deck B's BEAT (drums+bass+other) • {tempoNote} • {keyNote} • crossfader centered. Want it the other way round? Swap the two tracks and press MASHUP again.");
            else
                SetStatus($"\U0001F500 MIXING A+B: {tempoNote} • {keyNote} • crossfader centered. Ride the channel faders + EQ to blend!");
        }

        // ===== v3.43: shared progress window (Best Pair + Mashup narrate their steps) =====
        private sealed class ProgressWindowHelper
        {
            private readonly Window _win;
            private readonly ProgressBar _bar;
            private readonly TextBlock _status;
            private readonly StackPanel _log;
            private readonly ScrollViewer _scroll;
            private bool _closed;

            public ProgressWindowHelper(Window owner, string title)
            {
                _win = new Window
                {
                    Title = title,
                    Width = 560,
                    Height = 420,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = owner,
                    ResizeMode = ResizeMode.NoResize,
                    Background = new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x1E)),
                    Foreground = Brushes.White
                };
                var root = new StackPanel { Margin = new Thickness(16) };
                root.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.Bold, Foreground = Brushes.White });
                _bar = new ProgressBar { Height = 12, Margin = new Thickness(0, 10, 0, 6), Minimum = 0, Maximum = 100, Value = 0 };
                root.Children.Add(_bar);
                _status = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA5, 0xB5)), Text = "Starting…" };
                root.Children.Add(_status);
                _log = new StackPanel();
                _scroll = new ScrollViewer { Height = 250, Margin = new Thickness(0, 10, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _log };
                root.Children.Add(_scroll);
                _win.Content = root;
                _win.Closed += (s, e) => _closed = true;
            }

            public void Show() => _win.Show();
            public void Close() { if (!_closed) _win.Close(); }
            public void SetBar(int done, int total) { if (!_closed && total > 0) _bar.Value = done * 100.0 / total; }
            public void SetStatus(string text) { if (!_closed) _status.Text = text; }

            public void Log(string text, bool bold = false)
            {
                if (_closed) return;
                _log.Children.Add(new TextBlock
                {
                    Text = text,
                    Foreground = Brushes.White,
                    FontSize = 12.5,
                    FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                    Margin = new Thickness(0, 2, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                });
                _scroll.ScrollToEnd();
            }

            public void Fail(string text)
            {
                Log("✗ " + text, true);
                SetStatus("Stopped — the log above says why. You can close this window.");
            }
        }

        // ===== v3.43: MASHUP A+B — A's vocals over B's beat (the real remix recipe) =====
        private bool _mashupBusy;

        private async void MashupButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mashupBusy || _mixBusy || _pairBusy) return;
            if (!_panelA.HasTrack || !_panelB.HasTrack)
            {
                SetStatus("🎤 MASHUP A+B needs a track loaded on BOTH decks first.");
                return;
            }
            _mashupBusy = true;
            MashupButton.IsEnabled = false;
            MixButton.IsEnabled = false;
            FindPairButton.IsEnabled = false;
            var prog = new ProgressWindowHelper(this, "🎤 Mashup A+B — vocals over beat");
            prog.Show();
            try
            {
                if (_cruiseOn) TurnCruiseOff("🎤 Cruise off — MASHUP A+B took over.");
                prog.SetStatus("Working out the BPMs…");
                bool bpmA = await _panelA.EnsureBpmAsync();
                bool bpmB = await _panelB.EnsureBpmAsync();
                if (!bpmA || !bpmB)
                {
                    prog.Fail($"No BPM for {(!bpmA ? "Deck A" : "Deck B")} (auto-detect couldn't find one). Type it in or TAP it on that deck, then press MASHUP again.");
                    return;
                }
                prog.Log($"✓ BPMs ready: Deck A {_deckA.Bpm:0} • Deck B {_deckB.Bpm:0}");
                prog.SetBar(1, 3);
                if (!_panelA.StemsReady) prog.SetStatus("Splitting Deck A's stems… (first time for a song takes a while; after that it's cached)");
                bool stemsA = await _panelA.EnsureStemsAsync();
                if (!stemsA)
                {
                    prog.Fail("Deck A's stems couldn't be made — a mashup needs stems. The deck's own stem line (under Split Stems) says why: model download declined, or the split failed.");
                    return;
                }
                prog.Log("✓ Deck A stems ready");
                prog.SetBar(2, 3);
                if (!_panelB.StemsReady) prog.SetStatus("Splitting Deck B's stems…");
                bool stemsB = await _panelB.EnsureStemsAsync();
                if (!stemsB)
                {
                    prog.Fail("Deck B's stems couldn't be made — a mashup needs stems. The deck's own stem line (under Split Stems) says why.");
                    return;
                }
                prog.Log("✓ Deck B stems ready");
                prog.Log("Setting Deck A = VOCALS ONLY, Deck B = BEAT ONLY (drums + bass + other), syncing tempo + key, starting both together…", true);
                prog.SetBar(3, 3);
                prog.Close();
                await RunMixSetupAsync(mashup: true);
            }
            finally
            {
                _mashupBusy = false;
                MashupButton.IsEnabled = true;
                MixButton.IsEnabled = true;
                FindPairButton.IsEnabled = true;
            }
        }

        // ===== v3.42: Find Best Pair — analyze a pile of songs, remix the two that fit =====
        private sealed class PairCandidate
        {
            public string Name = "";
            public byte[] Bytes = Array.Empty<byte>();
            public double Bpm;
            public KeyDetector.KeyResult? Key;
        }

        private bool _pairBusy;

        private async void FindPairButton_Click(object sender, RoutedEventArgs e)
        {
            if (_pairBusy || _mixBusy) return;
            var dialog = new OpenFileDialog
            {
                Title = "Pick a bunch of songs — I'll find the best two to remix",
                Filter = "Audio|*.mp3;*.wav",
                Multiselect = true
            };
            if (dialog.ShowDialog() != true) return;
            var files = dialog.FileNames.Take(12).ToArray();
            if (files.Length < 2) { SetStatus("🎲 Pick at least two songs and I'll find the best pair."); return; }
            _pairBusy = true;
            FindPairButton.IsEnabled = false;
            MixButton.IsEnabled = false;
            MashupButton.IsEnabled = false;
            var prog = new ProgressWindowHelper(this, "🎲 Find Best Pair");
            prog.Show();
            prog.Log($"Analyzing {files.Length} songs — BPM and key for each one…");
            try
            {
                var found = new List<PairCandidate>();
                for (int i = 0; i < files.Length; i++)
                {
                    string path = files[i];
                    string name = System.IO.Path.GetFileName(path);
                    prog.SetStatus($"Analyzing {i + 1}/{files.Length}: {name}…");
                    prog.SetBar(i, files.Length);
                    try
                    {
                        var bytes = await Task.Run(() => File.ReadAllBytes(path));
                        string type = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
                        var (bpm, _, _) = await Task.Run(() => BeatDetector.Detect(bytes, type));
                        if (bpm < 50 || bpm > 200) { prog.Log($"✗ {name} — skipped (no clear BPM found)"); continue; }
                        KeyDetector.KeyResult? key = null;
                        try
                        {
                            key = await Task.Run(() =>
                            {
                                var (l, r) = StemSeparator.DecodeBytes(bytes, type);
                                var mono = new float[l.Length];
                                for (int j = 0; j < l.Length; j++) mono[j] = (l[j] + r[j]) * 0.5f;
                                return KeyDetector.Detect(mono);
                            });
                        }
                        catch { /* key unknown — scores neutral */ }
                        var cand = new PairCandidate { Name = name, Bytes = bytes, Bpm = Math.Round(bpm * 2) / 2, Key = key };
                        found.Add(cand);
                        prog.Log($"✓ {name} — {cand.Bpm:0} BPM, {(key != null ? key.KeyName + " (" + key.Camelot + ")" : "key not detected")}");
                        prog.SetBar(i + 1, files.Length);
                    }
                    catch { prog.Log($"✗ {name} — skipped (couldn't read that file)"); }
                }
                if (found.Count < 2)
                {
                    prog.Fail($"Only {found.Count} of those songs could be analyzed — pick at least two good MP3/WAV files.");
                    return;
                }
                prog.SetStatus("Scoring every pair for tempo + key fit…");
                prog.Log($"Scoring {found.Count * (found.Count - 1) / 2} possible pairs…");
                PairCandidate? bestA = null, bestB = null;
                double bestScore = double.MinValue;
                for (int i = 0; i < found.Count; i++)
                    for (int j = i + 1; j < found.Count; j++)
                    {
                        double sc = PairScore(found[i], found[j]);
                        if (sc > bestScore) { bestScore = sc; bestA = found[i]; bestB = found[j]; }
                    }
                if (bestA == null || bestB == null) return;
                string keyA = bestA.Key != null ? bestA.Key.KeyName + " (" + bestA.Key.Camelot + ")" : "key ?";
                string keyB = bestB.Key != null ? bestB.Key.KeyName + " (" + bestB.Key.Camelot + ")" : "key ?";
                prog.Log($"🏆 Best pair: \"{bestA.Name}\" ({bestA.Bpm:0} BPM, {keyA})", true);
                prog.Log($"🏆        + \"{bestB.Name}\" ({bestB.Bpm:0} BPM, {keyB})", true);
                prog.Log("Loading them onto the decks and mixing… (Tip: press 🎤 MASHUP next for A's vocals over B's beat — no lyric pile-up.)");
                if (!_panelA.LoadForMix(bestA.Bytes, bestA.Name) || !_panelB.LoadForMix(bestB.Bytes, bestB.Name))
                {
                    prog.Fail("Couldn't load the winning pair onto the decks.");
                    return;
                }
                _panelA.SetBpmUi(bestA.Bpm);
                _panelB.SetBpmUi(bestB.Bpm);
                prog.Close();
                await RunMixSetupAsync();
            }
            finally
            {
                _pairBusy = false;
                FindPairButton.IsEnabled = true;
                MixButton.IsEnabled = true;
                MashupButton.IsEnabled = true;
            }
        }

        private static double PairScore(PairCandidate a, PairCandidate b)
        {
            // Tempo: straight match within ±8% scores up to 100; half/double-time up to 60; else 0.
            double r = b.Bpm / a.Bpm;
            double tempo;
            if (r >= 0.92 && r <= 1.08) tempo = 100 * (1 - Math.Abs(r - 1) / 0.08 * 0.5);
            else if (r * 2 >= 0.92 && r * 2 <= 1.08) tempo = 60 * (1 - Math.Abs(r * 2 - 1) / 0.08 * 0.5);
            else if (r / 2 >= 0.92 && r / 2 <= 1.08) tempo = 60 * (1 - Math.Abs(r / 2 - 1) / 0.08 * 0.5);
            else tempo = 0;
            // Key: Camelot-compatible = 100; within 2 semitones = 55; unknown = 35; far = 10.
            double keyScore;
            if (a.Key == null || b.Key == null) keyScore = 35;
            else if (KeyDetector.CamelotCompatible(a.Key.Camelot, b.Key.Camelot)) keyScore = 100;
            else
            {
                int d = Math.Abs(a.Key.PitchClass - b.Key.PitchClass);
                d = Math.Min(d, 12 - d);
                keyScore = d <= 2 ? 55 : 10;
            }
            return tempo * 0.6 + keyScore * 0.4;
        }

        private async void StartCruise()
        {
            _cruisePlaylist.Clear();
            _cruisePlaylist.AddRange(_panelA.QueuePathsSnapshot);
            _cruisePlaylist.AddRange(_panelB.QueuePathsSnapshot);
            if (_cruisePlaylist.Count == 0)
            {
                SetStatus("\U0001F6A2 Cruise needs songs - add tracks to a deck's TRACK LIST first.");
                return;
            }
            _panelA.SetAutoNext(false);
            _panelB.SetAutoNext(false);
            _cruiseOn = true;
            _cruiseFading = false;
            _cruiseIndex = 0;
            _cruisePanel = _panelA;
            CruiseButton.Content = "\U0001F6A2 CRUISE: ON";
            CruiseButton.Background = new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54));
            _cruiseGeneration++;
            int gen = _cruiseGeneration;
            _cruiseLoading = true;
            bool loaded = false;
            while (_cruiseIndex < _cruisePlaylist.Count)
            {
                if (await _panelA.LoadPathForCruiseAsync(_cruisePlaylist[_cruiseIndex])) { loaded = true; break; }
                _cruiseIndex++;
            }
            _cruiseLoading = false;
            if (gen != _cruiseGeneration || !_cruiseOn) return;
            if (!loaded) { TurnCruiseOff("\U0001F6A2 Cruise stopped - none of the queued tracks could be loaded."); return; }
            XfadeSlider.Value = 0;
            _panelA.StartForCruise();
            SetStatus($"\U0001F6A2 Cruise on - {_cruisePlaylist.Count} track(s) queued. Now playing: {_panelA.CurrentTitle}");
        }

        private void TurnCruiseOff(string reason)
        {
            _cruiseOn = false;
            _cruiseFading = false;
            _cruiseLoading = false;
            _cruiseGeneration++;
            _cruisePanel = null;
            _cruiseFadePanel = null;
            CruiseButton.Content = "\U0001F6A2 CRUISE: OFF";
            CruiseButton.Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x3C));
            SetStatus(reason);
        }

        internal void CruiseManualOverride()
        {
            if (_cruiseOn) TurnCruiseOff("\U0001F6A2 Cruise off - manual control.");
        }

        private DeckPanel OtherPanel(DeckPanel p) => p == _panelA ? _panelB : _panelA;

        internal void RefreshKeyCompatibility()
        {
            string? ca = _panelA.EffectiveCamelot, cb = _panelB.EffectiveCamelot;
            if (ca != null && cb != null)
            {
                var brush = KeyDetector.CamelotCompatible(ca, cb) ? KeyOkBrush : KeyClashBrush;
                _panelA.SetKeyTextBrush(brush);
                _panelB.SetKeyTextBrush(brush);
            }
            else
            {
                if (ca != null) _panelA.SetKeyTextBrush(KeyNeutralBrush);
                if (cb != null) _panelB.SetKeyTextBrush(KeyNeutralBrush);
            }
            _panelA.UpdateKeyLabel();
            _panelB.UpdateKeyLabel();
        }

        private void ShowCamelotWheel(DeckPanel from)
        {
            var other = OtherPanel(from);
            var w = new CamelotWheelWindow(from.PanelName, from.EffectiveCamelot, other.EffectiveCamelot) { Owner = this };
            w.Show();
        }

        private static string KeyCachePath => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BeatSyncStudioPro", "key-cache.json");

        internal static System.Collections.Generic.Dictionary<string, KeyCacheEntry> LoadKeyCache()
        {
            try
            {
                if (File.Exists(KeyCachePath))
                {
                    var map = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, KeyCacheEntry>>(File.ReadAllText(KeyCachePath));
                    if (map != null) return map;
                }
            }
            catch { }
            return new System.Collections.Generic.Dictionary<string, KeyCacheEntry>();
        }

        internal static void SaveKeyCache(System.Collections.Generic.Dictionary<string, KeyCacheEntry> map)
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(KeyCachePath);
                if (dir != null) Directory.CreateDirectory(dir);
                File.WriteAllText(KeyCachePath, System.Text.Json.JsonSerializer.Serialize(map));
            }
            catch { }
        }

        private void CruiseTick()
        {
            if (!_cruiseOn || _cruisePanel == null) return;
            if (_cruiseFading)
            {
                _fadeElapsed += 0.1;
                double t = Math.Clamp(_fadeElapsed / _fadeDuration, 0, 1);
                XfadeSlider.Value = _fadeFrom + (_fadeTo - _fadeFrom) * t;
                if (t >= 1)
                {
                    _cruiseFading = false;
                    var old = _cruisePanel;
                    _cruisePanel = _cruiseFadePanel;
                    _cruiseFadePanel = null;
                    old.StopForCruise();
                    if (_cruisePanel != null)
                        SetStatus($"\U0001F6A2 Cruise: now playing {_cruisePanel.CurrentTitle} ({_cruiseIndex + 1}/{_cruisePlaylist.Count})");
                }
                return;
            }
            if (_cruiseIndex + 1 >= _cruisePlaylist.Count)
            {
                // last track: cruise ends when it does
                if (!_cruisePanel.IsPlayingDeck && _cruisePanel.RemainingSeconds <= 1.0)
                    TurnCruiseOff("\U0001F6A2 Cruise: playlist finished.");
                return;
            }
            if (_cruisePanel.IsPlayingDeck && _cruisePanel.RemainingSeconds <= CruiseFadeSeconds + 2)
            {
                if (_cruiseLoading) return;
                var next = _cruisePanel == _panelA ? _panelB : _panelA;
                int nextIndex = _cruiseIndex + 1;
                _cruiseLoading = true;
                _ = CruiseAdvanceAsync(next, nextIndex, _cruiseGeneration);
            }
        }

        private async Task CruiseAdvanceAsync(DeckPanel next, int nextIndex, int gen)
        {
            try
            {
                bool ok = await next.LoadPathForCruiseAsync(_cruisePlaylist[nextIndex]);
                if (gen != _cruiseGeneration || !_cruiseOn) return;
                if (!ok) { _cruisePlaylist.RemoveAt(nextIndex); return; }   // skip the broken file, retry next tick
                next.SyncForCruise();
                next.StartForCruise();
                _cruiseIndex = nextIndex;
                _cruiseFadePanel = next;
                _cruiseFading = true;
                _fadeElapsed = 0;
                _fadeDuration = CruiseFadeSeconds;
                _fadeFrom = XfadeSlider.Value;
                _fadeTo = next == _panelA ? 0 : 1000;
                SetStatus($"\U0001F6A2 Cruise: fading to {(next == _panelA ? "DECK A" : "DECK B")} - {next.CurrentTitle}");
            }
            finally { if (gen == _cruiseGeneration) _cruiseLoading = false; }
        }

        private void BuildSamplerPads()
        {
            var assignments = LoadPadAssignments();
            var names = LoadPadNames();
            for (int i = 0; i < SamplerEngine.PadCount; i++)
            {
                int pad = i;
                var (emoji, name, packEntry) = PadDefs[i];
                string displayName = names.TryGetValue(i, out string? custom) && !string.IsNullOrWhiteSpace(custom) ? custom : name;
                var title = new TextBlock { Text = emoji + " " + displayName, FontWeight = FontWeights.Bold, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                var sub = new TextBlock { Text = "", FontSize = 9, Foreground = PadSubBrush, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                var content = new StackPanel();
                content.Children.Add(title);
                content.Children.Add(sub);
                var btn = new Button { Content = content, Height = 54, Style = (Style)TryFindResource("HwButton"), Margin = new Thickness(3), Background = PadBaseBrush, Foreground = Brushes.White, BorderThickness = new Thickness(0), Cursor = Cursors.Hand };
                btn.ToolTip = "Play this sound. Right-click the pad to assign a sound, rename it, or clear it — every pad is programmable.";
                btn.Click += (s, e) => TriggerPad(pad);
                var menu = new ContextMenu();
                var miAssign = new MenuItem { Header = "🎵 Assign sound…" };
                miAssign.Click += (s, e) => AssignPad(pad);
                var miRename = new MenuItem { Header = "✏️ Rename pad…" };
                miRename.Click += (s, e) => RenamePad(pad);
                var miClear = new MenuItem { Header = "🧹 Clear sound" };
                miClear.Click += (s, e) => ClearPad(pad);
                menu.Items.Add(miAssign);
                menu.Items.Add(miRename);
                menu.Items.Add(miClear);
                btn.ContextMenu = menu;
                _padButtons[i] = btn;
                _padSubs[i] = sub;
                _padTitles[i] = title;
                SamplerPadGrid.Children.Add(btn);
                if (assignments.TryGetValue(i, out string? path))
                {
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    {
                        try { _sampler.Slots[i].Load(SamplerEngine.DecodeBytes(File.ReadAllBytes(path), path)); sub.Text = System.IO.Path.GetFileName(path); }
                        catch { sub.Text = "assigned file unreadable"; }
                    }
                    else sub.Text = "right-click: assign a sound";
                }
                else if (packEntry != null)
                {
                    var bytes = SamplerEngine.ReadPackEntry(packEntry);
                    if (bytes != null)
                    {
                        try { _sampler.Slots[i].Load(SamplerEngine.DecodeBytes(bytes, packEntry)); sub.Text = "built-in"; }
                        catch { sub.Text = "built-in (decode failed)"; }
                    }
                    else sub.Text = "sound pack not found";
                }
                else sub.Text = "right-click: assign a sound";
            }
        }

        private void TriggerPad(int pad)
        {
            if (pad < 0 || pad >= SamplerEngine.PadCount) return;
            if (!_sampler.Slots[pad].HasSound)
            {
                SetStatus($"Sampler pad {pad + 1} has no sound yet - right-click it to assign one.");
                return;
            }
            _owner.EnsureOutputRunning();
            _sampler.Trigger(pad);
            _padButtons[pad].Background = PadFlashBrush;
        }

        private void SamplerFlashTick()
        {
            for (int i = 0; i < SamplerEngine.PadCount; i++)
                if (!_sampler.Slots[i].IsPlaying && _padButtons[i] != null && !ReferenceEquals(_padButtons[i].Background, PadBaseBrush))
                    _padButtons[i].Background = PadBaseBrush;
        }

        private void AssignPad(int pad)
        {
            var dialog = new OpenFileDialog { Title = $"Assign a sound to sampler pad {pad + 1}", Filter = "Audio|*.wav;*.mp3" };
            if (dialog.ShowDialog() != true) return;
            try
            {
                var samples = SamplerEngine.DecodeBytes(File.ReadAllBytes(dialog.FileName), dialog.FileName);
                _sampler.Slots[pad].Load(samples);
                var map = LoadPadAssignments();
                map[pad] = dialog.FileName;
                SavePadAssignments(map);
                _padSubs[pad].Text = System.IO.Path.GetFileName(dialog.FileName);
                SetStatus($"Sampler pad {pad + 1}: {System.IO.Path.GetFileName(dialog.FileName)} assigned.");
                TriggerPad(pad);
            }
            catch (Exception ex) { SetStatus("Could not load that sound: " + ex.Message); }
        }

        private static string PadAssignmentsPath => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BeatSyncStudioPro", "sampler-pads.json");

        private static System.Collections.Generic.Dictionary<int, string> LoadPadAssignments()
        {
            try
            {
                if (File.Exists(PadAssignmentsPath))
                {
                    var map = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<int, string>>(File.ReadAllText(PadAssignmentsPath));
                    if (map != null) return map;
                }
            }
            catch { }
            return new System.Collections.Generic.Dictionary<int, string>();
        }

        private static void SavePadAssignments(System.Collections.Generic.Dictionary<int, string> map)
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(PadAssignmentsPath);
                if (dir != null) Directory.CreateDirectory(dir);
                File.WriteAllText(PadAssignmentsPath, System.Text.Json.JsonSerializer.Serialize(map));
            }
            catch { }
        }

        // v3.40: every sampler pad is programmable — custom names persist next
        // to the sound assignments (sampler-pad-names.json, pad index → name).
        private static string PadNamesPath => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BeatSyncStudioPro", "sampler-pad-names.json");

        private static System.Collections.Generic.Dictionary<int, string> LoadPadNames()
        {
            try
            {
                if (File.Exists(PadNamesPath))
                {
                    var map = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<int, string>>(File.ReadAllText(PadNamesPath));
                    if (map != null) return map;
                }
            }
            catch { }
            return new System.Collections.Generic.Dictionary<int, string>();
        }

        private static void SavePadNames(System.Collections.Generic.Dictionary<int, string> map)
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(PadNamesPath);
                if (dir != null) Directory.CreateDirectory(dir);
                File.WriteAllText(PadNamesPath, System.Text.Json.JsonSerializer.Serialize(map));
            }
            catch { }
        }

        private void ClearPad(int pad)
        {
            _sampler.Slots[pad].Clear();
            var map = LoadPadAssignments();
            map[pad] = "";   // an empty entry means "intentionally cleared" — the built-in default must not come back
            SavePadAssignments(map);
            _padSubs[pad].Text = "right-click: assign a sound";
            SetStatus($"Sampler pad {pad + 1} cleared — right-click it to assign a new sound.");
        }

        private void RenamePad(int pad)
        {
            var names = LoadPadNames();
            string current = names.TryGetValue(pad, out string? c) && !string.IsNullOrWhiteSpace(c) ? c : PadDefs[pad].Name;
            var input = new TextBox { Text = current, MinWidth = 240, Margin = new Thickness(10) };
            var ok = new Button { Content = "Save name", IsDefault = true, Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(10, 0, 10, 10), HorizontalAlignment = HorizontalAlignment.Right };
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = $"Name for sampler pad {pad + 1}:", Margin = new Thickness(10, 10, 10, 0), Foreground = Brushes.White });
            panel.Children.Add(input);
            panel.Children.Add(ok);
            var dlg = new Window
            {
                Title = "Rename sampler pad",
                Content = panel,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = new SolidColorBrush(Color.FromRgb(0x17, 0x1B, 0x26)),
                Foreground = Brushes.White,
                ResizeMode = ResizeMode.NoResize
            };
            ok.Click += (s, e) => { dlg.DialogResult = true; };
            input.Loaded += (s, e) => { input.Focus(); input.SelectAll(); };
            if (dlg.ShowDialog() != true) return;
            string newName = input.Text.Trim();
            if (newName.Length == 0) newName = PadDefs[pad].Name;
            names[pad] = newName;
            SavePadNames(names);
            _padTitles[pad].Text = PadDefs[pad].Emoji + " " + newName;
            SetStatus($"Sampler pad {pad + 1} renamed to \"{newName}\".");
        }

        private void MicButton_Click(object sender, RoutedEventArgs e)
        {
            if (_micOn) { StopMic(); return; }
            if (MicChannel.DeviceCount == 0)
            {
                SetStatus("\U0001F3A4 No microphone found - plug one in and reopen the DJ window.");
                return;
            }
            try
            {
                _mic.Start(Math.Max(0, MicDevicePicker.SelectedIndex));
                _micOn = true;
                _owner.EnsureOutputRunning();
                MicButton.Content = "\U0001F3A4 MIC: LIVE";
                MicButton.Background = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
                SetStatus("\U0001F3A4 Mic is LIVE - you are on the mix (and in Record Mix).");
            }
            catch (Exception ex) { SetStatus("\U0001F3A4 Couldn't open the microphone: " + ex.Message); }
        }

        private void StopMic()
        {
            _mic.Stop();
            _micOn = false;
            MicButton.Content = "\U0001F3A4 MIC: OFF";
            MicButton.Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x3C));
            SetStatus("\U0001F3A4 Mic off.");
        }

        private void MicFxButton_Click(object sender, RoutedEventArgs e) => OpenFx(_mic.Fx, "MIC");

        private void MicTick()
        {
            // Auto-duck: talking pulls the MUSIC down (never the mic), fast
            // attack, slow release; otherwise the master slider rules.
            bool ducking = _micOn && MicDuckBox.IsChecked == true && _mic.Level > 0.03f;
            float target = ducking ? _musicLevel * 0.35f : _musicLevel;
            float cur = _masterGain.Volume;
            float factor = target < cur ? 0.5f : 0.12f;
            _masterGain.Volume = cur + (target - cur) * factor;
            if (_micGain != null) _micGain.Volume = _micOn ? (float)MicLevelKnob.Value : 0f;
            if (MicMeterFill != null && MicMeterGrid != null)
                MicMeterFill.Width = _micOn ? Math.Clamp(_mic.Level, 0f, 1f) * MicMeterGrid.ActualWidth : 0;
        }

        private void CompanionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_companionWindow == null)
            {
                _companionWindow = new CompanionWindow(this) { Owner = this };
                _companionWindow.Closed += (s2, e2) => _companionWindow = null;
                _companionWindow.Show();
            }
            else _companionWindow.Activate();
        }

        private void DrainCompanion()
        {
            while (_companionQueue.TryDequeue(out var json))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("cmd", out var cmdEl)) continue;
                    switch (cmdEl.GetString())
                    {
                        case "playA": _panelA.RemotePlayPause(); break;
                        case "playB": _panelB.RemotePlayPause(); break;
                        case "cueA": _panelA.RemoteCue(); break;
                        case "cueB": _panelB.RemoteCue(); break;
                        case "xfader": if (root.TryGetProperty("value", out var xv)) XfadeSlider.Value = Math.Clamp(xv.GetDouble(), 0, 1000); break;
                        case "master": if (root.TryGetProperty("value", out var mv)) DjMasterKnob.Value = Math.Clamp(mv.GetDouble(), 0, 125) / 100.0; break;
                        case "cruise": if (_cruiseOn) TurnCruiseOff("\U0001F6A2 Cruise off (phone)."); else StartCruise(); break;
                        case "mic": MicButton_Click(this, new RoutedEventArgs()); break;
                        case "pad": if (root.TryGetProperty("index", out var pi)) TriggerPad(pi.GetInt32()); break;
                    }
                }
                catch { }
            }
            _companionStateJson = BuildCompanionState();
        }

        private string BuildCompanionState()
        {
            static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
            string Deck(DeckPanel p) => FormattableString.Invariant(
                $"{{\"title\":\"{Esc(p.CurrentTitle)}\",\"playing\":{(p.IsPlayingDeck ? "true" : "false")},\"pos\":{p.DeckPositionSeconds:0.0},\"dur\":{p.DeckTotalSeconds:0.0},\"key\":\"{Esc(p.KeyDisplayText)}\"}}");
            return FormattableString.Invariant(
                $"{{\"a\":{Deck(_panelA)},\"b\":{Deck(_panelB)},\"xfader\":{XfadeSlider.Value:0},\"master\":{DjMasterKnob.Value * 100:0},\"cruise\":{(_cruiseOn ? "true" : "false")},\"mic\":{(_micOn ? "true" : "false")}}}");
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
            private readonly Button _stemBtn;
            private readonly TextBlock _stemStatus;
            private readonly StackPanel _stemChipRow;
            private readonly Button[] _stemChips = new Button[4];
            private readonly bool[] _stemOn = { true, true, true, true };
            private CancellationTokenSource? _stemCts;
            private string _stemKey = "";
            private int _stemGeneration;
            private bool _stemBusy;
            private readonly Button _keySyncBtn;
            private readonly TextBlock _keyText;
            internal KeyDetector.KeyResult? DetectedKey;
            internal int KeyShiftSemitones;
            private float[]? _pristine;
            private float[][]? _pristineStems;
            private bool _keyBusy;
            private bool _quantize = true;
            private readonly Button _quantizeBtn;
            private int _loadGen;
            internal string PanelName => _name;
            internal int TempoSemitones => (int)Math.Round(12 * Math.Log2(Math.Max(0.5, _deck.Tempo)));
            internal int EffectivePitchClass => DetectedKey == null ? -1 : KeyDetector.Mod12(DetectedKey.PitchClass + KeyShiftSemitones + TempoSemitones);
            internal string? EffectiveCamelot => DetectedKey == null ? null : KeyDetector.CamelotFor(EffectivePitchClass, DetectedKey.IsMinor);
            internal void SetKeyTextBrush(Brush b) { _keyText.Foreground = b; }

            internal void UpdateKeyLabel()
            {
                if (DetectedKey == null) { _keyText.Text = "not detected"; return; }
                string suffix = KeyShiftSemitones != 0 ? $" ({KeyShiftSemitones:+0;-0} st)" : "";
                _keyText.Text = $"{KeyDetector.KeyNameFor(EffectivePitchClass, DetectedKey.IsMinor)} - {EffectiveCamelot}{suffix}";
            }
            private readonly TrackFxSettings _fx;
            private readonly string _name;
            private readonly SolidColorBrush _accent;
            private readonly VolumeSampleProvider _channelGain;
            private KnobControl? _gainKnob;

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
            private Button? _slipBtn;
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
            private float[] _peakCaps = new float[48];
            private float[] _dotPos = new float[48];
            private float[] _dotVel = new float[48];
            // v3.42: click the visual to cycle spectrum density (bars across the same range).
            private static readonly int[] SpectrumSteps = { 28, 48, 84 };
            private int _spectrumBands = 48;

            private void CycleSpectrumDensity()
            {
                int idx = Array.IndexOf(SpectrumSteps, _spectrumBands);
                _spectrumBands = SpectrumSteps[(idx + 1) % SpectrumSteps.Length];
                _peakCaps = new float[_spectrumBands];
                _dotPos = new float[_spectrumBands];
                _dotVel = new float[_spectrumBands];
                _waterHistory.Clear();
                string label = _spectrumBands == 28 ? "FAT bars (bass-heavy)" : _spectrumBands == 84 ? "FINE bars (full detail)" : "NORMAL bars";
                _window.SetStatus($"{_name} spectrum: {label} — {_spectrumBands} bars. Click the visual again to change it.");
            }
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

                var root = new StackPanel
                {
                    Background = new LinearGradientBrush
                    {
                        StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
                        GradientStops = new GradientStopCollection
                        {
                            new GradientStop(Color.FromRgb(0x17, 0x1B, 0x26), 0),
                            new GradientStop(Color.FromRgb(0x0D, 0x10, 0x17), 1)
                        }
                    }
                };

                _title = new TextBlock { Text = $"{name} — empty", FontSize = 14, FontWeight = FontWeights.Bold, Foreground = accent, Margin = new Thickness(0, 0, 0, 6) };
                root.Children.Add(_title);

                // v3.40 compact board: two-column zones so the whole deck fits
                // on screen at once — no scrolling. Platter zone on the left,
                // control zone on the right, performance rows below, then the
                // track list full width.
                var topGrid = new Grid { Margin = new Thickness(0, 2, 0, 0) };
                topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
                topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var leftStack = new StackPanel();
                var rightStack = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
                Grid.SetColumn(rightStack, 1);
                topGrid.Children.Add(leftStack);
                topGrid.Children.Add(rightStack);
                root.Children.Add(topGrid);
                var midGrid = new Grid { Margin = new Thickness(0, 2, 0, 0) };
                midGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                midGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var midL = new StackPanel();
                var midR = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
                Grid.SetColumn(midR, 1);
                midGrid.Children.Add(midL);
                midGrid.Children.Add(midR);
                root.Children.Add(midGrid);

                // The platter is built at a fixed 198px — a Viewbox scales it
                // down so the whole deck fits smaller / scaled-up screens.
                leftStack.Children.Add(new Viewbox
                {
                    Child = BuildPlatter(),
                    Height = 160,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center
                });

                var scratchRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) };
                _scratchBtn = MakeButton("✋ Scratch: OFF", 170);
                _scratchBtn.ToolTip = "Scratch mode: grab the vinyl and move it with the mouse. Off by default so the record can't be grabbed by accident.";
                _scratchBtn.Click += (s, e) => SetScratch(!_scratchOn);
                scratchRow.Children.Add(_scratchBtn);
                _slipBtn = MakeButton("\U0001F300 Slip: OFF", 110);
                _slipBtn.Margin = new Thickness(6, 0, 0, 0);
                _slipBtn.ToolTip = "Slip mode: while you scratch, the song keeps its place underneath. Release and you land exactly where the song would have been.";
                _slipBtn.Click += (s, e) => SetSlip(!_deck.SlipMode);
                scratchRow.Children.Add(_slipBtn);
                leftStack.Children.Add(scratchRow);

                // Waveform + playhead + scrub
                var waveGrid = new Grid { Height = 64, Background = new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x1E)) };
                _waveImage = new Image { Stretch = Stretch.Fill };
                waveGrid.Children.Add(_waveImage);
                _waveCanvas = new Canvas { Background = Brushes.Transparent };
                _playheadLine = new Line { Y1 = 0, Y2 = 64, Stroke = accent, StrokeThickness = 2 };
                _waveCanvas.Children.Add(_playheadLine);
                _waveCanvas.MouseLeftButtonDown += Wave_Mouse;
                _waveCanvas.MouseMove += Wave_Mouse;
                waveGrid.Children.Add(_waveCanvas);
                leftStack.Children.Add(waveGrid);

                _timeText = new TextBlock { Text = "0:00 / 0:00", FontSize = 11, Foreground = MutedText, Margin = new Thickness(0, 4, 0, 2) };
                leftStack.Children.Add(_timeText);

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
                rightStack.Children.Add(visRow);

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
                rightStack.Children.Add(styleRow);
                _visualBorder = new Border { Height = 56, Margin = new Thickness(0, 4, 0, 0), Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0D, 0x13)), CornerRadius = new CornerRadius(8), Cursor = Cursors.Hand, ToolTip = "This deck's live visual. Click it to change the spectrum density (fat / normal / fine bars) — works on all the spectrum styles." };
                _visualBorder.MouseLeftButtonDown += (s, e) => CycleSpectrumDensity();
                _visualImage = new Image { Stretch = Stretch.Fill };
                _visualBorder.Child = _visualImage;
                rightStack.Children.Add(_visualBorder);

                // Load row
                var loadRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
                _loadPicker = new ComboBox { Width = 210, VerticalAlignment = VerticalAlignment.Center };
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
                rightStack.Children.Add(loadRow);

                // Stems (v3.25): split the loaded track into 4 synced stems, toggled live
                var stemRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
                var stemLabel = new TextBlock { Text = "Stems", Foreground = MutedText, FontSize = 11, Width = 72, VerticalAlignment = VerticalAlignment.Center };
                _stemBtn = MakeButton("\U0001F9EC Split Stems", 118);
                _stemBtn.Click += async (s, e) => await SplitStemsAsync();
                _stemStatus = new TextBlock { Foreground = MutedText, FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
                stemRow.Children.Add(stemLabel);
                stemRow.Children.Add(_stemBtn);
                stemRow.Children.Add(_stemStatus);
                rightStack.Children.Add(stemRow);
                _stemChipRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(72, 4, 0, 0), Visibility = Visibility.Collapsed };
                string[] chipNames = { "\U0001F3A4 Vocals", "\U0001F941 Drums", "\U0001F3B8 Bass", "\U0001F3B9 Other" };
                int[] chipStem = { 3, 0, 1, 2 };
                for (int i = 0; i < 4; i++)
                {
                    int chip = i, stemIdx = chipStem[i];
                    var b = MakeButton(chipNames[i], 76);
                    b.Margin = new Thickness(0, 0, 6, 0);
                    b.Background = _accent;
                    b.Click += (s, e) =>
                    {
                        _stemOn[chip] = !_stemOn[chip];
                        _deck.SetStemOn(stemIdx, _stemOn[chip]);
                        b.Background = _stemOn[chip] ? _accent : PanelBtn;
                    };
                    _stemChips[i] = b;
                    _stemChipRow.Children.Add(b);
                }
                rightStack.Children.Add(_stemChipRow);

                // Transport row
                var transport = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2) };
                _playButton = MakeButton("▶ PLAY", 84);
                _playButton.Background = accent;
                _playButton.Click += (s, e) => PlayPause();
                transport.Children.Add(_playButton);
                var cueBtn = MakeButton("CUE", 56);
                cueBtn.Margin = new Thickness(6, 0, 0, 0);
                cueBtn.Click += (s, e) => Cue();
                transport.Children.Add(cueBtn);
                var syncBtn = MakeButton("SYNC", 56);
                syncBtn.Margin = new Thickness(6, 0, 0, 0);
                syncBtn.Click += (s, e) => SyncToOther();
                transport.Children.Add(syncBtn);
                var fxBtn = MakeButton("🎛️ FX", 56);
                fxBtn.Margin = new Thickness(6, 0, 0, 0);
                fxBtn.Click += (s, e) => _window.OpenFx(_fx, _name);
                transport.Children.Add(fxBtn);
                var pflBtn = MakeButton("🎧 PFL", 56);
                pflBtn.Margin = new Thickness(6, 0, 0, 0);
                pflBtn.IsEnabled = false;
                pflBtn.ToolTip = "Headphone cueing needs a second audio output — it unlocks when the hardware is there.";
                transport.Children.Add(pflBtn);
                rightStack.Children.Add(transport);

                // Tempo row
                var tempoRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
                tempoRow.Children.Add(new TextBlock { Text = "Tempo", Foreground = MutedText, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Width = 42 });
                var bendDown = MakeButton("−", 34);
                bendDown.PreviewMouseLeftButtonDown += (s, e) => _deck.Bend = 0.98;
                bendDown.PreviewMouseLeftButtonUp += (s, e) => _deck.Bend = 1.0;
                bendDown.MouseLeave += (s, e) => _deck.Bend = 1.0;
                tempoRow.Children.Add(bendDown);
                _tempoSlider = new Slider { Minimum = -8, Maximum = 8, Value = 0, Width = 140, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) };
                _tempoSlider.Style = _window.TryFindResource("HwSliderH") as Style;
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
                midL.Children.Add(tempoRow);

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
                midL.Children.Add(bpmRow);

                // Key row (v3.28): detected key + Camelot, key sync, wheel
                var keyRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
                keyRow.Children.Add(new TextBlock { Text = "Key", Foreground = MutedText, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Width = 42 });
                _keyText = new TextBlock { Text = "-", Foreground = MutedText, FontSize = 11, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Width = 118 };
                keyRow.Children.Add(_keyText);
                _keySyncBtn = MakeButton("\U0001F3B5 Key Sync", 92);
                _keySyncBtn.Click += async (s, e) => await ToggleKeySyncAsync();
                keyRow.Children.Add(_keySyncBtn);
                var wheelBtn = MakeButton("\U0001F3A1", 34);
                wheelBtn.Margin = new Thickness(6, 0, 0, 0);
                wheelBtn.Click += (s, e) => _window.ShowCamelotWheel(this);
                keyRow.Children.Add(wheelBtn);
                midL.Children.Add(keyRow);

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
                midR.Children.Add(hotRow);

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
                midR.Children.Add(loopRow);

                // Beat jump row (v3.30): whole-beat jumps + quantize toggle
                var jumpRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
                jumpRow.Children.Add(new TextBlock { Text = "Jump", Foreground = MutedText, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Width = 48 });
                foreach (int jb in new[] { -16, -8, -4, 4, 8, 16 })
                {
                    int beatsJump = jb;
                    var jbtn = MakeButton(jb > 0 ? $"+{jb}" : $"{jb}", 40);
                    jbtn.Margin = new Thickness(4, 0, 0, 0);
                    jbtn.Click += (s, e) => BeatJump(beatsJump);
                    jbtn.ToolTip = $"Beat jump: skip {(beatsJump > 0 ? "forward" : "back")} {Math.Abs(beatsJump)} beats from the playhead. Needs the BPM.";
                    jumpRow.Children.Add(jbtn);
                }
                _quantizeBtn = MakeButton("Quantize: ON", 96);
                _quantizeBtn.Margin = new Thickness(10, 0, 0, 0);
                _quantizeBtn.Background = _accent;
                _quantizeBtn.ToolTip = "Quantize: hot cues, the CUE point and beat jumps snap to the beat grid (anchored at the track start). Needs the BPM.";
                _quantizeBtn.Click += (s, e) =>
                {
                    _quantize = !_quantize;
                    _quantizeBtn.Content = _quantize ? "Quantize: ON" : "Quantize: OFF";
                    _quantizeBtn.Background = _quantize ? _accent : PanelBtn;
                };
                jumpRow.Children.Add(_quantizeBtn);
                root.Children.Add(jumpRow);

                // Gain
                var gainRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
                gainRow.Children.Add(new TextBlock { Text = "Gain", Foreground = MutedText, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Width = 48 });
                _gainKnob = new KnobControl { Minimum = 0, Maximum = 1.25, Value = 1.0, DefaultValue = 1.0, Width = 56, Height = 56, Accent = _accent, VerticalAlignment = VerticalAlignment.Center };
                _gainKnob.ValueChanged += (s, e) => _channelGain.Volume = (float)_gainKnob.Value;
                gainRow.Children.Add(_gainKnob);
                midR.Children.Add(gainRow);

                // Track list for this deck: queue songs, step through them, mix on
                var qHead = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
                qHead.Children.Add(new TextBlock { Text = "🎧 TRACK LIST", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = (Brush)_accent });
                _queueCount = new TextBlock { Text = "", Foreground = MutedText, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
                qHead.Children.Add(_queueCount);
                root.Children.Add(qHead);
                _queueList = new ListBox
                {
                    Height = 52,
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

                // v3.40: hover help on every control in the deck.
                _playButton.ToolTip = "Play / pause this deck.";
                cueBtn.ToolTip = "CUE: return to the cue point, or set it where the song is paused.";
                syncBtn.ToolTip = "SYNC: match this deck's tempo to the other deck's BPM. Needs the BPM set on both decks.";
                fxBtn.ToolTip = "FX: this deck's EQ, delay and reverb — the full effects window (the mixer's channel knobs drive the same EQ).";
                waveGrid.ToolTip = "Waveform: click or drag to jump to that part of the song.";
                loadBtn.ToolTip = "Load the song picked in the list on the left onto this deck.";
                fileBtn.ToolTip = "Browse for any WAV / MP3 file and load it onto this deck.";
                _loadPicker.ToolTip = "Songs from your library and generated tracks — pick one, then press Load.";
                _stemBtn.ToolTip = "Split Stems: separate this song into vocals, drums, bass and other so you can remix the parts live. The first run downloads the AI model.";
                for (int i = 0; i < 4; i++) _stemChips[i].ToolTip = "Turn this part of the song on or off, live, while it plays.";
                _visualPicker.ToolTip = "The live visual style for this deck's display (this deck's own sound).";
                _platterPicker.ToolTip = "Platter style: spinning vinyl or a cassette deck.";
                _colorPicker.ToolTip = "Color palette for this deck's visuals.";
                bendDown.ToolTip = "Hold to nudge the tempo down for a moment (pitch bend) — for lining up the beats by ear.";
                bendUp.ToolTip = "Hold to nudge the tempo up for a moment (pitch bend).";
                _tempoSlider.ToolTip = "Tempo slider: speed this deck up or slow it down (-8% to +8%).";
                resetBtn.ToolTip = "Reset the tempo to exactly 0%.";
                _bpmBox.ToolTip = "The song's BPM (beats per minute). Type it if you know it, or use TAP. SYNC, loops and quantize need this.";
                tapBtn.ToolTip = "Tap along to the beat (at least 4 taps) to set the BPM by ear.";
                _keySyncBtn.ToolTip = "Key Sync: shift this song's pitch so its key matches the other deck, keeping the tempo.";
                wheelBtn.ToolTip = "Camelot wheel: see which keys mix cleanly with this song's key.";
                for (int i = 0; i < 4; i++) _hotButtons[i].ToolTip = $"Hot cue {i + 1}: the first press remembers this spot in the song; later presses jump straight back to it. Right-click clears it.";
                for (int i = 0; i < 4; i++) _loopButtons[i].ToolTip = "Loop: repeat a section of the song, counted in beats (Off stops the loop). Needs the BPM.";
                _gainKnob.ToolTip = "This deck's channel gain — the same volume as the mixer's channel gain knob and fader for this deck. Double-click any knob to reset it.";
                _queueList.ToolTip = "This deck's track list. Double-click a song to load and play it.";
                addBtn.ToolTip = "Add songs to this deck's track list.";
                prevBtn.ToolTip = "Load the previous song in the track list.";
                nextBtn.ToolTip = "Load the next song in the track list.";
                removeBtn.ToolTip = "Remove the selected song from the track list.";
                _autoNextBox.ToolTip = "When this song ends, automatically load and play the next one in the track list.";

                ApplyHardwareStyles(root);
                host.Child = root;
            }

            /// <summary>v3.33 board skin: every button in the panel wears the
            /// hardware style; code-set backgrounds (stem chips, hot cues,
            /// play state) still drive the fill through the template.</summary>
            private void ApplyHardwareStyles(DependencyObject node)
            {
                var style = _window.TryFindResource("HwButton") as Style;
                void Walk(DependencyObject n)
                {
                    if (n is Button b && b.Style == null && style != null) b.Style = style;
                    if (n is Panel p) { foreach (var c in p.Children) { if (c is DependencyObject dep) Walk(dep); } }
                    else if (n is Border bd) { if (bd.Child != null) Walk(bd.Child); }
                    else if (n is ContentControl cc) { if (cc.Content is DependencyObject cd) Walk(cd); }
                }
                Walk(node);
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

                _vinylView = new Grid();
                // The halo belongs INSIDE the vinyl view: it used to sit on the
                // host directly, so switching to the cassette left the big
                // black neon disc floating behind the tape (Michael, v3.16).
                var halo = new Ellipse
                {
                    Width = 190, Height = 190,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    Fill = new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0C)),
                    Stroke = _accent, StrokeThickness = 3,
                    Effect = _haloFx = new DropShadowEffect { Color = _accent.Color, BlurRadius = 26, ShadowDepth = 0, Opacity = 0.85 }
                };
                _vinylView.Children.Add(halo);
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
                foreach (double d in new[] { 162.0, 144.0, 126.0, 108.0, 90.0 })
                {
                    vinyl.Children.Add(new Ellipse
                    {
                        Width = d, Height = d,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                        Stroke = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)), StrokeThickness = 3
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
                        Width = isLeft ? 44 : 26, Height = isLeft ? 44 : 26,
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
                    // One accent marker dot near the rim: the 3-spoke pattern is
                    // symmetrical, so without a marker the spin reads as static
                    // (wagon-wheel effect). The dot makes rotation unmistakable.
                    spokes.Children.Add(new Ellipse
                    {
                        Width = 8, Height = 8, Fill = _accent,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(0, 2, 0, 0)
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

            // v3.37: mixer channel-strip glue — the strip's gain knob/fader drive
            // the same channel volume as this panel's own gain knob.
            internal double ChannelVolume => _channelGain.Volume;
            internal float TapLevel => _tap.Level;
            internal void SetChannelVolume(double v)
            {
                _channelGain.Volume = (float)v;
                if (_gainKnob != null && Math.Abs(_gainKnob.Value - v) > 0.005) _gainKnob.Value = v;
            }

            private void SetSlip(bool on)
            {
                if (_slipBtn == null) return;
                _deck.SlipMode = on;
                _slipBtn.Content = on ? "\U0001F300 Slip: ON" : "\U0001F300 Slip: OFF";
                _slipBtn.Background = on ? _accent : PanelBtn;
                if (on) _window.SetStatus($"{_name}: slip mode on - scratch away; on release you land where the song kept playing to.");
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
                _window.CruiseManualOverride();
                if (_loadPicker.SelectedItem is not string name) { _window.SetStatus("The library is empty — import a song in the main window first, or use 📁 File…"); return; }
                if (!_window._owner.ZipSounds.TryGetValue(name, out var bytes)) return;
                LoadBytes(bytes, name);
            }

            /// <summary>Auto-gained spectrum bands (0..1) — raw FFT values run small.</summary>
            private double[] ScaledBands()
            {
                var bands = _tap.GetSpectrum(_spectrumBands);
                var scaled = new double[bands.Length];
                float frameMax = 0;
                for (int i = 0; i < bands.Length; i++) frameMax = Math.Max(frameMax, bands[i]);
                _specPeak = Math.Max(Math.Max(frameMax, _specPeak * 0.994f), 0.12f);
                for (int i = 0; i < bands.Length; i++)
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
                int n = v.Length;
                double bw = W / (double)n;

                switch (mode)
                {
                    case 0: // Spectrum bars in the deck color
                        for (int i = 0; i < n; i++)
                        {
                            double h = Math.Max(1, v[i] * (H - 4));
                            dc.DrawRectangle((Brush)viz, null, new Rect(i * bw + 1, H - h, bw - 2, h));
                        }
                        break;

                    case 1: // Rainbow LED blocks — hue sweeps red→orange across the bands
                    {
                        const int segH = 4, segGap = 2, rows = 8;
                        for (int i = 0; i < n; i++)
                        {
                            int lit = (int)Math.Round(v[i] * rows);
                            double hue = (360.0 - (i / (double)(n - 1)) * 325.0 + _hueShift + 360.0) % 360.0;
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
                        for (int i = 0; i < n; i++)
                        {
                            double h = Math.Max(1, v[i] * (H - 6));
                            dc.DrawRectangle((Brush)viz, null, new Rect(i * bw + 1, H - h, bw - 2, h));
                            _peakCaps[i] = Math.Max((float)v[i], _peakCaps[i] - 0.008f);
                            double capY = H - 2 - _peakCaps[i] * (H - 6);
                            dc.DrawRectangle(Brushes.White, null, new Rect(i * bw + 1, capY, bw - 2, 2));
                        }
                        break;

                    case 3: // Mirrored spectrum — bars grow both ways from the center line
                        for (int i = 0; i < n; i++)
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
                        for (int i = 0; i < n; i++)
                        {
                            double a = i / (double)n * Math.PI * 2 - Math.PI / 2;
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
                        int half = n / 2;
                        double vw = W / (double)half;
                        for (int i = 0; i < half; i++)
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
                        var frame = new float[n];
                        for (int i = 0; i < n; i++) frame[i] = (float)v[i];
                        _waterHistory.Insert(0, frame);
                        while (_waterHistory.Count > 12) _waterHistory.RemoveAt(_waterHistory.Count - 1);
                        double cellH = H / 12.0;
                        for (int r = 0; r < _waterHistory.Count; r++)
                        {
                            var row = _waterHistory[r];
                            for (int i = 0; i < n; i++)
                            {
                                if (row[i] < 0.04) continue;
                                var brush = new SolidColorBrush(viz.Color) { Opacity = Math.Clamp(row[i], 0f, 1f) * (1 - r / 16.0) };
                                dc.DrawRectangle(brush, null, new Rect(i * bw + 0.5, r * cellH + 0.5, bw - 1, cellH - 1));
                            }
                        }
                        break;
                    }

                    case 10: // Falling dots — dim bars with bright dots that drop with gravity
                        for (int i = 0; i < n; i++)
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
                _window.CruiseManualOverride();
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
                    FinishLoad(bytes, name);
                    return true;
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Could not decode that song: " + ex.Message, _name);
                    return false;
                }
            }

            private void FinishLoad(byte[] bytes, string name)
            {
                _lastBytes = bytes;
                _lastName = name;
                _tempoSlider.Value = 0;
                _bpmBox.Text = "";
                _title.Text = $"{_name} — {_deck.Title}";
                RenderWaveform();
                RenderPlatterRing();
                _window.SetStatus($"{_name}: {_deck.Title} loaded ({FmtTime(_deck.TotalSeconds)}). Set the BPM to unlock SYNC and loops.");
                _stemKey = StemBaseName(name) + "-" + bytes.Length;
                _stemGeneration++;
                ShowStemChips(false, "");
                _ = TryLoadCachedStemsAsync();
                StartKeyDetection();
            }

            private static string StemBaseName(string title)
            {
                var sb = new System.Text.StringBuilder();
                foreach (char c in title) sb.Append(char.IsLetterOrDigit(c) || c == ' ' || c == '-' ? c : '_');
                string s = sb.ToString().Trim();
                return s.Length == 0 ? "track" : s;
            }

            private static string StemCacheFolder(string key) => System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BeatSyncStudioPro", "stems", key);

            private static bool StemCacheExists(string folder, string baseName)
            {
                foreach (var stem in StemSeparator.StemNames)
                    if (!File.Exists(System.IO.Path.Combine(folder, baseName + " (" + stem + ").wav"))) return false;
                return true;
            }

            private void ShowStemChips(bool ready, string status)
            {
                _stemChipRow.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
                if (ready)
                    for (int i = 0; i < 4; i++) { _stemOn[i] = true; _stemChips[i].Background = _accent; }
                _stemStatus.Text = status;
            }

            private async Task TryLoadCachedStemsAsync()
            {
                try
                {
                    int gen = ++_stemGeneration;
                    string key = _stemKey;
                    if (key.Length == 0) return;
                    string baseName = StemBaseName(_deck.Title);
                    string folder = StemCacheFolder(key);
                    if (!StemCacheExists(folder, baseName)) return;
                    _stemStatus.Text = "loading cached stems...";
                    bool ok = await Task.Run(() => _deck.LoadStemsFromFolder(folder, baseName));
                    if (gen != _stemGeneration) return;
                    ShowStemChips(ok, ok ? "stems ready (cached)" : "");
                    if (ok) StemSeparator.NotifyStemsAvailable(folder, baseName);
                }
                catch
                {
                    ShowStemChips(false, "cached stems failed to load");
                }
            }

            private async Task SplitStemsAsync()
            {
                if (_stemBusy) return;
                if (!_deck.IsLoaded) { _stemStatus.Text = "load a track first"; return; }
                string baseName = StemBaseName(_deck.Title);
                string folder = StemCacheFolder(_stemKey.Length > 0 ? _stemKey : baseName);
                if (StemCacheExists(folder, baseName))
                {
                    _stemStatus.Text = "loading cached stems...";
                    bool okc;
                    try { okc = await Task.Run(() => _deck.LoadStemsFromFolder(folder, baseName)); }
                    catch { okc = false; }
                    ShowStemChips(okc, okc ? "stems ready (cached)" : "cached stems failed to load");
                    if (okc) StemSeparator.NotifyStemsAvailable(folder, baseName);
                    return;
                }
                string modelDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BeatSyncStudioPro", "models");
                string modelPath = System.IO.Path.Combine(modelDir, "htdemucs.onnx");
                if (!File.Exists(modelPath))
                {
                    var ask = MessageBox.Show("The stem splitter AI model (~304 MB) is not downloaded yet.\n\nDownload it once now?", "BeatSync Studio Pro", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (ask != MessageBoxResult.Yes) { _stemStatus.Text = "stem model not downloaded"; return; }
                    try
                    {
                        Directory.CreateDirectory(modelDir);
                        _stemStatus.Text = "downloading stem model...";
                        await DownloadManager.DownloadToFileAsync("https://huggingface.co/smank/htdemucs-onnx/resolve/main/htdemucs.onnx", "HTDemucs AI model (stem splitter)", modelPath,
                            item => { _stemStatus.Text = $"downloading stem model... {item.Percent:0}%"; });
                    }
                    catch (Exception ex) { _stemStatus.Text = "model download failed: " + ex.Message; return; }
                }
                _stemBusy = true;
                _stemBtn.IsEnabled = false;
                _stemCts?.Cancel();
                _stemCts = new CancellationTokenSource();
                int gen = ++_stemGeneration;
                try
                {
                    Directory.CreateDirectory(folder);
                    var (left, right) = _deck.GetChannelBuffers();
                    var progress = new Progress<double>(p => { if (gen == _stemGeneration) _stemStatus.Text = $"splitting stems... {p * 100:0}%"; });
                    await StemSeparator.SplitAsync(left, right, modelPath, folder, baseName, progress, _stemCts.Token);
                    if (gen != _stemGeneration) return;
                    bool ok = await Task.Run(() => _deck.LoadStemsFromFolder(folder, baseName));
                    if (gen != _stemGeneration) return;
                    ShowStemChips(ok, ok ? $"stems ready (ran on {StemSeparator.LastComputeUsed})" : "split finished but the stems failed to load");
                }
                catch (OperationCanceledException) { _stemStatus.Text = "stem split cancelled"; }
                catch (Exception ex) { _stemStatus.Text = "stem split failed: " + ex.Message; }
                finally { _stemBusy = false; _stemBtn.IsEnabled = true; }
            }

            private void StartKeyDetection()
            {
                int gen = ++_loadGen;
                DetectedKey = null;
                KeyShiftSemitones = 0;
                _pristine = null;
                _pristineStems = null;
                _keySyncBtn.Background = PanelBtn;
                _keyText.Text = "detecting...";
                _keyText.Foreground = MutedText;
                string cacheKey = _stemKey;
                _ = Task.Run(() =>
                {
                    KeyDetector.KeyResult? result = null;
                    var cache = DjWindow.LoadKeyCache();
                    if (cacheKey.Length > 0 && cache.TryGetValue(cacheKey, out var hit))
                        result = new KeyDetector.KeyResult(hit.Pc, hit.Minor, KeyDetector.KeyNameFor(hit.Pc, hit.Minor), KeyDetector.CamelotFor(hit.Pc, hit.Minor), 1);
                    if (result == null)
                    {
                        var (l, r) = _deck.GetChannelBuffers();
                        var mono = new float[l.Length];
                        for (int i = 0; i < l.Length; i++) mono[i] = (l[i] + r[i]) * 0.5f;
                        result = KeyDetector.Detect(mono);
                        if (result != null && cacheKey.Length > 0)
                        {
                            cache[cacheKey] = new KeyCacheEntry(result.PitchClass, result.IsMinor);
                            DjWindow.SaveKeyCache(cache);
                        }
                    }
                    var res = result;
                    _window.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (gen != _loadGen) return;
                        DetectedKey = res;
                        if (res == null) _keyText.Text = "not detected";
                        _window.RefreshKeyCompatibility();
                    }));
                });
            }

            private async Task ToggleKeySyncAsync()
            {
                if (_keyBusy) return;
                if (KeyShiftSemitones != 0)
                {
                    if (_pristine != null) _deck.SwapBuffers(_pristine, _pristineStems);
                    KeyShiftSemitones = 0;
                    _pristine = null;
                    _pristineStems = null;
                    _keySyncBtn.Background = PanelBtn;
                    _window.SetStatus($"{_name}: key sync off - original key restored.");
                    _window.RefreshKeyCompatibility();
                    return;
                }
                var otherPanel = _window.OtherPanel(this);
                if (DetectedKey == null) { _window.SetStatus($"{_name}: key sync needs a detected key on this deck first."); return; }
                if (otherPanel.DetectedKey == null) { _window.SetStatus($"{_name}: key sync needs the other deck's key too - load a track there."); return; }
                int dd = KeyDetector.Mod12(otherPanel.EffectivePitchClass - DetectedKey.PitchClass);
                int shift = dd > 6 ? dd - 12 : dd;
                if (shift == 0) { _window.SetStatus($"{_name}: already in key with the other deck."); return; }
                _keyBusy = true;
                _keySyncBtn.IsEnabled = false;
                int gen = _loadGen;
                int deckGen = _deck.LoadGeneration;
                try
                {
                    _window.SetStatus($"{_name}: shifting key {shift:+0;-0} semitones - rendering...");
                    var mix = _deck.SnapshotInterleaved();
                    var stems = _deck.SnapshotStemsInterleaved();
                    var shiftedMix = await Task.Run(() => PitchShifter.Shift(mix, shift));
                    float[][]? shiftedStems = null;
                    if (stems != null)
                    {
                        var ss = stems;
                        shiftedStems = await Task.Run(() => ss.Select(b => PitchShifter.Shift(b, shift)).ToArray());
                    }
                    if (gen != _loadGen || deckGen != _deck.LoadGeneration)
                    {
                        _window.SetStatus($"{_name}: key shift discarded - the track changed.");
                        return;
                    }
                    _pristine = mix;
                    _pristineStems = stems;
                    _deck.SwapBuffers(shiftedMix, shiftedStems);
                    KeyShiftSemitones = shift;
                    _keySyncBtn.Background = _accent;
                    _window.SetStatus($"{_name}: key synced {shift:+0;-0} semitones to match the other deck.");
                    _window.RefreshKeyCompatibility();
                }
                catch (Exception ex) { _window.SetStatus($"{_name}: key shift failed: " + ex.Message); }
                finally { _keyBusy = false; _keySyncBtn.IsEnabled = true; }
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

            internal System.Collections.Generic.IReadOnlyList<string> QueuePathsSnapshot => _queuePaths.ToArray();
            internal string CurrentTitle => _deck.Title;
            internal bool IsPlayingDeck => _deck.IsPlaying;
            internal double RemainingSeconds => _deck.IsLoaded ? Math.Max(0, _deck.TotalSeconds - _deck.PositionSeconds) : 0;

            internal void SetAutoNext(bool on) { if (_autoNextBox != null) _autoNextBox.IsChecked = on; }

            internal async Task<bool> LoadPathForCruiseAsync(string path)
            {
                byte[] bytes;
                string name = System.IO.Path.GetFileName(path);
                try { bytes = await Task.Run(() => File.ReadAllBytes(path)); }
                catch { return false; }
                bool ok = await Task.Run(() => { try { _deck.Load(bytes, name); return true; } catch { return false; } });
                if (!ok) return false;
                FinishLoad(bytes, name);
                return true;
            }

            internal void StartForCruise()
            {
                if (!_deck.IsLoaded) return;
                if (_deck.PositionSeconds >= _deck.TotalSeconds - 0.05) _deck.PositionSeconds = 0;
                _deck.IsPlaying = true;
                _window._owner.EnsureOutputRunning();
            }

            internal void StopForCruise() { _deck.IsPlaying = false; }

            internal void RemotePlayPause() => PlayPause();
            internal void RemoteCue() => Cue();
            internal double DeckPositionSeconds => _deck.PositionSeconds;
            internal double DeckTotalSeconds => _deck.TotalSeconds;
            internal string KeyDisplayText => DetectedKey == null ? "" : $"{KeyDetector.KeyNameFor(EffectivePitchClass, DetectedKey.IsMinor)} - {EffectiveCamelot}";

            // ===== v3.41: MIX A+B support =====
            private byte[]? _lastBytes;
            private string _lastName = "";

            internal bool HasTrack => _deck.IsLoaded;
            internal bool KeyReady => DetectedKey != null;

            internal void SetBpmUi(double bpm)
            {
                _deck.Bpm = bpm;
                _bpmBox.Text = $"{bpm:0}";
            }

            internal async Task<bool> EnsureBpmAsync()
            {
                if (_deck.Bpm > 0) return true;
                if (_lastBytes == null) return false;
                var bytes = _lastBytes;
                string type = System.IO.Path.GetExtension(_lastName).TrimStart('.').ToLowerInvariant();
                var (bpm, _, _) = await Task.Run(() => BeatDetector.Detect(bytes, type));
                if (bpm >= 50 && bpm <= 200) { SetBpmUi(Math.Round(bpm * 2) / 2); return true; }
                return false;
            }

            /// <summary>FIX 10 + MIX A+B: match this deck's tempo to a target
            /// BPM honestly — straight if within ±8%, half/double-time if that
            /// lands in range, otherwise refuse (false) instead of silently
            /// clamping and claiming success.</summary>
            internal bool TrySyncTempoTo(double targetBpm, out string how)
            {
                how = "";
                if (_deck.Bpm <= 0 || targetBpm <= 0) return false;
                double raw = targetBpm / _deck.Bpm;
                double chosen = raw;
                if (raw < 0.92 || raw > 1.08)
                {
                    if (raw * 2 >= 0.92 && raw * 2 <= 1.08) { chosen = raw * 2; how = "double-time"; }
                    else if (raw / 2 >= 0.92 && raw / 2 <= 1.08) { chosen = raw / 2; how = "half-time"; }
                    else return false;
                }
                _tempoSlider.Value = (chosen - 1) * 100;
                return true;
            }

            internal async Task<bool> EnsureKeySyncedAsync()
            {
                if (KeyShiftSemitones != 0) return true;
                if (DetectedKey == null) return false;
                var otherPanel = _window.OtherPanel(this);
                if (otherPanel.DetectedKey == null) return false;
                int dd = KeyDetector.Mod12(otherPanel.EffectivePitchClass - DetectedKey.PitchClass);
                int shift = dd > 6 ? dd - 12 : dd;
                if (shift == 0) return true;
                await ToggleKeySyncAsync();
                return KeyShiftSemitones != 0;
            }

            internal void MixStopAndRewindToCue()
            {
                _deck.IsPlaying = false;
                _deck.PositionSeconds = Math.Min(_deck.CueSeconds, Math.Max(0, _deck.TotalSeconds - 0.05));
            }

            internal void MixPlay()
            {
                if (!_deck.IsLoaded) return;
                _deck.IsPlaying = true;
                _window._owner.EnsureOutputRunning();
            }

            internal void SyncForCruise()
            {
                if (_deck.Bpm > 0 && _other().EffectiveBpm > 0) SyncToOther();
            }

            internal bool LoadForMix(byte[] bytes, string name) => LoadBytes(bytes, name);

            // ===== v3.43: mashup support =====
            internal bool StemsReady => _deck.StemModeActive;

            internal async Task<bool> EnsureStemsAsync()
            {
                if (_deck.StemModeActive) return true;
                await SplitStemsAsync();
                return _deck.StemModeActive;
            }

            /// <summary>Set this deck's stem mix (chips kept in sync). Chip order:
            /// 0 Vocals (stem 3), 1 Drums (0), 2 Bass (1), 3 Other (2).</summary>
            internal void SetStemMix(bool vocals, bool drums, bool bass, bool other)
            {
                bool[] want = { vocals, drums, bass, other };
                int[] stemIdx = { 3, 0, 1, 2 };
                for (int i = 0; i < 4; i++)
                {
                    _stemOn[i] = want[i];
                    _deck.SetStemOn(stemIdx[i], want[i]);
                    _stemChips[i].Background = want[i] ? _accent : PanelBtn;
                }
            }

            private void LoadQueueItem(int i, bool autoplay)
            {
                _window.CruiseManualOverride();
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
                _window.CruiseManualOverride();
                if (!_deck.IsLoaded) return;
                if (!_deck.IsPlaying && _deck.PositionSeconds >= _deck.TotalSeconds - 0.05)
                    _deck.PositionSeconds = 0;
                _deck.IsPlaying = !_deck.IsPlaying;
                if (_deck.IsPlaying) _window._owner.EnsureOutputRunning();
            }

            private void Cue()
            {
                _window.CruiseManualOverride();
                if (!_deck.IsLoaded) return;
                if (_deck.IsPlaying)
                {
                    _deck.PositionSeconds = _deck.CueSeconds;
                    _deck.IsPlaying = false;
                }
                else
                {
                    _deck.CueSeconds = SnapToBeat(_deck.PositionSeconds);
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
                if (TrySyncTempoTo(otherDeck.EffectiveBpm, out string how))
                {
                    _window.SetStatus(how.Length == 0
                        ? $"{_name}: synced to {otherDeck.EffectiveBpm:0.0} BPM."
                        : $"{_name}: synced to {otherDeck.EffectiveBpm:0.0} BPM at {how} — the two BPMs are far apart, so this deck plays at {(how == "half-time" ? "half" : "double")} the beat rate.");
                }
                else
                {
                    _window.SetStatus($"{_name}: can't honestly sync — {_deck.Bpm:0} vs {otherDeck.EffectiveBpm:0.0} BPM is beyond the ±8% tempo range, even at half/double time. Tempo left alone.");
                }
            }

            private void SetTempo(double percent)
            {
                _deck.Tempo = 1 + percent / 100.0;
                _tempoLabel.Text = $"{percent:+0.0;-0.0;0.0}%";
                _window.RefreshKeyCompatibility();
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
                    _deck.HotCues[idx] = SnapToBeat(_deck.PositionSeconds);
                    _window.SetStatus($"{_name}: hot cue {idx + 1} set at {FmtTime(_deck.HotCues[idx] ?? 0)}.");
                }
            }

            private double BeatLenSeconds => _deck.EffectiveBpm > 0 ? 60.0 / _deck.EffectiveBpm : 0;

            private double SnapToBeat(double seconds)
            {
                double bl = BeatLenSeconds;
                if (!_quantize || bl <= 0) return seconds;
                return Math.Round(seconds / bl) * bl;
            }

            private void BeatJump(int beats)
            {
                if (!_deck.IsLoaded) return;
                double bl = BeatLenSeconds;
                if (bl <= 0) { _window.SetStatus($"{_name}: beat jump needs the BPM first - type it or TAP it in."); return; }
                double target = _quantize
                    ? (Math.Round(_deck.PositionSeconds / bl) + beats) * bl
                    : _deck.PositionSeconds + beats * bl;
                _deck.PositionSeconds = Math.Clamp(target, 0, Math.Max(0, _deck.TotalSeconds - 0.05));
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
                    double frac = _deck.TotalSeconds > 0 ? Math.Clamp(_deck.PositionSeconds / _deck.TotalSeconds, 0, 1) : 0;
                    double dl = 44 - 18 * frac, dr = 26 + 18 * frac; // tape drains left → right
                    if (_tapeL != null) { _tapeL.Width = dl; _tapeL.Height = dl; }
                    if (_tapeR != null) { _tapeR.Width = dr; _tapeR.Height = dr; }
                    // Each reel spins at its OWN speed, like a real deck: the
                    // emptier a reel gets, the faster it turns (same tape
                    // speed, smaller spool). Pure function of position, so
                    // scrubbing and scratching turn the reels too.
                    if (_reelSpinL != null) _reelSpinL.Angle = _deck.PositionSeconds * 170.0 * (44.0 / dl) % 360.0;
                    if (_reelSpinR != null) _reelSpinR.Angle = _deck.PositionSeconds * 170.0 * (44.0 / dr) % 360.0;
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
