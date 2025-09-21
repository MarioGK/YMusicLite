using YMusicLite.Services;
using YMusicLite.Models;

namespace YMusicLite.Tests;

public class MetricsServiceTest
{
    public static void Main(string[] args)
    {
        var metrics = new MetricsService();
        metrics.TrackDownloadStart("t1", 1000);
        metrics.TrackDownloadProgress("t1", 250);
        metrics.TrackDownloadProgress("t1", 250);
        metrics.TrackDownloadCompleted("t1", 1000);
        metrics.UpdateActiveSync("pl1", SyncJobStatus.Running, 3, 10, DateTime.UtcNow.AddSeconds(-5));
        var snap = metrics.GetSnapshot();
        Console.WriteLine($"ActiveDownloads={snap.ActiveDownloads};Speed={snap.AggregateDownloadSpeedBytesPerSec};AvgProgress={snap.AverageTrackProgressPercent};SyncPercent={snap.ActiveSyncPercent}");
        // Basic sanity: snapshot object retrieved and SyncPercent matches processed/total (approx)
        if (snap.ActiveSyncTotal != 0 && Math.Abs(snap.ActiveSyncPercent - (snap.ActiveSyncProcessed * 100.0 / snap.ActiveSyncTotal)) > 0.01)
        {
            throw new Exception("Sync percent calculation mismatch");
        }
        Console.WriteLine("✅ MetricsService basic test passed");
    }
}