using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace BeatSyncStudioPro
{
    /// <summary>One internet-radio station: a display name + the direct stream URL.</summary>
    public sealed class RadioStation
    {
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
    }

    /// <summary>radio-stations.json persistence (path injectable for tests).
    /// Ships with Michael's two local Rutland stations (their direct
    /// streams, verified live 2026-10-23) plus his own Rad-Dad Radio.</summary>
    public static class RadioStore
    {
        public static string DefaultPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BeatSyncStudioPro", "radio-stations.json");

        public static List<RadioStation> Defaults() => new List<RadioStation>
        {
            new RadioStation { Name = "Rock 94.5 — WDVT (Rutland, classic rock)", Url = "https://ais-sa1.streamon.fm/7822_96k.aac" },
            new RadioStation { Name = "Z97.1 — WZRT (Rutland, Top 40)", Url = "https://ais-sa1.streamon.fm/7826_128k.aac" },
            new RadioStation { Name = "Rad-Dad Radio (your Icecast)", Url = "http://66.220.235.250:8000/stream" }
        };

        public static bool IsValidStreamUrl(string? url) =>
            !string.IsNullOrWhiteSpace(url) &&
            Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) &&
            (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

        public static List<RadioStation> Load(string? path = null)
        {
            try
            {
                string p = path ?? DefaultPath;
                if (File.Exists(p))
                {
                    var saved = JsonSerializer.Deserialize<List<RadioStation>>(File.ReadAllText(p));
                    if (saved != null && saved.Count > 0) return saved;
                }
            }
            catch { }
            return Defaults();
        }

        public static void Save(List<RadioStation> stations, string? path = null)
        {
            try
            {
                string p = path ?? DefaultPath;
                var dir = Path.GetDirectoryName(p);
                if (dir != null) Directory.CreateDirectory(dir);
                File.WriteAllText(p, JsonSerializer.Serialize(stations));
            }
            catch { }
        }

        /// <summary>Extract StreamTitle from an ICY metadata block (pure — tested).</summary>
        public static string ParseStreamTitle(string metadata)
        {
            if (string.IsNullOrEmpty(metadata)) return "";
            const string key = "StreamTitle='";
            int i = metadata.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return "";
            int start = i + key.Length;
            int end = metadata.IndexOf("';", start, StringComparison.Ordinal);
            if (end < 0) end = metadata.IndexOf('\'', start);
            if (end < 0) return "";
            return metadata.Substring(start, end - start).Trim();
        }
    }

    /// <summary>
    /// Internet radio player (v3.99): pulls a direct station stream with
    /// Media Foundation on a background pump thread into a ring buffer,
    /// so a network stall can never block or glitch the DJ mix — the
    /// provider just plays silence until audio arrives. A second,
    /// metadata-only connection reads ICY StreamTitle (the song the
    /// station is playing) without touching the audio path.
    /// </summary>
    public sealed class RadioPlayer : ISampleProvider
    {
        private const int BufferSamples = 44100 * 2 * 12; // 12 s of stereo audio
        private readonly float[] _ring = new float[BufferSamples];
        private int _ringRead;
        private int _ringWrite;
        private int _ringCount;
        private readonly object _ringLock = new object();

        private CancellationTokenSource? _cts;
        private MediaFoundationReader? _reader;
        private ISampleProvider? _readerChain;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        public VolumeSampleProvider Output { get; }

        public bool IsPlaying { get; private set; }
        public string CurrentStation { get; private set; } = "";
        public string NowPlayingTitle { get; private set; } = "";
        public string Status { get; private set; } = "Stopped.";

        public event Action? StateChanged;

        public RadioPlayer()
        {
            Output = new VolumeSampleProvider(this) { Volume = 0.9f };
        }

        public void Play(RadioStation station)
        {
            Stop();
            var cts = new CancellationTokenSource();
            _cts = cts;
            CurrentStation = station.Name;
            NowPlayingTitle = "";
            IsPlaying = true;
            Status = "Connecting to " + station.Name + "…";
            StateChanged?.Invoke();

            Task.Run(() => PumpAudio(station.Url, cts));
            Task.Run(() => PumpMetadata(station.Url, cts));
        }

        public void Stop()
        {
            _cts?.Cancel();
            _cts = null;
            try { _reader?.Dispose(); } catch { }
            _reader = null;
            _readerChain = null;
            lock (_ringLock) { _ringRead = 0; _ringWrite = 0; _ringCount = 0; }
            if (IsPlaying) { Status = "Stopped."; }
            IsPlaying = false;
            CurrentStation = "";
            NowPlayingTitle = "";
            StateChanged?.Invoke();
        }

        private void PumpAudio(string url, CancellationTokenSource cts)
        {
            try
            {
                var reader = new MediaFoundationReader(url);
                _reader = reader;
                ISampleProvider chain = reader.ToSampleProvider();
                if (chain.WaveFormat.SampleRate != 44100 || chain.WaveFormat.Channels != 2)
                {
                    var resampler = new MediaFoundationResampler(reader, WaveFormat.CreateIeeeFloatWaveFormat(44100, 2));
                    chain = resampler.ToSampleProvider();
                }
                _readerChain = chain;
                Status = "Playing " + CurrentStation;
                StateChanged?.Invoke();

                var buf = new float[8192];
                while (!cts.IsCancellationRequested)
                {
                    int read = chain.Read(buf, 0, buf.Length);
                    if (read <= 0) { Thread.Sleep(30); continue; }
                    lock (_ringLock)
                    {
                        for (int i = 0; i < read; i++)
                        {
                            _ring[_ringWrite] = buf[i];
                            _ringWrite = (_ringWrite + 1) % BufferSamples;
                            if (_ringCount < BufferSamples) _ringCount++;
                            else _ringRead = (_ringRead + 1) % BufferSamples; // overwrite oldest
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (!cts.IsCancellationRequested)
                {
                    Status = "Stream stopped: " + ex.Message;
                    IsPlaying = false;
                    StateChanged?.Invoke();
                }
            }
        }

        private async Task PumpMetadata(string url, CancellationTokenSource cts)
        {
            try
            {
                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromSeconds(20);
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Icy-MetaData", "1");
                req.Headers.UserAgent.ParseAdd("BeatSyncStudioPro/3.99");
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (!resp.IsSuccessStatusCode) return;
                int metaint = 0;
                if (resp.Headers.TryGetValues("icy-metaint", out var vals))
                    foreach (var v in vals) int.TryParse(v, out metaint);
                if (metaint <= 0) return;

                using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
                var audioSkip = new byte[metaint];
                while (!cts.IsCancellationRequested)
                {
                    int got = 0;
                    while (got < metaint)
                    {
                        int n = await stream.ReadAsync(audioSkip, got, metaint - got, cts.Token);
                        if (n <= 0) return;
                        got += n;
                    }
                    int lenByte = stream.ReadByte();
                    if (lenByte < 0) return;
                    int metaLen = lenByte * 16;
                    if (metaLen == 0) continue;
                    var meta = new byte[metaLen];
                    int mg = 0;
                    while (mg < metaLen)
                    {
                        int n = await stream.ReadAsync(meta, mg, metaLen - mg, cts.Token);
                        if (n <= 0) return;
                        mg += n;
                    }
                    string text = System.Text.Encoding.UTF8.GetString(meta).TrimEnd('\0');
                    string title = RadioStore.ParseStreamTitle(text);
                    if (title.Length > 0 && title != NowPlayingTitle)
                    {
                        NowPlayingTitle = title;
                        StateChanged?.Invoke();
                    }
                }
            }
            catch { /* metadata is a bonus; silence is fine */ }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            lock (_ringLock)
            {
                int n = Math.Min(count, _ringCount);
                for (int i = 0; i < n; i++)
                {
                    buffer[offset + i] = _ring[_ringRead];
                    _ringRead = (_ringRead + 1) % BufferSamples;
                }
                _ringCount -= n;
                if (n < count) Array.Clear(buffer, offset + n, count - n);
            }
            return count;
        }
    }
}
