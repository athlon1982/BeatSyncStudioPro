using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using NAudio.Wave;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// One song in the 🎵 Song Library (v3.72, Michael's ask): a full
    /// track — MP3 / FLAC / WAV / OGG / M4A — with the ID tags read out
    /// of the file itself (title, artist, album, genre, year, tagged
    /// BPM / key) plus analyzed BPM / key when the tags don't carry
    /// them. Kept separate from the short-sounds Asset Library on
    /// purpose: this is the crate of songs you remix and DJ with.
    /// </summary>
    public sealed class LibrarySong
    {
        public string Path { get; set; } = "";
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string Album { get; set; } = "";
        public string Genre { get; set; } = "";
        public string Year { get; set; } = "";
        public double Bpm { get; set; }
        public string Key { get; set; } = "";
        public double Seconds { get; set; }
        public string Format { get; set; } = "";
        public DateTime AddedUtc { get; set; }

        [JsonIgnore]
        public string DisplayTitle => Title.Length > 0 ? Title : System.IO.Path.GetFileNameWithoutExtension(Path);

        [JsonIgnore]
        public string SubLine =>
            (Artist.Length > 0 ? Artist + " · " : "")
            + (Bpm > 0 ? $"{Bpm:0.#} BPM" : "BPM ?")
            + (Key.Length > 0 ? " · " + Key : "")
            + (Seconds > 0 ? $" · {(int)Seconds / 60}:{(int)Seconds % 60:D2}" : "");

        [JsonIgnore]
        public string LengthText => Seconds > 0 ? TimeSpan.FromSeconds(Seconds).ToString(@"m\:ss") : "";

        [JsonIgnore]
        public string BpmText => Bpm > 0 ? Bpm.ToString("0") : "";
    }

    /// <summary>
    /// The Song Library's storage + scanning. The index lives at
    /// %AppData%/BeatSyncStudioPro/song-library.json; songs added from a
    /// ZIP are extracted under %AppData%/BeatSyncStudioPro/SongLibrary/
    /// so they stay put. Folder adds reference files where they live.
    /// </summary>
    public static class SongLibraryStore
    {
        public static readonly string[] AudioExtensions =
            { ".mp3", ".flac", ".wav", ".ogg", ".m4a", ".aac", ".aif", ".aiff" };

        public static string AppDir => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BeatSyncStudioPro");

        public static string IndexPath => System.IO.Path.Combine(AppDir, "song-library.json");

        public static string ExtractRoot => System.IO.Path.Combine(AppDir, "SongLibrary");

        public static bool IsAudioPath(string path) =>
            AudioExtensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());

        public static List<LibrarySong> Load()
        {
            try
            {
                if (!File.Exists(IndexPath)) return new List<LibrarySong>();
                var list = JsonSerializer.Deserialize<List<LibrarySong>>(File.ReadAllText(IndexPath));
                return list ?? new List<LibrarySong>();
            }
            catch { return new List<LibrarySong>(); }
        }

        public static void Save(List<LibrarySong> entries)
        {
            try
            {
                Directory.CreateDirectory(AppDir);
                File.WriteAllText(IndexPath,
                    JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* a failed save must never crash the app */ }
        }

        /// <summary>Read one audio file into an entry: ID tags via
        /// TagReader, duration via the app's reader factory. Returns null
        /// only when the file isn't a supported audio path or is gone.</summary>
        public static LibrarySong? ScanFile(string path)
        {
            try
            {
                if (!File.Exists(path) || !IsAudioPath(path)) return null;
                var entry = new LibrarySong
                {
                    Path = path,
                    Format = System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant(),
                    AddedUtc = DateTime.UtcNow
                };
                byte[] bytes = ReadForTags(path);
                var tags = TagReader.Parse(bytes, System.IO.Path.GetFileName(path));
                if (tags != null)
                {
                    entry.Title = tags.Title;
                    entry.Artist = tags.Artist;
                    entry.Album = tags.Album;
                    entry.Genre = tags.Genre;
                    entry.Year = tags.Year;
                    if (tags.Format.Length > 0) entry.Format = tags.Format;
                    if (double.TryParse(tags.Bpm, out double tb) && tb >= 30 && tb <= 300) entry.Bpm = tb;
                    if (tags.KeyTag.Length > 0) entry.Key = tags.KeyTag;
                }
                try
                {
                    string type = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
                    using var reader = AudioReaderFactory.Open(bytes, type);
                    entry.Seconds = reader.TotalTime.TotalSeconds;
                }
                catch { /* duration stays 0 — the entry still counts */ }
                return entry;
            }
            catch { return null; }
        }

        /// <summary>Whole file for normal music sizes; just the head for
        /// huge files (ID3v2 + FLAC metadata both live at the head).</summary>
        private static byte[] ReadForTags(string path)
        {
            var fi = new FileInfo(path);
            if (fi.Length <= 128 * 1024 * 1024) return File.ReadAllBytes(path);
            byte[] head = new byte[8 * 1024 * 1024];
            using var fs = File.OpenRead(path);
            int read = 0;
            while (read < head.Length)
            {
                int n = fs.Read(head, read, head.Length - read);
                if (n <= 0) break;
                read += n;
            }
            Array.Resize(ref head, read);
            return head;
        }

        /// <summary>Extract the audio files out of a ZIP into the
        /// library's own folder and scan them in.</summary>
        public static List<LibrarySong> ScanZip(string zipPath)
        {
            var found = new List<LibrarySong>();
            try
            {
                string baseName = System.IO.Path.GetFileNameWithoutExtension(zipPath);
                foreach (char c in System.IO.Path.GetInvalidFileNameChars()) baseName = baseName.Replace(c, '_');
                string dir = System.IO.Path.Combine(EnsureLibraryDir(), baseName);
                Directory.CreateDirectory(dir);
                using var zip = ZipFile.OpenRead(zipPath);
                foreach (var ze in zip.Entries)
                {
                    if (ze.Name.Length == 0 || !IsAudioPath(ze.Name)) continue;
                    string target = System.IO.Path.Combine(dir, ze.Name);
                    if (!File.Exists(target))
                    {
                        try { ze.ExtractToFile(target, overwrite: false); }
                        catch { continue; }
                    }
                    var e = ScanFile(target);
                    if (e != null) found.Add(e);
                }
            }
            catch { /* a bad zip just yields nothing */ }
            return found;
        }

        // ---------- the app's own music library folder (v3.82) ----------
        // Michael's ask: BeatSync keeps its OWN library — every song
        // added lands as a file in ONE folder he picks (default:
        // Music\BeatSync Library), and the Song Library's track list
        // is the list of everything that's been added.

        private sealed class LibrarySettings
        {
            public string LibraryDir { get; set; } = "";
            public bool CopyImports { get; set; } = true;
        }

        private static string SettingsPath => System.IO.Path.Combine(AppDir, "library-settings.json");
        private static LibrarySettings? _settings;

        private static LibrarySettings Settings
        {
            get
            {
                if (_settings != null) return _settings;
                try
                {
                    if (File.Exists(SettingsPath))
                        _settings = JsonSerializer.Deserialize<LibrarySettings>(File.ReadAllText(SettingsPath));
                }
                catch { /* fall through to defaults */ }
                return _settings ??= new LibrarySettings();
            }
        }

        private static void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(AppDir);
                File.WriteAllText(SettingsPath,
                    JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* a failed save must never crash the app */ }
        }

        public static string DefaultLibraryDir => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "BeatSync Library");

        public static string LibraryDir
        {
            get => Settings.LibraryDir.Length > 0 ? Settings.LibraryDir : DefaultLibraryDir;
            set { Settings.LibraryDir = value ?? ""; SaveSettings(); }
        }

        public static bool CopyImports
        {
            get => Settings.CopyImports;
            set { Settings.CopyImports = value; SaveSettings(); }
        }

        public static string EnsureLibraryDir()
        {
            string dir = LibraryDir;
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }

        public static bool IsInLibraryDir(string path)
        {
            try
            {
                string full = System.IO.Path.GetFullPath(path);
                string dir = System.IO.Path.GetFullPath(LibraryDir).TrimEnd('\\', '/') + "\\";
                return full.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static string SanitizeFileName(string name)
        {
            foreach (char c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            name = name.Trim();
            return name.Length == 0 ? "song" : name;
        }

        /// <summary>Where a new file with this name goes in the library
        /// folder: the name itself when free, the existing file when an
        /// identical-size copy is already there, else "name (2).ext".</summary>
        private static string UniqueTargetPath(string fileName, long length)
        {
            string dir = EnsureLibraryDir();
            string clean = SanitizeFileName(fileName);
            string candidate = System.IO.Path.Combine(dir, clean);
            if (!File.Exists(candidate)) return candidate;
            try { if (new FileInfo(candidate).Length == length) return candidate; } catch { }
            string stem = System.IO.Path.GetFileNameWithoutExtension(clean);
            string ext = System.IO.Path.GetExtension(clean);
            for (int i = 2; i < 100; i++)
            {
                string alt = System.IO.Path.Combine(dir, $"{stem} ({i}){ext}");
                if (!File.Exists(alt)) return alt;
            }
            return candidate;
        }

        /// <summary>Copy an existing file into the library folder.
        /// Returns the library path — or the original path when the
        /// copy can't happen, so the caller can still register it.</summary>
        public static string CopyIntoLibrary(string sourcePath)
        {
            try
            {
                if (IsInLibraryDir(sourcePath)) return sourcePath;
                long len = new FileInfo(sourcePath).Length;
                string target = UniqueTargetPath(System.IO.Path.GetFileName(sourcePath), len);
                if (!File.Exists(target))
                    File.Copy(sourcePath, target, overwrite: false);
                return target;
            }
            catch { return sourcePath; }
        }

        /// <summary>Add downloaded bytes as a song in the library: the
        /// file lands in the library folder, its tags are scanned, and
        /// the entry joins the saved index the Song Library window
        /// shows. Returns the entry, null when it couldn't be done.</summary>
        public static LibrarySong? AddBytesToLibrary(byte[] bytes, string fileName)
        {
            try
            {
                string target = UniqueTargetPath(fileName, bytes.LongLength);
                if (!(File.Exists(target) && new FileInfo(target).Length == bytes.LongLength))
                    File.WriteAllBytes(target, bytes);
                var entry = ScanFile(target);
                if (entry == null) return null;
                var all = Load();
                if (!all.Any(s => string.Equals(s.Path, target, StringComparison.OrdinalIgnoreCase)))
                {
                    all.Add(entry);
                    Save(all);
                }
                return entry;
            }
            catch { return null; }
        }

        /// <summary>Fill in BPM + key where they're missing: tags first
        /// (done at scan), then the app's own detectors — the same ones
        /// the DJ decks use. Returns true when anything changed.</summary>
        public static async Task<bool> AnalyzeAsync(LibrarySong entry)
        {
            bool changed = false;
            try
            {
                if (!File.Exists(entry.Path)) return false;
                byte[] bytes = await Task.Run(() => File.ReadAllBytes(entry.Path));
                string type = System.IO.Path.GetExtension(entry.Path).TrimStart('.').ToLowerInvariant();
                if (entry.Bpm <= 0)
                {
                    var (bpm, _, _) = await Task.Run(() => BeatDetector.Detect(bytes, type));
                    if (bpm >= 50 && bpm <= 200) { entry.Bpm = Math.Round(bpm * 2) / 2; changed = true; }
                }
                if (entry.Key.Length == 0)
                {
                    var mono = await Task.Run(() => DecodeMono(bytes, type));
                    if (mono != null)
                    {
                        var kr = KeyDetector.Detect(mono);
                        if (kr != null) { entry.Key = kr.Camelot; changed = true; }
                    }
                }
            }
            catch { /* analysis is best-effort */ }
            return changed;
        }

        internal static float[]? DecodeMono(byte[] bytes, string type)
        {
            try
            {
                using var reader = AudioReaderFactory.Open(bytes, type);
                var sp = reader.ToSampleProvider();
                int ch = Math.Max(1, sp.WaveFormat.Channels);
                ISampleProvider p441 = sp.WaveFormat.SampleRate == 44100
                    ? sp
                    : new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(sp, 44100);
                var all = new List<float>();
                var buf = new float[44100 * 2 * ch];
                int read;
                while ((read = p441.Read(buf, 0, buf.Length)) > 0)
                    for (int i = 0; i + ch - 1 < read; i += ch)
                    {
                        float s = 0;
                        for (int c = 0; c < ch; c++) s += buf[i + c];
                        all.Add(s / ch);
                    }
                return all.ToArray();
            }
            catch { return null; }
        }
    }
}
