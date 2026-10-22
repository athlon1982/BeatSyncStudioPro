using System;
using System.Text;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// v3.53: the tags embedded in one audio file — read by hand (no extra
    /// library) from MP3 ID3v2 (2.2 / 2.3 / 2.4) + ID3v1 and FLAC Vorbis
    /// comments + picture blocks. Michael asked for exactly this: "read the
    /// ID tags and see the cover art" on his MP3 / FLAC files.
    /// </summary>
    public sealed class TagInfo
    {
        public string Title = "";
        public string Artist = "";
        public string Album = "";
        public string Year = "";
        public string Genre = "";
        public string Bpm = "";
        public string KeyTag = "";
        public string Format = "";
        public byte[]? Picture;

        public bool HasAnything =>
            Title.Length > 0 || Artist.Length > 0 || Album.Length > 0 ||
            Year.Length > 0 || Genre.Length > 0 || Bpm.Length > 0 ||
            KeyTag.Length > 0 || (Picture != null && Picture.Length > 0);

        /// "Artist – Title" when both are tagged, else whichever exists.
        public string DisplayName
        {
            get
            {
                if (Artist.Length > 0 && Title.Length > 0) return Artist + " – " + Title;
                if (Title.Length > 0) return Title;
                return Artist;
            }
        }
    }

    public static class TagReader
    {
        /// <summary>Parse tags from raw file bytes. Never throws; null when the
        /// file carries no readable tags.</summary>
        public static TagInfo? Parse(byte[] b, string nameHint)
        {
            try
            {
                if (b == null || b.Length < 16) return null;
                TagInfo? info = null;
                if (b[0] == (byte)'I' && b[1] == (byte)'D' && b[2] == (byte)'3')
                {
                    info = ParseId3v2(b);
                    if (info != null) info.Format = "MP3";
                }
                else if (b[0] == (byte)'f' && b[1] == (byte)'L' && b[2] == (byte)'a' && b[3] == (byte)'C')
                {
                    info = ParseFlac(b);
                    if (info != null) info.Format = "FLAC";
                }
                if (info == null || !info.HasAnything)
                {
                    var v1 = ParseId3v1(b);
                    if (v1 != null)
                    {
                        if (info == null) { info = v1; info.Format = "MP3"; }
                        else
                        {
                            if (info.Title.Length == 0) info.Title = v1.Title;
                            if (info.Artist.Length == 0) info.Artist = v1.Artist;
                            if (info.Album.Length == 0) info.Album = v1.Album;
                            if (info.Year.Length == 0) info.Year = v1.Year;
                            if (info.Genre.Length == 0) info.Genre = v1.Genre;
                        }
                    }
                }
                return info != null && info.HasAnything ? info : null;
            }
            catch { return null; }
        }

        // ---------------- ID3v2 ----------------

        private static TagInfo? ParseId3v2(byte[] b)
        {
            int major = b[3];
            if (major < 2 || major > 4) return null;
            int tagSize = Synchsafe(b, 6);
            int end = Math.Min(b.Length, 10 + tagSize);
            int pos = 10;
            // Extended header (rare): skip it.
            if ((b[5] & 0x40) != 0)
            {
                if (major == 3 && pos + 4 <= end) pos += 4 + Be32(b, pos);
                else if (major == 4 && pos + 4 <= end) pos += Synchsafe(b, pos);
            }
            var info = new TagInfo();
            if (major == 2)
            {
                while (pos + 6 <= end)
                {
                    string id = Latin(b, pos, 3);
                    if (!IsFrameId(id)) break;
                    int size = (b[pos + 3] << 16) | (b[pos + 4] << 8) | b[pos + 5];
                    int data = pos + 6;
                    if (size <= 0 || data + size > end + 512) break;   // small slack for sloppy writers
                    ApplyFrame(info, MapV22(id), b, data, Math.Min(size, b.Length - data), true);
                    pos = data + size;
                }
                return info;
            }
            while (pos + 10 <= end)
            {
                string id = Latin(b, pos, 4);
                if (!IsFrameId(id)) break;
                int size = major == 4 ? Synchsafe(b, pos + 4) : Be32(b, pos + 4);
                int data = pos + 10;
                if (size < 0 || data >= b.Length) break;
                if (size > 0) ApplyFrame(info, id, b, data, Math.Min(size, b.Length - data), false);
                pos = data + size;
            }
            return info;
        }

        private static string MapV22(string id) => id switch
        {
            "TT2" => "TIT2", "TP1" => "TPE1", "TAL" => "TALB", "TYE" => "TYER",
            "TCO" => "TCON", "TBP" => "TBPM", "PIC" => "APIC", _ => id
        };

        private static void ApplyFrame(TagInfo info, string id, byte[] b, int off, int len, bool v22)
        {
            if (len <= 0) return;
            switch (id)
            {
                case "TIT2": info.Title = DecodeText(b, off, len); break;
                case "TPE1": info.Artist = DecodeText(b, off, len); break;
                case "TALB": info.Album = DecodeText(b, off, len); break;
                case "TYER":
                case "TDRC":
                    string y = DecodeText(b, off, len);
                    info.Year = y.Length >= 4 ? y.Substring(0, 4) : y;
                    break;
                case "TCON": info.Genre = CleanGenre(DecodeText(b, off, len)); break;
                case "TBPM": info.Bpm = DecodeText(b, off, len); break;
                case "TKEY": info.KeyTag = DecodeText(b, off, len); break;
                case "APIC":
                    var pic = v22 ? ParseApic22(b, off, len) : ParseApic(b, off, len);
                    if (pic != null) info.Picture = pic;
                    break;
            }
        }

        private static string DecodeText(byte[] b, int off, int len)
        {
            if (len < 1) return "";
            int enc = b[off];
            string s;
            try
            {
                s = enc switch
                {
                    1 => DecodeUtf16(b, off + 1, len - 1),
                    2 => Encoding.BigEndianUnicode.GetString(b, off + 1, len - 1),
                    3 => Encoding.UTF8.GetString(b, off + 1, len - 1),
                    _ => Encoding.Latin1.GetString(b, off + 1, len - 1)
                };
            }
            catch { s = ""; }
            s = s.Trim('\0', ' ', '\t', '\r', '\n');
            int cut = s.IndexOf('\0');
            if (cut >= 0) s = s.Substring(0, cut);
            return s.Trim();
        }

        private static string DecodeUtf16(byte[] b, int off, int len)
        {
            if (len >= 2 && b[off] == 0xFF && b[off + 1] == 0xFE) return Encoding.Unicode.GetString(b, off + 2, len - 2);
            if (len >= 2 && b[off] == 0xFE && b[off + 1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, off + 2, len - 2);
            return Encoding.Unicode.GetString(b, off, len);
        }

        private static string CleanGenre(string g)
        {
            // ID3v1-style genre refs look like "(17)" or "(17)Rock".
            if (g.StartsWith("("))
            {
                int close = g.IndexOf(')');
                if (close > 0)
                {
                    string rest = g.Substring(close + 1).Trim();
                    if (rest.Length > 0) return rest;
                    if (int.TryParse(g.Substring(1, close - 1), out int n)) return GenreName(n);
                }
            }
            return g;
        }

        private static byte[]? ParseApic(byte[] b, int off, int len)
        {
            try
            {
                int end = off + len;
                int enc = b[off];
                int p = off + 1;
                while (p < end && b[p] != 0) p++;            // mime type, latin1, single 0
                if (p >= end) return null;
                p++;                                        // mime terminator
                if (p >= end) return null;
                p++;                                        // picture type byte
                p = SkipEncodedString(b, p, end, enc);      // description
                if (p < 0 || p >= end) return null;
                int count = end - p;
                if (count < 100) return null;
                var pic = new byte[count];
                Array.Copy(b, p, pic, 0, count);
                return LooksLikeImage(pic) ? pic : null;
            }
            catch { return null; }
        }

        private static byte[]? ParseApic22(byte[] b, int off, int len)
        {
            try
            {
                int end = off + len;
                int enc = b[off];
                int p = off + 1 + 3;                        // 3-char format code (JPG/PNG)
                if (p >= end) return null;
                p++;                                        // picture type byte
                p = SkipEncodedString(b, p, end, enc);
                if (p < 0 || p >= end) return null;
                int count = end - p;
                if (count < 100) return null;
                var pic = new byte[count];
                Array.Copy(b, p, pic, 0, count);
                return LooksLikeImage(pic) ? pic : null;
            }
            catch { return null; }
        }

        private static int SkipEncodedString(byte[] b, int p, int end, int enc)
        {
            if (enc == 1 || enc == 2)
            {
                while (p + 1 < end)
                {
                    if (b[p] == 0 && b[p + 1] == 0) return p + 2;
                    p += 2;
                }
                return -1;
            }
            while (p < end)
            {
                if (b[p] == 0) return p + 1;
                p++;
            }
            return -1;
        }

        private static bool LooksLikeImage(byte[] p) =>
            (p.Length > 3 && p[0] == 0xFF && p[1] == 0xD8) ||                                  // JPEG
            (p.Length > 4 && p[0] == 0x89 && p[1] == 0x50 && p[2] == 0x4E && p[3] == 0x47) ||   // PNG
            (p.Length > 3 && p[0] == 0x47 && p[1] == 0x49 && p[2] == 0x46) ||                   // GIF
            (p.Length > 2 && p[0] == 0x42 && p[1] == 0x4D);                                     // BMP

        // ---------------- ID3v1 ----------------

        private static TagInfo? ParseId3v1(byte[] b)
        {
            if (b.Length < 128) return null;
            int off = b.Length - 128;
            if (b[off] != (byte)'T' || b[off + 1] != (byte)'A' || b[off + 2] != (byte)'G') return null;
            var info = new TagInfo
            {
                Title = Latin(b, off + 3, 30).Trim('\0', ' '),
                Artist = Latin(b, off + 33, 30).Trim('\0', ' '),
                Album = Latin(b, off + 63, 30).Trim('\0', ' '),
                Year = Latin(b, off + 93, 4).Trim('\0', ' '),
                Genre = GenreName(b[off + 127])
            };
            return info;
        }

        private static string GenreName(int n) => n switch
        {
            0 => "Blues", 1 => "Classic Rock", 2 => "Country", 3 => "Dance", 4 => "Disco",
            5 => "Funk", 6 => "Grunge", 7 => "Hip-Hop", 8 => "Jazz", 9 => "Metal",
            10 => "New Age", 11 => "Oldies", 12 => "Other", 13 => "Pop", 14 => "R&B",
            15 => "Rap", 16 => "Reggae", 17 => "Rock", 18 => "Techno", 19 => "Industrial",
            125 => "Dance Hall", _ => ""
        };

        // ---------------- FLAC ----------------

        private static TagInfo? ParseFlac(byte[] b)
        {
            var info = new TagInfo();
            int pos = 4;
            while (pos + 4 <= b.Length)
            {
                bool last = (b[pos] & 0x80) != 0;
                int type = b[pos] & 0x7F;
                int size = (b[pos + 1] << 16) | (b[pos + 2] << 8) | b[pos + 3];
                int data = pos + 4;
                if (size < 0 || data + size > b.Length) break;
                if (type == 4) ParseVorbisComment(info, b, data, size);
                else if (type == 6) ParseFlacPicture(info, b, data, size);
                if (last) break;
                pos = data + size;
            }
            return info;
        }

        private static void ParseVorbisComment(TagInfo info, byte[] b, int off, int len)
        {
            try
            {
                int end = off + len;
                int p = off;
                if (p + 4 > end) return;
                int vendor = Le32(b, p); p += 4 + vendor;
                if (p + 4 > end) return;
                int count = Le32(b, p); p += 4;
                for (int i = 0; i < count && p + 4 <= end; i++)
                {
                    int slen = Le32(b, p); p += 4;
                    if (slen < 0 || p + slen > end) return;
                    string s = Encoding.UTF8.GetString(b, p, slen);
                    p += slen;
                    int eq = s.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = s.Substring(0, eq).Trim().ToUpperInvariant();
                    string val = s.Substring(eq + 1).Trim();
                    switch (key)
                    {
                        case "TITLE": if (info.Title.Length == 0) info.Title = val; break;
                        case "ARTIST": if (info.Artist.Length == 0) info.Artist = val; break;
                        case "ALBUM": if (info.Album.Length == 0) info.Album = val; break;
                        case "DATE": if (info.Year.Length == 0) info.Year = val.Length >= 4 ? val.Substring(0, 4) : val; break;
                        case "GENRE": if (info.Genre.Length == 0) info.Genre = val; break;
                        case "BPM": if (info.Bpm.Length == 0) info.Bpm = val; break;
                        case "KEY":
                        case "INITIALKEY": if (info.KeyTag.Length == 0) info.KeyTag = val; break;
                    }
                }
            }
            catch { /* partial tags are fine */ }
        }

        private static void ParseFlacPicture(TagInfo info, byte[] b, int off, int len)
        {
            try
            {
                int end = off + len;
                int p = off;
                if (p + 4 > end) return;
                p += 4;                                     // picture type
                int mimeLen = Be32(b, p); p += 4 + mimeLen;
                if (p + 4 > end) return;
                int descLen = Be32(b, p); p += 4 + descLen;
                if (p + 16 > end) return;
                p += 16;                                    // width, height, depth, colors
                int dataLen = Be32(b, p); p += 4;
                if (dataLen < 100 || p + dataLen > end) return;
                var pic = new byte[dataLen];
                Array.Copy(b, p, pic, 0, dataLen);
                if (LooksLikeImage(pic)) info.Picture = pic;
            }
            catch { /* no picture is fine */ }
        }

        // ---------------- helpers ----------------

        private static int Synchsafe(byte[] b, int off) =>
            ((b[off] & 0x7F) << 21) | ((b[off + 1] & 0x7F) << 14) | ((b[off + 2] & 0x7F) << 7) | (b[off + 3] & 0x7F);

        private static int Be32(byte[] b, int off) =>
            (b[off] << 24) | (b[off + 1] << 16) | (b[off + 2] << 8) | b[off + 3];

        private static int Le32(byte[] b, int off) =>
            b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24);

        private static string Latin(byte[] b, int off, int len) => Encoding.Latin1.GetString(b, off, len);

        private static bool IsFrameId(string id)
        {
            if (id.Length == 0) return false;
            foreach (char c in id)
                if (!((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))) return false;
            return true;
        }
    }
}
