using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
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
            base.OnClosed(e);
        }
    }
}
