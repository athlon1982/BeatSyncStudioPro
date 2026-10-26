using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Microsoft.Win32;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace BeatSyncStudioPro
{
    public partial class MainWindow : Window
    {
        public Dictionary<string, byte[]> ZipSounds { get; private set; } = new Dictionary<string, byte[]>();

        /// <summary>Library category per sound name (SoundCategorizer values). Saved in the project file.</summary>
        public Dictionary<string, string> SoundCategories { get; private set; } = new Dictionary<string, string>();

        // Live timeline voices, so Stop can cut sounds that are still ringing out
        private readonly System.Collections.Generic.HashSet<KillableSampleProvider> _liveVoices = new System.Collections.Generic.HashSet<KillableSampleProvider>();
        private readonly object _voicesLock = new object();

        private void StopAllVoices()
        {
            KillableSampleProvider[] snapshot;
            lock (_voicesLock) { snapshot = _liveVoices.ToArray(); _liveVoices.Clear(); }
            foreach (var v in snapshot) v.Kill();
        }
        public List<FixedBlock> TimelineBlocks { get; private set; } = new List<FixedBlock>();
        public Dictionary<string, string> VideoAssets { get; private set; } = new Dictionary<string, string>();
        public Dictionary<string, string> ImageAssets { get; private set; } = new Dictionary<string, string>();

        public string? VideoPath { get; private set; } = null;
        public int AudioTrackCount { get; private set; } = 3;
        public double MasterVolume { get; private set; } = 0.8;

        private bool isPlayingTimeline = false;
        private bool isMuted = false;
        private double savedVolume = 0.8;

        private IWavePlayer? outputDevice;
        private MixingSampleProvider? mixerEngine;

        // Per-track mixer chains (volume + pan per track; last chain = VIDEO AUDIO lane)
        private readonly List<MixingSampleProvider> _trackMixers = new List<MixingSampleProvider>();
        private readonly List<VolumeSampleProvider> _trackGains = new List<VolumeSampleProvider>();
        private readonly List<StereoPanSampleProvider> _trackPanFx = new List<StereoPanSampleProvider>();
        private readonly List<ISampleProvider> _trackChainOutputs = new List<ISampleProvider>();
        public List<double> TrackVolumeValues { get; } = new List<double>();
        public List<double> TrackPanValues { get; } = new List<double>();
        public List<TrackFxSettings> TrackFxValues { get; } = new List<TrackFxSettings>();

        private OutputWindow? _outputWindow;
        private DjWindow? _djWindow;
        private SongLibraryWindow? _songLibraryWindow;
        private SetBuilderWindow? _setBuilderWindow;

        /// <summary>Master-bus tap feeding the Output window's visualizations.</summary>
        public AudioTapSampleProvider? AudioTap { get; private set; }

        /// <summary>Monitor-bus FX settings (EQ + compressor + width) — what you hear only, never exported.</summary>
        public MonitorFxSettings MonitorSettings { get; private set; } = new MonitorFxSettings();
        private MonitorFxWindow? _monitorFxWindow;

        // ---- Beat grid ----
        public double ProjectBpm { get; set; }
        public double BeatOffsetSeconds { get; set; }
        public bool SnapToBeatEnabled { get; set; } = true;

        /// <summary>Snap a timeline X (pixels) to the nearest beat line when within 8px and snapping is on.</summary>
        public double SnapX(double x)
        {
            if (!SnapToBeatEnabled || ProjectBpm <= 0) return x;
            double periodPx = 60.0 / ProjectBpm * FixedBlock.PixelsPerSecond;
            double offsetPx = BeatOffsetSeconds * FixedBlock.PixelsPerSecond;
            double n = Math.Round((x - offsetPx) / periodPx);
            double snapped = offsetPx + n * periodPx;
            return Math.Abs(snapped - x) <= 8 ? snapped : x;
        }

        private (double Bpm, double OffsetTimeline, double Confidence)? ComputeGridFromClip(
            FixedBlock block, double bpm, double fileOffset, double confidence)
        {
            if (bpm <= 0) return null;
            double period = 60.0 / bpm;
            double k = Math.Ceiling((block.SourceStartSeconds - fileOffset) / period - 1e-6);
            double beatFile = fileOffset + k * period;
            double offsetTimeline = block.StartX / FixedBlock.PixelsPerSecond + (beatFile - block.SourceStartSeconds) / Math.Max(0.01, block.Speed);
            double cycle = 4 * period; // normalize while preserving the downbeat phase
            offsetTimeline = ((offsetTimeline % cycle) + cycle) % cycle;
            return (bpm, offsetTimeline, confidence);
        }

        /// <summary>Detect the beat grid from the first audio clip (used by the Beat Grid dialog).</summary>
        public async System.Threading.Tasks.Task<(double Bpm, double OffsetTimeline, double Confidence)?> DetectBeatsForGridAsync()
        {
            var block = TimelineBlocks.FirstOrDefault(b => b.IsAudio && b.RawBytes != null && b.RawBytes.Length > 0);
            if (block == null) return null;
            var bytes = block.RawBytes;
            var type = block.SoundType;
            // Audit Bug 3 (v3.62): a corrupt/odd clip must read as "no grid
            // found", never as a crash — this feeds async-void callers.
            try
            {
                var (bpm, fileOffset, conf) = await System.Threading.Tasks.Task.Run(() => BeatDetector.Detect(bytes, type));
                if (bpm <= 0) return null;
                return ComputeGridFromClip(block, bpm, fileOffset, conf);
            }
            catch { return null; }
        }

        /// <summary>Detect the beat grid from one specific clip and apply it (clip context menu).</summary>
        public async void DetectBeatsFromClip(FixedBlock block)
        {
            if (block.RawBytes == null || block.RawBytes.Length == 0) return;
            // Audit Bug 3 (v3.62): async void — an unhandled throw here takes
            // the whole app down, so the detector gets a guard and a voice.
            try
            {
                var bytes = block.RawBytes;
                var type = block.SoundType;
                var (bpm, fileOffset, conf) = await System.Threading.Tasks.Task.Run(() => BeatDetector.Detect(bytes, type));
                if (bpm <= 0)
                {
                    MessageBox.Show("No steady beat found in that clip — you can set the grid by hand in Tracks → Beat Grid…", "Detect Beats");
                    return;
                }
                var grid = ComputeGridFromClip(block, bpm, fileOffset, conf);
                if (grid == null) return;
                ProjectBpm = grid.Value.Bpm;
                BeatOffsetSeconds = grid.Value.OffsetTimeline;
                RenderStudioGridLanes();
                MessageBox.Show($"Beat grid set: {grid.Value.Bpm:0.0} BPM (confidence {grid.Value.Confidence * 100:0}%).\n\nBeat lines are on the timeline and clips snap to them while dragging.",
                    "Detect Beats");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Beat detection hit a problem with that clip: " + ex.Message + "\n\nYou can set the grid by hand in Tracks → Beat Grid…", "Detect Beats");
            }
        }

        private void MenuBeatGrid_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new BeatGridWindow(this) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                ProjectBpm = dlg.Bpm;
                BeatOffsetSeconds = dlg.OffsetSeconds;
                RenderStudioGridLanes();
            }
        }

        private void MenuSnap_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem item) SnapToBeatEnabled = item.IsChecked;
        }

        // ---- A–B loop region (pixels on the timeline) ----
        public double? LoopStartX { get; private set; }
        public double? LoopEndX { get; private set; }
        public bool LoopEnabled { get; private set; }
        private double _currentPlayX;
        private ISampleProvider? _djProvider;

        /// <summary>Route the DJ decks' master mix into the main engine (a reopened DJ window swaps its new chain in).</summary>
        public void AttachDjMixer(ISampleProvider provider)
        {
            if (mixerEngine == null) return;
            if (ReferenceEquals(_djProvider, provider)) return;
            if (_djProvider != null) mixerEngine.RemoveMixerInput(_djProvider);
            mixerEngine.AddMixerInput(provider);
            _djProvider = provider;
        }

        /// <summary>Remove the DJ decks' master mix from the main engine (DJ window closed).</summary>
        public void DetachDjMixer(ISampleProvider provider)
        {
            if (mixerEngine == null || !ReferenceEquals(_djProvider, provider)) return;
            mixerEngine.RemoveMixerInput(provider);
            _djProvider = null;
        }

        /// <summary>
        /// The decks share the master output with the timeline; if the
        /// timeline's pause froze that output, resume it so the decks can
        /// always play when their own Play is pressed.
        /// </summary>
        public void EnsureOutputRunning()
        {
            try { if (outputDevice?.PlaybackState == PlaybackState.Paused) outputDevice.Play(); } catch { }
        }
        private MixerWindow? _mixerWindow;

        // Pause state for the timeline transport
        private System.Diagnostics.Stopwatch? _playStopwatch;
        private bool _isPaused;
        private Line? playhead;
        private Rectangle? playheadHandle;
        private Rectangle? _loopBand;
        private Rectangle? _loopBrace;
        private double _lastTimelineHeight = 450;
        private int _rulerDrawnSeconds = 25;
        private double _rulerDrawnPps = 100;
        private CancellationTokenSource? playbackCancelToken;
        private string? draggingSoundName = null;
        private Point dragStartPoint;

        private static readonly string[] VideoExtensions = new[] { ".mp4", ".avi", ".wmv", ".mkv", ".mov", ".flv", ".webm", ".mpg", ".mpeg", ".ogv", ".3gp", ".m4v" };
        private static readonly string[] ImageExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tiff" };
        private static readonly string[] AudioExtensions = new[] { ".wav", ".mp3", ".ogg", ".mid", ".midi" };

        public MainWindow()
        {
            InitializeComponent();
            // The brand-bar version chip reads the REAL app version so
            // it can never go stale again (it sat at v3.65 through 3.71).
            var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            if (ver != null) VersionChip.Text = $"v{ver.Major}.{ver.Minor}";
            // v3.74: autosave + crash recovery. A flag file lives while
            // the app runs; a clean exit deletes it (and the autosave).
            // Finding it at startup means last time ended badly.
            try
            {
                Directory.CreateDirectory(AppDataDir);
                bool crashedLastTime = File.Exists(SessionFlagPath);
                File.WriteAllText(SessionFlagPath, DateTime.UtcNow.ToString("o"));
                Closing += (s, e) =>
                {
                    try { File.Delete(SessionFlagPath); } catch { }
                    try { File.Delete(AutosavePath); } catch { }
                    try { File.Delete(AutosaveMetaPath); } catch { }
                };
                _autosaveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
                _autosaveTimer.Tick += (s, e) => TryAutosaveNow();
                _autosaveTimer.Start();
                if (crashedLastTime && File.Exists(AutosavePath))
                    Loaded += RecoverCrashOnce;
            }
            catch { /* recovery plumbing must never block startup */ }
            InitializeAudioEngine();
            RenderTimeRulerHeaders();
            RenderStudioGridLanes();
            MasterVolumeSlider.ValueChanged += (s, e) => UpdateVolumeDisplay();

            // Start screen once, at launch, when nothing is loaded yet
            Loaded += (s, e) =>
            {
                if (_startScreenShownOnce) return;
                _startScreenShownOnce = true;
                if (StartScreenEnabled && TimelineBlocks.Count == 0) OpenStartScreen();
            };
            MasterVideoPlayer.MediaOpened += MasterVideoPlayer_MediaOpened;

            // v3.44: stems land in the library under "Stems" whenever a split finishes
            StemSeparator.StemsAvailable += OnStemsAvailable;
            RefreshLibraryBrowser();
            // v3.47: downloads show in the title bar (name, %, speed, ETA) while they run.
            var titleTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            titleTimer.Tick += (s, e) => UpdateDownloadTitle();
            titleTimer.Start();
        }

        /// <summary>v3.44: file a set of split stems into the asset library under
        /// the "Stems" category so they can be dragged onto the timeline like any
        /// other sound. Duplicates (same song re-split or cache-loaded) are skipped.</summary>
        /// <summary>Group rank for the library: every "Stems · X" group sits at the Stems slot.</summary>
        private static int LibraryGroupRank(string category)
        {
            if (category == SoundCategorizer.Stems || category.StartsWith("Stems · ", StringComparison.Ordinal))
                return Array.IndexOf(LibraryGroupOrder, SoundCategorizer.Stems);
            int r = Array.IndexOf(LibraryGroupOrder, category);
            return r < 0 ? LibraryGroupOrder.Length : r;
        }

        private void OnStemsAvailable(string folder, string baseName)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnStemsAvailable(folder, baseName)));
                return;
            }
            foreach (var stem in StemSeparator.StemNames)
            {
                string nice = char.ToUpperInvariant(stem[0]) + stem.Substring(1);
                string display = $"{baseName} — {nice}.wav";
                if (ZipSounds.ContainsKey(display)) continue;
                string path = System.IO.Path.Combine(folder, $"{baseName} ({stem}).wav");
                try
                {
                    if (!File.Exists(path)) continue;
                    AddToLibraryOnly(File.ReadAllBytes(path), display, SoundCategorizer.StemCategory(stem));
                }
                catch { /* one bad stem file must not block the others */ }
            }
            _externalsBuilt = false;   // new stems just landed in the cache — rescan next refresh
        }

        private void UpdateVolumeDisplay()
        {
            if (VolumePercent != null)
                VolumePercent.Text = $"{(int)(MasterVolumeSlider.Value * 100)}%";
        }

        private void InitializeAudioEngine()
        {
            try
            {
                outputDevice = new WaveOutEvent();
                mixerEngine = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2)) { ReadFully = true };
                // Monitor chain: EQ -> compressor -> width -> tap (visuals hear what you hear)
                var monitorEq = new EqSampleProvider(mixerEngine, MonitorSettings.Eq);
                var monitorComp = new CompressorSampleProvider(monitorEq, MonitorSettings);
                var monitorWidth = new StereoWidthSampleProvider(monitorComp, MonitorSettings);
                AudioTap = new AudioTapSampleProvider(monitorWidth);
                outputDevice.Init(AudioTap);
                outputDevice.Play();
                BuildTrackChains();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"🔊 Audio Engine Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Draw the time ruler at the current zoom. The tick step adapts: whole
        /// seconds when zoomed out, splitting into halves / quarters / tenths
        /// of a second as you zoom in, so the gap between 1s and 2s breaks
        /// down into fractions (1.25s, 1.5s, …) instead of staying blank.
        /// </summary>
        private void RenderTimeRulerHeaders()
        {
            if (RulerTicksCanvas == null) return;
            RulerTicksCanvas.Children.Clear();
            double pps = FixedBlock.PixelsPerSecond;
            double totalSeconds = Math.Max(25, Math.Ceiling(TimelineLengthPx / pps));
            _rulerDrawnSeconds = (int)Math.Ceiling(totalSeconds);
            _rulerDrawnPps = pps;

            double[] steps = { 0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60 };
            double step = steps[steps.Length - 1];
            foreach (var s in steps)
            {
                if (s * pps >= 80) { step = s; break; }
            }

            var minorBrush = (Brush)new BrushConverter().ConvertFromString("#6B7690")!;
            var majorBrush = (Brush)new BrushConverter().ConvertFromString("#A0A5B5")!;
            int tickCount = (int)Math.Ceiling(totalSeconds / step);
            for (int i = 0; i <= tickCount; i++)
            {
                double t = i * step;
                double xPos = t * pps;
                bool wholeSecond = Math.Abs(t - Math.Round(t)) < 1e-6;
                Line tickLine = new Line
                {
                    X1 = xPos, Y1 = wholeSecond ? 18 : 28, X2 = xPos, Y2 = 40,
                    Stroke = wholeSecond ? majorBrush : minorBrush, StrokeThickness = 1
                };
                RulerTicksCanvas.Children.Add(tickLine);

                string label;
                if (t >= 60) label = FmtTime(t);
                else if (step < 1) label = t.ToString("0.00").TrimEnd('0').TrimEnd('.') + "s";
                else label = $"{(int)t:D2}s";
                TextBlock txtTime = new TextBlock { Text = label, Foreground = majorBrush, FontWeight = FontWeights.Bold, FontSize = 9 };
                Canvas.SetLeft(txtTime, xPos - 12);
                Canvas.SetTop(txtTime, 2);
                RulerTicksCanvas.Children.Add(txtTime);
            }
        }

        /// <summary>Mouse-wheel zoom for the timeline, anchored at the cursor:
        /// the moment under the mouse stays under the mouse while everything
        /// around it stretches or shrinks.</summary>
        private void TimelineScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            double oldPps = FixedBlock.PixelsPerSecond;
            double factor = Math.Pow(1.25, e.Delta / 120.0);
            double newPps = Math.Clamp(oldPps * factor, 10, 1000);
            if (Math.Abs(newPps - oldPps) < 0.01) { e.Handled = true; return; }

            double anchorSeconds = e.GetPosition(TimelineCanvas).X / oldPps;
            double mouseViewportX = e.GetPosition(TimelineScrollViewer).X;
            ApplyTimelineZoom(newPps);
            double newAnchorX = anchorSeconds * newPps;
            Dispatcher.BeginInvoke(new Action(() =>
                TimelineScrollViewer.ScrollToHorizontalOffset(Math.Max(0, newAnchorX - mouseViewportX))));
            e.Handled = true;
        }

        /// <summary>Rescale every clip, the loop region and the playhead to a
        /// new pixels-per-second zoom and redraw the timeline.</summary>
        private void ApplyTimelineZoom(double newPps)
        {
            double oldPps = FixedBlock.PixelsPerSecond;
            if (Math.Abs(newPps - oldPps) < 0.001) return;
            double ratio = newPps / oldPps;
            FixedBlock.PixelsPerSecond = newPps;

            foreach (var b in TimelineBlocks)
            {
                b.StartX *= ratio;
                // Recompute width from the clip's true length so repeated
                // zooms don't accumulate rounding drift (image clips keep
                // their LengthSeconds from placement, so this holds for them).
                b.Width = Math.Max(4, b.LengthSeconds * newPps);
                b.UpdateCanvasPosition();
            }
            if (LoopStartX.HasValue) LoopStartX = LoopStartX.Value * ratio;
            if (LoopEndX.HasValue) LoopEndX = LoopEndX.Value * ratio;
            _currentPlayX *= ratio;

            RenderStudioGridLanes();
            UpdateLoopButtons();
            if (ZoomLabel != null) ZoomLabel.Text = $"🔍 {newPps:0}%";
        }

        private void TimelineScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // Keep the ruler ticks aligned with the timeline while it scrolls
            if (RulerShift != null)
            {
                RulerShift.X = -e.HorizontalOffset;
            }
        }

        private void RenderWaveformOverlay()
        {
            if (TimelineCanvas == null || WaveformOverlayCanvas == null) return;

            WaveformOverlayCanvas.Children.Clear();

            foreach (var block in TimelineBlocks.Where(b => !b.IsVideo && b.RawBytes != null && b.RawBytes.Length > 0))
            {
                try
                {
                    var waveform = WaveformRenderer.GenerateWaveformBitmap(
                        block.RawBytes,
                        block.SoundType,
                        (int)Math.Max(40, block.Width - 4),
                        35
                    );

                    Image waveImg = new Image
                    {
                        Source = waveform,
                        Stretch = System.Windows.Media.Stretch.Fill,
                        Opacity = 0.5,
                        Width = block.Width - 4,
                        Height = 35
                    };

                    Canvas.SetLeft(waveImg, block.StartX + 2);
                    Canvas.SetTop(waveImg, block.LaneIndex * 50 + 7);
                    WaveformOverlayCanvas.Children.Add(waveImg);
                }
                catch { }
            }
        }

        /// <summary>Right edge of the furthest clip (or the video), in timeline pixels.</summary>
        private double GetContentEndX()
        {
            double end = 0;
            foreach (var b in TimelineBlocks)
            {
                double w = double.IsNaN(b.Width) ? b.LengthSeconds * FixedBlock.PixelsPerSecond : b.Width;
                end = Math.Max(end, b.StartX + w);
            }
            return end;
        }

        /// <summary>The timeline is at least 25 seconds wide and grows to fit the content.</summary>
        private double TimelineLengthPx => Math.Max(25 * FixedBlock.PixelsPerSecond, GetContentEndX() + 5 * FixedBlock.PixelsPerSecond);

        private static string FmtTime(double seconds)
        {
            if (seconds < 0 || double.IsNaN(seconds)) seconds = 0;
            var ts = TimeSpan.FromSeconds(seconds);
            return $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
        }

        private void UpdateTimeDisplay(double currentSeconds)
        {
            if (TimeDisplay != null)
                TimeDisplay.Text = $"{FmtTime(currentSeconds)} / {FmtTime(GetContentEndX() / FixedBlock.PixelsPerSecond)}";
        }

        /// <summary>
        /// The video preview box only earns its space while a video clip is
        /// actually on the timeline. With no video clip it collapses away and
        /// the timeline takes the whole workspace height; adding a video clip
        /// brings it straight back.
        /// </summary>
        private void UpdateVideoPreviewVisibility()
        {
            if (VideoPreviewBorder == null || VideoPreviewRow == null) return;
            bool hasVideo = TimelineBlocks.Any(b => b.IsVideo);
            VideoPreviewBorder.Visibility = hasVideo ? Visibility.Visible : Visibility.Collapsed;
            VideoPreviewRow.Height = hasVideo ? new GridLength(3, GridUnitType.Star) : new GridLength(0);
            VideoPreviewRow.MinHeight = hasVideo ? 140 : 0;
            if (!hasVideo && MasterVideoPlayer != null)
            {
                try { MasterVideoPlayer.Pause(); } catch { }
            }
        }

        /// <summary>Position/show the A–B loop band + top brace from LoopStartX/LoopEndX.</summary>
        private void RefreshLoopVisual()
        {
            if (_loopBand == null || _loopBrace == null) return;
            if (LoopStartX.HasValue && LoopEndX.HasValue && LoopEndX.Value > LoopStartX.Value)
            {
                double w = LoopEndX.Value - LoopStartX.Value;
                _loopBand.Visibility = Visibility.Visible;
                _loopBand.Width = w;
                _loopBand.Height = _lastTimelineHeight;
                Canvas.SetLeft(_loopBand, LoopStartX.Value);
                Canvas.SetTop(_loopBand, 0);
                _loopBrace.Visibility = Visibility.Visible;
                _loopBrace.Width = w;
                Canvas.SetLeft(_loopBrace, LoopStartX.Value);
                Canvas.SetTop(_loopBrace, 0);
            }
            else
            {
                _loopBand.Visibility = Visibility.Collapsed;
                _loopBrace.Visibility = Visibility.Collapsed;
            }
        }

        private void RenderStudioGridLanes()
        {
            if (TimelineCanvas == null || HeadersSidebar == null) return;

            TimelineCanvas.Children.Clear();
            HeadersSidebar.Children.Clear();

            // Keep the timeline tall enough for every lane (audio tracks + video lane + video-audio lane)
            bool hasVideoAudio = TimelineBlocks.Any(b => b.IsVideoAudio);
            double timelineHeight = Math.Max(450, (AudioTrackCount + 1 + (hasVideoAudio ? 1 : 0)) * 50 + 20);
            TimelineCanvas.Height = timelineHeight;
            if (WaveformOverlayCanvas != null) WaveformOverlayCanvas.Height = timelineHeight;
            if (TimelineHost != null) TimelineHost.Height = timelineHeight;

            double tlWidth = TimelineLengthPx;
            TimelineCanvas.Width = tlWidth;
            if (WaveformOverlayCanvas != null) WaveformOverlayCanvas.Width = tlWidth;
            if (TimelineHost != null) TimelineHost.Width = tlWidth;
            if (tlWidth / FixedBlock.PixelsPerSecond > _rulerDrawnSeconds || Math.Abs(FixedBlock.PixelsPerSecond - _rulerDrawnPps) > 0.01)
                RenderTimeRulerHeaders();
            UpdateTimeDisplay(0);
            UpdateVideoPreviewVisibility();

            _lastTimelineHeight = timelineHeight;

            var playheadGlow = new DropShadowEffect
            {
                Color = Color.FromRgb(0x3D, 0xFF, 0x8E),
                BlurRadius = 7,
                ShadowDepth = 0,
                Opacity = 0.9
            };
            playhead = new Line
            {
                X1 = 0, Y1 = 0, X2 = 0, Y2 = timelineHeight,
                Stroke = new SolidColorBrush(Color.FromRgb(0x3D, 0xFF, 0x8E)),
                StrokeThickness = 2,
                Effect = playheadGlow,
                Visibility = Visibility.Collapsed
            };
            Panel.SetZIndex(playhead, 999);
            TimelineCanvas.Children.Add(playhead);

            playheadHandle = new Rectangle
            {
                Width = 13, Height = 11,
                Fill = new SolidColorBrush(Color.FromRgb(0x3D, 0xFF, 0x8E)),
                Effect = playheadGlow,
                Visibility = Visibility.Collapsed
            };
            Canvas.SetLeft(playheadHandle, -6);
            Canvas.SetTop(playheadHandle, 0);
            Panel.SetZIndex(playheadHandle, 1000);
            TimelineCanvas.Children.Add(playheadHandle);

            // A–B loop region: a soft gold band across the lanes with a solid
            // gold brace along the top (the feature page's look)
            _loopBand = new Rectangle
            {
                Fill = new SolidColorBrush(Color.FromArgb(26, 0xFF, 0xC8, 0x3D)),
                Visibility = Visibility.Collapsed
            };
            Panel.SetZIndex(_loopBand, 2);
            TimelineCanvas.Children.Add(_loopBand);
            _loopBrace = new Rectangle
            {
                Height = 5,
                Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x3D)),
                RadiusX = 2,
                RadiusY = 2,
                Visibility = Visibility.Collapsed
            };
            Panel.SetZIndex(_loopBrace, 998);
            TimelineCanvas.Children.Add(_loopBrace);

            // Beat grid lines (under the clips; every 4th line is a brighter downbeat)
            if (ProjectBpm > 0)
            {
                double periodPx = 60.0 / ProjectBpm * FixedBlock.PixelsPerSecond;
                double offsetPx = BeatOffsetSeconds * FixedBlock.PixelsPerSecond;
                var beatBrush = new SolidColorBrush(Color.FromArgb(50, 0xFF, 0xC8, 0x3D));
                var downbeatBrush = new SolidColorBrush(Color.FromArgb(95, 0xFF, 0xC8, 0x3D));
                int beatIndex = 0;
                for (double x = offsetPx; x < tlWidth && beatIndex <= 4000; x += periodPx, beatIndex++)
                {
                    var line = new Line
                    {
                        X1 = x, Y1 = 0, X2 = x, Y2 = timelineHeight,
                        Stroke = beatIndex % 4 == 0 ? downbeatBrush : beatBrush,
                        StrokeThickness = 1
                    };
                    Panel.SetZIndex(line, 1);
                    TimelineCanvas.Children.Add(line);
                }
            }

            foreach (var block in TimelineBlocks)
            {
                if (block.TrackId < AudioTrackCount || block.IsVideo || block.IsVideoAudio)
                {
                    if (!TimelineCanvas.Children.Contains(block.BlockVisual))
                    {
                        TimelineCanvas.Children.Add(block.BlockVisual);
                    }
                    block.UpdateCanvasPosition();
                }
            }

            var converter = new BrushConverter();

            // Audio tracks
            for (int track = 0; track < AudioTrackCount; track++)
            {
                double yPos = track * 50;

                Line gridLine = new Line
                {
                    X1 = 0, Y1 = yPos, X2 = tlWidth, Y2 = yPos,
                    Stroke = (Brush)converter.ConvertFromString("#2D323F")!,
                    StrokeThickness = 1
                };
                TimelineCanvas.Children.Add(gridLine);

                double totalSeconds = 0.0;
                foreach (var b in TimelineBlocks.Where(b => b.TrackId == track && !b.IsVideo))
                {
                    totalSeconds += b.LengthSeconds;
                }

                string labelText = $"🎵 Audio {track + 1}";
                if (totalSeconds > 0)
                {
                    var ts = TimeSpan.FromSeconds(totalSeconds);
                    labelText += $" • {ts.Minutes}:{ts.Seconds:D2}";
                }

                TextBlock lbl = new TextBlock
                {
                    Text = labelText,
                    FontFamily = new FontFamily("Consolas"),
                    FontWeight = FontWeights.Bold,
                    FontSize = 10,
                    Foreground = (Brush)converter.ConvertFromString("#A0A5B5")!,
                    Margin = new Thickness(8, yPos + 15, 0, 0),
                    Background = (Brush)converter.ConvertFromString("#14171E")!,
                    Padding = new Thickness(4, 2, 4, 2)
                };
                HeadersSidebar.Children.Add(lbl);
            }

            // Video track - SEPARATE with thicker line
            double vY = AudioTrackCount * 50;

            Line videoLine = new Line
            {
                X1 = 0, Y1 = vY, X2 = tlWidth, Y2 = vY,
                Stroke = (Brush)converter.ConvertFromString("#5B4A8F")!,
                StrokeThickness = 3
            };
            TimelineCanvas.Children.Add(videoLine);

            // Divider line above video
            Line dividerLine = new Line
            {
                X1 = 0, Y1 = vY - 1, X2 = tlWidth, Y2 = vY - 1,
                Stroke = (Brush)converter.ConvertFromString("#8E7CC3")!,
                StrokeThickness = 1
            };
            TimelineCanvas.Children.Add(dividerLine);

            string vLabelText = "🎬 VIDEO TRACK";
            if (VideoPath != null)
            {
                var vname = System.IO.Path.GetFileName(VideoPath);
                vLabelText = $"🎬 VIDEO • {vname}";
            }

            TextBlock vLbl = new TextBlock
            {
                Text = vLabelText,
                FontFamily = new FontFamily("Consolas"),
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                Foreground = (Brush)converter.ConvertFromString("#B79CED")!,
                Margin = new Thickness(8, vY + 15, 0, 0),
                Background = (Brush)converter.ConvertFromString("#14171E")!,
                Padding = new Thickness(4, 2, 4, 2)
            };
            HeadersSidebar.Children.Add(vLbl);

            // Video-audio lane: the separated soundtrack sits directly under the video
            if (hasVideoAudio)
            {
                double vaY = (AudioTrackCount + 1) * 50;

                Line vaLine = new Line
                {
                    X1 = 0, Y1 = vaY, X2 = tlWidth, Y2 = vaY,
                    Stroke = (Brush)converter.ConvertFromString("#2A6F7F")!,
                    StrokeThickness = 2
                };
                TimelineCanvas.Children.Add(vaLine);

                TextBlock vaLbl = new TextBlock
                {
                    Text = "🔊 VIDEO AUDIO",
                    FontFamily = new FontFamily("Consolas"),
                    FontWeight = FontWeights.Bold,
                    FontSize = 11,
                    Foreground = (Brush)converter.ConvertFromString("#4FB3C9")!,
                    Margin = new Thickness(8, vaY + 15, 0, 0),
                    Background = (Brush)converter.ConvertFromString("#14171E")!,
                    Padding = new Thickness(4, 2, 4, 2)
                };
                HeadersSidebar.Children.Add(vaLbl);
            }

            RenderWaveformOverlay();
            RefreshLoopVisual();
        }

        // =========================
        // IMPORT HANDLERS
        // =========================

        /// <summary>
        /// One-click video add: pick a file and it lands straight on the video
        /// track (and in the library, like a normal import).
        /// </summary>
        private void BtnAddVideoToTimeline_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog
            {
                Filter = "Video files|*.mp4;*.avi;*.wmv;*.mkv;*.mov;*.flv;*.webm;*.mpg;*.mpeg;*.ogv;*.3gp;*.m4v|All files|*.*",
                Multiselect = false,
                Title = "Add Video to Timeline"
            };
            if (dlg.ShowDialog() != true) return;

            string path = dlg.FileName;
            string name = System.IO.Path.GetFileName(path);
            if (!VideoAssets.ContainsKey(name))
            {
                VideoAssets[name] = path;
                AddAssetButtonToGroup(CreateAssetButton($"🎬 {name}", name));
            }

            // Drop it straight onto the video lane at the start of the timeline
            PlaceAssetAt(name, new Point(0, AudioTrackCount * 50));
        }

        private void BtnImportVideo_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog
            {
                Filter = "Video files|*.mp4;*.avi;*.wmv;*.mkv;*.mov;*.flv;*.webm;*.mpg;*.mpeg;*.ogv;*.3gp;*.m4v|All files|*.*",
                Multiselect = true,
                Title = "Import Video Files"
            };
            if (dlg.ShowDialog() != true) return;

            foreach (var path in dlg.FileNames)
            {
                string name = System.IO.Path.GetFileName(path);
                VideoAssets[name] = path;

                AddAssetButtonToGroup(CreateAssetButton($"🎬 {name}", name));
            }
        }

        /// <summary>Register a generated video file in the library (AI Lab video tab).</summary>
        public void AddGeneratedVideoToLibrary(string path)
        {
            string name = System.IO.Path.GetFileName(path);
            VideoAssets[name] = path;
            AddAssetButtonToGroup(CreateAssetButton($"🎬 {name}", name));
            ApplyLibraryFilter();
        }

        private void BtnImportImages_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog
            {
                Filter = "Image files|*.jpg;*.jpeg;*.png;*.gif;*.bmp;*.tiff|All files|*.*",
                Multiselect = true,
                Title = "Import Image Files"
            };
            if (dlg.ShowDialog() != true) return;

            foreach (var path in dlg.FileNames)
            {
                string name = System.IO.Path.GetFileName(path);
                ImageAssets[name] = path;

                AddAssetButtonToGroup(CreateAssetButton($"🖼️ {name}", name));
            }
        }

        private void BtnImportAudioOrZip_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog
            {
                Filter = "Audio/MIDI files and ZIP|*.wav;*.mp3;*.ogg;*.mid;*.midi;*.zip|All files|*.*",
                Multiselect = true,
                Title = "Import Audio, MIDI, or ZIP Files"
            };
            if (dlg.ShowDialog() != true) return;

            foreach (var path in dlg.FileNames)
            {
                string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();

                if (ext == ".zip")
                {
                    try
                    {
                        var addedNames = new List<(string Name, string FullPath)>();
                        using (var archive = ZipFile.OpenRead(path))
                        {
                            foreach (var entry in archive.Entries)
                            {
                                var eext = System.IO.Path.GetExtension(entry.FullName).ToLowerInvariant();
                                if (AudioExtensions.Contains(eext))
                                {
                                    string baseName = entry.Name;
                                    if (!string.IsNullOrEmpty(baseName))
                                    {
                                        using (var ms = new MemoryStream())
                                        {
                                            entry.Open().CopyTo(ms);
                                            ZipSounds[baseName] = ms.ToArray();
                                        }
                                        addedNames.Add((baseName, entry.FullName));
                                    }
                                }
                            }
                        }

                        if (addedNames.Count > 0)
                        {
                            // v3.46: no more loose ZIP headers — every sound is filed
                            // under its category group (folder hint first, else inferred).
                            foreach (var (soundName, fullPath) in addedNames.OrderBy(n => n.Name))
                            {
                                if (!SoundCategories.ContainsKey(soundName))
                                    SoundCategories[soundName] = CategoryFromZipPath(fullPath);
                                string cat = SoundCategories[soundName];
                                var sext = System.IO.Path.GetExtension(soundName).ToLowerInvariant();
                                string icon = (sext == ".mid" || sext == ".midi") ? "🎹" : SoundCategorizer.Icon(cat);
                                AddAssetButtonToGroup(CreateAssetButton($"{icon} {soundName}", soundName));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to read ZIP: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                else if (AudioExtensions.Contains(ext))
                {
                    try
                    {
                        var bytes = File.ReadAllBytes(path);
                        string name = System.IO.Path.GetFileName(path);
                        ZipSounds[name] = bytes;

                        string icon = (ext == ".mid" || ext == ".midi") ? "🎹" : "🎵";
                        AddAssetButtonToGroup(CreateAssetButton($"{icon} {name}", name));
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to import {path}: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }

        /// <summary>
        /// Builds one asset-library entry. Press-and-move starts a real drag —
        /// drop the asset anywhere on the timeline — while a plain press still
        /// arms the old click-then-click-to-place fallback.
        /// </summary>
        private Button CreateAssetButton(string displayText, string assetName, double indent = 0)
        {
            Button btn = new Button
            {
                Content = displayText,
                Tag = assetName,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = Brushes.Transparent,
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Margin = new Thickness(indent, 3, 0, 3),
                Cursor = Cursors.Hand,
                Padding = new Thickness(4)
            };

            btn.PreviewMouseLeftButtonDown += (s, args) =>
            {
                draggingSoundName = assetName;
                dragStartPoint = args.GetPosition(null);
            };

            btn.MouseMove += (s, args) =>
            {
                if (args.LeftButton != MouseButtonState.Pressed) return;
                if (draggingSoundName != assetName) return;

                Point pos = args.GetPosition(null);
                if (Math.Abs(pos.X - dragStartPoint.X) <= SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(pos.Y - dragStartPoint.Y) <= SystemParameters.MinimumVerticalDragDistance)
                    return;

                var result = DragDrop.DoDragDrop(btn, new DataObject(DataFormats.StringFormat, assetName), DragDropEffects.Copy);
                if (result == DragDropEffects.None)
                {
                    // Drag was cancelled — disarm so a later stray canvas click doesn't place it.
                    draggingSoundName = null;
                }
            };

            // v3.46: right-click any library entry to delete it (with a confirm).
            var menu = new ContextMenu
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x21, 0x2E)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x34, 0x46)),
                BorderThickness = new Thickness(1)
            };
            var miDelete = new MenuItem { Header = "🗑️ Delete sound…", Foreground = Brushes.White };
            miDelete.Click += (s, e) => DeleteLibraryAsset(assetName);
            menu.Items.Add(miDelete);
            btn.ContextMenu = menu;

            return btn;
        }

        private void TimelineCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (string.IsNullOrEmpty(draggingSoundName)) return;

            PlaceAssetAt(draggingSoundName, e.GetPosition(TimelineCanvas));
            draggingSoundName = null;
        }

        private void TimelineCanvas_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.StringFormat) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void TimelineCanvas_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.StringFormat))
            {
                string assetName = (string)e.Data.GetData(DataFormats.StringFormat);
                PlaceAssetAt(assetName, e.GetPosition(TimelineCanvas));
                draggingSoundName = null;
            }
        }

        private void PlaceAssetAt(string assetName, Point clickPoint)
        {
            // Audit Bug 6 (v3.65): this method used to alias the parameter
            // into a same-named local and null it at the end (CS8600) — the
            // alias is gone; the parameter is used directly.
            int trackId = (int)(clickPoint.Y / 50);

            // VIDEO: Always goes to video track
            if (VideoAssets.ContainsKey(assetName))
            {
                VideoPath = VideoAssets[assetName];
                SetVideoAudioSeparated(false);
                _videoNaturalSeconds = 0;
                SetVideoSpeed(1.0);
                if (MasterVideoPlayer != null)
                {
                    try
                    {
                        MasterVideoPlayer.Source = new Uri(VideoPath);
                        if (NoVideoLabel != null) NoVideoLabel.Visibility = Visibility.Collapsed;
                    }
                    catch { }
                }

                // Remove existing video blocks
                var existingVideo = TimelineBlocks.Where(b => b.IsVideo).ToList();
                foreach (var ev in existingVideo)
                {
                    if (TimelineCanvas.Children.Contains(ev.BlockVisual))
                        TimelineCanvas.Children.Remove(ev.BlockVisual);
                    TimelineBlocks.Remove(ev);
                }

                // Add to VIDEO track (AudioTrackCount)
                FixedBlock vblock = new FixedBlock(this, TimelineCanvas, AudioTrackCount, assetName, clickPoint.X, Array.Empty<byte>(), "mp4", 280 * FixedBlock.PixelsPerSecond / 100.0, true);
                TimelineBlocks.Add(vblock);
                RenderStudioGridLanes();
            }
            // IMAGE: Goes to clicked audio track
            else if (ImageAssets.ContainsKey(assetName))
            {
                if (trackId >= 0 && trackId < AudioTrackCount)
                {
                    FixedBlock iblock = new FixedBlock(this, TimelineCanvas, trackId, assetName, clickPoint.X, Array.Empty<byte>(), "image", 120 * FixedBlock.PixelsPerSecond / 100.0, false);
                    TimelineBlocks.Add(iblock);
                }
            }
            // AUDIO: Goes to clicked audio track
            else if (ZipSounds.ContainsKey(assetName))
            {
                if (trackId >= 0 && trackId < AudioTrackCount)
                {
                    byte[] data = ZipSounds[assetName];
                    string fileExt = System.IO.Path.GetExtension(assetName).Replace(".", "").ToLower();
                    FixedBlock ablock = new FixedBlock(this, TimelineCanvas, trackId, assetName, clickPoint.X, data, fileExt);
                    TimelineBlocks.Add(ablock);
                }
            }
        }

        private void BtnAddTrack_Click(object sender, RoutedEventArgs e)
        {
            if (AudioTrackCount < 8)
            {
                AudioTrackCount++;
                RenderStudioGridLanes();
                BuildTrackChains();
            }
        }

        private void BtnRemoveTrack_Click(object sender, RoutedEventArgs e)
        {
            if (AudioTrackCount > 1)
            {
                int targetTrack = AudioTrackCount - 1;
                TimelineBlocks.RemoveAll(b => b.TrackId == targetTrack && !b.IsVideo && !b.IsVideoAudio);
                AudioTrackCount--;
                RenderStudioGridLanes();
                BuildTrackChains();
            }
        }

        private void BtnRemoveVideo_Click(object sender, RoutedEventArgs e)
        {
            // Check if there's any video to remove
            var videoCount = TimelineBlocks.Count(b => b.IsVideo);
            if (videoCount == 0)
            {
                MessageBox.Show("❌ No video track to remove.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show($"Remove {videoCount} video clip(s) from timeline?", "Remove Video Track?", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            // Remove ALL video blocks
            var videoBlocks = TimelineBlocks.Where(b => b.IsVideo).ToList();
            foreach (var vb in videoBlocks)
            {
                if (TimelineCanvas != null && TimelineCanvas.Children.Contains(vb.BlockVisual))
                    TimelineCanvas.Children.Remove(vb.BlockVisual);
                TimelineBlocks.Remove(vb);
            }

            // Clear video state
            VideoAssets.Clear();
            VideoPath = null;

            if (MasterVideoPlayer != null)
            {
                MasterVideoPlayer.Source = null;
            }
            if (NoVideoLabel != null)
            {
                NoVideoLabel.Visibility = Visibility.Visible;
            }

            // Re-render to show separation
            RenderStudioGridLanes();

            MessageBox.Show("✅ Video track removed successfully!", "Done", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnPlay_Click(object sender, RoutedEventArgs e)
        {
            // Resume from pause: unfreeze the play clock, the audio output and the video
            if (_isPaused)
            {
                _isPaused = false;
                _playStopwatch?.Start();
                outputDevice?.Play();
                if (VideoPath != null && MasterVideoPlayer != null)
                {
                    try { MasterVideoPlayer.Play(); } catch { }
                }
                return;
            }

            if (isPlayingTimeline) return;
            isPlayingTimeline = true;
            playbackCancelToken = new CancellationTokenSource();
            _playStopwatch = System.Diagnostics.Stopwatch.StartNew();

            if (VideoPath != null && MasterVideoPlayer != null)
            {
                try
                {
                    MasterVideoPlayer.Play();
                }
                catch { }
            }

            Task.Run(() => TimelinePlaybackEngineLoop(playbackCancelToken.Token));
        }

        private void BtnPause_Click(object sender, RoutedEventArgs e)
        {
            if (MasterVideoPlayer != null) MasterVideoPlayer.Pause();

            // Pause EVERYTHING: freeze the play clock and the audio output too,
            // so clips stop mid-sound instead of playing on behind a paused video
            if (isPlayingTimeline && !_isPaused)
            {
                _isPaused = true;
                _playStopwatch?.Stop();
                outputDevice?.Pause();
            }
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            isPlayingTimeline = false;
            _isPaused = false;
            StopAllVoices();
            playbackCancelToken?.Cancel();
            _playStopwatch?.Stop();
            if (outputDevice?.PlaybackState == PlaybackState.Paused) outputDevice.Play();
            if (MasterVideoPlayer != null) MasterVideoPlayer.Stop();
        }

        // =========================
        // MIXER (per-track volume + pan)
        // =========================

        private void BuildTrackChains()
        {
            if (mixerEngine == null) return;

            foreach (var output in _trackChainOutputs)
            {
                mixerEngine.RemoveMixerInput(output);
            }
            _trackMixers.Clear();
            _trackGains.Clear();
            _trackPanFx.Clear();
            _trackChainOutputs.Clear();

            int chainCount = AudioTrackCount + 1; // last chain = VIDEO AUDIO lane
            while (TrackVolumeValues.Count < chainCount) TrackVolumeValues.Add(1.0);
            while (TrackPanValues.Count < chainCount) TrackPanValues.Add(0.0);

            for (int i = 0; i < chainCount; i++)
            {
                var trackMixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2)) { ReadFully = true };
                var fx = GetTrackFx(i);
                var eq = new EqSampleProvider(trackMixer, fx);
                var delay = new DelaySampleProvider(eq, fx);
                var reverb = new ReverbSampleProvider(delay, fx);
                var pan = new StereoPanSampleProvider(reverb) { Pan = (float)TrackPanValues[i] };
                var gain = new VolumeSampleProvider(pan) { Volume = (float)TrackVolumeValues[i] };
                _trackMixers.Add(trackMixer);
                _trackPanFx.Add(pan);
                _trackGains.Add(gain);
                _trackChainOutputs.Add(gain);
                mixerEngine.AddMixerInput(gain);
            }

            _mixerWindow?.RefreshStrips();
        }

        public TrackFxSettings GetTrackFx(int index)
        {
            while (TrackFxValues.Count <= index) TrackFxValues.Add(new TrackFxSettings());
            return TrackFxValues[index];
        }

        public double GetTrackVolume(int index) => index >= 0 && index < TrackVolumeValues.Count ? TrackVolumeValues[index] : 1.0;

        public double GetTrackPan(int index) => index >= 0 && index < TrackPanValues.Count ? TrackPanValues[index] : 0.0;

        public void SetTrackVolume(int index, double value)
        {
            while (TrackVolumeValues.Count <= index) TrackVolumeValues.Add(1.0);
            TrackVolumeValues[index] = value;
            if (index >= 0 && index < _trackGains.Count) _trackGains[index].Volume = (float)value;
        }

        public void SetTrackPan(int index, double value)
        {
            while (TrackPanValues.Count <= index) TrackPanValues.Add(0.0);
            TrackPanValues[index] = value;
            if (index >= 0 && index < _trackPanFx.Count) _trackPanFx[index].Pan = (float)value;
        }

        public void SetMasterVolume(double value)
        {
            MasterVolumeSlider.Value = value;
        }

        private void BtnMixer_Click(object sender, RoutedEventArgs e)
        {
            if (_mixerWindow == null)
            {
                _mixerWindow = new MixerWindow(this) { Owner = this };
                _mixerWindow.Closed += (s, ev) => _mixerWindow = null;
                _mixerWindow.Show();
            }
            else
            {
                _mixerWindow.Activate();
            }
        }

        private void BtnSongLibrary_Click(object sender, RoutedEventArgs e)
        {
            if (_songLibraryWindow == null)
            {
                _songLibraryWindow = new SongLibraryWindow(this) { Owner = this };
                _songLibraryWindow.Closed += (s, ev) => _songLibraryWindow = null;
                _songLibraryWindow.Show();
            }
            else
            {
                _songLibraryWindow.Activate();
            }
        }

        private void BtnSetBuilder_Click(object sender, RoutedEventArgs e)
        {
            if (_setBuilderWindow == null)
            {
                _setBuilderWindow = new SetBuilderWindow(this) { Owner = this };
                _setBuilderWindow.Closed += (s, ev) => _setBuilderWindow = null;
                _setBuilderWindow.Show();
            }
            else
            {
                _setBuilderWindow.Activate();
            }
        }

        /// <summary>Send a song from the 🎵 Song Library to a DJ deck —
        /// loaded right onto it, or appended to its track list. Opens
        /// the DJ board first when it isn't open yet.</summary>
        public bool SendSongToDeck(bool deckA, string path, bool queueOnly)
        {
            try
            {
                if (!System.IO.File.Exists(path)) return false;
                BtnDj_Click(this, new RoutedEventArgs());
                if (_djWindow == null) return false;
                return queueOnly ? _djWindow.QueueOnDeck(deckA, path) : _djWindow.LoadOnDeck(deckA, path);
            }
            catch { return false; }
        }

        /// <summary>Send in-memory audio (a streamed Suno song) to a DJ
        /// deck. Opens the DJ board first when it isn't open yet.</summary>
        public bool SendBytesToDeck(bool deckA, byte[] bytes, string name)
        {
            try
            {
                BtnDj_Click(this, new RoutedEventArgs());
                if (_djWindow == null) return false;
                return _djWindow.LoadBytesOnDeck(deckA, bytes, name);
            }
            catch { return false; }
        }

        private void BtnDj_Click(object sender, RoutedEventArgs e)
        {
            if (_djWindow == null)
            {
                _djWindow = new DjWindow(this) { Owner = this };
                _djWindow.Closed += (s, ev) => _djWindow = null;
                _djWindow.Show();
            }
            else
            {
                _djWindow.Activate();
            }
        }

        // =========================
        // COMMANDS + HOTKEYS
        // =========================

        private CommandPaletteWindow? _paletteWindow;

        private void PlayPauseToggle()
        {
            if (isPlayingTimeline && !_isPaused) BtnPause_Click(this, new RoutedEventArgs());
            else BtnPlay_Click(this, new RoutedEventArgs());
        }

        private async void DetectAndApplyGrid()
        {
            var result = await DetectBeatsForGridAsync();
            if (result == null)
            {
                MessageBox.Show("No audio clip to detect from, or no steady beat found in it.", "Detect Beats");
                return;
            }
            ProjectBpm = result.Value.Bpm;
            BeatOffsetSeconds = result.Value.OffsetTimeline;
            RenderStudioGridLanes();
            MessageBox.Show($"Beat grid set: {result.Value.Bpm:0.0} BPM (confidence {result.Value.Confidence * 100:0}%).", "Detect Beats");
        }

        private void ToggleAutoCrossfade()
        {
            AutoCrossfadeEnabled = !AutoCrossfadeEnabled;
            if (AutoCrossfadeMenuItem != null) AutoCrossfadeMenuItem.IsChecked = AutoCrossfadeEnabled;
        }

        private void ToggleSnap()
        {
            SnapToBeatEnabled = !SnapToBeatEnabled;
            if (SnapMenuItem != null) SnapMenuItem.IsChecked = SnapToBeatEnabled;
        }

        public List<AppCommand> BuildCommands()
        {
            var ev = new RoutedEventArgs();
            return new List<AppCommand>
            {
                new("Play / Pause", "Transport", "Space", PlayPauseToggle),
                new("Stop", "Transport", "Esc", () => BtnStop_Click(this, ev)),
                new("Set Loop A", "Loop", "A", () => BtnSetA_Click(this, ev)),
                new("Set Loop B", "Loop", "B", () => BtnSetB_Click(this, ev)),
                new("Toggle A–B Loop", "Loop", "L", () => BtnLoopToggle_Click(this, ev)),
                new("Save Project", "File", "Ctrl+S", () => BtnSave_Click(this, ev)),
                new("Open Project", "File", "Ctrl+O", () => BtnOpenProject_Click(this, ev)),
                new("Export Audio…", "File", "Ctrl+E", () => BtnExportAudio_Click(this, ev)),
                new("Export Video…", "File", "", () => BtnExport_Click(this, ev)),
                new("Import Audio / ZIP", "File", "", () => BtnImportAudioOrZip_Click(this, ev)),
                new("Add Audio Track", "Tracks", "Ctrl+T", () => BtnAddTrack_Click(this, ev)),
                new("Remove Audio Track", "Tracks", "Ctrl+Shift+T", () => BtnRemoveTrack_Click(this, ev)),
                new("Beat Grid…", "Tracks", "", () => MenuBeatGrid_Click(this, ev)),
                new("Detect Beats → Set Grid", "Tracks", "", DetectAndApplyGrid),
                new("Toggle Auto-Crossfade", "Tracks", "", ToggleAutoCrossfade),
                new("Toggle Snap to Beat", "Tracks", "", ToggleSnap),
                new("Mixer", "View", "Ctrl+M", () => BtnMixer_Click(this, ev)),
                new("DJ Decks", "View", "Ctrl+Shift+D", () => BtnDj_Click(this, ev)),
                new("Song Library…", "Tools", "", () => BtnSongLibrary_Click(this, ev)),
                new("Set Builder…", "Tools", "", () => BtnSetBuilder_Click(this, ev)),
                new("Output Window", "View", "Ctrl+Shift+O", () => BtnOutput_Click(this, ev)),
                new("Monitor FX", "View", "Ctrl+Shift+M", () => BtnMonitorFx_Click(this, ev)),
                new("Transcript…", "View", "", () => BtnTranscript_Click(this, ev)),
                new("Stem Splitter…", "View", "", () => BtnStemSplit_Click(this, ev)),
                new("Patterns (Step Sequencer)…", "View", "", () => BtnPatterns_Click(this, ev)),
                new("Piano Roll…", "View", "", () => BtnPianoRoll_Click(this, ev)),
                new("Drum Generator…", "Create", "", () => BtnDrumGenerator_Click(this, ev)),
                new("AI Lab…", "Create", "", () => BtnAiLab_Click(this, ev)),
                new("My Songs…", "View", "", () => BtnMySongs_Click(this, ev)),
                new("Freesound Sounds…", "Create", "", () => BtnFreesound_Click(this, ev)),
                new("Internet Archive Music…", "Create", "", () => BtnArchive_Click(this, ev)),
                new("Sound Pack…", "Create", "", () => BtnSoundPack_Click(this, ev)),
                new("Downloads…", "View", "", () => BtnDownloads_Click(this, ev)),
                new("Create Song…", "Create", "", () => BtnCreateSong_Click(this, ev)),
                new("Render Queue…", "File", "", () => BtnRenderQueue_Click(this, ev)),
                new("Batch Convert…", "File", "", () => BtnBatchConvert_Click(this, ev)),
                new("Start Screen…", "File", "", () => MenuStartScreen_Click(this, ev)),
                new("About BeatSync", "Help", "F1", () => MenuAbout_Click(this, ev)),
                new("Changelog…", "Help", "", () => BtnChangelog_Click(this, ev)),
                new("Exit", "File", "", () => MenuExit_Click(this, ev)),
            };
        }

        private void OpenCommandPalette()
        {
            if (_paletteWindow != null) { _paletteWindow.Activate(); return; }
            _paletteWindow = new CommandPaletteWindow(BuildCommands()) { Owner = this };
            _paletteWindow.Closed += (s, e) => _paletteWindow = null;
            _paletteWindow.Show();
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            var ev = new RoutedEventArgs();

            if (ctrl && e.Key == Key.K) { OpenCommandPalette(); e.Handled = true; return; }
            if (ctrl && shift && e.Key == Key.P) { OpenCommandPalette(); e.Handled = true; return; }
            if (ctrl && shift && e.Key == Key.D) { BtnDj_Click(this, ev); e.Handled = true; return; }
            if (ctrl && shift && e.Key == Key.O) { BtnOutput_Click(this, ev); e.Handled = true; return; }
            if (ctrl && shift && e.Key == Key.M) { BtnMonitorFx_Click(this, ev); e.Handled = true; return; }
            if (ctrl && shift && e.Key == Key.T) { BtnRemoveTrack_Click(this, ev); e.Handled = true; return; }
            if (ctrl && e.Key == Key.S) { BtnSave_Click(this, ev); e.Handled = true; return; }
            if (ctrl && e.Key == Key.O) { BtnOpenProject_Click(this, ev); e.Handled = true; return; }
            if (ctrl && e.Key == Key.E) { BtnExportAudio_Click(this, ev); e.Handled = true; return; }
            if (ctrl && e.Key == Key.M) { BtnMixer_Click(this, ev); e.Handled = true; return; }
            if (ctrl && e.Key == Key.T) { BtnAddTrack_Click(this, ev); e.Handled = true; return; }

            // Single keys never fire while typing in a text box
            if (Keyboard.FocusedElement is TextBox) return;

            switch (e.Key)
            {
                case Key.Space: PlayPauseToggle(); e.Handled = true; break;
                case Key.A: BtnSetA_Click(this, ev); e.Handled = true; break;
                case Key.B: BtnSetB_Click(this, ev); e.Handled = true; break;
                case Key.L: BtnLoopToggle_Click(this, ev); e.Handled = true; break;
                case Key.F1: MenuAbout_Click(this, ev); e.Handled = true; break;
                case Key.Escape:
                    if (isPlayingTimeline) { BtnStop_Click(this, ev); e.Handled = true; }
                    break;
            }
        }

        private void MenuPalette_Click(object sender, RoutedEventArgs e) => OpenCommandPalette();

        // =========================
        // RENDER QUEUE + BATCH CONVERT
        // =========================

        /// <summary>Snapshot of the current timeline as an export job (render queue).</summary>
        public ExportJob BuildExportJobSnapshot() => BuildExportJob();

        /// <summary>Apply an export options filename pattern to a dialog-chosen path.</summary>
        public string BuildExportPath(string chosenPath, ExportOptionsWindow options)
        {
            string baseName = System.IO.Path.GetFileNameWithoutExtension(chosenPath);
            string expanded = ExportOptionsWindow.ExpandPattern(options.FilenamePattern, baseName, ProjectBpm, options.Preset);
            if (string.IsNullOrWhiteSpace(expanded) || expanded == baseName) return chosenPath;
            return System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(chosenPath) ?? "",
                expanded + System.IO.Path.GetExtension(chosenPath));
        }

        /// <summary>Set the A–B loop from timeline seconds and show it (transcript click-to-loop).</summary>
        public void SetLoopRange(double startSec, double endSec)
        {
            LoopStartX = Math.Max(0, startSec * FixedBlock.PixelsPerSecond);
            LoopEndX = Math.Max(LoopStartX.Value + 0.1 * FixedBlock.PixelsPerSecond, endSec * FixedBlock.PixelsPerSecond);
            LoopEnabled = true;
            UpdateLoopButtons();
            TimelineScrollViewer.ScrollToHorizontalOffset(Math.Max(0, LoopStartX.Value - 200));
        }

        // ---- Patterns (step sequencer) ----
        public List<PatternData> Patterns { get; } = new List<PatternData>();

        // ---- Piano roll melodies ----
        public List<PianoPattern> PianoPatterns { get; } = new List<PianoPattern>();

        private PianoRollWindow? _pianoRollWindow;
        private void BtnPianoRoll_Click(object sender, RoutedEventArgs e)
        {
            if (_pianoRollWindow != null) { _pianoRollWindow.Activate(); return; }
            _pianoRollWindow = new PianoRollWindow(this) { Owner = this };
            _pianoRollWindow.Closed += (s, ev) => _pianoRollWindow = null;
            _pianoRollWindow.Show();
        }

        private PatternWindow? _patternWindow;
        private void BtnPatterns_Click(object sender, RoutedEventArgs e)
        {
            if (_patternWindow != null) { _patternWindow.Activate(); return; }
            _patternWindow = new PatternWindow(this) { Owner = this };
            _patternWindow.Closed += (s, ev) => _patternWindow = null;
            _patternWindow.Show();
        }

        /// <summary>
        /// Add a sound to the asset library under a unique name, filed under
        /// a category (explicit, or inferred from the name). Returns the final name.
        /// </summary>
        public string AddToLibraryOnly(byte[] wavBytes, string desiredName, string? category = null)
        {
            string name = desiredName;
            int n = 2;
            while (ZipSounds.ContainsKey(name))
            {
                string ext = System.IO.Path.GetExtension(desiredName);
                name = $"{System.IO.Path.GetFileNameWithoutExtension(desiredName)} ({n}){ext}";
                n++;
            }
            ZipSounds[name] = wavBytes;
            string cat = category ?? SoundCategorizer.Infer(name);
            SoundCategories[name] = cat;
            AddAssetButtonToGroup(CreateAssetButton($"{SoundCategorizer.Icon(cat)} {name}", name));
            ApplyLibraryFilter();
            return name;
        }

        /// <summary>Add a downloaded full song to the 🎵 Song Library
        /// (v3.82): the file lands in the app's own library folder (the
        /// one picked in the Song Library window) and the song appears
        /// in the library's track list. Returns the stored file name,
        /// or "" when it couldn't be added.</summary>
        public string AddSongToLibrary(byte[] bytes, string fileName)
        {
            var entry = SongLibraryStore.AddBytesToLibrary(bytes, fileName);
            return entry != null ? System.IO.Path.GetFileName(entry.Path) : "";
        }

        /// <summary>Add a rendered pattern WAV to the library and drop it on track 1 at the start. Returns the final name.</summary>
        public string AddRenderedPatternToTimeline(byte[] wavBytes, string desiredName)
        {
            string name = AddToLibraryOnly(wavBytes, desiredName);
            if (AudioTrackCount > 0 && TimelineCanvas != null)
            {
                var block = new FixedBlock(this, TimelineCanvas, 0, name, 0, wavBytes, "wav");
                TimelineBlocks.Add(block);
                RenderStudioGridLanes();
            }
            return name;
        }

        /// <summary>Open the step sequencer focused on a specific pattern (drum generator hand-off).</summary>
        public void OpenPatternsOn(PatternData pattern)
        {
            BtnPatterns_Click(this, new RoutedEventArgs());
            _patternWindow?.SelectPattern(pattern);
        }

        private void BtnDrumGenerator_Click(object sender, RoutedEventArgs e)
        {
            var win = new DrumGeneratorWindow(this) { Owner = this };
            win.ShowDialog();
        }

        private StemSplitWindow? _stemSplitWindow;
        private void BtnStemSplit_Click(object sender, RoutedEventArgs e)
        {
            if (_stemSplitWindow != null) { _stemSplitWindow.Activate(); return; }
            _stemSplitWindow = new StemSplitWindow(this) { Owner = this };
            _stemSplitWindow.Closed += (s, ev) => _stemSplitWindow = null;
            _stemSplitWindow.Show();
        }

        private TranscriptWindow? _transcriptWindow;
        private void BtnTranscript_Click(object sender, RoutedEventArgs e)
        {
            if (_transcriptWindow != null) { _transcriptWindow.Activate(); return; }
            _transcriptWindow = new TranscriptWindow(this) { Owner = this };
            _transcriptWindow.Closed += (s, ev) => _transcriptWindow = null;
            _transcriptWindow.Show();
        }

        private RenderQueueWindow? _renderQueueWindow;
        private void BtnRenderQueue_Click(object sender, RoutedEventArgs e)
        {
            if (_renderQueueWindow != null) { _renderQueueWindow.Activate(); return; }
            _renderQueueWindow = new RenderQueueWindow(this) { Owner = this };
            _renderQueueWindow.Closed += (s, ev) => _renderQueueWindow = null;
            _renderQueueWindow.Show();
        }

        private BatchConvertWindow? _batchConvertWindow;
        private void BtnBatchConvert_Click(object sender, RoutedEventArgs e)
        {
            if (_batchConvertWindow != null) { _batchConvertWindow.Activate(); return; }
            _batchConvertWindow = new BatchConvertWindow { Owner = this };
            _batchConvertWindow.Closed += (s, ev) => _batchConvertWindow = null;
            _batchConvertWindow.Show();
        }

        // =========================
        // START SCREEN + RECENT PROJECTS
        // =========================

        private bool _startScreenShownOnce;
        private string? _openPathOverride;

        private static string AppDataDir => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BeatSyncStudioPro");
        private static string RecentProjectsPath => System.IO.Path.Combine(AppDataDir, "recent-projects.json");
        private static string AppSettingsPath => System.IO.Path.Combine(AppDataDir, "app-settings.json");

        // ---------- v3.74: autosave + crash recovery ----------
        private static string AutosavePath => System.IO.Path.Combine(AppDataDir, "autosave.bsp");
        private static string AutosaveMetaPath => System.IO.Path.Combine(AppDataDir, "autosave-meta.json");
        private static string SessionFlagPath => System.IO.Path.Combine(AppDataDir, "session-running.flag");
        private System.Windows.Threading.DispatcherTimer? _autosaveTimer;
        private string _lastAutosaveFingerprint = "";

        /// <summary>Cheap change detector for the project: block count +
        /// geometry, library size, patterns, master settings. Autosave
        /// only writes when this changes.</summary>
        private string ProjectFingerprint()
        {
            unchecked
            {
                long h = 1469598103934665603L;
                void Mix(long v) { h = (h ^ v) * 1099511628211L; }
                Mix(TimelineBlocks.Count);
                foreach (var b in TimelineBlocks)
                {
                    Mix(b.Filename?.GetHashCode() ?? 0);
                    Mix((long)(b.StartX * 10));
                    Mix((long)(b.Width * 10));
                    Mix(b.TrackId);
                }
                Mix(ZipSounds.Count);
                Mix(Patterns?.Count ?? 0);
                Mix(PianoPatterns?.Count ?? 0);
                Mix((long)(MasterVolume * 1000));
                Mix((long)(ProjectBpm * 10));
                return h.ToString("X");
            }
        }

        private void TryAutosaveNow()
        {
            try
            {
                if (TimelineBlocks.Count == 0 && ZipSounds.Count == 0) return;
                string fp = ProjectFingerprint();
                if (fp == _lastAutosaveFingerprint) return;
                WriteProjectFile(AutosavePath);
                _lastAutosaveFingerprint = fp;
                File.WriteAllText(AutosaveMetaPath, JsonSerializer.Serialize(new { SavedUtc = DateTime.UtcNow, Fingerprint = fp }));
            }
            catch { /* autosave must never interrupt the work */ }
        }

        private void RecoverCrashOnce(object sender, RoutedEventArgs e)
        {
            Loaded -= RecoverCrashOnce;
            try
            {
                string when = "";
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(AutosaveMetaPath));
                    if (doc.RootElement.TryGetProperty("SavedUtc", out var su)
                        && su.GetString() is string s && DateTime.TryParse(s, out var utc))
                        when = " from " + utc.ToLocalTime().ToString("h:mm tt on MMM d");
                }
                catch { }
                var r = MessageBox.Show(
                    $"BeatSync didn't close properly last time — but an autosave{when} survived.\n\nRestore it? (No just deletes the autosave; your saved projects are untouched.)",
                    "Recover Autosaved Project", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r == MessageBoxResult.Yes) OpenProjectFromPath(AutosavePath);
                else
                {
                    try { File.Delete(AutosavePath); } catch { }
                    try { File.Delete(AutosaveMetaPath); } catch { }
                }
            }
            catch { }
        }

        private void MenuRecoverAutosave_Click(object sender, RoutedEventArgs e)
        {
            if (!File.Exists(AutosavePath))
            {
                MessageBox.Show("There's no autosaved project right now. Autosave runs every 2 minutes while you're working on a project, so one appears on its own.", "Recover Autosave", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            OpenProjectFromPath(AutosavePath);
        }

        public static List<string> GetRecentProjects()
        {
            try
            {
                if (!File.Exists(RecentProjectsPath)) return new List<string>();
                var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(RecentProjectsPath)) ?? new List<string>();
                return list.Where(File.Exists).Take(8).ToList();
            }
            catch { return new List<string>(); }
        }

        public static void RememberRecentProject(string path)
        {
            try
            {
                var list = GetRecentProjects();
                list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                list.Insert(0, path);
                Directory.CreateDirectory(AppDataDir);
                File.WriteAllText(RecentProjectsPath, JsonSerializer.Serialize(list.Take(8).ToList()));
            }
            catch { }
        }

        public bool StartScreenEnabled
        {
            get
            {
                try
                {
                    if (File.Exists(AppSettingsPath))
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(AppSettingsPath));
                        if (doc.RootElement.TryGetProperty("ShowStartScreen", out var v)) return v.GetBoolean();
                    }
                }
                catch { }
                return true;
            }
            set
            {
                try
                {
                    Directory.CreateDirectory(AppDataDir);
                    File.WriteAllText(AppSettingsPath, JsonSerializer.Serialize(new { ShowStartScreen = value }));
                }
                catch { }
            }
        }

        public void OpenStartScreen()
        {
            var start = new StartScreenWindow(this) { Owner = this };
            start.ShowDialog();
        }

        private void MenuStartScreen_Click(object sender, RoutedEventArgs e) => OpenStartScreen();

        /// <summary>Public entry the start screen uses to run the normal open-project dialog flow.</summary>
        public void BtnOpenProjectPublic() => BtnOpenProject_Click(this, new RoutedEventArgs());

        /// <summary>Open a specific .bsp without showing the file dialog (recent-projects list).</summary>
        public void OpenProjectFromPath(string path)
        {
            _openPathOverride = path;
            try { BtnOpenProject_Click(this, new RoutedEventArgs()); }
            finally { _openPathOverride = null; }
        }

        /// <summary>
        /// Start-screen template: optionally clear the timeline (library stays),
        /// set the track count and beat grid, then run a follow-up action.
        /// </summary>
        public void StartNewProject(int audioTracks, double bpm, bool openDj, bool importVideo)
        {
            if (TimelineBlocks.Count > 0)
            {
                var confirm = MessageBox.Show(
                    "Start fresh? The current timeline will be cleared (your imported library stays).",
                    "Start Screen", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes) return;

                isPlayingTimeline = false;
                playbackCancelToken?.Cancel();
                MasterVideoPlayer.Stop();
                MasterVideoPlayer.Source = null;
                VideoPath = null;
                SetVideoAudioSeparated(false);
                SetVideoSpeed(1.0);
                TimelineBlocks.Clear();
            }

            while (AudioTrackCount < audioTracks) AudioTrackCount++;
            while (AudioTrackCount > Math.Max(1, audioTracks)) AudioTrackCount--;
            ProjectBpm = bpm;
            BeatOffsetSeconds = 0;
            LoopStartX = null;
            LoopEndX = null;
            LoopEnabled = false;
            UpdateLoopButtons();
            BuildTrackChains();
            RenderStudioGridLanes();

            if (openDj) BtnDj_Click(this, new RoutedEventArgs());
            if (importVideo) BtnImportVideo_Click(this, new RoutedEventArgs());
        }

        private void BtnMonitorFx_Click(object sender, RoutedEventArgs e)
        {
            if (_monitorFxWindow == null)
            {
                _monitorFxWindow = new MonitorFxWindow(MonitorSettings) { Owner = this };
                _monitorFxWindow.Closed += (s, ev) => _monitorFxWindow = null;
                _monitorFxWindow.Show();
            }
            else
            {
                _monitorFxWindow.Activate();
            }
        }

        private void BtnSetA_Click(object sender, RoutedEventArgs e)
        {
            LoopStartX = SnapX(_currentPlayX);
            if (LoopEndX.HasValue && LoopEndX.Value <= LoopStartX.Value) LoopEndX = null;
            UpdateLoopButtons();
        }

        private void BtnSetB_Click(object sender, RoutedEventArgs e)
        {
            LoopEndX = SnapX(_currentPlayX);
            if (LoopStartX.HasValue && LoopEndX.Value <= LoopStartX.Value) LoopStartX = null;
            UpdateLoopButtons();
        }

        private void BtnLoopToggle_Click(object sender, RoutedEventArgs e)
        {
            if (!LoopEnabled && !(LoopStartX.HasValue && LoopEndX.HasValue))
            {
                MessageBox.Show("Set an A point and a B point first: play the song, click Set A where the loop should start and Set B where it should end.", "A–B Loop");
                return;
            }
            LoopEnabled = !LoopEnabled;
            UpdateLoopButtons();
        }

        private void UpdateLoopButtons()
        {
            RefreshLoopVisual();
            if (SetABtn != null) SetABtn.Content = LoopStartX.HasValue ? $"A · {FmtTime(LoopStartX.Value / FixedBlock.PixelsPerSecond)}" : "Set A";
            if (SetBBtn != null) SetBBtn.Content = LoopEndX.HasValue ? $"B · {FmtTime(LoopEndX.Value / FixedBlock.PixelsPerSecond)}" : "Set B";
            if (LoopToggleBtn != null)
            {
                LoopToggleBtn.Content = LoopEnabled ? "🔁 Loop ON" : "🔁 Loop";
                LoopToggleBtn.Background = new SolidColorBrush(LoopEnabled ? Color.FromRgb(0x1D, 0xB9, 0x54) : Color.FromRgb(0x2A, 0x30, 0x3C));
            }
        }

        private void BtnOutput_Click(object sender, RoutedEventArgs e)
        {
            ShowOutputWindow();
        }

        // v3.88: shared with the DJ window's live streaming — open (or
        // raise) the Output window, and report its on-screen rectangle
        // so the streamer can capture exactly that region.
        // v3.94: stream overlay lines, pushed by the DJ window and held
        // here so the Output window shows the latest values whenever it
        // is (re)opened.
        private string _overlayNow = "", _overlayListeners = "", _overlayRequest = "";
        private bool _overlayVisible = true;

        internal void SetStreamOverlay(string nowPlaying, string listeners, string latestRequest, bool visible)
        {
            _overlayNow = nowPlaying; _overlayListeners = listeners; _overlayRequest = latestRequest; _overlayVisible = visible;
            _outputWindow?.ApplyOverlay(nowPlaying, listeners, latestRequest, visible);
        }

        internal void ShowOutputWindow()
        {
            if (_outputWindow == null)
            {
                _outputWindow = new OutputWindow(this) { Owner = this };
                _outputWindow.Closed += (s, ev) => _outputWindow = null;
                _outputWindow.Show();
                _outputWindow.ApplyOverlay(_overlayNow, _overlayListeners, _overlayRequest, _overlayVisible);
            }
            else
            {
                if (_outputWindow.WindowState == WindowState.Minimized)
                    _outputWindow.WindowState = WindowState.Normal;
                _outputWindow.Activate();
            }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        internal bool TryGetOutputWindowScreenRect(out int x, out int y, out int w, out int h)
        {
            x = 0; y = 0; w = 0; h = 0;
            if (_outputWindow == null) return false;
            var hwnd = new System.Windows.Interop.WindowInteropHelper(_outputWindow).Handle;
            if (hwnd == IntPtr.Zero || IsIconic(hwnd)) return false;
            if (!GetWindowRect(hwnd, out RECT r)) return false;
            x = r.Left; y = r.Top; w = r.Right - r.Left; h = r.Bottom - r.Top;
            return w > 50 && h > 50;
        }

        // =========================
        // EXPORT (offline mixdown)
        // =========================

        private async void BtnExportAudio_Click(object sender, RoutedEventArgs e)
        {
            if (TimelineBlocks.Count == 0)
            {
                MessageBox.Show("Add some clips to the timeline first, then export your mix.", "Nothing to export");
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export your mix",
                Filter = "WAV audio|*.wav|MP3 audio|*.mp3",
                FileName = "BeatSync Mix.wav",
                DefaultExt = ".wav"
            };
            if (dialog.ShowDialog() != true) return;

            var options = new ExportOptionsWindow(
                System.IO.Path.GetFileNameWithoutExtension(dialog.FileName), ProjectBpm) { Owner = this };
            if (options.ShowDialog() != true) return;

            // Filename pattern ($project, $date, $bpm, …) renames the chosen file
            string finalPath = BuildExportPath(dialog.FileName, options);

            var job = BuildExportJob();
            job.MasteringPreset = options.Preset;
            job.MasteringIntensity = options.Intensity;
            job.NormalizeLoudness = options.NormalizeLoudness;
            var progressWindow = new ExportProgressWindow { Owner = this };
            progressWindow.Show();
            try
            {
                await Task.Run(() => ExportRenderer.Render(job, finalPath, progressWindow.Progress, progressWindow.Token));
                progressWindow.Close();
                MessageBox.Show($"Your mix is exported:\n{finalPath}", "💾 Export complete");
            }
            catch (OperationCanceledException)
            {
                progressWindow.Close();
                try { if (System.IO.File.Exists(finalPath)) System.IO.File.Delete(finalPath); } catch { }
                MessageBox.Show("Export cancelled — no file was saved.", "Export");
            }
            catch (Exception ex)
            {
                progressWindow.Close();
                MessageBox.Show("Export failed: " + ex.Message, "Export");
            }
        }

        private ExportJob BuildExportJob()
        {
            var job = new ExportJob();
            for (int i = 0; i < AudioTrackCount + 1; i++)
            {
                job.Chains.Add(new ExportChain
                {
                    Volume = GetTrackVolume(i),
                    Pan = GetTrackPan(i),
                    Fx = CloneFx(GetTrackFx(i))
                });
            }

            // Auto-crossfade lengths per clip (same math as live playback)
            var fadeByBlock = new Dictionary<FixedBlock, (double Fin, double Fout)>();
            if (AutoCrossfadeEnabled)
            {
                var audioBlocks = TimelineBlocks.Where(x => x.IsAudio).ToList();
                var lanes = audioBlocks.Select(x => (x.LaneIndex, x.StartX / FixedBlock.PixelsPerSecond, x.LengthSeconds)).ToList();
                for (int i = 0; i < audioBlocks.Count; i++)
                {
                    if (audioBlocks[i].LengthSeconds > 0.2)
                        fadeByBlock[audioBlocks[i]] = ClipAudio.ComputeFades(lanes, i);
                }
            }

            double total = 0;
            foreach (var block in TimelineBlocks.Where(b => !b.IsVideo))
            {
                if (block.SoundType == "image" || block.RawBytes == null) continue;
                int chain = block.IsVideoAudio ? AudioTrackCount : Math.Clamp(block.TrackId, 0, AudioTrackCount - 1);
                double length = block.LengthSeconds > 0 ? block.LengthSeconds
                    : (double.IsNaN(block.Width) ? 0 : block.Width / FixedBlock.PixelsPerSecond);
                var clip = new ExportClip
                {
                    RawBytes = block.RawBytes,
                    SoundType = block.SoundType,
                    StartSeconds = block.StartX / FixedBlock.PixelsPerSecond,
                    Speed = block.Speed,
                    SourceStartSeconds = block.SourceStartSeconds,
                    LengthSeconds = block.LengthSeconds,
                    Volume = block.Volume * MasterVolume,
                    ChainIndex = chain,
                    Reverse = block.Reverse,
                    Normalize = block.Normalize,
                    NudgeMs = block.NudgeMs,
                    Fx = block.ClipFx,
                    VolumePoints = block.VolumePoints,
                    GateOn = block.GateOn,
                    GateThresholdDb = block.GateThresholdDb,
                    KeepPitch = block.KeepPitch
                };
                if (fadeByBlock.TryGetValue(block, out var fades))
                {
                    clip.FadeInSeconds = fades.Fin;
                    clip.FadeOutSeconds = fades.Fout;
                }
                job.Clips.Add(clip);
                total = Math.Max(total, block.StartX / FixedBlock.PixelsPerSecond + length);
            }

            // The video's own soundtrack joins the mix when it has not been
            // separated onto its lane (when separated, that block is already in).
            var videoBlock = TimelineBlocks.FirstOrDefault(b => b.IsVideo);
            if (VideoPath != null && videoBlock != null && !VideoAudioSeparated)
            {
                double vlen = videoBlock.LengthSeconds > 0 ? videoBlock.LengthSeconds
                    : (double.IsNaN(videoBlock.Width) ? 0 : videoBlock.Width / FixedBlock.PixelsPerSecond);
                if (vlen > 0)
                {
                    job.Clips.Add(new ExportClip
                    {
                        FilePath = VideoPath,
                        StartSeconds = videoBlock.StartX / FixedBlock.PixelsPerSecond,
                        Speed = VideoSpeed,
                        SourceStartSeconds = 0,
                        LengthSeconds = vlen,
                        Volume = MasterVolume,
                        ChainIndex = AudioTrackCount
                    });
                    total = Math.Max(total, videoBlock.StartX / FixedBlock.PixelsPerSecond + vlen);
                }
            }

            job.TotalSeconds = Math.Max(1.0, total) + 2.0; // tail so echo/reverb can ring out
            return job;
        }

        private static TrackFxSettings CloneFx(TrackFxSettings s) => new TrackFxSettings
        {
            EqOn = s.EqOn, LowDb = s.LowDb, MidDb = s.MidDb, HighDb = s.HighDb,
            DelayOn = s.DelayOn, DelayMs = s.DelayMs, DelayFeedback = s.DelayFeedback, DelayMix = s.DelayMix,
            ReverbOn = s.ReverbOn, ReverbSize = s.ReverbSize, ReverbDamp = s.ReverbDamp, ReverbMix = s.ReverbMix
        };

        private async Task TimelinePlaybackEngineLoop(CancellationToken token)
        {
            HashSet<FixedBlock> playedBlocks = new HashSet<FixedBlock>();
            double originOffsetX = 0;

            Dispatcher.Invoke(() =>
            {
                if (playhead != null) playhead.Visibility = Visibility.Visible;
                if (playheadHandle != null) playheadHandle.Visibility = Visibility.Visible;
            });

            while (isPlayingTimeline && !token.IsCancellationRequested)
            {
                double currentX = ((_playStopwatch?.ElapsedMilliseconds ?? 0) / 1000.0) * FixedBlock.PixelsPerSecond - originOffsetX;
                _currentPlayX = currentX;

                // A–B loop: at B, jump the clock back to A and re-arm the
                // blocks inside the region (blocks spanning the loop point
                // keep playing out — their start sits before A).
                bool didWrap = false;
                if (LoopEnabled && LoopStartX.HasValue && LoopEndX.HasValue && currentX >= LoopEndX.Value)
                {
                    originOffsetX += LoopEndX.Value - LoopStartX.Value;
                    currentX -= LoopEndX.Value - LoopStartX.Value;
                    _currentPlayX = currentX;
                    didWrap = true;
                    var rearmed = playedBlocks.Where(b => b.EffectiveStartX >= LoopStartX.Value && b.EffectiveStartX < LoopEndX.Value).ToList();
                    foreach (var b in rearmed) playedBlocks.Remove(b);
                }

                if (currentX > GetContentEndX() + 2 * FixedBlock.PixelsPerSecond) break;

                Dispatcher.Invoke(() =>
                {
                    if (didWrap && MasterVideoPlayer?.Source != null && LoopStartX.HasValue)
                    {
                        try { MasterVideoPlayer.Position = TimeSpan.FromSeconds(LoopStartX.Value / FixedBlock.PixelsPerSecond); } catch { }
                    }
                    if (playhead != null)
                    {
                        playhead.X1 = currentX;
                        playhead.X2 = currentX;
                    }
                    if (playheadHandle != null) Canvas.SetLeft(playheadHandle, currentX - 6);
                    UpdateTimeDisplay(currentX / FixedBlock.PixelsPerSecond);

                    // Follow the playhead so the song stays in view while it plays
                    if (TimelineScrollViewer != null)
                    {
                        double offset = TimelineScrollViewer.HorizontalOffset;
                        double viewWidth = TimelineScrollViewer.ViewportWidth;
                        if (viewWidth > 0 && (currentX < offset || currentX > offset + viewWidth - 60))
                        {
                            TimelineScrollViewer.ScrollToHorizontalOffset(Math.Max(0, currentX - 60));
                        }
                    }
                });

                foreach (var block in TimelineBlocks.Where(b => !b.IsVideo && !playedBlocks.Contains(b) && currentX >= b.EffectiveStartX))
                {
                    playedBlocks.Add(block);
                    Task.Run(() => PlayBlockSoundThreaded(block));
                }

                await Task.Delay(10);
            }

            Dispatcher.Invoke(() =>
            {
                isPlayingTimeline = false;
                _isPaused = false;
                if (playhead != null) playhead.Visibility = Visibility.Collapsed;
                if (playheadHandle != null) playheadHandle.Visibility = Visibility.Collapsed;
            });
        }

        /// <summary>When on, overlapping clips on the same lane crossfade into each other automatically.</summary>
        public bool AutoCrossfadeEnabled { get; set; } = true;

        private void MenuAutoCrossfade_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem item) AutoCrossfadeEnabled = item.IsChecked;
        }

        private void PlayBlockSoundThreaded(FixedBlock block)
        {
            if (mixerEngine == null || block.SoundType == "image") return;
            try
            {
                // Nudge shifts the audible window: early nudges skip into the
                // sound, and the window's audible length shrinks by the shift
                // either way so the clip still ends on its boundary.
                double shiftSec = block.NudgeMs / 1000.0;
                double extraSkip = Math.Max(0, -(block.StartX / FixedBlock.PixelsPerSecond + shiftSec));
                double audibleLen = block.LengthSeconds > 0
                    ? Math.Max(0.05, block.LengthSeconds - Math.Abs(shiftSec))
                    : Math.Max(0.05, block.FileDurationSeconds / Math.Max(0.01, block.Speed));

                ISampleProvider provider;
                bool useStretch = block.KeepPitch && Math.Abs(block.Speed - 1.0) > 0.001;
                if (block.Reverse || block.Normalize || useStretch)
                {
                    // All three need the whole segment decoded in memory
                    provider = useStretch
                        ? TimeStretch.BuildSegmentStretched(block.RawBytes, block.SoundType, block.Speed,
                            block.SourceStartSeconds, audibleLen, extraSkip, block.Reverse, block.Normalize)
                        : ClipAudio.BuildSegment(block.RawBytes, block.SoundType, block.Speed,
                            block.SourceStartSeconds, audibleLen, extraSkip, block.Reverse, block.Normalize);
                }
                else
                {
                    IWaveProvider reader = AudioReaderFactory.Open(block.RawBytes, block.SoundType);

                    provider = new WdlResamplingSampleProvider(reader.ToSampleProvider(), 44100).ToStereo();

                    // Per-clip speed (pitch shifts with it, like a record)
                    if (Math.Abs(block.Speed - 1.0) > 0.001)
                    {
                        provider = new SpeedSampleProvider(provider, block.Speed);
                    }

                    // Play only the clip's window of the sound: skip the trimmed-off
                    // front, stop at the trimmed end
                    if (block.LengthSeconds > 0)
                    {
                        provider = new OffsetSampleProvider(provider)
                        {
                            SkipOver = TimeSpan.FromSeconds(block.SourceStartSeconds / block.Speed + extraSkip),
                            Take = TimeSpan.FromSeconds(audibleLen)
                        };
                    }
                }

                // Auto-crossfade where same-lane neighbours overlap this clip
                if (AutoCrossfadeEnabled && block.LengthSeconds > 0.2)
                {
                    var audioBlocks = TimelineBlocks.Where(x => x.IsAudio).ToList();
                    int idx = audioBlocks.IndexOf(block);
                    if (idx >= 0)
                    {
                        var lanes = audioBlocks.Select(x => (x.LaneIndex, x.StartX / FixedBlock.PixelsPerSecond, x.LengthSeconds)).ToList();
                        var (fin, fout) = ClipAudio.ComputeFades(lanes, idx);
                        if (fin > 0.005 || fout > 0.005)
                            provider = new AutoFadeSampleProvider(provider, fin, fout, audibleLen);
                    }
                }

                if (block.GateOn) provider = new GateSampleProvider(provider, block.GateThresholdDb);
                if (ClipFxChain.HasAny(block.ClipFx)) provider = ClipFxChain.Apply(provider, block.ClipFx);
                if (block.VolumePoints.Count > 0) provider = new EnvelopeSampleProvider(provider, block.VolumePoints);

                var volumeSampleProvider = new VolumeSampleProvider(provider) { Volume = (float)(block.Volume * MasterVolume) };

                // Route through the clip's track chain (track volume + pan live there),
                // wrapped so Stop can cut the voice dead instead of letting it ring out
                int chain = block.IsVideoAudio ? AudioTrackCount : Math.Clamp(block.TrackId, 0, AudioTrackCount - 1);
                var target = chain >= 0 && chain < _trackMixers.Count ? _trackMixers[chain] : mixerEngine;
                var voice = new KillableSampleProvider(volumeSampleProvider);
                lock (_voicesLock) _liveVoices.Add(voice);
                voice.Ended += () => { lock (_voicesLock) _liveVoices.Remove(voice); };
                target.AddMixerInput(voice);
            }
            catch { }
        }

        // =========================
        // CLIP ACTIONS (called from FixedBlock)
        // =========================

        public void OpenNudgeDialog(FixedBlock block)
        {
            var dlg = new NudgeWindow(block.NudgeMs) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                block.NudgeMs = dlg.ValueMs;
                RefreshAfterBlockEdit();
            }
        }

        /// <summary>Volume-envelope editor for one clip.</summary>
        public void OpenEnvelopeEditor(FixedBlock block)
        {
            double duration = block.LengthSeconds > 0
                ? Math.Max(0.1, block.LengthSeconds - Math.Abs(block.NudgeMs) / 1000.0)
                : Math.Max(0.1, block.Width / FixedBlock.PixelsPerSecond);
            var dlg = new EnvelopeWindow(block.Filename, duration, block.VolumePoints) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                block.VolumePoints = dlg.Points;
                RefreshAfterBlockEdit();
            }
        }

        /// <summary>Noise-gate settings for one clip.</summary>
        public void OpenGateDialog(FixedBlock block)
        {
            var dlg = new GateWindow(block.GateOn, block.GateThresholdDb) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                block.GateOn = dlg.GateEnabled;
                block.GateThresholdDb = dlg.ThresholdDb;
                RefreshAfterBlockEdit();
            }
        }

        /// <summary>Spectrogram view for one clip.</summary>
        public void OpenSpectrogram(FixedBlock block)
        {
            if (block.RawBytes == null || block.RawBytes.Length == 0)
            {
                MessageBox.Show("That clip has no audio data loaded to analyze.", "Spectrogram");
                return;
            }
            var win = new SpectrogramWindow(block) { Owner = this };
            win.Show();
        }

        /// <summary>Per-clip FX editor — the same window as track FX, aimed at one clip's settings.</summary>
        public void OpenClipFxDialog(FixedBlock block)
        {
            var dlg = new TrackFxWindow(block.ClipFx, $"Clip: {block.Filename}") { Owner = this };
            dlg.ShowDialog();
            RefreshAfterBlockEdit();
        }

        public void RemoveBlock(FixedBlock block)
        {
            if (TimelineCanvas != null && TimelineCanvas.Children.Contains(block.BlockVisual))
            {
                TimelineCanvas.Children.Remove(block.BlockVisual);
            }
            TimelineBlocks.Remove(block);
            RenderStudioGridLanes();
        }

        public void RefreshAfterBlockEdit()
        {
            RenderStudioGridLanes();
        }

        /// <summary>True once the video's sound lives on an audio track instead (the video itself plays silent).</summary>
        public bool VideoAudioSeparated { get; private set; }

        public void SetVideoAudioSeparated(bool separated)
        {
            VideoAudioSeparated = separated;
            UpdateVideoVolume();
        }

        private void UpdateVideoVolume()
        {
            if (MasterVideoPlayer != null)
            {
                MasterVideoPlayer.Volume = VideoAudioSeparated ? 0 : MasterVolume;
            }
        }

        /// <summary>Playback speed of the video itself (1.0 = normal). Its soundtrack follows along.</summary>
        public double VideoSpeed { get; private set; } = 1.0;

        // ---- Hooks for the detachable Output window ----
        /// <summary>True while the timeline is playing and not paused.</summary>
        public bool OutputShouldPlay => VideoPath != null && isPlayingTimeline && !_isPaused;

        /// <summary>The main video player's current position (null when unavailable).</summary>
        public TimeSpan? VideoPosition
        {
            get
            {
                try { return MasterVideoPlayer?.Position; }
                catch { return null; }
            }
        }

        private double _videoNaturalSeconds;

        private void MasterVideoPlayer_MediaOpened(object sender, RoutedEventArgs e)
        {
            if (MasterVideoPlayer.NaturalDuration.HasTimeSpan)
            {
                _videoNaturalSeconds = MasterVideoPlayer.NaturalDuration.TimeSpan.TotalSeconds;
                ApplyVideoBlockLength();
            }
        }

        public void SetVideoSpeed(double speed)
        {
            VideoSpeed = Math.Clamp(speed, 0.25, 3.0);
            if (MasterVideoPlayer != null)
            {
                MasterVideoPlayer.SpeedRatio = VideoSpeed;
            }
            ApplyVideoBlockLength();
        }

        private void ApplyVideoBlockLength()
        {
            if (_videoNaturalSeconds <= 0) return;
            var videoBlock = TimelineBlocks.FirstOrDefault(b => b.IsVideo);
            if (videoBlock == null) return;
            videoBlock.Speed = VideoSpeed;
            videoBlock.LengthSeconds = _videoNaturalSeconds / VideoSpeed;
            videoBlock.Width = videoBlock.LengthSeconds * FixedBlock.PixelsPerSecond;
            videoBlock.UpdateCanvasPosition();
        }

        /// <summary>Decodes the loaded video's soundtrack to WAV bytes; null when no video is loaded.</summary>
        private byte[]? ExtractVideoAudioBytes()
        {
            if (VideoPath == null || !File.Exists(VideoPath)) return null;

            using var reader = new MediaFoundationReader(VideoPath);
            using var ms = new MemoryStream();
            using (var writer = new WaveFileWriter(ms, reader.WaveFormat))
            {
                byte[] buffer = new byte[8192];
                int read;
                while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    writer.Write(buffer, 0, read);
                }
            }
            return ms.ToArray();
        }

        /// <summary>
        /// Pulls the soundtrack out of the loaded video and adds it to the
        /// asset library as a WAV, ready to drag onto any audio track.
        /// </summary>
        public void ExtractAudioFromVideo(FixedBlock block)
        {
            try
            {
                byte[]? bytes = ExtractVideoAudioBytes();
                if (bytes == null)
                {
                    MessageBox.Show("❌ No video file is loaded to extract audio from.", "Extract Audio", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string audioName = $"{System.IO.Path.GetFileNameWithoutExtension(VideoPath!)} (audio).wav";
                ZipSounds[audioName] = bytes;
                AddAssetButtonToGroup(CreateAssetButton($"🎵 {audioName}", audioName));

                MessageBox.Show($"✅ Audio extracted to the library as '{audioName}'.\n\nDrag it onto a timeline track to use it.", "Audio Extracted", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"❌ Could not extract audio: {ex.Message}", "Extract Audio", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Full separation: the video's soundtrack becomes a normal audio clip on
        /// the lane directly under the video (same start position) and the video
        /// itself goes silent, so its sound can be edited like any other clip.
        /// </summary>
        public void SeparateAudioFromVideo(FixedBlock block)
        {
            try
            {
                byte[]? bytes = ExtractVideoAudioBytes();
                if (bytes == null)
                {
                    MessageBox.Show("❌ No video file is loaded to separate audio from.", "Separate Audio", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string audioName = $"{System.IO.Path.GetFileNameWithoutExtension(VideoPath!)} (audio).wav";
                ZipSounds[audioName] = bytes;
                AddAssetButtonToGroup(CreateAssetButton($"🎵 {audioName}", audioName));

                var audioBlock = new FixedBlock(this, TimelineCanvas, 0, audioName, block.StartX, bytes, "wav", 0, false, true);
                TimelineBlocks.Add(audioBlock);

                SetVideoAudioSeparated(true);
                RenderStudioGridLanes();

                MessageBox.Show($"✅ Audio separated!\n\n'{audioName}' is now on the VIDEO AUDIO lane directly under the video, and the video is silent — edit the sound like any other clip (right-click it for volume/speed, drag its edge to stretch).\n\nTo hear the video's original sound again, right-click the video clip and turn its own sound back on.", "Separate Audio", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"❌ Could not separate audio: {ex.Message}", "Separate Audio", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnMute_Click(object sender, RoutedEventArgs e)
        {
            isMuted = !isMuted;
            if (isMuted)
            {
                savedVolume = MasterVolumeSlider.Value;
                MasterVolumeSlider.Value = 0;
                BtnMasterMute.Content = "🔇";
                BtnMasterMute.Background = Brushes.DarkRed;
            }
            else
            {
                MasterVolumeSlider.Value = savedVolume;
                BtnMasterMute.Content = "🔊";
                BtnMasterMute.Background = (Brush)new BrushConverter().ConvertFromString("#2A303C")!;
            }
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            MasterVolume = MasterVolumeSlider.Value;
            UpdateVideoVolume();
            UpdateVolumeDisplay();
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog { Filter = "BeatSync Project (*.bsp)|*.bsp", DefaultExt = ".bsp", Title = "Save Project As" };
            if (dlg.ShowDialog() != true) return;

            try
            {
                WriteProjectFile(dlg.FileName);
                _lastAutosaveFingerprint = ProjectFingerprint();
                RememberRecentProject(dlg.FileName);
                MessageBox.Show($"✅ Project saved successfully!\n\n📁 {dlg.FileName}", "Save Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"❌ Save failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>Serialize the current project to a .bsp file — the
        /// ONE writer behind Save As and the v3.74 autosave, so an
        /// autosaved project is a real, fully-openable project file.
        /// No dialogs, no popups.</summary>
        private void WriteProjectFile(string path)
        {
            var project = new
            {
                Created = DateTime.UtcNow,
                Version = "2.0",
                AudioTrackCount = this.AudioTrackCount,
                MasterVolume = this.MasterVolume,
                Bpm = this.ProjectBpm,
                BeatOffsetSeconds = this.BeatOffsetSeconds,
                SnapToBeat = this.SnapToBeatEnabled,
                Patterns = this.Patterns,
                PianoPatterns = this.PianoPatterns,
                SoundCategories = this.SoundCategories,
                TrackVolumes = this.TrackVolumeValues,
                TrackPans = this.TrackPanValues,
                TrackFx = this.TrackFxValues,
                Blocks = this.TimelineBlocks.Select(b => new
                {
                    b.Filename,
                    b.TrackId,
                    // Project files always store geometry at the canonical
                    // 100 px/s scale, whatever the current zoom is.
                    StartX = b.StartX * 100.0 / FixedBlock.PixelsPerSecond,
                    Width = b.Width * 100.0 / FixedBlock.PixelsPerSecond,
                    b.Volume,
                    b.SoundType,
                    b.IsVideo,
                    b.Speed,
                    b.SourceStartSeconds,
                    b.IsVideoAudio,
                    b.LengthSeconds,
                    b.Reverse,
                    b.Normalize,
                    b.NudgeMs,
                    ClipFx = b.ClipFx,
                    VolumePoints = b.VolumePoints,
                    b.GateOn,
                    b.GateThresholdDb,
                    b.KeepPitch
                }).ToList()
            };

            using (var fs = new FileStream(path, FileMode.Create))
            using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var pjEntry = archive.CreateEntry("project.json", CompressionLevel.Optimal);
                using (var es = pjEntry.Open())
                using (var sw = new StreamWriter(es))
                {
                    var options = new JsonSerializerOptions { WriteIndented = true };
                    sw.Write(JsonSerializer.Serialize(project, options));
                }

                foreach (var kv in ZipSounds)
                {
                    var assetEntry = archive.CreateEntry(System.IO.Path.Combine("assets", kv.Key), CompressionLevel.Optimal);
                    using (var es = assetEntry.Open())
                    {
                        es.Write(kv.Value, 0, kv.Value.Length);
                    }
                }

                foreach (var kv in VideoAssets.Where(x => File.Exists(x.Value)))
                {
                    var vEntry = archive.CreateEntry(System.IO.Path.Combine("video", kv.Key), CompressionLevel.Optimal);
                    using (var es = vEntry.Open())
                    using (var vf = new FileStream(kv.Value, FileMode.Open, FileAccess.Read))
                    {
                        vf.CopyTo(es);
                    }
                }

                foreach (var kv in ImageAssets.Where(x => File.Exists(x.Value)))
                {
                    var iEntry = archive.CreateEntry(System.IO.Path.Combine("images", kv.Key), CompressionLevel.Optimal);
                    using (var es = iEntry.Open())
                    using (var vf = new FileStream(kv.Value, FileMode.Open, FileAccess.Read))
                    {
                        vf.CopyTo(es);
                    }
                }
            }
        }

        private void BtnOpenProject_Click(object sender, RoutedEventArgs e)
        {
            string? fileName = _openPathOverride;
            if (fileName == null)
            {
                var dlg = new OpenFileDialog { Filter = "BeatSync Project (*.bsp)|*.bsp", Title = "Open Project" };
                if (dlg.ShowDialog() != true) return;
                fileName = dlg.FileName;
            }

            if (TimelineBlocks.Count > 0)
            {
                var confirm = MessageBox.Show("Loading a project replaces everything currently on the timeline.\n\nContinue?", "Open Project", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes) return;
            }

            try
            {
                // Stop anything playing and clear the current project
                isPlayingTimeline = false;
                playbackCancelToken?.Cancel();
                MasterVideoPlayer.Stop();
                MasterVideoPlayer.Source = null;
                VideoPath = null;
                SetVideoAudioSeparated(false);
                SetVideoSpeed(1.0);

                TimelineBlocks.Clear();
                ZipSounds.Clear();
                SoundCategories.Clear();
                VideoAssets.Clear();
                ImageAssets.Clear();
                ClearLibraryGroups();

                string extractDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BeatSyncStudioPro", "loaded-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(extractDir);

                int missing = 0;

                using (var fs = new FileStream(fileName, FileMode.Open, FileAccess.Read))
                using (var archive = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    var pjEntry = archive.GetEntry("project.json");
                    if (pjEntry == null) throw new InvalidDataException("project.json is missing — this doesn't look like a BeatSync project file.");

                    JsonDocument doc;
                    using (var es = pjEntry.Open())
                    using (var ms = new MemoryStream())
                    {
                        es.CopyTo(ms);
                        doc = JsonDocument.Parse(ms.ToArray());
                    }

                    var root = doc.RootElement;

                    if (root.TryGetProperty("AudioTrackCount", out var tc)) AudioTrackCount = Math.Max(1, tc.GetInt32());
                    if (root.TryGetProperty("MasterVolume", out var mv)) MasterVolumeSlider.Value = mv.GetDouble();
                    if (root.TryGetProperty("Bpm", out var bpmEl)) ProjectBpm = bpmEl.GetDouble();
                    if (root.TryGetProperty("BeatOffsetSeconds", out var boEl)) BeatOffsetSeconds = boEl.GetDouble();
                    if (root.TryGetProperty("SnapToBeat", out var sbEl)) SnapToBeatEnabled = sbEl.GetBoolean();
                    if (root.TryGetProperty("PianoPatterns", out var ppEl))
                    {
                        try
                        {
                            var mels = JsonSerializer.Deserialize<List<PianoPattern>>(ppEl.GetRawText());
                            if (mels != null)
                            {
                                PianoPatterns.Clear();
                                PianoPatterns.AddRange(mels);
                            }
                        }
                        catch { }
                    }
                    if (root.TryGetProperty("Patterns", out var patEl))
                    {
                        try
                        {
                            var pats = JsonSerializer.Deserialize<List<PatternData>>(patEl.GetRawText());
                            if (pats != null)
                            {
                                Patterns.Clear();
                                Patterns.AddRange(pats);
                            }
                        }
                        catch { }
                    }
                    if (root.TryGetProperty("SoundCategories", out var scEl))
                    {
                        try
                        {
                            var cats = JsonSerializer.Deserialize<Dictionary<string, string>>(scEl.GetRawText());
                            if (cats != null)
                                foreach (var kv in cats) SoundCategories[kv.Key] = kv.Value;
                        }
                        catch { }
                    }
                    if (root.TryGetProperty("TrackVolumes", out var tvs))
                    {
                        TrackVolumeValues.Clear();
                        foreach (var x in tvs.EnumerateArray()) TrackVolumeValues.Add(x.GetDouble());
                    }
                    if (root.TryGetProperty("TrackPans", out var tps))
                    {
                        TrackPanValues.Clear();
                        foreach (var x in tps.EnumerateArray()) TrackPanValues.Add(x.GetDouble());
                    }
                    if (root.TryGetProperty("TrackFx", out var tfx))
                    {
                        try
                        {
                            var list = JsonSerializer.Deserialize<List<TrackFxSettings>>(tfx.GetRawText());
                            if (list != null)
                            {
                                TrackFxValues.Clear();
                                TrackFxValues.AddRange(list);
                            }
                        }
                        catch { }
                    }
                    BuildTrackChains();

                    // Bundled files: audio into memory, video/images extracted to disk
                    foreach (var entry in archive.Entries)
                    {
                        string full = entry.FullName.Replace('\\', '/');
                        if (full == "project.json" || entry.Name.Length == 0) continue;

                        if (full.StartsWith("assets/"))
                        {
                            using var es = entry.Open();
                            using var ms = new MemoryStream();
                            es.CopyTo(ms);
                            ZipSounds[entry.Name] = ms.ToArray();
                            string cat = SoundCategories.TryGetValue(entry.Name, out var savedCat) ? savedCat : SoundCategorizer.Infer(entry.Name);
                            AddAssetButtonToGroup(CreateAssetButton($"{SoundCategorizer.Icon(cat)} {entry.Name}", entry.Name));
                        }
                        else if (full.StartsWith("video/") || full.StartsWith("images/"))
                        {
                            string dest = System.IO.Path.Combine(extractDir, entry.Name);
                            using (var es = entry.Open())
                            using (var outFs = new FileStream(dest, FileMode.Create))
                            {
                                es.CopyTo(outFs);
                            }

                            if (full.StartsWith("video/"))
                            {
                                VideoAssets[entry.Name] = dest;
                                AddAssetButtonToGroup(CreateAssetButton($"🎬 {entry.Name}", entry.Name));
                            }
                            else
                            {
                                ImageAssets[entry.Name] = dest;
                                AddAssetButtonToGroup(CreateAssetButton($"🖼️ {entry.Name}", entry.Name));
                            }
                        }
                    }

                    // Rebuild the timeline clips with everything that was saved
                    if (root.TryGetProperty("Blocks", out var blocks))
                    {
                        foreach (var b in blocks.EnumerateArray())
                        {
                            string filename = b.GetProperty("Filename").GetString() ?? "clip";
                            int trackId = b.TryGetProperty("TrackId", out var t) ? t.GetInt32() : 0;
                            double startX = b.TryGetProperty("StartX", out var sx) ? sx.GetDouble() : 0;
                            double width = b.TryGetProperty("Width", out var w) ? w.GetDouble() : 0;
                            // Saved geometry is canonical 100 px/s — scale it to the current zoom.
                            double loadScale = FixedBlock.PixelsPerSecond / 100.0;
                            startX *= loadScale;
                            width *= loadScale;
                            double volume = b.TryGetProperty("Volume", out var v) ? v.GetDouble() : 1.0;
                            string soundType = b.TryGetProperty("SoundType", out var st) ? (st.GetString() ?? "") : "";
                            bool isVideo = b.TryGetProperty("IsVideo", out var iv) && iv.GetBoolean();
                            double speed = b.TryGetProperty("Speed", out var sp) ? sp.GetDouble() : 1.0;
                            double sourceStart = b.TryGetProperty("SourceStartSeconds", out var ss) ? ss.GetDouble() : 0;
                            bool isVideoAudio = b.TryGetProperty("IsVideoAudio", out var iva) && iva.GetBoolean();
                            double length = b.TryGetProperty("LengthSeconds", out var ln) ? ln.GetDouble() : 0;
                            bool reverse = b.TryGetProperty("Reverse", out var rv) && rv.GetBoolean();
                            bool normalize = b.TryGetProperty("Normalize", out var nm) && nm.GetBoolean();
                            int nudgeMs = b.TryGetProperty("NudgeMs", out var ng) ? ng.GetInt32() : 0;
                            TrackFxSettings? clipFx = null;
                            if (b.TryGetProperty("ClipFx", out var cfx))
                            {
                                try { clipFx = JsonSerializer.Deserialize<TrackFxSettings>(cfx.GetRawText()); } catch { }
                            }
                            System.Collections.Generic.List<EnvelopePoint>? volPoints = null;
                            if (b.TryGetProperty("VolumePoints", out var vpx))
                            {
                                try { volPoints = JsonSerializer.Deserialize<System.Collections.Generic.List<EnvelopePoint>>(vpx.GetRawText()); } catch { }
                            }
                            bool gateOn = b.TryGetProperty("GateOn", out var goEl) && goEl.GetBoolean();
                            double gateDb = b.TryGetProperty("GateThresholdDb", out var gdEl) ? gdEl.GetDouble() : -45;
                            bool keepPitch = b.TryGetProperty("KeepPitch", out var kpEl) && kpEl.GetBoolean();

                            if (isVideo)
                            {
                                var block = new FixedBlock(this, TimelineCanvas, trackId, filename, startX,
                                    Array.Empty<byte>(), soundType, width > 0 ? width : 280 * loadScale, true);
                                if (length > 0) block.LengthSeconds = length;
                                TimelineBlocks.Add(block);

                                if (VideoAssets.TryGetValue(filename, out var vpath))
                                {
                                    VideoPath = vpath;
                                    MasterVideoPlayer.Source = new Uri(vpath);
                                    MasterVideoPlayer.Pause();
                                }
                            }
                            else
                            {
                                if (!ZipSounds.TryGetValue(filename, out var bytes))
                                {
                                    missing++;
                                    continue;
                                }

                                var block = new FixedBlock(this, TimelineCanvas, trackId, filename, startX,
                                    bytes, soundType, 0, false, isVideoAudio);
                                block.Volume = volume;
                                block.Speed = speed;
                                block.SourceStartSeconds = sourceStart;
                                block.Reverse = reverse;
                                block.Normalize = normalize;
                                block.NudgeMs = nudgeMs;
                                if (clipFx != null) block.ClipFx = clipFx;
                                if (volPoints != null) block.VolumePoints = volPoints;
                                block.GateOn = gateOn;
                                block.GateThresholdDb = gateDb;
                                block.KeepPitch = keepPitch;
                                if (length > 0)
                                {
                                    block.LengthSeconds = length;
                                    block.Width = width > 0 ? width : length * FixedBlock.PixelsPerSecond;
                                }
                                block.UpdateCanvasPosition();
                                TimelineBlocks.Add(block);
                            }
                        }
                    }
                }

                if (TimelineBlocks.Any(b => b.IsVideoAudio)) SetVideoAudioSeparated(true);
                RenderStudioGridLanes();

                RememberRecentProject(fileName);
                string msg = $"✅ Project loaded!\n\n📁 {fileName}\n🎞️ {TimelineBlocks.Count} clip(s) on the timeline";
                if (missing > 0) msg += $"\n\n⚠️ {missing} clip(s) were skipped because their sound file wasn't in the project bundle.";
                MessageBox.Show(msg, "Open Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"❌ Could not open project: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            var videoBlock = TimelineBlocks.FirstOrDefault(b => b.IsVideo);
            if (videoBlock == null || VideoPath == null || !System.IO.File.Exists(VideoPath))
            {
                MessageBox.Show("Load a video onto the video lane first — video export combines that video (trim, speed and timeline position honored) with your full timeline mix.",
                    "🎬 Export Video", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export video",
                Filter = "MP4 video|*.mp4",
                FileName = "BeatSync Video.mp4",
                DefaultExt = ".mp4"
            };
            if (dialog.ShowDialog() != true) return;

            var options = new ExportOptionsWindow(
                System.IO.Path.GetFileNameWithoutExtension(dialog.FileName), ProjectBpm) { Owner = this };
            if (options.ShowDialog() != true) return;

            string finalPath = BuildExportPath(dialog.FileName, options);
            var job = BuildExportJob();
            job.MasteringPreset = options.Preset;
            job.MasteringIntensity = options.Intensity;
            job.NormalizeLoudness = options.NormalizeLoudness;

            var win = new VideoExportWindow(this, job, videoBlock, finalPath) { Owner = this };
            win.ShowDialog();
        }

        private void MenuExit_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private ChangelogWindow? _changelogWindow;
        private void BtnChangelog_Click(object sender, RoutedEventArgs e)
        {
            if (_changelogWindow != null) { _changelogWindow.Activate(); return; }
            _changelogWindow = new ChangelogWindow { Owner = this };
            _changelogWindow.Closed += (s, ev) => _changelogWindow = null;
            _changelogWindow.Show();
        }

        private SceneCutWindow? _sceneCutWindow;
        private void BtnSceneCut_Click(object sender, RoutedEventArgs e)
        {
            if (_sceneCutWindow != null) { _sceneCutWindow.Activate(); return; }
            _sceneCutWindow = new SceneCutWindow { Owner = this };
            _sceneCutWindow.Closed += (s, ev) => _sceneCutWindow = null;
            _sceneCutWindow.Show();
        }

        private AiLabWindow? _aiLabWindow;
        private void BtnAiLab_Click(object sender, RoutedEventArgs e)
        {
            if (_aiLabWindow != null) { _aiLabWindow.Activate(); return; }
            _aiLabWindow = new AiLabWindow(this) { Owner = this };
            _aiLabWindow.Closed += (s, ev) => _aiLabWindow = null;
            _aiLabWindow.Show();
        }

        private MySongsWindow? _mySongsWindow;
        private void BtnMySongs_Click(object sender, RoutedEventArgs e)
        {
            if (_mySongsWindow != null) { _mySongsWindow.Activate(); return; }
            _mySongsWindow = new MySongsWindow { Owner = this };
            _mySongsWindow.Closed += (s, ev) => _mySongsWindow = null;
            _mySongsWindow.Show();
        }

        // v3.59: Freesound + Internet Archive live in ONE categorized
        // window (🔊 sounds vs 🎵 music); both menu entries open it on
        // their tab.
        private SearchHubWindow? _searchHub;
        private void OpenSearchHub(int tab)
        {
            if (_searchHub != null)
            {
                _searchHub.Activate();
                if (tab is >= 0 and <= 5) _searchHub.SelectTab(tab);
                return;
            }
            _searchHub = new SearchHubWindow(this, tab) { Owner = this };
            _searchHub.Closed += (s, ev) => _searchHub = null;
            _searchHub.Show();
        }

        private void BtnFreesound_Click(object sender, RoutedEventArgs e) => OpenSearchHub(0);

        private void BtnArchive_Click(object sender, RoutedEventArgs e) => OpenSearchHub(1);

        // v3.66: Jamendo joins the hub as the third source (tab 2).
        private void BtnJamendo_Click(object sender, RoutedEventArgs e) => OpenSearchHub(2);

        // v3.79: Audius joins the hub as the fourth source (tab 3).
        private void BtnAudius_Click(object sender, RoutedEventArgs e) => OpenSearchHub(3);

        // v3.80: Pixabay joins the hub as the fifth source (tab 4).
        private void BtnPixabay_Click(object sender, RoutedEventArgs e) => OpenSearchHub(4);

        // v3.81: Mixkit gets a hub tab (5) too — an explainer + browser
        // route, since Mixkit has no API and its terms forbid scraping.
        private void BtnMixkit_Click(object sender, RoutedEventArgs e) => OpenSearchHub(5);

        private SoundPackWindow? _soundPackWindow;
        private void BtnSoundPack_Click(object sender, RoutedEventArgs e)
        {
            if (_soundPackWindow != null) { _soundPackWindow.Activate(); return; }
            _soundPackWindow = new SoundPackWindow(this) { Owner = this };
            _soundPackWindow.Closed += (s, ev) => _soundPackWindow = null;
            _soundPackWindow.Show();
        }

        private DownloadsWindow? _downloadsWindow;
        private void BtnDownloads_Click(object sender, RoutedEventArgs e)
        {
            if (_downloadsWindow != null) { _downloadsWindow.Activate(); return; }
            _downloadsWindow = new DownloadsWindow { Owner = this };
            _downloadsWindow.Closed += (s, ev) => _downloadsWindow = null;
            _downloadsWindow.Show();
        }

        // ===== v3.46: library category order for the sidebar browser =====
        private static readonly string[] LibraryGroupOrder =
        {
            SoundCategorizer.Drums, SoundCategorizer.Guitar, SoundCategorizer.Bass, SoundCategorizer.Keys,
            SoundCategorizer.Loops, SoundCategorizer.Vocals, SoundCategorizer.Fx, SoundCategorizer.Stems,
            SoundCategorizer.Mashups, SoundCategorizer.Other, "Video", "Images"
        };

        private string CategoryForAsset(string name)
        {
            if (VideoAssets.ContainsKey(name)) return "Video";
            if (ImageAssets.ContainsKey(name)) return "Images";
            if (SoundCategories.TryGetValue(name, out var c)) return SoundCategorizer.RefineStemCategory(c, name);
            return SoundCategorizer.RefineStemCategory(SoundCategorizer.Infer(name), name);
        }

        private static string GroupIcon(string category) => category switch
        {
            "Video" => "🎬",
            "Images" => "🖼️",
            _ => SoundCategorizer.Icon(category)
        };

        // ===== v3.46/v3.47 sidebar browser: category list + sound list, the same
        // shape as the DJ sampler picker (it replaces the old button-list library).
        // Rows come from the project's asset dictionaries PLUS the same outside
        // sources the picker reads (built-in pack, the compressed sound-library
        // archive, stem cache, Downloads) — picking an outside sound pulls it
        // into the project library on the spot. =====
        private sealed class ExternalSound
        {
            public string Key = "";
            public string Display = "";
            public string FileName = "";
            public string Category = "";
            public Func<byte[]?> Load = () => null;
        }

        private sealed class BrowserEntry
        {
            public string Key = "";          // library asset name, or "ext:" + ExternalSound.Key
            public string Display = "";
            public string Category = "";
            public ExternalSound? Ext;
        }

        private readonly List<string> _browserCategories = new();
        private readonly List<string> _soundListNames = new();
        private bool _refreshingLibrary;
        // v3.55: the stems sub-categories nest under one expandable "+ Stems"
        // parent row (Michael's layout) instead of sitting as flat siblings.
        private bool _stemsExpanded = true;
        private const string StemsParentKey = "STEMS_PARENT";
        private static bool IsStemsFamily(string c) => c == SoundCategorizer.Stems || c.StartsWith("Stems · ", StringComparison.Ordinal);
        private static string StemChildLabel(string c) => c == SoundCategorizer.Stems ? "General" : c.Substring("Stems · ".Length);
        private Point _libDragStart;
        private string? _libDragName;
        private List<ExternalSound> _externals = new();
        private bool _externalsBuilt;
        private readonly HashSet<string> _hiddenExternals = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _materializedFrom = new();

        private void AddAssetButtonToGroup(Button btn) => RefreshLibraryBrowser();

        private void ClearLibraryGroups() => RefreshLibraryBrowser();

        /// <summary>Remove one asset from the library entirely (dictionaries + UI).
        /// Callers handle the confirm dialog.</summary>
        public void RemoveLibraryAsset(string name)
        {
            ZipSounds.Remove(name);
            SoundCategories.Remove(name);
            VideoAssets.Remove(name);
            ImageAssets.Remove(name);
            _materializedFrom.Remove(name);
            if (draggingSoundName == name) draggingSoundName = null;
            RefreshLibraryBrowser();
        }

        private string SelectedBrowserCategory()
        {
            int i = LibraryCategoryList?.SelectedIndex ?? -1;
            return i >= 0 && i < _browserCategories.Count ? _browserCategories[i] : "";
        }

        private void BuildExternalCatalog()
        {
            _externals = new List<ExternalSound>();
            _externalsBuilt = true;
            var seen = new HashSet<(string, string)>();
            void Add(ExternalSound e)
            {
                if (!seen.Add((e.Category, e.Display.ToLowerInvariant()))) return;
                _externals.Add(e);
            }
            // 1) Built-in sound pack (same source as the DJ picker)
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
                        Add(new ExternalSound
                        {
                            Key = "pack:" + entryName,
                            Display = System.IO.Path.GetFileNameWithoutExtension(entry.Name),
                            FileName = entry.Name,
                            Category = cat,
                            Load = () => SamplerEngine.ReadPackEntry(entryName)
                        });
                    }
                }
            }
            catch { }
            // 2) The compressed sound-library archive (v3.47 — imports live in ONE zip)
            try
            {
                EnsureSoundLibraryArchive();
                foreach (var (entryName, cat, fileName) in LibraryArchiveEntries())
                {
                    string captured = entryName;
                    Add(new ExternalSound
                    {
                        Key = "archive:" + entryName,
                        Display = System.IO.Path.GetFileNameWithoutExtension(fileName),
                        FileName = fileName,
                        Category = cat,
                        Load = () => ReadLibraryArchiveEntry(captured)
                    });
                }
            }
            catch { }
            // 3) Stem cache files on disk, filed per stem type
            try
            {
                string stemsRoot = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BeatSyncStudioPro", "stems");
                if (Directory.Exists(stemsRoot))
                {
                    foreach (var file in Directory.EnumerateFiles(stemsRoot, "*.wav", SearchOption.AllDirectories))
                    {
                        string fn = System.IO.Path.GetFileNameWithoutExtension(file);
                        string display = fn;
                        string cat = SoundCategorizer.Stems;
                        int p = fn.LastIndexOf(" (", StringComparison.Ordinal);
                        if (p > 0 && fn.EndsWith(")"))
                        {
                            string stemWord = fn.Substring(p + 2, fn.Length - p - 3);
                            display = fn.Substring(0, p) + " — " + char.ToUpperInvariant(stemWord[0]) + stemWord.Substring(1);
                            cat = SoundCategorizer.StemCategory(stemWord);
                        }
                        else if (fn.ToLowerInvariant() is "vocals" or "drums" or "bass" or "other")
                        {
                            string parent = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(file) ?? "");
                            int dash = parent.LastIndexOf('-');
                            if (dash > 0 && parent.Substring(dash + 1).All(char.IsDigit)) parent = parent.Substring(0, dash);
                            display = (parent.Length > 0 ? parent + " — " : "") + char.ToUpperInvariant(fn[0]) + fn.Substring(1).ToLowerInvariant();
                            cat = SoundCategorizer.StemCategory(fn);
                        }
                        // Safety net: if the name parse still landed on plain
                        // "Stems", the (separately verified) refine pass gets
                        // the last word from the real file name.
                        if (cat == SoundCategorizer.Stems) cat = SoundCategorizer.RefineStemCategory(cat, file);
                        string path = file;
                        Add(new ExternalSound
                        {
                            Key = "file:" + path,
                            Display = display,
                            FileName = display + ".wav",
                            Category = cat,
                            Load = () => File.Exists(path) ? File.ReadAllBytes(path) : null
                        });
                    }
                }
            }
            catch { }
            // 4) Downloads folder
            try
            {
                string dl = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                if (Directory.Exists(dl))
                {
                    foreach (var file in Directory.EnumerateFiles(dl, "*.*", SearchOption.TopDirectoryOnly)
                                 .Where(f => f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)))
                    {
                        string path = file;
                        Add(new ExternalSound
                        {
                            Key = "file:" + path,
                            Display = System.IO.Path.GetFileNameWithoutExtension(file),
                            FileName = System.IO.Path.GetFileName(file),
                            Category = "Downloads",
                            Load = () => File.Exists(path) ? File.ReadAllBytes(path) : null
                        });
                    }
                }
            }
            catch { }
        }

        private List<BrowserEntry> MergedEntries()
        {
            if (!_externalsBuilt) BuildExternalCatalog();
            var entries = new List<BrowserEntry>();
            var seen = new HashSet<(string, string)>();
            void AddLib(string name, string cat)
            {
                if (!seen.Add((cat, System.IO.Path.GetFileNameWithoutExtension(name).ToLowerInvariant()))) return;
                entries.Add(new BrowserEntry { Key = name, Display = name, Category = cat });
            }
            foreach (var name in ZipSounds.Keys) AddLib(name, CategoryForAsset(name));
            foreach (var name in VideoAssets.Keys) AddLib(name, "Video");
            foreach (var name in ImageAssets.Keys) AddLib(name, "Images");
            foreach (var ext in _externals)
            {
                if (_hiddenExternals.Contains(ext.Key)) continue;
                if (!seen.Add((ext.Category, ext.Display.ToLowerInvariant()))) continue;
                entries.Add(new BrowserEntry { Key = "ext:" + ext.Key, Display = ext.Display, Category = ext.Category, Ext = ext });
            }
            return entries;
        }

        private void RefreshLibraryBrowser()
        {
            if (LibraryCategoryList == null || LibrarySoundList == null) return;
            string keepCat = SelectedBrowserCategory();
            var entries = MergedEntries();
            var counts = new Dictionary<string, int>();
            foreach (var e in entries) counts[e.Category] = counts.TryGetValue(e.Category, out var n) ? n + 1 : 1;
            var cats = counts.Keys.ToList();
            cats.Sort((a, b) =>
            {
                int ra = LibraryGroupRank(a), rb = LibraryGroupRank(b);
                return ra != rb ? ra.CompareTo(rb) : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            });
            _refreshingLibrary = true;
            _browserCategories.Clear();
            _browserCategories.Add("");
            LibraryCategoryList.Items.Clear();
            LibraryCategoryList.Items.Add($"\U0001F5C2\uFE0F All  ({entries.Count})");
            bool stemsEmitted = false;
            foreach (var c in cats)
            {
                if (IsStemsFamily(c))
                {
                    if (stemsEmitted) continue;
                    stemsEmitted = true;
                    // v3.55: one parent row for the whole stems family; the
                    // per-type categories become indented child rows under it.
                    var fam = cats.Where(IsStemsFamily).ToList();
                    int total = fam.Sum(fc => counts[fc]);
                    _browserCategories.Add(StemsParentKey);
                    LibraryCategoryList.Items.Add($"{(_stemsExpanded ? "\u2796" : "\u2795")} {GroupIcon(SoundCategorizer.Stems)} Stems  ({total})");
                    if (_stemsExpanded)
                    {
                        var children = fam.Where(fc => fc != SoundCategorizer.Stems)
                                          .Concat(fam.Where(fc => fc == SoundCategorizer.Stems));
                        foreach (var fc in children)
                        {
                            _browserCategories.Add(fc);
                            LibraryCategoryList.Items.Add($" -- {StemChildLabel(fc)}  ({counts[fc]})");
                        }
                    }
                    continue;
                }
                _browserCategories.Add(c);
                LibraryCategoryList.Items.Add($"{GroupIcon(c)} {c}  ({counts[c]})");
            }
            int sel = _browserCategories.IndexOf(keepCat);
            if (sel < 0 && (keepCat == StemsParentKey || IsStemsFamily(keepCat)))
                sel = _browserCategories.IndexOf(StemsParentKey);
            LibraryCategoryList.SelectedIndex = sel >= 0 ? sel : 0;
            _refreshingLibrary = false;
            RefillSoundList(entries);
        }

        private void RefillSoundList(List<BrowserEntry>? entries = null)
        {
            if (LibrarySoundList == null) return;
            entries ??= MergedEntries();
            string cat = SelectedBrowserCategory();
            var rows = entries
                .Where(e => cat == "" || e.Category == cat || (cat == StemsParentKey && IsStemsFamily(e.Category)))
                .OrderBy(e => e.Display, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _refreshingLibrary = true;
            _soundListNames.Clear();
            LibrarySoundList.Items.Clear();
            foreach (var e in rows)
            {
                _soundListNames.Add(e.Key);
                LibrarySoundList.Items.Add($"{GroupIcon(e.Category)} {e.Display}");
            }
            _refreshingLibrary = false;
        }

        public void ApplyLibraryFilter() => RefreshLibraryBrowser();

        private void LibraryCategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_refreshingLibrary) return;
            RefillSoundList();
        }

        // v3.55: clicking the "+ Stems" parent row folds/unfolds its
        // sub-category rows (Bass, Drums, Other, Vocals). MouseLeftButtonUp,
        // not SelectionChanged, so re-clicking the already-selected parent
        // toggles too.
        private void LibraryCategoryList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var el = LibraryCategoryList.InputHitTest(e.GetPosition(LibraryCategoryList)) as DependencyObject;
            var item = ItemsControl.ContainerFromElement(LibraryCategoryList, el) as ListBoxItem;
            if (item == null) return;
            int idx = LibraryCategoryList.ItemContainerGenerator.IndexFromContainer(item);
            if (idx >= 0 && idx < _browserCategories.Count && _browserCategories[idx] == StemsParentKey)
            {
                _stemsExpanded = !_stemsExpanded;
                RefreshLibraryBrowser();
            }
        }

        private void LibrarySoundList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_refreshingLibrary) return;
            var key = SelectedSoundName();
            if (key == null) return;
            var name = ResolveAssetKey(key);   // outside sounds join the project library when picked
            if (name != null) draggingSoundName = name;   // click-to-place, same as the old buttons
        }

        private string? SelectedSoundName()
        {
            int i = LibrarySoundList?.SelectedIndex ?? -1;
            return i >= 0 && i < _soundListNames.Count ? _soundListNames[i] : null;
        }

        private string? SoundNameFromPoint(Point point)
        {
            var el = LibrarySoundList.InputHitTest(point) as DependencyObject;
            var item = ItemsControl.ContainerFromElement(LibrarySoundList, el) as ListBoxItem;
            if (item == null) return null;
            int idx = LibrarySoundList.ItemContainerGenerator.IndexFromContainer(item);
            return idx >= 0 && idx < _soundListNames.Count ? _soundListNames[idx] : null;
        }

        /// <summary>Turn a browser row key into a real library asset name,
        /// pulling outside sounds into the project library on first use.</summary>
        private string? ResolveAssetKey(string key)
        {
            if (!key.StartsWith("ext:", StringComparison.Ordinal)) return key;
            var ext = _externals.Find(x => "ext:" + x.Key == key);
            return ext == null ? null : MaterializeExternal(ext);
        }

        private string? MaterializeExternal(ExternalSound ext)
        {
            try
            {
                var bytes = ext.Load();
                if (bytes == null) return null;
                string finalName = AddToLibraryOnly(bytes, ext.FileName, ext.Category);
                _materializedFrom[finalName] = ext.Key;
                return finalName;
            }
            catch { return null; }
        }

        private void LibrarySoundList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _libDragStart = e.GetPosition(null);
            _libDragName = SoundNameFromPoint(e.GetPosition(LibrarySoundList));
        }

        private void LibrarySoundList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Right-click acts on the row under the mouse, not the old selection.
            var el = LibrarySoundList.InputHitTest(e.GetPosition(LibrarySoundList)) as DependencyObject;
            var item = ItemsControl.ContainerFromElement(LibrarySoundList, el) as ListBoxItem;
            if (item != null) LibrarySoundList.SelectedIndex = LibrarySoundList.ItemContainerGenerator.IndexFromContainer(item);
        }

        private void LibrarySoundList_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _libDragName == null) return;
            Point pos = e.GetPosition(null);
            if (Math.Abs(pos.X - _libDragStart.X) <= SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - _libDragStart.Y) <= SystemParameters.MinimumVerticalDragDistance) return;
            string key = _libDragName;
            _libDragName = null;
            var name = ResolveAssetKey(key);
            if (name == null) return;
            draggingSoundName = name;
            var result = DragDrop.DoDragDrop(LibrarySoundList, new DataObject(DataFormats.StringFormat, name), DragDropEffects.Copy);
            if (result == DragDropEffects.None && draggingSoundName == name) draggingSoundName = null;
        }

        private void LibrarySoundList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var key = SoundNameFromPoint(e.GetPosition(LibrarySoundList)) ?? SelectedSoundName();
            if (key == null) return;
            var name = ResolveAssetKey(key);
            if (name != null && ZipSounds.ContainsKey(name)) PreviewLibrarySound(name);
        }

        private void BtnLibraryPreview_Click(object sender, RoutedEventArgs e)
        {
            // Toggle: while a preview is sounding, this button is Stop.
            if (_libPreviewOut != null) { StopLibraryPreview(); return; }
            var key = SelectedSoundName();
            if (key == null) return;
            var name = ResolveAssetKey(key);
            if (name != null) PreviewLibrarySound(name);
        }

        private void BtnLibraryDelete_Click(object sender, RoutedEventArgs e)
        {
            var key = SelectedSoundName();
            if (key != null) DeleteSound(key);
        }

        private void BtnLibraryExport_Click(object sender, RoutedEventArgs e)
        {
            var key = SelectedSoundName();
            if (key != null) ExportSound(key);
        }

        private void BtnLibraryRename_Click(object sender, RoutedEventArgs e)
        {
            var key = SelectedSoundName();
            if (key != null) RenameSound(key);
        }

        private void DeleteSound(string key)
        {
            if (key.StartsWith("ext:", StringComparison.Ordinal))
            {
                var ext = _externals.Find(x => "ext:" + x.Key == key);
                if (ext == null) return;
                if (ext.Key.StartsWith("archive:", StringComparison.Ordinal))
                {
                    var ask = MessageBox.Show($"Delete '{ext.Display}' permanently?\n\nIt's removed from your sound-library archive and this project. This can't be undone.",
                        "Delete sound", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (ask != MessageBoxResult.Yes) return;
                    RemoveLibraryArchiveEntryExact(ext.Key.Substring(8));
                    if (ZipSounds.ContainsKey(ext.FileName)) RemoveLibraryAsset(ext.FileName);
                }
                else if (ext.Key.StartsWith("pack:", StringComparison.Ordinal))
                {
                    var ask = MessageBox.Show($"Hide '{ext.Display}' from the list?\n\nIt's part of the built-in sound pack, so the pack keeps its copy — hiding just clears it from view for now.",
                        "Hide sound", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (ask != MessageBoxResult.Yes) return;
                }
                else
                {
                    var ask = MessageBox.Show($"Remove '{ext.Display}' from the list?\n\nThe file itself stays where it is (Downloads / stem cache) — this only clears it from view for now.",
                        "Remove sound", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (ask != MessageBoxResult.Yes) return;
                }
                _hiddenExternals.Add(ext.Key);
                RefreshLibraryBrowser();
                return;
            }
            DeleteLibraryAsset(key);
            if (_materializedFrom.TryGetValue(key, out var src)) _hiddenExternals.Add(src);
        }

        private void ExportSound(string key)
        {
            byte[]? bytes = null;
            string fileName;
            if (key.StartsWith("ext:", StringComparison.Ordinal))
            {
                var ext = _externals.Find(x => "ext:" + x.Key == key);
                if (ext == null) return;
                bytes = ext.Load();
                fileName = ext.FileName;
            }
            else if (ZipSounds.TryGetValue(key, out var zb)) { bytes = zb; fileName = key; }
            else if (VideoAssets.TryGetValue(key, out var vp)) { try { bytes = File.ReadAllBytes(vp); } catch { } fileName = key; }
            else if (ImageAssets.TryGetValue(key, out var ip)) { try { bytes = File.ReadAllBytes(ip); } catch { } fileName = key; }
            else return;
            if (bytes == null) { MessageBox.Show("That sound couldn't be read for export.", "Export sound"); return; }
            var dialog = new SaveFileDialog { Title = "Export sound", FileName = fileName, Filter = "Audio / media files|*.wav;*.mp3;*.mp4;*.png;*.jpg|All files|*.*" };
            if (dialog.ShowDialog() != true) return;
            try { File.WriteAllBytes(dialog.FileName, bytes); }
            catch (Exception ex) { MessageBox.Show("Export failed: " + ex.Message, "Export sound"); }
        }

        private void RenameSound(string key)
        {
            string name = key;
            if (key.StartsWith("ext:", StringComparison.Ordinal))
            {
                var resolved = ResolveAssetKey(key);
                if (resolved == null) return;
                name = resolved;
            }
            if (ZipSounds.ContainsKey(name))
            {
                string ext = System.IO.Path.GetExtension(name);
                string initial = System.IO.Path.GetFileNameWithoutExtension(name);
                var typed = PromptText("Rename sound", $"New name for '{name}':", initial);
                if (typed == null) return;
                string newName = typed.Trim();
                if (newName.Length == 0 || newName == initial) return;
                if (!newName.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) newName += ext;
                if (RenameLibraryAsset(name, newName) == null)
                    MessageBox.Show($"Couldn't rename — a sound named '{newName}' already exists, or the name is invalid.", "Rename sound");
            }
            else if (VideoAssets.TryGetValue(name, out var vp) || ImageAssets.TryGetValue(name, out vp))
            {
                bool isVideo = VideoAssets.ContainsKey(name);
                string ext = System.IO.Path.GetExtension(name);
                string initial = System.IO.Path.GetFileNameWithoutExtension(name);
                var typed = PromptText("Rename", $"New name for '{name}':", initial);
                if (typed == null) return;
                string newName = typed.Trim();
                if (newName.Length == 0 || newName == initial) return;
                if (!newName.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) newName += ext;
                if (VideoAssets.ContainsKey(newName) || ImageAssets.ContainsKey(newName) || ZipSounds.ContainsKey(newName))
                {
                    MessageBox.Show($"A library item named '{newName}' already exists.", "Rename");
                    return;
                }
                if (isVideo) { VideoAssets.Remove(name); VideoAssets[newName] = vp; }
                else { ImageAssets.Remove(name); ImageAssets[newName] = vp; }
                RefreshLibraryBrowser();
            }
        }

        /// <summary>Rename a library sound everywhere it lives: project library,
        /// its category record, and its entry in the sound-library archive.</summary>
        public string? RenameLibraryAsset(string oldName, string newName)
        {
            newName = (newName ?? "").Trim();
            if (newName.Length == 0 || newName == oldName) return null;
            if (!ZipSounds.TryGetValue(oldName, out var bytes)) return null;
            if (ZipSounds.ContainsKey(newName)) return null;
            ZipSounds.Remove(oldName);
            ZipSounds[newName] = bytes;
            if (SoundCategories.TryGetValue(oldName, out var cat))
            {
                SoundCategories.Remove(oldName);
                SoundCategories[newName] = cat;
            }
            if (_materializedFrom.TryGetValue(oldName, out var src))
            {
                _materializedFrom.Remove(oldName);
                _materializedFrom[newName] = src;
            }
            RenameLibraryArchiveEntryForFile(oldName, newName, bytes);
            if (draggingSoundName == oldName) draggingSoundName = newName;
            RefreshLibraryBrowser();
            return newName;
        }

        /// <summary>Small text prompt dialog (rename etc.). Returns null on cancel.</summary>
        public static string? PromptText(string title, string message, string initial)
        {
            var input = new TextBox { Text = initial, MinWidth = 260, Margin = new Thickness(10) };
            var ok = new Button { Content = "OK", IsDefault = true, Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(10, 0, 10, 10), HorizontalAlignment = HorizontalAlignment.Right };
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = message, Margin = new Thickness(10, 10, 10, 0), Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, MaxWidth = 340 });
            panel.Children.Add(input);
            panel.Children.Add(ok);
            var dlg = new Window
            {
                Title = title,
                Content = panel,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background = new SolidColorBrush(Color.FromRgb(0x17, 0x1B, 0x26)),
                Foreground = Brushes.White,
                ResizeMode = ResizeMode.NoResize
            };
            ok.Click += (s, e) => { dlg.DialogResult = true; };
            input.Loaded += (s, e) => { input.Focus(); input.SelectAll(); };
            return dlg.ShowDialog() == true ? input.Text : null;
        }

        private WaveOutEvent? _libPreviewOut;

        private void PreviewLibrarySound(string name)
        {
            StopLibraryPreview();
            if (!ZipSounds.TryGetValue(name, out var bytes)) return;
            try
            {
                var samples = SamplerEngine.DecodeBytes(bytes, name);
                _libPreviewOut = new WaveOutEvent();
                _libPreviewOut.Init(new SampleToWaveProvider(new ArraySampleProvider(samples)));
                var thisPlayer = _libPreviewOut;
                thisPlayer.PlaybackStopped += (s, e) =>
                {
                    // Sound finished naturally: the button flips back to Preview.
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (ReferenceEquals(_libPreviewOut, thisPlayer) && LibraryPreviewButton != null)
                            LibraryPreviewButton.Content = "▶ Preview";
                    }));
                };
                _libPreviewOut.Play();
                if (LibraryPreviewButton != null) LibraryPreviewButton.Content = "⏹ Stop";
            }
            catch { StopLibraryPreview(); }
        }

        private void StopLibraryPreview()
        {
            try { _libPreviewOut?.Stop(); _libPreviewOut?.Dispose(); } catch { }
            _libPreviewOut = null;
            if (LibraryPreviewButton != null) LibraryPreviewButton.Content = "▶ Preview";
        }

        private void DeleteLibraryAsset(string name)
        {
            var ask = MessageBox.Show($"Delete '{name}' from the library?\n\nIf it's in your sound-library archive, it's removed from there too. This can't be undone.",
                "Delete sound", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (ask != MessageBoxResult.Yes) return;
            RemoveLibraryAsset(name);
            RemoveLibraryArchiveEntryForFile(name);
            // Legacy loose files from the short-lived folder layout (pre-archive).
            try
            {
                string root = SoundLibraryRoot;
                if (Directory.Exists(root))
                {
                    foreach (var file in Directory.EnumerateFiles(root, name, SearchOption.AllDirectories))
                        File.Delete(file);
                }
            }
            catch { /* the library entry is gone either way */ }
            RefreshLibraryBrowser();
        }

        // ===== Sound-library archive (v3.47): imported sounds live in ONE
        // compressed file the whole app reads — %AppData%/BeatSyncStudioPro/sound-library.zip
        // with entries "<Category>/<filename>". Loose files from the old folder
        // layout are packed in once and removed. =====
        public static string SoundLibraryRoot => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BeatSyncStudioPro", "sound-library");

        public static string SoundLibraryArchivePath => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BeatSyncStudioPro", "sound-library.zip");

        private static bool _archiveReady;

        // Audit Bug 2 (v3.61): EVERY open of sound-library.zip goes through
        // this one lock. The archive is touched from the UI thread, imports,
        // mashup saves and a background stem-completion callback — two
        // unsynchronized opens meant "file in use" errors and could tear
        // the zip (the likely cause of the unreadable-archive incident on
        // 2026-10-22). Monitor locks are reentrant, so methods may freely
        // call each other while holding it.
        private static readonly object ArchiveLock = new object();

        public static void EnsureSoundLibraryArchive()
        {
            lock (ArchiveLock) { EnsureSoundLibraryArchiveLocked(); }
        }

        private static void EnsureSoundLibraryArchiveLocked()
        {
            if (_archiveReady) return;
            _archiveReady = true;
            try
            {
                string root = SoundLibraryRoot;
                if (!Directory.Exists(root)) return;
                var loose = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                    .Where(f => f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (loose.Count == 0) return;
                using var zip = ZipFile.Open(SoundLibraryArchivePath, ZipArchiveMode.Update);
                foreach (var file in loose)
                {
                    string dirName = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(file) ?? "");
                    if (dirName.Length == 0 || dirName == "sound-library") dirName = SoundCategorizer.Infer(file);
                    string entryName = dirName + "/" + System.IO.Path.GetFileName(file);
                    long len = new FileInfo(file).Length;
                    var existing = zip.GetEntry(entryName);
                    if (existing != null && existing.Length == len) { try { File.Delete(file); } catch { } continue; }
                    existing?.Delete();
                    var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                    using var es = entry.Open();
                    using var fs = File.OpenRead(file);
                    fs.CopyTo(es);
                    try { File.Delete(file); } catch { }
                }
            }
            catch { /* archive trouble must never block the app */ }
            try
            {
                string root = SoundLibraryRoot;
                if (Directory.Exists(root) && !Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories).Any())
                    Directory.Delete(root, true);
            }
            catch { }
        }

        /// <summary>(entryName, category, fileName) for every audio entry in the archive.</summary>
        public static List<(string EntryName, string Category, string FileName)> LibraryArchiveEntries()
        {
            lock (ArchiveLock) { return LibraryArchiveEntriesLocked(); }
        }

        private static List<(string EntryName, string Category, string FileName)> LibraryArchiveEntriesLocked()
        {
            var list = new List<(string, string, string)>();
            try
            {
                EnsureSoundLibraryArchive();
                if (!File.Exists(SoundLibraryArchivePath)) return list;
                using var zip = ZipFile.OpenRead(SoundLibraryArchivePath);
                foreach (var entry in zip.Entries)
                {
                    if (entry.Name.Length == 0) continue;
                    string ext = System.IO.Path.GetExtension(entry.Name).ToLowerInvariant();
                    if (ext != ".wav" && ext != ".mp3") continue;
                    string full = entry.FullName.Replace('\\', '/');
                    string cat = full.Contains('/') ? full.Substring(0, full.IndexOf('/')) : SoundCategorizer.Infer(entry.Name);
                    cat = SoundCategorizer.RefineStemCategory(cat, entry.Name);
                    list.Add((entry.FullName, cat, entry.Name));
                }
            }
            catch { }
            return list;
        }

        public static byte[]? ReadLibraryArchiveEntry(string entryName)
        {
            lock (ArchiveLock) { return ReadLibraryArchiveEntryLocked(entryName); }
        }

        private static byte[]? ReadLibraryArchiveEntryLocked(string entryName)
        {
            try
            {
                if (!File.Exists(SoundLibraryArchivePath)) return null;
                using var zip = ZipFile.OpenRead(SoundLibraryArchivePath);
                var entry = zip.GetEntry(entryName);
                if (entry == null) return null;
                using var es = entry.Open();
                using var ms = new MemoryStream();
                es.CopyTo(ms);
                return ms.ToArray();
            }
            catch { return null; }
        }

        /// <summary>Store a sound in the sound-library archive (used by Mashup save).</summary>
        public void StoreInSoundLibraryArchive(string category, string fileName, byte[] bytes)
        {
            WriteLibraryArchiveEntry(category, fileName, bytes);
            _externalsBuilt = false;
        }

        private static void WriteLibraryArchiveEntry(string category, string fileName, byte[] bytes)
        {
            lock (ArchiveLock)
            {
                EnsureSoundLibraryArchive();
                using var zip = ZipFile.Open(SoundLibraryArchivePath, ZipArchiveMode.Update);
                string entryName = category + "/" + fileName;
                zip.GetEntry(entryName)?.Delete();
                var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                using var es = entry.Open();
                es.Write(bytes, 0, bytes.Length);
            }
        }

        /// <summary>Rename one archive entry in place (same category folder, new file name).</summary>
        public static bool RenameLibraryArchiveEntryExact(string entryName, string newFileName)
        {
            try
            {
                var bytes = ReadLibraryArchiveEntry(entryName);
                if (bytes == null) return false;
                string full = entryName.Replace('\\', '/');
                string cat = full.Contains('/') ? full.Substring(0, full.IndexOf('/')) : "Other";
                RemoveLibraryArchiveEntryExact(entryName);
                WriteLibraryArchiveEntry(cat, newFileName, bytes);
                return true;
            }
            catch { return false; }
        }

        public static void RemoveLibraryArchiveEntryExact(string entryName)
        {
            try
            {
                lock (ArchiveLock)
                {
                    if (!File.Exists(SoundLibraryArchivePath)) return;
                    using var zip = ZipFile.Open(SoundLibraryArchivePath, ZipArchiveMode.Update);
                    zip.GetEntry(entryName)?.Delete();
                }
            }
            catch { }
        }

        private static void RemoveLibraryArchiveEntryForFile(string fileName)
        {
            try
            {
                lock (ArchiveLock)
                {
                    if (!File.Exists(SoundLibraryArchivePath)) return;
                    using var zip = ZipFile.Open(SoundLibraryArchivePath, ZipArchiveMode.Update);
                    var hits = zip.Entries.Where(en => en.Name == fileName).ToList();
                    foreach (var h in hits) h.Delete();
                }
            }
            catch { }
        }

        private static void RenameLibraryArchiveEntryForFile(string oldName, string newName, byte[] bytes)
        {
            try
            {
                lock (ArchiveLock)
                {
                    if (!File.Exists(SoundLibraryArchivePath)) return;
                    using var zip = ZipFile.Open(SoundLibraryArchivePath, ZipArchiveMode.Update);
                    var hit = zip.Entries.FirstOrDefault(en => en.Name == oldName);
                    if (hit == null) return;
                    string full = hit.FullName.Replace('\\', '/');
                    string cat = full.Contains('/') ? full.Substring(0, full.IndexOf('/')) : "Other";
                    hit.Delete();
                    var entry = zip.CreateEntry(cat + "/" + newName, CompressionLevel.Optimal);
                    using var es = entry.Open();
                    es.Write(bytes, 0, bytes.Length);
                }
            }
            catch { }
        }

        private static readonly string[] ImportAudioExtensions = { ".wav", ".mp3" };

        private static string CategoryFromZipPath(string fullName)
        {
            string first = fullName.Replace('\\', '/').Split('/')[0];
            string f = " " + first.ToLowerInvariant() + " ";
            if (f.Contains("drum") || f.Contains("kick") || f.Contains("snare") || f.Contains("perc")) return SoundCategorizer.Drums;
            if (f.Contains("guitar") || f.Contains("riff")) return SoundCategorizer.Guitar;
            if (f.Contains("bass")) return SoundCategorizer.Bass;
            if (f.Contains("key") || f.Contains("piano") || f.Contains("synth") || f.Contains("pad")) return SoundCategorizer.Keys;
            if (f.Contains("loop")) return SoundCategorizer.Loops;
            if (f.Contains("vocal") || f.Contains("vox") || f.Contains("voice")) return SoundCategorizer.Vocals;
            if (f.Contains("fx") || f.Contains("sfx") || f.Contains("sound effect")) return SoundCategorizer.Fx;
            if (f.Contains("stem")) return SoundCategorizer.RefineStemCategory(SoundCategorizer.Stems, fullName);
            return SoundCategorizer.Infer(fullName);
        }

        /// <summary>Import ZIP archives and/or loose audio files: every sound is
        /// categorized (folder hint, else keyword inference), stored in the ONE
        /// compressed sound-library archive, and added to the project library.
        /// A sound whose name + size already exists is skipped — never duplicated.</summary>
        public (int Added, int Skipped) ImportSoundArchives(IEnumerable<string> paths)
        {
            int added = 0, skipped = 0;
            var pending = new List<(string DisplayName, string Category, byte[] Bytes)>();
            foreach (var path in paths)
            {
                try
                {
                    if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        using var zip = System.IO.Compression.ZipFile.OpenRead(path);
                        foreach (var entry in zip.Entries)
                        {
                            string ext = System.IO.Path.GetExtension(entry.Name).ToLowerInvariant();
                            if (!ImportAudioExtensions.Contains(ext) || entry.Name.Length == 0) continue;
                            using var es = entry.Open();
                            using var ms = new MemoryStream();
                            es.CopyTo(ms);
                            pending.Add((entry.Name, CategoryFromZipPath(entry.FullName), ms.ToArray()));
                        }
                    }
                    else if (ImportAudioExtensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant()))
                    {
                        pending.Add((System.IO.Path.GetFileName(path), SoundCategorizer.Infer(path), File.ReadAllBytes(path)));
                    }
                }
                catch { skipped++; }
            }
            // Snapshot what's already in the archive (name → sizes) for the dedup check.
            var archived = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                lock (ArchiveLock)
                {
                    if (File.Exists(SoundLibraryArchivePath))
                    {
                        using var zip = ZipFile.OpenRead(SoundLibraryArchivePath);
                        foreach (var en in zip.Entries)
                        {
                            if (!archived.TryGetValue(en.Name, out var l)) { l = new List<long>(); archived[en.Name] = l; }
                            l.Add(en.Length);
                        }
                    }
                }
            }
            catch { }
            foreach (var (displayName, category, bytes) in pending)
            {
                string name = displayName;
                if (ZipSounds.TryGetValue(name, out var existing) && existing.Length == bytes.Length) { skipped++; continue; }
                if (archived.TryGetValue(name, out var sizes) && sizes.Contains(bytes.Length)) { skipped++; continue; }
                if (ZipSounds.ContainsKey(name) || archived.ContainsKey(name))
                {
                    string baseName = System.IO.Path.GetFileNameWithoutExtension(name);
                    string ext = System.IO.Path.GetExtension(name);
                    int n = 2;
                    while (ZipSounds.ContainsKey($"{baseName} ({n}){ext}") || archived.ContainsKey($"{baseName} ({n}){ext}")) n++;
                    name = $"{baseName} ({n}){ext}";
                }
                try
                {
                    WriteLibraryArchiveEntry(category, name, bytes);
                    if (!archived.TryGetValue(name, out var l2)) { l2 = new List<long>(); archived[name] = l2; }
                    l2.Add(bytes.Length);
                    AddToLibraryOnly(bytes, name, category);
                    added++;
                }
                catch { skipped++; }
            }
            _externalsBuilt = false;
            RefreshLibraryBrowser();
            return (added, skipped);
        }

        // ===== Title-bar download indicator: while anything is downloading,
        // the window title carries its name, percent, speed and ETA. =====
        private void UpdateDownloadTitle()
        {
            try
            {
                var active = DownloadManager.Items.Where(i => i.State == "Downloading" || i.State == "Paused").ToList();
                if (active.Count == 0) { Title = "BeatSync Studio Pro"; return; }
                var first = active[0];
                Title = $"BeatSync Studio Pro — ⬇ {first.Name} {first.Percent:0}% · {first.Detail}" +
                        (active.Count > 1 ? $"  (+{active.Count - 1} more downloading)" : "");
            }
            catch { }
        }

        private void BtnImportSounds_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import sounds — ZIP packs or loose audio files",
                Filter = "Sound packs & audio|*.zip;*.wav;*.mp3",
                Multiselect = true
            };
            if (dialog.ShowDialog() != true) return;
            var (added, skipped) = ImportSoundArchives(dialog.FileNames);
            MessageBox.Show($"Imported {added} sound{(added == 1 ? "" : "s")} into the library, sorted by category.\n{skipped} skipped (duplicates or unreadable files).",
                "Import sounds", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private CreateSongWindow? _createSongWindow;
        private void BtnCreateSong_Click(object sender, RoutedEventArgs e)
        {
            if (_createSongWindow != null) { _createSongWindow.Activate(); return; }
            _createSongWindow = new CreateSongWindow(this) { Owner = this };
            _createSongWindow.Closed += (s, ev) => _createSongWindow = null;
            _createSongWindow.Show();
        }

        private void MenuAbout_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("BeatSync Studio Pro\n\nCreated by Michael DeCato\n\nYour music mixing editor — multitrack timeline, mixer with EQ/delay/reverb, video, DJ decks and visuals on the way.",
                "About BeatSync Studio Pro", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        protected override void OnClosed(EventArgs e)
        {
            StopLibraryPreview();
            outputDevice?.Dispose();
            base.OnClosed(e);
        }
    }
}
