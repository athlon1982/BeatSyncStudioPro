using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace BeatSyncStudioPro
{
    /// <summary>Live view of DownloadManager's transfers.</summary>
    public partial class DownloadsWindow : Window
    {
        public DownloadsWindow()
        {
            InitializeComponent();
            ItemsList.ItemsSource = DownloadManager.Items;
        }

        private void Pause_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is DownloadItem item)
            {
                if (item.State == "Paused") item.Resume();
                else item.Pause();
                b.Content = item.State == "Paused" ? "▶" : "⏸";
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is DownloadItem item) item.Cancel();
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in DownloadManager.Items.Where(i => i.State is "Done" or "Cancelled" or "Failed").ToList())
                DownloadManager.Items.Remove(item);
        }
    }
}
