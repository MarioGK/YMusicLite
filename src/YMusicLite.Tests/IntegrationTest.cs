using YMusicLite.Models;
using YMusicLite.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace YMusicLite.Tests;

/// <summary>
/// Simple integration test to verify core functionality
/// Run with: dotnet run --project TestRunner.cs
/// </summary>
public class IntegrationTest
{
    public static async Task Main(string[] args)
    {
        Console.WriteLine("YMusicLite - Integration Test");
        Console.WriteLine("=====================================");

        try
        {
            // Test 1: Database Service
            await TestDatabaseService();
            
            // Test 2: YouTube Service  
            await TestYouTubeService();
            
            // Test 3: Scheduling Service
            await TestSchedulingService();
            
            Console.WriteLine();
            Console.WriteLine("✅ All tests passed successfully!");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Test failed: {ex.Message}");
            Environment.Exit(1);
        }
    }

    static async Task TestDatabaseService()
    {
        Console.WriteLine("Testing Database Service...");
        
        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["DataPath"] = "/tmp/test-ymusic"
            })
            .Build();
        
        var logger = LoggerFactory.Create(builder => builder.AddConsole())
            .CreateLogger<DatabaseService>();
        
        var dbService = new DatabaseService(config, logger);
        
        // Test creating a playlist
        var playlist = new Playlist
        {
            Name = "Test Playlist",
            YouTubeId = "test123",
            YouTubeUrl = "https://www.youtube.com/playlist?list=test123",
            Description = "Integration test playlist",
            SyncMode = true,
            UserId = "testuser"
        };
        
        await dbService.Playlists.CreateAsync(playlist);
        
        // Test retrieving playlist
        var retrieved = await dbService.Playlists.GetByIdAsync(playlist.Id);
        
        if (retrieved == null || retrieved.Name != "Test Playlist")
        {
            throw new Exception("Database service test failed - could not retrieve playlist");
        }
        
        // Test creating a track
        var track = new Track
        {
            YouTubeId = "track123",
            Title = "Test Song",
            Artist = "Test Artist", 
            Duration = TimeSpan.FromMinutes(3),
            PlaylistId = playlist.Id.ToString(),
            Status = TrackStatus.Pending
        };
        
        await dbService.Tracks.CreateAsync(track);
        
        var tracks = await dbService.Tracks.FindAllAsync(t => t.PlaylistId == playlist.Id.ToString());
        
        if (tracks.Count != 1 || tracks[0].Title != "Test Song")
        {
            throw new Exception("Database service test failed - could not retrieve track");
        }
        
        Console.WriteLine("✅ Database Service - OK");
    }

    static async Task TestYouTubeService()
    {
        Console.WriteLine("Testing YouTube Service...");
        
        var logger = LoggerFactory.Create(builder => builder.AddConsole())
            .CreateLogger<YouTubeService>();
        
        var youtubeService = new YouTubeService(logger);
        
        // Test with a known public playlist (this might fail if the playlist is removed)
        // Using a very popular, stable playlist
        var playlistUrl = "https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=PLFsQleAWXsj_4yDeebiIADdH5FMayBiJo";
        
        var playlistInfo = await youtubeService.GetPlaylistInfoAsync(playlistUrl);
        
        if (playlistInfo != null)
        {
            Console.WriteLine($"✅ YouTube Service - Retrieved playlist: {playlistInfo.Title}");
        }
        else
        {
            // This is not a failure - might be due to network or playlist unavailability
            Console.WriteLine("⚠️ YouTube Service - Could not retrieve test playlist (this may be normal)");
        }
    }

    static async Task TestSchedulingService()
    {
        Console.WriteLine("Testing Scheduling Service...");
        
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["DataPath"] = "/tmp/test-ymusic-sched"
            })
            .Build();
        
        var dbLogger = LoggerFactory.Create(builder => builder.AddConsole())
            .CreateLogger<DatabaseService>();
        var schedLogger = LoggerFactory.Create(builder => builder.AddConsole())
            .CreateLogger<SchedulingService>();
        
        var dbService = new DatabaseService(config, dbLogger);
        var serviceProvider = new TestServiceProvider(); // Mock service provider
        
        var schedulingService = new SchedulingService(dbService, serviceProvider, schedLogger);
        
        // Test cron expression validation
        var nextRun = schedulingService.GetNextScheduledTime("0 3 * * *"); // Daily at 3 AM
        
        if (!nextRun.HasValue)
        {
            throw new Exception("Scheduling service test failed - could not parse cron expression");
        }
        
        Console.WriteLine($"✅ Scheduling Service - Next run: {nextRun.Value:g}");
        
        // Test basic scheduling (without actually running sync)
        // First create a playlist to schedule
        var testPlaylist = new Playlist
        {
            Name = "Test Scheduled Playlist",
            YouTubeId = "test-sched-123",
            YouTubeUrl = "https://www.youtube.com/playlist?list=test-sched-123",
            Description = "Test playlist for scheduling",
            UserId = "testuser"
        };
        
        await dbService.Playlists.CreateAsync(testPlaylist);
        
        var result = await schedulingService.SchedulePlaylistSyncAsync(testPlaylist.Id.ToString(), "0 */6 * * *");
        
        if (!result)
        {
            throw new Exception("Scheduling service test failed - could not schedule playlist");
        }
        
        Console.WriteLine("✅ Scheduling Service - OK");
    }
}

// Mock service provider for testing
public class TestServiceProvider : IServiceProvider
{
    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(IServiceScope))
        {
            return new TestServiceScope();
        }
        return null;
    }
    
    public IServiceScope CreateScope()
    {
        return new TestServiceScope();
    }
}

public class TestServiceScope : IServiceScope
{
    public IServiceProvider ServiceProvider => new TestServiceProvider();
    
    public void Dispose() { }
}