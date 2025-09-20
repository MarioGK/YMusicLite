using YoutubeExplode;
using YoutubeExplode.Converter;
using YoutubeExplode.Videos.Streams;
using YMusicLite.Models;

namespace YMusicLite.Services;

public interface IDownloadService
{
    Task<bool> DownloadTrackAsync(Track track, string outputDirectory, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
    Task<List<Track>> DownloadPlaylistTracksAsync(string playlistId, string outputDirectory, IProgress<(int completed, int total, string currentTrack)>? progress = null, CancellationToken cancellationToken = default);
    Task<bool> ConvertToMp3Async(string inputPath, string outputPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
}

public class DownloadService : IDownloadService
{
    private readonly YoutubeClient _youtube;
    private readonly IDatabaseService _database;
    private readonly ILogger<DownloadService> _logger;
    private readonly IConfiguration _configuration;

    public DownloadService(
        IDatabaseService database, 
        ILogger<DownloadService> logger,
        IConfiguration configuration)
    {
        _youtube = new YoutubeClient();
        _database = database;
        _logger = logger;
        _configuration = configuration;
    }

    public async Task<bool> DownloadTrackAsync(Track track, string outputDirectory, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Starting download for track: {Title} ({YouTubeId})", track.Title, track.YouTubeId);
            
            track.Status = TrackStatus.Downloading;
            track.UpdatedAt = DateTime.UtcNow;
            await _database.Tracks.UpdateAsync(track);

            // Ensure output directory exists
            Directory.CreateDirectory(outputDirectory);

            // Get video metadata
            var video = await _youtube.Videos.GetAsync(track.YouTubeId, cancellationToken);
            
            // Get stream manifest
            var streamManifest = await _youtube.Videos.Streams.GetManifestAsync(track.YouTubeId, cancellationToken);
            
            // Get the best audio stream
            var audioStream = streamManifest
                .GetAudioOnlyStreams()
                .Where(s => s.Container == Container.Mp4 || s.Container == Container.WebM)
                .GetWithHighestBitrate();

            if (audioStream == null)
            {
                _logger.LogError("No suitable audio stream found for track: {YouTubeId}", track.YouTubeId);
                track.Status = TrackStatus.Error;
                track.ErrorMessage = "No suitable audio stream found";
                await _database.Tracks.UpdateAsync(track);
                return false;
            }

            // Create safe filename
            var safeTitle = GetSafeFileName($"{track.Artist} - {track.Title}");
            var outputPath = Path.Combine(outputDirectory, $"{safeTitle}.mp3");
            
            // Check if file already exists
            if (File.Exists(outputPath))
            {
                _logger.LogInformation("Track already exists: {OutputPath}", outputPath);
                track.Status = TrackStatus.Completed;
                track.LocalFilePath = outputPath;
                track.FileSizeBytes = new FileInfo(outputPath).Length;
                track.DownloadedAt = DateTime.UtcNow;
                track.UpdatedAt = DateTime.UtcNow;
                await _database.Tracks.UpdateAsync(track);
                return true;
            }

            _logger.LogInformation("Downloading and converting to MP3: {OutputPath}", outputPath);
            
            track.Status = TrackStatus.Converting;
            await _database.Tracks.UpdateAsync(track);

            // Download and convert to MP3 directly
            var progressReporter = new Progress<double>(p =>
            {
                track.DownloadProgress = (int)(p * 100);
                progress?.Report(p);
            });

            await _youtube.Videos.DownloadAsync(track.YouTubeId, outputPath, o => o
                .SetContainer(Container.Mp3)
                .SetPreset(ConversionPreset.Medium), progressReporter, cancellationToken);

            // Update track with file info
            var fileInfo = new FileInfo(outputPath);
            track.Status = TrackStatus.Completed;
            track.LocalFilePath = outputPath;
            track.FileSizeBytes = fileInfo.Length;
            track.DownloadProgress = 100;
            track.DownloadedAt = DateTime.UtcNow;
            track.UpdatedAt = DateTime.UtcNow;
            track.ErrorMessage = null;

            await _database.Tracks.UpdateAsync(track);

            _logger.LogInformation("Successfully downloaded track: {Title} ({SizeMB:F1} MB)", 
                track.Title, fileInfo.Length / 1024.0 / 1024.0);
            
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Download cancelled for track: {YouTubeId}", track.YouTubeId);
            track.Status = TrackStatus.Error;
            track.ErrorMessage = "Download cancelled";
            await _database.Tracks.UpdateAsync(track);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download track: {YouTubeId}", track.YouTubeId);
            track.Status = TrackStatus.Error;
            track.ErrorMessage = ex.Message;
            track.UpdatedAt = DateTime.UtcNow;
            await _database.Tracks.UpdateAsync(track);
            return false;
        }
    }

    public async Task<List<Track>> DownloadPlaylistTracksAsync(
        string playlistId, 
        string outputDirectory, 
        IProgress<(int completed, int total, string currentTrack)>? progress = null, 
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Starting batch download for playlist: {PlaylistId}", playlistId);
            
            var tracks = await _database.Tracks.FindAllAsync(t => t.PlaylistId == playlistId);
            var pendingTracks = tracks.Where(t => t.Status == TrackStatus.Pending || t.Status == TrackStatus.Error).ToList();
            
            _logger.LogInformation("Found {PendingCount} tracks to download out of {TotalCount} total tracks", 
                pendingTracks.Count, tracks.Count);

            var completedTracks = new List<Track>();
            var maxParallel = _configuration.GetValue<int>("MaxParallelDownloads", 3);
            
            var semaphore = new SemaphoreSlim(maxParallel, maxParallel);
            var completedCount = 0;
            
            var tasks = pendingTracks.Select(async track =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    var trackProgress = new Progress<double>(p => 
                    {
                        // Individual track progress could be reported here if needed
                    });
                    
                    var success = await DownloadTrackAsync(track, outputDirectory, trackProgress, cancellationToken);
                    
                    if (success)
                    {
                        lock (completedTracks)
                        {
                            completedTracks.Add(track);
                            completedCount++;
                            progress?.Report((completedCount, pendingTracks.Count, track.Title));
                        }
                    }
                    
                    return track;
                }
                finally
                {
                    semaphore.Release();
                }
            });
            
