using System;
using System.IO;
using System.Runtime.InteropServices;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// P/Invoke bindings for libprojectM 4.x (the MilkDrop engine, LGPL-2.1).
    /// The library ships as a replaceable DLL (projectM.dll, from the
    /// official v4.0.0-rc1 Windows package — the only 4.x release with a
    /// pre-built Windows dev package; signatures verified against its
    /// headers) beside the exe — BeatSync never links it statically, and
    /// if the DLL is missing or fails to start, the Output window silently
    /// keeps its built-in FFT scenes. Only the core API is bound here (no
    /// playlist DLL): the app rotates presets itself with
    /// projectm_load_preset_file.
    ///
    /// Threading rule: every call below must happen on the render thread
    /// that owns the OpenGL context (see ProjectMHost). projectm_create()
    /// returns NULL when no suitable GL context is current.
    /// </summary>
    internal static class ProjectMNative
    {
        private const string Dll = "projectM";

        public enum Channels : int
        {
            Mono = 1,
            Stereo = 2
        }

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr projectm_create();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void projectm_destroy(IntPtr instance);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void projectm_get_version_components(out int major, out int minor, out int patch);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void projectm_opengl_render_frame(IntPtr instance);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern void projectm_load_preset_file(IntPtr instance, string filename, [MarshalAs(UnmanagedType.U1)] bool smoothTransition);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void projectm_pcm_add_float(IntPtr instance, float[] samples, uint count, Channels channels);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint projectm_pcm_get_max_samples();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void projectm_set_window_size(IntPtr instance, UIntPtr width, UIntPtr height);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void projectm_set_mesh_size(IntPtr instance, UIntPtr width, UIntPtr height);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void projectm_set_fps(IntPtr instance, int fps);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void projectm_set_preset_duration(IntPtr instance, double durationSeconds);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void projectm_set_soft_cut_duration(IntPtr instance, double durationSeconds);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void projectm_set_beat_sensitivity(IntPtr instance, float sensitivity);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void projectm_set_preset_locked(IntPtr instance, [MarshalAs(UnmanagedType.U1)] bool locked);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void PresetSwitchFailedCallback(IntPtr presetFilename, IntPtr message, IntPtr userData);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        public static extern void projectm_set_preset_switch_failed_event_callback(IntPtr instance, PresetSwitchFailedCallback callback, IntPtr userData);

        /// <summary>True when projectM.dll sits beside the exe.</summary>
        public static bool DllPresent()
        {
            try { return File.Exists(Path.Combine(AppContext.BaseDirectory, "projectM.dll")); }
            catch { return false; }
        }

        /// <summary>"4.1.8" once an instance exists, else "".</summary>
        public static string VersionString()
        {
            try
            {
                projectm_get_version_components(out int a, out int b, out int c);
                return $"{a}.{b}.{c}";
            }
            catch { return ""; }
        }
    }
}
