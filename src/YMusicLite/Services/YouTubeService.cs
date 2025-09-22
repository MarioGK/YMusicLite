using YoutubeExplode;
using YoutubeExplode.Playlists;
using YoutubeExplode.Videos;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;

namespace YMusicLite.Services;

public interface IYouTubeService
{
    Task<PlaylistInfo?> GetPlaylistInfoAsync(string playlistUrl);
    Task<List<TrackInfo>> GetPlaylistTracksAsync(string playlistId, string? userId = null);
    Task<VideoInfo?> GetVideoInfoAsync(string videoId);
}

public class YouTubeService : IYouTubeService
{
    private readonly YoutubeClient _youtube;
    private readonly ILogger<YouTubeService> _logger;
    private readonly IGoogleAuthService _googleAuthService;

    public YouTubeService(ILogger<YouTubeService> logger, IGoogleAuthService googleAuthService)
    {
        _logger = logger;
        _googleAuthService = googleAuthService;
        _youtube = new YoutubeClient();
    }

    public async Task<PlaylistInfo?> GetPlaylistInfoAsync(string playlistUrl)
    {
        try
        {
            // Support YouTube Music liked songs pseudo-playlist (LM) which is NOT a real public playlist.
            if (playlistUrl.Contains("list=LM", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(playlistUrl.Trim(), "LM", StringComparison.OrdinalIgnoreCase))
            {
                // We cannot derive count without an authenticated user + myRating=like enumeration,
                // so leave VideoCount=0 (caller can still load tracks via GetPlaylistTracksAsync("LM", userId))
                return new PlaylistInfo
                {
                    Id = "LM",
                    Title = "Liked Songs",
                    Description = "Your liked YouTube Music songs (virtual playlist)",
                    Author = "YouTube Music",
                    ThumbnailUrl = string.Empty,
                    VideoCount = 0,
                    Url = playlistUrl
                };
            }

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

    public async Task<List<TrackInfo>> GetPlaylistTracksAsync(string playlistId, string? userId = null)
    {
        try
        {
            // Special handling for YouTube Music "Liked songs" pseudo-playlist (LM)
            if (string.Equals(playlistId, "LM", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(userId))
                {
                    _logger.LogWarning("Cannot retrieve LM playlist without authenticated userId");
                    return new List<TrackInfo>();
                }
                return await GetLikedMusicTracksAsync(userId);
            }

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
    private async Task<List<TrackInfo>> GetLikedMusicTracksAsync(string userId)
    {
        // The "LM" pseudo playlist is not directly exposed as a playlist via Data API.
        // We emulate it using videos.list with myRating=like.
        var result = new List<TrackInfo>();
        try
        {
            var credentials = await _googleAuthService.GetCredentialsAsync(userId);
            if (credentials == null)
            {
                _logger.LogWarning("User {UserId} not authenticated; cannot load liked (LM) videos", userId);
                return result;
            }

            var yt = new YouTubeService(new Google.Apis.Services.BaseClientService.Initializer
            {
                HttpClientInitializer = credentials,
                ApplicationName = "YMusicLite"
            });

            string? nextPage = null;
            int page = 0;
            const int maxPages = 40; // safety cap (~2000 liked videos @50/page)
            do
            {
                var request = yt.Videos.List("snippet,contentDetails");
                request.MyRating = VideosResource.ListRequest.MyRatingEnum.Like;
                request.MaxResults = 50;
                request.PageToken = nextPage;

                var response = await request.ExecuteAsync();
                if (response.Items != null)
                {
                    foreach (var v in response.Items)
                    {
                        TimeSpan duration = TimeSpan.Zero;
                        var durationIso = v.ContentDetails?.Duration;
                        if (!string.IsNullOrEmpty(durationIso))
                        {
                            if (System.Xml.XmlConvert.TryToTimeSpan(durationIso, out var ts))
                                duration = ts;
                        }

                        result.Add(new TrackInfo
                        {
                            YouTubeId = v.Id ?? string.Empty,
                            Title = v.Snippet?.Title ?? "Unknown",
                            Artist = v.Snippet?.ChannelTitle ?? "Unknown",
                            Duration = duration,
                            ThumbnailUrl = v.Snippet?.Thumbnails?.Medium?.Url
                                            ?? v.Snippet?.Thumbnails?.Standard?.Url
                                            ?? v.Snippet?.Thumbnails?.High?.Url
                                            ?? string.Empty
                        });
                    }
                }

                nextPage = response.NextPageToken;
            } while (!string.IsNullOrEmpty(nextPage) && ++page < maxPages);

            _logger.LogInformation("Retrieved {Count} liked (LM) tracks via myRating=like for user {UserId}", result.Count, userId);
        }
        catch (Google.GoogleApiException apiEx)
        {
            _logger.LogError(apiEx, "Google API error retrieving liked (LM) videos for user {UserId}", userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error retrieving liked (LM) videos for user {UserId}", userId);
        }
        return result;
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