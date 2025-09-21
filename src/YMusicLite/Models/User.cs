using LiteDB;

namespace YMusicLite.Models;

public class User
{
    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.NewObjectId();
    
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string GoogleId { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTime TokenExpiry { get; set; }
    public string Scopes { get; set; } = string.Empty; // space separated scopes granted
    
    // User preferences
    public bool DarkTheme { get; set; } = true;
    public string DownloadPath { get; set; } = "/app/data/downloads";
    public int MaxParallelDownloads { get; set; } = 3;
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}