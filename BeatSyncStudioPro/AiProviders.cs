using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// AI Lab settings, kept in %AppData%/BeatSyncStudioPro/ai-settings.json.
    /// API keys are stored DPAPI-encrypted for the current Windows user.
    /// </summary>
    public class AiSettings
    {
        public string ProviderId { get; set; } = "opencode";
        public string Model { get; set; } = "opencode/nemotron-3-ultra-free";
        public Dictionary<string, string> Keys { get; set; } = new Dictionary<string, string>();
        public string CustomBaseUrl { get; set; } = "";

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BeatSyncStudioPro", "ai-settings.json");

        public static AiSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<AiSettings>(File.ReadAllText(FilePath)) ?? new AiSettings();
            }
            catch { }
            return new AiSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        public string GetKey(string presetId)
        {
            if (!Keys.TryGetValue(presetId, out var enc) || string.IsNullOrEmpty(enc)) return "";
            try
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(enc), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch { return ""; }
        }

        public void SetKey(string presetId, string key)
        {
            if (string.IsNullOrWhiteSpace(key)) { Keys.Remove(presetId); return; }
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), null, DataProtectionScope.CurrentUser);
            Keys[presetId] = Convert.ToBase64String(bytes);
        }
    }

    public interface IAiProvider
    {
        string Id { get; }
        string DisplayName { get; }
        Task<List<string>> GetModelsAsync(CancellationToken ct);
        Task<string> ChatAsync(string model, string prompt, CancellationToken ct);
        // Vision: only providers that take images implement this. The rest
        // keep the default, which fails with a plain message the UI shows.
        Task<string> ChatWithImageAsync(string model, string prompt, string imageBase64, CancellationToken ct)
            => throw new NotSupportedException("This provider doesn't take images — switch to Ollama (local) with a vision model like moondream.");
    }

    internal static class AiHttp
    {
        public static readonly HttpClient Client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        public static async Task<string> GetStringAsync(string url, string? bearer, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(bearer)) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
            using var res = await Client.SendAsync(req, ct);
            string body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException($"HTTP {(int)res.StatusCode}: {Trim(body)}");
            return body;
        }

        public static async Task<string> PostJsonAsync(string url, object payload, string? bearer, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            if (!string.IsNullOrEmpty(bearer)) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var res = await Client.SendAsync(req, ct);
            string body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException($"HTTP {(int)res.StatusCode}: {Trim(body)}");
            return body;
        }

        private static string Trim(string s) => s.Length <= 300 ? s : s.Substring(0, 300) + "…";
    }

    /// <summary>Local models served by Ollama on this PC. No key, works offline.</summary>
    public class OllamaProvider : IAiProvider
    {
        public string Id => "ollama";
        public string DisplayName => "🖥️ Ollama (local, free)";
        private const string Base = "http://localhost:11434";

        public async Task<List<string>> GetModelsAsync(CancellationToken ct)
        {
            string json = await AiHttp.GetStringAsync(Base + "/api/tags", null, ct);
            var models = new List<string>();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("models", out var arr))
                foreach (var m in arr.EnumerateArray())
                    if (m.TryGetProperty("name", out var n)) models.Add(n.GetString() ?? "");
            return models;
        }

        public async Task<string> ChatAsync(string model, string prompt, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(model))
                throw new InvalidOperationException("No Ollama model picked — install one first (e.g. run: ollama pull llama3.1:8b), then Refresh.");
            string json = await AiHttp.PostJsonAsync(Base + "/api/chat",
                new { model, messages = new[] { new { role = "system", content = AiKnowledge.ForChatSystem() }, new { role = "user", content = prompt } }, stream = false }, null, ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";
        }

        public async Task<string> ChatWithImageAsync(string model, string prompt, string imageBase64, CancellationToken ct)
        {
            // No knowledge injection here on purpose: this is the Vision tab's
            // moondream describe step, and the description must stay a pure read
            // of the image. The pack arrives at the writing step, which goes
            // through ChatAsync above.
            if (string.IsNullOrWhiteSpace(model))
                throw new InvalidOperationException("No Ollama model picked — pull a vision model first (e.g. run: ollama pull moondream), then Refresh.");
            string json = await AiHttp.PostJsonAsync(Base + "/api/chat",
                new { model, messages = new[] { new { role = "user", content = prompt, images = new[] { imageBase64 } } }, stream = false }, null, ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";
        }
    }

    /// <summary>
    /// Michael's OpenCode (desktop app's bundled CLI) and its free models —
    /// runs opencode-cli as a subprocess under his own OpenCode sign-in.
    /// </summary>
    public class OpenCodeProvider : IAiProvider
    {
        public string Id => "opencode";
        public string DisplayName => "🆓 OpenCode (free models)";

        public static string? FindCli()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string[] candidates =
            {
                Path.Combine(local, "Programs", "@opencodedesktop", "resources", "opencode-cli.exe"),
                Path.Combine(profile, ".opencode", "bin", "opencode.exe"),
                "opencode"
            };
            foreach (var c in candidates)
            {
                if (c == "opencode" || File.Exists(c)) return c;
            }
            return null;
        }

        private static async Task<(string StdOut, string StdErr, int Code)> RunCliAsync(
            IEnumerable<string> args, CancellationToken ct, int timeoutMs = 600000)
        {
            string? cli = FindCli();
            if (cli == null)
                throw new InvalidOperationException("OpenCode was not found on this PC (looked for the OpenCode desktop app and PATH).");
            var psi = new ProcessStartInfo
            {
                FileName = cli,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath()
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var proc = new Process { StartInfo = psi };
            proc.Start();
            var outTask = proc.StandardOutput.ReadToEndAsync(ct);
            var errTask = proc.StandardError.ReadToEndAsync(ct);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);
            try { await proc.WaitForExitAsync(timeoutCts.Token); }
            catch (OperationCanceledException) { try { proc.Kill(true); } catch { } throw; }
            return (await outTask, await errTask, proc.ExitCode);
        }

        public async Task<List<string>> GetModelsAsync(CancellationToken ct)
        {
            var (stdout, _, _) = await RunCliAsync(new[] { "models" }, ct, 60000);
            var models = new List<string>();
            foreach (var line in stdout.Split('\n'))
            {
                string m = line.Trim();
                if (m.Contains("/") && m.Contains("free")) models.Add(m);
            }
            models.Sort((a, b) =>
            {
                int Rank(string s) => s.StartsWith("opencode/") ? 0 : 1;
                int c = Rank(a).CompareTo(Rank(b));
                return c != 0 ? c : string.Compare(a, b, StringComparison.Ordinal);
            });
            return models;
        }

        public async Task<string> ChatAsync(string model, string prompt, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(model)) model = "opencode/nemotron-3-ultra-free";
            var (stdout, stderr, code) = await RunCliAsync(new[] { "run", "-m", model, AiKnowledge.ForCli(prompt) }, ct);
            string text = stdout.Trim();
            if (text.Length == 0)
                throw new InvalidOperationException($"OpenCode returned nothing (exit {code}). {stderr.Trim()}");
            return text;
        }
    }

    /// <summary>Any OpenAI-compatible chat endpoint (OpenAI, Gemini, OpenRouter, custom).</summary>
    public class OpenAiCompatProvider : IAiProvider
    {
        private readonly AiSettings _settings;
        public string PresetId { get; }
        public string BaseUrl { get; }
        public string DefaultModel { get; }
        public string Id => PresetId;
        public string DisplayName { get; }

        public OpenAiCompatProvider(string presetId, string displayName, string baseUrl, string defaultModel, AiSettings settings)
        {
            PresetId = presetId;
            DisplayName = displayName;
            BaseUrl = baseUrl.TrimEnd('/');
            DefaultModel = defaultModel;
            _settings = settings;
        }

        private string Key => _settings.GetKey(PresetId == "custom" ? "custom" : PresetId);

        public async Task<List<string>> GetModelsAsync(CancellationToken ct)
        {
            var models = new List<string>();
            try
            {
                string json = await AiHttp.GetStringAsync(BaseUrl + "/models", Key, ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("data", out var arr))
                    foreach (var m in arr.EnumerateArray())
                        if (m.TryGetProperty("id", out var id)) models.Add(id.GetString() ?? "");
                models.Sort(StringComparer.OrdinalIgnoreCase);
            }
            catch { /* model list is a convenience; the default model still works */ }
            if (models.Count == 0 && DefaultModel.Length > 0) models.Add(DefaultModel);
            return models;
        }

        public async Task<string> ChatAsync(string model, string prompt, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(Key))
                throw new InvalidOperationException($"No API key saved for {DisplayName} — paste your key above and Save it first.");
            if (string.IsNullOrWhiteSpace(model)) model = DefaultModel;
            string json = await AiHttp.PostJsonAsync(BaseUrl + "/chat/completions",
                new { model, messages = new[] { new { role = "system", content = AiKnowledge.ForChatSystem() }, new { role = "user", content = prompt } } }, Key, ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        }
    }
}
