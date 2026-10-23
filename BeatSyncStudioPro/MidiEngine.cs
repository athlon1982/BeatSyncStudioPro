using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using NAudio.Midi;

namespace BeatSyncStudioPro
{
    /// <summary>One MIDI control bound to an app action (v3.75).</summary>
    public sealed class MidiBinding
    {
        public string Kind { get; set; } = "";      // "note" (buttons/pads) or "cc" (faders/knobs)
        public int Number { get; set; }             // note number or CC number
        public int Channel { get; set; }            // 1-16; 0 = any channel

        public string Describe()
        {
            if (Kind.Length == 0) return "—";
            string core = Kind == "note" ? $"Note {Number}" : $"CC {Number}";
            return Channel > 0 ? $"{core} · ch {Channel}" : core;
        }
    }

    public sealed class MidiActionInfo
    {
        public string Id = "";
        public string Label = "";
        public bool Continuous;                     // true = CC value drives it; false = note press fires it
        public MidiBinding? DefaultBinding;
    }

    /// <summary>The fixed catalog of things a controller can drive.</summary>
    public static class MidiCatalog
    {
        public static readonly List<MidiActionInfo> Actions = Build();

        private static List<MidiActionInfo> Build()
        {
            var list = new List<MidiActionInfo>
            {
                new() { Id = "deckA.play", Label = "Deck A — Play / Pause", DefaultBinding = new MidiBinding { Kind = "note", Number = 36 } },
                new() { Id = "deckA.cue", Label = "Deck A — Cue", DefaultBinding = new MidiBinding { Kind = "note", Number = 37 } },
                new() { Id = "deckB.play", Label = "Deck B — Play / Pause", DefaultBinding = new MidiBinding { Kind = "note", Number = 38 } },
                new() { Id = "deckB.cue", Label = "Deck B — Cue", DefaultBinding = new MidiBinding { Kind = "note", Number = 39 } },
                new() { Id = "mix.crossfader", Label = "Crossfader", Continuous = true, DefaultBinding = new MidiBinding { Kind = "cc", Number = 31 } },
                new() { Id = "mix.faderA", Label = "Deck A — Channel Fader", Continuous = true, DefaultBinding = new MidiBinding { Kind = "cc", Number = 17 } },
                new() { Id = "mix.faderB", Label = "Deck B — Channel Fader", Continuous = true, DefaultBinding = new MidiBinding { Kind = "cc", Number = 18 } },
            };
            for (int i = 0; i < 8; i++)
            {
                int n = i + 1;
                list.Add(new MidiActionInfo
                {
                    Id = $"pad.{n}",
                    Label = $"Sampler Pad {n}",
                    DefaultBinding = new MidiBinding { Kind = "note", Number = 39 + n }
                });
            }
            return list;
        }

        public static MidiActionInfo? Find(string id)
        {
            foreach (var a in Actions) if (a.Id == id) return a;
            return null;
        }
    }

    /// <summary>Persistence for MIDI: the user's learned bindings
    /// (midi-mappings.json) and the chosen device (midi-settings.json),
    /// both under %AppData%/BeatSyncStudioPro.</summary>
    public static class MidiStore
    {
        private static string Dir => SongLibraryStore.AppDir;
        private static string MapPath => Path.Combine(Dir, "midi-mappings.json");
        private static string SettingsPath => Path.Combine(Dir, "midi-settings.json");

        public sealed class Settings
        {
            public int DeviceIndex { get; set; } = -1;
            public bool Enabled { get; set; }
        }

        public static Settings LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsPath))
                    return JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) ?? new Settings();
            }
            catch { }
            return new Settings();
        }

        public static void SaveSettings(Settings s)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s));
            }
            catch { }
        }

        public static Dictionary<string, MidiBinding> LoadOverrides()
        {
            try
            {
                if (File.Exists(MapPath))
                    return JsonSerializer.Deserialize<Dictionary<string, MidiBinding>>(File.ReadAllText(MapPath))
                        ?? new Dictionary<string, MidiBinding>();
            }
            catch { }
            return new Dictionary<string, MidiBinding>();
        }

        public static void SaveOverrides(Dictionary<string, MidiBinding> map)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(MapPath, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        /// <summary>Defaults with the user's learned bindings laid over
        /// them — the map the engine actually dispatches on.</summary>
        public static Dictionary<string, MidiBinding> EffectiveBindings()
        {
            var map = new Dictionary<string, MidiBinding>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in MidiCatalog.Actions)
                if (a.DefaultBinding != null) map[a.Id] = a.DefaultBinding;
            foreach (var kv in LoadOverrides())
            {
                if (kv.Value == null || kv.Value.Kind.Length == 0) map.Remove(kv.Key);
                else map[kv.Key] = kv.Value;
            }
            return map;
        }
    }

    /// <summary>
    /// MIDI input (v3.75): listens to one controller via NAudio and
    /// republishes button presses and CC moves in plain terms.
    /// Events fire on the MIDI thread — subscribers marshal to the UI.
    /// </summary>
    public sealed class MidiEngine : IDisposable
    {
        private MidiIn? _in;

        /// <summary>kind ("note"/"cc"), channel (1-16), number, value 0-127.</summary>
        public event Action<string, int, int, int>? Message;

        /// <summary>One human-readable line per message, for the monitor.</summary>
        public event Action<string>? Activity;

        public static List<string> DeviceNames()
        {
            var names = new List<string>();
            try
            {
                for (int i = 0; i < MidiIn.NumberOfDevices; i++)
                {
                    try { names.Add(MidiIn.DeviceInfo(i).ProductName); }
                    catch { names.Add("MIDI device " + (i + 1)); }
                }
            }
            catch { }
            return names;
        }

        public bool Start(int deviceIndex)
        {
            Stop();
            try
            {
                if (deviceIndex < 0 || deviceIndex >= MidiIn.NumberOfDevices) return false;
                _in = new MidiIn(deviceIndex);
                _in.MessageReceived += OnMessage;
                _in.ErrorReceived += (s, e) => { };
                _in.Start();
                return true;
            }
            catch { _in = null; return false; }
        }

        private void OnMessage(object? sender, MidiInMessageEventArgs e)
        {
            int raw = e.RawMessage;
            int cmd = raw & 0xF0;
            int ch = (raw & 0x0F) + 1;
            int d1 = (raw >> 8) & 0x7F;
            int d2 = (raw >> 16) & 0x7F;
            if (cmd == 0x90 && d2 > 0)
            {
                Message?.Invoke("note", ch, d1, d2);
                Activity?.Invoke($"Note {d1} · channel {ch} · velocity {d2}");
            }
            else if (cmd == 0xB0)
            {
                Message?.Invoke("cc", ch, d1, d2);
                Activity?.Invoke($"CC {d1} · channel {ch} · value {d2}");
            }
        }

        public void Stop()
        {
            try { _in?.Stop(); } catch { }
            try { _in?.Dispose(); } catch { }
            _in = null;
        }

        public void Dispose() => Stop();
    }
}
