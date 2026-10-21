using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NAudio.Wave;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// One clip on the multitrack timeline: an audio sample, an image, or the video clip.
    /// Clips can be dragged around the timeline, stretched/shrunk by their right
    /// edge (which changes an audio clip's playback speed so the sound fits the
    /// new length), and right-clicked for per-clip volume / speed controls.
    /// Timeline scale is fixed: 100 pixels = 1 second, lanes are 50px tall.
    /// </summary>
    public class FixedBlock
    {
        public const double PixelsPerSecond = 100.0;
        public const double LaneHeight = 50.0;

        private const double BlockHeight = 40.0;
        private const double LanePadding = 5.0;
        private const double EdgeGrabWidth = 8.0;
        private const double MinClipSeconds = 0.1;
        private const double MinSpeed = 0.1;
        private const double MaxSpeed = 4.0;

        private readonly MainWindow _owner;
        private readonly Canvas _canvas;

        public string Filename { get; set; }
        public int TrackId { get; set; }
        public double StartX { get; set; }
        public double Width { get; set; }
        public double Volume { get; set; } = 1.0;

        /// <summary>Playback speed for audio clips (1.0 = original). Pitch shifts with it.</summary>
        public double Speed { get; set; } = 1.0;

        public string SoundType { get; set; }
        public bool IsVideo { get; set; }
        public byte[] RawBytes { get; set; }

        /// <summary>On-timeline length of the clip in seconds (audio: FileDuration / Speed).</summary>
        public double LengthSeconds { get; set; }

        /// <summary>Decoded length of the underlying audio file at speed 1.0 (audio clips only).</summary>
        public double FileDurationSeconds { get; private set; }

        public Border BlockVisual { get; private set; }

        private MenuItem? _videoSoundItem;

        // Drag state
        private enum DragMode { None, Move, StretchRight }
        private DragMode _dragMode = DragMode.None;
        private double _dragStartCanvasX;
        private double _origStartX;
        private double _origWidth;

        public bool IsAudio => !IsVideo && SoundType != "image";

        /// <param name="width">
        /// Explicit block width in pixels. Pass 0 (or omit) for audio clips and the
        /// width is derived from the decoded audio duration.
        /// </param>
        public FixedBlock(MainWindow owner, Canvas canvas, int trackId, string filename,
            double startX, byte[] rawBytes, string soundType, double width = 0, bool isVideo = false)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));

            TrackId = trackId;
            Filename = filename ?? string.Empty;
            StartX = Math.Max(0, startX);
            RawBytes = rawBytes ?? Array.Empty<byte>();
            SoundType = (soundType ?? string.Empty).ToLowerInvariant();
            IsVideo = isVideo;

            double duration = 0;
            if (IsAudio && RawBytes.Length > 0)
            {
                duration = TryGetAudioDurationSeconds(RawBytes, SoundType);
            }
            FileDurationSeconds = duration;

            if (width > 0)
            {
                Width = width;
                LengthSeconds = duration > 0 ? duration : Width / PixelsPerSecond;
            }
            else if (duration > 0)
            {
                LengthSeconds = duration;
                Width = Math.Max(60, duration * PixelsPerSecond);
            }
            else
            {
                // Unknown / undecodable audio (e.g. MIDI, OGG): give it a nominal 2s block.
                LengthSeconds = 2.0;
                Width = LengthSeconds * PixelsPerSecond;
            }

            BlockVisual = CreateVisual();

            if (!_canvas.Children.Contains(BlockVisual))
            {
                _canvas.Children.Add(BlockVisual);
            }
            UpdateCanvasPosition();
        }

        // =========================
        // SPEED / STRETCH MODEL
        // For audio clips: LengthSeconds = FileDurationSeconds / Speed, always.
        // =========================

        public void SetSpeed(double speed)
        {
            if (!IsAudio || FileDurationSeconds <= 0) return;
            Speed = Math.Clamp(speed, MinSpeed, MaxSpeed);
            LengthSeconds = FileDurationSeconds / Speed;
            Width = LengthSeconds * PixelsPerSecond;
            UpdateCanvasPosition();
        }

        /// <summary>Stretch/shrink the clip to a new on-timeline length; audio speed follows.</summary>
        public void SetLengthSeconds(double seconds)
        {
            seconds = Math.Max(MinClipSeconds, seconds);
            if (IsAudio && FileDurationSeconds > 0)
            {
                SetSpeed(FileDurationSeconds / seconds);
            }
            else
            {
                LengthSeconds = seconds;
                Width = seconds * PixelsPerSecond;
                UpdateCanvasPosition();
            }
        }

        // =========================
        // VISUAL + INTERACTION
        // =========================

        private Border CreateVisual()
        {
            Color bg;
            Color border;
            if (IsVideo)
            {
                bg = Color.FromRgb(0x7B, 0x1F, 0xA2);
                border = Color.FromRgb(0xE0, 0x40, 0xFB);
            }
            else if (SoundType == "image")
            {
                bg = Color.FromRgb(0x2A, 0x30, 0x3C);
                border = Color.FromRgb(0x15, 0x68, 0xDB);
            }
            else
            {
                bg = Color.FromRgb(0x1E, 0x3A, 0x29);
                border = Color.FromRgb(0x1D, 0xB9, 0x54);
            }

            var label = new TextBlock
            {
                Text = Filename,
                Foreground = Brushes.White,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 6, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var visual = new Border
            {
                Background = new SolidColorBrush(bg),
                BorderBrush = new SolidColorBrush(border),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Width = Width,
                Height = BlockHeight,
                Child = label
            };

            visual.MouseLeftButtonDown += Visual_MouseLeftButtonDown;
            visual.MouseMove += Visual_MouseMove;
            visual.MouseLeftButtonUp += Visual_MouseLeftButtonUp;
            visual.ContextMenu = BuildContextMenu();

            return visual;
        }

        private void Visual_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            Point posInBlock = e.GetPosition(BlockVisual);
            _dragMode = posInBlock.X >= BlockVisual.ActualWidth - EdgeGrabWidth
                ? DragMode.StretchRight
                : DragMode.Move;
            _dragStartCanvasX = e.GetPosition(_canvas).X;
            _origStartX = StartX;
            _origWidth = Width;
            BlockVisual.CaptureMouse();
            e.Handled = true;
        }

        private void Visual_MouseMove(object sender, MouseEventArgs e)
        {
            if (_dragMode == DragMode.None)
            {
                // Show the stretch cursor near the right edge.
                Point posInBlock = e.GetPosition(BlockVisual);
                BlockVisual.Cursor = posInBlock.X >= BlockVisual.ActualWidth - EdgeGrabWidth
                    ? Cursors.SizeWE
                    : Cursors.Arrow;
                return;
            }

            if (e.LeftButton != MouseButtonState.Pressed) return;

            Point canvasPos = e.GetPosition(_canvas);
            double delta = canvasPos.X - _dragStartCanvasX;

            if (_dragMode == DragMode.Move)
            {
                StartX = Math.Clamp(_origStartX + delta, 0, Math.Max(0, _canvas.Width - Width));
                if (IsAudio)
                {
                    int lane = (int)(canvasPos.Y / LaneHeight);
                    TrackId = Math.Clamp(lane, 0, Math.Max(0, _owner.AudioTrackCount - 1));
                }
                UpdateCanvasPosition();
            }
            else if (_dragMode == DragMode.StretchRight)
            {
                SetLengthSeconds((_origWidth + delta) / PixelsPerSecond);
            }
            e.Handled = true;
        }

        private void Visual_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_dragMode == DragMode.None) return;
            _dragMode = DragMode.None;
            BlockVisual.ReleaseMouseCapture();
            _owner.RefreshAfterBlockEdit();
            e.Handled = true;
        }

        // =========================
        // RIGHT-CLICK MENU
        // =========================

        private ContextMenu BuildContextMenu()
        {
            var menu = new ContextMenu();

            var header = new MenuItem
            {
                Header = Filename,
                IsEnabled = false,
                FontWeight = FontWeights.Bold
            };
            menu.Items.Add(header);
            menu.Items.Add(new Separator());

            if (IsAudio)
            {
                // Volume slider (0–150%)
                var volLabel = new TextBlock { Text = $"Volume: {(int)(Volume * 100)}%", Width = 90, VerticalAlignment = VerticalAlignment.Center };
                var volSlider = new Slider { Minimum = 0, Maximum = 1.5, Value = Volume, Width = 150, VerticalAlignment = VerticalAlignment.Center };
                volSlider.ValueChanged += (s, e) =>
                {
                    Volume = volSlider.Value;
                    volLabel.Text = $"Volume: {(int)(Volume * 100)}%";
                };
                var volPanel = new StackPanel { Orientation = Orientation.Horizontal };
                volPanel.Children.Add(volLabel);
                volPanel.Children.Add(volSlider);
                menu.Items.Add(new MenuItem { Header = volPanel, StaysOpenOnClick = true });

                // Speed slider (0.25x–3x) — stretches the clip on the timeline too
                var spdLabel = new TextBlock { Text = $"Speed: {Speed:0.00}x", Width = 90, VerticalAlignment = VerticalAlignment.Center };
                var spdSlider = new Slider { Minimum = 0.25, Maximum = 3.0, Value = Speed, Width = 150, VerticalAlignment = VerticalAlignment.Center };
                spdSlider.ValueChanged += (s, e) =>
                {
                    SetSpeed(spdSlider.Value);
                    spdLabel.Text = $"Speed: {Speed:0.00}x";
                };
                var spdPanel = new StackPanel { Orientation = Orientation.Horizontal };
                spdPanel.Children.Add(spdLabel);
                spdPanel.Children.Add(spdSlider);
                var spdItem = new MenuItem { Header = spdPanel, StaysOpenOnClick = true };
                spdItem.SubmenuClosed += (s, e) => _owner.RefreshAfterBlockEdit();
                menu.Items.Add(spdItem);

                menu.Items.Add(new Separator());

                var resetItem = new MenuItem { Header = "Reset volume & speed" };
                resetItem.Click += (s, e) =>
                {
                    Volume = 1.0;
                    SetSpeed(1.0);
                    _owner.RefreshAfterBlockEdit();
                };
                menu.Items.Add(resetItem);
            }

            if (IsVideo)
            {
                var separateItem = new MenuItem { Header = "🔀 Separate audio & video" };
                separateItem.Click += (s, e) => _owner.SeparateAudioFromVideo(this);
                menu.Items.Add(separateItem);

                var extractItem = new MenuItem { Header = "🎵 Extract audio to library only" };
                extractItem.Click += (s, e) => _owner.ExtractAudioFromVideo(this);
                menu.Items.Add(extractItem);

                _videoSoundItem = new MenuItem();
                _videoSoundItem.Click += (s, e) => _owner.SetVideoAudioSeparated(!_owner.VideoAudioSeparated);
                menu.Items.Add(_videoSoundItem);

                menu.Opened += (s, e) =>
                {
                    if (_videoSoundItem != null)
                    {
                        _videoSoundItem.Header = _owner.VideoAudioSeparated
                            ? "🔊 Turn video's own sound back ON"
                            : "🔇 Mute video's own sound";
                    }
                };

                menu.Items.Add(new Separator());
            }

            var removeItem = new MenuItem { Header = "🗑️ Remove clip" };
            removeItem.Click += (s, e) => _owner.RemoveBlock(this);
            menu.Items.Add(removeItem);

            return menu;
        }

        // =========================
        // POSITIONING
        // =========================

        /// <summary>
        /// Positions BlockVisual on the timeline canvas from TrackId / StartX.
        /// The video block always sits on the lane directly under the audio tracks,
        /// so it follows the owner's current AudioTrackCount.
        /// </summary>
        public void UpdateCanvasPosition()
        {
            if (BlockVisual == null) return;

            BlockVisual.Width = Width;
            BlockVisual.Height = BlockHeight;

            double lane = IsVideo ? _owner.AudioTrackCount : TrackId;
            Canvas.SetLeft(BlockVisual, StartX);
            Canvas.SetTop(BlockVisual, lane * LaneHeight + LanePadding);
            Panel.SetZIndex(BlockVisual, 10);
        }

        private static double TryGetAudioDurationSeconds(byte[] bytes, string soundType)
        {
            try
            {
                using var ms = new MemoryStream(bytes);
                using WaveStream reader = soundType == "wav"
                    ? new WaveFileReader(ms)
                    : new Mp3FileReader(ms);
                return reader.TotalTime.TotalSeconds;
            }
            catch
            {
                return 0;
            }
        }
    }
}
