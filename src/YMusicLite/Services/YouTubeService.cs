using YoutubeExplode;
using YoutubeExplode.Playlists;
using YoutubeExplode.Videos;

namespace YMusicLite.Services;

public interface IYouTubeService
{
    Task<PlaylistInfo?> GetPlaylistInfoAsync(string playlistUrl);
    Task<List<TrackInfo>> GetPlaylistTracksAsync(string playlistId);
    Task<VideoInfo?> GetVideoInfoAsync(string videoId);
}

public class YouTubeService : IYouTubeService
{
    private readonly YoutubeClient _youtube;
    private readonly ILogger<YouTubeService> _logger;

    public YouTubeService(ILogger<YouTubeService> logger)
    {
        _logger = logger;
        _youtube = new YoutubeClient();
    }

    public async Task<PlaylistInfo?> GetPlaylistInfoAsync(string playlistUrl)
    {
        try
        {
            var playlistId = PlaylistId.TryParse(playlistUrl);
            if (playlistId is null)
            {
                _logger.LogWarning("Invalid playlist URL: {Url}", playlistUrl);
                return null;
            }

            var playlist = await _youtube.Playlists.GetAsync(playlistId.Value);
            
            return new PlaylistInfo
            {
                Id = playlist.Id,
                Title = playlist.Title,
                Description = playlist.Description,
                Author = playlist.Author?.ChannelTitle ?? "Unknown",
                ThumbnailUrl = playlist.Thumbnails.LastOrDefault()?.Url ?? string.Empty,
                VideoCount = playlist.Count ?? 0,
                Url = playlistUrl
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get playlist info for URL: {Url}", playlistUrl);
            return null;
        }
    }

    public async Task<List<TrackInfo>> GetPlaylistTracksAsync(string playlistId)
    {
        try
        {
            var tracks = new List<TrackInfo>();
            
            await foreach (var video in _youtube.Playlists.GetVideosAsync(PlaylistId.Parse(playlistId)))
            {
                tracks.Add(new TrackInfo
                {
                    YouTubeId = video.Id,
                    Title = video.Title,
                    Artist = video.Author.ChannelTitle,
                    Duration = video.Duration ?? TimeSpan.Zero,
                    ThumbnailUrl = video.Thumbnails.LastOrDefault()?.Url ?? string.Empty
                });
            }

            return tracks;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get tracks for playlist: {PlaylistId}", playlistId);
            return new List<TrackInfo>();
        }
    }

    public async Task<VideoInfo?> GetVideoInfoAsync(string videoId)
    {
        try
        {
            var video = await _youtube.Videos.GetAsync(VideoId.Parse(videoId));
            
            return new VideoInfo
            {
                Id = video.Id,
                Title = video.Title,
                Author = video.Author.ChannelTitle,
                Duration = video.Duration ?? TimeSpan.Zero,
                ThumbnailUrl = video.Thumbnails.LastOrDefault()?.Url ?? string.Empty,
                Description = video.Description
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get video info: {VideoId}", videoId);
            return null;
        }
    }
}

public class PlaylistInfo
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string ThumbnailUrl { get; set; } = string.Empty;
    public int VideoCount { get; set; }
    public string Url { get; set; } = string.Empty;
}

public class TrackInfo
{
    public string YouTubeId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; }
    public string ThumbnailUrl { get; set; } = string.Empty;
}

public class VideoInfo
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; }
    public string ThumbnailUrl { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}