using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// Chat song requests (v3.93): while the DJ turns the monitor on,
    /// viewers can type "!request &lt;song&gt;" in Twitch chat (read-only
    /// anonymous IRC — no Twitch login needed) or YouTube live chat
    /// (Data API polling; needs the DJ's own free YouTube API key and the
    /// live video ID). Parsed requests land in the same CompanionServer
    /// request store the web request page uses, tagged with their source.
    /// </summary>
    public sealed class ChatRequestSettings
    {
        public string TwitchChannel { get; set; } = "";
        public string YoutubeVideoId { get; set; } = "";
        public string YoutubeApiKeyEncrypted { get; set; } = "";

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BeatSyncStudioPro", "chat-request-settings.json");

        public static ChatRequestSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = JsonSerializer.Deserialize<ChatRequestSettings>(File.ReadAllText(FilePath));
                    if (s != null) return s;
                }
            }
            catch { }
            return new ChatRequestSettings();
        }

        public void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (dir != null) Directory.CreateDirectory(dir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
            }
            catch { }
        }

        public string GetYoutubeApiKey()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(YoutubeApiKeyEncrypted)) return "";
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(YoutubeApiKeyEncrypted), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch { return ""; }
        }

        public void SetYoutubeApiKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) { YoutubeApiKeyEncrypted = ""; return; }
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), null, DataProtectionScope.CurrentUser);
            YoutubeApiKeyEncrypted = Convert.ToBase64String(bytes);
        }
    }

    public static class ChatCommand
    {
        /// <summary>"!request some song" / "!songrequest some song" → the song text.</summary>
        public static bool TryParseSongRequest(string? text, out string song)
        {
            song = "";
            if (string.IsNullOrWhiteSpace(text)) return false;
            string t = text.Trim();
            string rest;
            if (t.StartsWith("!request ", StringComparison.OrdinalIgnoreCase)) rest = t.Substring(9);
            else if (t.StartsWith("!songrequest ", StringComparison.OrdinalIgnoreCase)) rest = t.Substring(13);
            else if (t.Equals("!request", StringComparison.OrdinalIgnoreCase) ||
                     t.Equals("!songrequest", StringComparison.OrdinalIgnoreCase)) return false;
            else return false;
            rest = rest.Trim();
            if (rest.Length == 0) return false;
            if (rest.Length > 120) rest = rest.Substring(0, 120);
            song = rest;
            return true;
        }
    }

    public static class TwitchIrc
    {
        /// <summary>Parse one IRC line into (user, message) for PRIVMSG lines.</summary>
        public static bool TryParsePrivMsg(string? line, out string user, out string message)
        {
            user = ""; message = "";
            if (string.IsNullOrEmpty(line)) return false;
            int priv = line.IndexOf(" PRIVMSG ", StringComparison.Ordinal);
            if (priv < 0) return false;
            // prefix: ":user!user@host" (possibly after @tags)
            int bang = line.IndexOf('!');
            if (bang < 1) return false;
            int colon = line.LastIndexOf(':', bang);
            user = line.Substring(colon + 1, bang - colon - 1);
            // message: first " :" after the PRIVMSG target
            int msgSep = line.IndexOf(" :", priv + 9, StringComparison.Ordinal);
            if (msgSep < 0) return false;
            message = line.Substring(msgSep + 2).TrimEnd('\r', '\n');
            return user.Length > 0;
        }
    }

    public sealed record YouTubeChatMessage(string Author, string Text);

    public static class YouTubeChat
    {
        public static bool TryParsePage(string json, out string nextToken, out int pollMs,
            out List<YouTubeChatMessage> messages)
        {
            nextToken = ""; pollMs = 5000; messages = new List<YouTubeChatMessage>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("nextPageToken", out var nt)) nextToken = nt.GetString() ?? "";
                if (root.TryGetProperty("pollingIntervalMillis", out var pi) && pi.TryGetInt32(out int ms))
                    pollMs = Math.Clamp(ms, 2000, 15000);
                if (!root.TryGetProperty("items", out var items)) return true;
                foreach (var item in items.EnumerateArray())
                {
                    try
                    {
                        if (!item.TryGetProperty("snippet", out var sn)) continue;
                        if (!sn.TryGetProperty("textMessageDetails", out var det)) continue;
                        string text = det.TryGetProperty("messageText", out var mt) ? mt.GetString() ?? "" : "";
                        string author = "";
                        if (item.TryGetProperty("authorDetails", out var ad) &&
                            ad.TryGetProperty("displayName", out var dn)) author = dn.GetString() ?? "";
                        if (text.Length > 0) messages.Add(new YouTubeChatMessage(author, text));
                    }
                    catch { }
                }
                return true;
            }
            catch { return false; }
        }
    }

    public sealed class ChatRequestMonitor : IDisposable
    {
        private readonly CompanionServer _server;
        private CancellationTokenSource? _cts;
        private string _twitchStatus = "Off";
        private string _youtubeStatus = "Off";

        public ChatRequestSettings Settings { get; private set; } = ChatRequestSettings.Load();
        public bool IsRunning => _cts != null && !_cts.IsCancellationRequested;
        public string StatusText => "Twitch: " + _twitchStatus + "  •  YouTube: " + _youtubeStatus;
        public event Action? Changed;

        public ChatRequestMonitor(CompanionServer server) { _server = server; }

        public void SaveSettings(string twitchChannel, string youtubeVideoId, string? newYoutubeApiKey)
        {
            Settings.TwitchChannel = (twitchChannel ?? "").Trim().TrimStart('@', '#').ToLowerInvariant();
            Settings.YoutubeVideoId = (youtubeVideoId ?? "").Trim();
            if (newYoutubeApiKey != null) Settings.SetYoutubeApiKey(newYoutubeApiKey);
            Settings.Save();
        }

        public void Start()
        {
            if (IsRunning) return;
            Settings = ChatRequestSettings.Load();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            if (string.IsNullOrWhiteSpace(Settings.TwitchChannel)) _twitchStatus = "No channel set";
            else _ = RunTwitchAsync(Settings.TwitchChannel, ct);
            string apiKey = Settings.GetYoutubeApiKey();
            if (string.IsNullOrWhiteSpace(Settings.YoutubeVideoId) || string.IsNullOrWhiteSpace(apiKey))
                _youtubeStatus = "Needs video ID + API key";
            else _ = RunYouTubeAsync(Settings.YoutubeVideoId, apiKey, ct);
            Changed?.Invoke();
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            try { _cts?.Dispose(); } catch { }
            _cts = null;
            _twitchStatus = "Off";
            _youtubeStatus = "Off";
            Changed?.Invoke();
        }

        public void Dispose() => Stop();

        private void SetTwitch(string s) { _twitchStatus = s; Changed?.Invoke(); }
        private void SetYouTube(string s) { _youtubeStatus = s; Changed?.Invoke(); }

        private async Task RunTwitchAsync(string channel, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    SetTwitch("Connecting…");
                    using var tcp = new TcpClient();
                    await tcp.ConnectAsync("irc.chat.twitch.tv", 6667, ct);
                    using var reader = new StreamReader(tcp.GetStream(), Encoding.UTF8);
                    using var writer = new StreamWriter(tcp.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
                    await writer.WriteLineAsync("PASS SCHMOOPIIE");
                    await writer.WriteLineAsync("NICK justinfan" + Random.Shared.Next(10000, 99999));
                    await writer.WriteLineAsync("JOIN #" + channel);
                    SetTwitch("Watching #" + channel);
                    while (!ct.IsCancellationRequested)
                    {
                        string? line = await reader.ReadLineAsync(ct);
                        if (line == null) break;
                        if (line.StartsWith("PING", StringComparison.Ordinal))
                        {
                            await writer.WriteLineAsync("PONG :tmi.twitch.tv");
                            continue;
                        }
                        if (TwitchIrc.TryParsePrivMsg(line, out string user, out string msg) &&
                            ChatCommand.TryParseSongRequest(msg, out string song))
                        {
                            string verdict = _server.AddRequest(user, song, "", "twitch:" + user.ToLowerInvariant(), "Twitch");
                            if (verdict == "") SetTwitch("Watching #" + channel + " — last: " + song);
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { SetTwitch("Reconnecting (" + ex.Message + ")"); }
                try { await Task.Delay(5000, ct); } catch { break; }
            }
            if (!ct.IsCancellationRequested) SetTwitch("Off");
        }

        private async Task RunYouTubeAsync(string videoId, string apiKey, CancellationToken ct)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
                SetYouTube("Finding the live chat…");
                string resolveUrl = "https://www.googleapis.com/youtube/v3/videos?part=liveStreamingDetails&id=" +
                    Uri.EscapeDataString(videoId) + "&key=" + Uri.EscapeDataString(apiKey);
                string resolveJson = await http.GetStringAsync(resolveUrl, ct);
                string liveChatId = "";
                using (var doc = JsonDocument.Parse(resolveJson))
                {
                    if (doc.RootElement.TryGetProperty("items", out var items) && items.GetArrayLength() > 0 &&
                        items[0].TryGetProperty("liveStreamingDetails", out var lsd) &&
                        lsd.TryGetProperty("activeLiveChatId", out var alc))
                        liveChatId = alc.GetString() ?? "";
                }
                if (string.IsNullOrWhiteSpace(liveChatId))
                {
                    SetYouTube("No active live chat on that video (is it live?)");
                    return;
                }
                SetYouTube("Watching live chat");
                string? pageToken = null;
                bool baseline = true;
                while (!ct.IsCancellationRequested)
                {
                    string url = "https://www.googleapis.com/youtube/v3/liveChat/messages?liveChatId=" +
                        Uri.EscapeDataString(liveChatId) + "&part=snippet,authorDetails&key=" + Uri.EscapeDataString(apiKey) +
                        (pageToken != null ? "&pageToken=" + Uri.EscapeDataString(pageToken) : "");
                    string json = await http.GetStringAsync(url, ct);
                    if (!YouTubeChat.TryParsePage(json, out string next, out int pollMs, out var msgs))
                    {
                        await Task.Delay(6000, ct);
                        continue;
                    }
                    pageToken = next;
                    if (!baseline)
                    {
                        foreach (var m in msgs)
                        {
                            if (ChatCommand.TryParseSongRequest(m.Text, out string song))
                            {
                                string author = string.IsNullOrWhiteSpace(m.Author) ? "YouTube viewer" : m.Author;
                                string verdict = _server.AddRequest(author, song, "", "youtube:" + author.ToLowerInvariant(), "YouTube");
                                if (verdict == "") SetYouTube("Watching live chat — last: " + song);
                            }
                        }
                    }
                    baseline = false;
                    await Task.Delay(pollMs, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested) SetYouTube("Stopped (" + ex.Message + ")");
            }
        }
    }
}
