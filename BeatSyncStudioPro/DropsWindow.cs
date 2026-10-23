using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace BeatSyncStudioPro
{
    /// <summary>One radio drop assignment: a display name + the audio file it plays.</summary>
    public sealed class DropDef
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
    }

    /// <summary>radio-drops.json persistence (path injectable for tests).</summary>
    public static class DropsStore
    {
        public static string DefaultPath => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BeatSyncStudioPro", "radio-drops.json");

        public static readonly string[] DefaultNames =
        {
            "Station ID", "Rad-Dad Tag", "You're Listening", "Shoutout Time",
            "Weather Hit", "Parade Promo", "Haunt Promo", "Custom Drop"
        };

        public static Dictionary<int, DropDef> Load(string? path = null)
        {
            var map = new Dictionary<int, DropDef>();
            for (int i = 0; i < SamplerEngine.PadCount; i++)
                map[i] = new DropDef { Name = DefaultNames[i], Path = "" };
            try
            {
                string p = path ?? DefaultPath;
                if (File.Exists(p))
                {
                    var saved = JsonSerializer.Deserialize<Dictionary<int, DropDef>>(File.ReadAllText(p));
                    if (saved != null)
                        foreach (var kv in saved)
                            if (kv.Key >= 0 && kv.Key < SamplerEngine.PadCount && kv.Value != null)
                                map[kv.Key] = kv.Value;
                }
            }
            catch { }
            return map;
        }

        public static void Save(Dictionary<int, DropDef> map, string? path = null)
        {
            try
            {
                string p = path ?? DefaultPath;
                var dir = System.IO.Path.GetDirectoryName(p);
                if (dir != null) Directory.CreateDirectory(dir);
                File.WriteAllText(p, JsonSerializer.Serialize(map));
            }
            catch { }
        }
    }

    /// <summary>
    /// Radio Drops board (v3.97): a dedicated window of big pads for
    /// station IDs, tags and promos. Each pad holds any audio file the DJ
    /// assigns (right-click), plays over the mix through its own sampler
    /// bus, and the music auto-ducks while a drop sounds. Separate from
    /// the DJ sampler — different job, different sounds, bigger buttons
    /// for live radio work.
    /// </summary>
    public sealed class DropsWindow : Window
    {
        private readonly SamplerEngine _engine;
        private readonly Dictionary<int, DropDef> _defs;
        // Decoded samples kept per pad: SamplerEngine.StopPad CLEARS the
        // slot, so a stopped drop re-arms itself from this cache.
        private readonly Dictionary<int, float[]> _padSamples = new Dictionary<int, float[]>();
        private readonly Button[] _pads = new Button[SamplerEngine.PadCount];
        private readonly TextBlock _status = new TextBlock();
        private readonly DispatcherTimer _timer;
        private Action<string>? _notify;

        private static readonly Brush PadBase = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x3C));
        private static readonly Brush PadPlaying = new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54));
        private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0xA0, 0xA5, 0xB5));

        public DropsWindow(SamplerEngine engine, Action<string>? notify = null)
        {
            _engine = engine;
            _notify = notify;
            _defs = DropsStore.Load();
            Title = "📻 Radio Drops — Rad-Dad Radio";
            Width = 700;
            Height = 480;
            MinWidth = 560;
            MinHeight = 400;
            Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0E, 0x14));
            Foreground = Brushes.White;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var root = new DockPanel { Margin = new Thickness(14) };
            var header = new TextBlock
            {
                Text = "Big pads for your station IDs, tags and promos. Click a pad to play it — the music dips " +
                       "under it automatically. Right-click a pad to assign a sound (any WAV/MP3 — record your " +
                       "own ID with the mic and save it as a file), rename it, or clear it.",
                Foreground = Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
            };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var footer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            footer.Children.Add(new TextBlock { Text = "Drops volume", Foreground = Muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            var vol = new Slider { Minimum = 0, Maximum = 1.2, Value = _engine.Output.Volume, Width = 160, VerticalAlignment = VerticalAlignment.Center };
            vol.ValueChanged += (s, e) => _engine.Output.Volume = (float)vol.Value;
            footer.Children.Add(vol);
            var stopAll = new Button
            {
                Content = "⏹ Stop All", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(16, 0, 0, 0),
                Background = PadBase, Foreground = Brushes.White, BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            stopAll.Click += (s, e) => { for (int i = 0; i < SamplerEngine.PadCount; i++) { _engine.StopPad(i); Rearm(i); } SetStatus("All drops stopped."); };
            footer.Children.Add(stopAll);
            _status.Foreground = Muted;
            _status.FontSize = 12;
            _status.VerticalAlignment = VerticalAlignment.Center;
            _status.Margin = new Thickness(14, 0, 0, 0);
            _status.TextWrapping = TextWrapping.Wrap;
            footer.Children.Add(_status);
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            var ug = new System.Windows.Controls.Primitives.UniformGrid { Rows = 2, Columns = 4 };
            for (int i = 0; i < SamplerEngine.PadCount; i++)
            {
                int pad = i;
                var b = new Button
                {
                    Margin = new Thickness(5),
                    Background = PadBase,
                    Foreground = Brushes.White,
                    BorderThickness = new Thickness(0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Tag = pad
                };
                b.Click += (s, e) => PadClick(pad);
                b.ContextMenu = BuildPadMenu(pad);
                _pads[i] = b;
                ug.Children.Add(b);
            }
            root.Children.Add(ug);
            Content = root;

            LoadAssignedSounds();
            RefreshPads();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _timer.Tick += (s, e) => RefreshPadColors();
            _timer.Start();
            Closed += (s, e) => _timer.Stop();
        }

        private void LoadAssignedSounds()
        {
            foreach (var kv in _defs)
            {
                if (string.IsNullOrWhiteSpace(kv.Value.Path) || !File.Exists(kv.Value.Path)) continue;
                try
                {
                    var samples = SamplerEngine.DecodeBytes(File.ReadAllBytes(kv.Value.Path), kv.Value.Path);
                    if (samples.Length > 0) { _engine.Slots[kv.Key].Load(samples); _padSamples[kv.Key] = samples; }
                }
                catch { }
            }
        }

        private ContextMenu BuildPadMenu(int pad)
        {
            var menu = new ContextMenu();
            var assign = new MenuItem { Header = "🎵 Assign sound…" };
            assign.Click += (s, e) => Assign(pad);
            menu.Items.Add(assign);
            var rename = new MenuItem { Header = "✏️ Rename…" };
            rename.Click += (s, e) => Rename(pad);
            menu.Items.Add(rename);
            var stop = new MenuItem { Header = "⏹ Stop" };
            stop.Click += (s, e) => { _engine.StopPad(pad); Rearm(pad); };
            menu.Items.Add(stop);
            menu.Items.Add(new Separator());
            var clear = new MenuItem { Header = "🧹 Clear pad" };
            clear.Click += (s, e) =>
            {
                _defs[pad].Path = "";
                _padSamples.Remove(pad);
                _engine.Slots[pad].Clear();
                DropsStore.Save(_defs);
                RefreshPads();
                SetStatus($"Pad {pad + 1} cleared.");
            };
            menu.Items.Add(clear);
            return menu;
        }

        private void PadClick(int pad)
        {
            var slot = _engine.Slots[pad];
            if (slot.IsPlaying) { _engine.StopPad(pad); Rearm(pad); SetStatus($"Stopped '{_defs[pad].Name}'."); return; }
            if (!slot.HasSound && _padSamples.TryGetValue(pad, out var cached)) { slot.Load(cached); }
            if (!slot.HasSound)
            {
                SetStatus($"'{_defs[pad].Name}' has no sound yet — right-click the pad and Assign sound…");
                return;
            }
            _engine.Trigger(pad);
            _notify?.Invoke($"📻 Drop: {_defs[pad].Name}");
            SetStatus($"Playing '{_defs[pad].Name}' — music ducked under it.");
            RefreshPadColors();
        }

        private void Assign(int pad)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Pick the sound for this drop",
                Filter = "Audio files|*.wav;*.mp3;*.flac;*.ogg;*.m4a|All files|*.*"
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                var samples = SamplerEngine.DecodeBytes(File.ReadAllBytes(dlg.FileName), dlg.FileName);
                if (samples.Length == 0) { SetStatus("That file didn't decode to any audio."); return; }
                _engine.Slots[pad].Load(samples);
                _padSamples[pad] = samples;
                _defs[pad].Path = dlg.FileName;
                if (_defs[pad].Name == DropsStore.DefaultNames[pad])
                    _defs[pad].Name = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
                DropsStore.Save(_defs);
                RefreshPads();
                SetStatus($"Pad {pad + 1} now plays '{System.IO.Path.GetFileName(dlg.FileName)}'.");
            }
            catch (Exception ex) { SetStatus("Couldn't load that sound: " + ex.Message); }
        }

        private void Rename(int pad)
        {
            var dlg = new Window
            {
                Title = "Rename drop",
                Owner = this,
                Width = 340,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(Color.FromRgb(0x17, 0x1B, 0x26)),
                Foreground = Brushes.White,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(new TextBlock { Text = "Drop name:", Foreground = Muted, FontSize = 12, Margin = new Thickness(0, 0, 0, 6) });
            var input = new TextBox { Text = _defs[pad].Name, Padding = new Thickness(6, 4, 6, 4) };
            panel.Children.Add(input);
            var ok = new Button
            {
                Content = "Save", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 10, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                Background = new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54)), Foreground = Brushes.White,
                BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand
            };
            ok.Click += (s, e) => { dlg.DialogResult = true; };
            panel.Children.Add(ok);
            dlg.Content = panel;
            input.Loaded += (s, e) => { input.Focus(); input.SelectAll(); };
            if (dlg.ShowDialog() == true)
            {
                string name = input.Text.Trim();
                if (name.Length > 0) _defs[pad].Name = name;
                DropsStore.Save(_defs);
                RefreshPads();
            }
        }

        /// <summary>Re-load a pad's samples after a stop cleared its slot.</summary>
        private void Rearm(int pad)
        {
            if (_padSamples.TryGetValue(pad, out var samples) && samples.Length > 0)
                _engine.Slots[pad].Load(samples);
        }

        private void RefreshPads()
        {
            for (int i = 0; i < SamplerEngine.PadCount; i++)
            {
                var def = _defs[i];
                var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                sp.Children.Add(new TextBlock
                {
                    Text = "📻 " + def.Name, FontSize = 15, FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 150
                });
                sp.Children.Add(new TextBlock
                {
                    Text = _engine.Slots[i].HasSound
                        ? (string.IsNullOrWhiteSpace(def.Path) ? "sound loaded" : System.IO.Path.GetFileName(def.Path))
                        : "right-click: assign sound",
                    FontSize = 10.5, Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 150
                });
                _pads[i].Content = sp;
            }
            RefreshPadColors();
        }

        private void RefreshPadColors()
        {
            for (int i = 0; i < SamplerEngine.PadCount; i++)
                _pads[i].Background = _engine.Slots[i].IsPlaying ? PadPlaying : PadBase;
        }

        private void SetStatus(string text) => _status.Text = text;
    }
}
