using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// Cloud stem splitting via Replicate's Demucs (the same model family
    /// the local splitter runs). The user's own API key does the paying —
    /// a song costs a few cents. Errors from the service are surfaced
    /// verbatim so a mismatched model setting is a quick fix, not a mystery.
    /// </summary>
    public static class OnlineStems
    {
        public record StemFile(byte[] Data, string Ext);

        public static async Task<Dictionary<string, StemFile>> RunDemucsAsync(
            byte[] audio, string sourceExt, string apiKey, Action<string> status, CancellationToken ct)
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("BeatSyncStudioPro");

            string mime = sourceExt.ToLowerInvariant() switch
            {
                "wav" => "audio/wav",
                "flac" => "audio/flac",
                "m4a" => "audio/mp4",
                "ogg" => "audio/ogg",
                _ => "audio/mpeg"
            };
            string dataUri = $"data:{mime};base64," + Convert.ToBase64String(audio);
            var payload = new { input = new { audio = dataUri } };

            status("☁️ Sending the song to Replicate (Demucs)…");
            using var res = await http.PostAsync(
                "https://api.replicate.com/v1/models/cjwbw/demucs/predictions",
                new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), ct);
            string body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException($"Replicate said {(int)res.StatusCode}: {Trim(body)}");

            string getUrl;
            using (var doc = JsonDocument.Parse(body))
                getUrl = doc.RootElement.GetProperty("urls").GetProperty("get").GetString() ?? "";

            for (int i = 0; i < 200; i++)
            {
                await Task.Delay(3000, ct);
                using var pres = await http.GetAsync(getUrl, ct);
                string pbody = await pres.Content.ReadAsStringAsync(ct);
                using var pdoc = JsonDocument.Parse(pbody);
                var root = pdoc.RootElement;
                string state = root.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "";
                if (state == "succeeded")
                {
                    var stems = new Dictionary<string, StemFile>();
                    if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in output.EnumerateObject())
                        {
                            if (prop.Value.ValueKind != JsonValueKind.String) continue;
                            string url = prop.Value.GetString() ?? "";
                            status($"☁️ Downloading the {prop.Name} stem…");
                            byte[] data = await http.GetByteArrayAsync(url, ct);
                            string ext = url.Contains(".mp3") ? "mp3" : "wav";
                            stems[prop.Name] = new StemFile(data, ext);
                        }
                    }
                    if (stems.Count == 0)
                        throw new InvalidOperationException("Replicate finished but returned no stem files. Response: " + Trim(pbody));
                    return stems;
                }
                if (state == "failed" || state == "canceled")
                    throw new InvalidOperationException($"Replicate run {state}: {Trim(pbody)}");
                status($"☁️ Replicate is splitting… ({state})");
            }
            throw new InvalidOperationException("Replicate took too long (10+ minutes) — please try again.");
        }

        private static string Trim(string s) => s.Length <= 400 ? s : s.Substring(0, 400) + "…";

        // ---------- Hugging Face free tier (experimental) ----------

        private const string HfDemucsModel = "facebook/demucs";

        /// <summary>
        /// FREE online route: Hugging Face's inference API (free tier —
        /// rate-limited, models sleep when idle, so the first call can take
        /// a minute to wake up). Response parsing is defensive: if HF
        /// changes shape, the raw answer is surfaced instead of failing mute.
        /// </summary>
        public static async Task<Dictionary<string, StemFile>> RunHfDemucsAsync(
            byte[] audio, string sourceExt, string hfToken, Action<string> status, CancellationToken ct)
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + hfToken);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("BeatSyncStudioPro");

            status("☁️ Sending the song to Hugging Face (free tier — a sleeping model can take a minute to wake)…");
            using var content = new ByteArrayContent(audio);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                sourceExt.ToLowerInvariant() == "wav" ? "audio/wav" : "audio/mpeg");
            using var res = await http.PostAsync($"https://api-inference.huggingface.co/models/{HfDemucsModel}", content, ct);
            string body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException($"Hugging Face said {(int)res.StatusCode}: {Trim(body)}");

            var stems = new Dictionary<string, StemFile>();
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                void TryAdd(string name, JsonElement v)
                {
                    if (v.ValueKind != JsonValueKind.String) return;
                    string s = v.GetString() ?? "";
                    try
                    {
                        byte[] data = Convert.FromBase64String(s);
                        if (data.Length > 1000) stems[name] = new StemFile(data, "wav");
                    }
                    catch { }
                }
                if (root.ValueKind == JsonValueKind.Array)
                {
                    int i = 0;
                    foreach (var item in root.EnumerateArray())
                    {
                        string name = item.TryGetProperty("label", out var lb) ? lb.GetString() ?? $"stem{i + 1}" : $"stem{i + 1}";
                        if (item.TryGetProperty("data", out var d)) TryAdd(name, d);
                        else if (item.TryGetProperty("audio", out var a)) TryAdd(name, a);
                        i++;
                    }
                }
                else if (root.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in root.EnumerateObject()) TryAdd(prop.Name, prop.Value);
                }
            }
            catch (JsonException) { }
            if (stems.Count == 0)
                throw new InvalidOperationException("Hugging Face answered, but not with stems I can read — the free endpoint's shape may have changed. Raw answer: " + Trim(body));
            return stems;
        }
    }

    /// <summary>Cloud song generation (ACE-Step on Replicate).</summary>
    public static class OnlineSongs
    {
        /// <summary>Generates a song and returns the audio bytes (mp3/wav).</summary>
        public static async Task<(byte[] Data, string Ext)> GenerateAceStepAsync(
            string apiKey, string title, string style, string lyrics,
            int durationSeconds, double guidance, Action<string> status, CancellationToken ct)
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("BeatSyncStudioPro");

            var payload = new
            {
                input = new
                {
                    prompt = style,
                    lyrics = lyrics,
                    duration = durationSeconds,
                    guidance_scale = guidance
                }
            };
            status("☁️ Sending lyrics + style to ACE-Step on Replicate…");
            using var res = await http.PostAsync(
                "https://api.replicate.com/v1/models/lucataco/ace-step/predictions",
                new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), ct);
            string body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException($"Replicate said {(int)res.StatusCode}: {Trim(body)}");

            string getUrl;
            using (var doc = JsonDocument.Parse(body))
                getUrl = doc.RootElement.GetProperty("urls").GetProperty("get").GetString() ?? "";

            for (int i = 0; i < 240; i++)
            {
                await Task.Delay(3000, ct);
                using var pres = await http.GetAsync(getUrl, ct);
                string pbody = await pres.Content.ReadAsStringAsync(ct);
                using var pdoc = JsonDocument.Parse(pbody);
                var root = pdoc.RootElement;
                string state = root.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "";
                if (state == "succeeded")
                {
                    string? url = null;
                    if (root.TryGetProperty("output", out var output))
                    {
                        if (output.ValueKind == JsonValueKind.String) url = output.GetString();
                        else if (output.ValueKind == JsonValueKind.Array && output.GetArrayLength() > 0) url = output[0].GetString();
                    }
                    if (string.IsNullOrEmpty(url))
                        throw new InvalidOperationException("Replicate finished but returned no audio URL. Response: " + Trim(pbody));
                    status("☁️ Downloading your song…");
                    byte[] data = await http.GetByteArrayAsync(url, ct);
                    return (data, url.Contains(".wav") ? "wav" : "mp3");
                }
                if (state == "failed" || state == "canceled")
                    throw new InvalidOperationException($"Replicate run {state}: {Trim(pbody)}");
                status($"☁️ ACE-Step is composing… ({state})");
            }
            throw new InvalidOperationException("Replicate took too long (12+ minutes) — please try again.");
        }

        private static string Trim(string s) => s.Length <= 400 ? s : s.Substring(0, 400) + "…";
    }
}
