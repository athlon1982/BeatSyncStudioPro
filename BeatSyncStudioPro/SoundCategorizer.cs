using System;

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
        public const string Other = "Other";

        public static readonly string[] All = { Drums, Guitar, Bass, Keys, Loops, Vocals, Fx, Other };

        public static string Icon(string category) => category switch
        {
            Drums => "🥁",
            Guitar => "🎸",
            Bass => "🔊",
            Keys => "🎹",
            Loops => "🔁",
            Vocals => "🎤",
            Fx => "✨",
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
