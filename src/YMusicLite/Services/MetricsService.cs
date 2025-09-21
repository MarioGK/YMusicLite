using System.Collections.Concurrent;
using YMusicLite.Models;

namespace YMusicLite.Services;

public interface IMetricsService
{
    MetricsSnapshot GetSnapshot();
    void TrackDownloadStart(string trackId, long? expectedBytes = null);
    void TrackDownloadProgress(string trackId, long bytesReceivedDelta);
    void TrackDownloadCompleted(string trackId, long totalBytes);
    void TrackDownloadFailed(string trackId);
    void ResetDownload(string trackId);
    void UpdateActiveSync(string playlistId, SyncJobStatus status, int processed, int total, DateTime startedAt);
    void CompleteSync(string playlistId, SyncJobStatus finalStatus, DateTime completedAt);
}

public record MetricsSnapshot(
    int ActiveDownloads,
    double AggregateDownloadSpeedBytesPerSec,
    double AverageTrackProgressPercent,
    int CompletedDownloadsLastHour,
    int FailedDownloadsLastHour,
    DateTime? LastSyncStarted,
    DateTime? LastSyncCompleted,
    string? ActiveSyncPlaylistId,
    SyncJobStatus? ActiveSyncStatus,
    int ActiveSyncProcessed,
    int ActiveSyncTotal,
    double ActiveSyncPercent,
    DateTime GeneratedAtUtc
);

public class MetricsService : IMetricsService
{
    private class DownloadState
    {
        public long BytesReceived;
        public long? ExpectedBytes;
        public DateTime StartedAtUtc = DateTime.UtcNow;
        public DateTime LastUpdatedUtc = DateTime.UtcNow;
        public bool Completed;
        public bool Failed;
    }

    private readonly ConcurrentDictionary<string, DownloadState> _downloads = new();
    private readonly ConcurrentQueue<(DateTime when, bool success)> _recentCompleted = new();
    private readonly object _syncLock = new();
    private string? _activeSyncPlaylistId;
    private SyncJobStatus? _activeSyncStatus;
    private int _activeSyncProcessed;
    private int _activeSyncTotal;
    private DateTime? _activeSyncStartedAt;
    private DateTime? _lastSyncCompletedAt;

    public MetricsSnapshot GetSnapshot()
    {
        PruneOldCompletions();

        var now = DateTime.UtcNow;
        var active = _downloads.Values.Where(d => !d.Completed && !d.Failed).ToList();

        // Aggregate speed: sum(bytes delta / time) approximated by bytes so far / elapsed for each
        double totalSpeed = active.Sum(d =>
        {
            var elapsed = (now - d.StartedAtUtc).TotalSeconds;
            if (elapsed <= 0.25) return 0d;
            return d.BytesReceived / elapsed;
        });

        double avgProgress = 0;
        var progressEligible = active.Where(d => d.ExpectedBytes.HasValue && d.ExpectedBytes > 0).ToList();
        if (progressEligible.Any())
        {
            avgProgress = progressEligible.Average(d => Math.Clamp(d.BytesReceived * 100.0 / d.ExpectedBytes!.Value, 0, 100));
        }

        var arr = _recentCompleted.ToArray();
        var hourAgo = now - TimeSpan.FromHours(1);
        int completedOk = arr.Count(e => e.when >= hourAgo && e.success);
        int failed = arr.Count(e => e.when >= hourAgo && !e.success);

        double syncPercent = _activeSyncTotal > 0 ? (_activeSyncProcessed * 100.0 / _activeSyncTotal) : 0;

        return new MetricsSnapshot(
            ActiveDownloads: active.Count,
            AggregateDownloadSpeedBytesPerSec: totalSpeed,
            AverageTrackProgressPercent: avgProgress,
            CompletedDownloadsLastHour: completedOk,
            FailedDownloadsLastHour: failed,
            LastSyncStarted: _activeSyncStartedAt,
            LastSyncCompleted: _lastSyncCompletedAt,
            ActiveSyncPlaylistId: _activeSyncPlaylistId,
            ActiveSyncStatus: _activeSyncStatus,
            ActiveSyncProcessed: _activeSyncProcessed,
            ActiveSyncTotal: _activeSyncTotal,
            ActiveSyncPercent: syncPercent,
            GeneratedAtUtc: now
        );
    }

    public void TrackDownloadStart(string trackId, long? expectedBytes = null)
    {
        _downloads[trackId] = new DownloadState { ExpectedBytes = expectedBytes };
    }

    public void TrackDownloadProgress(string trackId, long bytesReceivedDelta)
    {
        if (_downloads.TryGetValue(trackId, out var state))
        {
            state.BytesReceived += bytesReceivedDelta;
            state.LastUpdatedUtc = DateTime.UtcNow;
        }
    }

    public void TrackDownloadCompleted(string trackId, long totalBytes)
    {
        if (_downloads.TryGetValue(trackId, out var state))
        {
            state.BytesReceived = totalBytes;
            state.Completed = true;
            state.LastUpdatedUtc = DateTime.UtcNow;
            _recentCompleted.Enqueue((DateTime.UtcNow, true));
        }
    }

    public void TrackDownloadFailed(string trackId)
    {
        if (_downloads.TryGetValue(trackId, out var state))
        {
            state.Failed = true;
            state.LastUpdatedUtc = DateTime.UtcNow;
            _recentCompleted.Enqueue((DateTime.UtcNow, false));
        }
    }

    public void ResetDownload(string trackId)
    {
        _downloads.TryRemove(trackId, out _);
    }

    public void UpdateActiveSync(string playlistId, SyncJobStatus status, int processed, int total, DateTime startedAt)
    {
        lock (_syncLock)
        {
            _activeSyncPlaylistId = playlistId;
            _activeSyncStatus = status;
            _activeSyncProcessed = processed;
            _activeSyncTotal = total;
            _activeSyncStartedAt = startedAt;
        }
    }

    public void CompleteSync(string playlistId, SyncJobStatus finalStatus, DateTime completedAt)
    {
        lock (_syncLock)
        {
            if (_activeSyncPlaylistId == playlistId)
            {
                _activeSyncStatus = finalStatus;
                _lastSyncCompletedAt = completedAt;
                _activeSyncPlaylistId = null;
                _activeSyncProcessed = 0;
                _activeSyncTotal = 0;
                _activeSyncStartedAt = null;
            }
        }
    }

    private void PruneOldCompletions()
    {
        var threshold = DateTime.UtcNow - TimeSpan.FromHours(1);
        while (_recentCompleted.TryPeek(out var item) && item.when < threshold)
        {
            _recentCompleted.TryDequeue(out _);
        }
    }
}