using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// The Providers directory (v3.19; split BY CATEGORY in v3.20 at
    /// Michael's direction — some services make music, some make video):
    /// two tabs, 🎵 Music &amp; Audio and 🎬 Video, each listing that
    /// category's providers. Dual-capable services (fal.ai, Replicate)
    /// appear in BOTH lists — one key covers everything they do. Every
    /// card is badged FREE or PAID. Providers with a KeyId have a working
    /// key box wired to ComputeSettings (encrypted at rest, the same store
    /// the AI Lab's Keys tab uses); the rest are honest directory listings.
    /// Opened from the AI Lab header and from Create Song.
    /// </summary>
    public partial class ProvidersWindow : Window
    {
        private record Prov(string Name, bool Music, bool Video, bool Free, string Blurb, string Note,
            string SignupUrl, string KeysUrl, string? KeyId);

        private static readonly Prov[] All =
        {
            new("fal.ai", true, true, false,
                "One key runs this app's cloud songs (ACE-Step), stem splitting AND all the video models.",
                "Works in BeatSync today — paste the key and you're connected.",
                "https://fal.ai/login", "https://fal.ai/dashboard/keys", "fal"),
            new("Replicate", true, true, false,
                "A huge catalog of hosted models. In this app today: song generation and stem splitting.",
                "Works in BeatSync today — paste the key and you're connected.",
                "https://replicate.com", "https://replicate.com/account/api-tokens", "replicate"),
            new("Hugging Face", true, false, true,
                "The home of open AI models, with a free tier. In this app: stem splitting at no cost.",
                "Works in BeatSync today — a free account token is all it takes.",
                "https://huggingface.co/join", "https://huggingface.co/settings/tokens", "hf"),
            new("🏠 Remote GPU — my other PC", true, false, true,
                "Songs generated on YOUR other computer's graphics card: the free BeatSync Engine runs there, and this app sends it jobs over your home network (or Tailscale, from anywhere). No per-run fees, no cloud, your music never leaves your machines. Your Rad-Dad voice conversion will live there too.",
                "Works in BeatSync today — save one or TWO engine addresses below (both your PCs can run an engine), pick 🏠 Remote GPU as the online provider in Create Song / AI Lab, and 🩺 Test each one. Every job goes to the first engine that answers — one's asleep, the other takes it. The engine itself gets installed on each PC once — that's the next setup step.",
                "", "", "remote"),
            new("Freesound", true, false, true,
                "Millions of free sound effects and samples for your library, from the 🔊 Sounds & Samples tab of the 🔎 Search Sounds & Music window.",
                "Free — its key lives in that window's 🔑 Freesound Key tab (View menu → 🔎 Search).",
                "https://freesound.org", "https://freesound.org/apiv2/apply", null),
            new("Internet Archive", true, false, true,
                "An enormous free library — the Live Music Archive's concert recordings, netlabel albums, old radio, community uploads — from the 🎵 Music & Recordings tab of the 🔎 Search Sounds & Music window.",
                "Free — no key, no account. It just works (View menu → 🔎 Search).",
                "https://archive.org", "", null),
            new("OpenCode", true, false, true,
                "Free AI models built right into the AI Lab — lyrics, titles and beat patterns.",
                "Already connected — nothing to set up, no key, no account.",
                "https://opencode.ai", "", null),
            new("Ollama", true, false, true,
                "Runs AI models on your own PC, fully offline and free. Install it, then pick 'Local (Ollama)' in the AI Lab.",
                "No key — it talks to the Ollama app on your machine.",
                "https://ollama.com", "", null),
            new("OpenRouter", true, false, true,
                "One key for hundreds of text models, several of them free. A good future lyrics/chat engine.",
                "Directory listing — sign up there; in-app generation arrives with its client.",
                "https://openrouter.ai", "https://openrouter.ai/keys", null),
            new("Google Gemini", true, false, true,
                "Google's models with a free API tier through AI Studio.",
                "Directory listing — sign up there; in-app generation arrives with its client.",
                "https://aistudio.google.com", "https://aistudio.google.com/app/apikey", null),
            new("WaveSpeedAI", false, true, false,
                "Kling, MiniMax and Wan video models under one key — an alternative route to the Video tab's models.",
                "Directory listing — sign up there; in-app generation arrives with its client.",
                "https://wavespeed.ai", "", null),
            new("Kling AI", false, true, false,
                "The official Kling video service — the same models the Video tab reaches through fal.ai.",
                "Directory listing — sign up there; in-app generation arrives with its client.",
                "https://klingai.com", "", null),
            new("Luma Dream Machine", false, true, false,
                "Luma's own video API. Its Ray 2 model also runs through fal.ai (in both lists above).",
                "Directory listing — sign up there; in-app generation arrives with its client.",
                "https://lumalabs.ai", "", null),
        };

        private readonly ComputeSettings _cs = ComputeSettings.Load();

        public ProvidersWindow()
        {
            InitializeComponent();
            foreach (var p in All)
            {
                if (p.Music) MusicCards.Children.Add(BuildCard(p));
                if (p.Video) VideoCards.Children.Add(BuildCard(p));
            }
        }

        private string GetKey(string id) => id switch
        {
            "fal" => _cs.GetFalKey(),
            "replicate" => _cs.GetOnlineKey(),
            "hf" => _cs.GetHfKey(),
            "remote" => _cs.RemoteEngineUrl,
            "remote2" => _cs.RemoteEngine2Url,
            _ => ""
        };

        private void SetKey(string id, string key)
        {
            switch (id)
            {
                case "fal": _cs.SetFalKey(key); break;
                case "replicate": _cs.SetOnlineKey(key); break;
                case "hf": _cs.SetHfKey(key); break;
                case "remote": _cs.RemoteEngineUrl = key.Trim(); break;
                case "remote2": _cs.RemoteEngine2Url = key.Trim(); break;
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


        /// <summary>One engine-address row of the Remote GPU card (v3.24: two PCs, each with its own address, Save and Test).</summary>
        private StackPanel BuildRemoteRow(string key, string label)
        {
            var wrap = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            wrap.Children.Add(new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xCD, 0xD8)) });
            var row = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var box = new TextBox
            {
                Text = GetKey(key),
                Background = new SolidColorBrush(Color.FromRgb(0x12, 0x15, 0x1C)),
                Foreground = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x3C)),
                Padding = new Thickness(6, 4, 6, 4),
                ToolTip = "That PC's engine address, e.g. http://192.168.1.50:7860 — or its Tailscale address"
            };
            var state = new TextBlock
            {
                Text = GetKey(key).Length > 0 ? "✓ address saved" : "no address yet",
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0xA0)), FontSize = 11,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0)
            };
            var save = Btn("💾 Save"); save.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(save, 1);
            string k = key;
            save.Click += (se, ev) => { SetKey(k, box.Text); state.Text = box.Text.Trim().Length > 0 ? "✓ address saved" : "address cleared"; };
            var test = Btn("🩺 Test"); test.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(test, 2);
            test.Click += async (se, ev) =>
            {
                string u = box.Text.Trim();
                if (u.Length == 0) { state.Text = "Type the engine address first."; return; }
                test.IsEnabled = false; state.Text = "Testing — asking that PC to say hello…";
                try { state.Text = "✓ " + await RemoteEngine.HealthAsync(u, CancellationToken.None); }
                catch (Exception ex) { state.Text = "✗ " + ex.Message; }
                finally { test.IsEnabled = true; }
            };
            row.Children.Add(box);
            row.Children.Add(save);
            row.Children.Add(test);
            wrap.Children.Add(row);
            wrap.Children.Add(state);
            return wrap;
        }

        private Border BuildCard(Prov p)
        {
            var root = new StackPanel { Margin = new Thickness(12) };

            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new TextBlock { Text = p.Name, FontSize = 15, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(Badge(p.Music && p.Video ? "🎵🎬 Music + Video" : p.Music ? "🎵 Music" : "🎬 Video", "#2A303C", "#C8CDD8"));
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

            if (p.KeyId == "remote")
            {
                root.Children.Add(BuildRemoteRow("remote", "Engine 1 — first choice (e.g. the GTX 1660 desktop)"));
                root.Children.Add(BuildRemoteRow("remote2", "Engine 2 — second PC (e.g. the RTX 3050 laptop)"));
            }
            else if (p.KeyId != null)
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

            var links = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0), Visibility = p.KeyId == "remote" ? Visibility.Collapsed : Visibility.Visible };
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
            catch { /* browser unavailable — nothing else sensible to do */ }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
