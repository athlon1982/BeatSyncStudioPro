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
    }
}
