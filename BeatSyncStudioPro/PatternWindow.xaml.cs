using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// FL-style step sequencer: channels of one-shot sounds (built-in synth
    /// drums or anything in the library) on a 16-step grid. Plays live
    /// through its own little mixer, and renders sample-accurately to a WAV
    /// clip that lands on the timeline.
    /// </summary>
    public partial class PatternWindow : Window
    {
        private readonly MainWindow _owner;
        private PatternData? _current;
        private readonly List<Button[]> _stepButtons = new List<Button[]>();
        private readonly Dictionary<string, float[]> _soundCache = new Dictionary<string, float[]>();

        private MixingSampleProvider? _mixer;
        private WaveOutEvent? _output;
        private Timer? _timer;
        private readonly Stopwatch _clock = new Stopwatch();
        private double _nextStepAt;
        private int _stepCounter;
        private double _stepDur = 0.125;
        private int _highlight = -1;
        private bool _loading;

        private static readonly Brush OnBrush = new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54));
        private static readonly Brush OnDownbeatBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x3D));
        private static readonly Brush OffBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x3C));
        private static readonly Brush OffDownbeatBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x41, 0x52));
        private static readonly Brush HighlightBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
        private static readonly Brush NormalBorder = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x2B));

        public PatternWindow(MainWindow owner)
        {
            InitializeComponent();
            _owner = owner;
            if (owner.Patterns.Count == 0) owner.Patterns.Add(SeedPattern(owner));
            RefreshPatternPicker();
            Closed += (s, e) => StopPlayback();
        }

        private static PatternData SeedPattern(MainWindow owner)
        {
            var p = new PatternData
            {
                Name = "Boom-Rap Groove",
                Bpm = owner.ProjectBpm > 0 ? owner.ProjectBpm : 95
            };
            PatternChannel Ch(string sound, params int[] on)
            {
                var c = new PatternChannel { Sound = sound };
                foreach (var i in on) c.Steps[i] = true;
                return c;
            }
            p.Channels.Add(Ch("builtin:kick", 0, 7, 8));
            p.Channels.Add(Ch("builtin:snare", 4, 12));
            p.Channels.Add(Ch("builtin:hat", 0, 2, 4, 6, 8, 10, 12, 14));
            p.Channels.Add(Ch("builtin:808", 0, 8));
            return p;
        }

        // ---------- pattern / rows UI ----------

        /// <summary>Focus the window on a specific pattern (used by the drum generator).</summary>
        public void SelectPattern(PatternData pattern)
        {
            _current = pattern;
            RefreshPatternPicker();
        }

        private void RefreshPatternPicker()
        {
            _loading = true;
            PatternPicker.Items.Clear();
            foreach (var p in _owner.Patterns) PatternPicker.Items.Add(p.Name);
            int idx = _current != null ? _owner.Patterns.IndexOf(_current) : 0;
            PatternPicker.SelectedIndex = Math.Max(0, idx);
            _current = _owner.Patterns[Math.Max(0, PatternPicker.SelectedIndex)];
            _loading = false;
            BpmBox.Text = _current.Bpm.ToString("0");
            BuildRows();
        }

        private void PatternPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || PatternPicker.SelectedIndex < 0) return;
            ReadBpm();
            _current = _owner.Patterns[PatternPicker.SelectedIndex];
            BpmBox.Text = _current.Bpm.ToString("0");
            BuildRows();
        }

        private void NewPattern_Click(object sender, RoutedEventArgs e)
        {
            ReadBpm();
            var p = new PatternData
            {
                Name = $"Pattern {_owner.Patterns.Count + 1}",
                Bpm = _current?.Bpm ?? 120
            };
            p.Channels.Add(new PatternChannel { Sound = "builtin:kick" });
            p.Channels.Add(new PatternChannel { Sound = "builtin:snare" });
            p.Channels.Add(new PatternChannel { Sound = "builtin:hat" });
            _owner.Patterns.Add(p);
            _current = p;
            RefreshPatternPicker();
        }

        private void BuildRows()
        {
            RowsPanel.Children.Clear();
            _stepButtons.Clear();
            if (_current == null) return;

            for (int ci = 0; ci < _current.Channels.Count; ci++)
            {
                var channel = _current.Channels[ci];
                int channelIndex = ci;
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };

                var picker = new ComboBox { Width = 170, VerticalAlignment = VerticalAlignment.Center };
                foreach (var (key, label) in SynthDrums.BuiltIns)
                    picker.Items.Add(new ComboBoxItem { Content = label, Tag = key });
                foreach (var name in _owner.ZipSounds.Keys.OrderBy(n => n))
                    picker.Items.Add(new ComboBoxItem { Content = "🎵 " + name, Tag = name });
                for (int i = 0; i < picker.Items.Count; i++)
                {
                    if ((picker.Items[i] as ComboBoxItem)?.Tag?.ToString() == channel.Sound)
                    {
                        picker.SelectedIndex = i;
                        break;
                    }
                }
                if (picker.SelectedIndex < 0) picker.SelectedIndex = 0;
                picker.SelectionChanged += (s, ev) =>
                {
                    if (picker.SelectedItem is ComboBoxItem item && item.Tag is string key)
                        channel.Sound = key;
                };
                row.Children.Add(picker);

                var buttons = new Button[16];
                for (int si = 0; si < 16; si++)
                {
                    int stepIndex = si;
                    var btn = new Button
                    {
                        Width = 27,
                        Height = 27,
                        Margin = new Thickness(si % 4 == 0 ? 7 : 2, 0, 0, 0),
                        BorderThickness = new Thickness(1),
                        BorderBrush = NormalBorder,
                        Cursor = System.Windows.Input.Cursors.Hand
                    };
                    btn.Click += (s, ev) =>
                    {
                        channel.Steps[stepIndex] = !channel.Steps[stepIndex];
                        PaintButton(btn, channel.Steps[stepIndex], stepIndex % 4 == 0);
                    };
                    PaintButton(btn, channel.Steps[si], si % 4 == 0);
                    buttons[si] = btn;
                    row.Children.Add(btn);
                }
                _stepButtons.Add(buttons);

                var remove = new Button
                {
                    Content = "✕",
                    Width = 28,
                    Height = 27,
                    Margin = new Thickness(10, 0, 0, 0),
                    Background = OffBrush,
                    Foreground = Brushes.White,
                    BorderThickness = new Thickness(0),
                    Cursor = System.Windows.Input.Cursors.Hand
                };
                remove.Click += (s, ev) =>
                {
                    _current.Channels.RemoveAt(channelIndex);
                    BuildRows();
                };
                row.Children.Add(remove);

                RowsPanel.Children.Add(row);
            }
        }

        private static void PaintButton(Button btn, bool on, bool downbeat)
        {
            btn.Background = on ? (downbeat ? OnDownbeatBrush : OnBrush) : (downbeat ? OffDownbeatBrush : OffBrush);
        }

        private void AddChannel_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            string[] cycle = { "builtin:kick", "builtin:snare", "builtin:hat", "builtin:clap", "builtin:808" };
            _current.Channels.Add(new PatternChannel { Sound = cycle[_current.Channels.Count % cycle.Length] });
            BuildRows();
        }

        // ---------- sounds ----------

        private float[]? GetSound(string key)
        {
            if (_soundCache.TryGetValue(key, out var cached)) return cached;
            float[]? result = null;
            try
            {
                if (key.StartsWith("builtin:"))
                {
                    result = SynthDrums.Get(key);
                }
                else if (_owner.ZipSounds.TryGetValue(key, out var bytes))
                {
                    string ext = Path.GetExtension(key).TrimStart('.').ToLowerInvariant();
                    var (l, r) = StemSeparator.DecodeBytes(bytes, ext.Length > 0 ? ext : "wav");
                    result = new float[l.Length * 2];
                    for (int i = 0; i < l.Length; i++)
                    {
                        result[i * 2] = l[i];
                        result[i * 2 + 1] = r[i];
                    }
                }
            }
            catch { result = null; }
            if (result != null) _soundCache[key] = result;
            return result;
        }

        // ---------- playback ----------

        private void ReadBpm()
        {
            if (_current == null) return;
            if (double.TryParse(BpmBox.Text, out var bpm) && bpm >= 30 && bpm <= 300) _current.Bpm = bpm;
        }

        private void Play_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            ReadBpm();
            EnsureOutput();
            StopTimerOnly();
            _stepDur = 60.0 / _current.Bpm / 4.0;
            _stepCounter = 0;
            _nextStepAt = 0;
            _clock.Restart();
            _timer = new Timer(SchedulerTick, null, 0, 25);
            PlayButton.Content = "▶ Playing…";
            StatusText.Text = $"Playing {_current.Name} at {_current.Bpm:0} BPM — hit Stop, or Render it to the timeline.";
        }

        private void Stop_Click(object sender, RoutedEventArgs e) => StopPlayback();

        private void EnsureOutput()
        {
            if (_output != null) return;
            _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2)) { ReadFully = true };
            _output = new WaveOutEvent();
            _output.Init(_mixer);
            _output.Play();
        }

        private void SchedulerTick(object? state)
        {
            try
            {
                if (_current == null || _mixer == null) return;
                double now = _clock.Elapsed.TotalSeconds;
                while (_nextStepAt < now + 0.12)
                {
                    double delay = Math.Max(0, _nextStepAt - now);
                    int step = _stepCounter % 16;
                    var channels = _current.Channels.ToArray();
                    foreach (var channel in channels)
                    {
                        if (step < channel.Steps.Length && channel.Steps[step])
                        {
                            var samples = GetSound(channel.Sound);
                            if (samples != null)
                            {
                                ISampleProvider src = new ArraySampleProvider(samples);
                                double vel = channel.VelocityAt(step);
                                if (Math.Abs(vel - 1.0) > 0.01)
                                    src = new VolumeSampleProvider(src) { Volume = (float)vel };
                                _mixer.AddMixerInput(new StartDelayedSampleProvider(src, delay));
                            }
                        }
                    }
                    int highlightStep = step;
                    Dispatcher.BeginInvoke(new Action(() => SetHighlight(highlightStep)));
                    _nextStepAt += _stepDur;
                    _stepCounter++;
                }
            }
            catch { }
        }

        private void SetHighlight(int step)
        {
            if (_highlight == step) return;
            foreach (var row in _stepButtons)
            {
                if (_highlight >= 0 && _highlight < row.Length)
                {
                    row[_highlight].BorderBrush = NormalBorder;
                    row[_highlight].BorderThickness = new Thickness(1);
                }
                if (step >= 0 && step < row.Length)
                {
                    row[step].BorderBrush = HighlightBrush;
                    row[step].BorderThickness = new Thickness(2);
                }
            }
            _highlight = step;
        }

        private void StopTimerOnly()
        {
            _timer?.Dispose();
            _timer = null;
            _clock.Stop();
        }

        private void StopPlayback()
        {
            StopTimerOnly();
            _mixer?.RemoveAllMixerInputs();
            if (_output != null)
            {
                try { _output.Stop(); _output.Dispose(); } catch { }
                _output = null;
                _mixer = null;
            }
            Dispatcher.InvokeAsync(() =>
            {
                SetHighlight(-1);
                _highlight = -1;
                if (PlayButton != null) PlayButton.Content = "▶ Play";
            });
        }

        // ---------- render ----------

        private void Render_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            ReadBpm();
            int bars = 4;
            string rawBars = BarsPicker.Text ?? "4";
            string digits = new string(rawBars.Where(char.IsDigit).ToArray());
            if (int.TryParse(digits, out int parsedBars)) bars = Math.Clamp(parsedBars, 1, 999);
            try
            {
                byte[] wav = PatternRenderer.RenderWav(_owner, _current, bars);
                string name = _owner.AddRenderedPatternToTimeline(wav, _current.Name + ".wav");
                StatusText.Text = $"✅ Rendered {bars} bar(s) — '{name}' is on track 1 and in your library.";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Render failed: " + ex.Message;
            }
        }
    }
}
