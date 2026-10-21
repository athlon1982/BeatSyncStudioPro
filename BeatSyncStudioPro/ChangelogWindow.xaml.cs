using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// The built-in changelog: renders the CHANGELOG.md embedded in the app
    /// (so it always matches the version you're running) as a formatted,
    /// scrollable document.
    /// </summary>
    public partial class ChangelogWindow : Window
    {
        private static readonly Brush HeadingBrush = new SolidColorBrush(Color.FromRgb(0x69, 0xF0, 0xAE));
        private static readonly Brush SectionBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x3D));
        private static readonly Brush BodyBrush = new SolidColorBrush(Color.FromRgb(0xD5, 0xDA, 0xE4));
        private static readonly Brush DimBrush = new SolidColorBrush(Color.FromRgb(0xA0, 0xA5, 0xB5));

        public ChangelogWindow()
        {
            InitializeComponent();
            DocViewer.Document = BuildDocument(LoadChangelogText());
        }

        private static string LoadChangelogText()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                string? name = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("CHANGELOG.md", StringComparison.OrdinalIgnoreCase));
                if (name != null)
                {
                    using var stream = asm.GetManifestResourceStream(name);
                    if (stream != null)
                    {
                        using var reader = new StreamReader(stream);
                        return reader.ReadToEnd();
                    }
                }
            }
            catch { }
            return "# Changelog\n\nThe changelog file could not be loaded.";
        }

        private static FlowDocument BuildDocument(string markdown)
        {
            var doc = new FlowDocument
            {
                PagePadding = new Thickness(18, 12, 18, 12),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 13.5
            };

            foreach (var rawLine in markdown.Replace("\r\n", "\n").Split('\n'))
            {
                string line = rawLine.TrimEnd();
                if (line.Length == 0) continue;

                if (line.StartsWith("## "))
                {
                    var p = new Paragraph
                    {
                        FontSize = 17,
                        FontWeight = FontWeights.Bold,
                        Foreground = HeadingBrush,
                        Margin = new Thickness(0, 16, 0, 4),
                        KeepWithNext = true
                    };
                    AddRuns(p, line.Substring(3));
                    doc.Blocks.Add(p);
                }
                else if (line.StartsWith("### "))
                {
                    var p = new Paragraph
                    {
                        FontSize = 13,
                        FontWeight = FontWeights.Bold,
                        Foreground = SectionBrush,
                        Margin = new Thickness(0, 9, 0, 3),
                        KeepWithNext = true
                    };
                    AddRuns(p, line.Substring(4));
                    doc.Blocks.Add(p);
                }
                else if (line.StartsWith("# "))
                {
                    var p = new Paragraph
                    {
                        FontSize = 21,
                        FontWeight = FontWeights.Bold,
                        Foreground = Brushes.White,
                        Margin = new Thickness(0, 0, 0, 6)
                    };
                    AddRuns(p, line.Substring(2));
                    doc.Blocks.Add(p);
                }
                else if (line.StartsWith("- "))
                {
                    var p = new Paragraph
                    {
                        Foreground = BodyBrush,
                        Margin = new Thickness(18, 1.5, 0, 1.5)
                    };
                    p.Inlines.Add(new Run("•  ") { Foreground = DimBrush });
                    AddRuns(p, line.Substring(2));
                    doc.Blocks.Add(p);
                }
                else
                {
                    var p = new Paragraph
                    {
                        Foreground = DimBrush,
                        Margin = new Thickness(0, 1, 0, 1)
                    };
                    AddRuns(p, line);
                    doc.Blocks.Add(p);
                }
            }
            return doc;
        }

        /// <summary>Adds text runs, turning **bold** spans into bold runs.</summary>
        private static void AddRuns(Paragraph p, string text)
        {
            int i = 0;
            while (i < text.Length)
            {
                int start = text.IndexOf("**", i, StringComparison.Ordinal);
                if (start < 0)
                {
                    p.Inlines.Add(new Run(text.Substring(i)));
                    break;
                }
                int end = text.IndexOf("**", start + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    p.Inlines.Add(new Run(text.Substring(i)));
                    break;
                }
                if (start > i) p.Inlines.Add(new Run(text.Substring(i, start - i)));
                p.Inlines.Add(new Bold(new Run(text.Substring(start + 2, end - start - 2))));
                i = end + 2;
            }
        }
    }
}
