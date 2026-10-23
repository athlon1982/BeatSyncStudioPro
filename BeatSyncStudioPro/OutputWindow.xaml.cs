using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// The detachable Output stage: a clean window you can throw onto a
    /// second monitor or TV. It mirrors the main window's video (muted —
    /// sound stays in the main app), goes fullscreen, stays on top, and
    /// shows black when idle. Visualizations and the DJ video feed will
    /// plug into this same stage later.
    /// </summary>
    public partial class OutputWindow : Window
    {
        private readonly MainWindow _owner;
        private readonly DispatcherTimer _syncTimer;
        private string? _lastPath;
        private bool _playing;
        private int _mode; // 0 = Video, 1 = Visuals, 2 = Black

        // ---- Visuals engine ----
        private const int VisW = 960, VisH = 540;
        private readonly WriteableBitmap _wbmp = new WriteableBitmap(VisW, VisH, 96, 96, PixelFormats.Bgra32, null);
        private readonly int[] _pixels = new int[VisW * VisH];
        private readonly DispatcherTimer _renderTimer;
        private int _scene = 1; // 1 Bars, 2 Scope, 3 Rings (scene picker 0 = Auto)
        private DateTime _sceneSince = DateTime.Now;
        private double _fadeOpacity;
        private readonly float[] _peakCaps = new float[56];
        private readonly List<(double R, double Alpha, int ColorIdx)> _rings = new List<(double, double, int)>();
        private float _lastBass;
        private DateTime _lastRing = DateTime.MinValue;
        private int _ringFlip;
        private bool _isFullscreen;
        private WindowState _prevState = WindowState.Normal;
        private WindowStyle _prevStyle = WindowStyle.SingleBorderWindow;
        private readonly List<Rect> _displays = new List<Rect>();

        public OutputWindow(MainWindow owner)
        {
            InitializeComponent();
            _owner = owner;
            Topmost = true;

            LoadDisplays();

            _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _syncTimer.Tick += SyncTick;
            _syncTimer.Start();
            Closed += (s, e) => _syncTimer.Stop();

            VisualImage.Source = _wbmp;
            _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _renderTimer.Tick += RenderTick;
            _renderTimer.Start();
            Closed += (s, e) => _renderTimer.Stop();

            // v3.56: projectM (MilkDrop) scene — bundled presets live in a
            // "presets" folder beside the exe; the host starts lazily the
            // first time the scene is picked.
            ProjectMView.InitialPresetFolder = Path.Combine(AppContext.BaseDirectory, "Assets", "presets");
            Closed += (s, e) => { try { ProjectMView.Dispose(); } catch { } };
        }

        // ---------------- Stream overlay (v3.94) ----------------

        /// <summary>The DJ window pushes fresh overlay lines every few
        /// seconds; empty lines hide, and the card hides when blank/off.</summary>
        public void ApplyOverlay(string nowPlaying, string listeners, string latestRequest, bool visible)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => ApplyOverlay(nowPlaying, listeners, latestRequest, visible)); return; }
            OverlayNow.Text = string.IsNullOrWhiteSpace(nowPlaying) ? "" : "🎵 " + nowPlaying;
            OverlayListeners.Text = listeners ?? "";
            OverlayRequest.Text = latestRequest ?? "";
            OverlayNow.Visibility = OverlayNow.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            OverlayListeners.Visibility = OverlayListeners.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            OverlayRequest.Visibility = OverlayRequest.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            OverlayCard.Visibility = visible && (OverlayNow.Text.Length > 0 || OverlayListeners.Text.Length > 0 || OverlayRequest.Text.Length > 0)
                ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---------------- Sync with the main player ----------------

        private void SyncTick(object? sender, EventArgs e)
        {
            if (_mode != 0)
            {
                if (!_noteVisible) IdleLabel.Visibility = Visibility.Collapsed;
                return;
            }

            try
            {
                string? path = _owner.VideoPath;
                if (path != _lastPath)
                {
                    _lastPath = path;
                    OutputPlayer.Source = path != null ? new Uri(path) : null;
                    _playing = false;
                }

                IdleLabel.Visibility = path == null ? Visibility.Visible : Visibility.Collapsed;
                if (path == null) return;

                OutputPlayer.SpeedRatio = _owner.VideoSpeed;

                bool shouldPlay = _owner.OutputShouldPlay;
                if (shouldPlay && !_playing)
                {
                    OutputPlayer.Play();
                    _playing = true;
                }
                else if (!shouldPlay && _playing)
                {
                    OutputPlayer.Pause();
                    _playing = false;
                }

                TimeSpan? mainPos = _owner.VideoPosition;
                if (mainPos.HasValue &&
                    Math.Abs((OutputPlayer.Position - mainPos.Value).TotalSeconds) > 0.4)
                {
                    OutputPlayer.Position = mainPos.Value;
                }
            }
            catch { }
        }

        // ---------------- Controls ----------------

        private void SourcePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ScenePicker == null || VisualImage == null) return; // still initializing
            _mode = SourcePicker.SelectedIndex;
            bool visuals = _mode == 1;
            if (!visuals) DeactivateProjectM();
            SceneLabel.Visibility = visuals ? Visibility.Visible : Visibility.Collapsed;
            ScenePicker.Visibility = visuals ? Visibility.Visible : Visibility.Collapsed;
            VisualImage.Visibility = visuals ? Visibility.Visible : Visibility.Collapsed;

            if (_mode == 0)
            {
                OutputPlayer.Visibility = Visibility.Visible;
            }
            else
            {
                try { OutputPlayer.Pause(); } catch { }
                _playing = false;
                OutputPlayer.Visibility = Visibility.Collapsed;
                IdleLabel.Visibility = Visibility.Collapsed;
                if (visuals) _sceneSince = DateTime.Now;
            }
        }

        private void ScenePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ScenePicker.SelectedIndex > 0)
                SetScene(ScenePicker.SelectedIndex);
            else
                _sceneSince = DateTime.Now; // Auto: restart the cycle clock
        }

        // ---- projectM (scene 4) ----

        private bool _pmFailed;
        private bool _noteVisible;
        private string _pmNameShown = "";
        private DispatcherTimer? _noteTimer;

        private List<int> AvailableScenes()
        {
            var list = new List<int> { 1, 2, 3 };
            if (!_pmFailed && ProjectMNative.DllPresent()) list.Add(4);
            return list;
        }

        private void SetScene(int scene)
        {
            if (scene == 4) { ActivateProjectM(); return; }
            DeactivateProjectM();
            SwitchScene(scene);
        }

        private void ActivateProjectM()
        {
            SwitchScene(4);
            ProjectMView.Tap = _owner.AudioTap;
            ProjectMView.Visibility = Visibility.Visible;
            ProjectMView.RenderEnabled = true;
            VisualImage.Visibility = Visibility.Collapsed;
            FadeImage.Visibility = Visibility.Collapsed;
            PmBar.Visibility = Visibility.Visible;
        }

        private void DeactivateProjectM()
        {
            if (ProjectMView == null) return;
            ProjectMView.RenderEnabled = false;
            ProjectMView.Visibility = Visibility.Collapsed;
            if (PmBar != null) PmBar.Visibility = Visibility.Collapsed;
            if (_mode == 1 && VisualImage != null) VisualImage.Visibility = Visibility.Visible;
        }

        private void PmPrev_Click(object sender, RoutedEventArgs e) => ProjectMView.PrevPreset();
        private void PmNext_Click(object sender, RoutedEventArgs e) => ProjectMView.NextPreset();

        private void PmImport_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Pick a folder of MilkDrop (.milk) presets" };
            if (dlg.ShowDialog(this) == true)
            {
                ProjectMView.AddPresetFolder(dlg.FolderName);
                ProjectMView.NextPreset();
            }
        }

        private void ShowStageNote(string text)
        {
            _noteVisible = true;
            IdleLabel.Text = text;
            IdleLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA5, 0xB5));
            IdleLabel.FontSize = 15;
            IdleLabel.Visibility = Visibility.Visible;
            _noteTimer?.Stop();
            _noteTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _noteTimer.Tick += (s, e2) =>
            {
                _noteTimer?.Stop();
                _noteVisible = false;
                IdleLabel.Visibility = Visibility.Collapsed;
                IdleLabel.Text = "BeatSync Studio Pro";
                IdleLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x2E, 0x34, 0x42));
                IdleLabel.FontSize = 28;
            };
            _noteTimer.Start();
        }

        private void Topmost_Changed(object sender, RoutedEventArgs e)
        {
            Topmost = TopmostCheck.IsChecked == true;
        }

        private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

        private void Stage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) ToggleFullscreen();
        }

        private void OutputWindow_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && _isFullscreen) ToggleFullscreen();
            else if (e.Key == Key.F11) ToggleFullscreen();
        }

        private void ToggleFullscreen()
        {
            if (!_isFullscreen)
            {
                _prevState = WindowState;
                _prevStyle = WindowStyle;
                WindowStyle = WindowStyle.None;
                WindowState = WindowState.Maximized;
                ControlBar.Visibility = Visibility.Collapsed;
                _isFullscreen = true;
            }
            else
            {
                WindowStyle = _prevStyle;
                WindowState = _prevState;
                ControlBar.Visibility = Visibility.Visible;
                _isFullscreen = false;
            }
        }

        // ---------------- Displays ----------------

        private void LoadDisplays()
        {
            _displays.Clear();
            DisplayPicker.Items.Clear();
            try
            {
                var monitors = MonitorHelper.GetMonitors();
                int i = 1;
                foreach (var m in monitors)
                {
                    _displays.Add(m);
                    DisplayPicker.Items.Add($"Display {i} — {m.Width:0}×{m.Height:0}");
                    i++;
                }
            }
            catch { }
            if (DisplayPicker.Items.Count == 0)
            {
                DisplayPicker.Items.Add("Display 1");
                _displays.Add(new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight));
            }
            DisplayPicker.SelectedIndex = 0;
        }

        private void SendToDisplay_Click(object sender, RoutedEventArgs e)
        {
            int idx = DisplayPicker.SelectedIndex;
            if (idx < 0 || idx >= _displays.Count) return;
            var target = _displays[idx];
            if (_isFullscreen) ToggleFullscreen();
            WindowState = WindowState.Normal;
            Left = target.Left + 40;
            Top = target.Top + 40;
        }

        // =========================
        // VISUALS (FFT scenes, crossfaded)
        // =========================

        private void SwitchScene(int scene)
        {
            if (scene == _scene) { _sceneSince = DateTime.Now; return; }
            try
            {
                FadeImage.Source = new WriteableBitmap(_wbmp);
                FadeImage.Opacity = 1;
                FadeImage.Visibility = Visibility.Visible;
                _fadeOpacity = 1;
            }
            catch { }
            _scene = scene;
            _sceneSince = DateTime.Now;
            Array.Clear(_pixels, 0, _pixels.Length);
            _rings.Clear();
        }

        private void RenderTick(object? sender, EventArgs e)
        {
            if (_mode != 1) return;

            if (ScenePicker.SelectedIndex == 0 && (DateTime.Now - _sceneSince).TotalSeconds > 24)
            {
                var avail = AvailableScenes();
                int cur = avail.IndexOf(_scene);
                SetScene(avail[(cur + 1) % avail.Count]);
            }

            if (_scene == 4)
            {
                // projectM renders itself on its own thread; here we only
                // watch for failure (fall back to the FFT scenes) and keep
                // the preset name label current.
                ProjectMView.Tap = _owner.AudioTap;
                if (ProjectMView.Failed && !_pmFailed)
                {
                    _pmFailed = true;
                    PmNameLabel.Text = "";
                    _pmNameShown = "";
                    DeactivateProjectM();
                    SwitchScene(1);
                    if (ScenePicker.SelectedIndex > 0) ScenePicker.SelectedIndex = 1;
                    ShowStageNote("projectM unavailable — " + ProjectMView.FailReason + " Showing Spectrum Bars instead.");
                }
                else if (ProjectMView.Started)
                {
                    string name = ProjectMView.CurrentPresetName;
                    if (name != _pmNameShown) { _pmNameShown = name; PmNameLabel.Text = name; }
                }
                return;
            }

            var tap = _owner.AudioTap;
            if (tap == null) return;

            if (_fadeOpacity > 0)
            {
                _fadeOpacity -= 0.05;
                FadeImage.Opacity = Math.Max(0, _fadeOpacity);
                if (_fadeOpacity <= 0) FadeImage.Visibility = Visibility.Collapsed;
            }

            Decay(0.80f);
            switch (_scene)
            {
                case 2: DrawScope(tap); break;
                case 3: DrawRings(tap); break;
                default: DrawBars(tap); break;
            }
            _wbmp.WritePixels(new Int32Rect(0, 0, VisW, VisH), _pixels, VisW * 4, 0);
        }

        private static int Pack(byte r, byte g, byte b) => (255 << 24) | (r << 16) | (g << 8) | b;
        private static readonly int ColGreen = Pack(0x1D, 0xB9, 0x54);
        private static readonly int ColGold = Pack(0xFF, 0xC8, 0x3D);
        private static readonly int ColPurple = Pack(0xB3, 0x88, 0xFF);
        private static readonly int ColRed = Pack(0xFF, 0x5A, 0x5A);
        private static readonly int ColWhite = Pack(0xF5, 0xF5, 0xF5);

        private void Decay(float keep)
        {
            for (int i = 0; i < _pixels.Length; i++)
            {
                int p = _pixels[i];
                if (p == 0) continue;
                int r = (int)(((p >> 16) & 0xFF) * keep);
                int g = (int)(((p >> 8) & 0xFF) * keep);
                int b = (int)((p & 0xFF) * keep);
                _pixels[i] = (255 << 24) | (r << 16) | (g << 8) | b;
            }
        }

        private void SetPixel(int x, int y, int color)
        {
            if ((uint)x < VisW && (uint)y < VisH) _pixels[y * VisW + x] = color;
        }

        private void FillRect(int x, int y, int w, int h, int color)
        {
            for (int yy = y; yy < y + h; yy++)
                for (int xx = x; xx < x + w; xx++)
                    SetPixel(xx, yy, color);
        }

        private void DrawLine(int x0, int y0, int x1, int y1, int color)
        {
            int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;
            while (true)
            {
                SetPixel(x0, y0, color);
                SetPixel(x0, y0 + 1, color);
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
        }

        private void DrawCircle(int cx, int cy, int radius, int color)
        {
            if (radius <= 0) return;
            int x = radius, y = 0, err = 0;
            while (x >= y)
            {
                SetPixel(cx + x, cy + y, color); SetPixel(cx + y, cy + x, color);
                SetPixel(cx - y, cy + x, color); SetPixel(cx - x, cy + y, color);
                SetPixel(cx - x, cy - y, color); SetPixel(cx - y, cy - x, color);
                SetPixel(cx + y, cy - x, color); SetPixel(cx + x, cy - y, color);
                y++;
                if (err <= 0) err += 2 * y + 1;
                if (err > 0) { x--; err -= 2 * x + 1; }
            }
        }

        private void DrawBars(AudioTapSampleProvider tap)
        {
            const int bars = 56;
            var spectrum = tap.GetSpectrum(bars);
            int bw = VisW / bars;
            for (int i = 0; i < bars; i++)
            {
                float v = spectrum[i];
                int h = (int)(v * (VisH - 40));
                int color = v > 0.85f ? ColRed : v > 0.55f ? ColGold : ColGreen;
                int x = i * bw + 2;
                if (h > 0) FillRect(x, VisH - h, bw - 4, h, color);
                if (v > _peakCaps[i]) _peakCaps[i] = v; else _peakCaps[i] *= 0.97f;
                int capY = VisH - (int)(_peakCaps[i] * (VisH - 40));
                FillRect(x, Math.Max(0, capY - 3), bw - 4, 3, ColWhite);
            }
        }

        private void DrawScope(AudioTapSampleProvider tap)
        {
            var wave = tap.GetWaveform(480);
            int mid = VisH / 2;
            for (int i = 1; i < wave.Length; i++)
            {
                int x0 = (i - 1) * VisW / wave.Length, x1 = i * VisW / wave.Length;
                int y0 = mid - (int)(wave[i - 1] * (VisH * 0.42));
                int y1 = mid - (int)(wave[i] * (VisH * 0.42));
                DrawLine(x0, y0, x1, y1, ColGreen);
                DrawLine(x0, y0 + 130, x1, y1 + 130, ColPurple);
            }
        }

        private void DrawRings(AudioTapSampleProvider tap)
        {
            float bass = tap.BassLevel;
            float level = tap.Level;
            if (bass > _lastBass + 0.06f && bass > 0.25f && (DateTime.Now - _lastRing).TotalMilliseconds > 240)
            {
                _rings.Add((12, 1.0, _ringFlip++ % 2));
                _lastRing = DateTime.Now;
            }
            _lastBass = bass;

            int cx = VisW / 2, cy = VisH / 2;
            for (int i = _rings.Count - 1; i >= 0; i--)
            {
                var ring = _rings[i];
                double r = ring.R + 6;
                double a = ring.Alpha - 0.016;
                if (a <= 0 || r > 760) { _rings.RemoveAt(i); continue; }
                _rings[i] = (r, a, ring.ColorIdx);
                int faded = FadeColor(ring.ColorIdx == 0 ? ColGold : ColGreen, a);
                DrawCircle(cx, cy, (int)r, faded);
                DrawCircle(cx, cy, (int)r + 1, faded);
            }
            int core = 18 + (int)(level * 90);
            for (int r = core; r > 0; r -= 3) DrawCircle(cx, cy, r, ColPurple);
        }

        private static int FadeColor(int color, double alpha)
        {
            int r = (int)(((color >> 16) & 0xFF) * alpha);
            int g = (int)(((color >> 8) & 0xFF) * alpha);
            int b = (int)((color & 0xFF) * alpha);
            return (255 << 24) | (r << 16) | (g << 8) | b;
        }
    }

    /// <summary>Monitor enumeration via Win32 (bounds in WPF units).</summary>
    internal static class MonitorHelper
    {
        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref NativeRect lprcMonitor, IntPtr dwData);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MonitorInfoEx
        {
            public int Size;
            public NativeRect Monitor;
            public NativeRect WorkArea;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        }

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx lpmi);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        public static List<Rect> GetMonitors()
        {
            var results = new List<Rect>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdc, ref NativeRect rect, IntPtr data) =>
            {
                double scale = 1.0;
                try
                {
                    if (GetDpiForMonitor(hMonitor, 0, out uint dpiX, out _) == 0 && dpiX > 0)
                        scale = dpiX / 96.0;
                }
                catch { }

                var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
                var r = rect;
                if (GetMonitorInfo(hMonitor, ref info)) r = info.Monitor;
                results.Add(new Rect(r.Left / scale, r.Top / scale, (r.Right - r.Left) / scale, (r.Bottom - r.Top) / scale));
                return true;
            }, IntPtr.Zero);
            return results;
        }
    }
}
