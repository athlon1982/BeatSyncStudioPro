using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// The AI Lab: pick a provider (OpenCode's free models, local Ollama,
    /// or an OpenAI-compatible service like Gemini / OpenRouter / ChatGPT)
    /// and generate lyrics, title ideas, or a beat straight into the
    /// step sequencer.
    /// </summary>
    public partial class AiLabWindow : Window
    {
        private readonly MainWindow _owner;
        private readonly AiSettings _settings;
        private readonly ComputeSettings _compute;
        private bool _computeLoading;
        private List<IAiProvider> _providers = new List<IAiProvider>();
        private CancellationTokenSource? _cts;
        private CancellationTokenSource? _storyCts;
        private CancellationTokenSource? _optCts;
        private CancellationTokenSource? _rwCts;
        private CancellationTokenSource? _ytCts;
        private CancellationTokenSource? _hitCts;
        private CancellationTokenSource? _visCts;
        private CancellationTokenSource? _sgCts;
        private CancellationTokenSource? _apCts;
        private CancellationTokenSource? _chCts;
        private CancellationTokenSource? _mvCts;
        private CancellationTokenSource? _cvCts;
        private CancellationTokenSource? _skCts;
        private byte[]? _cvBytes;
        private bool _cvPromptDirty;
        private string _visImageBase64 = "";
        private string _ytTitle = "";
        private string _ytAuthor = "";

        private static readonly Dictionary<string, string> InfoByProvider = new Dictionary<string, string>
        {
            ["opencode"] = "Uses the OpenCode app on this PC and its free models (including the OpenRouter free models connected there) — no key needed, signed in as you. First answers can take a while; free models spin up slowly.",
            ["ollama"] = "Runs 100% on this PC — nothing leaves the machine. Needs Ollama installed with a model pulled (e.g. ollama pull llama3.1:8b).",
            ["openai"] = "ChatGPT via OpenAI's API. OpenAI charges for API use, and a ChatGPT subscription does not include API credits.",
            ["gemini"] = "Google Gemini. Free API key: aistudio.google.com → “Get API key” — the free tier is generous.",
            ["openrouter"] = "OpenRouter gathers many models behind one key. Free key at openrouter.ai → Keys; pick models ending in :free for $0 runs.",
            ["custom"] = "Any OpenAI-compatible endpoint — LM Studio on this PC, another cloud, your own server."
        };

        private static readonly Dictionary<string, string> KeyHintByProvider = new Dictionary<string, string>
        {
            ["openai"] = "Paste your OpenAI API key (platform.openai.com → API keys). Stored encrypted on this PC, only ever sent to OpenAI.",
            ["gemini"] = "Paste your Google AI Studio API key. Stored encrypted on this PC, only ever sent to Google.",
            ["openrouter"] = "Paste your OpenRouter API key. Stored encrypted on this PC, only ever sent to OpenRouter.",
            ["custom"] = "Base URL + key (if the endpoint needs one). Stored encrypted on this PC."
        };

        public AiLabWindow(MainWindow owner)
        {
            InitializeComponent();
            _owner = owner;
            _settings = AiSettings.Load();
            _compute = ComputeSettings.Load();
            BuildProviders();
            InitCompute();
            Loaded += async (s, e) => await RefreshModelsAsync();
        }

        private void InitCompute()
        {
            _computeLoading = true;
            ComputePicker.SelectedIndex = _compute.Mode switch { "CPU" => 1, "GPU" => 2, "Online" => 3, _ => 0 };
            OnlineProviderPicker.SelectedIndex = _compute.OnlineProvider switch { "huggingface" => 1, "fal" => 2, "remote" => 3, _ => 0 };
            LoadProviderKey();
            var gpus = ComputeSettings.GpuNames();
            GpuPicker.Items.Clear();
            if (gpus.Count == 0)
            {
                GpuPicker.Items.Add(new ComboBoxItem { Content = "(no GPU detected)", IsEnabled = false });
                GpuPicker.SelectedIndex = 0;
            }
            else
            {
                for (int i = 0; i < gpus.Count; i++)
                    GpuPicker.Items.Add(new ComboBoxItem { Content = $"GPU {i} — {gpus[i]}", Tag = i });
                GpuPicker.SelectedIndex = Math.Min(_compute.GpuIndex, gpus.Count - 1);
            }
            _computeLoading = false;
            RefreshComputeInfo();
        }

        private void RefreshComputeInfo()
        {
            GpuPicker.IsEnabled = _compute.Mode == "GPU";
            OnlinePanel.Visibility = _compute.Mode == "Online" ? Visibility.Visible : Visibility.Collapsed;
            ComputeInfo.Text = _compute.Describe();
        }

        private void ComputePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_computeLoading || !IsLoaded) return;
            _compute.Mode = ComputePicker.SelectedIndex switch { 1 => "CPU", 2 => "GPU", 3 => "Online", _ => "Auto" };
            _compute.Save();
            RefreshComputeInfo();
        }

        private CreateSongWindow? _createSong;
        private void Providers_Click(object sender, RoutedEventArgs e)
        {
            new ProvidersWindow { Owner = this }.ShowDialog();
        }

        // ---------- 🎬 Video tab (fal.ai text-to-video) ----------
        private string? _lastVideoPath;
        private CancellationTokenSource? _videoCts;

        private async void VideoGen_Click(object sender, RoutedEventArgs e)
        {
            string key = ComputeSettings.Load().GetFalKey();
            if (string.IsNullOrWhiteSpace(key))
            {
                VideoStatus.Text = "Video runs on fal.ai — add your fal.ai key first (Keys & Compute tab). It's paid per clip.";
                return;
            }
            string prompt = VideoPromptBox.Text.Trim();
            if (prompt.Length == 0)
            {
                VideoStatus.Text = "Write a prompt first — describe the shot: subject, place, camera move, mood.";
                return;
            }
            string model = (VideoModelBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "fal-ai/minimax/video-01";
            _videoCts?.Cancel();
            _videoCts = new CancellationTokenSource();
            var ct = _videoCts.Token;
            VideoGenBtn.IsEnabled = false;
            VideoProg.Visibility = Visibility.Visible;
            try
            {
                string url = await FalOnline.GenerateVideoAsync(key, model, prompt,
                    s => Dispatcher.Invoke(() => VideoStatus.Text = s), ct);
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "BeatSyncStudioPro", "Generated");
                Directory.CreateDirectory(dir);
                string dest = Path.Combine(dir, "video-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".mp4");
                VideoStatus.Text = "☁️ fal.ai: clip ready — downloading… (watch the ⬇ Downloads window too)";
                await DownloadManager.DownloadToFileAsync(url, Path.GetFileName(dest), dest,
                    p => Dispatcher.Invoke(() => VideoStatus.Text = $"⬇ Downloading your clip… {p.Percent:0}%"));
                _lastVideoPath = dest;
                VideoPlayer.Source = new Uri(dest);
                VideoPlayer.Play();
                VideoAddBtn.IsEnabled = true;
                VideoOpenBtn.IsEnabled = true;
                VideoStatus.Text = "Done — your clip is previewing above. 🎬 Add to my project files it in the library under Video.";
            }
            catch (OperationCanceledException)
            {
                VideoStatus.Text = "Video generation cancelled.";
            }
            catch (Exception ex)
            {
                VideoStatus.Text = ex.Message.Length > 320 ? ex.Message.Substring(0, 320) + "…" : ex.Message;
            }
            finally
            {
                VideoGenBtn.IsEnabled = true;
                VideoProg.Visibility = Visibility.Collapsed;
            }
        }

        private void VideoPlay_Click(object sender, RoutedEventArgs e) { if (VideoPlayer.Source != null) VideoPlayer.Play(); }
        private void VideoPause_Click(object sender, RoutedEventArgs e) { if (VideoPlayer.Source != null) VideoPlayer.Pause(); }
        private void VideoStop_Click(object sender, RoutedEventArgs e) { if (VideoPlayer.Source != null) VideoPlayer.Stop(); }

        private void VideoAdd_Click(object sender, RoutedEventArgs e)
        {
            if (_lastVideoPath != null && File.Exists(_lastVideoPath))
            {
                _owner.AddGeneratedVideoToLibrary(_lastVideoPath);
                VideoStatus.Text = $"Filed in your library as 🎬 {Path.GetFileName(_lastVideoPath)} — drag it onto the video track in the main window.";
            }
        }

        private void VideoOpen_Click(object sender, RoutedEventArgs e)
        {
            if (_lastVideoPath != null && File.Exists(_lastVideoPath))
                Process.Start("explorer.exe", "/select,\"" + _lastVideoPath + "\"");
        }

        private void CreateSongFromThis_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(OutputBox.Text))
            {
                StatusText.Text = "Generate some lyrics first — then this button carries them into Create Song with your style and voice ready.";
                return;
            }
            if (_createSong != null) { _createSong.Activate(); return; }
            // Carry the lyrics AND Michael's standing style into Create Song,
            // where the style chips, voice picker, sliders and auto-beat live.
            // Wrapped: a window-open error must never take the whole app down.
            try
            {
                _createSong = new CreateSongWindow(_owner, OutputBox.Text,
                    "hip-hop, boom-rap, heavy 808 sub-bass, hard-hitting bass, country rap, Vermont") { Owner = this };
                _createSong.Closed += (s, ev) => _createSong = null;
                _createSong.Show();
            }
            catch (Exception ex)
            {
                _createSong = null;
                StatusText.Text = "Create Song couldn't open: " + ex.Message;
            }
        }

        // ---- 📖 Story to Lyrics (AI Toolkit, part 1) ----
        // Same provider/model plumbing as the 🎤 Song tab; the story, genre,
        // mood and structure shape the draft, and the answer is split back
        // into lyrics + a style suggestion + title ideas.

        private static string ComboText(ComboBox box, string fallback)
        {
            if (box.SelectedItem is ComboBoxItem item && item.Content is string text) return text;
            return fallback;
        }

        private string BuildStoryPrompt(string story, string genre, string mood, string structure)
        {
            return "Write a complete song for Rad-Dad, a Vermont country-rap (boom-rap) artist — heavy 808 bass, honest small-town Vermont life, family, trucks, hard work, no explicit content. " +
                   $"The song is {genre} in feel and {mood} in mood. Song structure: {structure}. " +
                   $"Story / concept: {story}. " +
                   "Output ONLY in this exact shape, no commentary outside these sections:\n" +
                   "[LYRICS]\nFull lyrics with section labels like [Verse 1], [Chorus], [Verse 2], [Bridge], [Outro] as the structure calls for.\n" +
                   "[STYLE]\nOne line: a style prompt describing this song's sound (genre, instruments, tempo feel, vocal style).\n" +
                   "[TITLES]\nFive original title ideas, one per line, numbered.";
        }

        private void FillStoryResults(string reply)
        {
            string lyrics = reply, style = "", titles = "";
            int lyricsIdx = reply.IndexOf("[LYRICS]", StringComparison.OrdinalIgnoreCase);
            int styleIdx = reply.IndexOf("[STYLE]", StringComparison.OrdinalIgnoreCase);
            int titlesIdx = reply.IndexOf("[TITLES]", StringComparison.OrdinalIgnoreCase);
            if (lyricsIdx >= 0 || styleIdx >= 0 || titlesIdx >= 0)
            {
                int lyricsStart = lyricsIdx >= 0 ? lyricsIdx + "[LYRICS]".Length : 0;
                int lyricsEnd = styleIdx >= 0 ? styleIdx : (titlesIdx >= 0 ? titlesIdx : reply.Length);
                lyrics = lyricsEnd > lyricsStart ? reply.Substring(lyricsStart, lyricsEnd - lyricsStart).Trim() : "";
                if (styleIdx >= 0)
                {
                    int styleStart = styleIdx + "[STYLE]".Length;
                    int styleEnd = titlesIdx >= 0 && titlesIdx > styleIdx ? titlesIdx : reply.Length;
                    style = styleEnd > styleStart ? reply.Substring(styleStart, styleEnd - styleStart).Trim() : "";
                }
                if (titlesIdx >= 0)
                {
                    int titlesStart = titlesIdx + "[TITLES]".Length;
                    titles = reply.Length > titlesStart ? reply.Substring(titlesStart).Trim() : "";
                }
            }
            StoryLyricsBox.Text = lyrics;
            StoryStyleBox.Text = style.Length > 0 ? style : "No style line came back — your standing style still applies: boom-rap, heavy 808 sub-bass, hard-hitting bass.";
            StoryTitlesBox.Text = titles;
        }

        private async void StoryGenerate_Click(object sender, RoutedEventArgs e)
        {
            string story = StoryBox.Text.Trim();
            if (story.Length == 0)
            {
                StoryStatusText.Text = "Type a story or idea first.";
                return;
            }
            string genre = ComboText(GenrePicker, "Boom-Rap (Rad-Dad style)");
            string mood = ComboText(MoodPicker, "Nostalgic");
            string structure = ComboText(StructurePicker, "Verse-Chorus-Verse-Chorus-Bridge-Chorus-Outro");
            string model = (ModelPicker.Text ?? "").Trim();
            _settings.Model = model;
            _settings.ProviderId = Current.Id;
            _settings.Save();

            var provider = Current;
            _storyCts = new CancellationTokenSource();
            StoryGenerateBtn.IsEnabled = false;
            StoryCancelBtn.IsEnabled = true;
            StoryStatusText.Text = $"Asking {provider.DisplayName} — free and local models can take a minute…";
            try
            {
                string reply = await provider.ChatAsync(model, BuildStoryPrompt(story, genre, mood, structure), _storyCts.Token);
                FillStoryResults(reply);
                StoryStatusText.Text = "Done — edit the lyrics, then hit 🎤 Make THESE Lyrics into a Song to add your style, voice and a beat.";
            }
            catch (OperationCanceledException)
            {
                StoryStatusText.Text = "Cancelled.";
            }
            catch (Exception ex)
            {
                StoryStatusText.Text = "Error: " + ex.Message;
            }
            finally
            {
                StoryGenerateBtn.IsEnabled = true;
                StoryCancelBtn.IsEnabled = false;
                _storyCts?.Dispose();
                _storyCts = null;
            }
        }

        private void StoryCancel_Click(object sender, RoutedEventArgs e) => _storyCts?.Cancel();

        private void StoryCopy_Click(object sender, RoutedEventArgs e)
        {
            if (StoryLyricsBox.Text.Length > 0)
            {
                Clipboard.SetText(StoryLyricsBox.Text);
                StoryStatusText.Text = "Lyrics copied ✓";
            }
        }

        private void StoryMakeSong_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(StoryLyricsBox.Text))
            {
                StoryStatusText.Text = "Generate some lyrics first — then this button carries them into Create Song with your style and voice ready.";
                return;
            }
            if (_createSong != null) { _createSong.Activate(); return; }
            // Same carry-over as the 🎤 Song tab, but from the story draft.
            // Wrapped: a window-open error must never take the whole app down.
            try
            {
                _createSong = new CreateSongWindow(_owner, StoryLyricsBox.Text,
                    "hip-hop, boom-rap, heavy 808 sub-bass, hard-hitting bass, country rap, Vermont") { Owner = this };
                _createSong.Closed += (s, ev) => _createSong = null;
                _createSong.Show();
            }
            catch (Exception ex)
            {
                _createSong = null;
                StoryStatusText.Text = "Create Song couldn't open: " + ex.Message;
            }
        }

        // ---- ✨ Lyrics Optimizer (AI Toolkit, part 2) ----
        // Same provider/model plumbing as the 🎤 Song and 📖 Story tabs.
        // The story and meaning stay the user's; only flow, rhyme and
        // singability get tightened, shaped by meta tags + vocal coaching.

        private string BuildOptimizePrompt(string lyrics, string tags, string vocal)
        {
            string tagsPart = tags.Length > 0 ? $"Meta tags to honor: {tags}. " : "";
            string vocalPart = vocal.Length > 0 ? $"Vocal coaching: {vocal}. " : "";
            return "Polish these song lyrics for Rad-Dad, a Vermont country-rap (boom-rap) artist — heavy 808 bass, honest small-town Vermont life, family, trucks, hard work, no explicit content. " +
                   "Keep the story and meaning; improve flow, rhyme, and singability; tighten awkward lines. " +
                   tagsPart + vocalPart +
                   $"Original lyrics: {lyrics}. " +
                   "Output ONLY in this exact shape, no commentary outside these sections:\n" +
                   "[LYRICS]\nThe full polished lyrics with section labels like [Verse 1], [Chorus], [Verse 2], [Bridge], [Outro] as the original calls for.\n" +
                   "[STYLE]\nOne line: a style prompt describing this song's sound (genre, instruments, tempo feel, vocal style).";
        }

        private void FillOptimizedResults(string reply)
        {
            string lyrics = reply, style = "";
            int lyricsIdx = reply.IndexOf("[LYRICS]", StringComparison.OrdinalIgnoreCase);
            int styleIdx = reply.IndexOf("[STYLE]", StringComparison.OrdinalIgnoreCase);
            if (lyricsIdx >= 0 || styleIdx >= 0)
            {
                int lyricsStart = lyricsIdx >= 0 ? lyricsIdx + "[LYRICS]".Length : 0;
                int lyricsEnd = styleIdx >= 0 ? styleIdx : reply.Length;
                lyrics = lyricsEnd > lyricsStart ? reply.Substring(lyricsStart, lyricsEnd - lyricsStart).Trim() : "";
                if (styleIdx >= 0)
                {
                    int styleStart = styleIdx + "[STYLE]".Length;
                    style = reply.Length > styleStart ? reply.Substring(styleStart).Trim() : "";
                }
            }
            OptOutLyricsBox.Text = lyrics;
            OptStyleBox.Text = style.Length > 0 ? style : "No style line came back — your standing style still applies: boom-rap, heavy 808 sub-bass, hard-hitting bass.";
        }

        private async void OptGenerate_Click(object sender, RoutedEventArgs e)
        {
            string lyrics = OptLyricsBox.Text.Trim();
            if (lyrics.Length == 0)
            {
                OptStatusText.Text = "Paste some lyrics first.";
                return;
            }
            string tags = MetaTagsBox.Text.Trim();
            string vocal = VocalBox.Text.Trim();
            string model = (ModelPicker.Text ?? "").Trim();
            _settings.Model = model;
            _settings.ProviderId = Current.Id;
            _settings.Save();

            var provider = Current;
            _optCts = new CancellationTokenSource();
            OptGenerateBtn.IsEnabled = false;
            OptCancelBtn.IsEnabled = true;
            OptStatusText.Text = $"Asking {provider.DisplayName} — free and local models can take a minute…";
            try
            {
                string reply = await provider.ChatAsync(model, BuildOptimizePrompt(lyrics, tags, vocal), _optCts.Token);
                FillOptimizedResults(reply);
                OptStatusText.Text = "Done — edit the polished lyrics, then hit 🎤 Make THESE Lyrics into a Song to add your style, voice and a beat.";
            }
            catch (OperationCanceledException)
            {
                OptStatusText.Text = "Cancelled.";
            }
            catch (Exception ex)
            {
                OptStatusText.Text = "Error: " + ex.Message;
            }
            finally
            {
                OptGenerateBtn.IsEnabled = true;
                OptCancelBtn.IsEnabled = false;
                _optCts?.Dispose();
                _optCts = null;
            }
        }

        private void OptCancel_Click(object sender, RoutedEventArgs e) => _optCts?.Cancel();

        private void OptCopy_Click(object sender, RoutedEventArgs e)
        {
            if (OptOutLyricsBox.Text.Length > 0)
            {
                Clipboard.SetText(OptOutLyricsBox.Text);
                OptStatusText.Text = "Optimized lyrics copied ✓";
            }
        }

        private void OptMakeSong_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(OptOutLyricsBox.Text))
            {
                OptStatusText.Text = "Optimize some lyrics first — then this button carries them into Create Song with your style and voice ready.";
                return;
            }
            if (_createSong != null) { _createSong.Activate(); return; }
            // Same carry-over as the 🎤 Song and 📖 Story tabs.
            // Wrapped: a window-open error must never take the whole app down.
            try
            {
                _createSong = new CreateSongWindow(_owner, OptOutLyricsBox.Text,
                    "hip-hop, boom-rap, heavy 808 sub-bass, hard-hitting bass, country rap, Vermont") { Owner = this };
                _createSong.Closed += (s, ev) => _createSong = null;
                _createSong.Show();
            }
            catch (Exception ex)
            {
                _createSong = null;
                OptStatusText.Text = "Create Song couldn't open: " + ex.Message;
            }
        }

        // ---- 🔄 Lyrics Rewriter (AI Toolkit, part 3) ----
        // Same provider/model plumbing as the 🎤 Song, 📖 Story and
        // ✨ Optimizer tabs. A rewrite is a NEW draft — same idea, new
        // words — not a polish of the original lines.

        private string BuildRewritePrompt(string lyrics, string goal, string direction)
        {
            string directionPart = direction.Length > 0 ? $"Direction: {direction}. " : "";
            return "Rewrite these song lyrics for Rad-Dad, a Vermont country-rap (boom-rap) artist — heavy 808 bass, honest small-town Vermont life, family, trucks, hard work, no explicit content. " +
                   $"Rewrite goal: {goal}. " + directionPart +
                   "Write a fresh draft with new wording and imagery; keep the song singable and the section structure the original calls for. " +
                   $"Original lyrics: {lyrics}. " +
                   "Output ONLY in this exact shape, no commentary outside these sections, and do not stop until you have written BOTH sections — the [STYLE] line is required, not optional:\n" +
                   "[LYRICS]\nThe full rewritten lyrics with section labels like [Verse 1], [Chorus], [Verse 2], [Bridge], [Outro] as the rewrite calls for.\n" +
                   "[STYLE]\nOne line: a style prompt describing this song's sound (genre, instruments, tempo feel, vocal style).";
        }

        private void FillRewrittenResults(string reply)
        {
            string lyrics = reply, style = "";
            int lyricsIdx = reply.IndexOf("[LYRICS]", StringComparison.OrdinalIgnoreCase);
            int styleIdx = reply.IndexOf("[STYLE]", StringComparison.OrdinalIgnoreCase);
            if (lyricsIdx >= 0 || styleIdx >= 0)
            {
                int lyricsStart = lyricsIdx >= 0 ? lyricsIdx + "[LYRICS]".Length : 0;
                int lyricsEnd = styleIdx >= 0 ? styleIdx : reply.Length;
                lyrics = lyricsEnd > lyricsStart ? reply.Substring(lyricsStart, lyricsEnd - lyricsStart).Trim() : "";
                if (styleIdx >= 0)
                {
                    int styleStart = styleIdx + "[STYLE]".Length;
                    style = reply.Length > styleStart ? reply.Substring(styleStart).Trim() : "";
                }
            }
            RwOutLyricsBox.Text = lyrics;
            RwStyleBox.Text = style.Length > 0 ? style : "No style line came back — your standing style still applies: boom-rap, heavy 808 sub-bass, hard-hitting bass.";
        }

        private async void RwGenerate_Click(object sender, RoutedEventArgs e)
        {
            string lyrics = RwLyricsBox.Text.Trim();
            if (lyrics.Length == 0)
            {
                RwStatusText.Text = "Paste some lyrics first.";
                return;
            }
            string goal = (RwGoalPicker.Text ?? "").Trim();
            string direction = RwDirectionBox.Text.Trim();
            string model = (ModelPicker.Text ?? "").Trim();
            _settings.Model = model;
            _settings.ProviderId = Current.Id;
            _settings.Save();

            var provider = Current;
            _rwCts = new CancellationTokenSource();
            RwGenerateBtn.IsEnabled = false;
            RwCancelBtn.IsEnabled = true;
            RwStatusText.Text = $"Asking {provider.DisplayName} — free and local models can take a minute…";
            try
            {
                string reply = await provider.ChatAsync(model, BuildRewritePrompt(lyrics, goal, direction), _rwCts.Token);
                FillRewrittenResults(reply);
                RwStatusText.Text = "Done — edit the rewritten lyrics, then hit 🎤 Make THESE Lyrics into a Song to add your style, voice and a beat.";
            }
            catch (OperationCanceledException)
            {
                RwStatusText.Text = "Cancelled.";
            }
            catch (Exception ex)
            {
                RwStatusText.Text = "Error: " + ex.Message;
            }
            finally
            {
                RwGenerateBtn.IsEnabled = true;
                RwCancelBtn.IsEnabled = false;
                _rwCts?.Dispose();
                _rwCts = null;
            }
        }

        private void RwCancel_Click(object sender, RoutedEventArgs e) => _rwCts?.Cancel();

        private void RwCopy_Click(object sender, RoutedEventArgs e)
        {
            if (RwOutLyricsBox.Text.Length > 0)
            {
                Clipboard.SetText(RwOutLyricsBox.Text);
                RwStatusText.Text = "Rewritten lyrics copied ✓";
            }
        }

        private void RwMakeSong_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(RwOutLyricsBox.Text))
            {
                RwStatusText.Text = "Rewrite some lyrics first — then this button carries them into Create Song with your style and voice ready.";
                return;
            }
            if (_createSong != null) { _createSong.Activate(); return; }
            // Same carry-over as the 🎤 Song, 📖 Story and ✨ Optimizer tabs.
            // Wrapped: a window-open error must never take the whole app down.
            try
            {
                _createSong = new CreateSongWindow(_owner, RwOutLyricsBox.Text,
                    "hip-hop, boom-rap, heavy 808 sub-bass, hard-hitting bass, country rap, Vermont") { Owner = this };
                _createSong.Closed += (s, ev) => _createSong = null;
                _createSong.Show();
            }
            catch (Exception ex)
            {
                _createSong = null;
                RwStatusText.Text = "Create Song couldn't open: " + ex.Message;
            }
        }

        // ---- ▶️ Youtube to Style (AI Toolkit, part 4) ----
        // Reads a video's title + creator through YouTube's free oEmbed
        // lookup (no API key), then the AI writes a style prompt and a
        // lyrics CONCEPT for a NEW song in that style. No lyrics are ever
        // copied from the video. If the link can't be read, the tab says
        // so and generation still works from the user's notes alone.

        private async void YtExtract_Click(object sender, RoutedEventArgs e)
        {
            string link = YtLinkBox.Text.Trim();
            if (link.Length == 0)
            {
                YtFoundText.Text = "Paste a YouTube link first.";
                return;
            }
            YtFoundText.Text = "Reading video…";
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                string url = "https://www.youtube.com/oembed?url=" + Uri.EscapeDataString(link) + "&format=json";
                string json = await client.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                _ytTitle = doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                _ytAuthor = doc.RootElement.TryGetProperty("author_name", out var a) ? a.GetString() ?? "" : "";
                YtFoundText.Text = _ytTitle.Length > 0
                    ? $"Found: {_ytTitle}" + (_ytAuthor.Length > 0 ? $" — {_ytAuthor}" : "")
                    : "That link didn't give back a title — you can still generate from your notes below.";
            }
            catch (Exception ex)
            {
                _ytTitle = "";
                _ytAuthor = "";
                YtFoundText.Text = "Couldn't read that link (" + ex.Message + ") — you can still generate from your notes below.";
            }
        }

        private string BuildYtPrompt(string link, string title, string author, string notes)
        {
            string videoPart = title.Length > 0
                ? $"Video title: {title}." + (author.Length > 0 ? $" Creator: {author}." : "")
                : $"Video link: {link} (title could not be read).";
            string notesPart = notes.Length > 0 ? $" Listener notes on the sound: {notes}." : "";
            return "You are a music production analyst. The user wants a NEW original song in the style of a YouTube video — do not copy any lyrics from the video (you do not have them) and do not reproduce copyrighted lyrics. " +
                   videoPart + notesPart +
                   " Write a style prompt capturing this production's sound (genre, instruments, tempo feel, mood, vocal style) and a lyrics concept for a NEW song (theme, story angle, key imagery, suggested structure). " +
                   "Output ONLY in this exact shape, no commentary outside these sections, and do not stop until you have written BOTH sections — both are required, not optional:\n" +
                   "[STYLE]\nOne style prompt for a song generator, in the spirit of this production.\n" +
                   "[CONCEPT]\nA lyrics concept prompt: theme, story angle, key imagery, and a suggested song structure for a NEW original song.";
        }

        private void FillYtResults(string reply)
        {
            string style = reply, concept = "";
            int styleIdx = reply.IndexOf("[STYLE]", StringComparison.OrdinalIgnoreCase);
            int conceptIdx = reply.IndexOf("[CONCEPT]", StringComparison.OrdinalIgnoreCase);
            if (styleIdx >= 0 || conceptIdx >= 0)
            {
                int styleStart = styleIdx >= 0 ? styleIdx + "[STYLE]".Length : 0;
                int styleEnd = conceptIdx >= 0 ? conceptIdx : reply.Length;
                style = styleEnd > styleStart ? reply.Substring(styleStart, styleEnd - styleStart).Trim() : "";
                if (conceptIdx >= 0)
                {
                    int conceptStart = conceptIdx + "[CONCEPT]".Length;
                    concept = reply.Length > conceptStart ? reply.Substring(conceptStart).Trim() : "";
                }
            }
            YtStyleBox.Text = style;
            YtConceptBox.Text = concept.Length > 0 ? concept : "No concept came back — try again, or add notes about what you hear and the story you want.";
        }

        private async void YtGenerate_Click(object sender, RoutedEventArgs e)
        {
            string link = YtLinkBox.Text.Trim();
            string notes = YtNotesBox.Text.Trim();
            if (link.Length == 0 && notes.Length == 0)
            {
                YtStatusText.Text = "Paste a YouTube link or add notes about the sound first.";
                return;
            }
            string model = (ModelPicker.Text ?? "").Trim();
            _settings.Model = model;
            _settings.ProviderId = Current.Id;
            _settings.Save();

            var provider = Current;
            _ytCts = new CancellationTokenSource();
            YtGenerateBtn.IsEnabled = false;
            YtCancelBtn.IsEnabled = true;
            YtStatusText.Text = $"Asking {provider.DisplayName} — free and local models can take a minute…";
            try
            {
                string reply = await provider.ChatAsync(model, BuildYtPrompt(link, _ytTitle, _ytAuthor, notes), _ytCts.Token);
                FillYtResults(reply);
                YtStatusText.Text = "Done — edit the style and concept, then hit 🎤 Use This Style in Create Song to build on it.";
            }
            catch (OperationCanceledException)
            {
                YtStatusText.Text = "Cancelled.";
            }
            catch (Exception ex)
            {
                YtStatusText.Text = "Error: " + ex.Message;
            }
            finally
            {
                YtGenerateBtn.IsEnabled = true;
                YtCancelBtn.IsEnabled = false;
                _ytCts?.Dispose();
                _ytCts = null;
            }
        }

        private void YtCancel_Click(object sender, RoutedEventArgs e) => _ytCts?.Cancel();

        private void YtCopyStyle_Click(object sender, RoutedEventArgs e)
        {
            if (YtStyleBox.Text.Length > 0)
            {
                Clipboard.SetText(YtStyleBox.Text);
                YtStatusText.Text = "Style prompt copied ✓";
            }
        }

        private void YtCopyConcept_Click(object sender, RoutedEventArgs e)
        {
            if (YtConceptBox.Text.Length > 0)
            {
                Clipboard.SetText(YtConceptBox.Text);
                YtStatusText.Text = "Lyrics concept copied ✓";
            }
        }

        private void YtMakeSong_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(YtStyleBox.Text))
            {
                YtStatusText.Text = "Make a style first — then this button carries it into Create Song with the lyrics left blank for you to fill.";
                return;
            }
            if (_createSong != null) { _createSong.Activate(); return; }
            // The extracted style goes in as the style; lyrics stay blank
            // on purpose — the style is borrowed, the words must be his.
            // Wrapped: a window-open error must never take the whole app down.
            try
            {
                _createSong = new CreateSongWindow(_owner, "", YtStyleBox.Text) { Owner = this };
                _createSong.Closed += (s, ev) => _createSong = null;
                _createSong.Show();
            }
            catch (Exception ex)
            {
                _createSong = null;
                YtStatusText.Text = "Create Song couldn't open: " + ex.Message;
            }
        }

        // ---- 📊 Hit Analyzer (AI Toolkit, part 5) ----
        // Same provider/model plumbing as the tabs before it. The model
        // scores the lyrics, names strengths, flags issues, reads genre
        // trends from its own knowledge, and returns a polished draft
        // with the fixes applied. The score is an AI opinion, never a
        // chart prediction; the trends read is genre knowledge, never
        // live chart data. Both are labeled as such in the UI.

        private static string HitSection(string reply, string marker, string? nextMarker)
        {
            int start = reply.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return "";
            start += marker.Length;
            int end = nextMarker != null ? reply.IndexOf(nextMarker, start, StringComparison.OrdinalIgnoreCase) : reply.Length;
            if (end < 0) end = reply.Length;
            return end > start ? reply.Substring(start, end - start).Trim() : "";
        }

        private string BuildHitPrompt(string lyrics, string style)
        {
            string stylePart = style.Length > 0 ? $" The song is meant to be {style}." : "";
            return "You are a hit-song analyst. Analyze these lyrics for commercial potential as a song." + stylePart +
                   " Be specific and honest — a weak hook, a muddled story, or lines that are hard to sing cost points. " +
                   $"Lyrics: {lyrics}. " +
                   "Output ONLY in this exact shape, in this order, no commentary outside these sections, and do not stop until you have written ALL FIVE sections — all are required, not optional:\n" +
                   "[SCORE]\nA single number from 0 to 100.\n" +
                   "[STRENGTHS]\nShort list of what is working.\n" +
                   "[ISSUES]\nShort list of detected issues holding the song back.\n" +
                   "[TRENDS]\nA short read on what works in this genre right now (hook-first writing, short intros, chorus length, and similar craft trends).\n" +
                   "[POLISHED]\nThe full lyrics with the issues fixed, with section labels like [Verse 1], [Chorus], [Verse 2], [Bridge], [Outro] as the song calls for.";
        }

        private void FillHitResults(string reply)
        {
            string scoreRaw = HitSection(reply, "[SCORE]", "[STRENGTHS]");
            string digits = new string(scoreRaw.Where(char.IsDigit).ToArray());
            HitScoreText.Text = digits.Length > 0 ? $"Score: {digits} / 100" : "Score: — / 100";
            HitStrengthsBox.Text = HitSection(reply, "[STRENGTHS]", "[ISSUES]");
            HitIssuesBox.Text = HitSection(reply, "[ISSUES]", "[TRENDS]");
            HitTrendsBox.Text = HitSection(reply, "[TRENDS]", "[POLISHED]");
            HitPolishedBox.Text = HitSection(reply, "[POLISHED]", null);
            if (digits.Length == 0 && HitStrengthsBox.Text.Length == 0 && HitIssuesBox.Text.Length == 0
                && HitTrendsBox.Text.Length == 0 && HitPolishedBox.Text.Length == 0)
            {
                HitStatusText.Text = "The analysis didn't come back in the expected shape — try again.";
            }
        }

        private async void HitAnalyze_Click(object sender, RoutedEventArgs e)
        {
            string lyrics = HitLyricsBox.Text.Trim();
            if (lyrics.Length == 0)
            {
                HitStatusText.Text = "Paste some lyrics first.";
                return;
            }
            string style = HitStyleBox.Text.Trim();
            string model = (ModelPicker.Text ?? "").Trim();
            _settings.Model = model;
            _settings.ProviderId = Current.Id;
            _settings.Save();

            var provider = Current;
            _hitCts = new CancellationTokenSource();
            HitAnalyzeBtn.IsEnabled = false;
            HitCancelBtn.IsEnabled = true;
            HitStatusText.Text = $"Asking {provider.DisplayName} — free and local models can take a minute…";
            try
            {
                string reply = await provider.ChatAsync(model, BuildHitPrompt(lyrics, style), _hitCts.Token);
                FillHitResults(reply);
                if (HitPolishedBox.Text.Length > 0)
                    HitStatusText.Text = "Done — edit the polished draft, then hit 🎤 Make THESE Lyrics into a Song to add your style, voice and a beat.";
            }
            catch (OperationCanceledException)
            {
                HitStatusText.Text = "Cancelled.";
            }
            catch (Exception ex)
            {
                HitStatusText.Text = "Error: " + ex.Message;
            }
            finally
            {
                HitAnalyzeBtn.IsEnabled = true;
                HitCancelBtn.IsEnabled = false;
                _hitCts?.Dispose();
                _hitCts = null;
            }
        }

        private void HitCancel_Click(object sender, RoutedEventArgs e) => _hitCts?.Cancel();

        private void HitCopy_Click(object sender, RoutedEventArgs e)
        {
            if (HitPolishedBox.Text.Length > 0)
            {
                Clipboard.SetText(HitPolishedBox.Text);
                HitStatusText.Text = "Polished lyrics copied ✓";
            }
        }

        private void HitMakeSong_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(HitPolishedBox.Text))
            {
                HitStatusText.Text = "Analyze some lyrics first — then this button carries the polished draft into Create Song with your style and voice ready.";
                return;
            }
            if (_createSong != null) { _createSong.Activate(); return; }
            // Same carry-over as the 🎤 Song, 📖 Story, ✨ Optimizer and 🔄 Rewriter tabs.
            // Wrapped: a window-open error must never take the whole app down.
            try
            {
                _createSong = new CreateSongWindow(_owner, HitPolishedBox.Text,
                    "hip-hop, boom-rap, heavy 808 sub-bass, hard-hitting bass, country rap, Vermont") { Owner = this };
                _createSong.Closed += (s, ev) => _createSong = null;
                _createSong.Show();
            }
            catch (Exception ex)
            {
                _createSong = null;
                HitStatusText.Text = "Create Song couldn't open: " + ex.Message;
            }
        }

        // ---- 👁️ Vision to Style (AI Toolkit, part 6) ----
        // TWO steps, because the small local vision model can't do both
        // jobs in one call (proven 2026-10-24: moondream describes the
        // image fine but stops after the first output section instead
        // of writing all four). Step 1: local Ollama moondream LOOKS at
        // the image and describes it. Step 2: the provider/model picked
        // in the 🎤 Song tab WRITES the style, title and mood tags from
        // that description — the same section format the text models
        // already follow in every other tab. The image stays on this PC
        // (local Ollama) and is sent to moondream as base64.

        private void VisPick_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp",
                Title = "Pick an image to turn into a style"
            };
            if (dlg.ShowDialog() != true) return;
            byte[] bytes;
            try { bytes = File.ReadAllBytes(dlg.FileName); }
            catch (Exception ex) { VisStatusText.Text = "Couldn't read that file: " + ex.Message; return; }
            if (bytes.Length > 20 * 1024 * 1024)
            {
                VisStatusText.Text = "That image is too big (over 20 MB) — pick a smaller one.";
                return;
            }
            _visImageBase64 = Convert.ToBase64String(bytes);
            try
            {
                var img = new BitmapImage();
                using (var ms = new MemoryStream(bytes))
                {
                    img.BeginInit();
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.StreamSource = ms;
                    img.EndInit();
                    img.Freeze();
                }
                VisPreview.Source = img;
            }
            catch { VisPreview.Source = null; }
            VisPathText.Text = System.IO.Path.GetFileName(dlg.FileName);
            VisStatusText.Text = "Image ready — pick a vision model (moondream) in the 🎤 Song tab, then hit 👁️ Read Image → Style.";
        }

        private string BuildVisStylePrompt(string description, string notes)
        {
            string notesPart = notes.Length > 0 ? $" Notes from the user: {notes}." : "";
            return "You are a music creative director. A listener described a picture like this: " + description + notesPart +
                   " Write a style prompt, a title idea, and mood tags for a NEW original song in this picture's mood, and repeat the description as the analysis. " +
                   "Output ONLY in this exact shape, in this order, no commentary outside these sections, and do not stop until you have written ALL FOUR sections — all are required, not optional:\n" +
                   "[ANALYSIS]\nA short description of the picture: scene, subjects, colors, light, mood.\n" +
                   "[STYLE]\nOne style prompt for a song generator, in the mood of this picture.\n" +
                   "[TITLE]\nOne song title idea.\n" +
                   "[MOOD]\nComma-separated mood tags for the song.";
        }

        private void FillVisResults(string reply)
        {
            VisAnalysisBox.Text = HitSection(reply, "[ANALYSIS]", "[STYLE]");
            VisStyleBox.Text = HitSection(reply, "[STYLE]", "[TITLE]");
            VisTitleBox.Text = HitSection(reply, "[TITLE]", "[MOOD]");
            VisMoodBox.Text = HitSection(reply, "[MOOD]", null);
            if (VisAnalysisBox.Text.Length == 0 && VisStyleBox.Text.Length == 0
                && VisTitleBox.Text.Length == 0 && VisMoodBox.Text.Length == 0)
            {
                VisStatusText.Text = "The read didn't come back in the expected shape — try again, and make sure a vision model is picked.";
            }
        }

        private async void VisGenerate_Click(object sender, RoutedEventArgs e)
        {
            if (_visImageBase64.Length == 0)
            {
                VisStatusText.Text = "Pick an image first.";
                return;
            }
            string notes = VisNotesBox.Text.Trim();
            string model = (ModelPicker.Text ?? "").Trim();
            _settings.Model = model;
            _settings.ProviderId = Current.Id;
            _settings.Save();

            var provider = Current;
            _visCts = new CancellationTokenSource();
            VisGenerateBtn.IsEnabled = false;
            VisCancelBtn.IsEnabled = true;
            VisStatusText.Text = "moondream is looking at the image…";
            try
            {
                // Step 1: moondream looks (local Ollama, always this model).
                var ollama = new OllamaProvider();
                string description = await ollama.ChatWithImageAsync("moondream:1.8b",
                    "Describe this image briefly: scene, subjects, colors, light, mood.", _visImageBase64, _visCts.Token);
                // Guard: moondream sometimes answers empty on the CPU-only
                // Dell (seen once 2026-10-24). Never claim it "saw" the
                // picture on an empty answer — fail plainly instead.
                if (string.IsNullOrWhiteSpace(description))
                    throw new InvalidOperationException("moondream came back empty — try again; if it keeps happening, restart Ollama on the Dell.");
                // Step 2: the picked model writes from that description.
                VisStatusText.Text = $"moondream saw it — now {provider.DisplayName} is writing the style…";
                string reply = await provider.ChatAsync(model, BuildVisStylePrompt(description, notes), _visCts.Token);
                FillVisResults(reply);
                if (VisStyleBox.Text.Length > 0)
                    VisStatusText.Text = "Done — edit the style, then hit 🎤 Use This Style in Create Song to build on it.";
            }
            catch (OperationCanceledException)
            {
                VisStatusText.Text = "Cancelled.";
            }
            catch (Exception ex)
            {
                VisStatusText.Text = "Error: " + ex.Message;
            }
            finally
            {
                VisGenerateBtn.IsEnabled = true;
                VisCancelBtn.IsEnabled = false;
                _visCts?.Dispose();
                _visCts = null;
            }
        }

        private void VisCancel_Click(object sender, RoutedEventArgs e) => _visCts?.Cancel();

        private void VisCopyStyle_Click(object sender, RoutedEventArgs e)
        {
            if (VisStyleBox.Text.Length > 0)
            {
                Clipboard.SetText(VisStyleBox.Text);
                VisStatusText.Text = "Style prompt copied ✓";
            }
        }

        private void VisCopyTitle_Click(object sender, RoutedEventArgs e)
        {
            if (VisTitleBox.Text.Length > 0)
            {
                Clipboard.SetText(VisTitleBox.Text);
                VisStatusText.Text = "Title copied ✓";
            }
        }

        private void VisMakeSong_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(VisStyleBox.Text))
            {
                VisStatusText.Text = "Read an image first — then this button carries the style into Create Song with the lyrics left blank for you to fill.";
                return;
            }
            if (_createSong != null) { _createSong.Activate(); return; }
            // The image-born style goes in as the style; lyrics stay blank
            // on purpose — the style is borrowed, the words must be his.
            // Wrapped: a window-open error must never take the whole app down.
            try
            {
                _createSong = new CreateSongWindow(_owner, "", VisStyleBox.Text) { Owner = this };
                _createSong.Closed += (s, ev) => _createSong = null;
                _createSong.Show();
            }
            catch (Exception ex)
            {
                _createSong = null;
                VisStatusText.Text = "Create Song couldn't open: " + ex.Message;
            }
        }

        // ---- 🎛️ Style Generator (AI Toolkit, part 7) ----
        // Same provider/model plumbing as the tabs before it. Base genre
        // + mood + keywords go in; three distinct, usable style prompts
        // come out, each in its own editable box. The picked variation
        // carries into Create Song with the lyrics left blank.

        private string BuildSgPrompt(string genre, string mood, string keywords)
        {
            string keywordsPart = keywords.Length > 0 ? $" Keywords to weave in: {keywords}." : "";
            return "You are a music style prompt writer for a song generator. The artist is Rad-Dad, Vermont country-rap — when the base genre is Boom-Rap, keep the heavy 808 sub-bass identity in all three; for other genres, blend that identity in only if it fits. " +
                   $"Base genre: {genre}. Mood: {mood}." + keywordsPart +
                   " Write THREE distinct style prompts, each one line naming genre, instruments, tempo feel, mood, and vocal style, each different enough to choose between. " +
                   "Output ONLY in this exact shape, in this order, no commentary outside these sections, and do not stop until you have written ALL THREE sections — all are required, not optional:\n" +
                   "[STYLE 1]\nThe first style prompt.\n" +
                   "[STYLE 2]\nThe second style prompt.\n" +
                   "[STYLE 3]\nThe third style prompt.";
        }

        private void FillSgResults(string reply)
        {
            SgStyle1Box.Text = HitSection(reply, "[STYLE 1]", "[STYLE 2]");
            SgStyle2Box.Text = HitSection(reply, "[STYLE 2]", "[STYLE 3]");
            SgStyle3Box.Text = HitSection(reply, "[STYLE 3]", null);
            if (SgStyle1Box.Text.Length == 0 && SgStyle2Box.Text.Length == 0 && SgStyle3Box.Text.Length == 0)
            {
                SgStatusText.Text = "The styles didn't come back in the expected shape — try again.";
            }
        }

        private string SgPickedStyle()
        {
            int idx = SgPickPicker.SelectedIndex;
            if (idx == 1) return SgStyle2Box.Text;
            if (idx == 2) return SgStyle3Box.Text;
            return SgStyle1Box.Text;
        }

        private async void SgGenerate_Click(object sender, RoutedEventArgs e)
        {
            string genre = (SgGenrePicker.Text ?? "").Trim();
            string mood = (SgMoodPicker.Text ?? "").Trim();
            string keywords = SgKeywordsBox.Text.Trim();
            string model = (ModelPicker.Text ?? "").Trim();
            _settings.Model = model;
            _settings.ProviderId = Current.Id;
            _settings.Save();

            var provider = Current;
            _sgCts = new CancellationTokenSource();
            SgGenerateBtn.IsEnabled = false;
            SgCancelBtn.IsEnabled = true;
            SgStatusText.Text = $"Asking {provider.DisplayName} — free and local models can take a minute…";
            try
            {
                string reply = await provider.ChatAsync(model, BuildSgPrompt(genre, mood, keywords), _sgCts.Token);
                FillSgResults(reply);
                if (SgStyle1Box.Text.Length > 0)
                    SgStatusText.Text = "Done — pick a variation, then hit 🎤 Use This Style in Create Song to build on it.";
            }
            catch (OperationCanceledException)
            {
                SgStatusText.Text = "Cancelled.";
            }
            catch (Exception ex)
            {
                SgStatusText.Text = "Error: " + ex.Message;
            }
            finally
            {
                SgGenerateBtn.IsEnabled = true;
                SgCancelBtn.IsEnabled = false;
                _sgCts?.Dispose();
                _sgCts = null;
            }
        }

        private void SgCancel_Click(object sender, RoutedEventArgs e) => _sgCts?.Cancel();

        private void SgCopy_Click(object sender, RoutedEventArgs e)
        {
            string picked = SgPickedStyle();
            if (picked.Length > 0)
            {
                Clipboard.SetText(picked);
                SgStatusText.Text = "Selected variation copied ✓";
            }
        }

        private void SgMakeSong_Click(object sender, RoutedEventArgs e)
        {
            string picked = SgPickedStyle();
            if (string.IsNullOrWhiteSpace(picked))
            {
                SgStatusText.Text = "Generate some variations first — then this button carries the picked one into Create Song with the lyrics left blank for you to fill.";
                return;
            }
            if (_createSong != null) { _createSong.Activate(); return; }
            // The picked variation goes in as the style; lyrics stay blank
            // on purpose — the style is generated, the words must be his.
            // Wrapped: a window-open error must never take the whole app down.
            try
            {
                _createSong = new CreateSongWindow(_owner, "", picked) { Owner = this };
                _createSong.Closed += (s, ev) => _createSong = null;
                _createSong.Show();
            }
            catch (Exception ex)
            {
                _createSong = null;
                SgStatusText.Text = "Create Song couldn't open: " + ex.Message;
            }
        }

        // ---- 🎭 Artist Persona (AI Toolkit, part 8) ----
        // Same provider/model plumbing as the tabs before it. An artist
        // name + style hints go in; a full persona comes out in five
        // sections. The Suno prompt carries into Create Song with the
        // lyrics left blank.

        private string BuildApPrompt(string name, string hints)
        {
            string hintsPart = hints.Length > 0 ? $" Style influences: {hints}." : "";
            return $"You are an artist persona builder for a song generator. Artist name: {name}." + hintsPart +
                   " Write a full persona for this artist: genre tags (a comma list), vocal style, visual style (stage look and cover aesthetic), a Suno-style prompt that captures the sound, and a short backstory/bio. " +
                   "Output ONLY in this exact shape, in this order, no commentary outside these sections, and do not stop until you have written ALL FIVE sections — all are required, not optional:\n" +
                   "[GENRE TAGS]\nThe genre tags.\n" +
                   "[VOCAL STYLE]\nThe vocal style.\n" +
                   "[VISUAL STYLE]\nThe visual style.\n" +
                   "[SUNO PROMPT]\nThe Suno prompt.\n" +
                   "[BIO]\nThe backstory/bio.";
        }

        private void FillApResults(string reply)
        {
            ApGenreBox.Text = HitSection(reply, "[GENRE TAGS]", "[VOCAL STYLE]");
            ApVocalBox.Text = HitSection(reply, "[VOCAL STYLE]", "[VISUAL STYLE]");
            ApVisualBox.Text = HitSection(reply, "[VISUAL STYLE]", "[SUNO PROMPT]");
            ApSunoBox.Text = HitSection(reply, "[SUNO PROMPT]", "[BIO]");
            ApBioBox.Text = HitSection(reply, "[BIO]", null);
            if (ApGenreBox.Text.Length == 0 && ApVocalBox.Text.Length == 0 && ApVisualBox.Text.Length == 0
                && ApSunoBox.Text.Length == 0 && ApBioBox.Text.Length == 0)
            {
                ApStatusText.Text = "The persona didn't come back in the expected shape — try again.";
            }
        }

        private async void ApGenerate_Click(object sender, RoutedEventArgs e)
        {
            string name = ApNameBox.Text.Trim();
            if (name.Length == 0)
            {
                ApStatusText.Text = "Give the artist a name first.";
                return;
            }
            string hints = ApHintsBox.Text.Trim();
            string model = (ModelPicker.Text ?? "").Trim();
            _settings.Model = model;
            _settings.ProviderId = Current.Id;
            _settings.Save();

            var provider = Current;
            _apCts = new CancellationTokenSource();
            ApGenerateBtn.IsEnabled = false;
            ApCancelBtn.IsEnabled = true;
            ApStatusText.Text = $"Asking {provider.DisplayName} — free and local models can take a minute…";
            try
            {
                string reply = await provider.ChatAsync(model, BuildApPrompt(name, hints), _apCts.Token);
                FillApResults(reply);
                if (ApSunoBox.Text.Length > 0)
                    ApStatusText.Text = "Done — copy the Suno prompt, or hit 🎤 Use This Style in Create Song to build on it.";
            }
            catch (OperationCanceledException)
            {
                ApStatusText.Text = "Cancelled.";
            }
            catch (Exception ex)
            {
                ApStatusText.Text = "Error: " + ex.Message;
            }
            finally
            {
                ApGenerateBtn.IsEnabled = true;
                ApCancelBtn.IsEnabled = false;
                _apCts?.Dispose();
                _apCts = null;
            }
        }

        private void ApCancel_Click(object sender, RoutedEventArgs e) => _apCts?.Cancel();

        private void ApCopy_Click(object sender, RoutedEventArgs e)
        {
            if (ApSunoBox.Text.Length > 0)
            {
                Clipboard.SetText(ApSunoBox.Text);
                ApStatusText.Text = "Suno prompt copied ✓";
            }
        }

        private void ApMakeSong_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(ApSunoBox.Text))
            {
                ApStatusText.Text = "Build a persona first — then this button carries the Suno prompt into Create Song with the lyrics left blank for you to fill.";
                return;
            }
            if (_createSong != null) { _createSong.Activate(); return; }
            // The Suno prompt goes in as the style; lyrics stay blank
            // on purpose — the style is generated, the words must be his.
            // Wrapped: a window-open error must never take the whole app down.
            try
            {
                _createSong = new CreateSongWindow(_owner, "", ApSunoBox.Text) { Owner = this };
                _createSong.Closed += (s, ev) => _createSong = null;
                _createSong.Show();
            }
            catch (Exception ex)
            {
                _createSong = null;
                ApStatusText.Text = "Create Song couldn't open: " + ex.Message;
            }
        }

        // ---- 🎹 Chords (AI Toolkit, part 9) ----
        // Same provider/model plumbing as the tabs before it. A mood +
        // lyrics go in; a chord sheet comes out in four sections. The
        // sheet copies out labeled, ready to paste anywhere.

        private string BuildChPrompt(string mood, string lyrics)
        {
            return $"You are a music theory assistant for a songwriter. Mood: {mood}. Lyrics:\n{lyrics}\n" +
                   "Suggest a key that fits the mood and the words, a verse chord progression, a chorus chord progression, and the instruments that should play them. Keep each progression as chord names in order, with a one-line note on why it fits. " +
                   "Output exactly four labeled sections, in this order, each label on its own line, with nothing before the first label, and do not stop until you have written ALL FOUR — all are required, not optional:\n" +
                   "Key: the key\n" +
                   "Verse: the verse progression\n" +
                   "Chorus: the chorus progression\n" +
                   "Instruments: the suggested instruments";
        }

        // Label slicer: text after "Label:" up to the next label or the end.
        private static string ChSection(string reply, string marker, string? nextMarker)
        {
            int start = reply.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return "";
            start += marker.Length;
            int end = reply.Length;
            if (nextMarker != null)
            {
                int n = reply.IndexOf(nextMarker, start, StringComparison.OrdinalIgnoreCase);
                if (n >= 0) end = n;
            }
            return reply.Substring(start, end - start).Trim();
        }

        private static bool LooksLikeKey(string line)
        {
            string t = line.Trim();
            if (t.Length == 0 || t.Length > 14) return false;
            char first = t[0];
            if (first < 'A' || first > 'G') return false;
            string rest = t.Substring(1).Trim().ToLowerInvariant();
            return rest == "" || rest == "#" || rest == "b" || rest == "major" || rest == "minor"
                || rest == "maj" || rest == "min" || rest == "m" || rest == "# major" || rest == "# minor"
                || rest == "b major" || rest == "b minor";
        }

        private void FillChResults(string reply)
        {
            ChKeyBox.Text = ChSection(reply, "Key:", "Verse:");
            ChVerseBox.Text = ChSection(reply, "Verse:", "Chorus:");
            ChChorusBox.Text = ChSection(reply, "Chorus:", "Instruments:");
            ChInstBox.Text = ChSection(reply, "Instruments:", null);
            // Mixed case: labels present but the key line dropped — the
            // first line of the reply is almost always the key itself.
            if (ChKeyBox.Text.Length == 0)
            {
                string firstLine = reply.Split('\n')[0].Trim();
                if (LooksLikeKey(firstLine)) ChKeyBox.Text = firstLine;
            }
            // Unlabeled case: small models sometimes answer in order with
            // no labels at all — key first, progressions next, instruments
            // last. Split on blank lines and map by position (any extra
            // middle chunk joins the chorus, where variations belong).
            if (ChKeyBox.Text.Length == 0 && ChVerseBox.Text.Length == 0 && ChChorusBox.Text.Length == 0
                && ChInstBox.Text.Length == 0)
            {
                string[] chunks = reply.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < chunks.Length; i++) chunks[i] = chunks[i].Trim();
                if (chunks.Length >= 4)
                {
                    ChKeyBox.Text = chunks[0];
                    ChVerseBox.Text = chunks[1];
                    ChChorusBox.Text = string.Join("\n\n", chunks, 2, chunks.Length - 3);
                    ChInstBox.Text = chunks[chunks.Length - 1];
                    ChStatusText.Text = "Parsed from the reply's layout — give the sections a quick look.";
                    return;
                }
                ChStatusText.Text = "The chords didn't come back in the expected shape — try again.";
            }
        }

        private async void ChGenerate_Click(object sender, RoutedEventArgs e)
        {
            string lyrics = ChLyricsBox.Text.Trim();
            if (lyrics.Length == 0)
            {
                ChStatusText.Text = "Paste some lyrics first — the key and progressions are picked to fit your words.";
                return;
            }
            string mood = (ChMoodPicker.Text ?? "").Trim();
            string model = (ModelPicker.Text ?? "").Trim();
            _settings.Model = model;
            _settings.ProviderId = Current.Id;
            _settings.Save();

            var provider = Current;
            _chCts = new CancellationTokenSource();
            ChGenerateBtn.IsEnabled = false;
            ChCancelBtn.IsEnabled = true;
            ChStatusText.Text = $"Asking {provider.DisplayName} — free and local models can take a minute…";
            try
            {
                string reply = await provider.ChatAsync(model, BuildChPrompt(mood, lyrics), _chCts.Token);
                FillChResults(reply);
                if (ChKeyBox.Text.Length > 0)
                    ChStatusText.Text = "Done — copy the whole sheet, or edit any section first.";
            }
            catch (OperationCanceledException)
            {
                ChStatusText.Text = "Cancelled.";
            }
            catch (Exception ex)
            {
                ChStatusText.Text = "Error: " + ex.Message;
            }
            finally
            {
                ChGenerateBtn.IsEnabled = true;
                ChCancelBtn.IsEnabled = false;
                _chCts?.Dispose();
                _chCts = null;
            }
        }

        private void ChCancel_Click(object sender, RoutedEventArgs e) => _chCts?.Cancel();

        private void ChCopy_Click(object sender, RoutedEventArgs e)
        {
            if (ChKeyBox.Text.Length == 0 && ChVerseBox.Text.Length == 0 && ChChorusBox.Text.Length == 0
                && ChInstBox.Text.Length == 0)
            {
                return;
            }
            string sheet = "Key: " + ChKeyBox.Text + "\n\nVerse: " + ChVerseBox.Text
                + "\n\nChorus: " + ChChorusBox.Text + "\n\nInstruments: " + ChInstBox.Text;
            Clipboard.SetText(sheet);
            ChStatusText.Text = "Chord sheet copied ✓";
        }

        // ---- 🎬 MV Director (AI Toolkit, part 11) ----
        // Same provider/model plumbing as the tabs before it. A theme +
        // lyrics go in; a video treatment comes out in three sections.
        // The treatment copies out labeled, ready to paste anywhere.

        private string BuildMvPrompt(string theme, string lyrics)
        {
            return $"You are a music video director. Theme: {theme}. Lyrics:\n{lyrics}\n" +
                   "Write a music video treatment for this song: the concept (what happens, in a few sentences), the art direction (look, lighting, camera, color), and a timed shot-by-shot script with timestamps that follows the lyrics from start to finish. " +
                   "Output ONLY in this exact shape, in this order, no commentary outside these sections, and do not stop until you have written ALL THREE sections — all are required, not optional:\n" +
                   "[CONCEPT]\nThe concept.\n" +
                   "[ART DIRECTION]\nThe art direction.\n" +
                   "[SCRIPT]\nThe timed script.";
        }

        private void FillMvResults(string reply)
        {
            MvConceptBox.Text = HitSection(reply, "[CONCEPT]", "[ART DIRECTION]");
            MvArtBox.Text = HitSection(reply, "[ART DIRECTION]", "[SCRIPT]");
            MvScriptBox.Text = HitSection(reply, "[SCRIPT]", null);
            if (MvConceptBox.Text.Length == 0 && MvArtBox.Text.Length == 0 && MvScriptBox.Text.Length == 0)
            {
                MvStatusText.Text = "The treatment didn't come back in the expected shape — try again.";
            }
        }

        private async void MvGenerate_Click(object sender, RoutedEventArgs e)
        {
            string theme = MvThemeBox.Text.Trim();
            if (theme.Length == 0)
            {
                MvStatusText.Text = "Give it a theme first — that's what the video is about.";
                return;
            }
            string lyrics = MvLyricsBox.Text.Trim();
            if (lyrics.Length == 0)
            {
                MvStatusText.Text = "Paste the lyrics first — the script is timed to your words.";
                return;
            }
            string model = (ModelPicker.Text ?? "").Trim();
            _settings.Model = model;
            _settings.ProviderId = Current.Id;
            _settings.Save();

            var provider = Current;
            _mvCts = new CancellationTokenSource();
            MvGenerateBtn.IsEnabled = false;
            MvCancelBtn.IsEnabled = true;
            MvStatusText.Text = $"Asking {provider.DisplayName} — free and local models can take a minute…";
            try
            {
                string reply = await provider.ChatAsync(model, BuildMvPrompt(theme, lyrics), _mvCts.Token);
                FillMvResults(reply);
                if (MvConceptBox.Text.Length > 0)
                    MvStatusText.Text = "Done — copy the full treatment, or edit any section first.";
            }
            catch (OperationCanceledException)
            {
                MvStatusText.Text = "Cancelled.";
            }
            catch (Exception ex)
            {
                MvStatusText.Text = "Error: " + ex.Message;
            }
            finally
            {
                MvGenerateBtn.IsEnabled = true;
                MvCancelBtn.IsEnabled = false;
                _mvCts?.Dispose();
                _mvCts = null;
            }
        }

        private void MvCancel_Click(object sender, RoutedEventArgs e) => _mvCts?.Cancel();

        private void MvCopy_Click(object sender, RoutedEventArgs e)
        {
            if (MvConceptBox.Text.Length == 0 && MvArtBox.Text.Length == 0 && MvScriptBox.Text.Length == 0)
            {
                return;
            }
            string treatment = "Concept: " + MvConceptBox.Text + "\n\nArt direction: " + MvArtBox.Text
                + "\n\nScript: " + MvScriptBox.Text;
            Clipboard.SetText(treatment);
            MvStatusText.Text = "Full treatment copied ✓";
        }

        // ---- 🎨 Cover Art (AI Toolkit, part 10) ----
        // Two engines, per Michael's call: local Stable Diffusion on Dayz
        // (free, his own GPU) or an image API (endpoint + key he supplies;
        // the API side expects an OpenAI-compatible images endpoint).
        // The final prompt is his to edit — Generate uses the edited
        // prompt when he changed it, otherwise it composes one fresh.
        // The API key is stored DPAPI-encrypted like every other key.

        private void CvFinalPrompt_Changed(object sender, TextChangedEventArgs e) => _cvPromptDirty = true;

        private void CvShowBytes(byte[] bytes)
        {
            _cvBytes = bytes;
            var bmp = new BitmapImage();
            using (var stream = new MemoryStream(bytes))
            {
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = stream;
                bmp.EndInit();
            }
            bmp.Freeze();
            CvImage.Source = bmp;
        }

        private async Task<byte[]> CvGenerateLocalAsync(string prompt, int width, int height, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "http://192.168.1.238:11438/generate");
            req.Content = new StringContent(
                JsonSerializer.Serialize(new { prompt, width, height, steps = 30, guidance_scale = 7.5 }),
                System.Text.Encoding.UTF8, "application/json");
            using var res = await AiHttp.Client.SendAsync(req, ct);
            string body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException($"Local engine HTTP {(int)res.StatusCode}: {body.Trim()}");
            return await res.Content.ReadAsByteArrayAsync(ct);
        }

        private async Task<byte[]> CvGenerateApiAsync(string prompt, string endpoint, string key, string shape, CancellationToken ct)
        {
            string size = shape.StartsWith("Portrait") ? "1024x1536"
                : shape.StartsWith("Landscape") ? "1536x1024" : "1024x1024";
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            req.Content = new StringContent(
                JsonSerializer.Serialize(new { prompt, size, n = 1 }),
                System.Text.Encoding.UTF8, "application/json");
            using var res = await AiHttp.Client.SendAsync(req, ct);
            string body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException($"Image API HTTP {(int)res.StatusCode}: {body.Trim()}");
            using var doc = JsonDocument.Parse(body);
            var data = doc.RootElement.GetProperty("data")[0];
            if (data.TryGetProperty("b64_json", out var b64) && b64.GetString() is string b64Text && b64Text.Length > 0)
                return Convert.FromBase64String(b64Text);
            if (data.TryGetProperty("url", out var url) && url.GetString() is string urlText && urlText.Length > 0)
            {
                using var imgRes = await AiHttp.Client.GetAsync(urlText, ct);
                if (!imgRes.IsSuccessStatusCode)
                    throw new InvalidOperationException("Could not download the image the API returned.");
                return await imgRes.Content.ReadAsByteArrayAsync(ct);
            }
            throw new InvalidOperationException("The API didn't return image data — check the endpoint (it expects an OpenAI-compatible images endpoint).");
        }

        private async void CvGenerate_Click(object sender, RoutedEventArgs e)
        {
            string concept = CvConceptBox.Text.Trim();
            if (concept.Length == 0)
            {
                CvStatusText.Text = "Describe the cover first.";
                return;
            }
            string style = (CvStylePicker.Text ?? "").Trim();
            string shape = (CvShapePicker.Text ?? "").Trim();
            string prompt;
            if (_cvPromptDirty && CvFinalPromptBox.Text.Trim().Length > 0)
            {
                prompt = CvFinalPromptBox.Text.Trim();
            }
            else
            {
                prompt = concept + ", " + style + " style, album cover artwork, high detail";
                CvFinalPromptBox.Text = prompt;
                _cvPromptDirty = false;
            }

            bool useApi = CvEnginePicker.SelectedIndex == 1;
            string apiKey = "";
            string endpoint = "";
            if (useApi)
            {
                apiKey = CvApiKeyBox.Password.Trim();
                if (apiKey.Length == 0) apiKey = _settings.GetKey("cover-api");
                if (apiKey.Length == 0)
                {
                    CvStatusText.Text = "Paste an image API key first — it's stored encrypted on this PC.";
                    return;
                }
                endpoint = CvApiEndpointBox.Text.Trim();
                if (endpoint.Length == 0) endpoint = "https://api.openai.com/v1/images/generations";
            }

            _cvCts = new CancellationTokenSource();
            CvGenerateBtn.IsEnabled = false;
            CvCancelBtn.IsEnabled = true;
            CvStatusText.Text = useApi ? "Asking the image API…" : "Asking Dayz — first image loads the model, so it can take a couple minutes…";
            try
            {
                byte[] bytes;
                if (useApi)
                {
                    bytes = await CvGenerateApiAsync(prompt, endpoint, apiKey, shape, _cvCts.Token);
                    _settings.SetKey("cover-api", apiKey);
                    _settings.Save();
                }
                else
                {
                    int width = shape.StartsWith("Portrait") ? 512 : 768;
                    int height = shape.StartsWith("Landscape") ? 512 : shape.StartsWith("Portrait") ? 768 : 768;
                    bytes = await CvGenerateLocalAsync(prompt, width, height, _cvCts.Token);
                }
                CvShowBytes(bytes);
                CvStatusText.Text = "Done — save it with 💾 Save Artwork.";
            }
            catch (OperationCanceledException)
            {
                CvStatusText.Text = "Cancelled.";
            }
            catch (Exception ex)
            {
                CvStatusText.Text = useApi ? "Error: " + ex.Message
                    : "Local engine didn't answer — is the Dayz Stable Diffusion service up? " + ex.Message;
            }
            finally
            {
                CvGenerateBtn.IsEnabled = true;
                CvCancelBtn.IsEnabled = false;
                _cvCts?.Dispose();
                _cvCts = null;
            }
        }

        private void CvCancel_Click(object sender, RoutedEventArgs e) => _cvCts?.Cancel();

        private void CvSave_Click(object sender, RoutedEventArgs e)
        {
            if (_cvBytes == null || _cvBytes.Length == 0)
            {
                CvStatusText.Text = "Generate some art first.";
                return;
            }
            try
            {
                var dlg = new SaveFileDialog { Filter = "PNG image|*.png", FileName = "cover.png" };
                if (dlg.ShowDialog() == true)
                {
                    File.WriteAllBytes(dlg.FileName, _cvBytes);
                    CvStatusText.Text = "Saved ✓";
                }
            }
            catch (Exception ex)
            {
                CvStatusText.Text = "Save failed: " + ex.Message;
            }
        }

        // ---- 📣 Social Kit (AI Toolkit, part 12) ----
        // A song title + what it's about go in; ready-to-post copy comes
        // out in three sections. Nothing here is lyrics, so the outputs
        // copy out instead of feeding Create Song.

        private string BuildSkPrompt(string title, string theme)
        {
            return $"You write social-media copy for a song. The song is called \"{title}\". What it is about:\n{theme}\n" +
                   "Write the social kit for this song: a YouTube SEO description (one title line, a 2-3 sentence description, a tags line), a Facebook/Instagram caption (2-3 sentences plus a call to listen), and a TikTok hashtag strategy (8-12 hashtags plus one sentence on why they fit). " +
                   "Output ONLY in this exact shape, in this order, no commentary outside these sections, and do not stop until you have written ALL THREE sections — all are required, not optional:\n" +
                   "[YOUTUBE]\nThe YouTube description.\n" +
                   "[CAPTION]\nThe caption.\n" +
                   "[HASHTAGS]\nThe hashtag strategy.";
        }

        private void FillSkResults(string reply)
        {
            SkYoutubeBox.Text = HitSection(reply, "[YOUTUBE]", "[CAPTION]");
            SkCaptionBox.Text = HitSection(reply, "[CAPTION]", "[HASHTAGS]");
            SkHashtagsBox.Text = HitSection(reply, "[HASHTAGS]", null);
            if (SkYoutubeBox.Text.Length == 0 && SkCaptionBox.Text.Length == 0 && SkHashtagsBox.Text.Length == 0)
            {
                SkStatusText.Text = "The social kit didn't come back in the expected shape — try again.";
            }
        }

        private async void SkGenerate_Click(object sender, RoutedEventArgs e)
        {
            string title = SkTitleBox.Text.Trim();
            if (title.Length == 0)
            {
                SkStatusText.Text = "Give the song a title first — the copy is written for that song.";
                return;
            }
            string theme = SkThemeBox.Text.Trim();
            if (theme.Length == 0)
            {
                SkStatusText.Text = "Say what the song is about first — the copy is written from that.";
                return;
            }
            string model = (ModelPicker.Text ?? "").Trim();
            _settings.Model = model;
            _settings.ProviderId = Current.Id;
            _settings.Save();

            var provider = Current;
            _skCts = new CancellationTokenSource();
            SkGenerateBtn.IsEnabled = false;
            SkCancelBtn.IsEnabled = true;
            SkStatusText.Text = $"Asking {provider.DisplayName} — free and local models can take a minute…";
            try
            {
                string reply = await provider.ChatAsync(model, BuildSkPrompt(title, theme), _skCts.Token);
                FillSkResults(reply);
                if (SkYoutubeBox.Text.Length > 0)
                    SkStatusText.Text = "Done — copy any section, or edit it first.";
            }
            catch (OperationCanceledException)
            {
                SkStatusText.Text = "Cancelled.";
            }
            catch (Exception ex)
            {
                SkStatusText.Text = "Error: " + ex.Message;
            }
            finally
            {
                SkGenerateBtn.IsEnabled = true;
                SkCancelBtn.IsEnabled = false;
                _skCts?.Dispose();
                _skCts = null;
            }
        }

        private void SkCancel_Click(object sender, RoutedEventArgs e) => _skCts?.Cancel();

        private void SkCopyYoutube_Click(object sender, RoutedEventArgs e)
        {
            if (SkYoutubeBox.Text.Length == 0) return;
            Clipboard.SetText(SkYoutubeBox.Text);
            SkStatusText.Text = "YouTube description copied ✓";
        }

        private void SkCopyCaption_Click(object sender, RoutedEventArgs e)
        {
            if (SkCaptionBox.Text.Length == 0) return;
            Clipboard.SetText(SkCaptionBox.Text);
            SkStatusText.Text = "Caption copied ✓";
        }

        private void SkCopyHashtags_Click(object sender, RoutedEventArgs e)
        {
            if (SkHashtagsBox.Text.Length == 0) return;
            Clipboard.SetText(SkHashtagsBox.Text);
            SkStatusText.Text = "Hashtags copied ✓";
        }

        private void SkCopyAll_Click(object sender, RoutedEventArgs e)
        {
            if (SkYoutubeBox.Text.Length == 0 && SkCaptionBox.Text.Length == 0 && SkHashtagsBox.Text.Length == 0)
            {
                return;
            }
            string kit = "YouTube: " + SkYoutubeBox.Text + "\n\nCaption: " + SkCaptionBox.Text
                + "\n\nHashtags: " + SkHashtagsBox.Text;
            Clipboard.SetText(kit);
            SkStatusText.Text = "Full social kit copied ✓";
        }

        // ---- 🏠 Dashboard (AI Toolkit, part 13) ----
        // A card grid home for the Lab: each card jumps to its tab. No AI
        // work happens here, so there is no CTS to cancel.

        private void DbCard_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tag && int.TryParse(tag, out int index)
                && index >= 0 && index < AiTabControl.Items.Count)
            {
                AiTabControl.SelectedIndex = index;
            }
        }

        private void ReplicateSignup_Click(object sender, RoutedEventArgs e) => OpenUrl("https://replicate.com/signin");

        private void ReplicateToken_Click(object sender, RoutedEventArgs e) => OpenUrl("https://replicate.com/account/api-tokens");

        private static void OpenUrl(string url)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { }
        }

        private bool HfSelected => OnlineProviderPicker.SelectedIndex == 1;
        private bool FalSelected => OnlineProviderPicker.SelectedIndex == 2;
        private bool RemoteSelected => OnlineProviderPicker.SelectedIndex == 3;

        private void LoadProviderKey()
        {
            if (RemoteSelected)
            {
                OnlineKeyBox.Password = "";
                OnlineKeyStatus.Text = _compute.RemoteEngineUrls().Count > 0
                    ? $"Engine(s): {string.Join(" + ", _compute.RemoteEngineUrls())} — edit in 🌐 Providers"
                    : "No key needed — set the engine address in 🌐 Providers → 🏠 Remote GPU";
                return;
            }
            OnlineKeyBox.Password = HfSelected ? _compute.GetHfKey()
                : FalSelected ? _compute.GetFalKey()
                : _compute.GetOnlineKey();
            OnlineKeyStatus.Text = OnlineKeyBox.Password.Length > 0 ? "Key saved ✓" : "";
        }

        private void OnlineProviderPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_computeLoading || !IsLoaded) return;
            _compute.OnlineProvider = HfSelected ? "huggingface" : FalSelected ? "fal" : RemoteSelected ? "remote" : "replicate";
            _compute.Save();
            LoadProviderKey();
            RefreshComputeInfo();
        }

        private void HfToken_Click(object sender, RoutedEventArgs e) => OpenUrl("https://huggingface.co/settings/tokens");

        private void FalSignup_Click(object sender, RoutedEventArgs e) => OpenUrl("https://fal.ai/login");

        private void FalKeys_Click(object sender, RoutedEventArgs e) => OpenUrl("https://fal.ai/dashboard/keys");

        private void OnlineSaveKey_Click(object sender, RoutedEventArgs e)
        {
            if (RemoteSelected)
            {
                OnlineKeyStatus.Text = "Remote GPU uses an address, not a key — 🌐 Providers → 🏠 Remote GPU card";
                return;
            }
            if (HfSelected) _compute.SetHfKey(OnlineKeyBox.Password);
            else if (FalSelected) _compute.SetFalKey(OnlineKeyBox.Password);
            else _compute.SetOnlineKey(OnlineKeyBox.Password);
            _compute.Save();
            OnlineKeyStatus.Text = OnlineKeyBox.Password.Length > 0 ? "Key saved ✓" : "Key cleared";
            RefreshComputeInfo();
        }

        private void GpuPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_computeLoading || !IsLoaded) return;
            if (GpuPicker.SelectedItem is ComboBoxItem item && item.Tag is int idx)
            {
                _compute.GpuIndex = idx;
                _compute.Save();
                RefreshComputeInfo();
            }
        }

        private void BuildProviders()
        {
            _providers = new List<IAiProvider>
            {
                new OpenCodeProvider(),
                new OllamaProvider(),
                new OpenAiCompatProvider("gemini", "✨ Google Gemini (free key)",
                    "https://generativelanguage.googleapis.com/v1beta/openai", "gemini-2.0-flash", _settings),
                new OpenAiCompatProvider("openrouter", "🆓 OpenRouter (free models)",
                    "https://openrouter.ai/api/v1", "openrouter/free", _settings),
                new OpenAiCompatProvider("openai", "💬 ChatGPT / OpenAI (your key)",
                    "https://api.openai.com/v1", "gpt-4o-mini", _settings),
                new OpenAiCompatProvider("custom", "🔌 Custom endpoint",
                    string.IsNullOrWhiteSpace(_settings.CustomBaseUrl) ? "http://localhost:1234/v1" : _settings.CustomBaseUrl,
                    "", _settings)
            };
            ProviderPicker.Items.Clear();
            int select = 0;
            for (int i = 0; i < _providers.Count; i++)
            {
                ProviderPicker.Items.Add(new ComboBoxItem { Content = _providers[i].DisplayName, Tag = _providers[i].Id });
                if (_providers[i].Id == _settings.ProviderId) select = i;
            }
            ProviderPicker.SelectedIndex = select;
            UpdateProviderPanel();
        }

        private IAiProvider Current =>
            _providers[Math.Max(0, Math.Min(ProviderPicker.SelectedIndex, _providers.Count - 1))];

        private bool IsKeyProvider(string id) => id == "openai" || id == "gemini" || id == "openrouter" || id == "custom";

        private void UpdateProviderPanel()
        {
            string id = Current.Id;
            InfoText.Text = InfoByProvider.TryGetValue(id, out var info) ? info : "";
            KeyPanel.Visibility = IsKeyProvider(id) ? Visibility.Visible : Visibility.Collapsed;
            CustomUrlRow.Visibility = id == "custom" ? Visibility.Visible : Visibility.Collapsed;
            if (IsKeyProvider(id))
            {
                KeyHint.Text = KeyHintByProvider[id];
                KeyBox.Password = _settings.GetKey(id);
                KeyStatus.Text = _settings.GetKey(id).Length > 0 ? "Key saved ✓" : "";
                if (id == "custom") CustomUrlBox.Text = _settings.CustomBaseUrl;
            }
        }

        private async void ProviderPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || _providers.Count == 0) return;
            _settings.ProviderId = Current.Id;
            _settings.Save();
            UpdateProviderPanel();
            await RefreshModelsAsync();
        }

        private async void RefreshBtn_Click(object sender, RoutedEventArgs e) => await RefreshModelsAsync();

        private async Task RefreshModelsAsync()
        {
            var provider = Current;
            StatusText.Text = $"Loading {provider.DisplayName} models…";
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                var models = await provider.GetModelsAsync(cts.Token);
                ModelPicker.Items.Clear();
                foreach (var m in models) ModelPicker.Items.Add(m);
                if (models.Count == 0)
                {
                    StatusText.Text = provider.Id == "ollama"
                        ? "Ollama answered but has no models yet — run: ollama pull llama3.1:8b"
                        : "No models listed — type a model name in the Model box.";
                    if (provider is OpenAiCompatProvider compat && compat.DefaultModel.Length > 0)
                        ModelPicker.Text = compat.DefaultModel;
                }
                else
                {
                    // Default pick: the saved choice, else the recommended
                    // nemotron-3-ultra-free when this provider offers it.
                    ModelPicker.Text = models.Contains(_settings.Model) ? _settings.Model
                        : models.Contains("opencode/nemotron-3-ultra-free") ? "opencode/nemotron-3-ultra-free"
                        : models[0];
                    StatusText.Text = $"{models.Count} model(s) available on {provider.DisplayName}.";
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = provider.Id == "ollama"
                    ? "Ollama isn't answering on this PC. Install it from ollama.com, pull a model, then Refresh."
                    : provider.Id == "opencode"
                        ? "OpenCode wasn't found or didn't answer — is the OpenCode app installed on this PC?"
                        : $"Couldn't list models: {ex.Message}";
                if (provider is OpenAiCompatProvider compat2 && compat2.DefaultModel.Length > 0)
                    ModelPicker.Text = compat2.DefaultModel;
            }
        }

        private void SaveKey_Click(object sender, RoutedEventArgs e)
        {
            string id = Current.Id;
            if (id == "custom")
            {
                _settings.CustomBaseUrl = CustomUrlBox.Text.Trim();
                BuildProviders(); // rebuild so the custom provider uses the new URL
                ProviderPicker.SelectedIndex = _providers.FindIndex(p => p.Id == "custom");
            }
            _settings.SetKey(id, KeyBox.Password);
            _settings.Save();
            KeyStatus.Text = KeyBox.Password.Length > 0 ? "Key saved ✓" : "Key cleared";
        }

        private string BuildPrompt(string mode, string input)
        {
            switch (mode)
            {
                case "titles":
                    return $"Give me 10 original song title ideas for Rad-Dad, a Vermont country-rap (boom-rap) artist, about: {input}. Reply with a numbered list of titles only, no commentary.";
                case "beat":
                    return "You control a drum machine. Available styles: boomrap, trap, house, funk, rock, dembow, lofi, drill, dancehall, westcoast, techno, dnb, phonk, dubstep, synthwave, afrobeat, country. " +
                           $"The user describes a beat: \"{input}\". Reply with ONLY a JSON object like {{\"style\":\"boomrap\",\"bpm\":95,\"density\":0.7}} — style exactly one of the listed keys, bpm 60-180, density 0.2-1.0. No other words.";
                case "chat":
                    return input;
                default: // lyrics
                    return $"Write complete song lyrics (two verses, a chorus, and a bridge) for Rad-Dad, a Vermont country-rap artist. Style: boom-rap — heavy 808 bass, honest small-town Vermont life, family, trucks, hard work, no explicit content. Topic: {input}. Output only the lyrics with [Verse 1], [Chorus], [Verse 2], [Bridge] labels, no commentary.";
            }
        }

        private static string ModeKey(int index) => index switch { 1 => "titles", 2 => "beat", 3 => "chat", _ => "lyrics" };

        private async void Generate_Click(object sender, RoutedEventArgs e)
        {
            string input = InputBox.Text.Trim();
            if (input.Length == 0)
            {
                StatusText.Text = "Type a topic or description first.";
                return;
            }
            string mode = ModeKey(ModePicker.SelectedIndex);
            string model = (ModelPicker.Text ?? "").Trim();
            _settings.Model = model;
            _settings.ProviderId = Current.Id;
            _settings.Save();

            var provider = Current;
            _cts = new CancellationTokenSource();
            GenerateBtn.IsEnabled = false;
            CancelBtn.IsEnabled = true;
            StatusText.Text = $"Asking {provider.DisplayName} — free and local models can take a minute…";
            try
            {
                if (CompareBox.IsChecked == true)
                {
                    await RunCompareAsync(mode, input);
                    return;
                }
                string reply = await provider.ChatAsync(model, BuildPrompt(mode, input), _cts.Token);
                if (mode == "beat")
                {
                    OutputBox.Text = reply;
                    TryMakeBeat(reply);
                }
                else
                {
                    OutputBox.Text = reply;
                    StatusText.Text = mode == "lyrics"
                        ? "Done — like them? Hit 🎤 Make THIS into a Song (bottom right) to add your style, voice and a beat and generate the track."
                        : "Done — edit it, copy it, make it yours.";
                }
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "Cancelled.";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Error: " + ex.Message;
            }
            finally
            {
                GenerateBtn.IsEnabled = true;
                CancelBtn.IsEnabled = false;
                _cts?.Dispose();
                _cts = null;
            }
        }

        /// <summary>
        /// Compare mode: the same prompt goes to every connected AI, one at a
        /// time, and the answers open side by side in the compare window.
        /// Providers with no key saved are skipped with a note.
        /// </summary>
        private async Task RunCompareAsync(string mode, string input)
        {
            string prompt = BuildPrompt(mode, input);
            var results = new List<CompareWindow.Entry>();
            string currentModel = (ModelPicker.Text ?? "").Trim();

            foreach (var p in _providers)
            {
                if (IsKeyProvider(p.Id) && p.Id != "custom" && _settings.GetKey(p.Id).Length == 0)
                {
                    results.Add(new CompareWindow.Entry { Provider = p.DisplayName, Model = "", Seconds = 0, Text = "⏭️ Skipped — no API key saved for this one yet. Add it in the 🔑 Keys & Compute tab." });
                    continue;
                }
                if (p.Id == "opencode" && OpenCodeProvider.FindCli() == null)
                {
                    results.Add(new CompareWindow.Entry { Provider = p.DisplayName, Model = "", Seconds = 0, Text = "⏭️ Skipped — the OpenCode app isn't installed on this PC." });
                    continue;
                }
                string model = p.Id == Current.Id ? currentModel : "";
                StatusText.Text = $"🔀 Comparing — asking {p.DisplayName}…";
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    if (model.Length == 0)
                    {
                        using var mcts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                        var models = await p.GetModelsAsync(mcts.Token);
                        model = p.Id == "opencode" && models.Contains("opencode/nemotron-3-ultra-free")
                            ? "opencode/nemotron-3-ultra-free"
                            : models.Count > 0 ? models[0] : "";
                    }
                    using var pects = CancellationTokenSource.CreateLinkedTokenSource(_cts!.Token);
                    pects.CancelAfter(TimeSpan.FromSeconds(240));
                    string reply = await p.ChatAsync(model, prompt, pects.Token);
                    sw.Stop();
                    results.Add(new CompareWindow.Entry { Provider = p.DisplayName, Model = model, Seconds = sw.Elapsed.TotalSeconds, Text = reply });
                }
                catch (OperationCanceledException) when (_cts!.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    results.Add(new CompareWindow.Entry { Provider = p.DisplayName, Model = model, Seconds = sw.Elapsed.TotalSeconds, Text = "⚠️ " + ex.Message });
                }
            }

            StatusText.Text = "🔀 Compare done — the results are side by side in the compare window; 'Use this one' brings the winner back here.";
            var win = new CompareWindow(results, text =>
            {
                OutputBox.Text = text;
                if (mode == "beat") TryMakeBeat(text);
            })
            { Owner = this };
            win.Show();
        }

        private void TryMakeBeat(string reply)
        {
            int open = reply.IndexOf('{');
            int close = reply.LastIndexOf('}');
            if (open < 0 || close <= open)
            {
                StatusText.Text = "The AI didn't answer in beat format — try again, or describe the beat differently.";
                return;
            }
            try
            {
                using var doc = JsonDocument.Parse(reply.Substring(open, close - open + 1));
                var root = doc.RootElement;
                string style = root.GetProperty("style").GetString() ?? "boomrap";
                double bpm = root.TryGetProperty("bpm", out var b) ? b.GetDouble() : 95;
                double density = root.TryGetProperty("density", out var d) ? d.GetDouble() : 0.7;
                bool known = DrumGenerator.MyStyles.Any(s => s.Key == style) || DrumGenerator.Styles.Any(s => s.Key == style);
                if (!known) style = "boomrap";
                bpm = Math.Clamp(bpm, 60, 180);
                density = Math.Clamp(density, 0.2, 1.0);
                var pattern = DrumGenerator.Generate(style, bpm, density, true, Environment.TickCount);
                _owner.Patterns.Add(pattern);
                _owner.OpenPatternsOn(pattern);
                StatusText.Text = $"🥁 Beat created in the step sequencer — {pattern.Name} ({bpm:0} BPM). Tweak it there, then Render to Timeline.";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Couldn't build the beat from that answer: " + ex.Message;
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            if (OutputBox.Text.Length > 0)
            {
                Clipboard.SetText(OutputBox.Text);
                StatusText.Text = "Copied to clipboard ✓";
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _cts?.Cancel();
            _storyCts?.Cancel();
            _optCts?.Cancel();
            _rwCts?.Cancel();
            _ytCts?.Cancel();
            _hitCts?.Cancel();
            _visCts?.Cancel();
            _sgCts?.Cancel();
            _apCts?.Cancel();
            _chCts?.Cancel();
            _mvCts?.Cancel();
            _cvCts?.Cancel();
            _skCts?.Cancel();
            base.OnClosed(e);
        }
    }
}
