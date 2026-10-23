using System;
using System.Text.Json;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// Stream overlay helpers (v3.94): the pure pieces behind the Output
    /// window's overlay card — which deck is "now playing" given the
    /// crossfader, and how many listeners the local Icecast server reports
    /// for Rad-Dad Radio. Kept free of UI/network state so they can be
    /// tested directly.
    /// </summary>
    public static class OverlayData
    {
        /// <summary>Pick the audible deck's title. xfade01: 0 = full A, 1 = full B.</summary>
        public static string PickNowPlaying(bool aPlaying, string aTitle, bool bPlaying, string bTitle, double xfade01)
        {
            aTitle = (aTitle ?? "").Trim();
            bTitle = (bTitle ?? "").Trim();
            if (aPlaying && bPlaying)
            {
                if (xfade01 < 0.4) return aTitle;
                if (xfade01 > 0.6) return bTitle;
                if (aTitle.Length > 0 && bTitle.Length > 0) return aTitle + "  +  " + bTitle;
                return aTitle.Length > 0 ? aTitle : bTitle;
            }
            if (aPlaying) return aTitle;
            if (bPlaying) return bTitle;
            return "";
        }

        /// <summary>
        /// Sum listener counts across all sources in an Icecast
        /// status-json.xsl payload. Returns -1 when the payload cannot be
        /// understood at all; 0 when the server answered but no source is
        /// streaming.
        /// </summary>
        public static int ParseListenerCount(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return -1;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("icestats", out var stats)) return -1;
                if (!stats.TryGetProperty("source", out var source)) return 0;
                int total = 0;
                if (source.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in source.EnumerateArray()) total += ReadListeners(s);
                }
                else if (source.ValueKind == JsonValueKind.Object)
                {
                    total += ReadListeners(source);
                }
                return total;
            }
            catch { return -1; }
        }

        private static int ReadListeners(JsonElement source)
        {
            try
            {
                if (source.TryGetProperty("listeners", out var l) && l.TryGetInt32(out int n)) return Math.Max(0, n);
            }
            catch { }
            return 0;
        }
    }
}
