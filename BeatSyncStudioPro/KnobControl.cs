using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// A rotary knob in the hardware-board style (v3.33): tick ring lit up to
    /// the current value, machined body with a top sheen, accent needle.
    /// Drag vertically (or scroll the wheel) to turn it. Drop-in value
    /// control with the same shape as a Slider: Value / Minimum / Maximum.
    /// </summary>
    public class KnobControl : FrameworkElement
    {
        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
            nameof(Value), typeof(double), typeof(KnobControl),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));
        public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
            nameof(Minimum), typeof(double), typeof(KnobControl), new FrameworkPropertyMetadata(0.0));
        public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
            nameof(Maximum), typeof(double), typeof(KnobControl), new FrameworkPropertyMetadata(1.0));
        public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
            nameof(Accent), typeof(Brush), typeof(KnobControl),
            new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54)), FrameworkPropertyMetadataOptions.AffectsRender));

        public event EventHandler<double>? ValueChanged;

        public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
        public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
        public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
        public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }

        private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var k = (KnobControl)d;
            k.InvalidateVisual();
            k.ValueChanged?.Invoke(k, (double)e.NewValue);
        }

        private bool _dragging;
        private Point _dragStart;
        private double _dragValue;

        public KnobControl()
        {
            Cursor = Cursors.Hand;
            Focusable = true;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            _dragging = true;
            _dragStart = e.GetPosition(this);
            _dragValue = Value;
            CaptureMouse();
            Focus();
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging) return;
            double range = Maximum - Minimum;
            double delta = (_dragStart.Y - e.GetPosition(this).Y) / 140.0 * range;
            Value = Math.Clamp(_dragValue + delta, Minimum, Maximum);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            _dragging = false;
            ReleaseMouseCapture();
            e.Handled = true;
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);
            double range = Maximum - Minimum;
            Value = Math.Clamp(Value + (e.Delta > 0 ? range / 20.0 : -range / 20.0), Minimum, Maximum);
            e.Handled = true;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double size = Math.Min(ActualWidth, ActualHeight);
            if (size <= 8) return;
            var c = new Point(ActualWidth / 2, ActualHeight / 2);
            double r = size / 2 - 11;
            double range = Maximum - Minimum;
            double frac = range > 0 ? Math.Clamp((Value - Minimum) / range, 0, 1) : 0;
            var accent = Accent as SolidColorBrush ?? new SolidColorBrush(Colors.LimeGreen);
            var tickOff = new SolidColorBrush(Color.FromRgb(0x34, 0x3A, 0x4A));
            for (int i = 0; i <= 10; i++)
            {
                double a = (-135 + i * 27) * Math.PI / 180.0;
                var p1 = new Point(c.X + (r + 4) * Math.Sin(a), c.Y - (r + 4) * Math.Cos(a));
                var p2 = new Point(c.X + (r + 9) * Math.Sin(a), c.Y - (r + 9) * Math.Cos(a));
                dc.DrawLine(new Pen(i <= (int)Math.Round(frac * 10) ? (Brush)accent : tickOff, 2.5), p1, p2);
            }
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x23, 0x28, 0x36)),
                new Pen(new SolidColorBrush(Color.FromRgb(0x45, 0x4E, 0x63)), 1.5), c, r, r);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x15, 0x17, 0x20)), null, c, r - 5, r - 5);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF)), null,
                new Point(c.X, c.Y - r * 0.55), r * 0.62, r * 0.26);
            double ang = (-135 + frac * 270) * Math.PI / 180.0;
            var tip = new Point(c.X + (r - 7) * Math.Sin(ang), c.Y - (r - 7) * Math.Cos(ang));
            dc.DrawLine(new Pen(accent, 3.5), c, tip);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x0A, 0x0B, 0x0F)), null, c, 3.5, 3.5);
        }
    }
}