            await Task.WhenAll(tasks);
            
            _logger.LogInformation("Batch download completed. Successfully downloaded {SuccessCount} out of {TotalCount} tracks", 
                completedTracks.Count, pendingTracks.Count);
            
            return completedTracks;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed during batch download for playlist: {PlaylistId}", playlistId);
            throw;
        }
    }

    public async Task<bool> ConvertToMp3Async(string inputPath, string outputPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(inputPath))
            {
                _logger.LogError("Input file does not exist: {InputPath}", inputPath);
                return false;
            }

            _logger.LogInformation("Converting file to MP3: {InputPath} -> {OutputPath}", inputPath, outputPath);
            
            // Ensure output directory exists
            var outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            // For now, since YoutubeExplode.Converter handles this internally during download,
            // this method is primarily for standalone conversion needs
            
            // If input is already MP3, just copy
            if (Path.GetExtension(inputPath).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(inputPath, outputPath, overwrite: true);
                progress?.Report(1.0);
                return true;
            }

            // For other formats, we would need additional conversion logic
            // This is a placeholder for more complex conversion scenarios
            _logger.LogWarning("Direct conversion not implemented for non-MP3 files. File: {InputPath}", inputPath);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to convert file: {InputPath}", inputPath);
            return false;
        }
    }

    private static string GetSafeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var safeName = fileName;
        
        foreach (var c in invalidChars)
        {
            safeName = safeName.Replace(c, '_');
        }
        
        // Remove multiple consecutive underscores and trim
        while (safeName.Contains("__"))
        {
            safeName = safeName.Replace("__", "_");
        }
        
        return safeName.Trim('_').Trim();
    }
}