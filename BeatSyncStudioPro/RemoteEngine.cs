using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// Client for the BeatSync Engine (v3.23, Michael's ask: "make the app
    /// use a GPU that's in another computer for the AI Lab"). The engine is
    /// a small free server that runs on Michael's OTHER PC — the one with
    /// the real graphics card (GTX 1660 desktop / RTX 3050 laptop) — and
    /// this app sends it generation jobs over the home network or
    /// Tailscale, exactly like a cloud provider, except it's his machine:
    /// no per-run fees, and nothing leaves his own hardware.
    ///
    /// THE ENGINE CONTRACT (what the server on the other PC implements —
    /// deliberately tiny, so the engine can be upgraded without touching
    /// this app):
    ///   GET  /health   → 200 JSON {"status":"ok","gpu":"GeForce GTX 1660",
    ///                    "models":["ace-step","demucs"]}
    ///   POST /generate → JSON in {"title","prompt","lyrics",
    ///                    "duration_seconds"}; answers with the audio file
    ///                    itself (audio/* bytes), or JSON {"audio_url": "..."}
    ///                    pointing at the file, or {"audio_base64": "...",
    ///                    "format": "mp3"}.
    /// ACE-Step does the singing on the engine; the Rad-Dad voice
    /// conversion (RVC) stage joins the same contract later.
    /// </summary>
    public static class RemoteEngine
    {
        // Infinite timeout on the client itself: generation on a home GPU
        // legitimately takes minutes. Every call carries its own linked
        // cancellation token with a sane cap instead.
        private static readonly HttpClient Http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        public static string Normalize(string url)
        {
            url = (url ?? "").Trim();
            if (url.Length > 0 && !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                url = "http://" + url;
            return url.TrimEnd('/');
        }

        private static string Unreachable(string url, Exception ex) =>
            $"Couldn't reach the BeatSync Engine at {url} — is that PC on, the engine running, and the address right? ({ex.Message})";

        /// <summary>Ask the engine to say hello. Returns a one-line report (GPU + models) or throws with a plain-language reason.</summary>
        public static async Task<string> HealthAsync(string baseUrl, CancellationToken ct)
        {
            string url = Normalize(baseUrl);
            if (url.Length == 0) throw new InvalidOperationException("No engine address saved yet.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                string json = await Http.GetStringAsync(url + "/health", timeout.Token);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                string gpu = root.TryGetProperty("gpu", out var g) ? g.GetString() ?? "unknown GPU" : "unknown GPU";
                string models = "";
                if (root.TryGetProperty("models", out var m) && m.ValueKind == JsonValueKind.Array)
                {
                    var names = new System.Collections.Generic.List<string>();
                    foreach (var item in m.EnumerateArray())
                        if (item.GetString() is string n) names.Add(n);
                    if (names.Count > 0) models = " · models: " + string.Join(", ", names);
                }
                return $"Engine online — {gpu}{models}";
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new InvalidOperationException($"The engine at {url} didn't answer in 10 seconds — the PC may be asleep, or the engine isn't running.");
            }
            catch (HttpRequestException ex) { throw new InvalidOperationException(Unreachable(url, ex)); }
            catch (JsonException) { throw new InvalidOperationException($"Something answered at {url}, but it doesn't look like the BeatSync Engine."); }
        }

        /// <summary>
        /// Probe the configured engines in order and return the first that
        /// answers /health (v3.24 — Michael's two-PC pool). Each job runs on
        /// ONE engine; if the first PC is asleep or off, the next takes the
        /// job. Throws with every engine's reason when none answer.
        /// </summary>
        public static async Task<string> PickEngineAsync(System.Collections.Generic.IReadOnlyList<string> urls, Action<string> status, CancellationToken ct)
        {
            var failures = new System.Collections.Generic.List<string>();
            for (int i = 0; i < urls.Count; i++)
            {
                string u = Normalize(urls[i]);
                status($"🩺 Asking engine {i + 1} ({u}) if it's awake…");
                try
                {
                    string health = await HealthAsync(u, ct);
                    status($"🎛️ Engine {i + 1} is on the job — {health}");
                    return u;
                }
                catch (Exception ex) { failures.Add($"engine {i + 1} ({u}): {ex.Message}"); }
            }
            throw new InvalidOperationException("No engine PC answered. " + string.Join(" · ", failures));
        }

        /// <summary>Pick a healthy engine from the pool, then generate there.</summary>
        public static async Task<(byte[] Data, string Ext)> GenerateSongPooledAsync(
            System.Collections.Generic.IReadOnlyList<string> urls, string title, string style, string lyrics, int seconds,
            Action<string> status, CancellationToken ct)
        {
            string engine = await PickEngineAsync(urls, status, ct);
            return await GenerateSongAsync(engine, title, style, lyrics, seconds, status, ct);
        }

        /// <summary>Send a song job to the engine and bring the finished audio home. Returns (bytes, extension).</summary>
        public static async Task<(byte[] Data, string Ext)> GenerateSongAsync(
            string baseUrl, string title, string style, string lyrics, int seconds,
            Action<string> status, CancellationToken ct)
        {
            string url = Normalize(baseUrl);
            if (url.Length == 0) throw new InvalidOperationException("No engine address saved yet — 🌐 Providers → 🏠 Remote GPU card.");

            status($"📡 Sending '{title}' to your engine PC ({url})…");
            var payload = JsonSerializer.Serialize(new
            {
                title,
                prompt = style,
                lyrics,
                duration_seconds = seconds
            });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(20));
            HttpResponseMessage resp;
            try
            {
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                resp = await Http.PostAsync(url + "/generate", content, timeout.Token);
            }
            catch (HttpRequestException ex) { throw new InvalidOperationException(Unreachable(url, ex)); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new InvalidOperationException("The engine took over 20 minutes — giving up on this run. Check the engine PC.");
            }

            using (resp)
            {
                if (!resp.IsSuccessStatusCode)
                {
                    string body = await resp.Content.ReadAsStringAsync(ct);
                    if (body.Length > 220) body = body.Substring(0, 220);
                    throw new InvalidOperationException($"The engine answered {(int)resp.StatusCode}: {body}");
                }
                string media = resp.Content.Headers.ContentType?.MediaType ?? "";
                if (media.StartsWith("audio/") || media == "application/octet-stream")
                {
                    status("⬇️ Your song is coming home…");
                    byte[] data = await resp.Content.ReadAsByteArrayAsync(ct);
                    return (data, ExtFromMedia(media));
                }
                string json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("audio_base64", out var b64) && b64.GetString() is string s && s.Length > 0)
                {
                    string fmt = root.TryGetProperty("format", out var f) ? f.GetString() ?? "mp3" : "mp3";
                    return (Convert.FromBase64String(s), SanitizeExt(fmt));
                }
                string? fileUrl = null;
                if (root.TryGetProperty("audio_url", out var au)) fileUrl = au.GetString();
                else if (root.TryGetProperty("url", out var u)) fileUrl = u.GetString();
                if (!string.IsNullOrEmpty(fileUrl))
                {
                    string full = fileUrl!.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        ? fileUrl! : url + "/" + fileUrl!.TrimStart('/');
                    status("⬇️ Your song is coming home…");
                    byte[] data = await Http.GetByteArrayAsync(full, ct);
                    return (data, ExtFromUrl(full));
                }
                throw new InvalidOperationException("The engine answered, but in a shape BeatSync doesn't understand yet (no audio, audio_url or audio_base64).");
            }
        }

        private static string ExtFromMedia(string media) => media switch
        {
            "audio/mpeg" => "mp3",
            "audio/wav" or "audio/x-wav" => "wav",
            "audio/flac" or "audio/x-flac" => "flac",
            "audio/mp4" => "m4a",
            "audio/ogg" => "ogg",
            _ => "mp3"
        };

        private static string ExtFromUrl(string u)
        {
            try
            {
                string path = new Uri(u).AbsolutePath;
                int dot = path.LastIndexOf('.');
                if (dot >= 0) return SanitizeExt(path.Substring(dot + 1));
            }
            catch { }
            return "mp3";
        }

        private static string SanitizeExt(string ext)
        {
            ext = (ext ?? "").Trim().TrimStart('.').ToLowerInvariant();
            return ext is "mp3" or "wav" or "flac" or "m4a" or "ogg" ? ext : "mp3";
        }
    }
}
