using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO.Compression;
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
        private readonly StreamAudioSink _streamSink;
        internal StreamEngine Streamer { get; }
        private StreamWindow? _streamWindow;
        private readonly AudioTapSampleProvider _masterTap;
        private readonly DrawingVisual _masterVisual = new DrawingVisual();
        private RenderTargetBitmap? _masterBmp;
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
        // ---------- v3.75: MIDI controller ----------
        private MidiEngine? _midiEngine;
        private Dictionary<string, MidiBinding> _midiMap = new Dictionary<string, MidiBinding>();
        private MidiSettingsWindow? _midiWindow;

        public event Action<string>? MidiActivity;
        public event Action<string, int, int, int>? MidiRaw;

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
        private RequestsWindow? _requestsWindow;
        private int _lastReqCount = -1;
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
            _streamSink = new StreamAudioSink(_masterTap);
            _recorder = new RecordingSampleProvider(_streamSink);
            _owner.AttachDjMixer(_recorder);
            // v3.88: live streaming (Output window video + master audio
            // to RTMP via ffmpeg). The rect provider reads the Output
            // window's screen rectangle from the main window.
            Streamer = new StreamEngine(_streamSink, () =>
                _owner.TryGetOutputWindowScreenRect(out int sx, out int sy, out int sw, out int sh)
                    ? (true, sx, sy, sw, sh) : (false, 0, 0, 0, 0));
            Streamer.EnsureOutputSurface = () => _owner.Dispatcher.Invoke(() => _owner.ShowOutputWindow());
            Streamer.Failed += msg => Dispatcher.BeginInvoke(() =>
            {
                SetStatus("\U0001F534 Live stream stopped: " + msg);
                RefreshStreamButton();
            });
            djMaster.AddMixerInput(_sampler.Output);
            SamplerVolumeKnob.ValueChanged += (s, e) => _sampler.Output.Volume = (float)SamplerVolumeKnob.Value;
            BuildSamplerPads();
            // v3.46: stem compute picker (Auto / GPU / CPU) — the app-wide
            // ComputeSettings the stem splitter already honors.
            _stemPickerInit = true;
            StemComputePicker.SelectedIndex = ComputeSettings.Load().Mode switch { "GPU" => 1, "CPU" => 2, _ => 0 };
            _stemPickerInit = false;
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
            // NOTE: no Source assignment here on purpose (v3.83 fix) —
            // a DrawingImage over DrawingVisual.Drawing freezes on the
            // first frame, because every RenderOpen REPLACES the visual's
            // DrawingGroup. DrawMasterSpectrum renders to a bitmap
            // instead, like the deck visuals do.

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _companion.CommandReceived += json => _companionQueue.Enqueue(json);
            _companion.StateJsonProvider = () => _companionStateJson;
            _timer.Tick += (s, e) =>
            {
                // v3.83: every step guarded on its own. The old bare chain
                // meant one step throwing on a tick silently starved every
                // step after it — the frozen-visuals failure shape. A fault
                // now gets logged once per step (to crash.log, like the
                // app's crash handler) and the rest of the tick still runs.
                SafeTick("panelA", () => _panelA.Refresh());
                SafeTick("panelB", () => _panelB.Refresh());
                SafeTick("cruise", CruiseTick);
                SafeTick("sampler", SamplerFlashTick);
                SafeTick("mic", MicTick);
                SafeTick("mixer", MixerTick);
                SafeTick("companion", DrainCompanion);
                SafeTick("setrec", SetRecordTick);
                SafeTick("stream", StreamTick);
            };
            InitMidi();
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
            Closed += (s, e) => { if (_setRecording) FinishSetRecording(); _timer.Stop(); _recorder.Stop(); _owner.DetachDjMixer(_recorder); _mic.Dispose(); _companion.Stop(); Streamer.Stop(); };
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

        private readonly System.Collections.Generic.HashSet<string> _tickFaultsLogged = new System.Collections.Generic.HashSet<string>();

        /// <summary>Run one DJ timer step; a throwing step is logged
        /// (once per step) instead of silently killing the steps that
        /// come after it on every tick.</summary>
        private void SafeTick(string step, Action run)
        {
            try { run(); }
            catch (Exception ex)
            {
                if (_tickFaultsLogged.Add(step))
                {
                    try
                    {
                        string dir = System.IO.Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BeatSyncStudioPro");
                        System.IO.Directory.CreateDirectory(dir);
                        System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "crash.log"),
                            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] DJ tick step '{step}': {ex}{Environment.NewLine}");
                    }
                    catch { /* logging must never take the tick down with it */ }
                }
            }
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
            // v3.84 ROOT-CAUSE FIX: size the drawing from the strip's
            // BORDER, never from the Image itself. The Image starts
            // life with no sized source, so it measures 0x0 — and the
            // old code read ActualWidth/Height FROM THE IMAGE, hit the
            // <10 early-out on every tick, and never drew a single
            // frame (the years-long black strip). The border always
            // has a real size; once the first bitmap lands, the Image
            // gets a real size too and the strip stays live.
            double w = 0, h = 0;
            if (MasterSpectrumImage.Parent is FrameworkElement host && host.ActualWidth > 20 && host.ActualHeight > 10)
            {
                w = host.ActualWidth - 12;  // the Image's 6,3 margin
                h = host.ActualHeight - 6;
            }
            else
            {
                w = MasterSpectrumImage.ActualWidth;
                h = MasterSpectrumImage.ActualHeight;
            }
            if (w < 10 || h < 10) return;
            var bands = _masterTap.GetSpectrum(_masterBands);
            // v3.85: AUTO-GAIN, same as the deck visuals' ScaledBands.
            // Raw FFT band values run tiny (hundredths), so drawing
            // them raw — as this strip always did — makes sub-pixel
            // bars: a black strip even with the math working. Scale
            // each frame by a slow-decaying peak so the bars dance at
            // any volume.
            float frameMax = 0;
            for (int i = 0; i < bands.Length; i++) frameMax = Math.Max(frameMax, bands[i]);
            _masterSpecPeak = Math.Max(Math.Max(frameMax, _masterSpecPeak * 0.994f), 0.02f);
            using (var dc = _masterVisual.RenderOpen())
            {
                dc.DrawRectangle(SpectrumBgBrush, null, new Rect(0, 0, w, h));
                double bw = w / bands.Length;
                for (int i = 0; i < bands.Length; i++)
                {
                    double scaled = Math.Clamp(Math.Sqrt(bands[i] / _masterSpecPeak) * 0.95, 0, 1);
                    double bh = Math.Max(1, scaled * (h - 2));
                    var brush = SpectrumBrushes[Math.Min(SpectrumBrushes.Length - 1, (int)(scaled * SpectrumBrushes.Length))];
                    dc.DrawRectangle(brush, null, new Rect(i * bw + 0.5, h - bh, Math.Max(1, bw - 1.5), bh));
                }
            }
            // v3.83: present the frame the way the deck visuals do —
            // render the DrawingVisual into a bitmap every tick. The old
            // code handed the Image a DrawingImage over the visual's
            // Drawing ONCE at startup; RenderOpen replaces that Drawing
            // every call, so the strip showed the first (silent) frame
            // forever — a permanently black spectrum while music played.
            int pw = (int)Math.Round(w), ph = (int)Math.Round(h);
            if (_masterBmp == null || _masterBmp.PixelWidth != pw || _masterBmp.PixelHeight != ph)
                _masterBmp = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
            _masterBmp.Render(_masterVisual);
            if (!ReferenceEquals(MasterSpectrumImage.Source, _masterBmp))
                MasterSpectrumImage.Source = _masterBmp;
        }

        private float _masterSpecPeak = 0.02f;

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

        /// <summary>Load a song file straight onto a deck from outside
        /// this window (the 🎵 Song Library). False when unreadable.</summary>
        public bool LoadOnDeck(bool deckA, string path)
        {
            try
            {
                byte[] bytes = System.IO.File.ReadAllBytes(path);
                (deckA ? _panelA : _panelB).LoadForMix(bytes, System.IO.Path.GetFileName(path));
                return true;
            }
            catch { return false; }
        }

        /// <summary>Append a song file to a deck's track list from
        /// outside this window (the 🎵 Song Library).</summary>
        public bool QueueOnDeck(bool deckA, string path)
        {
            try { (deckA ? _panelA : _panelB).QueuePath(path); return true; }
            catch { return false; }
        }

        /// <summary>Load in-memory audio (a streamed song) onto a deck —
        /// the 🎵 Song Library's Suno tab sends streams here.</summary>
        public bool LoadBytesOnDeck(bool deckA, byte[] bytes, string name)
        {
            try { return (deckA ? _panelA : _panelB).LoadForMix(bytes, name); }
            catch { return false; }
        }

        // ---------- v3.75: MIDI controller ----------

        private void InitMidi()
        {
            _midiMap = MidiStore.EffectiveBindings();
            var settings = MidiStore.LoadSettings();
            if (settings.Enabled && settings.DeviceIndex >= 0)
                StartMidiEngine(settings.DeviceIndex);
            Closed += (s, e) => { _midiEngine?.Dispose(); _midiEngine = null; };
        }

        private bool StartMidiEngine(int deviceIndex)
        {
            _midiEngine?.Dispose();
            _midiEngine = new MidiEngine();
            _midiEngine.Message += OnMidiMessage;
            return _midiEngine.Start(deviceIndex);
        }

        private void OnMidiMessage(string kind, int channel, int number, int value)
        {
            string line = kind == "note"
                ? $"Note {number} · channel {channel} · velocity {value}"
                : $"CC {number} · channel {channel} · value {value}";
            Dispatcher.BeginInvoke(new Action(() =>
            {
                MidiRaw?.Invoke(kind, channel, number, value);
                MidiActivity?.Invoke(line);
                DispatchMidi(kind, channel, number, value);
            }));
        }

        private void DispatchMidi(string kind, int channel, int number, int value)
        {
            foreach (var kv in _midiMap)
            {
                var b = kv.Value;
                if (b.Kind != kind || b.Number != number) continue;
                if (b.Channel != 0 && b.Channel != channel) continue;
                string id = kv.Key;
                if (id == "mix.crossfader") XfadeSlider.Value = value / 127.0 * 1000.0;
                else if (id == "mix.faderA") ChAFader.Value = value / 127.0 * 1000.0;
                else if (id == "mix.faderB") ChBFader.Value = value / 127.0 * 1000.0;
                else if (kind == "note" && id == "deckA.play") _panelA.MidiPlayPause();
                else if (kind == "note" && id == "deckA.cue") _panelA.MidiCue();
                else if (kind == "note" && id == "deckB.play") _panelB.MidiPlayPause();
                else if (kind == "note" && id == "deckB.cue") _panelB.MidiCue();
                else if (kind == "note" && id.StartsWith("pad.") && int.TryParse(id.Substring(4), out int pn) && pn >= 1 && pn <= 8) TriggerPad(pn - 1);
            }
        }

        public Dictionary<string, MidiBinding> CurrentMidiMap() => _midiMap;

        public MidiStore.Settings CurrentMidiSettings() => MidiStore.LoadSettings();

        public void SetMidiBinding(string actionId, MidiBinding? binding)
        {
            var overrides = MidiStore.LoadOverrides();
            if (binding == null) overrides.Remove(actionId);
            else overrides[actionId] = binding;
            MidiStore.SaveOverrides(overrides);
            _midiMap = MidiStore.EffectiveBindings();
        }

        public bool ApplyMidiSettings(int deviceIndex, bool enabled)
        {
            MidiStore.SaveSettings(new MidiStore.Settings { DeviceIndex = deviceIndex, Enabled = enabled });
            if (!enabled)
            {
                _midiEngine?.Dispose();
                _midiEngine = null;
                return false;
            }
            return StartMidiEngine(deviceIndex);
        }

        private void MidiButton_Click(object sender, RoutedEventArgs e)
        {
            if (_midiWindow == null)
            {
                _midiWindow = new MidiSettingsWindow(this) { Owner = this };
                _midiWindow.Closed += (s, ev) => _midiWindow = null;
                _midiWindow.Show();
            }
            else _midiWindow.Activate();
        }

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

        private async Task RunMixSetupAsync(bool mashup = false)
        {
            if (_cruiseOn) TurnCruiseOff("🎤 Cruise off — MASHUP took over.");
            SetStatus("🎤 MASHUP: working out the BPMs…");
            bool bpmA = await _panelA.EnsureBpmAsync();
            bool bpmB = await _panelB.EnsureBpmAsync();
            if (!bpmA || !bpmB)
            {
                SetStatus($"🎤 MASHUP stopped — no BPM for {(!bpmA ? "Deck A" : "Deck B")} (auto-detect couldn't find one). Type it in or TAP it, then press MASHUP again.");
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
            {
                string captureNote = StartMashupCapture()
                    ? " It's being captured while it plays — if it's a keeper, press 💾 Save Mashup and it lands in the library under 🔀 Mash-ups."
                    : " (Record Mix is already running, so this mashup isn't being auto-captured.)";
                SetStatus($"🎤 MASHUP A+B: Deck A's VOCALS over Deck B's BEAT (drums+bass+other) • {tempoNote} • {keyNote} • crossfader centered.{captureNote} Want it the other way round? Swap the two tracks and press MASHUP again.");
            }
        }

        // ===== v3.46: mashup capture — every mashup is recorded as it plays;
        // 💾 Save Mashup files the keeper into the library's Mash-ups category =====
        private bool _mashupCapture;
        private string? _mashupTempPath;
        private string _mashupTitleA = "";
        private string _mashupTitleB = "";

        private bool StartMashupCapture()
        {
            if (_recorder.IsRecording && !_mashupCapture) return false;   // a manual Record Mix owns the recorder
            try
            {
                if (_mashupCapture) _recorder.Stop();
                string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BeatSyncStudioPro", "mashup-capture");
                Directory.CreateDirectory(dir);
                _mashupTempPath = System.IO.Path.Combine(dir, "last-mashup.wav");
                if (File.Exists(_mashupTempPath)) File.Delete(_mashupTempPath);
                _mashupTitleA = _panelA.CurrentTitle;
                _mashupTitleB = _panelB.CurrentTitle;
                _recorder.Start(_mashupTempPath);
                _mashupCapture = true;
                SaveMashupButton.IsEnabled = true;
                SaveMashupButton.Content = "💾 Save Mashup (capturing…)";
                return true;
            }
            catch { _mashupCapture = false; return false; }
        }

        private async void SaveMashupButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_mashupCapture || _mashupTempPath == null || !File.Exists(_mashupTempPath))
            {
                SetStatus("💾 No mashup captured yet — press 🎤 MASHUP A+B first; it captures automatically while it plays.");
                return;
            }
            _recorder.Stop();
            _mashupCapture = false;
            SaveMashupButton.IsEnabled = false;
            SaveMashupButton.Content = "💾 Save Mashup";
            string tempPath = _mashupTempPath;
            string titleA = _mashupTitleA, titleB = _mashupTitleB;
            SetStatus("💾 Saving your mashup…");
            try
            {
                var result = await Task.Run(() =>
                {
                    var samples = SamplerEngine.DecodeBytes(File.ReadAllBytes(tempPath), tempPath);
                    // Trim the silence at both ends (keep a quarter-second of breathing room).
                    const float thresh = 0.002f;
                    int first = 0, last = samples.Length - 2;
                    while (first < samples.Length - 1 && Math.Abs(samples[first]) < thresh && Math.Abs(samples[first + 1]) < thresh) first += 2;
                    while (last > 0 && Math.Abs(samples[last]) < thresh && Math.Abs(samples[last + 1]) < thresh) last -= 2;
                    first = Math.Max(0, first - 44100 / 2);
                    last = Math.Min(samples.Length - 2, last + 44100 / 2);
                    int count = Math.Max(0, last - first + 2);
                    if (count < 44100 * 2) throw new InvalidOperationException("the capture came out silent — nothing to save.");
                    var trimmed = new float[count];
                    Array.Copy(samples, first, trimmed, 0, count);
                    string Clean(string t)
                    {
                        string s = System.IO.Path.GetFileNameWithoutExtension(t ?? "");
                        foreach (var ch in System.IO.Path.GetInvalidFileNameChars()) s = s.Replace(ch, ' ');
                        s = s.Trim();
                        if (s.Length > 40) s = s.Substring(0, 40).Trim();
                        return s.Length == 0 ? "Track" : s;
                    }
                    string desired = $"Mashup - {Clean(titleA)} x {Clean(titleB)}.wav";
                    string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BeatSyncStudioPro", "mashup-capture");
                    string finalTemp = System.IO.Path.Combine(dir, "final-mashup.wav");
                    if (File.Exists(finalTemp)) File.Delete(finalTemp);
                    WaveFileWriter.CreateWaveFile16(finalTemp, new ArraySampleProvider(trimmed));
                    return (Desired: desired, FinalTemp: finalTemp, Seconds: count / 2.0 / 44100.0);
                });
                var bytes = File.ReadAllBytes(result.FinalTemp);
                string savedName = _owner.AddToLibraryOnly(bytes, result.Desired, SoundCategorizer.Mashups);
                try { _owner.StoreInSoundLibraryArchive(SoundCategorizer.Mashups, savedName, bytes); }
                catch { /* the library copy is saved either way */ }
                SetStatus($"💾 Mashup saved to the library under 🔀 Mash-ups: '{savedName}' ({result.Seconds:0}s). Drag it onto the timeline like any other sound!");
            }
            catch (Exception ex) { SetStatus("💾 Couldn't save the mashup: " + ex.Message); }
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

        // ===== v3.45: sound picker — categorized browser with preview =====
        private sealed class SoundChoice
        {
            public string Display = "";
            public string DecodeName = "";
            public string PersistKey = "";
            public string SubLabel = "";
            public Func<byte[]?> LoadBytes = () => null;
        }

        private sealed class SoundPickerWindow
        {
            private readonly Window _win;
            private readonly ListBox _catList;
            private readonly ListBox _soundList;
            private readonly TextBlock _status;
            private readonly Button _useBtn;
            private Dictionary<string, List<SoundChoice>> _catalog;
            private List<string> _cats;
            // v3.55: same nested stems layout as the main library — the
            // category list rows are _rows (keys into _catalog, plus one
            // StemsParentKey row); _shown mirrors the sound list so a sound
            // picked under the Stems parent still maps to its real choice.
            private List<string> _rows = new();
            private readonly List<SoundChoice> _shown = new();
            private bool _stemsExpanded = true;
            private const string StemsParentKey = "STEMS_PARENT";
            private static bool IsStemsFamily(string c) => c == SoundCategorizer.Stems || c.StartsWith("Stems · ", StringComparison.Ordinal);
            private static string StemChildLabel(string c) => c == SoundCategorizer.Stems ? "General" : c.Substring("Stems · ".Length);
            private readonly Style? _buttonStyle;
            private readonly Button _previewBtn;
            private readonly DispatcherTimer _previewTimer;
            // v3.53: tag line + cover thumbnail under the lists.
            private readonly TextBlock _tagLine;
            private readonly Image _coverThumb;

            public SoundChoice? Result { get; private set; }
            public Func<SoundChoice, string?>? PreviewHandler;
            public Action? StopPreviewHandler;
            public Func<bool>? PreviewPlayingHandler;
            public Func<SoundChoice, bool>? DeleteHandler;
            public Func<string?>? ImportHandler;
            public Action<SoundChoice>? ExportHandler;
            public Func<SoundChoice, bool>? RenameHandler;
            // v3.53: read the selected sound's embedded tags (cover + text).
            public Func<SoundChoice, TagInfo?>? TagsHandler;

            public SoundPickerWindow(Window owner, int pad, Dictionary<string, List<SoundChoice>> catalog, Style? buttonStyle = null)
            {
                _buttonStyle = buttonStyle;
                _catalog = catalog;
                string[] preferred = { SoundCategorizer.Drums, SoundCategorizer.Guitar, SoundCategorizer.Bass, SoundCategorizer.Keys, SoundCategorizer.Loops, SoundCategorizer.Fx, SoundCategorizer.Vocals, SoundCategorizer.Stems, SoundCategorizer.Mashups, "Downloads", SoundCategorizer.Other };
                _cats = catalog.Keys.ToList();
                _cats.Sort((a, b) =>
                {
                    string ka = a.StartsWith("Stems", StringComparison.Ordinal) ? SoundCategorizer.Stems : a;
                    string kb = b.StartsWith("Stems", StringComparison.Ordinal) ? SoundCategorizer.Stems : b;
                    int ia = Array.IndexOf(preferred, ka); if (ia < 0) ia = 100;
                    int ib = Array.IndexOf(preferred, kb); if (ib < 0) ib = 100;
                    return ia != ib ? ia.CompareTo(ib) : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
                });
                _win = new Window
                {
                    Title = $"Assign a sound — sampler pad {pad + 1}",
                    Width = 880,
                    Height = 520,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = owner,
                    Background = new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x1E)),
                    Foreground = Brushes.White
                };
                var root = new DockPanel { Margin = new Thickness(14) };
                var header = new TextBlock { Text = "🎵 Pick a sound — everything sorted by category. Preview it, then use it.", FontSize = 14, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 10) };
                DockPanel.SetDock(header, Dock.Top);
                root.Children.Add(header);
                var bottom = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
                DockPanel.SetDock(bottom, Dock.Bottom);
                root.Children.Add(bottom);
                _status = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA5, 0xB5)), Text = "Pick a category, then a sound.", Margin = new Thickness(0, 0, 0, 8) };
                bottom.Children.Add(_status);
                // v3.53: when the picked sound is a tagged MP3/FLAC, show its
                // cover thumbnail + tag summary here while you browse.
                var tagRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
                _coverThumb = new Image { Width = 40, Height = 40, Stretch = Stretch.UniformToFill, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 8, 0) };
                tagRow.Children.Add(_coverThumb);
                _tagLine = new TextBlock { Foreground = Brushes.White, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                tagRow.Children.Add(_tagLine);
                bottom.Children.Add(tagRow);
                var btnRow = new StackPanel { Orientation = Orientation.Horizontal };
                bottom.Children.Add(btnRow);
                var previewBtn = new Button { Content = "▶ Preview", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0), Style = _buttonStyle };
                _previewBtn = previewBtn;
                var browseBtn = new Button { Content = "📁 Browse files…", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0), Style = _buttonStyle };
                var importBtn = new Button { Content = "📦 Import ZIP…", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0), Style = _buttonStyle };
                var deleteBtn = new Button { Content = "🗑️ Delete", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0), Style = _buttonStyle, IsEnabled = false };
                _useBtn = new Button { Content = "✓ Use this sound", Padding = new Thickness(14, 6, 14, 6), FontWeight = FontWeights.Bold, Background = new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54)), Foreground = Brushes.White, BorderThickness = new Thickness(0), IsEnabled = false, Style = _buttonStyle };
                var cancelBtn = new Button { Content = "Cancel", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8, 0, 0, 0), Style = _buttonStyle };
                btnRow.Children.Add(previewBtn);
                btnRow.Children.Add(browseBtn);
                btnRow.Children.Add(importBtn);
                btnRow.Children.Add(deleteBtn);
                btnRow.Children.Add(_useBtn);
                btnRow.Children.Add(cancelBtn);
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var listBg = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x28));
                var listBorder = new SolidColorBrush(Color.FromRgb(0x2A, 0x34, 0x46));
                _catList = new ListBox { Background = listBg, Foreground = Brushes.White, BorderBrush = listBorder, Margin = new Thickness(0, 0, 8, 0) };
                _soundList = new ListBox { Background = listBg, Foreground = Brushes.White, BorderBrush = listBorder };
                Grid.SetColumn(_soundList, 1);
                grid.Children.Add(_catList);
                grid.Children.Add(_soundList);
                root.Children.Add(grid);
                _win.Content = root;
                BuildCategoryRows();
                _catList.MouseLeftButtonUp += CatList_MouseLeftButtonUp;
                _catList.SelectionChanged += (s, e) =>
                {
                    StopPreview();
                    _soundList.Items.Clear();
                    _shown.Clear();
                    _useBtn.IsEnabled = false;
                    if (_catList.SelectedIndex < 0 || _catList.SelectedIndex >= _rows.Count) return;
                    string cat = _rows[_catList.SelectedIndex];
                    var list = ChoicesFor(cat);
                    foreach (var ch in list) { _shown.Add(ch); _soundList.Items.Add(ch.Display); }
                    _status.Text = $"{(cat == StemsParentKey ? "Stems" : cat)}: {list.Count} sounds — pick one and hit Preview.";
                };
                _soundList.SelectionChanged += (s, e) =>
                {
                    _useBtn.IsEnabled = SelectedChoice() != null;
                    UpdateTagLine();
                    var ch = SelectedChoice();
                    deleteBtn.IsEnabled = ch != null && DeleteHandler != null &&
                        (ch.PersistKey.StartsWith("library:", StringComparison.Ordinal) ||
                         ch.PersistKey.StartsWith("archive:", StringComparison.Ordinal) ||
                         ch.PersistKey.StartsWith(MainWindow.SoundLibraryRoot, StringComparison.OrdinalIgnoreCase));
                };
                _soundList.MouseDoubleClick += (s, e) => { if (SelectedChoice() != null) Accept(); };
                previewBtn.Click += (s, e) =>
                {
                    // One toggle button: Preview becomes Stop while the sound
                    // plays, and flips back when it ends or is pressed again.
                    if ((string)_previewBtn.Content == "⏹ Stop") StopPreview();
                    else Preview();
                };
                _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                _previewTimer.Tick += (s, e) =>
                {
                    if ((string)_previewBtn.Content == "⏹ Stop" && PreviewPlayingHandler != null && !PreviewPlayingHandler())
                        _previewBtn.Content = "▶ Preview";
                };
                _previewTimer.Start();
                _win.Closed += (s, e) => _previewTimer.Stop();
                browseBtn.Click += (s, e) => Browse();
                importBtn.Click += (s, e) =>
                {
                    StopPreview();
                    var note = ImportHandler?.Invoke();
                    if (note != null) _status.Text = note;
                };
                deleteBtn.Click += (s, e) =>
                {
                    var ch = SelectedChoice();
                    if (ch == null || DeleteHandler == null) return;
                    StopPreview();
                    if (DeleteHandler(ch))
                    {
                        string? home = null;
                        foreach (var kv in _catalog) if (kv.Value.Contains(ch)) { home = kv.Key; break; }
                        if (home != null) _catalog[home].Remove(ch);
                        _shown.Remove(ch);
                        _soundList.Items.RemoveAt(_soundList.SelectedIndex);
                        RefreshCategoryCounts();
                        _status.Text = $"Deleted: {ch.Display}";
                    }
                };
                _useBtn.Click += (s, e) => Accept();
                cancelBtn.Click += (s, e) => { StopPreview(); _win.DialogResult = false; };
                // v3.47: right-click a sound — export it, rename it, or delete it.
                var soundMenu = new ContextMenu();
                var miExport = new MenuItem { Header = "📤 Export sound…" };
                miExport.Click += (s, e) => { var ch = SelectedChoice(); if (ch != null) ExportHandler?.Invoke(ch); };
                var miRename = new MenuItem { Header = "✏️ Rename…" };
                miRename.Click += (s, e) => { var ch = SelectedChoice(); if (ch != null) RenameHandler?.Invoke(ch); };
                var miDelete = new MenuItem { Header = "🗑️ Delete sound…" };
                miDelete.Click += (s, e) => { if (deleteBtn.IsEnabled) deleteBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
                soundMenu.Items.Add(miExport);
                soundMenu.Items.Add(miRename);
                soundMenu.Items.Add(new Separator());
                soundMenu.Items.Add(miDelete);
                _soundList.ContextMenu = soundMenu;
                _win.Closed += (s, e) => StopPreview();
                if (_catList.Items.Count > 0) _catList.SelectedIndex = 0;
                else _status.Text = "No sounds found anywhere — use Browse files… to pick one from disk.";
            }

            // v3.55: (re)build the category rows — flat categories as before,
            // but the whole stems family collapses into one parent row with
            // the per-type categories as indented children under it.
            private void BuildCategoryRows()
            {
                var fam = _cats.Where(IsStemsFamily).ToList();
                var keys = new List<string>();
                bool emitted = false;
                foreach (var c in _cats)
                {
                    if (IsStemsFamily(c))
                    {
                        if (emitted) continue;
                        emitted = true;
                        keys.Add(StemsParentKey);
                        if (_stemsExpanded)
                            keys.AddRange(fam.Where(fc => fc != SoundCategorizer.Stems)
                                             .Concat(fam.Where(fc => fc == SoundCategorizer.Stems)));
                        continue;
                    }
                    keys.Add(c);
                }
                _rows = keys;
                _catList.Items.Clear();
                foreach (var k in _rows) _catList.Items.Add(CategoryRowText(k));
            }

            private string CategoryRowText(string key)
            {
                if (key == StemsParentKey)
                {
                    int total = _cats.Where(IsStemsFamily).Sum(fc => _catalog.TryGetValue(fc, out var l) ? l.Count : 0);
                    return $"{(_stemsExpanded ? "\u2796" : "\u2795")} {SoundCategorizer.Icon(SoundCategorizer.Stems)} Stems  ({total})";
                }
                if (IsStemsFamily(key)) return $" -- {StemChildLabel(key)}  ({_catalog[key].Count})";
                return $"{SoundCategorizer.Icon(key)} {key}  ({_catalog[key].Count})";
            }

            private List<SoundChoice> ChoicesFor(string key)
            {
                if (key != StemsParentKey) return _catalog.TryGetValue(key, out var l) ? l : new List<SoundChoice>();
                var all = new List<SoundChoice>();
                var fam = _cats.Where(IsStemsFamily)
                               .OrderBy(fc => fc == SoundCategorizer.Stems ? 1 : 0)
                               .ThenBy(fc => StemChildLabel(fc), StringComparer.OrdinalIgnoreCase);
                foreach (var fc in fam) if (_catalog.TryGetValue(fc, out var l)) all.AddRange(l);
                return all;
            }

            // v3.55: clicking the "+ Stems" parent row folds/unfolds the
            // sub-category rows (same as the main library list).
            private void CatList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
            {
                var el = _catList.InputHitTest(e.GetPosition(_catList)) as DependencyObject;
                var item = ItemsControl.ContainerFromElement(_catList, el) as ListBoxItem;
                if (item == null) return;
                int idx = _catList.ItemContainerGenerator.IndexFromContainer(item);
                if (idx >= 0 && idx < _rows.Count && _rows[idx] == StemsParentKey)
                {
                    _stemsExpanded = !_stemsExpanded;
                    string keep = _rows[Math.Max(0, Math.Min(idx, _rows.Count - 1))];
                    BuildCategoryRows();
                    int ni = _rows.IndexOf(keep == StemsParentKey || IsStemsFamily(keep) ? StemsParentKey : keep);
                    if (ni >= 0) _catList.SelectedIndex = ni;
                }
            }

            private void RefreshCategoryCounts()
            {
                for (int i = 0; i < _rows.Count && i < _catList.Items.Count; i++)
                    _catList.Items[i] = CategoryRowText(_rows[i]);
            }

            public void ReloadCatalog(Dictionary<string, List<SoundChoice>> catalog)
            {
                StopPreview();
                _catalog = catalog;
                string[] preferred = { SoundCategorizer.Drums, SoundCategorizer.Guitar, SoundCategorizer.Bass, SoundCategorizer.Keys, SoundCategorizer.Loops, SoundCategorizer.Vocals, SoundCategorizer.Fx, SoundCategorizer.Stems, SoundCategorizer.Mashups, "Downloads", SoundCategorizer.Other };
                _cats = catalog.Keys.ToList();
                _cats.Sort((a, b) =>
                {
                    string ka = a.StartsWith("Stems", StringComparison.Ordinal) ? SoundCategorizer.Stems : a;
                    string kb = b.StartsWith("Stems", StringComparison.Ordinal) ? SoundCategorizer.Stems : b;
                    int ia = Array.IndexOf(preferred, ka); if (ia < 0) ia = 100;
                    int ib = Array.IndexOf(preferred, kb); if (ib < 0) ib = 100;
                    return ia != ib ? ia.CompareTo(ib) : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
                });
                BuildCategoryRows();
                _soundList.Items.Clear();
                _shown.Clear();
                if (_catList.Items.Count > 0) _catList.SelectedIndex = 0;
            }

            private SoundChoice? SelectedChoice()
            {
                if (_soundList.SelectedIndex < 0 || _soundList.SelectedIndex >= _shown.Count) return null;
                return _shown[_soundList.SelectedIndex];
            }

            // v3.53: fill the tag line + cover thumbnail for the selected sound.
            private void UpdateTagLine()
            {
                _tagLine.Text = "";
                _coverThumb.Source = null;
                _coverThumb.Visibility = Visibility.Collapsed;
                var ch = SelectedChoice();
                if (ch == null || TagsHandler == null) return;
                TagInfo? tags;
                try { tags = TagsHandler(ch); } catch { tags = null; }
                if (tags == null) return;
                var parts = new List<string>();
                if (tags.DisplayName.Length > 0) parts.Add("🏷️ " + tags.DisplayName);
                if (tags.Album.Length > 0) parts.Add("💿 " + tags.Album);
                if (tags.Year.Length > 0) parts.Add(tags.Year);
                if (tags.Genre.Length > 0) parts.Add(tags.Genre);
                _tagLine.Text = string.Join(" • ", parts);
                var pic = tags.Picture;
                if (pic != null && pic.Length > 0)
                {
                    try
                    {
                        var bmp = new BitmapImage();
                        using (var ms = new MemoryStream(pic))
                        {
                            bmp.BeginInit();
                            bmp.CacheOption = BitmapCacheOption.OnLoad;
                            bmp.StreamSource = ms;
                            bmp.EndInit();
                        }
                        bmp.Freeze();
                        _coverThumb.Source = bmp;
                        _coverThumb.Visibility = Visibility.Visible;
                    }
                    catch { _coverThumb.Source = null; _coverThumb.Visibility = Visibility.Collapsed; }
                }
            }

            private void Preview()
            {
                StopPreview();
                var ch = SelectedChoice();
                if (ch == null) { _status.Text = "Pick a sound first."; return; }
                if (PreviewHandler == null) { _status.Text = "Preview isn't available here."; return; }
                var err = PreviewHandler(ch);
                if (err == null)
                {
                    _previewBtn.Content = "⏹ Stop";
                    _status.Text = $"▶ Previewing: {ch.Display} — the Preview button is now Stop if it runs long.";
                }
                else _status.Text = "Preview failed: " + err;
            }

            private void StopPreview()
            {
                StopPreviewHandler?.Invoke();
                if (_previewBtn != null) _previewBtn.Content = "▶ Preview";
            }

            private void Browse()
            {
                StopPreview();
                var dialog = new OpenFileDialog { Title = "Pick any sound file", Filter = "Audio|*.wav;*.mp3" };
                if (dialog.ShowDialog() != true) return;
                string file = dialog.FileName;
                Result = new SoundChoice
                {
                    Display = System.IO.Path.GetFileNameWithoutExtension(file),
                    DecodeName = file,
                    PersistKey = file,
                    SubLabel = System.IO.Path.GetFileName(file),
                    LoadBytes = () => File.Exists(file) ? File.ReadAllBytes(file) : null
                };
                _win.DialogResult = true;
            }

            private void Accept()
            {
                var ch = SelectedChoice();
                if (ch == null) return;
                StopPreview();
                Result = ch;
                _win.DialogResult = true;
            }

            public bool? ShowDialog() => _win.ShowDialog();
        }

        // ===== v3.43: MASHUP A+B — A's vocals over B's beat (the real remix recipe) =====
        private bool _mashupBusy;

        private async void MashupButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mashupBusy) return;
            if (!_panelA.HasTrack || !_panelB.HasTrack)
            {
                SetStatus("🎤 MASHUP A+B needs a track loaded on BOTH decks first.");
                return;
            }
            _mashupBusy = true;
            MashupButton.IsEnabled = false;
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
            }
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

        // v3.54 (audit #23): both decks detect keys on background threads and
        // used to race load-modify-save on the shared cache file — last writer
        // won and dropped the other deck's key. Merge one entry under a lock.
        private static readonly object KeyCacheLock = new object();

        internal static void SaveKeyCacheEntry(string key, KeyCacheEntry entry)
        {
            lock (KeyCacheLock)
            {
                var map = LoadKeyCache();
                map[key] = entry;
                SaveKeyCache(map);
            }
        }

        // v3.67: BPM cache (bpm-cache.json) — Suggest Next analyzes library
        // tracks once and remembers their BPM; same pattern as the key cache.
        private static string BpmCachePath => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BeatSyncStudioPro", "bpm-cache.json");

        internal static System.Collections.Generic.Dictionary<string, double> LoadBpmCache()
        {
            try
            {
                if (File.Exists(BpmCachePath))
                {
                    var map = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, double>>(File.ReadAllText(BpmCachePath));
                    if (map != null) return map;
                }
            }
            catch { }
            return new System.Collections.Generic.Dictionary<string, double>();
        }

        private static readonly object BpmCacheLock = new object();

        internal static void SaveBpmCacheEntry(string key, double bpm)
        {
            lock (BpmCacheLock)
            {
                try
                {
                    var map = LoadBpmCache();
                    map[key] = bpm;
                    var dir = System.IO.Path.GetDirectoryName(BpmCachePath);
                    if (dir != null) Directory.CreateDirectory(dir);
                    File.WriteAllText(BpmCachePath, System.Text.Json.JsonSerializer.Serialize(map));
                }
                catch { }
            }
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
                btn.ToolTip = "Play this sound — hit the pad again while it's playing to stop it. Right-click the pad to assign a sound, rename it, or clear it — every pad is programmable.";
                btn.Click += (s, e) => TriggerPad(pad);
                var menu = new ContextMenu();
                var miAssign = new MenuItem { Header = "🎵 Assign sound…" };
                miAssign.Click += (s, e) => AssignPad(pad);
                var miRename = new MenuItem { Header = "✏️ Rename pad…" };
                miRename.Click += (s, e) => RenamePad(pad);
                var miClear = new MenuItem { Header = "🧹 Clear sound" };
                miClear.Click += (s, e) => ClearPad(pad);
                var miStop = new MenuItem { Header = "⏹ Stop" };
                miStop.Click += (s, e) => _sampler.StopPad(pad);
                menu.Items.Add(miAssign);
                menu.Items.Add(miRename);
                menu.Items.Add(miClear);
                menu.Items.Add(miStop);
                btn.ContextMenu = menu;
                _padButtons[i] = btn;
                _padSubs[i] = sub;
                _padTitles[i] = title;
                SamplerPadGrid.Children.Add(btn);
                string? loadedName = null;   // v3.46: the pad's face follows its sound's name
                if (assignments.TryGetValue(i, out string? key))
                {
                    if (string.IsNullOrEmpty(key)) sub.Text = "right-click: assign a sound";
                    else if (key.StartsWith("pack:", StringComparison.Ordinal))
                    {
                        string entry = key.Substring(5);
                        var pb = SamplerEngine.ReadPackEntry(entry);
                        if (pb != null)
                        {
                            try { _sampler.Slots[i].Load(SamplerEngine.DecodeBytes(pb, entry)); sub.Text = "sound pack"; loadedName = System.IO.Path.GetFileNameWithoutExtension(entry); }
                            catch { sub.Text = "sound pack (decode failed)"; }
                        }
                        else sub.Text = "sound pack not found";
                    }
                    else if (key.StartsWith("library:", StringComparison.Ordinal))
                    {
                        string lname = key.Substring(8);
                        if (_owner.ZipSounds.TryGetValue(lname, out var lb))
                        {
                            try { _sampler.Slots[i].Load(SamplerEngine.DecodeBytes(lb, lname)); sub.Text = lname; loadedName = System.IO.Path.GetFileNameWithoutExtension(lname); }
                            catch { sub.Text = "library sound (decode failed)"; }
                        }
                        else sub.Text = "not in this project — reassign";
                    }
                    else if (key.StartsWith("archive:", StringComparison.Ordinal))
                    {
                        string entryName = key.Substring(8);
                        var ab = MainWindow.ReadLibraryArchiveEntry(entryName);
                        if (ab != null)
                        {
                            try { _sampler.Slots[i].Load(SamplerEngine.DecodeBytes(ab, entryName)); sub.Text = System.IO.Path.GetFileName(entryName); loadedName = System.IO.Path.GetFileNameWithoutExtension(entryName); }
                            catch { sub.Text = "archive sound (decode failed)"; }
                        }
                        else sub.Text = "not in sound library — reassign";
                    }
                    else if (File.Exists(key))
                    {
                        try { _sampler.Slots[i].Load(SamplerEngine.DecodeBytes(File.ReadAllBytes(key), key)); sub.Text = System.IO.Path.GetFileName(key); loadedName = System.IO.Path.GetFileNameWithoutExtension(key); }
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
                // A pad with an assigned sound shows that sound's name on its face
                // (a name the user typed in Rename still wins).
                if (loadedName != null && !names.ContainsKey(i))
                    title.Text = emoji + " " + loadedName;
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
            // v3.76: a pad that's still playing STOPS when hit again —
            // long loops and risers finally have an off switch.
            if (_sampler.Slots[pad].IsPlaying)
            {
                _sampler.StopPad(pad);
                SetStatus($"⏹ Sampler pad {pad + 1} stopped.");
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
            var picker = new SoundPickerWindow(this, pad, BuildSoundCatalog(), (Style)TryFindResource("HwButton"));
            picker.PreviewHandler = PreviewPickerSound;
            picker.TagsHandler = ch =>
            {
                try { var b = ch.LoadBytes(); return b == null ? null : TagReader.Parse(b, ch.DecodeName); }
                catch { return null; }
            };
            picker.StopPreviewHandler = () => _sampler.StopPreview();
            picker.PreviewPlayingHandler = () => _sampler.PreviewIsPlaying;
            picker.DeleteHandler = DeletePickerSound;
            picker.ExportHandler = ExportPickerChoice;
            picker.RenameHandler = ch => RenamePickerChoice(picker, ch);
            picker.ImportHandler = () =>
            {
                var dialog = new OpenFileDialog
                {
                    Title = "Import sounds — ZIP packs or loose audio files",
                    Filter = "Sound packs & audio|*.zip;*.wav;*.mp3",
                    Multiselect = true
                };
                if (dialog.ShowDialog() != true) return null;
                var (added, skipped) = _owner.ImportSoundArchives(dialog.FileNames);
                picker.ReloadCatalog(BuildSoundCatalog());
                SetStatus($"Imported {added} sound(s) into the library ({skipped} skipped — duplicates or unreadable).");
                return $"Imported {added} sound(s), sorted by category. {skipped} skipped (duplicates or unreadable).";
            };
            if (picker.ShowDialog() != true) return;
            var choice = picker.Result;
            if (choice == null) return;
            var bytes = choice.LoadBytes();
            if (bytes == null) { SetStatus("That sound couldn't be loaded."); return; }
            ApplyPadChoice(pad, bytes, choice);
        }

        private void ApplyPadChoice(int pad, byte[] bytes, SoundChoice choice)
        {
            try
            {
                var samples = SamplerEngine.DecodeBytes(bytes, choice.DecodeName);
                _sampler.Slots[pad].Load(samples);
                var map = LoadPadAssignments();
                map[pad] = choice.PersistKey;
                SavePadAssignments(map);
                // v3.46: the pad's face takes the sound's name (and keeps it across restarts).
                var names = LoadPadNames();
                names[pad] = choice.Display;
                SavePadNames(names);
                _padTitles[pad].Text = PadDefs[pad].Emoji + " " + choice.Display;
                _padSubs[pad].Text = choice.SubLabel;
                SetStatus($"Sampler pad {pad + 1}: {choice.Display} assigned.");
                TriggerPad(pad);
            }
            catch (Exception ex) { SetStatus("Could not load that sound: " + ex.Message); }
        }

        /// <summary>v3.46: picker previews play through the sampler engine itself
        /// (the same bus the pads use) — returns an error string or null.</summary>
        private string? PreviewPickerSound(SoundChoice choice)
        {
            try
            {
                var bytes = choice.LoadBytes();
                if (bytes == null) return "That sound couldn't be loaded.";
                var samples = SamplerEngine.DecodeBytes(bytes, choice.DecodeName);
                _owner.EnsureOutputRunning();
                _sampler.Preview(samples);
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>v3.46: picker delete — library entries and imported
        /// sound-library files only, always behind a confirm.</summary>
        private bool DeletePickerSound(SoundChoice choice)
        {
            bool isLibrary = choice.PersistKey.StartsWith("library:", StringComparison.Ordinal);
            bool isArchive = choice.PersistKey.StartsWith("archive:", StringComparison.Ordinal);
            bool isSoundLibFile = !isLibrary && !isArchive && choice.PersistKey.StartsWith(MainWindow.SoundLibraryRoot, StringComparison.OrdinalIgnoreCase);
            if (!isLibrary && !isArchive && !isSoundLibFile) return false;
            var ask = MessageBox.Show($"Delete '{choice.Display}'?\n\nThis removes it from the library{(isArchive ? " and from your sound-library archive" : isSoundLibFile ? " and deletes the file from your sound-library folder" : "")}. This can't be undone.",
                "Delete sound", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (ask != MessageBoxResult.Yes) return false;
            try
            {
                if (isLibrary) _owner.RemoveLibraryAsset(choice.PersistKey.Substring(8));
                if (isArchive)
                {
                    string entryName = choice.PersistKey.Substring(8);
                    MainWindow.RemoveLibraryArchiveEntryExact(entryName);
                    string fileName = System.IO.Path.GetFileName(entryName);
                    if (_owner.ZipSounds.ContainsKey(fileName)) _owner.RemoveLibraryAsset(fileName);
                }
                if (isSoundLibFile)
                {
                    string fileName = System.IO.Path.GetFileName(choice.PersistKey);
                    if (_owner.ZipSounds.ContainsKey(fileName)) _owner.RemoveLibraryAsset(fileName);
                    if (File.Exists(choice.PersistKey)) File.Delete(choice.PersistKey);
                }
                SetStatus($"Deleted sound: {choice.Display}");
                return true;
            }
            catch (Exception ex) { SetStatus("Delete failed: " + ex.Message); return false; }
        }

        /// <summary>v3.47: picker right-click Export — write the sound out to a file.</summary>
        private void ExportPickerChoice(SoundChoice choice)
        {
            var bytes = choice.LoadBytes();
            if (bytes == null) { SetStatus("That sound couldn't be read for export."); return; }
            string fileName = choice.PersistKey.StartsWith("library:", StringComparison.Ordinal)
                ? choice.PersistKey.Substring(8)
                : System.IO.Path.GetFileName(choice.DecodeName);
            var dialog = new SaveFileDialog { Title = "Export sound", FileName = fileName, Filter = "Audio files|*.wav;*.mp3|All files|*.*" };
            if (dialog.ShowDialog() != true) return;
            try { File.WriteAllBytes(dialog.FileName, bytes); SetStatus($"Exported: {System.IO.Path.GetFileName(dialog.FileName)}"); }
            catch (Exception ex) { SetStatus("Export failed: " + ex.Message); }
        }

        /// <summary>v3.47: picker right-click Rename — library and archive sounds.</summary>
        private bool RenamePickerChoice(SoundPickerWindow picker, SoundChoice choice)
        {
            if (choice.PersistKey.StartsWith("library:", StringComparison.Ordinal))
            {
                string name = choice.PersistKey.Substring(8);
                string ext = System.IO.Path.GetExtension(name);
                var typed = MainWindow.PromptText("Rename sound", $"New name for '{name}':", System.IO.Path.GetFileNameWithoutExtension(name));
                if (typed == null) return false;
                string newName = typed.Trim();
                if (newName.Length == 0) return false;
                if (!newName.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) newName += ext;
                if (_owner.RenameLibraryAsset(name, newName) == null)
                {
                    MessageBox.Show($"Couldn't rename — a sound named '{newName}' already exists, or the name is invalid.", "Rename sound");
                    return false;
                }
                picker.ReloadCatalog(BuildSoundCatalog());
                SetStatus($"Renamed sound to '{newName}'.");
                return true;
            }
            if (choice.PersistKey.StartsWith("archive:", StringComparison.Ordinal))
            {
                string entryName = choice.PersistKey.Substring(8);
                string fileName = System.IO.Path.GetFileName(entryName);
                string ext = System.IO.Path.GetExtension(fileName);
                var typed = MainWindow.PromptText("Rename sound", $"New name for '{fileName}':", System.IO.Path.GetFileNameWithoutExtension(fileName));
                if (typed == null) return false;
                string newFile = typed.Trim();
                if (newFile.Length == 0) return false;
                if (!newFile.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) newFile += ext;
                bool ok = _owner.ZipSounds.ContainsKey(fileName)
                    ? _owner.RenameLibraryAsset(fileName, newFile) != null
                    : MainWindow.RenameLibraryArchiveEntryExact(entryName, newFile);
                if (!ok)
                {
                    MessageBox.Show($"Couldn't rename — a sound named '{newFile}' already exists, or the name is invalid.", "Rename sound");
                    return false;
                }
                picker.ReloadCatalog(BuildSoundCatalog());
                SetStatus($"Renamed sound to '{newFile}'.");
                return true;
            }
            MessageBox.Show("That sound lives outside the library (built-in pack / a loose file), so it keeps its original name. Sounds you've imported can be renamed.", "Rename sound");
            return false;
        }

        // ===== v3.67: Suggest Next results window =====
        private sealed class SuggestWindow
        {
            private readonly Window _win;
            private readonly ListBox _list;
            private readonly DeckPanel _target;
            private readonly System.Collections.Generic.List<DeckPanel.SuggestPick> _picks;

            public SuggestWindow(DjWindow owner, DeckPanel source, DeckPanel target, System.Collections.Generic.List<DeckPanel.SuggestPick> picks)
            {
                _target = target;
                _picks = picks;
                _win = new Window
                {
                    Title = "🎯 Suggest Next Song",
                    Width = 580,
                    Height = 440,
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x2B)),
                    Foreground = Brushes.White,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = owner,
                };
                var grid = new Grid { Margin = new Thickness(12) };
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var head = new TextBlock
                {
                    Text = $"Songs that mix well after {source.PanelName}'s track — best first. Key match counts most, BPM closeness second.",
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA0, 0xA5, 0xB5)),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                };
                grid.Children.Add(head);
                _list = new ListBox
                {
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x14, 0x17, 0x1E)),
                    Foreground = Brushes.White,
                    BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x33, 0x33, 0x33)),
                    Margin = new Thickness(0, 10, 0, 0),
                };
                foreach (var p in picks)
                    _list.Items.Add($"{p.Choice.Display}  ·  key {p.Camelot}  ·  {(p.Bpm > 0 ? p.Bpm.ToString("0") + " BPM" : "BPM ?")}  ·  match {p.Score}%");
                if (_list.Items.Count > 0) _list.SelectedIndex = 0;
                _list.MouseDoubleClick += (s, e) => LoadPicked();
                Grid.SetRow(_list, 1);
                grid.Children.Add(_list);
                var loadBtn = new Button
                {
                    Content = $"⬇ Load onto {target.PanelName}",
                    Padding = new Thickness(14, 6, 14, 6),
                    Margin = new Thickness(0, 10, 0, 0),
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x3C)),
                    Foreground = Brushes.White,
                    BorderThickness = new Thickness(0),
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                loadBtn.Click += (s, e) => LoadPicked();
                Grid.SetRow(loadBtn, 2);
                grid.Children.Add(loadBtn);
                var note = new TextBlock
                {
                    Text = "Analysis is cached — the more you use Suggest, the faster it answers.",
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA0, 0xA5, 0xB5)),
                    FontSize = 11,
                    Margin = new Thickness(0, 8, 0, 0),
                };
                Grid.SetRow(note, 3);
                grid.Children.Add(note);
                _win.Content = grid;
            }

            public void Show() => _win.Show();

            private void LoadPicked()
            {
                int idx = _list.SelectedIndex;
                if (idx < 0 || idx >= _picks.Count) return;
                var p = _picks[idx];
                _target.LoadForMix(p.Bytes, p.Choice.DecodeName);
                _win.Close();
            }
        }

        /// <summary>v3.45: every assignable sound in one sorted catalog —
        /// the built-in pack (compressed, read straight from soundpack.zip),
        /// the editor library (incl. split stems), stem files on disk, and the
        /// PC's Downloads folder.</summary>
        private Dictionary<string, List<SoundChoice>> BuildSoundCatalog()
        {
            var catalog = new Dictionary<string, List<SoundChoice>>();
            var seen = new HashSet<(string, string)>();   // v3.46: (category, display) — the same sound never shows twice
            void Add(string cat, SoundChoice c)
            {
                if (!seen.Add((cat, c.Display.ToLowerInvariant()))) return;
                if (!catalog.TryGetValue(cat, out var list)) { list = new List<SoundChoice>(); catalog[cat] = list; }
                list.Add(c);
            }
            // 1) Built-in sound pack (Assets/soundpack.zip, Category/name.wav entries)
            try
            {
                string pack = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "soundpack.zip");
                if (File.Exists(pack))
                {
                    using var zip = ZipFile.OpenRead(pack);
                    foreach (var entry in zip.Entries.Where(en => en.Name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)))
                    {
                        string cat = entry.FullName.Contains('/') ? entry.FullName.Substring(0, entry.FullName.IndexOf('/')) : "Sounds";
                        if (cat == "Keys") cat = SoundCategorizer.Keys;
                        string entryName = entry.FullName;
                        string display = System.IO.Path.GetFileNameWithoutExtension(entry.Name);
                        Add(cat, new SoundChoice
                        {
                            Display = display,
                            DecodeName = entryName,
                            PersistKey = "pack:" + entryName,
                            SubLabel = display,
                            LoadBytes = () => SamplerEngine.ReadPackEntry(entryName)
                        });
                    }
                }
            }
            catch { /* pack missing/unreadable — the rest of the catalog still works */ }
            // 2) The editor library (this project's sounds, incl. stems filed by v3.44)
            foreach (var (name, bytes) in _owner.ZipSounds)
            {
                string cat = _owner.SoundCategories.TryGetValue(name, out var c) ? c : SoundCategorizer.Infer(name);
                cat = SoundCategorizer.RefineStemCategory(cat, name);
                var captured = bytes;
                Add(cat, new SoundChoice
                {
                    Display = System.IO.Path.GetFileNameWithoutExtension(name),
                    DecodeName = name,
                    PersistKey = "library:" + name,
                    SubLabel = name,
                    LoadBytes = () => captured
                });
            }
            // 3) Stem files on disk (the DJ stem cache) — also reachable as plain files
            try
            {
                string stemsRoot = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BeatSyncStudioPro", "stems");
                if (Directory.Exists(stemsRoot))
                {
                    var seenStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var file in Directory.EnumerateFiles(stemsRoot, "*.wav", SearchOption.AllDirectories))
                    {
                        string fn = System.IO.Path.GetFileNameWithoutExtension(file);
                        string display = fn;
                        string stemCat = SoundCategorizer.Stems;
                        int p = fn.LastIndexOf(" (", StringComparison.Ordinal);
                        if (p > 0 && fn.EndsWith(")"))
                        {
                            string stemWord = fn.Substring(p + 2, fn.Length - p - 3);
                            display = fn.Substring(0, p) + " — " + char.ToUpperInvariant(stemWord[0]) + stemWord.Substring(1);
                            stemCat = SoundCategorizer.StemCategory(stemWord);   // v3.46: stems filed per type, not one pile
                        }
                        else if (fn.ToLowerInvariant() is "vocals" or "drums" or "bass" or "other")
                        {
                            // Plain-named stem files inside a per-song folder: file it by type too.
                            string parent = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(file) ?? "");
                            int dash = parent.LastIndexOf('-');
                            if (dash > 0 && parent.Substring(dash + 1).All(char.IsDigit)) parent = parent.Substring(0, dash);
                            display = (parent.Length > 0 ? parent + " — " : "") + char.ToUpperInvariant(fn[0]) + fn.Substring(1).ToLowerInvariant();
                            stemCat = SoundCategorizer.StemCategory(fn);
                        }
                        if (!seenStems.Add(display)) continue;
                        string path = file;
                        Add(stemCat, new SoundChoice
                        {
                            Display = display,
                            DecodeName = file,
                            PersistKey = file,
                            SubLabel = display,
                            LoadBytes = () => File.Exists(path) ? File.ReadAllBytes(path) : null
                        });
                    }
                }
            }
            catch { /* no stems yet — fine */ }
            // 4) The PC's Downloads folder (sounds downloaded outside the app)
            try
            {
                string dl = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                if (Directory.Exists(dl))
                {
                    foreach (var file in Directory.EnumerateFiles(dl, "*.*", SearchOption.TopDirectoryOnly)
                                 .Where(f => f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)))
                    {
                        string path = file;
                        Add("Downloads", new SoundChoice
                        {
                            Display = System.IO.Path.GetFileNameWithoutExtension(file),
                            DecodeName = file,
                            PersistKey = file,
                            SubLabel = System.IO.Path.GetFileName(file),
                            LoadBytes = () => File.Exists(path) ? File.ReadAllBytes(path) : null
                        });
                    }
                }
            }
            catch { /* downloads folder unreadable — fine */ }
            // 5) The sound-library archive (v3.47) — every imported sound lives in ONE zip
            try
            {
                foreach (var (entryName, cat, fileName) in MainWindow.LibraryArchiveEntries())
                {
                    string captured = entryName;
                    Add(cat, new SoundChoice
                    {
                        Display = System.IO.Path.GetFileNameWithoutExtension(fileName),
                        DecodeName = fileName,
                        PersistKey = "archive:" + entryName,
                        SubLabel = fileName,
                        LoadBytes = () => MainWindow.ReadLibraryArchiveEntry(captured)
                    });
                }
            }
            catch { /* sound library unreadable — fine */ }
            foreach (var list in catalog.Values) list.Sort((a, b) => string.Compare(a.Display, b.Display, StringComparison.OrdinalIgnoreCase));
            return catalog;
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
            var names = LoadPadNames();
            names.Remove(pad);   // no sound on the pad — its face goes back to the default label
            SavePadNames(names);
            _padTitles[pad].Text = PadDefs[pad].Emoji + " " + PadDefs[pad].Name;
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

        private void StreamButton_Click(object sender, RoutedEventArgs e)
        {
            if (_streamWindow == null)
            {
                _streamWindow = new StreamWindow(Streamer) { Owner = this };
                _streamWindow.Closed += (s2, e2) => _streamWindow = null;
                _streamWindow.Show();
            }
            else _streamWindow.Activate();
        }

        private string _streamLabel = "";

        private void StreamTick()
        {
            RefreshStreamButton();
        }

        private void RefreshStreamButton()
        {
            string label = Streamer.IsLive
                ? $"\U0001F534 LIVE {(DateTime.UtcNow - Streamer.LiveSinceUtc):hh:mm:ss}"
                : "\U0001F534 Go Live";
            if (label != _streamLabel)
            {
                _streamLabel = label;
                StreamButton.Content = label;
            }
        }

        private void RequestsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_requestsWindow == null)
            {
                _requestsWindow = new RequestsWindow(_companion) { Owner = this };
                _requestsWindow.Closed += (s2, e2) => _requestsWindow = null;
                _requestsWindow.Show();
            }
            else _requestsWindow.Activate();
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
                        case "nextA": _panelA.RemoteNext(); break;
                        case "prevA": _panelA.RemotePrev(); break;
                        case "nextB": _panelB.RemoteNext(); break;
                        case "prevB": _panelB.RemotePrev(); break;
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
            int pendingReq = _companion.PendingRequestCount;
            if (pendingReq != _lastReqCount)
            {
                _lastReqCount = pendingReq;
                RequestsButton.Content = pendingReq == 0 ? "\U0001F4E8 Requests" : $"\U0001F4E8 Requests ({pendingReq})";
            }
        }

        private string BuildCompanionState()
        {
            static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
            string Deck(DeckPanel p) => FormattableString.Invariant(
                $"{{\"title\":\"{Esc(p.CurrentTitle)}\",\"playing\":{(p.IsPlayingDeck ? "true" : "false")},\"pos\":{p.DeckPositionSeconds:0.0},\"dur\":{p.DeckTotalSeconds:0.0},\"key\":\"{Esc(p.KeyDisplayText)}\",\"next\":\"{Esc(p.NextUpTitle)}\",\"q\":\"{Esc(p.QueueInfoText)}\"}}");
            string ReqJson()
            {
                var list = _companion.SnapshotRequests();
                var sb = new System.Text.StringBuilder("[");
                for (int i = 0; i < list.Count; i++)
                {
                    var r = list[i];
                    if (i > 0) sb.Append(',');
                    sb.Append(FormattableString.Invariant($"{{\"id\":{r.Id},\"name\":\"{Esc(r.Name)}\",\"song\":\"{Esc(r.Song)}\",\"shoutout\":\"{Esc(r.Shoutout)}\"}}"));
                }
                return sb.Append(']').ToString();
            }
            return FormattableString.Invariant(
                $"{{\"a\":{Deck(_panelA)},\"b\":{Deck(_panelB)},\"xfader\":{XfadeSlider.Value:0},\"master\":{DjMasterKnob.Value * 100:0},\"cruise\":{(_cruiseOn ? "true" : "false")},\"mic\":{(_micOn ? "true" : "false")},\"req\":{ReqJson()}}}");
        }

        private void StopBothButton_Click(object sender, RoutedEventArgs e)
        {
            if (_cruiseOn) TurnCruiseOff("⏹ Cruise off — both decks stopped.");
            _panelA.MixStopAndRewindToCue();
            _panelB.MixStopAndRewindToCue();
            SetStatus(_mashupCapture
                ? "⏹ Both decks stopped. The mashup capture kept what played — press 💾 Save Mashup if it was a keeper."
                : "⏹ Both decks stopped and rewound to their cues.");
        }

        private bool _stemPickerInit;

        private void StemComputePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_stemPickerInit || StemComputePicker == null) return;
            var settings = ComputeSettings.Load();
            settings.Mode = StemComputePicker.SelectedIndex switch { 1 => "GPU", 2 => "CPU", _ => "Auto" };
            settings.Save();
            SetStatus($"⚙️ Stem splitting now runs on: {settings.Describe()} — applies to the next Mashup / stem split.");
        }

        // ---------- v3.77: Record My Set ----------
        private bool _setRecording;
        private DateTime _setStartUtc;
        private string _setWavPath = "";
        private readonly List<string> _setTracklist = new List<string>();
        private string? _setLastTitleA;
        private string? _setLastTitleB;

        private void SetRecordButton_Click(object sender, RoutedEventArgs e)
        {
            if (_setRecording) { FinishSetRecording(); return; }
            if (_mashupCapture)
            {
                SetStatus("🎤 A mashup capture owns the recorder right now — save the mashup (or press MASHUP to restart it) before recording a set.");
                return;
            }
            if (_recorder.IsRecording)
            {
                SetStatus("🔴 Record Mix is already rolling — stop it first, then record your set.");
                return;
            }
            try
            {
                string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "BeatSync Sets");
                Directory.CreateDirectory(dir);
                string stamp = DateTime.Now.ToString("yyyy-MM-dd h-mm tt");
                _setWavPath = System.IO.Path.Combine(dir, $"Set {stamp}.wav");
                _recorder.Start(_setWavPath);
                _setRecording = true;
                _setStartUtc = DateTime.UtcNow;
                _setTracklist.Clear();
                _setLastTitleA = null;
                _setLastTitleB = null;
                TrackSetTitles();
                SetRecordButton.Content = "⏹ Stop Set (0:00)";
                SetRecordButton.Background = new SolidColorBrush(Color.FromRgb(0xA1, 0x24, 0x24));
                SetStatus($"🎙 Recording your set — {_setWavPath}. The tracklist writes itself as songs come in.");
            }
            catch (Exception ex)
            {
                _setRecording = false;
                MessageBox.Show("Could not start the set recording: " + ex.Message, "Record My Set");
            }
        }

        private void TrackSetTitles()
        {
            if (!_setRecording) return;
            double elapsed = (DateTime.UtcNow - _setStartUtc).TotalSeconds;
            void Watch(DeckPanel panel, bool isA)
            {
                if (!panel.IsPlayingDeck) return;
                string title = panel.CurrentTitle;
                if (string.IsNullOrWhiteSpace(title)) return;
                if (isA) { if (title == _setLastTitleA) return; _setLastTitleA = title; }
                else { if (title == _setLastTitleB) return; _setLastTitleB = title; }
                _setTracklist.Add($"{FmtTime(elapsed)}  {title}  (Deck {(isA ? "A" : "B")})");
            }
            Watch(_panelA, true);
            Watch(_panelB, false);
        }

        private void SetRecordTick()
        {
            if (!_setRecording) return;
            double elapsed = (DateTime.UtcNow - _setStartUtc).TotalSeconds;
            SetRecordButton.Content = $"⏹ Stop Set ({FmtTime(elapsed)})";
            TrackSetTitles();
        }

        private void FinishSetRecording()
        {
            if (!_setRecording) return;
            _recorder.Stop();
            _setRecording = false;
            double seconds = (DateTime.UtcNow - _setStartUtc).TotalSeconds;
            SetRecordButton.Content = "🎙 Record My Set";
            SetRecordButton.Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x3C));
            string listPath = System.IO.Path.ChangeExtension(_setWavPath, ".txt");
            try
            {
                var lines = new List<string>
                {
                    $"BeatSync Set — {DateTime.Now:ddd MMM d, yyyy, h:mm tt}",
                    $"Duration: {FmtTime(seconds)}   Tracks: {_setTracklist.Count}",
                    "",
                    "TRACKLIST"
                };
                lines.AddRange(_setTracklist.Count > 0 ? _setTracklist : new List<string> { "(no tracks played)" });
                File.WriteAllLines(listPath, lines);
                SetStatus($"🎙 Set saved: {_setWavPath} ({FmtTime(seconds)}, {_setTracklist.Count} tracks) — tracklist beside it: {System.IO.Path.GetFileName(listPath)}.");
            }
            catch (Exception ex)
            {
                SetStatus($"🎙 Set audio saved to {_setWavPath}, but the tracklist couldn't be written: {ex.Message}");
            }
        }

        private void RecordButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mashupCapture)
            {
                SetStatus("🎤 A mashup capture is running on the recorder — press 💾 Save Mashup to keep it in the library, or press MASHUP again to restart the capture.");
                return;
            }
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
            // Key Lock (v3.57): while on, the playback buffer is pre-shifted
            // by the exact opposite of the tempo fader's pitch change, so
            // tempo moves speed without moving pitch (Serato-style master
            // tempo). _keyAppliedTotal is the total shift (key sync + lock
            // compensation) currently baked into the deck's buffer; every
            // key render derives from _pristine, never from a shifted copy.
            internal bool KeyLockOn;
            private double _keyAppliedTotal;
            private CancellationTokenSource? _keyRenderCts;
            private readonly Button _keyLockBtn;
            private Button _suggestBtn = null!;
            private Button _autoGainBtn = null!;
            private TextBlock _autoGainText = null!;
            internal bool AutoGainOn = true;
            private double _autoGainFactor = 1.0;
            private double _autoGainDb;
            private bool _quantize = true;
            private readonly Button _quantizeBtn;
            private int _loadGen;
            internal string PanelName => _name;
            internal int TempoSemitones => (int)Math.Round(12 * Math.Log2(Math.Max(0.5, _deck.Tempo)));
            internal int EffectivePitchClass => DetectedKey == null ? -1 : KeyDetector.Mod12(DetectedKey.PitchClass + KeyShiftSemitones + (KeyLockOn ? 0 : TempoSemitones));
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
            // v3.53: embedded ID tags + cover art for the loaded track.
            private readonly Image _coverImage;
            private readonly TextBlock _tagLine;
            private readonly Button _tagsButton;
            private TagInfo? _tags;
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

            // v3.54 (audit #17): the deck visual re-renders every 100 ms — its
            // bitmap, pens and brushes are cached instead of allocated per tick.
            private RenderTargetBitmap? _visualBmp;
            private readonly DrawingVisual _deckVisual = new DrawingVisual();
            private readonly Dictionary<int, SolidColorBrush> _hueBrushes = new Dictionary<int, SolidColorBrush>();
            private SolidColorBrush? _dimSrc;
            private SolidColorBrush? _dim45;
            private SolidColorBrush? _dim35;
            private SolidColorBrush? _penSrc;
            private Pen? _vizPen12;
            private Pen? _vizPen25;
            private Pen? _vizPen10;

            private static SolidColorBrush FrozenBrush(Color c)
            {
                var b = new SolidColorBrush(c);
                b.Freeze();
                return b;
            }

            private static Pen FrozenPen(Brush brush, double thickness)
            {
                var pen = new Pen(brush, thickness);
                pen.Freeze();
                return pen;
            }

            private static readonly SolidColorBrush Viz2Default = FrozenBrush(Color.FromRgb(0xFF, 0xC8, 0x3D));
            private static readonly SolidColorBrush VuGreen = FrozenBrush(Color.FromRgb(0x1D, 0xB9, 0x54));
            private static readonly SolidColorBrush VuYellow = FrozenBrush(Color.FromRgb(0xFF, 0xC8, 0x3D));
            private static readonly SolidColorBrush VuRed = FrozenBrush(Color.FromRgb(0xFF, 0x52, 0x52));

            private void VizPens(SolidColorBrush viz, out Pen pen12, out Pen pen25, out Pen pen10)
            {
                if (!ReferenceEquals(_penSrc, viz))
                {
                    _penSrc = viz;
                    _vizPen12 = FrozenPen(viz, 1.2);
                    _vizPen25 = FrozenPen(viz, 2.5);
                    _vizPen10 = FrozenPen(viz, 1);
                }
                pen12 = _vizPen12!; pen25 = _vizPen25!; pen10 = _vizPen10!;
            }

            private SolidColorBrush DimBrush(SolidColorBrush viz, double opacity)
            {
                if (!ReferenceEquals(_dimSrc, viz))
                {
                    _dimSrc = viz;
                    var b45 = new SolidColorBrush(viz.Color) { Opacity = 0.45 };
                    b45.Freeze();
                    _dim45 = b45;
                    var b35 = new SolidColorBrush(viz.Color) { Opacity = 0.35 };
                    b35.Freeze();
                    _dim35 = b35;
                }
                return opacity < 0.4 ? _dim35! : _dim45!;
            }
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
            private CheckBox _shuffleBox = null!;
            private CheckBox _repeatBox = null!;
            private readonly Random _shuffleRng = new Random();
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

                // v3.53 header row: cover art thumbnail (when the file has
                // one), title + tag line, and the Tags button.
                var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                _coverImage = new Image { Width = 46, Height = 46, Stretch = Stretch.UniformToFill, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 8, 0) };
                headerGrid.Children.Add(_coverImage);
                var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                _title = new TextBlock { Text = $"{name} — empty", FontSize = 14, FontWeight = FontWeights.Bold, Foreground = accent, TextTrimming = TextTrimming.CharacterEllipsis };
                titleStack.Children.Add(_title);
                _tagLine = new TextBlock { FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x93, 0xA6)), TextTrimming = TextTrimming.CharacterEllipsis, Visibility = Visibility.Collapsed };
                titleStack.Children.Add(_tagLine);
                Grid.SetColumn(titleStack, 1);
                headerGrid.Children.Add(titleStack);
                _tagsButton = MakeButton("🏷️ Tags", 78);
                _tagsButton.VerticalAlignment = VerticalAlignment.Top;
                _tagsButton.IsEnabled = false;
                _tagsButton.ToolTip = "See everything tagged inside this file — title, artist, album, cover art and more.";
                _tagsButton.Click += (s, e) => ShowTags();
                Grid.SetColumn(_tagsButton, 2);
                headerGrid.Children.Add(_tagsButton);
                root.Children.Add(headerGrid);

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

                // v3.90: one-tap transition FX row
                var fxRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
                var spinBtn = MakeButton("⏪ SPINBACK", 118);
                spinBtn.ToolTip = "Spinback: whip the record backwards and cut it — the classic spin-out exit. The deck stops a few seconds back.";
                spinBtn.Click += (s, e) =>
                {
                    _window.CruiseManualOverride();
                    if (_deck.IsPlaying) { _deck.StartSpinback(); _window.SetStatus($"{_name}: spinback!"); }
                    else _window.SetStatus($"{_name}: spinback needs the deck playing.");
                };
                fxRow.Children.Add(spinBtn);
                var echoBtn = MakeButton("🔊 ECHO OUT", 118);
                echoBtn.Margin = new Thickness(6, 0, 0, 0);
                echoBtn.ToolTip = "Echo Out: the track keeps echoing on a beat-synced delay while the dry sound fades away, then the deck stops — a clean exit under the next track.";
                echoBtn.Click += (s, e) =>
                {
                    _window.CruiseManualOverride();
                    if (_deck.IsPlaying) { _deck.StartEchoOut(); _window.SetStatus($"{_name}: echo out…"); }
                    else _window.SetStatus($"{_name}: echo out needs the deck playing.");
                };
                fxRow.Children.Add(echoBtn);
                rightStack.Children.Add(fxRow);

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
                _keyLockBtn = MakeButton("\U0001F512 Key Lock", 88);
                _keyLockBtn.Margin = new Thickness(6, 0, 0, 0);
                _keyLockBtn.ToolTip = "Key Lock: tempo changes speed WITHOUT changing pitch (master tempo). Off = vinyl style, pitch follows tempo.";
                _keyLockBtn.Click += async (s, e) => await ToggleKeyLockAsync();
                keyRow.Children.Add(_keyLockBtn);
                _suggestBtn = MakeButton("\U0001F3AF Suggest", 84);
                _suggestBtn.Margin = new Thickness(6, 0, 0, 0);
                _suggestBtn.ToolTip = "Suggest Next: find library songs that mix well after this deck's track (matching key, close BPM) and load your pick onto the other deck.";
                _suggestBtn.Click += async (s, e) => await SuggestNextAsync();
                keyRow.Children.Add(_suggestBtn);
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
                _autoGainBtn = MakeButton("Auto-Gain: ON", 108);
                _autoGainBtn.Margin = new Thickness(10, 0, 0, 0);
                _autoGainBtn.Background = _accent;
                _autoGainBtn.ToolTip = "Auto-Gain: measures each song's loudness when it loads and trims this deck so every track plays at the same level. Your Gain knob still trims on top.";
                _autoGainBtn.Click += (s, e) => ToggleAutoGain();
                gainRow.Children.Add(_autoGainBtn);
                _autoGainText = new TextBlock { Text = "", Foreground = MutedText, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
                gainRow.Children.Add(_autoGainText);
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
                var srRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
                _shuffleBox = new CheckBox
                {
                    Content = "🔀 Shuffle",
                    Foreground = MutedText,
                    FontSize = 11,
                    Margin = new Thickness(0, 0, 14, 0)
                };
                _repeatBox = new CheckBox
                {
                    Content = "🔁 Repeat track",
                    Foreground = MutedText,
                    FontSize = 11
                };
                srRow.Children.Add(_shuffleBox);
                srRow.Children.Add(_repeatBox);
                root.Children.Add(srRow);

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
                _shuffleBox.ToolTip = "Shuffle: with Auto-play on, when a song ends jump to a random other song in this deck's track list instead of the next one in order.";
                _repeatBox.ToolTip = "Repeat: when this song ends, start the same song over from the top. Works on its own — no need for Auto-play.";

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
                SolidColorBrush viz2 = _visualBrush2 ?? Viz2Default;
                // Wave-based modes draw from the scope trace (flat line when idle)
                if (mode == 7 || mode == 8)
                {
                    var wave = _tap.GetWaveform(220);
                    if (mode == 8)
                    {
                        VizPens(viz, out var pen, out _, out _);
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
                        VizPens(viz, out var pen7, out _, out _);
                        dc.DrawGeometry(DimBrush(viz, 0.45), pen7, geo);
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
                        dc.DrawRectangle(DimBrush(viz, 0.45), null, new Rect(0, H - 4, W, 2));
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
                            int hueKey = (int)hue;
                            if (!_hueBrushes.TryGetValue(hueKey, out var brush))
                            {
                                brush = FrozenBrush(HslToRgb(hueKey, 1.0, 0.55));
                                _hueBrushes[hueKey] = brush;
                            }
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
                        VizPens(viz, out _, out var pen, out var penRing);
                        dc.DrawEllipse(null, penRing, center, 8, 8);
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
                                var brush = frac < 0.55 ? VuGreen : frac < 0.8 ? VuYellow : VuRed;
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
                            dc.DrawRectangle(DimBrush(viz, 0.35), null, new Rect(i * bw + 1, H - h, bw - 2, h));
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
                ApplyTagHeader(bytes);
                RenderWaveform();
                RenderPlatterRing();
                _window.SetStatus($"{_name}: {_deck.Title} loaded ({FmtTime(_deck.TotalSeconds)}). Set the BPM to unlock SYNC and loops.");
                _stemKey = StemBaseName(name) + "-" + bytes.Length;
                _stemGeneration++;
                ShowStemChips(false, "");
                _ = TryLoadCachedStemsAsync();
                StartKeyDetection();
                StartAutoGainAnalysis();
            }

            // ===== v3.68: per-deck auto-gain =====

            private void ToggleAutoGain()
            {
                AutoGainOn = !AutoGainOn;
                _deck.AutoGain = AutoGainOn ? _autoGainFactor : 1.0;
                _autoGainBtn.Content = AutoGainOn ? "Auto-Gain: ON" : "Auto-Gain: OFF";
                _autoGainBtn.Background = AutoGainOn ? _accent : PanelBtn;
                UpdateAutoGainLabel();
                _window.SetStatus(AutoGainOn
                    ? $"{_name}: auto-gain on — this track is trimmed {_autoGainDb:+0.0;-0.0} dB to the standard level."
                    : $"{_name}: auto-gain off — tracks play at their recorded level.");
            }

            private void UpdateAutoGainLabel()
            {
                _autoGainText.Text = !_deck.IsLoaded ? "" : AutoGainOn ? $"auto {_autoGainDb:+0.0;-0.0} dB" : "auto off";
            }

            /// <summary>Measure the loaded track's loudness (RMS + peak off
            /// the decoded buffers) and set the deck's auto-gain trim so
            /// every song plays at the same level: RMS target ≈ -14 dBFS,
            /// gain clamped to about ±10 dB, peak-capped at 0.94.</summary>
            private void StartAutoGainAnalysis()
            {
                int gen = _loadGen;
                _autoGainFactor = 1.0;
                _autoGainDb = 0;
                _deck.AutoGain = 1.0;
                UpdateAutoGainLabel();
                _ = Task.Run(() =>
                {
                    double factor = 1.0, db = 0;
                    try
                    {
                        var (l, r) = _deck.GetChannelBuffers();
                        long n = Math.Min(l.Length, r.Length);
                        if (n > 0)
                        {
                            double sum = 0;
                            float peak = 0;
                            for (long i = 0; i < n; i++)
                            {
                                sum += (double)l[i] * l[i] + (double)r[i] * r[i];
                                float a = Math.Max(Math.Abs(l[i]), Math.Abs(r[i]));
                                if (a > peak) peak = a;
                            }
                            double rms = Math.Sqrt(sum / (2.0 * n));
                            if (rms > 1e-6)
                            {
                                const double target = 0.20;
                                factor = Math.Clamp(target / rms, 0.30, 3.0);
                                if (peak > 0 && peak * factor > 0.94) factor = 0.94 / peak;
                                db = 20 * Math.Log10(factor);
                            }
                        }
                    }
                    catch { factor = 1.0; db = 0; }
                    double f2 = factor, d2 = db;
                    _window.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (gen != _loadGen) return;
                        _autoGainFactor = f2;
                        _autoGainDb = d2;
                        if (AutoGainOn) _deck.AutoGain = f2;
                        UpdateAutoGainLabel();
                    }));
                });
            }

            // v3.53: read the file's embedded tags and dress the deck header
            // with them — tagged "Artist – Title" instead of the bare file
            // name, an album/year/genre line, the cover art thumbnail, and
            // the Tags button lit up.
            private void ApplyTagHeader(byte[] bytes)
            {
                _tags = TagReader.Parse(bytes, _lastName);
                string display = _tags?.DisplayName ?? "";
                _title.Text = display.Length > 0 ? $"{_name} — {display}" : $"{_name} — {_deck.Title}";
                var parts = new List<string>();
                if (_tags != null)
                {
                    if (_tags.Album.Length > 0) parts.Add("💿 " + _tags.Album);
                    if (_tags.Year.Length > 0) parts.Add(_tags.Year);
                    if (_tags.Genre.Length > 0) parts.Add(_tags.Genre);
                    if (_tags.Bpm.Length > 0) parts.Add(_tags.Bpm + " BPM (tag)");
                    if (_tags.KeyTag.Length > 0) parts.Add("Key " + _tags.KeyTag + " (tag)");
                }
                _tagLine.Text = string.Join(" • ", parts);
                _tagLine.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                var pic = _tags?.Picture;
                if (pic != null && pic.Length > 0)
                {
                    try
                    {
                        var bmp = new BitmapImage();
                        using (var ms = new MemoryStream(pic))
                        {
                            bmp.BeginInit();
                            bmp.CacheOption = BitmapCacheOption.OnLoad;
                            bmp.StreamSource = ms;
                            bmp.EndInit();
                        }
                        bmp.Freeze();
                        _coverImage.Source = bmp;
                        _coverImage.Visibility = Visibility.Visible;
                    }
                    catch { _coverImage.Source = null; _coverImage.Visibility = Visibility.Collapsed; }
                }
                else { _coverImage.Source = null; _coverImage.Visibility = Visibility.Collapsed; }
                _tagsButton.IsEnabled = _tags != null;
            }

            private void ShowTags()
            {
                if (_tags == null) return;
                var w = new TagsWindow(_tags, _lastName, _deck.TotalSeconds) { Owner = _window };
                w.Show();
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
                // v3.54 (audit #24): dispose the replaced CTS — the previous
                // split has finished by here (the busy guard above), so its
                // token is no longer in use.
                var oldCts = _stemCts;
                _stemCts = new CancellationTokenSource();
                oldCts?.Cancel();
                oldCts?.Dispose();
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
                _keyAppliedTotal = 0;
                _keyRenderCts?.Cancel();
                _keySyncBtn.Background = PanelBtn;
                _keyLockBtn.Background = KeyLockOn ? _accent : PanelBtn;
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
                            DjWindow.SaveKeyCacheEntry(cacheKey, new KeyCacheEntry(result.PitchClass, result.IsMinor));
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
                    KeyShiftSemitones = 0;
                    _keySyncBtn.Background = PanelBtn;
                    await ReRenderKeyBufferAsync();
                    _window.SetStatus(KeyLockOn
                        ? $"{_name}: key sync off — Key Lock is still holding the pitch at tempo."
                        : $"{_name}: key sync off - original key restored.");
                    _window.RefreshKeyCompatibility();
                    return;
                }
                var otherPanel = _window.OtherPanel(this);
                if (DetectedKey == null) { _window.SetStatus($"{_name}: key sync needs a detected key on this deck first."); return; }
                if (otherPanel.DetectedKey == null) { _window.SetStatus($"{_name}: key sync needs the other deck's key too - load a track there."); return; }
                int dd = KeyDetector.Mod12(otherPanel.EffectivePitchClass - DetectedKey.PitchClass);
                int shift = dd > 6 ? dd - 12 : dd;
                if (shift == 0) { _window.SetStatus($"{_name}: already in key with the other deck."); return; }
                _keySyncBtn.IsEnabled = false;
                try
                {
                    _window.SetStatus($"{_name}: shifting key {shift:+0;-0} semitones - rendering...");
                    KeyShiftSemitones = shift;
                    await ReRenderKeyBufferAsync();
                    if (Math.Abs(_keyAppliedTotal - KeyTargetShift()) < 0.05)
                    {
                        _keySyncBtn.Background = _accent;
                        _window.SetStatus($"{_name}: key synced {shift:+0;-0} semitones to match the other deck.");
                    }
                    else
                    {
                        // the render was discarded (track changed / another
                        // render in flight) — don't leave a phantom shift
                        KeyShiftSemitones = 0;
                        UpdateKeyLabel();
                        _window.SetStatus($"{_name}: key shift discarded - the track changed.");
                    }
                    _window.RefreshKeyCompatibility();
                }
                finally { _keySyncBtn.IsEnabled = true; }
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

            /// <summary>Append one file to this deck's track list (the
            /// 🎵 Song Library sends songs here).</summary>
            internal void QueuePath(string path)
            {
                _queuePaths.Add(path);
                RefreshQueueList();
                if (_queueIndex < 0)
                    _window.SetStatus($"{_name}: {_queuePaths.Count} song(s) queued — double-click one (or press Next) to load it.");
            }
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

            // v3.86: phone-companion queue stepping - same moves as the
            // deck panel Prev/Next buttons, plus a first-load from index 0
            // when nothing from the queue is loaded yet.
            internal void RemoteNext()
            {
                if (_queueIndex + 1 < _queuePaths.Count) LoadQueueItem(_queueIndex + 1, true);
                else if (_queueIndex < 0 && _queuePaths.Count > 0) LoadQueueItem(0, true);
            }

            internal void RemotePrev()
            {
                if (_queueIndex > 0) LoadQueueItem(_queueIndex - 1, true);
            }

            internal string NextUpTitle => _queueIndex + 1 < _queuePaths.Count
                ? System.IO.Path.GetFileName(_queuePaths[_queueIndex + 1]) : "";

            internal string QueueInfoText => _queuePaths.Count == 0 ? ""
                : $"{Math.Max(_queueIndex + 1, 0)}/{_queuePaths.Count}";
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

            // ===== v3.67: Suggest Next Song =====
            internal sealed record SuggestPick(SoundChoice Choice, byte[] Bytes, string Camelot, double Bpm, int Score);

            private bool _suggestBusy;

            /// <summary>Find library songs that mix well after this deck's
            /// track — Camelot-compatible key first, close BPM second —
            /// analyzing (and caching) library tracks as needed, then offer
            /// the ranked list in a window that loads the pick onto the
            /// OTHER deck.</summary>
            internal async Task SuggestNextAsync()
            {
                if (_suggestBusy) return;
                if (!_deck.IsLoaded || _lastBytes == null) { _window.SetStatus($"{_name}: load a track first, then I'll suggest what follows it."); return; }
                if (DetectedKey == null)
                {
                    StartKeyDetection();
                    _window.SetStatus($"{_name}: detecting this track's key first — give it a second, then hit 🎯 Suggest again.");
                    return;
                }
                if (!await EnsureBpmAsync())
                {
                    _window.SetStatus($"{_name}: I need this track's BPM to suggest a match — type it or TAP it in, then hit 🎯 Suggest.");
                    return;
                }
                _suggestBusy = true;
                _suggestBtn.IsEnabled = false;
                try
                {
                    string srcCamelot = EffectiveCamelot ?? "";
                    double srcBpm = _deck.Bpm;
                    string srcKey = _stemKey;
                    var target = _window.OtherPanel(this);
                    string otherKey = target._stemKey;
                    var catalog = _window.BuildSoundCatalog();
                    var pool = new System.Collections.Generic.List<SoundChoice>();
                    foreach (var kv in catalog)
                    {
                        if (kv.Key == SoundCategorizer.Stems || kv.Key.StartsWith("Stems · ", StringComparison.Ordinal)) continue;
                        pool.AddRange(kv.Value);
                    }
                    _window.SetStatus($"{_name}: analyzing your library for a good follow-up… (first run is the slow one — key + BPM results are cached, so it gets faster)");
                    var picks = await Task.Run(() => AnalyzeCandidates(pool, srcCamelot, srcBpm, srcKey, otherKey));
                    if (picks.Count == 0)
                    {
                        _window.SetStatus($"{_name}: no library songs with a detectable key yet — import a few full songs and try again.");
                        return;
                    }
                    _window.SetStatus($"{_name}: {picks.Count} songs mix well after this one — pick one to load it onto {target.PanelName}.");
                    new SuggestWindow(_window, this, target, picks).Show();
                }
                catch (Exception ex) { _window.SetStatus($"{_name}: suggest failed — {ex.Message}"); }
                finally { _suggestBusy = false; _suggestBtn.IsEnabled = true; }
            }

            private System.Collections.Generic.List<SuggestPick> AnalyzeCandidates(
                System.Collections.Generic.List<SoundChoice> pool, string srcCamelot, double srcBpm, string srcKey, string otherKey)
            {
                var keyCache = DjWindow.LoadKeyCache();
                var bpmCache = DjWindow.LoadBpmCache();
                var picks = new System.Collections.Generic.List<SuggestPick>();
                int examined = 0;
                foreach (var choice in pool)
                {
                    if (examined >= 48) break;
                    byte[]? bytes;
                    try { bytes = choice.LoadBytes(); } catch { bytes = null; }
                    if (bytes == null || bytes.Length == 0) continue;
                    examined++;
                    string stemKey = StemBaseName(choice.DecodeName) + "-" + bytes.Length;
                    if (stemKey == srcKey || stemKey == otherKey) continue;

                    KeyDetector.KeyResult? kr = null;
                    double seconds = 0;
                    if (keyCache.TryGetValue(stemKey, out var hit))
                        kr = new KeyDetector.KeyResult(hit.Pc, hit.Minor, KeyDetector.KeyNameFor(hit.Pc, hit.Minor), KeyDetector.CamelotFor(hit.Pc, hit.Minor), 1);
                    if (kr == null)
                    {
                        var mono = DecodeMono(bytes, choice.DecodeName, out seconds);
                        if (mono == null || mono.Length == 0) continue;
                        kr = KeyDetector.Detect(mono);
                        if (kr != null) DjWindow.SaveKeyCacheEntry(stemKey, new KeyCacheEntry(kr.PitchClass, kr.IsMinor));
                    }
                    if (kr == null) continue;
                    if (seconds > 0 && seconds < 45) continue; // a hit or a loop, not a next song

                    double bpm = 0;
                    if (bpmCache.TryGetValue(stemKey, out var cb) && cb > 0) bpm = cb;
                    else
                    {
                        string type = System.IO.Path.GetExtension(choice.DecodeName).TrimStart('.').ToLowerInvariant();
                        try
                        {
                            var (detected, _, _) = BeatDetector.Detect(bytes, type);
                            if (detected >= 50 && detected <= 200) { bpm = Math.Round(detected * 2) / 2; DjWindow.SaveBpmCacheEntry(stemKey, bpm); }
                        }
                        catch { }
                    }
                    string camelot = KeyDetector.CamelotFor(kr.PitchClass, kr.IsMinor);
                    picks.Add(new SuggestPick(choice, bytes, camelot, bpm, ScoreMatch(srcCamelot, srcBpm, camelot, bpm)));
                }
                picks.Sort((a, b) => b.Score.CompareTo(a.Score));
                if (picks.Count > 10) picks.RemoveRange(10, picks.Count - 10);
                return picks;
            }

            /// <summary>Decode a candidate file to mono 44.1k samples for key
            /// detection (channel-averaged by hand — no format helpers).</summary>
            private static float[]? DecodeMono(byte[] bytes, string name, out double seconds)
            {
                seconds = 0;
                try
                {
                    string type = System.IO.Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
                    using var reader = AudioReaderFactory.Open(bytes, type);
                    var sp = reader.ToSampleProvider();
                    int ch = Math.Max(1, sp.WaveFormat.Channels);
                    NAudio.Wave.ISampleProvider p441 = sp.WaveFormat.SampleRate == 44100
                        ? sp
                        : new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(sp, 44100);
                    var all = new System.Collections.Generic.List<float>();
                    var buf = new float[44100 * 2 * ch];
                    int read;
                    while ((read = p441.Read(buf, 0, buf.Length)) > 0)
                        for (int i = 0; i + ch - 1 < read; i += ch)
                        {
                            float s = 0;
                            for (int c = 0; c < ch; c++) s += buf[i + c];
                            all.Add(s / ch);
                        }
                    seconds = all.Count / 44100.0;
                    return all.ToArray();
                }
                catch { return null; }
            }

            private static int ScoreMatch(string srcCamelot, double srcBpm, string candCamelot, double candBpm)
            {
                int keyScore = 35;
                if (ParseCamelot(srcCamelot, out int sn, out char sl) && ParseCamelot(candCamelot, out int cn, out char cl))
                {
                    int dn = Math.Abs(sn - cn);
                    dn = Math.Min(dn, 12 - dn);
                    bool sameLetter = sl == cl;
                    if (dn == 0 && sameLetter) keyScore = 100;
                    else if (dn == 0) keyScore = 86;               // relative major/minor
                    else if (dn == 1 && sameLetter) keyScore = 88; // energy shift, up or down
                    else if (dn == 1) keyScore = 62;
                    else keyScore = Math.Max(28, (sameLetter ? 78 : 70) - dn * 14);
                }
                int bpmScore = 45;
                if (srcBpm > 0 && candBpm > 0)
                {
                    double r = candBpm / srcBpm;
                    while (r < 0.85) r *= 2;
                    while (r > 1.18) r /= 2;
                    double diff = Math.Abs(r - 1);
                    bpmScore = diff <= 0.06 ? (int)(100 - diff * 300) : Math.Max(0, (int)(82 - (diff - 0.06) * 400));
                }
                return (int)Math.Round(keyScore * 0.6 + bpmScore * 0.4);
            }

            private static bool ParseCamelot(string s, out int num, out char letter)
            {
                num = 0;
                letter = 'A';
                if (string.IsNullOrEmpty(s) || s.Length < 2) return false;
                letter = char.ToUpperInvariant(s[s.Length - 1]);
                return int.TryParse(s.Substring(0, s.Length - 1), out num);
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

            /// <summary>Where Auto-play goes next: a random other track when
            /// Shuffle is on, otherwise the next one in order (-1 = stop).</summary>
            private int NextQueueIndex()
            {
                if (_queuePaths.Count == 0) return -1;
                if (_shuffleBox != null && _shuffleBox.IsChecked == true && _queuePaths.Count > 1)
                {
                    int n;
                    do { n = _shuffleRng.Next(_queuePaths.Count); } while (n == _queueIndex);
                    return n;
                }
                return _queueIndex + 1 < _queuePaths.Count ? _queueIndex + 1 : -1;
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

            internal void MidiPlayPause() => PlayPause();
            internal void MidiCue() => Cue();

            private void PlayPause()
            {
                _window.CruiseManualOverride();
                _deck.CancelFx();
                if (!_deck.IsLoaded) return;
                if (!_deck.IsPlaying && _deck.PositionSeconds >= _deck.TotalSeconds - 0.05)
                    _deck.PositionSeconds = 0;
                _deck.IsPlaying = !_deck.IsPlaying;
                if (_deck.IsPlaying) _window._owner.EnsureOutputRunning();
            }

            private void Cue()
            {
                _window.CruiseManualOverride();
                _deck.CancelFx();
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
                if (KeyLockOn) DebounceKeyRender();
            }

            // The total pitch shift the deck's buffer SHOULD carry right
            // now: the key-sync shift plus, with Key Lock on, the exact
            // inverse of the tempo fader's own pitch change.
            private double KeyTargetShift()
                => KeyShiftSemitones + (KeyLockOn ? -12.0 * Math.Log2(Math.Max(0.5, _deck.Tempo)) : 0.0);

            private void DebounceKeyRender()
            {
                var old = _keyRenderCts;
                _keyRenderCts = new CancellationTokenSource();
                old?.Cancel();
                old?.Dispose();
                var token = _keyRenderCts.Token;
                _ = Task.Run(async () =>
                {
                    try { await Task.Delay(300, token); }
                    catch { return; }
                    if (token.IsCancellationRequested) return;
                    await _window.Dispatcher.InvokeAsync(new Action(() => { _ = ReRenderKeyBufferAsync(); }));
                });
            }

            private async Task ToggleKeyLockAsync()
            {
                if (_keyBusy) { _window.SetStatus($"{_name}: a key render is already running — give it a second."); return; }
                KeyLockOn = !KeyLockOn;
                _keyLockBtn.Background = KeyLockOn ? _accent : PanelBtn;
                _window.SetStatus(KeyLockOn
                    ? $"{_name}: Key Lock ON — tempo now changes speed only, the pitch stays put."
                    : $"{_name}: Key Lock OFF — pitch follows the tempo again (vinyl style).");
                _window.RefreshKeyCompatibility();
                await ReRenderKeyBufferAsync();
            }

            /// <summary>
            /// Rebuild the deck's playback buffer from the pristine original
            /// at KeyTargetShift() and swap it in. The single render path
            /// behind Key Sync AND Key Lock, so the two compose instead of
            /// fighting over the buffer.
            /// </summary>
            private async Task ReRenderKeyBufferAsync()
            {
                if (_keyBusy) return;
                double target = KeyTargetShift();
                if (Math.Abs(target - _keyAppliedTotal) < 0.05) { UpdateKeyLabel(); return; }
                if (!_deck.IsLoaded) { _keyAppliedTotal = target; return; }
                if (_pristine == null)
                {
                    // Nothing baked in yet, so what the deck holds IS the
                    // original — pin it as the render source.
                    if (Math.Abs(_keyAppliedTotal) > 0.001) return;
                    _pristine = _deck.SnapshotInterleaved();
                    _pristineStems = _deck.SnapshotStemsInterleaved();
                }
                _keyBusy = true;
                int gen = _loadGen;
                int deckGen = _deck.LoadGeneration;
                try
                {
                    float[] newMix;
                    float[][]? newStems = null;
                    if (Math.Abs(target) < 0.01)
                    {
                        newMix = _pristine;
                        newStems = _pristineStems;
                    }
                    else
                    {
                        var srcMix = _pristine;
                        var srcStems = _pristineStems;
                        newMix = await Task.Run(() => PitchShifter.Shift(srcMix, target));
                        if (srcStems != null)
                        {
                            var ss = srcStems;
                            newStems = await Task.Run(() => ss.Select(b => PitchShifter.Shift(b, target)).ToArray());
                        }
                    }
                    if (gen != _loadGen || deckGen != _deck.LoadGeneration) return;
                    _deck.SwapBuffers(newMix, newStems);
                    _keyAppliedTotal = target;
                    if (Math.Abs(target) < 0.01 && KeyShiftSemitones == 0 && !KeyLockOn)
                    {
                        _pristine = null;
                        _pristineStems = null;
                    }
                    UpdateKeyLabel();
                    _window.RefreshKeyCompatibility();
                }
                catch (Exception ex) { _window.SetStatus($"{_name}: key render failed: " + ex.Message); }
                finally
                {
                    _keyBusy = false;
                    // the tempo moved again while this render ran — catch up
                    if (KeyLockOn && Math.Abs(KeyTargetShift() - _keyAppliedTotal) >= 0.05)
                        DebounceKeyRender();
                }
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
                else
                {
                    // v3.54 (audit #20): don't fail silently — a mistyped BPM
                    // used to keep the old value with no sign anything was
                    // wrong. Restore the real value and say so.
                    _bpmBox.Text = _deck.Bpm > 0 ? $"{_deck.Bpm:0}" : "";
                    _window.SetStatus($"{_name}: that BPM didn't take — type a number between 40 and 240, or clear the box to unset it.");
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
                bool trackEnded = _wasPlayingLastTick && !_deck.IsPlaying
                    && _queueIndex >= 0
                    && _deck.IsLoaded && _deck.TotalSeconds > 0
                    && _deck.PositionSeconds >= _deck.TotalSeconds - 1.0;
                if (trackEnded && _repeatBox != null && _repeatBox.IsChecked == true)
                {
                    // Repeat wins first: start the same track over.
                    LoadQueueItem(_queueIndex, true);
                }
                else if (trackEnded && _autoNextBox != null && _autoNextBox.IsChecked == true)
                {
                    int next = NextQueueIndex();
                    if (next >= 0) LoadQueueItem(next, true);
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
                    using (var dc = _deckVisual.RenderOpen())
                    {
                        DrawDeckVisual(dc, _visualPicker.SelectedIndex, W, H);
                    }
                    _visualBmp ??= new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
                    _visualBmp.Render(_deckVisual);
                    if (!ReferenceEquals(_visualImage.Source, _visualBmp)) _visualImage.Source = _visualBmp;
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
