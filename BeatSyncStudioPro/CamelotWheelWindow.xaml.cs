using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// The Camelot wheel (v3.28): 24 wedges — outer ring majors (B), inner
    /// ring minors (A). The calling deck's key is lit green, the other
    /// deck's key purple, and every key that mixes harmonically with the
    /// calling deck is tinted so the next pick is obvious at a glance.
    /// </summary>
    public partial class CamelotWheelWindow : Window
    {
        private static readonly Brush BaseFill = new SolidColorBrush(Color.FromRgb(0x23, 0x29, 0x36));
        private static readonly Brush MineFill = new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54));
        private static readonly Brush OtherFill = new SolidColorBrush(Color.FromRgb(0xB3, 0x88, 0xFF));
        private static readonly Brush CompatFill = new SolidColorBrush(Color.FromRgb(0x2E, 0x5E, 0x43));
        private static readonly Brush WedgeStroke = new SolidColorBrush(Color.FromRgb(0x0B, 0x0E, 0x14));

        public CamelotWheelWindow(string deckName, string? mine, string? other)
        {
            InitializeComponent();
            DrawWheel(mine, other);
            string verdict;
            if (mine == null) verdict = $"{deckName}: no key detected yet — load a track and give it a few seconds.";
            else if (other == null) verdict = $"{deckName} is {mine}. Load a track on the other deck to compare keys.";
            else if (mine == other) verdict = $"{deckName} {mine} · other deck {other} — identical key, perfect match.";
            else if (KeyDetector.CamelotCompatible(mine, other)) verdict = $"{deckName} {mine} · other deck {other} — harmonically compatible ✓";
            else verdict = $"{deckName} {mine} · other deck {other} — key clash. Try Key Sync, or pick a green-lit key.";
            LegendText.Text = verdict;
        }

        private void DrawWheel(string? mine, string? other)
        {
            double cx = 168, cy = 168;
            // Camelot number n sits at angle ((n - 8) mod 12) * 30°, so 8 is on top like the real wheel
            for (int n = 1; n <= 12; n++)
            {
                double mid = ((n - 8 + 12) % 12) * 30 - 90;
                AddWedge(cx, cy, 166, 112, mid, n + "B", mine, other);
                AddWedge(cx, cy, 108, 58, mid, n + "A", mine, other);
            }
            var center = new TextBlock
            {
                Text = mine ?? "—",
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                TextAlignment = TextAlignment.Center,
                Width = 64
            };
            Canvas.SetLeft(center, cx - 32);
            Canvas.SetTop(center, cy - 14);
            WheelCanvas.Children.Add(center);
        }

        private void AddWedge(double cx, double cy, double rOuter, double rInner, double midAngle, string code, string? mine, string? other)
        {
            double a0 = midAngle - 14.5, a1 = midAngle + 14.5;
            Brush fill = BaseFill;
            if (code == mine) fill = MineFill;
            else if (code == other) fill = OtherFill;
            else if (mine != null && KeyDetector.CamelotCompatible(mine, code)) fill = CompatFill;

            Point P(double r, double aDeg)
            {
                double a = aDeg * Math.PI / 180;
                return new Point(cx + r * Math.Cos(a), cy + r * Math.Sin(a));
            }
            var fig = new PathFigure { StartPoint = P(rOuter, a0) };
            fig.Segments.Add(new ArcSegment(P(rOuter, a1), new Size(rOuter, rOuter), 0, false, SweepDirection.Clockwise, true));
            fig.Segments.Add(new LineSegment(P(rInner, a1), true));
            fig.Segments.Add(new ArcSegment(P(rInner, a0), new Size(rInner, rInner), 0, false, SweepDirection.Counterclockwise, true));
            fig.IsClosed = true;
            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            WheelCanvas.Children.Add(new Path { Data = geo, Fill = fill, Stroke = WedgeStroke, StrokeThickness = 1 });

            double rMid = (rOuter + rInner) / 2;
            var label = new TextBlock
            {
                Text = code,
                FontSize = 10,
                FontWeight = (code == mine || code == other) ? FontWeights.Bold : FontWeights.Normal,
                Foreground = Brushes.White,
                TextAlignment = TextAlignment.Center,
                Width = 30
            };
            var p = P(rMid, midAngle);
            Canvas.SetLeft(label, p.X - 15);
            Canvas.SetTop(label, p.Y - 7);
            WheelCanvas.Children.Add(label);
        }
    }
}
