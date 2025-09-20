using LiteDB;

namespace YMusicLite.Models;

public class SyncJob
{
    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.NewObjectId();
    
    public string PlaylistId { get; set; } = string.Empty;
    public SyncJobType Type { get; set; } = SyncJobType.Manual;
    public SyncJobStatus Status { get; set; } = SyncJobStatus.Pending;
    
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public TimeSpan? Duration { get; set; }
    
    // Progress tracking
    public int TotalTracks { get; set; }
    public int ProcessedTracks { get; set; }
    public int SuccessfulTracks { get; set; }
    public int FailedTracks { get; set; }
    public int SkippedTracks { get; set; }
    
    // Results
    public string? ErrorMessage { get; set; }
    public List<string> Logs { get; set; } = new();
    
    public string UserId { get; set; } = string.Empty;
}

public enum SyncJobType
{
    Manual,
    Scheduled,
    AutoSync
}

public enum SyncJobStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled
}