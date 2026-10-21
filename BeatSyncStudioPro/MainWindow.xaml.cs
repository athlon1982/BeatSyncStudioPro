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
using System.Windows.Shapes;
using Microsoft.Win32;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace BeatSyncStudioPro
{
    public partial class MainWindow : Window
    {
        public Dictionary<string, byte[]> ZipSounds { get; private set; } = new Dictionary<string, byte[]>();
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
            double periodPx = 60.0 / ProjectBpm * 100.0;
            double offsetPx = BeatOffsetSeconds * 100.0;
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
            double offsetTimeline = block.StartX / 100.0 + (beatFile - block.SourceStartSeconds) / Math.Max(0.01, block.Speed);
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
            var (bpm, fileOffset, conf) = await System.Threading.Tasks.Task.Run(() => BeatDetector.Detect(bytes, type));
            if (bpm <= 0) return null;
            return ComputeGridFromClip(block, bpm, fileOffset, conf);
        }

        /// <summary>Detect the beat grid from one specific clip and apply it (clip context menu).</summary>
        public async void DetectBeatsFromClip(FixedBlock block)
        {
            if (block.RawBytes == null || block.RawBytes.Length == 0) return;
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
        private bool _djAttached;

        /// <summary>Route the DJ decks' master mix into the main engine (once).</summary>
        public void AttachDjMixer(ISampleProvider provider)
        {
            if (mixerEngine == null || _djAttached) return;
            mixerEngine.AddMixerInput(provider);
            _djAttached = true;
        }
        private MixerWindow? _mixerWindow;

        // Pause state for the timeline transport
        private System.Diagnostics.Stopwatch? _playStopwatch;
        private bool _isPaused;
        private Line? playhead;
        private Rectangle? playheadHandle;
        private int _rulerDrawnSeconds = 25;
        private CancellationTokenSource? playbackCancelToken;
        private string? draggingSoundName = null;
        private Point dragStartPoint;

        private static readonly string[] VideoExtensions = new[] { ".mp4", ".avi", ".wmv", ".mkv", ".mov", ".flv", ".webm", ".mpg", ".mpeg", ".ogv", ".3gp", ".m4v" };
        private static readonly string[] ImageExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tiff" };
        private static readonly string[] AudioExtensions = new[] { ".wav", ".mp3", ".ogg", ".mid", ".midi" };

        public MainWindow()
        {
            InitializeComponent();
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

        private void RenderTimeRulerHeaders()
        {
            if (RulerTicksCanvas == null) return;
            RulerTicksCanvas.Children.Clear();
            int totalSeconds = Math.Max(25, (int)Math.Ceiling(TimelineLengthPx / 100.0));
            _rulerDrawnSeconds = totalSeconds;
            for (int sec = 0; sec <= totalSeconds; sec++)
            {
                double xPos = sec * 100;
                Line tickLine = new Line { X1 = xPos, Y1 = 20, X2 = xPos, Y2 = 40, Stroke = (Brush)new BrushConverter().ConvertFromString("#A0A5B5")!, StrokeThickness = 1 };
                RulerTicksCanvas.Children.Add(tickLine);

                TextBlock txtTime = new TextBlock { Text = sec < 60 ? $"{sec:D2}s" : FmtTime(sec), Foreground = (Brush)new BrushConverter().ConvertFromString("#A0A5B5")!, FontWeight = FontWeights.Bold, FontSize = 9 };
                Canvas.SetLeft(txtTime, xPos - 12);
                Canvas.SetTop(txtTime, 2);
                RulerTicksCanvas.Children.Add(txtTime);
            }
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
                        Opacity = 0.3,
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
                double w = double.IsNaN(b.Width) ? b.LengthSeconds * 100 : b.Width;
                end = Math.Max(end, b.StartX + w);
            }
            return end;
        }

        /// <summary>The timeline is at least 25 seconds wide and grows to fit the content.</summary>
        private double TimelineLengthPx => Math.Max(2500, GetContentEndX() + 500);

        private static string FmtTime(double seconds)
        {
            if (seconds < 0 || double.IsNaN(seconds)) seconds = 0;
            var ts = TimeSpan.FromSeconds(seconds);
            return $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
        }

        private void UpdateTimeDisplay(double currentSeconds)
        {
            if (TimeDisplay != null)
                TimeDisplay.Text = $"{FmtTime(currentSeconds)} / {FmtTime(GetContentEndX() / 100.0)}";
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
            if (tlWidth / 100 > _rulerDrawnSeconds) RenderTimeRulerHeaders();
            UpdateTimeDisplay(0);

            playhead = new Line
            {
                X1 = 0, Y1 = 0, X2 = 0, Y2 = timelineHeight,
                Stroke = new SolidColorBrush(Color.FromRgb(0x69, 0xF0, 0xAE)),
                StrokeThickness = 3,
                Visibility = Visibility.Collapsed
            };
            Panel.SetZIndex(playhead, 999);
            TimelineCanvas.Children.Add(playhead);

            playheadHandle = new Rectangle
            {
                Width = 13, Height = 11,
                Fill = new SolidColorBrush(Color.FromRgb(0x69, 0xF0, 0xAE)),
                Visibility = Visibility.Collapsed
            };
            Canvas.SetLeft(playheadHandle, -6);
            Canvas.SetTop(playheadHandle, 0);
            Panel.SetZIndex(playheadHandle, 1000);
            TimelineCanvas.Children.Add(playheadHandle);

            // Beat grid lines (under the clips; every 4th line is a brighter downbeat)
            if (ProjectBpm > 0)
            {
                double periodPx = 60.0 / ProjectBpm * 100.0;
                double offsetPx = BeatOffsetSeconds * 100.0;
                var beatBrush = new SolidColorBrush(Color.FromArgb(70, 0xFF, 0xC8, 0x3D));
                var downbeatBrush = new SolidColorBrush(Color.FromArgb(170, 0xFF, 0xC8, 0x3D));
                int beatIndex = 0;
                for (double x = offsetPx; x < tlWidth && beatIndex <= 4000; x += periodPx, beatIndex++)
                {
                    var line = new Line
                    {
                        X1 = x, Y1 = 0, X2 = x, Y2 = timelineHeight,
                        Stroke = beatIndex % 4 == 0 ? downbeatBrush : beatBrush,
                        StrokeThickness = beatIndex % 4 == 0 ? 2 : 1
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
                Stroke = (Brush)converter.ConvertFromString("#7B1FA2")!,
                StrokeThickness = 4
            };
            TimelineCanvas.Children.Add(videoLine);

            // Divider line above video
            Line dividerLine = new Line
            {
                X1 = 0, Y1 = vY - 1, X2 = tlWidth, Y2 = vY - 1,
                Stroke = (Brush)converter.ConvertFromString("#E040FB")!,
                StrokeThickness = 2
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
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                Foreground = (Brush)converter.ConvertFromString("#E040FB")!,
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
                    Stroke = (Brush)converter.ConvertFromString("#1DB954")!,
                    StrokeThickness = 2
                };
                TimelineCanvas.Children.Add(vaLine);

                TextBlock vaLbl = new TextBlock
                {
                    Text = "🔊 VIDEO AUDIO",
                    FontWeight = FontWeights.Bold,
                    FontSize = 11,
                    Foreground = (Brush)converter.ConvertFromString("#1DB954")!,
                    Margin = new Thickness(8, vaY + 15, 0, 0),
                    Background = (Brush)converter.ConvertFromString("#14171E")!,
                    Padding = new Thickness(4, 2, 4, 2)
                };
                HeadersSidebar.Children.Add(vaLbl);
            }

            RenderWaveformOverlay();
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
                ListContainer.Children.Add(CreateAssetButton($"🎬 {name}", name));
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

                ListContainer.Children.Add(CreateAssetButton($"🎬 {name}", name));
            }
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

                ListContainer.Children.Add(CreateAssetButton($"🖼️ {name}", name));
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
                        var addedNames = new List<string>();
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
                                        addedNames.Add(baseName);
                                    }
                                }
                            }
                        }

                        if (addedNames.Count > 0)
                        {
                            // Group this ZIP's sounds under their own header so the
                            // pack stays together as one sorted, easy-to-scan list.
                            ListContainer.Children.Add(new TextBlock
                            {
                                Text = $"📦 {System.IO.Path.GetFileName(path)}",
                                Foreground = (Brush)new BrushConverter().ConvertFromString("#A0A5B5")!,
                                FontWeight = FontWeights.Bold,
                                FontSize = 11,
                                Margin = new Thickness(0, 10, 0, 2)
                            });

                            foreach (var soundName in addedNames.OrderBy(n => n))
                            {
                                var sext = System.IO.Path.GetExtension(soundName).ToLowerInvariant();
                                string icon = (sext == ".mid" || sext == ".midi") ? "🎹" : "🎵";
                                ListContainer.Children.Add(CreateAssetButton($"{icon} {soundName}", soundName, 14));
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
                        ListContainer.Children.Add(CreateAssetButton($"{icon} {name}", name));
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
            string draggingSoundName = assetName;
            int trackId = (int)(clickPoint.Y / 50);

            // VIDEO: Always goes to video track
            if (VideoAssets.ContainsKey(draggingSoundName))
            {
                VideoPath = VideoAssets[draggingSoundName];
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
                FixedBlock vblock = new FixedBlock(this, TimelineCanvas, AudioTrackCount, draggingSoundName, clickPoint.X, Array.Empty<byte>(), "mp4", 280, true);
                TimelineBlocks.Add(vblock);
                RenderStudioGridLanes();
            }
            // IMAGE: Goes to clicked audio track
            else if (ImageAssets.ContainsKey(draggingSoundName))
            {
                if (trackId >= 0 && trackId < AudioTrackCount)
                {
                    FixedBlock iblock = new FixedBlock(this, TimelineCanvas, trackId, draggingSoundName, clickPoint.X, Array.Empty<byte>(), "image", 120, false);
                    TimelineBlocks.Add(iblock);
                }
            }
            // AUDIO: Goes to clicked audio track
            else if (ZipSounds.ContainsKey(draggingSoundName))
            {
                if (trackId >= 0 && trackId < AudioTrackCount)
                {
                    byte[] data = ZipSounds[draggingSoundName];
                    string fileExt = System.IO.Path.GetExtension(draggingSoundName).Replace(".", "").ToLower();
                    FixedBlock ablock = new FixedBlock(this, TimelineCanvas, trackId, draggingSoundName, clickPoint.X, data, fileExt);
                    TimelineBlocks.Add(ablock);
                }
            }
            draggingSoundName = null;
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
                new("Output Window", "View", "Ctrl+Shift+O", () => BtnOutput_Click(this, ev)),
                new("Monitor FX", "View", "Ctrl+Shift+M", () => BtnMonitorFx_Click(this, ev)),
                new("Render Queue…", "File", "", () => BtnRenderQueue_Click(this, ev)),
                new("Batch Convert…", "File", "", () => BtnBatchConvert_Click(this, ev)),
                new("Start Screen…", "File", "", () => MenuStartScreen_Click(this, ev)),
                new("About BeatSync", "Help", "F1", () => MenuAbout_Click(this, ev)),
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
            if (SetABtn != null) SetABtn.Content = LoopStartX.HasValue ? $"A · {FmtTime(LoopStartX.Value / 100.0)}" : "Set A";
            if (SetBBtn != null) SetBBtn.Content = LoopEndX.HasValue ? $"B · {FmtTime(LoopEndX.Value / 100.0)}" : "Set B";
            if (LoopToggleBtn != null)
            {
                LoopToggleBtn.Content = LoopEnabled ? "🔁 Loop ON" : "🔁 Loop";
                LoopToggleBtn.Background = new SolidColorBrush(LoopEnabled ? Color.FromRgb(0x1D, 0xB9, 0x54) : Color.FromRgb(0x2A, 0x30, 0x3C));
            }
        }

        private void BtnOutput_Click(object sender, RoutedEventArgs e)
        {
            if (_outputWindow == null)
            {
                _outputWindow = new OutputWindow(this) { Owner = this };
                _outputWindow.Closed += (s, ev) => _outputWindow = null;
                _outputWindow.Show();
            }
            else
            {
                _outputWindow.Activate();
            }
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
                var lanes = audioBlocks.Select(x => (x.LaneIndex, x.StartX / 100.0, x.LengthSeconds)).ToList();
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
                    : (double.IsNaN(block.Width) ? 0 : block.Width / 100.0);
                var clip = new ExportClip
                {
                    RawBytes = block.RawBytes,
                    SoundType = block.SoundType,
                    StartSeconds = block.StartX / 100.0,
                    Speed = block.Speed,
                    SourceStartSeconds = block.SourceStartSeconds,
                    LengthSeconds = block.LengthSeconds,
                    Volume = block.Volume * MasterVolume,
                    ChainIndex = chain,
                    Reverse = block.Reverse,
                    Normalize = block.Normalize,
                    NudgeMs = block.NudgeMs
                };
                if (fadeByBlock.TryGetValue(block, out var fades))
                {
                    clip.FadeInSeconds = fades.Fin;
                    clip.FadeOutSeconds = fades.Fout;
                }
                job.Clips.Add(clip);
                total = Math.Max(total, block.StartX / 100.0 + length);
            }

            // The video's own soundtrack joins the mix when it has not been
            // separated onto its lane (when separated, that block is already in).
            var videoBlock = TimelineBlocks.FirstOrDefault(b => b.IsVideo);
            if (VideoPath != null && videoBlock != null && !VideoAudioSeparated)
            {
                double vlen = videoBlock.LengthSeconds > 0 ? videoBlock.LengthSeconds
                    : (double.IsNaN(videoBlock.Width) ? 0 : videoBlock.Width / 100.0);
                if (vlen > 0)
                {
                    job.Clips.Add(new ExportClip
                    {
                        FilePath = VideoPath,
                        StartSeconds = videoBlock.StartX / 100.0,
                        Speed = VideoSpeed,
                        SourceStartSeconds = 0,
                        LengthSeconds = vlen,
                        Volume = MasterVolume,
                        ChainIndex = AudioTrackCount
                    });
                    total = Math.Max(total, videoBlock.StartX / 100.0 + vlen);
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
                double currentX = ((_playStopwatch?.ElapsedMilliseconds ?? 0) * 100) / 1000.0 - originOffsetX;
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

                if (currentX > GetContentEndX() + 200) break;

                Dispatcher.Invoke(() =>
                {
                    if (didWrap && MasterVideoPlayer?.Source != null && LoopStartX.HasValue)
                    {
                        try { MasterVideoPlayer.Position = TimeSpan.FromSeconds(LoopStartX.Value / 100.0); } catch { }
                    }
                    if (playhead != null)
                    {
                        playhead.X1 = currentX;
                        playhead.X2 = currentX;
                    }
                    if (playheadHandle != null) Canvas.SetLeft(playheadHandle, currentX - 6);
                    UpdateTimeDisplay(currentX / 100.0);

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
                double extraSkip = Math.Max(0, -(block.StartX / 100.0 + shiftSec));
                double audibleLen = block.LengthSeconds > 0
                    ? Math.Max(0.05, block.LengthSeconds - Math.Abs(shiftSec))
                    : Math.Max(0.05, block.FileDurationSeconds / Math.Max(0.01, block.Speed));

                ISampleProvider provider;
                if (block.Reverse || block.Normalize)
                {
                    // Both need the whole segment decoded in memory
                    provider = ClipAudio.BuildSegment(block.RawBytes, block.SoundType, block.Speed,
                        block.SourceStartSeconds, audibleLen, extraSkip, block.Reverse, block.Normalize);
                }
                else
                {
                    IWaveProvider reader = (block.SoundType.ToLower() == "wav")
                        ? new WaveFileReader(new MemoryStream(block.RawBytes))
                        : new Mp3FileReader(new MemoryStream(block.RawBytes));

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
                        var lanes = audioBlocks.Select(x => (x.LaneIndex, x.StartX / 100.0, x.LengthSeconds)).ToList();
                        var (fin, fout) = ClipAudio.ComputeFades(lanes, idx);
                        if (fin > 0.005 || fout > 0.005)
                            provider = new AutoFadeSampleProvider(provider, fin, fout, audibleLen);
                    }
                }

                var volumeSampleProvider = new VolumeSampleProvider(provider) { Volume = (float)(block.Volume * MasterVolume) };

                // Route through the clip's track chain (track volume + pan live there)
                int chain = block.IsVideoAudio ? AudioTrackCount : Math.Clamp(block.TrackId, 0, AudioTrackCount - 1);
                var target = chain >= 0 && chain < _trackMixers.Count ? _trackMixers[chain] : mixerEngine;
                target.AddMixerInput(volumeSampleProvider);
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
                ListContainer.Children.Add(CreateAssetButton($"🎵 {audioName}", audioName));

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
                ListContainer.Children.Add(CreateAssetButton($"🎵 {audioName}", audioName));

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
                var project = new
                {
                    Created = DateTime.UtcNow,
                    Version = "2.0",
                    AudioTrackCount = this.AudioTrackCount,
                    MasterVolume = this.MasterVolume,
                    Bpm = this.ProjectBpm,
                    BeatOffsetSeconds = this.BeatOffsetSeconds,
                    SnapToBeat = this.SnapToBeatEnabled,
                    TrackVolumes = this.TrackVolumeValues,
                    TrackPans = this.TrackPanValues,
                    TrackFx = this.TrackFxValues,
                    Blocks = this.TimelineBlocks.Select(b => new
                    {
                        b.Filename,
                        b.TrackId,
                        b.StartX,
                        b.Width,
                        b.Volume,
                        b.SoundType,
                        b.IsVideo,
                        b.Speed,
                        b.SourceStartSeconds,
                        b.IsVideoAudio,
                        b.LengthSeconds,
                        b.Reverse,
                        b.Normalize,
                        b.NudgeMs
                    }).ToList()
                };

                using (var fs = new FileStream(dlg.FileName, FileMode.Create))
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

                RememberRecentProject(dlg.FileName);
                MessageBox.Show($"✅ Project saved successfully!\n\n📁 {dlg.FileName}", "Save Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"❌ Save failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
                VideoAssets.Clear();
                ImageAssets.Clear();
                ListContainer.Children.Clear();

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
                            ListContainer.Children.Add(CreateAssetButton($"🎵 {entry.Name}", entry.Name));
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
                                ListContainer.Children.Add(CreateAssetButton($"🎬 {entry.Name}", entry.Name));
                            }
                            else
                            {
                                ImageAssets[entry.Name] = dest;
                                ListContainer.Children.Add(CreateAssetButton($"🖼️ {entry.Name}", entry.Name));
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

                            if (isVideo)
                            {
                                var block = new FixedBlock(this, TimelineCanvas, trackId, filename, startX,
                                    Array.Empty<byte>(), soundType, width > 0 ? width : 280, true);
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
            MessageBox.Show("🎬 Video export feature coming soon!\n\nThis will render your entire timeline with all audio & video synchronized.", "Export Video", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void MenuExit_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void MenuAbout_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("BeatSync Studio Pro\n\nCreated by Michael DeCato\n\nYour music mixing editor — multitrack timeline, mixer with EQ/delay/reverb, video, DJ decks and visuals on the way.",
                "About BeatSync Studio Pro", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        protected override void OnClosed(EventArgs e)
        {
            outputDevice?.Dispose();
            base.OnClosed(e);
        }
    }
}
