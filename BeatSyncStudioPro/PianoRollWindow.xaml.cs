using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// Piano roll: click notes onto a 2-bar, 2-octave grid (C3–B4), hear them
    /// on synthesized instruments, play the loop live, and render it to a
    /// timeline clip exactly like the step sequencer does.
    /// </summary>
    public partial class PianoRollWindow : Window
    {
        private const int KeyW = 48;
        private const int StepW = 26;
        private const int RowH = 18;
        private const int Steps = 32;
        private const int TopMidi = 71;    // B4
        private const int BottomMidi = 48; // C3

        private readonly MainWindow _owner;
        private PianoPattern? _current;
        private bool _loading;
        private Rectangle? _playheadRect;
        private readonly Dictionary<string, float[]> _noteCache = new Dictionary<string, float[]>();

        private MixingSampleProvider? _mixer;
        private WaveOutEvent? _output;
        private Timer? _timer;
        private readonly Stopwatch _clock = new Stopwatch();
        private double _nextStepAt;
        private int _stepCounter;
        private double _stepDur = 0.13;

        private static readonly string[] NoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        public PianoRollWindow(MainWindow owner)
        {
            InitializeComponent();
            _owner = owner;
            if (owner.PianoPatterns.Count == 0) owner.PianoPatterns.Add(SeedMelody(owner));
            foreach (var (key, label) in SynthTones.Instruments)
                InstrumentPicker.Items.Add(new ComboBoxItem { Content = label, Tag = key });
            RefreshMelodyPicker();
            DrawGrid();
            DrawNotes();
            Closed += (s, e) => StopPlayback();
        }

        private static PianoPattern SeedMelody(MainWindow owner)
        {
            // A simple minor-key bassline riff (A minor) so Play makes music immediately.
            var p = new PianoPattern
            {
                Name = "Melody 1",
                Bpm = owner.ProjectBpm > 0 ? owner.ProjectBpm : 95,
                Instrument = "bass"
            };
            (int Step, int Midi, int Len)[] riff =
            {
                (0, 57, 2), (4, 57, 2), (8, 60, 2), (12, 57, 2),
                (16, 55, 2), (20, 55, 2), (24, 62, 2), (28, 60, 2)
            };
            foreach (var (s, m, l) in riff) p.Notes.Add(new PianoNote { Step = s, Midi = m, LengthSteps = l });
            return p;
        }

        // ---------- grid drawing ----------

        private static bool IsBlackKey(int midi)
        {
            int pc = midi % 12;
            return pc == 1 || pc == 3 || pc == 6 || pc == 8 || pc == 10;
        }

        private static int RowOf(int midi) => TopMidi - midi;
        private static int MidiOfRow(int row) => TopMidi - row;

        private void DrawGrid()
        {
            double width = KeyW + Steps * StepW;
            double height = (TopMidi - BottomMidi + 1) * RowH;
            foreach (var canvas in new[] { GridCanvas, NotesCanvas, ClickCanvas })
            {
                canvas.Width = width;
                canvas.Height = height;
                canvas.HorizontalAlignment = HorizontalAlignment.Left;
                canvas.VerticalAlignment = VerticalAlignment.Top;
            }
            GridCanvas.Children.Clear();

            for (int row = 0; row <= TopMidi - BottomMidi; row++)
            {
                int midi = MidiOfRow(row);
                bool black = IsBlackKey(midi);
                double y = row * RowH;

                // key strip
                var key = new Rectangle
                {
                    Width = KeyW - 2,
                    Height = RowH - 1,
                    Fill = new SolidColorBrush(black ? Color.FromRgb(0x24, 0x27, 0x30) : Color.FromRgb(0xD8, 0xDC, 0xE4))
                };
                Canvas.SetLeft(key, 0);
                Canvas.SetTop(key, y);
                GridCanvas.Children.Add(key);
                if (midi % 12 == 0)
                {
                    var label = new TextBlock
                    {
                        Text = NoteNames[0] + (midi / 12 - 1),
                        FontSize = 9,
                        Foreground = Brushes.Black,
                        FontWeight = FontWeights.Bold
                    };
                    Canvas.SetLeft(label, 4);
                    Canvas.SetTop(label, y + 2);
                    GridCanvas.Children.Add(label);
                }

                // cells
                for (int step = 0; step < Steps; step++)
                {
                    var cell = new Rectangle
                    {
                        Width = StepW - 1,
                        Height = RowH - 1,
                        Fill = new SolidColorBrush(black ? Color.FromRgb(0x11, 0x13, 0x18) : Color.FromRgb(0x17, 0x1B, 0x23))
                    };
                    Canvas.SetLeft(cell, KeyW + step * StepW);
                    Canvas.SetTop(cell, y);
                    GridCanvas.Children.Add(cell);
                }
            }

            // beat / bar lines
            for (int step = 0; step <= Steps; step += 4)
            {
                var line = new Rectangle
                {
                    Width = step % 16 == 0 ? 2 : 1,
                    Height = height,
                    Fill = new SolidColorBrush(Color.FromRgb(0x35, 0x3D, 0x4E))
                };
                Canvas.SetLeft(line, KeyW + step * StepW - 1);
                Canvas.SetTop(line, 0);
                GridCanvas.Children.Add(line);
            }
        }

        private void DrawNotes()
        {
            NotesCanvas.Children.Clear();
            _playheadRect = new Rectangle
            {
                Width = StepW,
                Height = (TopMidi - BottomMidi + 1) * RowH,
                Fill = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)),
                Visibility = Visibility.Collapsed
            };
            Canvas.SetLeft(_playheadRect, KeyW);
            Canvas.SetTop(_playheadRect, 0);
            NotesCanvas.Children.Add(_playheadRect);

            if (_current == null) return;
            foreach (var note in _current.Notes)
            {
                var rect = new Rectangle
                {
                    Width = note.LengthSteps * StepW - 3,
                    Height = RowH - 4,
                    RadiusX = 3,
                    RadiusY = 3,
                    Fill = new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54)),
                    Stroke = new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)),
                    StrokeThickness = 0.75
                };
                Canvas.SetLeft(rect, KeyW + note.Step * StepW + 1);
                Canvas.SetTop(rect, RowOf(note.Midi) * RowH + 2);
                NotesCanvas.Children.Add(rect);
            }
        }

        // ---------- interaction ----------

        private void ClickCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_current == null) return;
            var pos = e.GetPosition(ClickCanvas);
            int row = (int)(pos.Y / RowH);
            if (row < 0 || row > TopMidi - BottomMidi) return;
            int midi = MidiOfRow(row);

            if (pos.X < KeyW)
            {
                PlayOneShot(SynthTones.Note(_current.Instrument, midi, 0.45), 0);
                return;
            }

            int step = (int)((pos.X - KeyW) / StepW);
            if (step < 0 || step >= Steps) return;

            var existing = _current.Notes.FirstOrDefault(n => n.Step == step && n.Midi == midi);
            if (existing != null)
            {
                _current.Notes.Remove(existing);
            }
            else
            {
                int len = SelectedLength();
                _current.Notes.Add(new PianoNote { Step = step, Midi = midi, LengthSteps = len });
                PlayOneShot(GetNote(_current.Instrument, midi, len), 0);
            }
            DrawNotes();
        }

        private int SelectedLength()
        {
            if (LengthPicker.SelectedItem is ComboBoxItem item && item.Tag is string tag && int.TryParse(tag, out var len)) return len;
            return 1;
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            if (MessageBox.Show("Clear all notes in this melody?", "Piano Roll", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            _current.Notes.Clear();
            DrawNotes();
        }

        // ---------- melody management ----------

        private void RefreshMelodyPicker()
        {
            _loading = true;
            MelodyPicker.Items.Clear();
            foreach (var p in _owner.PianoPatterns) MelodyPicker.Items.Add(p.Name);
            int idx = _current != null ? _owner.PianoPatterns.IndexOf(_current) : 0;
            MelodyPicker.SelectedIndex = Math.Max(0, idx);
            _current = _owner.PianoPatterns[Math.Max(0, MelodyPicker.SelectedIndex)];
            _loading = false;
            BpmBox.Text = _current.Bpm.ToString("0");
            for (int i = 0; i < InstrumentPicker.Items.Count; i++)
            {
                if ((InstrumentPicker.Items[i] as ComboBoxItem)?.Tag?.ToString() == _current.Instrument)
                {
                    InstrumentPicker.SelectedIndex = i;
                    break;
                }
            }
        }

        private void MelodyPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || MelodyPicker.SelectedIndex < 0) return;
            ReadBpm();
            _current = _owner.PianoPatterns[MelodyPicker.SelectedIndex];
            BpmBox.Text = _current.Bpm.ToString("0");
            for (int i = 0; i < InstrumentPicker.Items.Count; i++)
            {
                if ((InstrumentPicker.Items[i] as ComboBoxItem)?.Tag?.ToString() == _current.Instrument)
                {
                    InstrumentPicker.SelectedIndex = i;
                    break;
                }
            }
            DrawNotes();
        }

        private void NewMelody_Click(object sender, RoutedEventArgs e)
        {
            ReadBpm();
            var p = new PianoPattern
            {
                Name = $"Melody {_owner.PianoPatterns.Count + 1}",
                Bpm = _current?.Bpm ?? 95,
                Instrument = _current?.Instrument ?? "keys"
            };
            _owner.PianoPatterns.Add(p);
            _current = p;
            RefreshMelodyPicker();
            DrawNotes();
        }

        private void InstrumentPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _current == null) return;
            if (InstrumentPicker.SelectedItem is ComboBoxItem item && item.Tag is string key) _current.Instrument = key;
        }

        // ---------- audio ----------

        private float[] GetNote(string instrument, int midi, int lengthSteps)
        {
            double gate = Math.Max(0.05, lengthSteps * _stepDur);
            string cacheKey = $"{instrument}|{midi}|{gate:0.000}";
            if (_noteCache.TryGetValue(cacheKey, out var cached)) return cached;
            var samples = SynthTones.Note(instrument, midi, gate);
            _noteCache[cacheKey] = samples;
            return samples;
        }

        private void EnsureOutput()
        {
            if (_output != null) return;
            _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2)) { ReadFully = true };
            _output = new WaveOutEvent();
            _output.Init(_mixer);
            _output.Play();
        }

        private void PlayOneShot(float[] samples, double delay)
        {
            EnsureOutput();
            _mixer!.AddMixerInput(new StartDelayedSampleProvider(new ArraySampleProvider(samples), delay));
        }

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
            if (_playheadRect != null) _playheadRect.Visibility = Visibility.Visible;
            StatusText.Text = $"Playing {_current.Name} — 2-bar loop at {_current.Bpm:0} BPM.";
        }

        private void Stop_Click(object sender, RoutedEventArgs e) => StopPlayback();

        private void SchedulerTick(object? state)
        {
            try
            {
                if (_current == null || _mixer == null) return;
                double now = _clock.Elapsed.TotalSeconds;
                while (_nextStepAt < now + 0.12)
                {
                    double delay = Math.Max(0, _nextStepAt - now);
                    int step = _stepCounter % Steps;
                    foreach (var note in _current.Notes.ToArray())
                    {
                        if (note.Step == step)
                        {
                            var samples = GetNote(_current.Instrument, note.Midi, note.LengthSteps);
                            _mixer.AddMixerInput(new StartDelayedSampleProvider(new ArraySampleProvider(samples), delay));
                        }
                    }
                    int highlightStep = step;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_playheadRect != null) Canvas.SetLeft(_playheadRect, KeyW + highlightStep * StepW);
                    }));
                    _nextStepAt += _stepDur;
                    _stepCounter++;
                }
            }
            catch { }
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
                if (_playheadRect != null) _playheadRect.Visibility = Visibility.Collapsed;
                if (PlayButton != null) PlayButton.Content = "▶ Play";
            });
        }

        // ---------- render ----------

        private void Render_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            ReadBpm();
            int loops = 2;
            if (LoopsPicker.SelectedItem is ComboBoxItem item && item.Tag is string tag) int.TryParse(tag, out loops);
            try
            {
                double stepDur = 60.0 / _current.Bpm / 4.0;
                int stepSamples = (int)(stepDur * 44100);
                int totalFrames = stepSamples * Steps * loops;
                var mix = new float[totalFrames * 2];
                for (int loop = 0; loop < loops; loop++)
                {
                    foreach (var note in _current.Notes)
                    {
                        var samples = SynthTones.Note(_current.Instrument, note.Midi, Math.Max(0.05, note.LengthSteps * stepDur));
                        int at = (loop * Steps + note.Step) * stepSamples;
                        int frames = Math.Min(samples.Length / 2, totalFrames - at);
                        for (int i = 0; i < frames * 2; i++) mix[at * 2 + i] += samples[i];
                    }
                }
                double peak = 0;
                foreach (var v in mix) peak = Math.Max(peak, Math.Abs(v));
                if (peak > 0.95)
                {
                    float scale = (float)(0.95 / peak);
                    for (int i = 0; i < mix.Length; i++) mix[i] *= scale;
                }
                string temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "beatsync-piano-" + Guid.NewGuid().ToString("N") + ".wav");
                byte[] wav;
                try
                {
                    WaveFileWriter.CreateWaveFile16(temp, new ArraySampleProvider(mix));
                    wav = File.ReadAllBytes(temp);
                }
                finally
                {
                    try { File.Delete(temp); } catch { }
                }
                string name = _owner.AddRenderedPatternToTimeline(wav, _current.Name + ".wav");
                StatusText.Text = $"✅ Rendered {loops * 2} bars — '{name}' is on track 1 and in your library.";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Render failed: " + ex.Message;
            }
        }
    }
}
