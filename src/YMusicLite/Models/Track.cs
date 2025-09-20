using LiteDB;

namespace YMusicLite.Models;

public class Track
{
    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.NewObjectId();
    
    public string YouTubeId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; }
    public string ThumbnailUrl { get; set; } = string.Empty;
    
    // Download info
    public TrackStatus Status { get; set; } = TrackStatus.Pending;
    public string? LocalFilePath { get; set; }
    public long FileSizeBytes { get; set; }
    public int DownloadProgress { get; set; } // 0-100
    public string? ErrorMessage { get; set; }
    
    // Relationships
    public string PlaylistId { get; set; } = string.Empty;
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DownloadedAt { get; set; }
}

public enum TrackStatus
{
    Pending,
    Downloading,
    Converting,
    Completed,
    Error,
    Skipped
}