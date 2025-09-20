using LiteDB;

namespace YMusicLite.Models;

public class Playlist
{
    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.NewObjectId();
    
    public string Name { get; set; } = string.Empty;
    public string YouTubeId { get; set; } = string.Empty;
    public string YouTubeUrl { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    
    // Configuration
    public bool SyncMode { get; set; } = true;
    public bool AutoSync { get; set; } = false;
    public List<string> CronExpressions { get; set; } = new();
    public int MinDurationSeconds { get; set; } = 0;
    public int MaxDurationSeconds { get; set; } = 7200; // 2 hours
    
    // Status
    public PlaylistStatus Status { get; set; } = PlaylistStatus.Idle;
    public DateTime? LastSyncStarted { get; set; }
    public DateTime? LastSyncCompleted { get; set; }
    public string? LastSyncError { get; set; }
    
    // Statistics
    public int TotalTracks { get; set; }
    public int DownloadedTracks { get; set; }
    public long TotalSizeBytes { get; set; }
    
    public string UserId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum PlaylistStatus
{
    Idle,
    Syncing,
    Downloading,
    Error,
    Completed
}