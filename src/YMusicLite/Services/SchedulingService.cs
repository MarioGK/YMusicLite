using Cronos;
using YMusicLite.Models;

namespace YMusicLite.Services;

public interface ISchedulingService
{
    Task<bool> SchedulePlaylistSyncAsync(string playlistId, string cronExpression);
    Task<bool> UnschedulePlaylistSyncAsync(string playlistId);
    Task<List<string>> GetScheduledPlaylistsAsync();
    Task ExecuteScheduledSyncsAsync(CancellationToken cancellationToken = default);
    DateTime? GetNextScheduledTime(string cronExpression);
}

public class SchedulingService : BackgroundService, ISchedulingService
{
    private readonly IDatabaseService _database;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SchedulingService> _logger;
    private readonly Dictionary<string, (CronExpression Expression, Timer Timer)> _scheduledJobs = new();
    private readonly object _lock = new object();

    public SchedulingService(
        IDatabaseService database,
        IServiceProvider serviceProvider,
        ILogger<SchedulingService> logger)
    {
        _database = database;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task<bool> SchedulePlaylistSyncAsync(string playlistId, string cronExpression)
    {
        try
        {
            _logger.LogInformation("Scheduling sync for playlist {PlaylistId} with expression: {CronExpression}", 
                playlistId, cronExpression);

            // Validate cron expression
            var cron = CronExpression.Parse(cronExpression);
            
            // Update playlist with cron expression
            var playlist = await _database.Playlists.GetByIdAsync(playlistId);
            if (playlist == null)
            {
                _logger.LogError("Playlist not found: {PlaylistId}", playlistId);
                return false;
            }

            if (!playlist.CronExpressions.Contains(cronExpression))
            {
                playlist.CronExpressions.Add(cronExpression);
                playlist.AutoSync = true;
                playlist.UpdatedAt = DateTime.UtcNow;
                await _database.Playlists.UpdateAsync(playlist);
            }

            // Schedule the job
            await ScheduleJobAsync(playlistId, cron);
            
            _logger.LogInformation("Successfully scheduled sync for playlist: {PlaylistId}", playlistId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to schedule sync for playlist: {PlaylistId}", playlistId);
            return false;
        }
    }

    public async Task<bool> UnschedulePlaylistSyncAsync(string playlistId)
    {
        try
        {
            _logger.LogInformation("Unscheduling sync for playlist: {PlaylistId}", playlistId);

            lock (_lock)
            {
                if (_scheduledJobs.TryGetValue(playlistId, out var job))
                {
                    job.Timer.Dispose();
                    _scheduledJobs.Remove(playlistId);
                }
            }

            // Update playlist to remove auto sync
            var playlist = await _database.Playlists.GetByIdAsync(playlistId);
            if (playlist != null)
            {
                playlist.AutoSync = false;
                playlist.CronExpressions.Clear();
                playlist.UpdatedAt = DateTime.UtcNow;
                await _database.Playlists.UpdateAsync(playlist);
            }

            _logger.LogInformation("Successfully unscheduled sync for playlist: {PlaylistId}", playlistId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to unschedule sync for playlist: {PlaylistId}", playlistId);
            return false;
        }
    }

    public async Task<List<string>> GetScheduledPlaylistsAsync()
    {
        try
        {
            var playlists = await _database.Playlists.FindAllAsync(p => p.AutoSync && p.CronExpressions.Count > 0);
            return playlists.Select(p => p.Id.ToString()).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get scheduled playlists");
            return new List<string>();
        }
    }

    public DateTime? GetNextScheduledTime(string cronExpression)
    {
        try
        {
            var cron = CronExpression.Parse(cronExpression);
            var utcNow = DateTime.UtcNow;
            return cron.GetNextOccurrence(utcNow);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse cron expression: {CronExpression}", cronExpression);
            return null;
        }
    }

    public async Task ExecuteScheduledSyncsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var playlists = await _database.Playlists.FindAllAsync(p => p.AutoSync && p.CronExpressions.Count > 0);
            
            foreach (var playlist in playlists)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    foreach (var cronExpr in playlist.CronExpressions)
                    {
                        var nextRun = GetNextScheduledTime(cronExpr);
                        if (nextRun.HasValue)
                        {
                            var cron = CronExpression.Parse(cronExpr);
                            var utcNow = DateTime.UtcNow;
                            
                            // Check if we should run now (within the last minute)
                            // Simple check: if next run is in the future and last sync was more than an hour ago
                            var shouldRun = false;
                            if (playlist.LastSyncStarted.HasValue)
                            {
                                var timeSinceLastSync = utcNow - playlist.LastSyncStarted.Value;
                                var nextRunTime = cron.GetNextOccurrence(utcNow);
                                
                                // If it's been more than the cron interval since last sync, run now
                                if (timeSinceLastSync.TotalHours >= 1) // Basic check for now
                                {
                                    shouldRun = true;
                                }
                            }
                            else
                            {
                                // Never synced, should run
                                shouldRun = true;
                            }
                            
                            if (shouldRun)
                            {
                                _logger.LogInformation("Executing scheduled sync for playlist: {PlaylistId}", playlist.Id);
                                
                                using var scope = _serviceProvider.CreateScope();
                                var syncService = scope.ServiceProvider.GetRequiredService<ISyncService>();
                                await syncService.SyncPlaylistAsync(playlist.Id.ToString(), SyncJobType.Scheduled);
                                break; // Only run once per check cycle
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to execute scheduled sync for playlist: {PlaylistId}", playlist.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute scheduled syncs");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Scheduling service started");

        // Load existing scheduled jobs on startup
        await LoadScheduledJobsAsync();

        // Main scheduling loop - check every minute
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ExecuteScheduledSyncsAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Scheduling service cancelled");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in scheduling service main loop");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); // Wait before retrying
            }
        }

        _logger.LogInformation("Scheduling service stopped");
    }

