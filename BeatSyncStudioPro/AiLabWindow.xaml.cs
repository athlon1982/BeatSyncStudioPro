using System;
using System.Collections.Generic;
using System.Linq;
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
            OnlineKeyBox.Password = _compute.GetOnlineKey();
            OnlineKeyStatus.Text = _compute.GetOnlineKey().Length > 0 ? "Key saved ✓" : "";
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

        private void OnlineSaveKey_Click(object sender, RoutedEventArgs e)
        {
            _compute.SetOnlineKey(OnlineKeyBox.Password);
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
                    return "You control a drum machine. Available styles: boomrap, trap, house, funk, rock, dembow, lofi, drill, dancehall, westcoast, techno, dnb. " +
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
                string reply = await provider.ChatAsync(model, BuildPrompt(mode, input), _cts.Token);
                if (mode == "beat")
                {
                    OutputBox.Text = reply;
                    TryMakeBeat(reply);
                }
                else
                {
                    OutputBox.Text = reply;
                    StatusText.Text = "Done — edit it, copy it, make it yours.";
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
            base.OnClosed(e);
        }
    }
}
