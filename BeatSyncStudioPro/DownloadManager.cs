using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace BeatSyncStudioPro
{
    /// <summary>One tracked download: progress, speed, ETA, pause/cancel.</summary>
    public class DownloadItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));

        public string Name { get; }
        public DownloadItem(string name) => Name = name;

        private long _total = -1, _done;
        private string _state = "Downloading", _detail = "Starting…";

        public long TotalBytes { get => _total; set { _total = value; Raise(nameof(TotalBytes)); Raise(nameof(Percent)); Raise(nameof(Detail)); } }
        public long DoneBytes { get => _done; set { _done = value; Raise(nameof(DoneBytes)); Raise(nameof(Percent)); } }
        public double Percent => _total > 0 ? _done * 100.0 / _total : 0;
        public string State { get => _state; set { _state = value; Raise(nameof(State)); Raise(nameof(CanPause)); Raise(nameof(CanResume)); } }
        public string Detail { get => _detail; set { _detail = value; Raise(nameof(Detail)); } }
        public bool CanPause => State == "Downloading";
        public bool CanResume => State == "Paused";

        internal readonly CancellationTokenSource Cts = new CancellationTokenSource();
        internal readonly ManualResetEventSlim Gate = new ManualResetEventSlim(true);

        public void Pause() { if (State == "Downloading") { Gate.Reset(); State = "Paused"; } }
        public void Resume() { if (State == "Paused") { Gate.Set(); State = "Downloading"; } }
        public void Cancel() { Gate.Set(); Cts.Cancel(); }

        public static string FmtBytes(long b) => b < 0 ? "?" : b >= 1048576 ? $"{b / 1048576.0:0.0} MB" : $"{b / 1024.0:0} KB";
        public static string FmtEta(double seconds) => seconds < 90 ? $"~{seconds:0}s left" : $"~{seconds / 60:0}:{seconds % 60:00} left";
    }

    /// <summary>
    /// The app's download engine: streamed transfers with live progress,
    /// speed, ETA, pause and cancel, all listed in the Downloads window.
    /// Pausing holds the connection open (a very long pause can make a
    /// server drop it — cancelling and retrying is the fallback).
    /// </summary>
    public static class DownloadManager
    {
        public static ObservableCollection<DownloadItem> Items { get; } = new ObservableCollection<DownloadItem>();

        private static DownloadItem NewItem(string name)
        {
            var item = new DownloadItem(name);
            var disp = Application.Current?.Dispatcher;
            if (disp != null && !disp.CheckAccess()) disp.Invoke(() => Items.Insert(0, item));
            else Items.Insert(0, item);
            return item;
        }

        public static async Task<byte[]> DownloadBytesAsync(string url, string name, Action<DownloadItem>? onProgress = null)
        {
            var item = NewItem(name);
            using var ms = new MemoryStream();
            await RunAsync(item, url, ms, onProgress);
            return ms.ToArray();
        }

        public static async Task DownloadToFileAsync(string url, string name, string destPath, Action<DownloadItem>? onProgress = null)
        {
            var item = NewItem(name);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await RunAsync(item, url, fs, onProgress);
        }

        private static async Task RunAsync(DownloadItem item, string url, Stream dest, Action<DownloadItem>? onProgress)
        {
            var ct = item.Cts.Token;
            try
            {
                using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("BeatSyncStudioPro");
                using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                res.EnsureSuccessStatusCode();
                item.TotalBytes = res.Content.Headers.ContentLength ?? -1;
                await using var src = await res.Content.ReadAsStreamAsync(ct);

                var buffer = new byte[81920];
                var sw = Stopwatch.StartNew();
                long windowBytes = 0, lastDone = 0;
                double speed = 0;
                int read;
                while (true)
                {
                    item.Gate.Wait(ct);
                    read = await src.ReadAsync(buffer, 0, buffer.Length, ct);
                    if (read == 0) break;
                    await dest.WriteAsync(buffer, 0, read, ct);
                    item.DoneBytes += read;
                    windowBytes += read;
                    if (sw.ElapsedMilliseconds >= 300)
                    {
                        double inst = windowBytes / (sw.ElapsedMilliseconds / 1000.0);
                        speed = speed == 0 ? inst : speed * 0.55 + inst * 0.45;
                        windowBytes = 0; sw.Restart();
                        string eta = item.TotalBytes > 0 && speed > 0
                            ? DownloadItem.FmtEta((item.TotalBytes - item.DoneBytes) / speed) : "…";
                        item.Detail = item.TotalBytes > 0
                            ? $"{DownloadItem.FmtBytes(item.DoneBytes)} of {DownloadItem.FmtBytes(item.TotalBytes)} · {DownloadItem.FmtBytes((long)speed)}/s · {eta}"
                            : $"{DownloadItem.FmtBytes(item.DoneBytes)} · {DownloadItem.FmtBytes((long)speed)}/s";
                        onProgress?.Invoke(item);
                    }
                }
                item.State = "Done";
                item.Detail = $"Done — {DownloadItem.FmtBytes(item.DoneBytes)}";
                onProgress?.Invoke(item);
            }
            catch (OperationCanceledException)
            {
                item.State = "Cancelled";
                item.Detail = "Cancelled";
                onProgress?.Invoke(item);
                throw;
            }
            catch (Exception ex)
            {
                item.State = "Failed";
                item.Detail = "Failed: " + ex.Message;
                onProgress?.Invoke(item);
                throw;
            }
        }
    }
}
