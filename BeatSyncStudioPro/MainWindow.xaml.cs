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
        private MixerWindow? _mixerWindow;

        // Pause state for the timeline transport
        private System.Diagnostics.Stopwatch? _playStopwatch;
        private bool _isPaused;
        private Line? playhead;
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
                outputDevice.Init(mixerEngine);
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
            for (int sec = 0; sec <= 25; sec++)
            {
                double xPos = sec * 100;
                Line tickLine = new Line { X1 = xPos, Y1 = 20, X2 = xPos, Y2 = 40, Stroke = (Brush)new BrushConverter().ConvertFromString("#A0A5B5")!, StrokeThickness = 1 };
                RulerTicksCanvas.Children.Add(tickLine);

                TextBlock txtTime = new TextBlock { Text = $"{sec:D2}s", Foreground = (Brush)new BrushConverter().ConvertFromString("#A0A5B5")!, FontWeight = FontWeights.Bold, FontSize = 9 };
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

            playhead = new Line
            {
                X1 = 0, Y1 = 0, X2 = 0, Y2 = timelineHeight,
                Stroke = Brushes.Red,
                StrokeThickness = 2,
                Visibility = Visibility.Collapsed
            };
            TimelineCanvas.Children.Add(playhead);

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
                    X1 = 0, Y1 = yPos, X2 = 2500, Y2 = yPos,
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
                X1 = 0, Y1 = vY, X2 = 2500, Y2 = vY,
                Stroke = (Brush)converter.ConvertFromString("#7B1FA2")!,
                StrokeThickness = 4
            };
            TimelineCanvas.Children.Add(videoLine);

            // Divider line above video
            Line dividerLine = new Line
            {
                X1 = 0, Y1 = vY - 1, X2 = 2500, Y2 = vY - 1,
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
                    X1 = 0, Y1 = vaY, X2 = 2500, Y2 = vaY,
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

            var job = BuildExportJob();
            var progressWindow = new ExportProgressWindow { Owner = this };
            progressWindow.Show();
            try
            {
                await Task.Run(() => ExportRenderer.Render(job, dialog.FileName, progressWindow.Progress, progressWindow.Token));
                progressWindow.Close();
                MessageBox.Show($"Your mix is exported:\n{dialog.FileName}", "💾 Export complete");
            }
            catch (OperationCanceledException)
            {
                progressWindow.Close();
                try { if (System.IO.File.Exists(dialog.FileName)) System.IO.File.Delete(dialog.FileName); } catch { }
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

            double total = 0;
            foreach (var block in TimelineBlocks.Where(b => !b.IsVideo))
            {
                if (block.SoundType == "image" || block.RawBytes == null) continue;
                int chain = block.IsVideoAudio ? AudioTrackCount : Math.Clamp(block.TrackId, 0, AudioTrackCount - 1);
                double length = block.LengthSeconds > 0 ? block.LengthSeconds
                    : (double.IsNaN(block.Width) ? 0 : block.Width / 100.0);
                job.Clips.Add(new ExportClip
                {
                    RawBytes = block.RawBytes,
                    SoundType = block.SoundType,
                    StartSeconds = block.StartX / 100.0,
                    Speed = block.Speed,
                    SourceStartSeconds = block.SourceStartSeconds,
                    LengthSeconds = block.LengthSeconds,
                    Volume = block.Volume * MasterVolume,
                    ChainIndex = chain
                });
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

            Dispatcher.Invoke(() => { if (playhead != null) playhead.Visibility = Visibility.Visible; });

            while (isPlayingTimeline && !token.IsCancellationRequested)
            {
                double currentX = ((_playStopwatch?.ElapsedMilliseconds ?? 0) * 100) / 1000.0;
                if (currentX > 2500) break;

                Dispatcher.Invoke(() =>
                {
                    if (playhead != null)
                    {
                        playhead.X1 = currentX;
                        playhead.X2 = currentX;
                    }
                });

                foreach (var block in TimelineBlocks.Where(b => !b.IsVideo && !playedBlocks.Contains(b) && currentX >= b.StartX))
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
            });
        }

        private void PlayBlockSoundThreaded(FixedBlock block)
        {
            if (mixerEngine == null || block.SoundType == "image") return;
            try
            {
                IWaveProvider reader = (block.SoundType.ToLower() == "wav")
                    ? new WaveFileReader(new MemoryStream(block.RawBytes))
                    : new Mp3FileReader(new MemoryStream(block.RawBytes));

                ISampleProvider provider = new WdlResamplingSampleProvider(reader.ToSampleProvider(), 44100).ToStereo();

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
                        SkipOver = TimeSpan.FromSeconds(block.SourceStartSeconds / block.Speed),
                        Take = TimeSpan.FromSeconds(block.LengthSeconds)
                    };
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
                        b.LengthSeconds
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

                MessageBox.Show($"✅ Project saved successfully!\n\n📁 {dlg.FileName}", "Save Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"❌ Save failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnOpenProject_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "BeatSync Project (*.bsp)|*.bsp", Title = "Open Project" };
            if (dlg.ShowDialog() != true) return;

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

                using (var fs = new FileStream(dlg.FileName, FileMode.Open, FileAccess.Read))
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

                string msg = $"✅ Project loaded!\n\n📁 {dlg.FileName}\n🎞️ {TimelineBlocks.Count} clip(s) on the timeline";
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

        protected override void OnClosed(EventArgs e)
        {
            outputDevice?.Dispose();
            base.OnClosed(e);
        }
    }
}
