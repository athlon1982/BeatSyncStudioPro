using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// The Providers directory (v3.19, Michael's ask): every AI service the
    /// app can use — or will use — in one place, each labeled FREE or PAID,
    /// with signup + API-key links. Providers with a KeyId have a working
    /// key box wired to ComputeSettings (encrypted at rest, same store the
    /// AI Lab's Keys tab uses); the rest are honest directory listings.
    /// Opened from the AI Lab header and from Create Song.
    /// </summary>
    public partial class ProvidersWindow : Window
    {
        private record Prov(string Name, string Kind, bool Free, string Blurb, string Note,
            string SignupUrl, string KeysUrl, string? KeyId);

        private static readonly Prov[] All =
        {
            new("fal.ai", "🎵 Audio + 🎬 Video", false,
                "One key runs this app's cloud songs (ACE-Step), stem splitting AND all the video models.",
                "Works in BeatSync today — paste the key and you're connected.",
                "https://fal.ai/login", "https://fal.ai/dashboard/keys", "fal"),
            new("Replicate", "🎵 Audio + 🎬 Video", false,
                "A huge catalog of hosted models. In this app today: song generation and stem splitting.",
                "Works in BeatSync today — paste the key and you're connected.",
                "https://replicate.com", "https://replicate.com/account/api-tokens", "replicate"),
            new("Hugging Face", "🎵 Audio", true,
                "The home of open AI models, with a free tier. In this app: stem splitting at no cost.",
                "Works in BeatSync today — a free account token is all it takes.",
                "https://huggingface.co/join", "https://huggingface.co/settings/tokens", "hf"),
            new("OpenCode", "🎵 Audio (lyrics & beats)", true,
                "Free AI models built right into the AI Lab — lyrics, titles and beat patterns.",
                "Already connected — nothing to set up, no key, no account.",
                "https://opencode.ai", "", null),
            new("Ollama", "🎵 Audio (local AI)", true,
                "Runs AI models on your own PC, fully offline and free. Install it, then pick 'Local (Ollama)' in the AI Lab.",
                "No key — it talks to the Ollama app on your machine.",
                "https://ollama.com", "", null),
            new("OpenRouter", "🎵 Audio (text AI)", true,
                "One key for hundreds of text models, several of them free. Good future lyrics/chat engine.",
                "Directory listing — sign up there; in-app generation arrives with its client.",
                "https://openrouter.ai", "https://openrouter.ai/keys", null),
            new("Google Gemini", "🎵 Audio (text AI)", true,
                "Google's models with a free API tier through AI Studio.",
                "Directory listing — sign up there; in-app generation arrives with its client.",
                "https://aistudio.google.com", "https://aistudio.google.com/app/apikey", null),
            new("WaveSpeedAI", "🎬 Video", false,
                "Kling, MiniMax and Wan video models under one key — an alternative route to the Video tab's models.",
                "Directory listing — sign up there; in-app generation arrives with its client.",
                "https://wavespeed.ai", "", null),
            new("Kling AI", "🎬 Video", false,
                "The official Kling video service — the same models the Video tab reaches through fal.ai.",
                "Directory listing — sign up there; in-app generation arrives with its client.",
                "https://klingai.com", "", null),
            new("Luma Dream Machine", "🎬 Video", false,
                "Luma's own video API. Its Ray 2 model also runs through fal.ai above.",
                "Directory listing — sign up there; in-app generation arrives with its client.",
                "https://lumalabs.ai", "", null),
        };

        private readonly ComputeSettings _cs = ComputeSettings.Load();

        public ProvidersWindow()
        {
            InitializeComponent();
            foreach (var p in All) Cards.Children.Add(BuildCard(p));
        }

        private string GetKey(string id) => id switch
        {
            "fal" => _cs.GetFalKey(),
            "replicate" => _cs.GetOnlineKey(),
            "hf" => _cs.GetHfKey(),
            _ => ""
        };

        private void SetKey(string id, string key)
        {
            switch (id)
            {
                case "fal": _cs.SetFalKey(key); break;
                case "replicate": _cs.SetOnlineKey(key); break;
                case "hf": _cs.SetHfKey(key); break;
            }
            _cs.Save();
        }

        private static Border Badge(string text, string bg, string fg)
        {
            return new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bg)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = text, FontSize = 10, FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fg))
                }
            };
        }

        private static Button Btn(string text) => new Button
        {
            Content = text, Padding = new Thickness(12, 5, 12, 5), Cursor = System.Windows.Input.Cursors.Hand,
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x3C)), Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3D, 0x44, 0x54)), BorderThickness = new Thickness(1)
        };

        private Border BuildCard(Prov p)
        {
            var root = new StackPanel { Margin = new Thickness(12) };

            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new TextBlock { Text = p.Name, FontSize = 15, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(Badge(p.Kind, "#2A303C", "#C8CDD8"));
            head.Children.Add(p.Free
                ? Badge("FREE", "#14532D", "#7EE2A8")
                : Badge("PAID", "#4A3205", "#FFC95C"));
            root.Children.Add(head);

            root.Children.Add(new TextBlock
            {
                Text = p.Blurb, Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xCD, 0xD8)),
                FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0)
            });
            root.Children.Add(new TextBlock
            {
                Text = p.Note, Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0xA0)),
                FontSize = 11, FontStyle = FontStyles.Italic, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0)
            });

            if (p.KeyId != null)
            {
                var keyRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
                keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var box = new PasswordBox
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x12, 0x15, 0x1C)),
                    Foreground = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x3C)),
                    Padding = new Thickness(6, 4, 6, 4),
                    ToolTip = "Paste the API key — it's stored encrypted on this PC, never shown again"
                };
                var state = new TextBlock
                {
                    Text = GetKey(p.KeyId).Length > 0 ? "✓ key saved" : "no key yet",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0xA0)), FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0)
                };
                Grid.SetColumn(state, 2);
                var save = Btn("💾 Save key"); save.Margin = new Thickness(8, 0, 0, 0);
                Grid.SetColumn(save, 1);
                string id = p.KeyId;
                save.Click += (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(box.Password))
                    {
                        SetKey(id, box.Password);
                        box.Clear();
                        state.Text = "✓ key saved";
                    }
                };
                keyRow.Children.Add(box);
                keyRow.Children.Add(save);
                keyRow.Children.Add(state);
                root.Children.Add(keyRow);
            }

            var links = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            var signup = Btn("🆕 Sign up");
            signup.Click += (s, e) => OpenUrl(p.SignupUrl);
            links.Children.Add(signup);
            if (!string.IsNullOrEmpty(p.KeysUrl))
            {
                var keys = Btn("🔑 Get API key"); keys.Margin = new Thickness(8, 0, 0, 0);
                keys.Click += (s, e) => OpenUrl(p.KeysUrl);
                links.Children.Add(keys);
            }
            root.Children.Add(links);

            return new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x17, 0x1B, 0x23)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x3C)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(0, 0, 0, 10),
                Child = root
            };
        }

        private static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { /* browser unavailable — the URL is in the tooltip of the card buttons */ }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