    private async Task LoadScheduledJobsAsync()
    {
        try
        {
            _logger.LogInformation("Loading scheduled jobs on startup");
            
            var playlists = await _database.Playlists.FindAllAsync(p => p.AutoSync && p.CronExpressions.Count > 0);
            
            foreach (var playlist in playlists)
            {
                foreach (var cronExpr in playlist.CronExpressions)
                {
                    try
                    {
                        var cron = CronExpression.Parse(cronExpr);
                        await ScheduleJobAsync(playlist.Id.ToString(), cron);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to load scheduled job for playlist {PlaylistId} with expression {CronExpression}", 
                            playlist.Id, cronExpr);
                    }
                }
            }
            
            _logger.LogInformation("Loaded {Count} scheduled jobs", _scheduledJobs.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load scheduled jobs");
        }
    }

    private Task ScheduleJobAsync(string playlistId, CronExpression cronExpression)
    {
        var utcNow = DateTime.UtcNow;
        var nextRun = cronExpression.GetNextOccurrence(utcNow);
        
        if (!nextRun.HasValue)
        {
            _logger.LogWarning("No next occurrence found for cron expression: {CronExpression}", cronExpression.ToString());
            return Task.CompletedTask;
        }

        var delay = nextRun.Value - utcNow;
        if (delay.TotalMilliseconds <= 0)
        {
            delay = TimeSpan.FromMilliseconds(1000); // Schedule for next second if time has passed
        }

        _logger.LogDebug("Scheduling job for playlist {PlaylistId} to run at {NextRun} (delay: {Delay})", 
            playlistId, nextRun.Value, delay);

        lock (_lock)
        {
            // Remove existing timer if any
            if (_scheduledJobs.TryGetValue(playlistId, out var existingJob))
            {
                existingJob.Timer.Dispose();
            }

            // Create new timer
            var timer = new Timer(_ =>
            {
                // Run on threadpool to avoid blocking the timer thread
                _ = Task.Run(async () =>
                {
                    try
                    {
                        _logger.LogInformation("Executing scheduled sync for playlist: {PlaylistId}", playlistId);

                        using var scope = _serviceProvider.CreateScope();
                        var syncService = scope.ServiceProvider.GetRequiredService<ISyncService>();
                        await syncService.SyncPlaylistAsync(playlistId, SyncJobType.Scheduled);

                        // Reschedule for next occurrence
                        await ScheduleJobAsync(playlistId, cronExpression);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error executing scheduled sync for playlist: {PlaylistId}", playlistId);
                    }
                });
            }, null, delay, Timeout.InfiniteTimeSpan);

            _scheduledJobs[playlistId] = (cronExpression, timer);
        }
        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        lock (_lock)
        {
            foreach (var (_, timer) in _scheduledJobs.Values)
            {
                timer.Dispose();
            }
            _scheduledJobs.Clear();
        }
        
        base.Dispose();
    }
}