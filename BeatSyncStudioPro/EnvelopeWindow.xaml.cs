using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// Volume-envelope editor: breakpoints on a time/gain canvas.
    /// X = seconds from the clip's audible start, Y = gain 0..1.5.
    /// </summary>
    public partial class EnvelopeWindow : Window
    {
        private const double PadL = 10, PadR = 10, PadT = 12, PadB = 16, MaxGain = 1.5;
        private readonly double _duration;
        private readonly List<EnvelopePoint> _points;
        private int _dragIndex = -1;

        public List<EnvelopePoint> Points => _points.OrderBy(p => p.TimeSeconds).ToList();

        public EnvelopeWindow(string clipName, double durationSeconds, List<EnvelopePoint> existing)
        {
            InitializeComponent();
            _duration = Math.Max(0.1, durationSeconds);
            _points = existing.Select(p => new EnvelopePoint { TimeSeconds = p.TimeSeconds, Gain = p.Gain })
                              .OrderBy(p => p.TimeSeconds).ToList();
            TitleText.Text = $"📉 Volume Envelope — {clipName} ({_duration:0.0}s)";
            Redraw();
        }

        private double XOf(double t) => PadL + t / _duration * (Plot.Width - PadL - PadR);
        private double YOf(double g) => PadT + (MaxGain - g) / MaxGain * (Plot.Height - PadT - PadB);
        private double TimeOf(double x) => Clamp((x - PadL) / (Plot.Width - PadL - PadR) * _duration, 0, _duration);
        private double GainOf(double y) => Clamp(MaxGain - (y - PadT) / (Plot.Height - PadT - PadB) * MaxGain, 0, MaxGain);
        private static double Clamp(double v, double lo, double hi) => Math.Max(lo, Math.Min(hi, v));

        private void Redraw()
        {
            Plot.Children.Clear();

            // 1.0 guide line
            var guide = new Line
            {
                X1 = PadL, X2 = Plot.Width - PadR, Y1 = YOf(1.0), Y2 = YOf(1.0),
                Stroke = new SolidColorBrush(Color.FromRgb(0x44, 0x4C, 0x5E)),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 4 }
            };
            Plot.Children.Add(guide);

            var sorted = _points.OrderBy(p => p.TimeSeconds).ToList();
            if (sorted.Count > 0)
            {
                var line = new Polyline
                {
                    Stroke = new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54)),
                    StrokeThickness = 2
                };
                line.Points.Add(new Point(XOf(0), YOf(sorted[0].Gain)));
                foreach (var p in sorted) line.Points.Add(new Point(XOf(p.TimeSeconds), YOf(p.Gain)));
                line.Points.Add(new Point(XOf(_duration), YOf(sorted[sorted.Count - 1].Gain)));
                Plot.Children.Add(line);

                foreach (var p in sorted)
                {
                    var dot = new Ellipse
                    {
                        Width = 11, Height = 11,
                        Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x3D)),
                        Stroke = Brushes.Black, StrokeThickness = 1
                    };
                    Canvas.SetLeft(dot, XOf(p.TimeSeconds) - 5.5);
                    Canvas.SetTop(dot, YOf(p.Gain) - 5.5);
                    Plot.Children.Add(dot);
                }
            }
        }

        private int HitIndex(Point pos)
        {
            for (int i = 0; i < _points.Count; i++)
            {
                double dx = XOf(_points[i].TimeSeconds) - pos.X;
                double dy = YOf(_points[i].Gain) - pos.Y;
                if (dx * dx + dy * dy <= 100) return i;
            }
            return -1;
        }

        private void Plot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var pos = e.GetPosition(Plot);
            int hit = HitIndex(pos);
            if (hit >= 0)
            {
                _dragIndex = hit;
                Plot.CaptureMouse();
            }
            else
            {
                var p = new EnvelopePoint { TimeSeconds = TimeOf(pos.X), Gain = GainOf(pos.Y) };
                _points.Add(p);
                _points.Sort((a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
                _dragIndex = _points.IndexOf(p);
                Plot.CaptureMouse();
                Redraw();
            }
        }

        private void Plot_MouseMove(object sender, MouseEventArgs e)
        {
            if (_dragIndex < 0 || _dragIndex >= _points.Count) return;
            var pos = e.GetPosition(Plot);
            var sorted = _points.OrderBy(p => p.TimeSeconds).ToList();
            var p = _points[_dragIndex];
            int si = sorted.IndexOf(p);
            double lo = si > 0 ? sorted[si - 1].TimeSeconds + 0.05 : 0;
            double hi = si < sorted.Count - 1 ? sorted[si + 1].TimeSeconds - 0.05 : _duration;
            p.TimeSeconds = Clamp(TimeOf(pos.X), lo, Math.Max(lo, hi));
            p.Gain = GainOf(pos.Y);
            Redraw();
        }

        private void Plot_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _dragIndex = -1;
            Plot.ReleaseMouseCapture();
        }

        private void Plot_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            int hit = HitIndex(e.GetPosition(Plot));
            if (hit >= 0)
            {
                _points.RemoveAt(hit);
                Redraw();
            }
        }

        // ---------- presets ----------

        private void SetPoints(params (double T, double G)[] pts)
        {
            _points.Clear();
            foreach (var (t, g) in pts) _points.Add(new EnvelopePoint { TimeSeconds = t, Gain = g });
            Redraw();
        }

        private void PresetFlat_Click(object sender, RoutedEventArgs e) => SetPoints();
        private void PresetFadeIn_Click(object sender, RoutedEventArgs e) => SetPoints((0, 0), (_duration * 0.35, 1.0));
        private void PresetFadeOut_Click(object sender, RoutedEventArgs e) => SetPoints((_duration * 0.65, 1.0), (_duration, 0));
        private void PresetDuck_Click(object sender, RoutedEventArgs e) =>
            SetPoints((0, 1.0), (_duration * 0.38, 1.0), (_duration * 0.46, 0.3), (_duration * 0.56, 0.3), (_duration * 0.64, 1.0), (_duration, 1.0));
        private void PresetSwell_Click(object sender, RoutedEventArgs e) => SetPoints((0, 0), (_duration, 1.25));

        private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
