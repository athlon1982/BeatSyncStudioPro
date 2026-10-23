using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace BeatSyncStudioPro
{
    /// <summary>Snapshot of one deck for the Event Mode screen.</summary>
    public readonly record struct EventDeckState(string Title, bool Playing, double Pos, double Total, bool Loaded);

    /// <summary>Everything Event Mode needs from the DJ board, as delegates —
    /// the screen itself never touches DJ internals.</summary>
    public sealed class EventHooks
    {
        public required Func<EventDeckState> DeckA { get; init; }
        public required Func<EventDeckState> DeckB { get; init; }
        public required Action PlayPauseA { get; init; }
        public required Action PlayPauseB { get; init; }
        public required Func<int> AudibleDeck { get; init; }   // 0 = A, 1 = B, -1 = neither
        public required Func<double> GetCrossfade { get; init; }
        public required Action<double> SetCrossfade { get; init; }
        public required Action ToggleMic { get; init; }
        public required Func<bool> MicOn { get; init; }
        public required Action NextSong { get; init; }
        public required Func<string> NextUpTitle { get; init; }
        public required Action<int> TriggerDrop { get; init; }
        public required Func<int, bool> DropPlaying { get; init; }
        public required Func<int, string> DropName { get; init; }
    }

    /// <summary>Pure decision logic for Event Mode (unit-tested).</summary>
    public static class EventLogic
    {
        /// <summary>Which deck the big PLAY/PAUSE button should drive:
        /// the audible one; if nothing is audible, the loaded one (A first).</summary>
        public static int PickTransportDeck(int audible, bool aLoaded, bool bLoaded)
        {
            if (audible == 0 || audible == 1) return audible;
            if (aLoaded) return 0;
            if (bLoaded) return 1;
            return -1;
        }

        /// <summary>Which deck NEXT SONG should advance: prefer the audible
        /// deck's queue; fall back to the other deck; -1 when neither has a next.</summary>
        public static int ChooseNextDeck(int audible, bool aHasNext, bool bHasNext)
        {
            if (audible == 0) return aHasNext ? 0 : (bHasNext ? 1 : -1);
            if (audible == 1) return bHasNext ? 1 : (aHasNext ? 0 : -1);
            return aHasNext ? 0 : (bHasNext ? 1 : -1);
        }
    }

    /// <summary>
    /// Event Mode (v3.98): a stripped-down, big-button screen for running
    /// a live event in the dark — the haunt, the parade, a party. Now
    /// Playing, one giant Play/Pause, Next Song, three crossfader jumps,
    /// a giant Mic toggle, the radio drops, and nothing else to
    /// fat-finger. The full DJ board stays open behind it.
    /// </summary>
    public sealed class EventModeWindow : Window
    {
        private readonly EventHooks _h;
        private readonly DispatcherTimer _timer;
        private readonly TextBlock _clock = new TextBlock();
        private readonly TextBlock _nowTitle = new TextBlock();
        private readonly TextBlock _nowMeta = new TextBlock();
        private readonly ProgressBar _progress = new ProgressBar();
        private readonly Button _playBtn = new Button();
        private readonly Button _nextBtn = new Button();
        private readonly TextBlock _nextLabel = new TextBlock();
        private readonly Button _micBtn = new Button();
        private readonly Button[] _dropBtns = new Button[SamplerEngine.PadCount];
        private readonly Button _xfA = new Button();
        private readonly Button _xfBoth = new Button();
        private readonly Button _xfB = new Button();

        private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x07, 0x09, 0x0D));
        private static readonly Brush Panel = new SolidColorBrush(Color.FromRgb(0x1A, 0x1F, 0x29));
        private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54));
        private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
        private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0xA0, 0xA5, 0xB5));
        private static readonly Brush Amber = new SolidColorBrush(Color.FromRgb(0xB9, 0x86, 0x1D));

        public EventModeWindow(EventHooks hooks)
        {
            _h = hooks;
            Title = "🎉 Event Mode — BeatSync Studio Pro";
            WindowState = WindowState.Maximized;
            Background = Bg;
            Foreground = Brushes.White;

            var root = new StackPanel { Margin = new Thickness(26) };

            var top = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
            var heading = new TextBlock { Text = "🎉 EVENT MODE", FontSize = 30, FontWeight = FontWeights.Bold, Foreground = Brushes.White };
            DockPanel.SetDock(heading, Dock.Left);
            top.Children.Add(heading);
            _clock.FontSize = 30;
            _clock.Foreground = Muted;
            _clock.HorizontalAlignment = HorizontalAlignment.Right;
            top.Children.Add(_clock);
            root.Children.Add(top);

            var nowPanel = new Border { Background = Panel, CornerRadius = new CornerRadius(12), Padding = new Thickness(20, 14, 20, 16), Margin = new Thickness(0, 0, 0, 16) };
            var nowStack = new StackPanel();
            nowStack.Children.Add(new TextBlock { Text = "NOW PLAYING", FontSize = 14, Foreground = Muted, FontWeight = FontWeights.Bold });
            _nowTitle.FontSize = 34;
            _nowTitle.FontWeight = FontWeights.Bold;
            _nowTitle.Foreground = Brushes.White;
            _nowTitle.TextTrimming = TextTrimming.CharacterEllipsis;
            _nowTitle.Margin = new Thickness(0, 2, 0, 2);
            nowStack.Children.Add(_nowTitle);
            _nowMeta.FontSize = 16;
            _nowMeta.Foreground = Muted;
            nowStack.Children.Add(_nowMeta);
            _progress.Height = 16;
            _progress.Margin = new Thickness(0, 10, 0, 0);
            _progress.Minimum = 0;
            _progress.Maximum = 1;
            nowStack.Children.Add(_progress);
            nowPanel.Child = nowStack;
            root.Children.Add(nowPanel);

            var transport = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            transport.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            transport.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            transport.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            StyleGiant(_playBtn, Green);
            _playBtn.Click += (s, e) =>
            {
                int deck = EventLogic.PickTransportDeck(_h.AudibleDeck(), _h.DeckA().Loaded, _h.DeckB().Loaded);
                if (deck == 0) _h.PlayPauseA(); else if (deck == 1) _h.PlayPauseB();
                Refresh();
            };
            Grid.SetColumn(_playBtn, 0);
            transport.Children.Add(_playBtn);
            var nextStack = new StackPanel();
            StyleGiant(_nextBtn, Amber);
            _nextBtn.Content = "⏭ NEXT SONG";
            _nextBtn.Click += (s, e) => { _h.NextSong(); Refresh(); };
            nextStack.Children.Add(_nextBtn);
            _nextLabel.FontSize = 15;
            _nextLabel.Foreground = Muted;
            _nextLabel.HorizontalAlignment = HorizontalAlignment.Center;
            _nextLabel.Margin = new Thickness(0, 6, 0, 0);
            _nextLabel.TextTrimming = TextTrimming.CharacterEllipsis;
            nextStack.Children.Add(_nextLabel);
            Grid.SetColumn(nextStack, 2);
            transport.Children.Add(nextStack);
            root.Children.Add(transport);

            var ugx = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 14) };
            StyleMid(_xfA, Panel); _xfA.Content = "◀◀ DECK A";
            _xfA.Click += (s, e) => { _h.SetCrossfade(0); Refresh(); };
            StyleMid(_xfBoth, Panel); _xfBoth.Content = "◀ BOTH ▶";
            _xfBoth.Click += (s, e) => { _h.SetCrossfade(0.5); Refresh(); };
            StyleMid(_xfB, Panel); _xfB.Content = "DECK B ▶▶";
            _xfB.Click += (s, e) => { _h.SetCrossfade(1); Refresh(); };
            ugx.Children.Add(_xfA); ugx.Children.Add(_xfBoth); ugx.Children.Add(_xfB);
            root.Children.Add(ugx);

            StyleGiant(_micBtn, Panel);
            _micBtn.Height = 84;
            _micBtn.FontSize = 26;
            _micBtn.Click += (s, e) => { _h.ToggleMic(); Refresh(); };
            _micBtn.Margin = new Thickness(0, 0, 0, 14);
            root.Children.Add(_micBtn);

            root.Children.Add(new TextBlock { Text = "RADIO DROPS", FontSize = 14, Foreground = Muted, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) });
            var dropsGrid = new System.Windows.Controls.Primitives.UniformGrid { Rows = 2, Columns = 4, Margin = new Thickness(0, 0, 0, 14) };
            for (int i = 0; i < SamplerEngine.PadCount; i++)
            {
                int pad = i;
                var b = new Button
                {
                    Height = 76, Margin = new Thickness(4), FontSize = 15, FontWeight = FontWeights.Bold,
                    Background = Panel, Foreground = Brushes.White, BorderThickness = new Thickness(0),
                    Cursor = System.Windows.Input.Cursors.Hand
                };
                b.Click += (s, e) => { _h.TriggerDrop(pad); Refresh(); };
                _dropBtns[i] = b;
                dropsGrid.Children.Add(b);
            }
            root.Children.Add(dropsGrid);

            var exit = new Button
            {
                Content = "Exit Event Mode (the DJ board is still open behind this)",
                Height = 46, FontSize = 15, Background = Panel, Foreground = Muted,
                BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand
            };
            exit.Click += (s, e) => Close();
            root.Children.Add(exit);

            Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (s, e) => Refresh();
            _timer.Start();
            Closed += (s, e) => _timer.Stop();
            Refresh();
        }

        private static void StyleGiant(Button b, Brush bg)
        {
            b.Height = 110;
            b.FontSize = 30;
            b.FontWeight = FontWeights.Bold;
            b.Background = bg;
            b.Foreground = Brushes.White;
            b.BorderThickness = new Thickness(0);
            b.Cursor = System.Windows.Input.Cursors.Hand;
        }

        private static void StyleMid(Button b, Brush bg)
        {
            b.Height = 62;
            b.FontSize = 19;
            b.FontWeight = FontWeights.Bold;
            b.Margin = new Thickness(4, 0, 4, 0);
            b.Background = bg;
            b.Foreground = Brushes.White;
            b.BorderThickness = new Thickness(0);
            b.Cursor = System.Windows.Input.Cursors.Hand;
        }

        private static string Fmt(double seconds)
        {
            if (seconds < 0) seconds = 0;
            var t = TimeSpan.FromSeconds(seconds);
            return $"{(int)t.TotalMinutes}:{t.Seconds:00}";
        }

        private void Refresh()
        {
            _clock.Text = DateTime.Now.ToString("h:mm tt");
            var a = _h.DeckA();
            var b = _h.DeckB();
            int audible = _h.AudibleDeck();
            EventDeckState cur = audible == 0 ? a : audible == 1 ? b : default;
            if (audible >= 0 && cur.Loaded)
            {
                _nowTitle.Text = string.IsNullOrWhiteSpace(cur.Title) ? "(untitled)" : cur.Title;
                _nowMeta.Text = $"Deck {(audible == 0 ? "A" : "B")} • {Fmt(cur.Pos)} / {Fmt(cur.Total)}" + (cur.Playing ? "" : " • PAUSED");
                _progress.Value = cur.Total > 0 ? Math.Clamp(cur.Pos / cur.Total, 0, 1) : 0;
            }
            else
            {
                _nowTitle.Text = "Nothing playing";
                _nowMeta.Text = "Load songs on the DJ board (or from the Song Library), then run the night from here.";
                _progress.Value = 0;
            }

            int transport = EventLogic.PickTransportDeck(audible, a.Loaded, b.Loaded);
            bool targetPlaying = transport == 0 ? a.Playing : transport == 1 ? b.Playing : false;
            _playBtn.Content = targetPlaying ? "⏸ PAUSE" : "▶ PLAY";
            _playBtn.Background = targetPlaying ? Amber : Green;

            string next = _h.NextUpTitle();
            _nextLabel.Text = string.IsNullOrEmpty(next) ? "queue a playlist on a deck for Next" : "next: " + next;

            bool mic = _h.MicOn();
            _micBtn.Content = mic ? "🎤 MIC: LIVE — tap to mute" : "🎤 MIC: OFF — tap to go live";
            _micBtn.Background = mic ? Red : Panel;

            double xf = _h.GetCrossfade();
            _xfA.Background = xf < 0.25 ? Green : Panel;
            _xfBoth.Background = xf >= 0.25 && xf <= 0.75 ? Green : Panel;
            _xfB.Background = xf > 0.75 ? Green : Panel;

            for (int i = 0; i < SamplerEngine.PadCount; i++)
            {
                _dropBtns[i].Content = "📻 " + _h.DropName(i);
                _dropBtns[i].Background = _h.DropPlaying(i) ? Green : Panel;
            }
        }
    }
}
