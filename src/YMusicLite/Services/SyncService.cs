using YMusicLite.Models;

namespace YMusicLite.Services;

public interface ISyncService
{
    Task<SyncJob> SyncPlaylistAsync(string playlistId, SyncJobType jobType = SyncJobType.Manual, CancellationToken cancellationToken = default);
    Task<bool> CancelSyncAsync(string syncJobId);
    Task<SyncJob?> GetActiveSyncJobAsync(string playlistId);
    Task<List<SyncJob>> GetSyncHistoryAsync(string playlistId, int limit = 50);
}

public class SyncService : ISyncService
{
    private readonly IDatabaseService _database;
    private readonly IYouTubeService _youtubeService;
    private readonly IDownloadService _downloadService;
    private readonly ILogger<SyncService> _logger;
    private readonly IConfiguration _configuration;
    private readonly Dictionary<string, CancellationTokenSource> _activeSyncs = new();
    private readonly object _lock = new object();
    private readonly IMetricsService _metrics;

    public SyncService(
        IDatabaseService database,
        IYouTubeService youtubeService,
        IDownloadService downloadService,
        ILogger<SyncService> logger,
        IConfiguration configuration,
        IMetricsService metrics)
    {
        _database = database;
        _youtubeService = youtubeService;
        _downloadService = downloadService;
        _logger = logger;
        _configuration = configuration;
        _metrics = metrics;
    }

    public async Task<SyncJob> SyncPlaylistAsync(string playlistId, SyncJobType jobType = SyncJobType.Manual, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting sync for playlist: {PlaylistId} (Type: {JobType})", playlistId, jobType);

        // Check if sync is already running for this playlist
        var existingJob = await GetActiveSyncJobAsync(playlistId);
        if (existingJob != null)
        {
            _logger.LogWarning("Sync already running for playlist: {PlaylistId}", playlistId);
            return existingJob;
        }

        // Get playlist from database
        var playlist = await _database.Playlists.GetByIdAsync(playlistId);
        if (playlist == null)
        {
            throw new ArgumentException($"Playlist not found: {playlistId}");
        }

        // Create sync job
        var syncJob = new SyncJob
        {
            PlaylistId = playlistId,
            Type = jobType,
            Status = SyncJobStatus.Running,
            StartedAt = DateTime.UtcNow,
            UserId = playlist.UserId
        };

        await _database.SyncJobs.CreateAsync(syncJob);

        // Create cancellation token source for this sync
        var syncCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_lock)
        {
            _activeSyncs[syncJob.Id.ToString()] = syncCts;
        }

        // Update playlist status
        playlist.Status = PlaylistStatus.Syncing;
        playlist.LastSyncStarted = DateTime.UtcNow;
        playlist.LastSyncError = null;
        await _database.Playlists.UpdateAsync(playlist);

        // Start sync process in background
        _ = Task.Run(async () =>
        {
            try
            {
                _metrics.UpdateActiveSync(playlist.Id.ToString(), SyncJobStatus.Running, 0, 0, syncJob.StartedAt);
                await PerformSyncAsync(playlist, syncJob, syncCts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in sync process for playlist: {PlaylistId}", playlistId);
                await HandleSyncError(playlist, syncJob, ex.Message);
            }
            finally
            {
                if (syncJob.Status is SyncJobStatus.Completed or SyncJobStatus.Failed or SyncJobStatus.Cancelled)
                {
                    _metrics.CompleteSync(playlist.Id.ToString(), syncJob.Status, DateTime.UtcNow);
                }
                // Cleanup
                lock (_lock)
                {
                    _activeSyncs.Remove(syncJob.Id.ToString());
                }
                syncCts.Dispose();
            }
        }, cancellationToken);

        return syncJob;
    }

