using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// One of the user's OWN Suno songs in the Song Library's ☀️ Suno
    /// tab (v3.73, Michael's ask): found by syncing the signed-in Suno
    /// library page inside the app — nothing about any account is baked
    /// into the app, and the sync never touches Suno's Download action
    /// or its quota. Playing a song streams the same audio URL Suno's
    /// own web player streams, straight into memory and onto a deck.
    /// </summary>
    public sealed class SunoSong
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string CoverUrl { get; set; } = "";
        public bool Published { get; set; }
        public bool PublishKnown { get; set; }
        public double Bpm { get; set; }
        public string Key { get; set; } = "";
        public double Seconds { get; set; }

        [JsonIgnore]
        public string StreamUrl => $"https://cdn1.suno.ai/{Id}.mp3";

        [JsonIgnore]
        public string PageUrl => $"https://suno.com/song/{Id}";

        [JsonIgnore]
        public string DisplayTitle => Title.Length > 0 ? Title : "Suno song " + Id.Substring(0, Math.Min(8, Id.Length));

        [JsonIgnore]
        public string DisplayCover => CoverUrl.Length > 0 ? CoverUrl : $"https://cdn2.suno.ai/image_{Id}.jpeg";

        [JsonIgnore]
        public string StatusText => !PublishKnown ? "Yours" : Published ? "Published" : "Draft";

        [JsonIgnore]
        public string BpmText => Bpm > 0 ? Bpm.ToString("0") : "";

        [JsonIgnore]
        public string LengthText => Seconds > 0 ? TimeSpan.FromSeconds(Seconds).ToString(@"m\:ss") : "";
    }

    /// <summary>The Suno tab's local cache: what the last sync found,
    /// kept at %AppData%/BeatSyncStudioPro/suno-songs.json so the list
    /// is there between sessions without re-syncing.</summary>
    public static class SunoLibraryStore
    {
        public static string CachePath => Path.Combine(SongLibraryStore.AppDir, "suno-songs.json");

        public static List<SunoSong> Load()
        {
            try
            {
                if (!File.Exists(CachePath)) return new List<SunoSong>();
                return JsonSerializer.Deserialize<List<SunoSong>>(File.ReadAllText(CachePath)) ?? new List<SunoSong>();
            }
            catch { return new List<SunoSong>(); }
        }

        public static void Save(List<SunoSong> songs)
        {
            try
            {
                Directory.CreateDirectory(SongLibraryStore.AppDir);
                File.WriteAllText(CachePath,
                    JsonSerializer.Serialize(songs, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* best effort */ }
        }
    }
}
