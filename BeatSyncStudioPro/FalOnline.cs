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
    /// fal.ai online compute (PAID per run — the user's own fal key):
    /// ACE-Step for songs and Demucs for stems, via fal's queue API.
    /// Errors surface verbatim — fal's response is the source of truth.
    /// </summary>
    public static class FalOnline
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        private static HttpRequestMessage Req(HttpMethod method, string url, string key, object? payload = null)
        {
            var req = new HttpRequestMessage(method, url);
            req.Headers.TryAddWithoutValidation("Authorization", "Key " + key);
            if (payload != null)
                req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            return req;
        }

        /// <summary>Submit to the fal queue, poll until done, return the result JSON.</summary>
        private static async Task<JsonDocument> RunQueueAsync(string key, string model, object payload,
            Action<string> status, CancellationToken ct, int maxPolls = 240)
        {
            status($"☁️ fal.ai: submitting to {model}…");
            using var submit = await Http.SendAsync(Req(HttpMethod.Post, $"https://queue.fal.run/{model}", key, payload), ct);
            string submitBody = await submit.Content.ReadAsStringAsync(ct);
            if (!submit.IsSuccessStatusCode)
                throw new InvalidOperationException($"fal.ai HTTP {(int)submit.StatusCode}: {Trim(submitBody)}");
            string statusUrl, responseUrl;
            using (var doc = JsonDocument.Parse(submitBody))
            {
                statusUrl = doc.RootElement.GetProperty("status_url").GetString() ?? "";
                responseUrl = doc.RootElement.GetProperty("response_url").GetString() ?? "";
            }
            for (int i = 0; i < maxPolls; i++)
            {
                await Task.Delay(3000, ct);
                using var poll = await Http.SendAsync(Req(HttpMethod.Get, statusUrl, key), ct);
                string pollBody = await poll.Content.ReadAsStringAsync(ct);
                if (!poll.IsSuccessStatusCode)
                    throw new InvalidOperationException($"fal.ai status HTTP {(int)poll.StatusCode}: {Trim(pollBody)}");
                string st;
                using (var doc = JsonDocument.Parse(pollBody))
                    st = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
                if (st == "COMPLETED") break;
                if (st == "FAILED" || st == "ERROR")
                    throw new InvalidOperationException("fal.ai reported the run failed: " + Trim(pollBody));
                if (i == maxPolls - 1)
                    throw new TimeoutException("fal.ai run timed out.");
                status($"☁️ fal.ai: {st.ToLowerInvariant()}… ({(i + 1) * 3}s)");
            }
            status("☁️ fal.ai: done — downloading the result…");
            using var res = await Http.SendAsync(Req(HttpMethod.Get, responseUrl, key), ct);
            string resBody = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException($"fal.ai result HTTP {(int)res.StatusCode}: {Trim(resBody)}");
            return JsonDocument.Parse(resBody);
        }

        /// <summary>ACE-Step song generation on fal.ai. Returns the audio bytes + extension.</summary>
        public static async Task<(byte[] Data, string Ext)> GenerateAceStepAsync(string key, string style,
            string lyrics, int durationSeconds, Action<string> status, CancellationToken ct)
        {
            using var doc = await RunQueueAsync(key, "fal-ai/ace-step", new
            {
                prompt = style,
                lyrics,
                duration = durationSeconds
            }, status, ct);
            string? url = FindAudioUrl(doc.RootElement);
            if (url == null)
                throw new InvalidOperationException("fal.ai finished but no audio URL was in the result: " + Trim(doc.RootElement.GetRawText()));
            byte[] data = await Http.GetByteArrayAsync(url, ct);
            string ext = url.Contains(".wav") ? "wav" : "mp3";
            return (data, ext);
        }

        /// <summary>Text-to-video on fal.ai. Returns the finished clip's URL
        /// (the caller downloads it). Confirmed model ids (fal docs, Oct 2026):
        /// fal-ai/minimax/video-01, fal-ai/ltx-2-19b/text-to-video,
        /// fal-ai/ltx-2.3-22b/text-to-video — all return { video: { url } }.</summary>
        public static async Task<string> GenerateVideoAsync(string key, string model, string prompt,
            Action<string> status, CancellationToken ct)
        {
            using var doc = await RunQueueAsync(key, model, new { prompt }, status, ct, 600);
            string? url = FindVideoUrl(doc.RootElement);
            if (url == null && doc.RootElement.TryGetProperty("data", out var data))
                url = FindVideoUrl(data);
            if (url == null)
                throw new InvalidOperationException("fal.ai finished but no video URL was in the result: " + Trim(doc.RootElement.GetRawText()));
            return url;
        }

        private static string? FindVideoUrl(JsonElement el)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            if (el.TryGetProperty("video", out var v) && v.ValueKind == JsonValueKind.Object
                && v.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
                return u.GetString();
            if (el.TryGetProperty("video_url", out var vs) && vs.ValueKind == JsonValueKind.String)
                return vs.GetString();
            return null;
        }

        /// <summary>Demucs stem separation on fal.ai. Returns stem name → file.</summary>
        public static async Task<Dictionary<string, OnlineStems.StemFile>> RunDemucsAsync(
            byte[] audioBytes, string ext, string key, Action<string> status, CancellationToken ct)
        {
            string mime = ext == "wav" ? "audio/wav" : ext == "flac" ? "audio/flac" : "audio/mpeg";
            string dataUri = $"data:{mime};base64,{Convert.ToBase64String(audioBytes)}";
            using var doc = await RunQueueAsync(key, "fal-ai/demucs", new { audio_url = dataUri }, status, ct, 300);
            var stems = new Dictionary<string, OnlineStems.StemFile>();
            foreach (string stemName in new[] { "vocals", "drums", "bass", "other", "guitar", "piano" })
            {
                string? url = FindNamedUrl(doc.RootElement, stemName);
                if (url == null) continue;
                byte[] data = await Http.GetByteArrayAsync(url, ct);
                stems[stemName] = new OnlineStems.StemFile(data, url.Contains(".wav") ? "wav" : "mp3");
            }
            if (stems.Count == 0)
                throw new InvalidOperationException("fal.ai finished but no stem URLs were in the result: " + Trim(doc.RootElement.GetRawText()));
            return stems;
        }

        private static string? FindAudioUrl(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in el.EnumerateObject())
                {
                    if ((prop.Name == "url" || prop.Name == "audio_url") && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        string v = prop.Value.GetString() ?? "";
                        if (v.StartsWith("http")) return v;
                    }
                    var found = FindAudioUrl(prop.Value);
                    if (found != null) return found;
                }
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in el.EnumerateArray())
                {
                    var found = FindAudioUrl(item);
                    if (found != null) return found;
                }
            }
            return null;
        }

        private static string? FindNamedUrl(JsonElement el, string name)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
            if (el.TryGetProperty(name, out var child))
            {
                if (child.ValueKind == JsonValueKind.String) return child.GetString();
                if (child.ValueKind == JsonValueKind.Object && child.TryGetProperty("url", out var u))
                    return u.GetString();
            }
            foreach (var prop in el.EnumerateObject())
            {
                var found = FindNamedUrl(prop.Value, name);
                if (found != null) return found;
            }
            return null;
        }

        private static string Trim(string s) => s.Length <= 400 ? s : s.Substring(0, 400) + "…";
    }
}
