using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using NAudio.Wave;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// Pass-through that, while armed, copies everything passing through
    /// it to a named pipe as 16-bit PCM for the streaming encoder.
    /// Same shape as RecordingSampleProvider: Read only queues a copy;
    /// a background thread owns the pipe I/O, and a backed-up queue
    /// drops blocks instead of ever stalling the audio callback.
    /// </summary>
    public sealed class StreamAudioSink : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly ConcurrentQueue<float[]> _pending = new ConcurrentQueue<float[]>();
        private Thread? _writerThread;
        private volatile bool _armed;
        private volatile bool _stopRequested;
        private NamedPipeServerStream? _pipe;

        public StreamAudioSink(ISampleProvider source)
        {
            _source = source;
            WaveFormat = source.WaveFormat;
        }

        public WaveFormat WaveFormat { get; }
        public bool IsArmed => _armed;

        public void Arm(NamedPipeServerStream pipe)
        {
            Disarm();
            _pipe = pipe;
            while (_pending.TryDequeue(out _)) { }
            _stopRequested = false;
            _armed = true;
            _writerThread = new Thread(WriterLoop) { IsBackground = true, Name = "StreamAudioPipe" };
            _writerThread.Start();
        }

        public void Disarm()
        {
            if (!_armed) return;
            _stopRequested = true;
            try { _writerThread?.Join(1500); } catch { }
            _armed = false;
            _pipe = null;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            if (_armed && read > 0 && _pending.Count < 64)
            {
                try
                {
                    var block = new float[read];
                    Array.Copy(buffer, offset, block, 0, read);
                    _pending.Enqueue(block);
                }
                catch { }
            }
            return read;
        }

        private void WriterLoop()
        {
            var pipe = _pipe;
            if (pipe == null) return;
            try { pipe.WaitForConnection(); }
            catch { return; }
            var bytes = new byte[8192 * 4];
            while (!_stopRequested)
            {
                if (_pending.TryDequeue(out var block))
                {
                    int n = block.Length;
                    if (bytes.Length < n * 2) bytes = new byte[n * 2];
                    for (int i = 0; i < n; i++)
                    {
                        float v = block[i];
                        if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                        short s16 = (short)(v * 32767f);
                        bytes[i * 2] = (byte)(s16 & 0xFF);
                        bytes[i * 2 + 1] = (byte)((s16 >> 8) & 0xFF);
                    }
                    try { pipe.Write(bytes, 0, n * 2); }
                    catch { return; }   // encoder gone: stop quietly
                }
                else Thread.Sleep(3);
            }
        }
    }

    /// <summary>Screen-region capture via raw GDI (no System.Drawing
    /// dependency): BitBlt/StretchBlt from the screen into a top-down
    /// 32-bit DIB, bytes ready for ffmpeg's rawvideo bgra input.</summary>
    internal static class GdiCapture
    {
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hDC, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDC, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool StretchBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest, IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, int rop);
        [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr hDC, int mode);

        private const int SRCCOPY = 0x00CC0020;
        private const int HALFTONE = 4;

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public uint bmiColors0;
            public uint bmiColors1;
            public uint bmiColors2;
        }

        public static bool CaptureRegionBgra(int x, int y, int w, int h, int outW, int outH, byte[] buffer)
        {
            if (buffer.Length < outW * outH * 4) return false;
            IntPtr screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return false;
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr dib = IntPtr.Zero;
            IntPtr old = IntPtr.Zero;
            try
            {
                var bmi = new BITMAPINFO();
                bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
                bmi.bmiHeader.biWidth = outW;
                bmi.bmiHeader.biHeight = -outH;      // negative = top-down rows
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = 32;
                dib = CreateDIBSection(memDc, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
                if (dib == IntPtr.Zero || bits == IntPtr.Zero) return false;
                old = SelectObject(memDc, dib);
                SetStretchBltMode(memDc, HALFTONE);
                if (!StretchBlt(memDc, 0, 0, outW, outH, screenDc, x, y, w, h, SRCCOPY)) return false;
                Marshal.Copy(bits, buffer, 0, outW * outH * 4);
                return true;
            }
            catch { return false; }
            finally
            {
                if (old != IntPtr.Zero) SelectObject(memDc, old);
                if (dib != IntPtr.Zero) DeleteObject(dib);
                if (memDc != IntPtr.Zero) DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }

    /// <summary>Stream settings + DPAPI-encrypted stream keys, kept in
    /// stream-settings.json in the app data folder (same pattern as the
    /// other key stores in this app).</summary>
    public sealed class StreamSettings
    {
        public string Destination { get; set; } = "YouTube";
        public string CustomUrl { get; set; } = "";
        public string TikTokUrl { get; set; } = "";
        public bool Quality720 { get; set; } = true;
        public Dictionary<string, string> Keys { get; set; } = new Dictionary<string, string>();

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BeatSyncStudioPro", "stream-settings.json");

        public static StreamSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = JsonSerializer.Deserialize<StreamSettings>(File.ReadAllText(FilePath));
                    if (s != null) return s;
                }
            }
            catch { }
            return new StreamSettings();
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

        public string GetKey(string destination)
        {
            try
            {
                if (Keys.TryGetValue(destination, out var b64) && !string.IsNullOrEmpty(b64))
                {
                    var bytes = ProtectedData.Unprotect(Convert.FromBase64String(b64), null, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(bytes);
                }
            }
            catch { }
            return "";
        }

        public void SetKey(string destination, string key)
        {
            if (string.IsNullOrWhiteSpace(key)) { Keys.Remove(destination); return; }
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), null, DataProtectionScope.CurrentUser);
            Keys[destination] = Convert.ToBase64String(bytes);
        }
    }

    /// <summary>
    /// The live-streaming engine (v3.88): pushes the Output (visuals)
    /// window + the DJ master audio to an RTMP ingest (YouTube /
    /// Facebook / TikTok / custom) through a child ffmpeg process.
    /// Video: GDI screen capture of the Output window's screen region,
    /// scaled to the target size, written as raw BGRA to ffmpeg's stdin.
    /// Audio: the StreamAudioSink named pipe. ffmpeg does the H.264/AAC
    /// encode and the RTMP push. If ffmpeg exits unexpectedly, Failed
    /// fires with the tail of its log.
    /// </summary>
    public sealed class StreamEngine
    {
        public const string PipeName = "BeatSyncStreamAudio";

        private readonly StreamAudioSink _sink;
        private readonly Func<(bool Ok, int X, int Y, int W, int H)> _rectProvider;

        private Process? _ffmpeg;
        private NamedPipeServerStream? _pipe;
        private Thread? _captureThread;
        private volatile bool _capturing;
        private string _lastLogLine = "";

        public StreamEngine(StreamAudioSink sink, Func<(bool Ok, int X, int Y, int W, int H)> rectProvider)
        {
            _sink = sink;
            _rectProvider = rectProvider;
        }

        /// <summary>Set by the DJ window: brings the Output window up
        /// (called on the UI thread by the provider's owner).</summary>
        public Action? EnsureOutputSurface { get; set; }

        public bool IsLive { get; private set; }
        public DateTime LiveSinceUtc { get; private set; }
        public long FramesSent { get; private set; }
        public long FramesDropped { get; private set; }
        public string? LastError { get; internal set; }
        public event Action<string>? Failed;

        public static string LogPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BeatSyncStudioPro", "stream-log.txt");

        public static string? FindFfmpeg()
        {
            var candidates = new List<string>
            {
                Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "BeatSyncTools", "ffmpeg.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BeatSyncStudioPro", "tools", "ffmpeg.exe")
            };
            foreach (var c in candidates)
            {
                try { if (File.Exists(c)) return c; } catch { }
            }
            try
            {
                var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var dir in pathVar.Split(';'))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    var c = Path.Combine(dir.Trim(), "ffmpeg.exe");
                    if (File.Exists(c)) return c;
                }
            }
            catch { }
            return null;
        }

        public static string ToolsDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BeatSyncStudioPro", "tools");

        /// <summary>Start streaming. Returns null on success, else a
        /// plain-language error. Blocks briefly while the output
        /// surface and ffmpeg come up — call from a background thread.</summary>
        public string? Start(string fullUrl, int outW, int outH, int fps, int videoBitrateKbps)
        {
            if (IsLive) return "Already live.";
            LastError = null;
            if (!fullUrl.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase) &&
                !fullUrl.StartsWith("rtmps://", StringComparison.OrdinalIgnoreCase))
                return "The stream address must start with rtmp:// or rtmps://.";

            var ffmpeg = FindFfmpeg();
            if (ffmpeg == null)
                return "ffmpeg isn't installed yet — use the Download ffmpeg button in this window first.";

            var rect = _rectProvider();
            if (!rect.Ok)
            {
                try { EnsureOutputSurface?.Invoke(); } catch { }
                Thread.Sleep(1200);
                rect = _rectProvider();
                if (!rect.Ok)
                    return "The Output (visuals) window isn't open. Open it from the main window (Output button), then Go Live again.";
            }

            try
            {
                _pipe = new NamedPipeServerStream(PipeName, PipeDirection.Out, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                _sink.Arm(_pipe);

                var psi = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardError = true
                };
                var fmt = _sink.WaveFormat;
                psi.ArgumentList.Add("-hide_banner");
                psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("info");
                psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("rawvideo");
                psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("bgra");
                psi.ArgumentList.Add("-s"); psi.ArgumentList.Add($"{outW}x{outH}");
                psi.ArgumentList.Add("-r"); psi.ArgumentList.Add(fps.ToString());
                psi.ArgumentList.Add("-i"); psi.ArgumentList.Add("pipe:0");
                psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("s16le");
                psi.ArgumentList.Add("-ar"); psi.ArgumentList.Add(fmt.SampleRate.ToString());
                psi.ArgumentList.Add("-ac"); psi.ArgumentList.Add(fmt.Channels.ToString());
                psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(@"\\.\pipe\" + PipeName);
                psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("libx264");
                psi.ArgumentList.Add("-preset"); psi.ArgumentList.Add("veryfast");
                psi.ArgumentList.Add("-tune"); psi.ArgumentList.Add("zerolatency");
                psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("yuv420p");
                psi.ArgumentList.Add("-b:v"); psi.ArgumentList.Add($"{videoBitrateKbps}k");
                psi.ArgumentList.Add("-maxrate"); psi.ArgumentList.Add($"{videoBitrateKbps}k");
                psi.ArgumentList.Add("-bufsize"); psi.ArgumentList.Add($"{videoBitrateKbps * 2}k");
                psi.ArgumentList.Add("-g"); psi.ArgumentList.Add((fps * 2).ToString());
                psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("aac");
                psi.ArgumentList.Add("-b:a"); psi.ArgumentList.Add("160k");
                psi.ArgumentList.Add("-ar"); psi.ArgumentList.Add("44100");
                psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("flv");
                psi.ArgumentList.Add(fullUrl);

                _ffmpeg = new Process { StartInfo = psi, EnableRaisingEvents = true };
                _ffmpeg.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data == null) return;
                    _lastLogLine = e.Data;
                    try { File.AppendAllText(LogPath, e.Data + Environment.NewLine); } catch { }
                };
                _ffmpeg.Exited += (s, e) =>
                {
                    if (IsLive && !_capturing) return;      // our own Stop path
                    if (IsLive)
                    {
                        string tail = _lastLogLine;
                        CleanupAfterExit();
                        Failed?.Invoke(string.IsNullOrWhiteSpace(tail)
                            ? "ffmpeg stopped unexpectedly."
                            : "ffmpeg stopped: " + tail);
                    }
                };
                try { File.WriteAllText(LogPath, ""); } catch { }
                _ffmpeg.Start();
                _ffmpeg.BeginErrorReadLine();

                IsLive = true;
                LiveSinceUtc = DateTime.UtcNow;
                FramesSent = 0;
                FramesDropped = 0;
                _capturing = true;
                _captureThread = new Thread(() => CaptureLoop(outW, outH, fps))
                { IsBackground = true, Name = "StreamCapture" };
                _captureThread.Start();
                return null;
            }
            catch (Exception ex)
            {
                CleanupAfterExit();
                return "Could not start the stream: " + ex.Message;
            }
        }

        public void Stop()
        {
            _capturing = false;
            try { _captureThread?.Join(1200); } catch { }
            _captureThread = null;
            try { _ffmpeg?.StandardInput.Close(); } catch { }
            try
            {
                if (_ffmpeg != null && !_ffmpeg.HasExited)
                {
                    if (!_ffmpeg.WaitForExit(2500))
                    {
                        try { _ffmpeg.Kill(); } catch { }
                    }
                }
            }
            catch { }
            CleanupAfterExit();
        }

        private void CleanupAfterExit()
        {
            IsLive = false;
            _capturing = false;
            try { _sink.Disarm(); } catch { }
            try { _pipe?.Dispose(); } catch { }
            _pipe = null;
            try { _ffmpeg?.Dispose(); } catch { }
            _ffmpeg = null;
        }

        private void CaptureLoop(int outW, int outH, int fps)
        {
            var buffer = new byte[outW * outH * 4];
            var stdin = _ffmpeg?.StandardInput.BaseStream;
            if (stdin == null) { CleanupAfterExit(); return; }
            long intervalMs = Math.Max(1, 1000 / fps);
            var sw = Stopwatch.StartNew();
            long nextTick = 0;
            while (_capturing && IsLive)
            {
                long now = sw.ElapsedMilliseconds;
                if (now < nextTick)
                {
                    Thread.Sleep((int)Math.Min(nextTick - now, intervalMs));
                    continue;
                }
                nextTick = now + intervalMs;
                var rect = _rectProvider();
                if (!rect.Ok) { FramesDropped++; continue; }
                if (!GdiCapture.CaptureRegionBgra(rect.X, rect.Y, rect.W, rect.H, outW, outH, buffer))
                {
                    FramesDropped++;
                    continue;
                }
                try
                {
                    stdin.Write(buffer, 0, buffer.Length);
                    FramesSent++;
                }
                catch
                {
                    // ffmpeg died mid-write; its Exited handler reports it
                    return;
                }
                if (sw.ElapsedMilliseconds - now > intervalMs * 2) FramesDropped++;
            }
        }
    }
}
