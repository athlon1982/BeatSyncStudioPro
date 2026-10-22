using System;
using System.IO;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// Files sounds into categories by name/context keywords, so downloads
    /// land organized (drums with drums, guitar with guitar) and the
    /// library's category dropdown can navigate them. An explicit
    /// category (sound-pack folder, the user's pick) always wins over
    /// inference.
    /// </summary>
    public static class SoundCategorizer
    {
        public const string Drums = "Drums";
        public const string Guitar = "Guitar";
        public const string Bass = "Bass";
        public const string Keys = "Keys & Pads";
        public const string Loops = "Loops";
        public const string Vocals = "Vocals";
        public const string Fx = "FX";
        public const string Stems = "Stems";
        public const string Mashups = "Mash-ups";
        public const string Other = "Other";

        public static readonly string[] All = { Drums, Guitar, Bass, Keys, Loops, Vocals, Fx, Other };

        /// <summary>Stems are filed per type (v3.46): "Stems · Vocals", "Stems · Drums", …</summary>
        public static string StemCategory(string stem) => (stem ?? "").Trim().ToLowerInvariant() switch
        {
            "vocals" => "Stems · Vocals",
            "drums" => "Stems · Drums",
            "bass" => "Stems · Bass",
            "other" => "Stems · Other",
            _ => Stems
        };

        public static string Icon(string category) => category.StartsWith("Stems", StringComparison.Ordinal) ? "🧬" : category switch
        {
            Drums => "🥁",
            Guitar => "🎸",
            Bass => "🔊",
            Keys => "🎹",
            Loops => "🔁",
            Vocals => "🎤",
            Fx => "✨",
            Stems => "🧬",
            Mashups => "🔀",
            _ => "🎵"
        };

        public static string Infer(string text)
        {
            string s = " " + (text ?? "").ToLowerInvariant().Replace('_', ' ').Replace('-', ' ') + " ";
            bool Has(params string[] words)
            {
                foreach (var w in words) if (s.Contains(w)) return true;
                return false;
            }
            if (Has("vocal", "voice", "choir", "acapella", "a capella", "singing", "chant")) return Vocals;
            if (Has("riser", "downlifter", "impact", "sweep", "vinyl", "crackle", "transition", "whoosh", "fx ", "sfx", "noise sweep")) return Fx;
            if (Has("loop")) return Loops;
            if (Has("kick", "snare", "hihat", "hi hat", "hat ", "clap", "crash", "ride ", "tom ", "shaker", "tambourine", "cowbell", "conga", "rimshot", "rim ", "808", "drum", "percussion", "cymbal", "woodblock")) return Drums;
            if (Has("guitar", "strum", "acoustic", "electric guitar", "riff")) return Guitar;
            if (Has("bass", "sub ", "growl")) return Bass;
            if (Has("piano", "pad ", "keys", "organ", "rhodes", "synth", "chord")) return Keys;
            if (Has("pluck", "banjo", "mandolin", "fiddle", "violin", "strings")) return Guitar;
            return Other;
        }

        /// <summary>If a sound sits in the generic Stems bucket, file it under its
        /// stem type instead (from "(vocals)"-style suffixes, a bare stem file
        /// name, or a "Song — Vocals" tail). Anything else keeps its category.</summary>
        public static string RefineStemCategory(string category, string fileName)
        {
            if (category != Stems) return category;
            string fn = Path.GetFileNameWithoutExtension(fileName ?? "").Trim().ToLowerInvariant();
            if (fn.Length == 0) return category;
            if (fn is "vocals" or "drums" or "bass" or "other") return StemCategory(fn);
            int p = fn.LastIndexOf('(');
            if (p >= 0 && fn.EndsWith(")"))
            {
                string w = fn.Substring(p + 1, fn.Length - p - 2).Trim();
                string c = StemCategory(w);
                if (c != Stems) return c;
            }
            foreach (var sep in new[] { " — ", " - ", "_" })
            {
                int i = fn.LastIndexOf(sep, StringComparison.Ordinal);
                if (i >= 0)
                {
                    string tail = fn.Substring(i + sep.Length).Trim();
                    string c = StemCategory(tail);
                    if (c != Stems) return c;
                }
            }
            return category;
        }

        /// <summary>Maps a sound-pack folder name onto a canonical category.</summary>
        public static string FromPackFolder(string folder) => folder switch
        {
            "Drums" => Drums,
            "Guitar" => Guitar,
            "Bass" => Bass,
            "Keys" => Keys,
            "Loops" => Loops,
            "FX" => Fx,
            _ => Other
        };
    }
}
