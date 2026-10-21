using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// The app-wide compute choice for AI features: Auto / CPU / GPU, plus
    /// which graphics device. Saved in %AppData%/BeatSyncStudioPro.
    /// It drives the Stem Splitter (ONNX Runtime: CPU or DirectML) AND —
    /// since v3.21, Michael's ask — the ffmpeg video export encoder:
    /// GPU/Auto picks the hardware encoder for the detected adapter.
    /// The song-generation engine reads the same setting.
    /// </summary>
    public class ComputeSettings
    {
        /// <summary>"Auto", "CPU", "GPU", or "Online".</summary>
        public string Mode { get; set; } = "Auto";

        /// <summary>Which graphics adapter (DirectML device id) when Mode is GPU.</summary>
        public int GpuIndex { get; set; } = 0;

        /// <summary>Cloud compute provider when Mode is Online: "replicate" (paid, fast), "huggingface" (free tier) or "fal" (paid, fast).</summary>
        public string OnlineProvider { get; set; } = "replicate";

        /// <summary>Address of the BeatSync Engine on Michael's OTHER PC (OnlineProvider "remote", v3.23) — e.g. http://192.168.1.50:7860 or its Tailscale address. A plain URL, not a secret, so it is stored unencrypted like Mode/Provider.</summary>
        public string RemoteEngineUrl { get; set; } = "";

        /// <summary>fal.ai API key, DPAPI-encrypted.</summary>
        public string FalKeyEncrypted { get; set; } = "";

        public string GetFalKey()
        {
            if (string.IsNullOrEmpty(FalKeyEncrypted)) return "";
            try
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(FalKeyEncrypted), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch { return ""; }
        }

        public void SetFalKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) { FalKeyEncrypted = ""; return; }
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), null, DataProtectionScope.CurrentUser);
            FalKeyEncrypted = Convert.ToBase64String(bytes);
        }

        /// <summary>Hugging Face token (free tier), DPAPI-encrypted.</summary>
        public string HfKeyEncrypted { get; set; } = "";

        public string GetHfKey()
        {
            if (string.IsNullOrEmpty(HfKeyEncrypted)) return "";
            try
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(HfKeyEncrypted), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch { return ""; }
        }

        public void SetHfKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) { HfKeyEncrypted = ""; return; }
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), null, DataProtectionScope.CurrentUser);
            HfKeyEncrypted = Convert.ToBase64String(bytes);
        }

        /// <summary>The online provider's API key, DPAPI-encrypted (never stored in plain text).</summary>
        public string OnlineKeyEncrypted { get; set; } = "";

        public string GetOnlineKey()
        {
            if (string.IsNullOrEmpty(OnlineKeyEncrypted)) return "";
            try
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(OnlineKeyEncrypted), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch { return ""; }
        }

        public void SetOnlineKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) { OnlineKeyEncrypted = ""; return; }
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), null, DataProtectionScope.CurrentUser);
            OnlineKeyEncrypted = Convert.ToBase64String(bytes);
        }

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BeatSyncStudioPro", "compute-settings.json");

        public static ComputeSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<ComputeSettings>(File.ReadAllText(FilePath)) ?? new ComputeSettings();
            }
            catch { }
            return new ComputeSettings();
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

        /// <summary>Display names of this PC's graphics adapters, read from the registry.</summary>
        public static List<string> GpuNames()
        {
            var names = new List<string>();
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
                if (key != null)
                {
                    foreach (var sub in key.GetSubKeyNames())
                    {
                        using var adapter = key.OpenSubKey(sub);
                        if (adapter?.GetValue("DriverDesc") is string desc && desc.Length > 0 && !names.Contains(desc))
                            names.Add(desc);
                    }
                }
            }
            catch { }
            return names;
        }

        /// <summary>
        /// Should heavy AI work try the GPU first? Auto means: yes when a
        /// discrete card is present (integrated Intel graphics stay on CPU —
        /// they're usually slower for this and share system RAM).
        /// </summary>
        public bool UseGpu
        {
            get
            {
                if (Mode == "GPU") return true;
                if (Mode == "CPU") return false;
                return GpuNames().Exists(n => !n.Contains("Intel"));
            }
        }

        /// <summary>
        /// The ffmpeg H.264 encoder video exports should use, honoring the
        /// compute choice: CPU mode (or no GPU) → libx264 on the processor;
        /// GPU / Auto / Online (exports always render on this PC) → the
        /// hardware encoder for the detected adapter. NVIDIA wins over an
        /// integrated Intel GPU when both are listed (common on laptops).
        /// Unlike the ONNX rule in UseGpu, Intel integrated graphics DO get
        /// used here — QuickSync is built exactly for video encoding.
        /// The export falls back to libx264 on its own if the hardware
        /// encoder fails, so this is a preference, never a hard failure.
        /// </summary>
        public (string Encoder, string Label) VideoEncoder()
        {
            if (Mode == "CPU") return ("libx264", "CPU (libx264)");
            var gpus = GpuNames();
            if (gpus.Exists(n => n.Contains("NVIDIA"))) return ("h264_nvenc", "NVIDIA NVENC (GPU)");
            if (gpus.Exists(n => n.Contains("Intel"))) return ("h264_qsv", "Intel QuickSync (GPU)");
            if (gpus.Exists(n => n.Contains("AMD") || n.Contains("Radeon"))) return ("h264_amf", "AMD AMF (GPU)");
            return ("libx264", "CPU (libx264)");
        }

        /// <summary>Short human description of what will actually run, for status lines.</summary>
        public string Describe()
        {
            var gpus = GpuNames();
            string gpuPart = gpus.Count == 0 ? "no GPU found" : string.Join(" + ", gpus);
            return Mode switch
            {
                "CPU" => $"CPU only (detected: {gpuPart})",
                "GPU" => gpus.Count == 0 ? "GPU requested, but no GPU was detected — CPU will be used"
                    : $"GPU #{GpuIndex} of: {gpuPart}",
                "Online" => OnlineProvider == "remote"
                    ? (RemoteEngineUrl.Trim().Length > 0 ? $"☁️ Online via 🏠 Remote GPU — your other PC at {RemoteEngineUrl.Trim()} (FREE)" : "☁️ Online via 🏠 Remote GPU (no engine address saved yet — set it in 🌐 Providers)")
                    : OnlineProvider == "huggingface"
                    ? (GetHfKey().Length > 0 ? "☁️ Online via Hugging Face free tier (token saved)" : "☁️ Online via Hugging Face free tier (no token saved yet — add it below)")
                    : OnlineProvider == "fal"
                    ? (GetFalKey().Length > 0 ? "☁️ Online via fal.ai (paid, key saved)" : "☁️ Online via fal.ai (paid — no key saved yet, add it below)")
                    : (GetOnlineKey().Length > 0 ? "☁️ Online via Replicate (key saved)" : "☁️ Online via Replicate (no key saved yet — add it below)"),
                _ => UseGpu ? $"Auto → GPU ({gpuPart})" : $"Auto → CPU ({gpuPart})"
            };
        }
    }
}