    public async Task<bool> CancelSyncAsync(string syncJobId)
    {
        try
        {
            _logger.LogInformation("Cancelling sync job: {SyncJobId}", syncJobId);

            lock (_lock)
            {
                if (_activeSyncs.TryGetValue(syncJobId, out var cts))
                {
                    cts.Cancel();
                    _activeSyncs.Remove(syncJobId);
                    cts.Dispose();
                }
            }

            // Update sync job status
            var syncJob = await _database.SyncJobs.GetByIdAsync(syncJobId);
            if (syncJob != null)
            {
                syncJob.Status = SyncJobStatus.Cancelled;
                syncJob.CompletedAt = DateTime.UtcNow;
                syncJob.Duration = DateTime.UtcNow - syncJob.StartedAt;
                syncJob.Logs.Add($"[{DateTime.UtcNow:HH:mm:ss}] Sync cancelled by user");
                await _database.SyncJobs.UpdateAsync(syncJob);

                // Update playlist status
                var playlist = await _database.Playlists.GetByIdAsync(syncJob.PlaylistId);
                if (playlist != null)
                {
                    playlist.Status = PlaylistStatus.Idle;
                    await _database.Playlists.UpdateAsync(playlist);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel sync job: {SyncJobId}", syncJobId);
            return false;
        }
    }

    public async Task<SyncJob?> GetActiveSyncJobAsync(string playlistId)
    {
        try
        {
            return await _database.SyncJobs.FindOneAsync(job => 
                job.PlaylistId == playlistId && 
                job.Status == SyncJobStatus.Running);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get active sync job for playlist: {PlaylistId}", playlistId);
            return null;
        }
    }

    public async Task<List<SyncJob>> GetSyncHistoryAsync(string playlistId, int limit = 50)
    {
        try
        {
            var jobs = await _database.SyncJobs.FindAllAsync(job => job.PlaylistId == playlistId);
            return jobs.OrderByDescending(j => j.StartedAt).Take(limit).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get sync history for playlist: {PlaylistId}", playlistId);
            return new List<SyncJob>();
        }
    }

    private async Task PerformSyncAsync(Playlist playlist, SyncJob syncJob, CancellationToken cancellationToken)
    {
        try
        {
            syncJob.Logs.Add($"[{DateTime.UtcNow:HH:mm:ss}] Starting sync for playlist: {playlist.Name}");
            await _database.SyncJobs.UpdateAsync(syncJob);

            // Get latest tracks from YouTube
            _logger.LogInformation("Fetching tracks from YouTube for playlist: {PlaylistId}", playlist.YouTubeId);
            var youtubeTracksInfo = await _youtubeService.GetPlaylistTracksAsync(playlist.YouTubeId);
            
            if (cancellationToken.IsCancellationRequested)
            {
                await HandleSyncCancellation(playlist, syncJob);
                return;
            }

            // Filter tracks by duration
            var filteredTracks = youtubeTracksInfo.Where(t => 
                t.Duration.TotalSeconds >= playlist.MinDurationSeconds && 
                t.Duration.TotalSeconds <= playlist.MaxDurationSeconds).ToList();

            syncJob.TotalTracks = filteredTracks.Count;
            syncJob.Logs.Add($"[{DateTime.UtcNow:HH:mm:ss}] Found {filteredTracks.Count} tracks (filtered by duration)");
            await _database.SyncJobs.UpdateAsync(syncJob);

            // Get existing tracks from database
            var existingTracks = await _database.Tracks.FindAllAsync(t => t.PlaylistId == playlist.Id.ToString());
            var existingTrackIds = existingTracks.Select(t => t.YouTubeId).ToHashSet();

            // Identify new tracks to add
            var newTracks = filteredTracks.Where(yt => !existingTrackIds.Contains(yt.YouTubeId)).ToList();
            
            // Add new tracks to database
            foreach (var trackInfo in newTracks)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var track = new Track
                {
                    YouTubeId = trackInfo.YouTubeId,
                    Title = trackInfo.Title,
                    Artist = trackInfo.Artist,
                    Duration = trackInfo.Duration,
                    ThumbnailUrl = trackInfo.ThumbnailUrl,
                    PlaylistId = playlist.Id.ToString(),
                    Status = TrackStatus.Pending
                };

                await _database.Tracks.CreateAsync(track);
                _logger.LogDebug("Added new track: {Title} by {Artist}", track.Title, track.Artist);
            }

            if (newTracks.Count > 0)
            {
                syncJob.Logs.Add($"[{DateTime.UtcNow:HH:mm:ss}] Added {newTracks.Count} new tracks to database");
                await _database.SyncJobs.UpdateAsync(syncJob);
            }

            // Handle sync mode (delete removed tracks)
            if (playlist.SyncMode)
            {
                var youtubeTrackIds = filteredTracks.Select(t => t.YouTubeId).ToHashSet();
                var tracksToRemove = existingTracks.Where(t => !youtubeTrackIds.Contains(t.YouTubeId)).ToList();

                foreach (var track in tracksToRemove)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    // Delete local file if it exists
                    if (!string.IsNullOrEmpty(track.LocalFilePath) && File.Exists(track.LocalFilePath))
                    {
                        try
                        {
                            File.Delete(track.LocalFilePath);
                            _logger.LogDebug("Deleted local file: {FilePath}", track.LocalFilePath);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to delete local file: {FilePath}", track.LocalFilePath);
                        }
                    }

                    // Remove track from database
                    await _database.Tracks.DeleteAsync(track.Id.ToString());
                    _logger.LogDebug("Removed track: {Title} by {Artist}", track.Title, track.Artist);
                }

                if (tracksToRemove.Count > 0)
                {
                    syncJob.Logs.Add($"[{DateTime.UtcNow:HH:mm:ss}] Removed {tracksToRemove.Count} tracks no longer in playlist");
                    await _database.SyncJobs.UpdateAsync(syncJob);
                }
            }

            // Download tracks
            playlist.Status = PlaylistStatus.Downloading;
            await _database.Playlists.UpdateAsync(playlist);

            var downloadPath = GetDownloadPath(playlist);
            var allTracks = await _database.Tracks.FindAllAsync(t => t.PlaylistId == playlist.Id.ToString());
            var tracksToDownload = allTracks.Where(t => t.Status == TrackStatus.Pending || t.Status == TrackStatus.Error).ToList();

            if (tracksToDownload.Count > 0)
            {
                syncJob.Logs.Add($"[{DateTime.UtcNow:HH:mm:ss}] Starting downloads for {tracksToDownload.Count} tracks");
                await _database.SyncJobs.UpdateAsync(syncJob);

                var progress = new Progress<(int completed, int total, string currentTrack)>(p =>
                {
                    syncJob.ProcessedTracks = p.completed;
                    if (p.completed > syncJob.SuccessfulTracks + syncJob.FailedTracks)
                    {
                        syncJob.SuccessfulTracks = p.completed - syncJob.FailedTracks;
                    }
                    _metrics.UpdateActiveSync(playlist.Id.ToString(), SyncJobStatus.Running, syncJob.ProcessedTracks, syncJob.TotalTracks, syncJob.StartedAt);
                });

                var downloadedTracks = await _downloadService.DownloadPlaylistTracksAsync(
                    playlist.Id.ToString(), downloadPath, progress, cancellationToken);

                syncJob.SuccessfulTracks = downloadedTracks.Count;
                syncJob.FailedTracks = tracksToDownload.Count - downloadedTracks.Count;
            }

            // Update playlist statistics
            var finalTracks = await _database.Tracks.FindAllAsync(t => t.PlaylistId == playlist.Id.ToString());
            var completedTracks = finalTracks.Where(t => t.Status == TrackStatus.Completed).ToList();
            
            playlist.TotalTracks = finalTracks.Count;
            playlist.DownloadedTracks = completedTracks.Count;
            playlist.TotalSizeBytes = completedTracks.Sum(t => t.FileSizeBytes);
            playlist.Status = PlaylistStatus.Completed;
            playlist.LastSyncCompleted = DateTime.UtcNow;
            playlist.UpdatedAt = DateTime.UtcNow;
            
            await _database.Playlists.UpdateAsync(playlist);

            // Complete sync job
            syncJob.Status = SyncJobStatus.Completed;
            syncJob.CompletedAt = DateTime.UtcNow;
            syncJob.Duration = DateTime.UtcNow - syncJob.StartedAt;
            syncJob.ProcessedTracks = finalTracks.Count;
            syncJob.Logs.Add($"[{DateTime.UtcNow:HH:mm:ss}] Sync completed successfully");
            
            await _database.SyncJobs.UpdateAsync(syncJob);

            _logger.LogInformation("Sync completed for playlist: {PlaylistId}. Downloaded {SuccessfulTracks}/{TotalTracks} tracks", 
                playlist.Id, syncJob.SuccessfulTracks, syncJob.TotalTracks);
        }
        catch (OperationCanceledException)
        {
            await HandleSyncCancellation(playlist, syncJob);
        }
        catch (Exception ex)
        {
            await HandleSyncError(playlist, syncJob, ex.Message);
            throw;
        }
    }

