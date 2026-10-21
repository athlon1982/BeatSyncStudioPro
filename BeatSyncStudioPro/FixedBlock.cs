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
        private const double EdgeGrabWidth = 14.0;
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

        /// <summary>How far into the underlying sound this clip starts (seconds, at speed 1.0) — set by left-edge trims.</summary>
        public double SourceStartSeconds { get; set; }

        /// <summary>Play this clip's audio backwards.</summary>
        public bool Reverse { get; set; }

        /// <summary>Per-clip FX (EQ/delay/reverb) applied before the track chain. Edited via the shared FX window.</summary>
        public TrackFxSettings ClipFx { get; set; } = new TrackFxSettings();

        /// <summary>Volume automation breakpoints (empty = flat).</summary>
        public System.Collections.Generic.List<EnvelopePoint> VolumePoints { get; set; } = new System.Collections.Generic.List<EnvelopePoint>();

        /// <summary>Noise-gate cleanup.</summary>
        public bool GateOn { get; set; }
        public double GateThresholdDb { get; set; } = -45;

        /// <summary>Normalize this clip's audio to about -0.5 dB peak.</summary>
        public bool Normalize { get; set; }

        /// <summary>Shift the audio by this many milliseconds without moving the clip (negative = earlier) — for A/V sync fixes.</summary>
        public int NudgeMs { get; set; }

        /// <summary>Where the clip's audio actually starts on the timeline once the nudge is applied (pixels).</summary>
        public double EffectiveStartX => StartX + NudgeMs * 0.1;

        public string SoundType { get; set; }
        public bool IsVideo { get; set; }

        /// <summary>True for the soundtrack separated from the video: it lives on the lane directly under the video lane.</summary>
        public bool IsVideoAudio { get; set; }

        /// <summary>The lane this clip is drawn on: its audio track, the video lane, or the video-audio lane right under the video.</summary>
        public int LaneIndex => IsVideo ? _owner.AudioTrackCount : IsVideoAudio ? _owner.AudioTrackCount + 1 : TrackId;
        public byte[] RawBytes { get; set; }

        /// <summary>On-timeline length of the clip in seconds (audio: FileDuration / Speed).</summary>
        public double LengthSeconds { get; set; }

        /// <summary>Decoded length of the underlying audio file at speed 1.0 (audio clips only).</summary>
        public double FileDurationSeconds { get; private set; }

        public Border BlockVisual { get; private set; }

        private MenuItem? _videoSoundItem;

        // Drag state
        private enum DragMode { None, Move, StretchRight, StretchLeft }
        private DragMode _dragMode = DragMode.None;
        private double _dragStartCanvasX;
        private double _origStartX;
        private double _origWidth;
        private double _origSourceStart;
        private double _origLength;

        public bool IsAudio => !IsVideo && SoundType != "image";

        /// <param name="width">
        /// Explicit block width in pixels. Pass 0 (or omit) for audio clips and the
        /// width is derived from the decoded audio duration.
        /// </param>
        public FixedBlock(MainWindow owner, Canvas canvas, int trackId, string filename,
            double startX, byte[] rawBytes, string soundType, double width = 0, bool isVideo = false, bool isVideoAudio = false)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));

            TrackId = trackId;
            Filename = filename ?? string.Empty;
            StartX = Math.Max(0, startX);
            RawBytes = rawBytes ?? Array.Empty<byte>();
            SoundType = (soundType ?? string.Empty).ToLowerInvariant();
            IsVideo = isVideo;
            IsVideoAudio = isVideoAudio;

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
                // Full length — a dropped song is the whole song. The timeline
                // grows and scrolls to fit (see MainWindow's timeline width),
                // and the clip can still be trimmed from either edge.
                LengthSeconds = duration;
                Width = Math.Max(20, LengthSeconds * PixelsPerSecond);
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
            double oldSpeed = Speed;
            Speed = Math.Clamp(speed, MinSpeed, MaxSpeed);
            // Speeding up shrinks the clip on the timeline, slowing down stretches
            // it — but never past the end of the underlying sound.
            double maxLen = (FileDurationSeconds - SourceStartSeconds) / Speed;
            LengthSeconds = Math.Min(LengthSeconds * oldSpeed / Speed, maxLen);
            if (LengthSeconds < MinClipSeconds) LengthSeconds = MinClipSeconds;
            Width = LengthSeconds * PixelsPerSecond;
            UpdateCanvasPosition();
        }

        /// <summary>
        /// Resize the clip's window on the timeline (a trim). Speed is untouched;
        /// the clip can never extend past the end of the underlying sound.
        /// </summary>
        public void SetLengthSeconds(double seconds)
        {
            if (IsAudio && FileDurationSeconds > 0)
            {
                double maxLen = (FileDurationSeconds - SourceStartSeconds) / Speed;
                LengthSeconds = Math.Clamp(seconds, MinClipSeconds, Math.Max(MinClipSeconds, maxLen));
            }
            else
            {
                LengthSeconds = Math.Max(MinClipSeconds, seconds);
            }
            Width = LengthSeconds * PixelsPerSecond;
            UpdateCanvasPosition();
        }

        /// <summary>Trim (or un-trim) the front of the clip, computed from the drag-start snapshot.</summary>
        public void TrimLeftFromSnapshot(double deltaSeconds)
        {
            if (!IsAudio || FileDurationSeconds <= 0) return;
            double minDelta = -_origSourceStart / Speed;
            double maxDelta = _origLength - MinClipSeconds;
            double d = Math.Clamp(deltaSeconds, minDelta, maxDelta);
            SourceStartSeconds = _origSourceStart + d * Speed;
            LengthSeconds = _origLength - d;
            StartX = _origStartX + d * PixelsPerSecond;
            Width = LengthSeconds * PixelsPerSecond;
            UpdateCanvasPosition();
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
                Margin = new Thickness(9, 0, 9, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var grid = new Grid();
            grid.Children.Add(label);

            // Visible grip on the right edge — drag it to trim/stretch the end of the clip
            grid.Children.Add(new Border
            {
                Width = 5,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(border),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 3, 2, 3)
            });

            // Audio clips get a left grip too — drag it to trim the front
            if (IsAudio)
            {
                grid.Children.Add(new Border
                {
                    Width = 5,
                    CornerRadius = new CornerRadius(2),
                    Background = new SolidColorBrush(border),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(2, 3, 0, 3)
                });
            }

            var visual = new Border
            {
                Background = new SolidColorBrush(bg),
                BorderBrush = new SolidColorBrush(border),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Width = Width,
                Height = BlockHeight,
                Child = grid
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
            bool onRightEdge = posInBlock.X >= BlockVisual.ActualWidth - EdgeGrabWidth;
            bool onLeftEdge = IsAudio && posInBlock.X <= EdgeGrabWidth;
            _dragMode = onRightEdge ? DragMode.StretchRight
                : onLeftEdge ? DragMode.StretchLeft
                : DragMode.Move;
            _dragStartCanvasX = e.GetPosition(_canvas).X;
            _origStartX = StartX;
            _origWidth = Width;
            _origSourceStart = SourceStartSeconds;
            _origLength = LengthSeconds;
            BlockVisual.CaptureMouse();
            e.Handled = true;
        }

        private void Visual_MouseMove(object sender, MouseEventArgs e)
        {
            if (_dragMode == DragMode.None)
            {
                // Show the trim cursor near either edge (the left edge only trims audio).
                Point posInBlock = e.GetPosition(BlockVisual);
                bool onEdge = posInBlock.X >= BlockVisual.ActualWidth - EdgeGrabWidth
                    || (IsAudio && posInBlock.X <= EdgeGrabWidth);
                BlockVisual.Cursor = onEdge ? Cursors.SizeWE : Cursors.Arrow;
                return;
            }

            if (e.LeftButton != MouseButtonState.Pressed) return;

            Point canvasPos = e.GetPosition(_canvas);
            double delta = canvasPos.X - _dragStartCanvasX;

            if (_dragMode == DragMode.Move)
            {
                StartX = Math.Clamp(_origStartX + delta, 0, Math.Max(0, _canvas.Width - Width));
                StartX = _owner.SnapX(StartX);
                if (IsAudio && !IsVideoAudio)
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
            else if (_dragMode == DragMode.StretchLeft)
            {
                TrimLeftFromSnapshot(delta / PixelsPerSecond);
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
                menu.Items.Add(new MenuItem { Header = "↔ Drag edges to trim • drag middle to move", IsEnabled = false });

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

                var reverseItem = new MenuItem { Header = "🔄 Reverse", IsCheckable = true, IsChecked = Reverse };
                reverseItem.Click += (s, e) => { Reverse = reverseItem.IsChecked; _owner.RefreshAfterBlockEdit(); };
                menu.Items.Add(reverseItem);

                var normalizeItem = new MenuItem { Header = "📈 Normalize (-0.5 dB)", IsCheckable = true, IsChecked = Normalize };
                normalizeItem.Click += (s, e) => { Normalize = normalizeItem.IsChecked; _owner.RefreshAfterBlockEdit(); };
                menu.Items.Add(normalizeItem);

                var nudgeItem = new MenuItem { Header = "⏱️ Nudge audio…" };
                nudgeItem.Click += (s, e) => _owner.OpenNudgeDialog(this);
                menu.Items.Add(nudgeItem);

                var detectItem = new MenuItem { Header = "🥁 Detect beats → set grid" };
                detectItem.Click += (s, e) => _owner.DetectBeatsFromClip(this);
                menu.Items.Add(detectItem);

                var clipFxItem = new MenuItem { Header = "🎛️ Clip FX…" };
                clipFxItem.Click += (s, e) => _owner.OpenClipFxDialog(this);
                menu.Items.Add(clipFxItem);

                var envItem = new MenuItem { Header = "📉 Volume envelope…" };
                envItem.Click += (s, e) => _owner.OpenEnvelopeEditor(this);
                menu.Items.Add(envItem);

                var gateItem = new MenuItem { Header = "🔇 Noise gate…" };
                gateItem.Click += (s, e) => _owner.OpenGateDialog(this);
                menu.Items.Add(gateItem);

                var specItem = new MenuItem { Header = "🌈 Spectrogram…" };
                specItem.Click += (s, e) => _owner.OpenSpectrogram(this);
                menu.Items.Add(specItem);

                menu.Opened += (s, e) =>
                {
                    reverseItem.IsChecked = Reverse;
                    normalizeItem.IsChecked = Normalize;
                    nudgeItem.Header = NudgeMs == 0 ? "⏱️ Nudge audio…" : $"⏱️ Nudge audio… ({NudgeMs:+0;-0} ms)";
                };

                menu.Items.Add(new Separator());

                var resetItem = new MenuItem { Header = "Reset volume & speed" };
                resetItem.Click += (s, e) =>
                {
                    Volume = 1.0;
                    SetSpeed(1.0);
                    Reverse = false;
                    Normalize = false;
                    NudgeMs = 0;
                    ClipFx = new TrackFxSettings();
                    VolumePoints.Clear();
                    GateOn = false;
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

                var vSpdLabel = new TextBlock { Text = $"Video speed: {_owner.VideoSpeed:0.00}x", Width = 110, VerticalAlignment = VerticalAlignment.Center };
                var vSpdSlider = new Slider { Minimum = 0.25, Maximum = 3.0, Value = _owner.VideoSpeed, Width = 150, VerticalAlignment = VerticalAlignment.Center };
                vSpdSlider.ValueChanged += (s, e) =>
                {
                    _owner.SetVideoSpeed(vSpdSlider.Value);
                    vSpdLabel.Text = $"Video speed: {_owner.VideoSpeed:0.00}x";
                };
                var vSpdPanel = new StackPanel { Orientation = Orientation.Horizontal };
                vSpdPanel.Children.Add(vSpdLabel);
                vSpdPanel.Children.Add(vSpdSlider);
                menu.Items.Add(new MenuItem { Header = vSpdPanel, StaysOpenOnClick = true });

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

            double lane = LaneIndex;
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
