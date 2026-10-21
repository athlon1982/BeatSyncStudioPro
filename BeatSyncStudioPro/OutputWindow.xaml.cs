using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
        private bool _blackout;
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
        }

        // ---------------- Sync with the main player ----------------

        private void SyncTick(object? sender, EventArgs e)
        {
            if (_blackout)
            {
                IdleLabel.Visibility = Visibility.Collapsed;
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
            _blackout = SourcePicker.SelectedIndex == 1;
            if (_blackout)
            {
                try { OutputPlayer.Pause(); } catch { }
                _playing = false;
                OutputPlayer.Visibility = Visibility.Collapsed;
                IdleLabel.Visibility = Visibility.Collapsed;
            }
            else
            {
                OutputPlayer.Visibility = Visibility.Visible;
            }
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