    private async Task HandleSyncError(Playlist playlist, SyncJob syncJob, string errorMessage)
    {
        _logger.LogError("Sync failed for playlist: {PlaylistId}. Error: {ErrorMessage}", playlist.Id, errorMessage);

        playlist.Status = PlaylistStatus.Error;
        playlist.LastSyncError = errorMessage;
        await _database.Playlists.UpdateAsync(playlist);

        syncJob.Status = SyncJobStatus.Failed;
        syncJob.ErrorMessage = errorMessage;
        syncJob.CompletedAt = DateTime.UtcNow;
        syncJob.Duration = DateTime.UtcNow - syncJob.StartedAt;
        syncJob.Logs.Add($"[{DateTime.UtcNow:HH:mm:ss}] Sync failed: {errorMessage}");
        
        await _database.SyncJobs.UpdateAsync(syncJob);
    }

    private async Task HandleSyncCancellation(Playlist playlist, SyncJob syncJob)
    {
        _logger.LogInformation("Sync cancelled for playlist: {PlaylistId}", playlist.Id);

        playlist.Status = PlaylistStatus.Idle;
        await _database.Playlists.UpdateAsync(playlist);

        syncJob.Status = SyncJobStatus.Cancelled;
        syncJob.CompletedAt = DateTime.UtcNow;
        syncJob.Duration = DateTime.UtcNow - syncJob.StartedAt;
        syncJob.Logs.Add($"[{DateTime.UtcNow:HH:mm:ss}] Sync cancelled");
        
        await _database.SyncJobs.UpdateAsync(syncJob);
    }

    private string GetDownloadPath(Playlist playlist)
    {
        var basePath = _configuration.GetValue<string>("DownloadPath", "/app/data/downloads");
        var playlistPath = Path.Combine(basePath, GetSafeFileName(playlist.Name));
        Directory.CreateDirectory(playlistPath);
        return playlistPath;
    }

    private static string GetSafeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var safeName = fileName;
        
        foreach (var c in invalidChars)
        {
            safeName = safeName.Replace(c, '_');
        }
        
        return safeName.Trim('_').Trim();
    }
}