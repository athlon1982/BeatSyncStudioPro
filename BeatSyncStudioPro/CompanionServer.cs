using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// The phone companion server (v3.32): a minimal TcpListener HTTP server
    /// inside the DJ window serving one mobile control page plus JSON
    /// state/command endpoints. The same server answers on the LAN address
    /// (Wi-Fi door) and the machine's Tailscale address (remote door).
    /// A password set in the app gates everything: salted PBKDF2-SHA256 at
    /// rest, token after sign-in, per-IP lockout after repeated failures.
    /// Commands are handed to the DJ window (event) which applies them on
    /// its UI thread against the real controls; state comes from a
    /// provider the window refreshes every tick.
    /// </summary>
    public sealed class CompanionServer
    {
        public sealed class Settings
        {
            public int Port { get; set; } = 8765;
            public string? SaltB64 { get; set; }
            public string? HashB64 { get; set; }
        }

        private static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BeatSyncStudioPro", "companion.json");

        public static Settings LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath));
                    if (s != null) return s;
                }
            }
            catch { }
            return new Settings();
        }

        public static void SaveSettings(Settings s)
        {
            try
            {
                var dir = Path.GetDirectoryName(SettingsPath);
                if (dir != null) Directory.CreateDirectory(dir);
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s));
            }
            catch { }
        }

        private const int MaxBodyBytes = 65536; // 64 KB: real phone requests are tiny JSON, so cap every request body

        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private Settings? _settingsCache;
        private readonly Dictionary<string, DateTime> _tokens = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, (int Fails, DateTime LockedUntil, int Strikes)> _authFails = new Dictionary<string, (int, DateTime, int)>();
        private readonly object _authLock = new object();

        public bool IsRunning => _listener != null;
        public int Port { get; private set; }
        public string? LastError { get; private set; }
        public bool PasswordRequired => CachedSettings().HashB64 != null;
        public int SessionCount { get { lock (_authLock) return _tokens.Count; } }

        private Settings CachedSettings()
        {
            lock (_authLock)
            {
                _settingsCache ??= LoadSettings();
                return _settingsCache;
            }
        }

        public event Action<string>? CommandReceived;
        public Func<string>? StateJsonProvider;

        // ---------- listener requests (v3.87) ----------
        // Public song requests for the radio: anyone who can reach the
        // /request page can send one; they land here (persisted to
        // requests.json) and the DJ window shows them on its Requests
        // button + window, and the phone page lists them read-only.

        public sealed class SongRequest
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
            public string Song { get; set; } = "";
            public string Shoutout { get; set; } = "";
            public DateTime WhenUtc { get; set; }
        }

        private static string RequestsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BeatSyncStudioPro", "requests.json");

        private readonly object _reqLock = new object();
        private readonly List<SongRequest> _requests = new List<SongRequest>();
        private readonly Dictionary<string, DateTime> _reqLastByIp = new Dictionary<string, DateTime>();
        private int _nextReqId = 1;
        private bool _requestsLoaded;

        private void EnsureRequestsLoaded()
        {
            lock (_reqLock)
            {
                if (_requestsLoaded) return;
                _requestsLoaded = true;
                try
                {
                    if (File.Exists(RequestsPath))
                    {
                        var list = JsonSerializer.Deserialize<List<SongRequest>>(File.ReadAllText(RequestsPath));
                        if (list != null)
                        {
                            _requests.AddRange(list);
                            foreach (var r in _requests) if (r.Id >= _nextReqId) _nextReqId = r.Id + 1;
                        }
                    }
                }
                catch { }
            }
        }

        private void SaveRequestsLocked()
        {
            try
            {
                var dir = Path.GetDirectoryName(RequestsPath);
                if (dir != null) Directory.CreateDirectory(dir);
                File.WriteAllText(RequestsPath, JsonSerializer.Serialize(_requests));
            }
            catch { }
        }

        public int PendingRequestCount
        {
            get { EnsureRequestsLoaded(); lock (_reqLock) { return _requests.Count; } }
        }

        public List<SongRequest> SnapshotRequests()
        {
            EnsureRequestsLoaded();
            lock (_reqLock) { return new List<SongRequest>(_requests); }
        }

        public void DismissRequest(int id)
        {
            EnsureRequestsLoaded();
            lock (_reqLock)
            {
                _requests.RemoveAll(r => r.Id == id);
                SaveRequestsLocked();
            }
        }

        public void ClearRequests()
        {
            EnsureRequestsLoaded();
            lock (_reqLock)
            {
                _requests.Clear();
                SaveRequestsLocked();
            }
        }

        /// <summary>Store a listener request. Returns "" when stored,
        /// else a short reason: "rate", "full", or "empty".</summary>
        public string AddRequest(string name, string song, string shoutout, string remoteIp)
        {
            EnsureRequestsLoaded();
            lock (_reqLock)
            {
                if (_reqLastByIp.TryGetValue(remoteIp, out var last) && (DateTime.UtcNow - last).TotalSeconds < 15)
                    return "rate";
                if (_requests.Count >= 100) return "full";
                if (string.IsNullOrWhiteSpace(song)) return "empty";
                _reqLastByIp[remoteIp] = DateTime.UtcNow;
                _requests.Add(new SongRequest
                {
                    Id = _nextReqId++,
                    Name = name.Trim(),
                    Song = song.Trim(),
                    Shoutout = shoutout.Trim(),
                    WhenUtc = DateTime.UtcNow
                });
                SaveRequestsLocked();
                return "";
            }
        }


        // ---------- password ----------

        public void SetPassword(string? password)
        {
            var s = LoadSettings();
            if (string.IsNullOrEmpty(password))
            {
                s.SaltB64 = null;
                s.HashB64 = null;
            }
            else
            {
                byte[] salt = RandomNumberGenerator.GetBytes(16);
                byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
                s.SaltB64 = Convert.ToBase64String(salt);
                s.HashB64 = Convert.ToBase64String(hash);
            }
            SaveSettings(s);
            lock (_authLock) { _settingsCache = s; _tokens.Clear(); }   // changing the password signs every phone out
        }

        private bool CheckPassword(string password)
        {
            var s = CachedSettings();
            if (s.HashB64 == null || s.SaltB64 == null) return true;
            byte[] salt = Convert.FromBase64String(s.SaltB64);
            byte[] expected = Convert.FromBase64String(s.HashB64);
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }

        private bool IsAuthorized(string? token)
        {
            if (!PasswordRequired) return true;
            if (string.IsNullOrEmpty(token)) return false;
            lock (_authLock)
            {
                if (!_tokens.TryGetValue(token!, out var expiry)) return false;
                if (expiry <= DateTime.UtcNow) { _tokens.Remove(token!); return false; }
                return true;
            }
        }

        // ---------- lifecycle ----------

        public void Start(int port)
        {
            Stop();
            LastError = null;
            try
            {
                _cts = new CancellationTokenSource();
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start();
                Port = port;
                var token = _cts.Token;
                _ = Task.Run(() => AcceptLoop(token), token);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _listener = null;
            }
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            _listener = null;
            lock (_authLock) { _tokens.Clear(); _authFails.Clear(); }
            try { _cts?.Dispose(); } catch { }
            _cts = null;
        }

        private async Task AcceptLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _listener != null)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(token); }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        LastError = "Companion listener stopped: " + ex.Message;
                        _listener = null;
                    }
                    break;
                }
                _ = Task.Run(() => HandleClient(client));
            }
        }

        // ---------- HTTP ----------

        private void HandleClient(TcpClient client)
        {
            try
            {
                client.ReceiveTimeout = 10000;
                client.SendTimeout = 10000;
                using (client)
                using (var stream = client.GetStream())
                {
                    string remoteIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
                    var (tooLarge, method, path, headers, body) = ReadRequest(stream);
                    if (method == null) return;
                    if (tooLarge) { Respond(stream, 413, "text/plain", "payload too large"); return; }
                    headers.TryGetValue("x-companion-token", out string? token);

                    if (method == "GET" && path == "/")
                    {
                        bool open = IsAuthorized(token);
                        Respond(stream, 200, "text/html; charset=utf-8", open ? AppPage : LockPage);
                    }
                    else if (method == "GET" && path == "/favicon.ico")
                    {
                        Respond(stream, 204, "text/plain", "");
                    }
                    else if (method == "GET" && path == "/request")
                    {
                        Respond(stream, 200, "text/html; charset=utf-8", RequestPage);
                    }
                    else if (method == "POST" && path == "/api/request")
                    {
                        HandleSongRequest(stream, body, remoteIp);
                    }
                    else if (method == "POST" && path == "/api/auth")
                    {
                        HandleAuth(stream, body, remoteIp);
                    }
                    else if (method == "GET" && path == "/api/state")
                    {
                        if (!IsAuthorized(token)) { Respond(stream, 401, "application/json", "{\"error\":\"unauthorized\"}"); return; }
                        Respond(stream, 200, "application/json", StateJsonProvider?.Invoke() ?? "{}");
                    }
                    else if (method == "POST" && path == "/api/cmd")
                    {
                        if (!IsAuthorized(token)) { Respond(stream, 401, "application/json", "{\"error\":\"unauthorized\"}"); return; }
                        CommandReceived?.Invoke(body);
                        Respond(stream, 200, "application/json", "{\"ok\":true}");
                    }
                    else
                    {
                        Respond(stream, 404, "text/plain", "not found");
                    }
                }
            }
            catch { /* a dropped phone connection is not an app problem */ }
        }

        private void HandleSongRequest(NetworkStream stream, string body, string remoteIp)
        {
            string name = "", song = "", shoutout = "";
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("name", out var n)) name = n.GetString() ?? "";
                if (root.TryGetProperty("song", out var sg)) song = sg.GetString() ?? "";
                if (root.TryGetProperty("shoutout", out var so)) shoutout = so.GetString() ?? "";
            }
            catch { }
            if (name.Length > 60) name = name[..60];
            if (song.Length > 120) song = song[..120];
            if (shoutout.Length > 200) shoutout = shoutout[..200];
            string verdict = AddRequest(name, song, shoutout, remoteIp);
            if (verdict == "")
                Respond(stream, 200, "application/json", "{\"ok\":true}");
            else if (verdict == "rate")
                Respond(stream, 429, "application/json", "{\"error\":\"too fast - give it a few seconds and try again\"}");
            else if (verdict == "full")
                Respond(stream, 429, "application/json", "{\"error\":\"the request list is full right now\"}");
            else
                Respond(stream, 400, "application/json", "{\"error\":\"tell us what song you want to hear\"}");
        }

        private void HandleAuth(NetworkStream stream, string body, string remoteIp)
        {
            int lockedSecs = 0;
            lock (_authLock)
            {
                if (_authFails.TryGetValue(remoteIp, out var rec) && rec.LockedUntil > DateTime.UtcNow)
                    lockedSecs = (int)(rec.LockedUntil - DateTime.UtcNow).TotalSeconds + 1;
            }
            if (lockedSecs > 0)
            {
                Respond(stream, 429, "application/json", $"{{\"error\":\"locked\",\"seconds\":{lockedSecs}}}");
                return;
            }
            string password = "";
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("password", out var p)) password = p.GetString() ?? "";
            }
            catch { }
            if (CheckPassword(password))
            {
                string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                lock (_authLock) { _tokens[token] = DateTime.UtcNow.AddHours(12); _authFails.Remove(remoteIp); }
                Respond(stream, 200, "application/json", $"{{\"token\":\"{token}\"}}");
            }
            else
            {
                lock (_authLock)
                {
                    (int Fails, DateTime LockedUntil, int Strikes) rec = _authFails.TryGetValue(remoteIp, out var r) ? r : (0, DateTime.MinValue, 0);
                    rec.Fails++;
                    if (rec.Fails >= 5)
                    {
                        rec.Strikes++;
                        int lockSeconds = Math.Min(30 * (1 << Math.Min(rec.Strikes - 1, 5)), 900);
                        rec.LockedUntil = DateTime.UtcNow.AddSeconds(lockSeconds);
                        rec.Fails = 0;
                    }
                    _authFails[remoteIp] = rec;
                }
                Respond(stream, 401, "application/json", "{\"error\":\"wrong password\"}");
            }
        }

        private static (bool TooLarge, string? Method, string Path, Dictionary<string, string> Headers, string Body) ReadRequest(NetworkStream stream)
        {
            var headerBytes = new List<byte>();
            var one = new byte[1];
            int matched = 0;
            byte[] terminator = { 13, 10, 13, 10 };
            while (headerBytes.Count < 65536)
            {
                int n = stream.Read(one, 0, 1);
                if (n <= 0) return (false, null, "", new Dictionary<string, string>(), "");
                headerBytes.Add(one[0]);
                matched = one[0] == terminator[matched] ? matched + 1 : (one[0] == terminator[0] ? 1 : 0);
                if (matched == 4) break;
            }
            string headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
            var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var first = lines[0].Split(' ');
            if (first.Length < 2) return (false, null, "", new Dictionary<string, string>(), "");
            string method = first[0], path = first[1];
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int contentLength = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                string k = lines[i][..colon].Trim(), v = lines[i][(colon + 1)..].Trim();
                headers[k] = v;
                if (k.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) int.TryParse(v, out contentLength);
            }
            if (contentLength < 0) contentLength = 0;
            if (contentLength > MaxBodyBytes)
                return (true, method, path, headers, "");
            string body = "";
            if (contentLength > 0)
            {
                var buf = new byte[contentLength];
                int read = 0;
                while (read < contentLength)
                {
                    int n = stream.Read(buf, read, contentLength - read);
                    if (n <= 0) break;
                    read += n;
                }
                body = Encoding.UTF8.GetString(buf, 0, read);
            }
            int q = path.IndexOf('?');
            if (q >= 0) path = path[..q];
            return (false, method, path, headers, body);
        }

        private static void Respond(NetworkStream stream, int status, string contentType, string body)
        {
            byte[] payload = Encoding.UTF8.GetBytes(body);
            string head = $"HTTP/1.1 {status} OK\r\nContent-Type: {contentType}\r\nContent-Length: {payload.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n";
            byte[] headBytes = Encoding.ASCII.GetBytes(head);
            stream.Write(headBytes, 0, headBytes.Length);
            if (payload.Length > 0) stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }

        // ---------- pages ----------

        private const string LockPage = """
<!DOCTYPE html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>BeatSync Companion</title><style>
body{background:#0B0E14;color:#fff;font-family:system-ui,sans-serif;margin:0;padding:24px;text-align:center}
h1{font-size:22px} .box{max-width:340px;margin:40px auto;background:#12161F;border-radius:14px;padding:24px}
input{width:100%;box-sizing:border-box;padding:12px;font-size:17px;border-radius:8px;border:1px solid #2A3446;background:#0D1420;color:#fff;margin:12px 0}
button{width:100%;padding:13px;font-size:17px;font-weight:700;border:0;border-radius:8px;background:#1DB954;color:#fff;cursor:pointer}
#err{color:#E8C766;min-height:20px;font-size:14px}
</style></head><body><div class="box"><h1>&#x1F3A7; BeatSync Companion</h1>
<p style="color:#A0A5B5">This remote is password protected.<br>Enter the password set in the DJ app.</p>
<input id="pw" type="password" autocomplete="current-password" placeholder="Password">
<div id="err"></div><button onclick="go()">Unlock</button></div>
<script>
async function go(){
  const pw=document.getElementById('pw').value, err=document.getElementById('err');
  const r=await fetch('/api/auth',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({password:pw})});
  const j=await r.json().catch(()=>({}));
  if(r.ok&&j.token){localStorage.setItem('ct',j.token);location.reload();}
  else if(r.status===429){err.textContent='Too many tries - locked for '+j.seconds+'s.';}
  else{err.textContent='Wrong password.';}
}
document.getElementById('pw').addEventListener('keydown',e=>{if(e.key==='Enter')go();});
</script></body></html>
""";

        private const string RequestPage = """
<!DOCTYPE html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Rad-Dad Radio - Request a Song</title><style>
body{background:#0B0E14;color:#fff;font-family:system-ui,sans-serif;margin:0;padding:24px}
h1{font-size:22px;text-align:center} .box{max-width:360px;margin:20px auto;background:#12161F;border-radius:14px;padding:24px}
label{color:#A0A5B5;font-size:13px;display:block;margin:14px 0 6px}
input,textarea{width:100%;box-sizing:border-box;padding:12px;font-size:16px;border-radius:8px;border:1px solid #2A3446;background:#0D1420;color:#fff;font-family:inherit}
button{width:100%;padding:13px;font-size:17px;font-weight:700;border:0;border-radius:8px;background:#1DB954;color:#fff;cursor:pointer;margin-top:18px}
#msg{min-height:22px;font-size:15px;margin-top:14px;text-align:center}
.sub{color:#A0A5B5;text-align:center;margin-top:-6px;font-size:14px}
</style></head><body><div class="box"><h1>&#x1F3A7; Rad-Dad Radio</h1>
<p class="sub">Request a song or send a shoutout.<br>The DJ sees it live in the studio.</p>
<label>Your name (optional)</label><input id="nm" maxlength="60" placeholder="e.g. Sarah from Rutland">
<label>Song request *</label><input id="sg" maxlength="120" placeholder="Artist - Song title">
<label>Shoutout / dedication (optional)</label><textarea id="so" maxlength="200" rows="2" placeholder="Say it here..."></textarea>
<button onclick="go()">Send Request</button><div id="msg"></div></div>
<script>
async function go(){
  const msg=document.getElementById('msg');
  const song=document.getElementById('sg').value.trim();
  if(!song){msg.style.color='#E8C766';msg.textContent='Tell us what song you want to hear.';return;}
  try{
    const r=await fetch('/api/request',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({name:document.getElementById('nm').value,song:song,shoutout:document.getElementById('so').value})});
    const j=await r.json().catch(()=>({}));
    if(r.ok){msg.style.color='#1DB954';msg.textContent='Got it! Your request is with the DJ.';document.getElementById('sg').value='';document.getElementById('so').value='';}
    else{msg.style.color='#E8C766';msg.textContent=j.error||'Something went wrong - try again.';}
  }catch(e){msg.style.color='#E8C766';msg.textContent='Could not reach the station - try again.';}
}
</script></body></html>
""";

        private const string AppPage = """
<!DOCTYPE html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>BeatSync Companion</title><style>
body{background:#0B0E14;color:#fff;font-family:system-ui,sans-serif;margin:0;padding:12px}
h1{font-size:17px;text-align:center;margin:4px 0 10px}
.deck{background:#12161F;border-radius:12px;padding:12px;margin-bottom:10px}
.deck .name{font-weight:800;font-size:13px;letter-spacing:1px}
.deck .title{font-size:15px;margin:4px 0;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.deck .meta{color:#A0A5B5;font-size:12px}
.row{display:flex;gap:8px;margin-top:8px}
button{flex:1;padding:12px;font-size:15px;font-weight:700;border:0;border-radius:8px;background:#2A303C;color:#fff;cursor:pointer}
button.on{background:#1DB954}
button.live{background:#C0392B}
.card{background:#12161F;border-radius:12px;padding:12px;margin-bottom:10px}
.lbl{color:#A0A5B5;font-size:12px;margin-bottom:6px}
input[type=range]{width:100%}
.pads{display:grid;grid-template-columns:repeat(4,1fr);gap:8px}
.pads button{padding:14px 4px;font-size:13px}
.pads button:active{background:#1DB954}
</style></head><body>
<h1>&#x1F3A7; BeatSync Companion</h1>
<div class="deck"><div class="name" style="color:#1DB954">DECK A</div><div class="title" id="tA">-</div>
<div class="meta"><span id="kA"></span> <span id="tmA"></span></div>
<div class="row"><button id="pA" onclick="cmd('playA')">&#x25B6;&#xFE0F; Play</button><button onclick="cmd('cueA')">CUE</button></div>
<div class="meta" id="nA" style="margin-top:8px"></div>
<div class="row"><button onclick="cmd('prevA')">&#x23EE;&#xFE0F; Prev</button><button onclick="cmd('nextA')">&#x23ED;&#xFE0F; Next</button></div></div>
<div class="deck"><div class="name" style="color:#B388FF">DECK B</div><div class="title" id="tB">-</div>
<div class="meta"><span id="kB"></span> <span id="tmB"></span></div>
<div class="row"><button id="pB" onclick="cmd('playB')">&#x25B6;&#xFE0F; Play</button><button onclick="cmd('cueB')">CUE</button></div>
<div class="meta" id="nB" style="margin-top:8px"></div>
<div class="row"><button onclick="cmd('prevB')">&#x23EE;&#xFE0F; Prev</button><button onclick="cmd('nextB')">&#x23ED;&#xFE0F; Next</button></div></div>
<div class="card"><div class="lbl">Crossfader</div><input type="range" id="xf" min="0" max="1000" value="500" oninput="cmd('xfader',{value:+this.value})">
<div class="lbl" style="margin-top:8px">Master</div><input type="range" id="ms" min="0" max="125" value="90" oninput="cmd('master',{value:+this.value})"></div>
<div class="card"><div class="row" style="margin-top:0"><button id="cr" onclick="cmd('cruise')">&#x1F6A2; Cruise</button><button id="mc" onclick="cmd('mic')">&#x1F3A4; Mic</button></div></div>
<div class="card"><div class="lbl">Sampler</div><div class="pads" id="pads"></div></div>
<div class="card"><div class="lbl">&#x1F4E8; Listener Requests</div><div id="rq"></div></div>
<script>
const tok=()=>localStorage.getItem('ct')||'';
const padNames=['RAD-DAD!','Impact','Riser','Down','Crash','Clap','808','Cowbell'];
const pads=document.getElementById('pads');
padNames.forEach((n,i)=>{const b=document.createElement('button');b.textContent=n;b.onclick=()=>cmd('pad',{index:i});pads.appendChild(b);});
async function cmd(c,extra){
  try{const r=await fetch('/api/cmd',{method:'POST',headers:{'Content-Type':'application/json','X-Companion-Token':tok()},body:JSON.stringify(Object.assign({cmd:c},extra||{}))});
  if(r.status===401){localStorage.removeItem('ct');location.reload();}}catch(e){}
}
function fmt(s){if(!isFinite(s)||s<0)s=0;const m=Math.floor(s/60),x=Math.floor(s%60);return m+':'+String(x).padStart(2,'0');}
let xfBusy=false,msBusy=false;
document.getElementById('xf').addEventListener('pointerdown',()=>xfBusy=true);
document.getElementById('xf').addEventListener('pointerup',()=>xfBusy=false);
document.getElementById('ms').addEventListener('pointerdown',()=>msBusy=true);
document.getElementById('ms').addEventListener('pointerup',()=>msBusy=false);
async function poll(){
  try{const r=await fetch('/api/state',{headers:{'X-Companion-Token':tok()}});
  if(r.status===401){localStorage.removeItem('ct');location.reload();return;}
  const s=await r.json();
  tA.textContent=s.a.title||'(empty)'; tB.textContent=s.b.title||'(empty)';
  kA.textContent=s.a.key||''; kB.textContent=s.b.key||'';
  tmA.textContent=fmt(s.a.pos)+' / '+fmt(s.a.dur); tmB.textContent=fmt(s.b.pos)+' / '+fmt(s.b.dur);
  nA.textContent=s.a.next?('\u23ED Next: '+s.a.next+(s.a.q?'  ('+s.a.q+')':'')):''; nB.textContent=s.b.next?('\u23ED Next: '+s.b.next+(s.b.q?'  ('+s.b.q+')':'')):'';
  pA.innerHTML=s.a.playing?'&#x23F8;&#xFE0F; Pause':'&#x25B6;&#xFE0F; Play'; pA.className=s.a.playing?'on':'';
  pB.innerHTML=s.b.playing?'&#x23F8;&#xFE0F; Pause':'&#x25B6;&#xFE0F; Play'; pB.className=s.b.playing?'on':'';
  if(!xfBusy)xf.value=s.xfader; if(!msBusy)ms.value=s.master;
  cr.className=s.cruise?'on':''; mc.className=s.mic?'live':'';
  const rq=document.getElementById('rq'); rq.innerHTML='';
  if(!s.req||!s.req.length){const d=document.createElement('div');d.className='meta';d.textContent='No requests waiting.';rq.appendChild(d);}
  (s.req||[]).forEach(r=>{const d=document.createElement('div');d.style.marginBottom='6px';const b=document.createElement('b');b.textContent=r.song;d.appendChild(b);if(r.name){const sp=document.createElement('span');sp.className='meta';sp.textContent=' - '+r.name;d.appendChild(sp);}if(r.shoutout){const so=document.createElement('div');so.className='meta';so.textContent='\u201C'+r.shoutout+'\u201D';d.appendChild(so);}rq.appendChild(d);});
  }catch(e){}
  setTimeout(poll,700);
}
poll();
</script></body></html>
""";
    }
}
