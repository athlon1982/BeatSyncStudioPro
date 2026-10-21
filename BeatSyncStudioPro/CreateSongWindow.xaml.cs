using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// The personal-Suno panel: lyrics + style + influence sliders + a
    /// voice picker, then Generate. Routing follows the Compute setting:
    /// ☁️ Online/Replicate runs ACE-Step in the cloud today; the local
    /// engine (on the user's stronger PC) plugs into the same button.
    /// Voice conversion to the user's own voice happens as a second stage
    /// once their voice model is trained on the engine PC — the panel is
    /// honest about that instead of faking it.
    /// </summary>
    public partial class CreateSongWindow : Window
    {
        private readonly MainWindow _owner;
        private CancellationTokenSource? _cts;

        public CreateSongWindow(MainWindow owner, string? lyrics = null, string? style = null)
        {
            InitializeComponent();
            _owner = owner;
            if (!string.IsNullOrWhiteSpace(lyrics)) LyricsBox.Text = lyrics;
            if (!string.IsNullOrWhiteSpace(style)) StyleBox.Text = style;
            ComputeText.Text = "Compute: " + ComputeSettings.Load().Describe();
            BuildStyleChips();
            Slider_Changed(this, null); // sync the % labels now that everything exists
            Closed += (s, e) => _cts?.Cancel();
        }

        private void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // The XAML sets each slider's Value WHILE the window is still
            // loading, which fires this handler before the later labels exist.
            // Guard every label on its own (the v3.14 crash was exactly this).
            if (StyleInfValue != null && StyleInfSlider != null) StyleInfValue.Text = ((int)StyleInfSlider.Value).ToString();
            if (VoiceInfValue != null && VoiceInfSlider != null) VoiceInfValue.Text = ((int)VoiceInfSlider.Value).ToString();
            if (WeirdValue != null && WeirdSlider != null) WeirdValue.Text = ((int)WeirdSlider.Value).ToString();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

        private void Providers_Click(object sender, RoutedEventArgs e)
        {
            new ProvidersWindow { Owner = this }.ShowDialog();
            ComputeText.Text = "Compute: " + ComputeSettings.Load().Describe();
        }

        /// <summary>Suno-style preset chips (genres + descriptors, from Suno's style list).</summary>
        private static readonly (string Label, string Prompt)[] StylePresets =
        {
            ("👑 My Style", "hip-hop, boom-rap, heavy 808 sub-bass, hard-hitting bass, country rap, Vermont"),
            ("🚗 Phonk", "drift phonk, distorted 808 bass, memphis vocal chops, aggressive cowbell, dark atmosphere, 140 BPM"),
            ("🔥 Trap", "melodic trap, heavy 808 bass, crisp hi-hat rolls, dark piano loop, atmospheric, 140 BPM"),
            ("🌧️ Lo-Fi", "mellow lo-fi hip hop, dusty vinyl texture, jazzy rhodes chords, warm tape saturation, chill, 82 BPM"),
            ("🤠 Country", "modern country, acoustic guitar, fiddle, heartfelt storytelling, warm, uplifting"),
            ("🚜 Country Rap", "country rap, hick-hop, trap drums over banjo and guitar, muddy 808s, southern, anthemic"),
            ("🎸 Rock", "alternative rock, overdriven electric guitars, live drum kit, gritty vocals, anthemic chorus, 128 BPM"),
            ("🎆 EDM", "festival edm, punchy kick, supersaw chords, risers, euphoric drop, energetic, 126 BPM"),
            ("🌆 Synthwave", "synthwave, retro 80s analog synths, gated reverb drums, dreamy, neon night drive"),
            ("🎷 Soul / Gospel", "soulful gospel, choir harmonies, hammond organ, hand claps, uplifting, powerful vocals"),
            ("🎺 Blues", "gritty blues, harmonica, slide guitar, slow groove, raspy vocals, heartfelt"),
            ("🪕 Folk", "indie folk, acoustic guitar, soft harmonies, reflective, warm, melodic"),
        };

        private void BuildStyleChips()
        {
            foreach (var (label, prompt) in StylePresets)
            {
                var b = new Button
                {
                    Content = label,
                    Margin = new Thickness(0, 0, 6, 6),
                    Padding = new Thickness(9, 3, 9, 3),
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x3C)),
                    Foreground = System.Windows.Media.Brushes.White,
                    BorderThickness = new Thickness(0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = prompt
                };
                b.Click += (s, e) => StyleBox.Text = prompt;
                StyleChips.Children.Add(b);
            }
        }

        private string? _backingBeatName;

        /// <summary>
        /// The automatic backing track: a beat in the chosen style from the
        /// built-in generator — added as an editable pattern AND a library loop.
        /// </summary>
        private void AutoBeat_Click(object sender, RoutedEventArgs e)
        {
            int bpm = int.TryParse(BpmBox.Text, out var b) ? Math.Clamp(b, 40, 240) : 95;
            string s = StyleBox.Text.ToLowerInvariant();
            string key = s.Contains("phonk") ? "phonk"
                : s.Contains("dubstep") ? "dubstep"
                : s.Contains("synthwave") ? "synthwave"
                : s.Contains("afrobeat") ? "afrobeat"
                : s.Contains("lo-fi") || s.Contains("lofi") ? "lofi"
                : s.Contains("drill") ? "drill"
                : s.Contains("dancehall") ? "dancehall"
                : s.Contains("reggaeton") || s.Contains("dembow") ? "dembow"
                : s.Contains("house") ? "house"
                : s.Contains("techno") ? "techno"
                : s.Contains("drum and bass") || s.Contains("dnb") ? "dnb"
                : s.Contains("funk") ? "funk"
                : s.Contains("west coast") || s.Contains("g-funk") ? "westcoast"
                : s.Contains("country") && !s.Contains("rap") ? "country"
                : s.Contains("rock") ? "rock"
                : s.Contains("trap") ? "trap"
                : "boomrap";
            var pattern = DrumGenerator.Generate(key, bpm, 0.8, true, Environment.TickCount);
            string title = string.IsNullOrWhiteSpace(TitleBox.Text) ? "Song" : TitleBox.Text.Trim();
            pattern.Name = title + " Beat";
            _owner.Patterns.Add(pattern);
            byte[] wav = PatternRenderer.RenderWav(_owner, pattern, 8);
            if (wav.Length > 0)
            {
                _backingBeatName = _owner.AddToLibraryOnly(wav, pattern.Name + ".wav", SoundCategorizer.Loops);
                BeatText.Text = $"🥁 '{_backingBeatName}' — {bpm} BPM, filed under 🔁 Loops. The pattern's in 🥁 Patterns too, ready to edit. Layer the generated song over it on the timeline.";
            }
            else
            {
                BeatText.Text = "The pattern was created (see 🥁 Patterns), but it rendered silent — open it and check the channel sounds.";
            }
        }

        private int DurationSeconds => DurationPicker.SelectedIndex switch { 0 => 30, 1 => 60, 2 => 120, 4 => 240, _ => 180 };

        private async void Generate_Click(object sender, RoutedEventArgs e)
        {
            var compute = ComputeSettings.Load();
            string title = string.IsNullOrWhiteSpace(TitleBox.Text) ? "Untitled Song" : TitleBox.Text.Trim();
            bool radDad = VoicePicker.SelectedIndex == 1;

            if (compute.Mode != "Online")
            {
                StatusText.Text = "🎙️ Your local song engine isn't set up yet. It runs on your stronger PC (the GTX 1660 desktop or the RTX 3050 laptop) — tell Jarvis which one and he'll install the generator there and train your 👑 Rad-Dad voice from your songs. " +
                    "Everything in this window (lyrics, style, sliders, voice) is exactly what it will use. For cloud generation right now, set Compute to ☁️ Online in the AI Lab.";
                return;
            }
            if (compute.OnlineProvider == "huggingface")
            {
                StatusText.Text = "☁️ Song generation isn't wired to the Hugging Face free tier yet (their free song models come and go). Switch the online provider to Replicate or fal.ai in the AI Lab, or use your local engine once it's set up.";
                return;
            }
            bool remote = compute.OnlineProvider == "remote";
            var remoteUrls = compute.RemoteEngineUrls();
            if (remote && remoteUrls.Count == 0)
            {
                StatusText.Text = "🏠 Your Remote GPU provider is picked, but there's no engine address yet. Open 🌐 Providers (the button below) → the 🏠 Remote GPU card → type an engine PC's address (like http://192.168.1.50:7860) and Save. The free BeatSync Engine has to be running on that PC (either of them).";
                return;
            }
            bool fal = compute.OnlineProvider == "fal";
            if (!remote && fal && compute.GetFalKey().Length == 0)
            {
                StatusText.Text = "☁️ Add your fal.ai API key first — AI Lab → 🔑 Keys & Compute tab (fal.ai is paid per run; there's a signup button right there).";
                return;
            }
            string key = compute.GetOnlineKey();
            if (!remote && !fal && key.Length == 0)
            {
                StatusText.Text = "☁️ Add your Replicate API key first — AI Lab → 🔑 Keys & Compute tab (there's a 'Sign up for Replicate' button right there).";
                return;
            }

            GenerateButton.IsEnabled = false;
            CancelButton.IsEnabled = true;
            _cts = new CancellationTokenSource();
            try
            {
                double guidance = 1.0 + (WeirdSlider.Value / 100.0) * 9.0; // weirdness -> guidance 1..10
                var (data, ext) = fal
                    ? await FalOnline.GenerateAceStepAsync(
                        compute.GetFalKey(), StyleBox.Text.Trim(), LyricsBox.Text.Trim(),
                        DurationSeconds, msg => StatusText.Text = msg, _cts.Token)
                    : remote
                    ? await RemoteEngine.GenerateSongPooledAsync(
                        remoteUrls, title, StyleBox.Text.Trim(), LyricsBox.Text.Trim(),
                        DurationSeconds, msg => StatusText.Text = msg, _cts.Token)
                    : await OnlineSongs.GenerateAceStepAsync(
                        key, title, StyleBox.Text.Trim(), LyricsBox.Text.Trim(),
                        DurationSeconds, guidance,
                        msg => StatusText.Text = msg, _cts.Token);
                string stored = _owner.AddToLibraryOnly(data, title + "." + ext);
                StatusText.Text = $"✅ '{stored}' is in your library — drag it onto the timeline to mix it." +
                    (_backingBeatName != null ? $"\n🥁 Your backing beat '{_backingBeatName}' is in the library too — drop it on the track under the song." : "") +
                    (radDad
                        ? "\n👑 Voice note: this take is ACE-Step's own singer. Singing it in YOUR Rad-Dad voice is the voice-conversion stage, which runs once your voice model is trained on your engine PC — the Voice Influence slider above is saved for that stage."
                        : "");
            }
            catch (OperationCanceledException) { StatusText.Text = "Generation cancelled."; }
            catch (Exception ex) { StatusText.Text = "Generation failed: " + ex.Message; }
            finally
            {
                GenerateButton.IsEnabled = true;
                CancelButton.IsEnabled = false;
                _cts?.Dispose(); _cts = null;
            }
        }
    }
}
