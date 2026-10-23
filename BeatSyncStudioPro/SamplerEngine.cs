using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// One persistent mixer slot per sampler pad (v3.27). Holds the pad's
    /// decoded one-shot and a play cursor; Trigger restarts it from zero.
    /// Silent when idle, so it can live in the DJ mixer permanently.
    /// </summary>
    public sealed class OneShotSlot : ISampleProvider
    {
        private readonly object _lock = new object();
        private float[] _buffer = Array.Empty<float>();
        private int _pos;
        private bool _playing;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

        public bool IsPlaying { get { lock (_lock) return _playing; } }
        public bool HasSound { get { lock (_lock) return _buffer.Length > 0; } }

        public void Load(float[] interleaved)
        {
            lock (_lock) { _buffer = interleaved; _pos = 0; _playing = false; }
        }

        public void Clear()
        {
            lock (_lock) { _buffer = Array.Empty<float>(); _pos = 0; _playing = false; }
        }

        public void Trigger()
        {
            lock (_lock) { _pos = 0; _playing = _buffer.Length > 0; }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            lock (_lock)
            {
                if (!_playing) { Array.Clear(buffer, offset, count); return count; }
                int n = 0;
                while (n < count)
                {
                    if (_pos >= _buffer.Length) { _playing = false; break; }
                    buffer[offset + n] = _buffer[_pos++];
                    n++;
                }
                if (n < count) Array.Clear(buffer, offset + n, count - n);
                return count;
            }
        }
    }

    /// <summary>
    /// The DJ sampler: 8 one-shot pads mixed into the DJ master bus, so pads
    /// are heard live and captured by the mix recorder. Default sounds come
    /// from the built-in sound pack (read straight out of Assets/soundpack.zip);
    /// any pad can be reassigned to a user's own WAV/MP3.
    /// </summary>
    public sealed class SamplerEngine
    {
        public const int PadCount = 8;

        public OneShotSlot[] Slots { get; }
        public VolumeSampleProvider Output { get; }

        private readonly OneShotSlot _previewSlot = new OneShotSlot();

        public SamplerEngine()
        {
            Slots = new OneShotSlot[PadCount];
            var mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2)) { ReadFully = true };
            for (int i = 0; i < PadCount; i++)
            {
                Slots[i] = new OneShotSlot();
                mixer.AddMixerInput(Slots[i]);
            }
            mixer.AddMixerInput(_previewSlot);
            Output = new VolumeSampleProvider(mixer) { Volume = 0.9f };
        }

        /// <summary>v3.46: picker previews play through the sampler's own bus —
        /// same output the pads use, so a preview is always audible.</summary>
        public void Preview(float[] samples)
        {
            _previewSlot.Load(samples);
            _previewSlot.Trigger();
        }

        /// <summary>Cut a preview dead — for sounds that run long.</summary>
        public void StopPreview() => _previewSlot.Clear();

        /// <summary>Cut a pad dead mid-play (v3.76) — pads toggle-stop
        /// when hit again, and the right-click menu has a Stop item.</summary>
        public void StopPad(int pad)
        {
            if (pad >= 0 && pad < Slots.Length) Slots[pad].Clear();
        }

        /// <summary>True while a preview is actually sounding (drives the
        /// picker's Preview⇄Stop toggle button).</summary>
        public bool PreviewIsPlaying => _previewSlot.IsPlaying;

        public void Trigger(int pad)
        {
            if (pad < 0 || pad >= PadCount) return;
            Slots[pad].Trigger();
        }

        /// <summary>Read one entry's bytes out of the built-in sound pack next to the app.</summary>
        public static byte[]? ReadPackEntry(string entryName)
        {
            string pack = Path.Combine(AppContext.BaseDirectory, "Assets", "soundpack.zip");
            if (!File.Exists(pack)) return null;
            using var zip = ZipFile.OpenRead(pack);
            var entry = zip.GetEntry(entryName);
            if (entry == null) return null;
            using var ms = new MemoryStream();
            using (var s = entry.Open()) s.CopyTo(ms);
            return ms.ToArray();
        }

        /// <summary>Decode WAV/MP3 bytes to interleaved 44.1kHz stereo floats — same recipe as the decks.</summary>
        public static float[] DecodeBytes(byte[] bytes, string filename)
        {
            string lower = filename.ToLowerInvariant();
            if (lower.EndsWith(".wav"))
                return ReadAll(new WaveFileReader(new MemoryStream(bytes)));
            string ext = Path.GetExtension(lower);
            string temp = Path.Combine(Path.GetTempPath(), "beatsync-pad-" + Guid.NewGuid().ToString("N") + ext);
            try
            {
                File.WriteAllBytes(temp, bytes);
                return ReadAll(new MediaFoundationReader(temp));
            }
            catch
            {
                return ReadAll(new Mp3FileReader(new MemoryStream(bytes)));
            }
            finally
            {
                try { File.Delete(temp); } catch { }
            }
        }

        private static float[] ReadAll(IWaveProvider reader)
        {
            try
            {
                ISampleProvider provider = new WdlResamplingSampleProvider(reader.ToSampleProvider(), 44100).ToStereo();
                var all = new List<float>();
                var buffer = new float[44100 * 2];
                int read;
                while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
                    for (int i = 0; i < read; i++) all.Add(buffer[i]);
                if (all.Count < 441)   // one-shots can be short; ~5ms of stereo is the floor
                    throw new InvalidOperationException("That file decoded to almost no audio — it may be corrupt or an unsupported format.");
                return all.ToArray();
            }
            finally
            {
                (reader as IDisposable)?.Dispose();
            }
        }
    }
}
