using System;
using System.IO;
using System.Text.Json;

namespace BeatSyncStudioPro
{
    /// <summary>Remembered DJ-deck choices: platter style and visual colors per deck.</summary>
    public class DjSettings
    {
        public string PlatterA { get; set; } = "Vinyl";
        public string PlatterB { get; set; } = "Vinyl";
        public string ColorA { get; set; } = "Deck Color";
        public string ColorB { get; set; } = "Deck Color";

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BeatSyncStudioPro", "dj-settings.json");

        private static DjSettings? _cached;
        public static DjSettings Current => _cached ??= Load();

        private static DjSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<DjSettings>(File.ReadAllText(FilePath)) ?? new DjSettings();
            }
            catch { }
            return new DjSettings();
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
    }
}
