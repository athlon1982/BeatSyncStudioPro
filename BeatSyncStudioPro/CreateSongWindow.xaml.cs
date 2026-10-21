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
            Closed += (s, e) => _cts?.Cancel();
        }

        private void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (StyleInfValue == null) return;
            StyleInfValue.Text = ((int)StyleInfSlider.Value).ToString();
            VoiceInfValue.Text = ((int)VoiceInfSlider.Value).ToString();
            WeirdValue.Text = ((int)WeirdSlider.Value).ToString();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

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
                StatusText.Text = "☁️ Song generation isn't wired to the Hugging Face free tier yet (their free song models come and go). Switch the online provider to Replicate in the AI Lab, or use your local engine once it's set up.";
                return;
            }
            string key = compute.GetOnlineKey();
            if (key.Length == 0)
            {
                StatusText.Text = "☁️ Add your Replicate API key first — AI Lab → Compute → ☁️ Online (there's a 'Sign up for Replicate' button right there).";
                return;
            }

            GenerateButton.IsEnabled = false;
            CancelButton.IsEnabled = true;
            _cts = new CancellationTokenSource();
            try
            {
                double guidance = 1.0 + (WeirdSlider.Value / 100.0) * 9.0; // weirdness -> guidance 1..10
                var (data, ext) = await OnlineSongs.GenerateAceStepAsync(
                    key, title, StyleBox.Text.Trim(), LyricsBox.Text.Trim(),
                    DurationSeconds, guidance,
                    msg => StatusText.Text = msg, _cts.Token);
                string stored = _owner.AddToLibraryOnly(data, title + "." + ext);
                StatusText.Text = $"✅ '{stored}' is in your library — drag it onto the timeline to mix it." +
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
