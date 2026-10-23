using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// Library deep-scan (v3.95): walks every song in the Song Library
    /// and fills whatever analysis is missing — BPM and key (written
    /// into the library entries themselves) and energy (into the energy
    /// planner's cache). Afterwards the Set Builder's energy planner,
    /// the key-fit dots and Suggest Next all answer instantly instead of
    /// analyzing on first use. Cancellable between songs; the library
    /// is saved as it goes, so stopping early keeps the progress.
    /// </summary>
    public sealed class DeepScanResult
    {
        public int Total;
        public int Completed;
        public int AlreadyDone;
        public int Skipped;
        public int BpmFilled;
        public int KeyFilled;
        public int EnergyFilled;
        public bool Cancelled;
    }

    public static class LibraryDeepScan
    {
        public static async Task<DeepScanResult> ScanAsync(
            IProgress<(int Done, int Total, string Title)>? progress, CancellationToken ct)
        {
            var result = new DeepScanResult();
            var songs = SongLibraryStore.Load();
            result.Total = songs.Count;
            int done = 0;
            foreach (var song in songs)
            {
                if (ct.IsCancellationRequested) { result.Cancelled = true; break; }
                done++;
                progress?.Report((done, result.Total, song.DisplayTitle));
                try
                {
                    if (!File.Exists(song.Path)) { result.Skipped++; continue; }
                    bool hasEnergy = EnergyPlanner.TryGet(song.Path, out double cachedEnergy);
                    bool needsBpmKey = song.Bpm <= 0 || string.IsNullOrWhiteSpace(song.Key);
                    if (!needsBpmKey && hasEnergy) { result.AlreadyDone++; continue; }

                    double bpmBefore = song.Bpm;
                    string keyBefore = song.Key;
                    if (needsBpmKey) await SongLibraryStore.AnalyzeAsync(song);
                    if (song.Bpm > 0 && bpmBefore <= 0) result.BpmFilled++;
                    if (!string.IsNullOrWhiteSpace(song.Key) && string.IsNullOrWhiteSpace(keyBefore)) result.KeyFilled++;

                    if (!hasEnergy)
                    {
                        double e = await EnergyPlanner.AnalyzeAsync(song.Path, song.Bpm);
                        if (e >= 0) result.EnergyFilled++;
                    }
                    else if (cachedEnergy >= 0) result.EnergyFilled++;

                    result.Completed++;
                    if (result.Completed % 10 == 0) SongLibraryStore.Save(songs);
                }
                catch { result.Skipped++; }
            }
            try { SongLibraryStore.Save(songs); } catch { }
            return result;
        }
    }
}
