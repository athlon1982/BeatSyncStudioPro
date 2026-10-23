using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// 🎹 MIDI Controller window (v3.75): pick the controller, switch
    /// MIDI on, and teach the app which note / CC drives what with the
    /// Learn buttons — press Learn, then touch the control on your
    /// hardware. Code-built like the DJ board's other popups. The
    /// controller stays live while this window is open, so you can
    /// wiggle a fader and watch the board move.
    /// </summary>
    public sealed class MidiSettingsWindow : Window
    {
        private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x2B));
        private static readonly Brush PanelBtn = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x3C));
        private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0xA0, 0xA5, 0xB5));
        private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54));

        private readonly DjWindow _dj;
        private readonly ComboBox _deviceBox = new ComboBox();
        private readonly CheckBox _enableBox = new CheckBox();
        private readonly TextBlock _monitor = new TextBlock();
        private readonly TextBlock _status = new TextBlock();
        private readonly Dictionary<string, TextBlock> _bindingTexts = new Dictionary<string, TextBlock>();
        private string? _learningId;
        private bool _loading = true;

        public MidiSettingsWindow(DjWindow dj)
        {
            _dj = dj;
            Title = "MIDI Controller — BeatSync Studio Pro";
            Width = 600;
            Height = 640;
            MinWidth = 520;
            MinHeight = 480;
            Background = Bg;
            Foreground = Brushes.White;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = root;

            var title = new TextBlock { Text = "🎹 MIDI Controller", FontSize = 16, FontWeight = FontWeights.Bold };
            root.Children.Add(title);

            var intro = new TextBlock
            {
                Text = "Plug in any MIDI controller (USB DJ controller, keyboard, pad board). Pick it below, switch MIDI on, then use Learn to teach each row: press Learn, touch the control on your hardware, done. Buttons and pads learn notes; faders and knobs learn CCs.",
                Foreground = Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0)
            };
            Grid.SetRow(intro, 1);
            root.Children.Add(intro);

            // device row
            var devGrid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            devGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            devGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            devGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetRow(devGrid, 2);
            root.Children.Add(devGrid);

            _deviceBox.Background = new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x1E));
            _deviceBox.Foreground = Brushes.White;
            _deviceBox.Padding = new Thickness(6, 4, 6, 4);
            _deviceBox.SelectionChanged += (s, e) => { if (!_loading) ApplyDevice(); };
            devGrid.Children.Add(_deviceBox);

            _enableBox.Content = "MIDI on";
            _enableBox.Foreground = Brushes.White;
            _enableBox.VerticalAlignment = VerticalAlignment.Center;
            _enableBox.Margin = new Thickness(12, 0, 0, 0);
            _enableBox.Checked += (s, e) => { if (!_loading) ApplyDevice(); };
            _enableBox.Unchecked += (s, e) => { if (!_loading) ApplyDevice(); };
            Grid.SetColumn(_enableBox, 1);
            devGrid.Children.Add(_enableBox);

            var refreshBtn = new Button
            {
                Content = "🔄 Re-scan",
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(10, 0, 0, 0),
                Background = PanelBtn, Foreground = Brushes.White, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand
            };
            refreshBtn.Click += (s, e) => ReloadDevices();
            Grid.SetColumn(refreshBtn, 2);
            devGrid.Children.Add(refreshBtn);

            // mapping rows
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 14, 0, 0) };
            Grid.SetRow(scroll, 3);
            root.Children.Add(scroll);
            var rows = new StackPanel();
            scroll.Content = rows;
            foreach (var action in MidiCatalog.Actions)
            {
                var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var label = new TextBlock { Text = action.Label, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
                row.Children.Add(label);

                var bindText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Muted, FontSize = 12 };
                Grid.SetColumn(bindText, 1);
                row.Children.Add(bindText);
                _bindingTexts[action.Id] = bindText;

                string id = action.Id;
                var learnBtn = new Button
                {
                    Content = "Learn",
                    Padding = new Thickness(10, 3, 10, 3),
                    Background = PanelBtn, Foreground = Brushes.White, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand
                };
                learnBtn.Click += (s, e) => StartLearn(id);
                Grid.SetColumn(learnBtn, 2);
                row.Children.Add(learnBtn);

                var clearBtn = new Button
                {
                    Content = "Reset",
                    Padding = new Thickness(10, 3, 10, 3),
                    Margin = new Thickness(6, 0, 0, 0),
                    Background = PanelBtn, Foreground = Brushes.White, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = "Back to the default binding for this row"
                };
                clearBtn.Click += (s, e) => { _dj.SetMidiBinding(id, null); RefreshBindings(); _status.Text = "Reset to the default binding."; };
                Grid.SetColumn(clearBtn, 3);
                row.Children.Add(clearBtn);

                rows.Children.Add(row);
            }

            _monitor.Text = "Controller monitor: no messages yet — move a fader or press a pad.";
            _monitor.Foreground = Muted;
            _monitor.FontSize = 11;
            _monitor.Margin = new Thickness(0, 12, 0, 0);
            Grid.SetRow(_monitor, 4);
            root.Children.Add(_monitor);

            _status.Foreground = Green;
            _status.FontSize = 11;
            _status.TextWrapping = TextWrapping.Wrap;
            _status.Margin = new Thickness(0, 4, 0, 0);
            Grid.SetRow(_status, 5);
            root.Children.Add(_status);

            _dj.MidiActivity += OnMidiActivity;
            _dj.MidiRaw += OnMidiRaw;
            Closed += (s, e) =>
            {
                _dj.MidiActivity -= OnMidiActivity;
                _dj.MidiRaw -= OnMidiRaw;
            };

            ReloadDevices();
            RefreshBindings();
            _loading = false;
        }

        private void ReloadDevices()
        {
            _loading = true;
            var names = MidiEngine.DeviceNames();
            _deviceBox.Items.Clear();
            if (names.Count == 0) _deviceBox.Items.Add("(no MIDI devices connected)");
            else foreach (var n in names) _deviceBox.Items.Add(n);
            var settings = _dj.CurrentMidiSettings();
            _deviceBox.SelectedIndex = names.Count == 0 ? 0 : Math.Min(Math.Max(settings.DeviceIndex, 0), names.Count - 1);
            _deviceBox.IsEnabled = names.Count > 0;
            _enableBox.IsChecked = settings.Enabled && names.Count > 0;
            _enableBox.IsEnabled = names.Count > 0;
            _loading = false;
        }

        private void ApplyDevice()
        {
            var names = MidiEngine.DeviceNames();
            if (names.Count == 0) return;
            int idx = Math.Max(_deviceBox.SelectedIndex, 0);
            bool on = _enableBox.IsChecked == true;
            bool started = _dj.ApplyMidiSettings(idx, on);
            _status.Text = on
                ? (started ? $"Listening to {names[idx]} — try a control." : "Couldn't start that device — try Re-scan, or unplug and replug the controller.")
                : "MIDI is off. Your bindings are saved for next time.";
        }

        private void RefreshBindings()
        {
            var map = _dj.CurrentMidiMap();
            foreach (var action in MidiCatalog.Actions)
            {
                if (_bindingTexts.TryGetValue(action.Id, out var t))
                    t.Text = map.TryGetValue(action.Id, out var b) ? b.Describe() : "—";
            }
        }

        private void StartLearn(string actionId)
        {
            if (MidiEngine.DeviceNames().Count == 0 || _enableBox.IsChecked != true)
            {
                _status.Text = "Learning needs a connected controller with MIDI switched on — pick the device above first.";
                return;
            }
            _learningId = actionId;
            var action = MidiCatalog.Find(actionId);
            _status.Text = $"Learning {action?.Label}… touch the {(action is { Continuous: true } ? "fader or knob" : "button or pad")} on your controller now (any other Learn cancels this one).";
        }

        private void OnMidiActivity(string line) => _monitor.Text = "Controller monitor: " + line;

        private void OnMidiRaw(string kind, int channel, int number, int value)
        {
            if (_learningId == null) return;
            string id = _learningId;
            _learningId = null;
            var action = MidiCatalog.Find(id);
            if (action != null && action.Continuous != (kind == "cc"))
            {
                _status.Text = action.Continuous
                    ? "That was a button — this row needs a fader/knob (a CC). Press Learn and try again."
                    : "That was a fader/knob — this row needs a button or pad (a note). Press Learn and try again.";
                return;
            }
            _dj.SetMidiBinding(id, new MidiBinding { Kind = kind, Number = number, Channel = channel });
            RefreshBindings();
            _status.Text = $"Bound ✓ {action?.Label} → {(kind == "note" ? "Note" : "CC")} {number} · channel {channel}.";
        }
    }
}
