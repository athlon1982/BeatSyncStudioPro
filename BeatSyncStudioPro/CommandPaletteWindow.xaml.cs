using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BeatSyncStudioPro
{
    /// <summary>One runnable command in the palette (and behind the hotkeys).</summary>
    public class AppCommand
    {
        public AppCommand(string name, string category, string hotkey, Action run)
        {
            Name = name;
            Category = category;
            Hotkey = hotkey;
            Run = run;
        }

        public string Name { get; }
        public string Category { get; }
        public string Hotkey { get; }
        public Action Run { get; }
    }

    /// <summary>Ctrl+K command palette: filter every app action by name and run it.</summary>
    public partial class CommandPaletteWindow : Window
    {
        private readonly List<AppCommand> _all;
        private Action? _pending;

        public CommandPaletteWindow(List<AppCommand> commands)
        {
            InitializeComponent();
            _all = commands;
            ApplyFilter("");
            Closed += (s, e) => _pending?.Invoke();
            Loaded += (s, e) => SearchBox.Focus();
        }

        private void ApplyFilter(string query)
        {
            var words = (query ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var matches = _all.Where(c =>
            {
                string hay = (c.Name + " " + c.Category).ToLowerInvariant();
                return words.All(w => hay.Contains(w));
            }).ToList();
            ResultsList.ItemsSource = matches;
            if (matches.Count > 0) ResultsList.SelectedIndex = 0;
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter(SearchBox.Text);

        private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down) { MoveSelection(1); e.Handled = true; }
            else if (e.Key == Key.Up) { MoveSelection(-1); e.Handled = true; }
            else if (e.Key == Key.Enter) { RunSelected(); e.Handled = true; }
            else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        }

        private void ResultsList_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { RunSelected(); e.Handled = true; }
            else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        }

        private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => RunSelected();

        private void MoveSelection(int delta)
        {
            int count = ResultsList.Items.Count;
            if (count == 0) return;
            int idx = Math.Clamp(ResultsList.SelectedIndex + delta, 0, count - 1);
            ResultsList.SelectedIndex = idx;
            ResultsList.ScrollIntoView(ResultsList.SelectedItem);
        }

        private void RunSelected()
        {
            if (ResultsList.SelectedItem is AppCommand cmd)
            {
                _pending = cmd.Run;
                Close();
            }
        }
    }
}
