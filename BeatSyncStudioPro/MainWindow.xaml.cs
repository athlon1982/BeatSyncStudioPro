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
        private Line? playhead;
        private CancellationTokenSource? playbackCancelToken;
        private string? draggingSoundName = null;

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
            }
            catch (Exception ex)
            {
                MessageBox.Show($"🔊 Audio Engine Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RenderTimeRulerHeaders()
        {
            if (RulerCanvas == null) return;
            for (int sec = 0; sec <= 25; sec++)
            {
                double xPos = (sec * 100) + 140;
                Line tickLine = new Line { X1 = xPos, Y1 = 20, X2 = xPos, Y2 = 40, Stroke = (Brush)new BrushConverter().ConvertFromString("#A0A5B5")!, StrokeThickness = 1 };
                RulerCanvas.Children.Add(tickLine);

                TextBlock txtTime = new TextBlock { Text = $"{sec:D2}s", Foreground = (Brush)new BrushConverter().ConvertFromString("#A0A5B5")!, FontWeight = FontWeights.Bold, FontSize = 9 };
                Canvas.SetLeft(txtTime, xPos - 12);
                Canvas.SetTop(txtTime, 2);
                RulerCanvas.Children.Add(txtTime);
            }
        }

        private void RenderWaveformOverlay()
        {
            if (TimelineCanvas == null || WaveformOverlayCanvas == null) return;

            WaveformOverlayCanvas.Children.Clear();
            double videoTrackY = AudioTrackCount * 50;

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
                    Canvas.SetTop(waveImg, videoTrackY + 7);
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

            playhead = new Line
            {
                X1 = 0, Y1 = 0, X2 = 0, Y2 = 450,
                Stroke = Brushes.Red,
                StrokeThickness = 2,
                Visibility = Visibility.Collapsed
            };
            TimelineCanvas.Children.Add(playhead);

            foreach (var block in TimelineBlocks)
            {
                if (block.TrackId < AudioTrackCount || block.IsVideo)
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

            RenderWaveformOverlay();
        }

        // =========================
        // IMPORT HANDLERS
        // =========================

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

                Button btn = new Button
                {
                    Content = $"🎬 {name}",
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Background = Brushes.Transparent,
                    Foreground = Brushes.White,
                    BorderThickness = new Thickness(0),
                    Margin = new Thickness(0, 3, 0, 3),
                    Cursor = Cursors.Hand,
                    Padding = new Thickness(4)
                };

                string assetName = name;
                btn.PreviewMouseLeftButtonDown += (s, args) => draggingSoundName = assetName;
                ListContainer.Children.Add(btn);
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

                Button btn = new Button
                {
                    Content = $"🖼️ {name}",
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Background = Brushes.Transparent,
                    Foreground = Brushes.White,
                    BorderThickness = new Thickness(0),
                    Margin = new Thickness(0, 3, 0, 3),
                    Cursor = Cursors.Hand,
                    Padding = new Thickness(4)
                };

                string assetName = name;
                btn.PreviewMouseLeftButtonDown += (s, args) => draggingSoundName = assetName;
                ListContainer.Children.Add(btn);
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

                                        string icon = (eext == ".mid" || eext == ".midi") ? "🎹" : "🎵";
                                        Button btn = new Button
                                        {
                                            Content = $"{icon} {baseName}",
                                            HorizontalContentAlignment = HorizontalAlignment.Left,
                                            Background = Brushes.Transparent,
                                            Foreground = Brushes.White,
                                            BorderThickness = new Thickness(0),
                                            Margin = new Thickness(0, 3, 0, 3),
                                            Cursor = Cursors.Hand,
                                            Padding = new Thickness(4)
                                        };

                                        string soundName = baseName;
                                        btn.PreviewMouseLeftButtonDown += (s, args) => draggingSoundName = soundName;
                                        ListContainer.Children.Add(btn);
                                    }
                                }
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
                        Button btn = new Button
                        {
                            Content = $"{icon} {name}",
                            HorizontalContentAlignment = HorizontalAlignment.Left,
                            Background = Brushes.Transparent,
                            Foreground = Brushes.White,
                            BorderThickness = new Thickness(0),
                            Margin = new Thickness(0, 3, 0, 3),
                            Cursor = Cursors.Hand,
                            Padding = new Thickness(4)
                        };

                        string soundName = name;
                        btn.PreviewMouseLeftButtonDown += (s, args) => draggingSoundName = soundName;
                        ListContainer.Children.Add(btn);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to import {path}: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }

        private void TimelineCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (string.IsNullOrEmpty(draggingSoundName)) return;

            Point clickPoint = e.GetPosition(TimelineCanvas);
            int trackId = (int)(clickPoint.Y / 50);

            // VIDEO: Always goes to video track
            if (VideoAssets.ContainsKey(draggingSoundName))
            {
                VideoPath = VideoAssets[draggingSoundName];
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
            }
        }

        private void BtnRemoveTrack_Click(object sender, RoutedEventArgs e)
        {
            if (AudioTrackCount > 1)
            {
                int targetTrack = AudioTrackCount - 1;
                TimelineBlocks.RemoveAll(b => b.TrackId == targetTrack && !b.IsVideo);
                AudioTrackCount--;
                RenderStudioGridLanes();
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
            if (isPlayingTimeline) return;
            isPlayingTimeline = true;
            playbackCancelToken = new CancellationTokenSource();

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
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            isPlayingTimeline = false;
            playbackCancelToken?.Cancel();
            if (MasterVideoPlayer != null) MasterVideoPlayer.Stop();
        }

        private async Task TimelinePlaybackEngineLoop(CancellationToken token)
        {
            System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
            HashSet<FixedBlock> playedBlocks = new HashSet<FixedBlock>();

            Dispatcher.Invoke(() => { if (playhead != null) playhead.Visibility = Visibility.Visible; });

            while (isPlayingTimeline && !token.IsCancellationRequested)
            {
                double currentX = (stopwatch.ElapsedMilliseconds * 100) / 1000.0;
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

                var resampled = new WdlResamplingSampleProvider(reader.ToSampleProvider(), 44100).ToStereo();
                var volumeSampleProvider = new VolumeSampleProvider(resampled) { Volume = (float)(block.Volume * MasterVolume) };

                mixerEngine.AddMixerInput(volumeSampleProvider);
            }
            catch { }
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
            if (MasterVideoPlayer != null) MasterVideoPlayer.Volume = MasterVolume;
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
                    Blocks = this.TimelineBlocks.Select(b => new
                    {
                        b.Filename,
                        b.TrackId,
                        b.StartX,
                        b.Width,
                        b.Volume,
                        b.SoundType,
                        b.IsVideo,
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
