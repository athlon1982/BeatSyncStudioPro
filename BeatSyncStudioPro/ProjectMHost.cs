using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// Hosts libprojectM (MilkDrop visuals) inside the Output window.
    /// projectM creates no window of its own: this HwndHost gives it a
    /// child HWND, a WGL OpenGL context on a dedicated render thread, a
    /// PCM feed from the master AudioTap, and a simple preset rotation
    /// (bundled self-authored .milk files + any folders the user imports).
    ///
    /// Everything native happens on the render thread. If the DLL is
    /// missing or projectM fails to start, Failed is set and the Output
    /// window falls back to its built-in FFT scenes — this control then
    /// just stays black and inert.
    /// </summary>
    public sealed class ProjectMHost : HwndHost
    {
        // ---------- Win32 / WGL ----------
        private const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000;
        private const uint CS_OWNDC = 0x0020;
        private const byte PFD_TYPE_RGBA = 0, PFD_MAIN_PLANE = 0;
        private const uint PFD_DOUBLEBUFFER = 0x00000001, PFD_DRAW_TO_WINDOW = 0x00000004, PFD_SUPPORT_OPENGL = 0x00000020;

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        private static WndProcDelegate? _wndProc;
        private static bool _classRegistered;
        private const string ClassName = "BeatSyncProjectMHost";

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            public WndProcDelegate lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct PIXELFORMATDESCRIPTOR
        {
            public ushort nSize;
            public ushort nVersion;
            public uint dwFlags;
            public byte iPixelType;
            public byte cColorBits;
            public byte cRedBits, cRedShift, cGreenBits, cGreenShift, cBlueBits, cBlueShift, cAlphaBits, cAlphaShift;
            public byte cAccumBits, cAccumRedBits, cAccumGreenBits, cAccumBlueBits, cAccumAlphaBits;
            public byte cDepthBits, cStencilBits, cAuxBuffers, iLayerType, bReserved;
            public uint dwLayerMask, dwVisibleMask, dwDamageMask;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
        [DllImport("gdi32.dll", SetLastError = true)] private static extern int ChoosePixelFormat(IntPtr hdc, ref PIXELFORMATDESCRIPTOR pfd);
        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetPixelFormat(IntPtr hdc, int format, ref PIXELFORMATDESCRIPTOR pfd);
        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SwapBuffers(IntPtr hdc);
        // libprojectM links GLEW but (in the 4.0 package) leaves loading the
        // GL function pointers to the host: without glewInit the engine's
        // shader calls silently no-op and every frame renders black.
        [DllImport("glew32.dll", CallingConvention = CallingConvention.Cdecl)] private static extern uint glewInit();

        [DllImport("opengl32.dll", SetLastError = true)] private static extern IntPtr wglCreateContext(IntPtr hdc);
        [DllImport("opengl32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool wglMakeCurrent(IntPtr hdc, IntPtr hglrc);
        [DllImport("opengl32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool wglDeleteContext(IntPtr hglrc);

        // ---------- State ----------
        private IntPtr _hwnd;
        private Thread? _thread;
        private volatile bool _stop;
        private volatile bool _renderEnabled;
        private readonly List<string> _presets = new List<string>();
        private readonly object _presetLock = new object();
        private int _pendingStep; // +1 next / -1 prev, set by UI thread
        private string _initialFolder = "";

        public AudioTapSampleProvider? Tap { get; set; }
        public bool Failed { get; private set; }
        public string FailReason { get; private set; } = "";
        public static string LastPresetError { get; private set; } = "";

        // Diagnostics: engine chatter goes to %AppData%/BeatSyncStudioPro/pm-debug.log
        private static readonly ProjectMNative.PresetSwitchFailedCallback _failedCb = OnPresetSwitchFailed;

        private static void OnPresetSwitchFailed(IntPtr file, IntPtr msg, IntPtr userData)
        {
            try
            {
                string f = file != IntPtr.Zero ? Marshal.PtrToStringAnsi(file) ?? "" : "";
                string m = msg != IntPtr.Zero ? Marshal.PtrToStringAnsi(msg) ?? "" : "";
                LastPresetError = f + ": " + m;
                PmLog("PRESET FAILED: " + LastPresetError);
            }
            catch { }
        }

        internal static void PmLog(string line)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BeatSyncStudioPro");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "pm-debug.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {line}{Environment.NewLine}");
            }
            catch { }
        }
        public bool Started { get; private set; }
        public string CurrentPresetName { get; private set; } = "";
        public string EngineVersion { get; private set; } = "";

        public bool RenderEnabled
        {
            get => _renderEnabled;
            set => _renderEnabled = value;
        }

        /// <summary>Folder scanned for bundled .milk presets at start.</summary>
        public string InitialPresetFolder
        {
            get => _initialFolder;
            set => _initialFolder = value ?? "";
        }

        public int PresetCount { get { lock (_presetLock) return _presets.Count; } }

        public void AddPresetFolder(string folder)
        {
            try
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
                var files = Directory.GetFiles(folder, "*.milk");
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                lock (_presetLock)
                {
                    foreach (var f in files)
                        if (!_presets.Contains(f)) _presets.Add(f);
                }
            }
            catch { }
        }

        public void NextPreset() => Interlocked.Exchange(ref _pendingStep, 1);
        public void PrevPreset() => Interlocked.Exchange(ref _pendingStep, -1);

        // ---------- HwndHost ----------
        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            // .NET 8's HwndHost has only the single-parameter BuildWindowCore
            // (the Rect overload arrives in .NET 9); HwndHost itself keeps
            // the child window sized to this element after creation.
            EnsureClass();
            _hwnd = CreateWindowEx(0, ClassName, "", WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS,
                0, 0, Math.Max(64, (int)ActualWidth), Math.Max(64, (int)ActualHeight),
                hwndParent.Handle, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                Failed = true;
                FailReason = "Could not create the visuals surface.";
            }
            else
            {
                _stop = false;
                _thread = new Thread(RenderLoop) { IsBackground = true, Name = "projectM render" };
                _thread.Start();
            }
            return new HandleRef(this, _hwnd);
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            _stop = true;
            try { _thread?.Join(2500); } catch { }
            _thread = null;
            if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        private static void EnsureClass()
        {
            if (_classRegistered) return;
            _wndProc = (h, m, w, l) => DefWindowProc(h, m, w, l);
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                style = CS_OWNDC,
                lpfnWndProc = _wndProc,
                hInstance = GetModuleHandle(null),
                lpszClassName = ClassName
            };
            _classRegistered = RegisterClassEx(ref wc) != 0;
        }

        // ---------- Render thread ----------
        private void RenderLoop()
        {
            IntPtr hdc = IntPtr.Zero, ctx = IntPtr.Zero, pm = IntPtr.Zero;
            try
            {
                if (!ProjectMNative.DllPresent())
                {
                    Failed = true;
                    FailReason = "projectM.dll is not installed.";
                    return;
                }

                hdc = GetDC(_hwnd);
                if (hdc == IntPtr.Zero) { Failed = true; FailReason = "No device context for the visuals surface."; return; }

                var pfd = new PIXELFORMATDESCRIPTOR
                {
                    nSize = (ushort)Marshal.SizeOf<PIXELFORMATDESCRIPTOR>(),
                    nVersion = 1,
                    dwFlags = PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER,
                    iPixelType = PFD_TYPE_RGBA,
                    cColorBits = 32,
                    cDepthBits = 24,
                    iLayerType = PFD_MAIN_PLANE
                };
                int fmt = ChoosePixelFormat(hdc, ref pfd);
                if (fmt == 0 || !SetPixelFormat(hdc, fmt, ref pfd))
                { Failed = true; FailReason = "OpenGL pixel format is unavailable."; return; }

                ctx = wglCreateContext(hdc);
                if (ctx == IntPtr.Zero) { Failed = true; FailReason = "OpenGL context creation failed."; return; }
                if (!wglMakeCurrent(hdc, ctx)) { Failed = true; FailReason = "OpenGL context would not activate."; return; }

                try { uint glewRc = glewInit(); PmLog("glewInit rc=" + glewRc); }
                catch (Exception glewEx) { PmLog("glewInit threw: " + glewEx.Message); }

                pm = ProjectMNative.projectm_create();
                if (pm == IntPtr.Zero)
                { Failed = true; FailReason = "projectM refused to start (no usable OpenGL 3 context)."; return; }

                EngineVersion = ProjectMNative.VersionString();
                PmLog($"engine created; version={EngineVersion}");
                try { ProjectMNative.projectm_set_preset_switch_failed_event_callback(pm, _failedCb, IntPtr.Zero); } catch (Exception cbEx) { PmLog("callback bind failed: " + cbEx.Message); }
                ProjectMNative.projectm_set_fps(pm, 30);
                ProjectMNative.projectm_set_mesh_size(pm, (UIntPtr)32, (UIntPtr)24);
                ProjectMNative.projectm_set_preset_duration(pm, 30.0);
                ProjectMNative.projectm_set_soft_cut_duration(pm, 3.0);
                ProjectMNative.projectm_set_beat_sensitivity(pm, 1.0f);

                // Give the engine a window size up front so it never sees
                // a zero size; the render loop keeps it current from the
                // child window's real client rect every frame.
                int lastW = 0, lastH = 0;
                if (GetClientRect(_hwnd, out var rcInit))
                {
                    int cw0 = Math.Max(1, rcInit.Right - rcInit.Left), ch0 = Math.Max(1, rcInit.Bottom - rcInit.Top);
                    double sc0 = Math.Min(1.0, Math.Min(1280.0 / cw0, 720.0 / ch0));
                    lastW = Math.Max(64, (int)(cw0 * sc0)); lastH = Math.Max(64, (int)(ch0 * sc0));
                    ProjectMNative.projectm_set_window_size(pm, (UIntPtr)lastW, (UIntPtr)lastH);
                    PmLog($"initial window size {lastW}x{lastH}");
                }

                if (!string.IsNullOrEmpty(_initialFolder)) AddPresetFolder(_initialFolder);

                int presetIndex = -1;
                DateTime lastSwitch = DateTime.MinValue;
                void SwitchTo(int idx, bool smooth)
                {
                    string? path = null;
                    lock (_presetLock)
                    {
                        if (_presets.Count == 0) return;
                        idx = ((idx % _presets.Count) + _presets.Count) % _presets.Count;
                        presetIndex = idx;
                        path = _presets[idx];
                    }
                    if (path == null) return;
                    ProjectMNative.projectm_load_preset_file(pm, path, smooth);
                    CurrentPresetName = Path.GetFileNameWithoutExtension(path);
                    lastSwitch = DateTime.Now;
                    PmLog($"SwitchTo idx={idx} smooth={smooth} name={CurrentPresetName}");
                }
                SwitchTo(0, false);
                Started = true;

                long feedCursor = -1;
                float[] feedBuf = new float[2 * 8192];
                uint maxSamples = 2048;
                try { maxSamples = Math.Max(512u, ProjectMNative.projectm_pcm_get_max_samples()); } catch { }
                PmLog($"feed ready; maxSamples={maxSamples}; presets={PresetCount}");
                var sw = new Stopwatch();

                while (!_stop)
                {
                    sw.Restart();
                    if (_renderEnabled)
                    {
                        // Size (render at most 1280x720, upscale by the window)
                        if (GetClientRect(_hwnd, out var rc))
                        {
                            int cw = Math.Max(1, rc.Right - rc.Left), ch = Math.Max(1, rc.Bottom - rc.Top);
                            double scale = Math.Min(1.0, Math.Min(1280.0 / cw, 720.0 / ch));
                            int rw = Math.Max(64, (int)(cw * scale)), rh = Math.Max(64, (int)(ch * scale));
                            if (rw != lastW || rh != lastH)
                            {
                                lastW = rw; lastH = rh;
                                ProjectMNative.projectm_set_window_size(pm, (UIntPtr)rw, (UIntPtr)rh);
                            }
                        }

                        // Preset rotation requests + auto-advance
                        int step = Interlocked.Exchange(ref _pendingStep, 0);
                        if (step != 0) SwitchTo(presetIndex + step, true);
                        else if (PresetCount > 1 && (DateTime.Now - lastSwitch).TotalSeconds > 30)
                            SwitchTo(presetIndex + 1, true);

                        // PCM feed: only the frames projectM hasn't heard yet
                        var tap = Tap;
                        if (tap != null)
                        {
                            long written = tap.StereoFramesWritten;
                            if (feedCursor < 0 || written - feedCursor > 44100) feedCursor = Math.Max(0, written - 2048);
                            long avail = written - feedCursor;
                            while (avail > 0)
                            {
                                int chunk = (int)Math.Min(avail, Math.Min((long)maxSamples, 8192L));
                                int n = tap.ReadStereo(feedCursor, feedBuf, chunk);
                                if (n <= 0) break;
                                ProjectMNative.projectm_pcm_add_float(pm, feedBuf, (uint)n, ProjectMNative.Channels.Stereo);
                                feedCursor += n;
                                avail -= n;
                            }
                        }

                        ProjectMNative.projectm_opengl_render_frame(pm);
                        SwapBuffers(hdc);
                    }
                    int elapsed = (int)sw.ElapsedMilliseconds;
                    int wait = _renderEnabled ? 33 - elapsed : 80;
                    if (wait > 1) Thread.Sleep(wait);
                }
            }
            catch (Exception ex)
            {
                Failed = true;
                FailReason = "projectM stopped: " + ex.Message;
            }
            finally
            {
                Started = false;
                try { if (pm != IntPtr.Zero) ProjectMNative.projectm_destroy(pm); } catch { }
                try { if (ctx != IntPtr.Zero) { wglMakeCurrent(IntPtr.Zero, IntPtr.Zero); wglDeleteContext(ctx); } } catch { }
                try { if (hdc != IntPtr.Zero && _hwnd != IntPtr.Zero) ReleaseDC(_hwnd, hdc); } catch { }
            }
        }
    }
}
