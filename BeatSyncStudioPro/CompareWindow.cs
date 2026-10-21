using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// Side-by-side results when the AI Lab's Compare mode runs one prompt
    /// through every connected AI. "Use this one" sends the winner back.
    /// </summary>
    public class CompareWindow : Window
    {
        public class Entry
        {
            public string Provider = "";
            public string Model = "";
            public double Seconds;
            public string Text = "";
        }

        public CompareWindow(List<Entry> entries, Action<string> useResult)
        {
            Title = "🔀 Compare AIs";
            Width = 860; Height = 640;
            MinWidth = 560; MinHeight = 400;
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x2B));
            Foreground = Brushes.White;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            try { Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/app.ico")); } catch { }

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var stack = new StackPanel { Margin = new Thickness(14) };
            stack.Children.Add(new TextBlock
            {
                Text = "Same prompt, every AI. Read them over and send the winner back to the AI Lab.",
                Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA5, 0xB5)),
                FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
            });

            foreach (var e in entries)
            {
                var border = new Border
                {
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x34, 0x46)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12),
                    Margin = new Thickness(0, 0, 0, 12),
                    Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x21, 0x2E))
                };
                var inner = new StackPanel();
                var head = new Grid();
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                string secs = e.Seconds > 0 ? $" · {e.Seconds:0.0}s" : "";
                head.Children.Add(new TextBlock
                {
                    Text = e.Model.Length > 0 ? $"{e.Provider} — {e.Model}{secs}" : e.Provider,
                    FontWeight = FontWeights.Bold, FontSize = 13, Foreground = Brushes.White,
                    TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
                });
                var useBtn = new Button
                {
                    Content = "✓ Use this one",
                    Padding = new Thickness(12, 4, 12, 4),
                    Background = new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54)),
                    Foreground = Brushes.White, FontWeight = FontWeights.Bold,
                    BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand
                };
                string captured = e.Text;
                useBtn.Click += (s, ev) => { useResult(captured); Close(); };
                Grid.SetColumn(useBtn, 1);
                head.Children.Add(useBtn);
                inner.Children.Add(head);
                inner.Children.Add(new TextBox
                {
                    Text = e.Text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                    MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Background = new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x1E)),
                    Foreground = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                    Padding = new Thickness(8), Margin = new Thickness(0, 8, 0, 0), FontSize = 12
                });
                border.Child = inner;
                stack.Children.Add(border);
            }

            scroll.Content = stack;
            Content = scroll;
        }
    }
}
