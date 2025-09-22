using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Xunit;
using YMusicLite.Models;
using YMusicLite.Services;

namespace YMusicLite.Tests;

public class SyncServiceTests
{
    private readonly FakeDatabaseService _db = new();
    private readonly Mock<IYouTubeService> _youTube = new();
    private readonly Mock<IDownloadService> _download = new();
    private readonly MetricsService _metrics = new();
    private readonly Mock<ILogger<SyncService>> _logger = new();
    private readonly SyncService _syncService;

    public SyncServiceTests()
    {
        // Provide deterministic empty playlist tracks
        _youTube
            .Setup(s => s.GetPlaylistTracksAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync(new List<TrackInfo>());

        // Download service never called (no tracks), but set up safe default
        _download
            .Setup(d => d.DownloadPlaylistTracksAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IProgress<(int completed, int total, string currentTrack)>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Track>());

        // Minimal configuration
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "DownloadPath", "/tmp/ymusic-tests/downloads" },
                { "MaxParallelDownloads", "2" }
            })
            .Build();

        _syncService = new SyncService(_db, _youTube.Object, _download.Object, _logger.Object, config, _metrics);

        // Seed a playlist
        var playlist = new Playlist
        {
            Name = "Test Sync",
            YouTubeId = "PLTEST123",
            YouTubeUrl = "https://www.youtube.com/playlist?list=PLTEST123",
            Description = "Sync test",
            SyncMode = true,
            UserId = "user1"
        };
        _db.Playlists.CreateAsync(playlist).GetAwaiter().GetResult();
        _playlistId = playlist.Id.ToString();
    }

    private readonly string _playlistId;

    [Fact]
    public async Task SecondSyncCallReturnsExistingJob()
    {
        var job1 = await _syncService.SyncPlaylistAsync(_playlistId, SyncJobType.Manual);

        // Immediately call again (active job should still be running)
        var job2 = await _syncService.SyncPlaylistAsync(_playlistId, SyncJobType.Manual);

        job2.Id.Should().Be(job1.Id, "second sync should return existing running job instead of starting a new one");
    }

    [Fact]
    public async Task ActiveSyncJobQueryableDuringRun()
    {
        await _syncService.SyncPlaylistAsync(_playlistId, SyncJobType.Manual);

        var active = await _syncService.GetActiveSyncJobAsync(_playlistId);
        active.Should().NotBeNull();
        active!.Status.Should().Be(SyncJobStatus.Running);
    }

    [Fact]
    public async Task CancelSyncTransitionsJobAndPlaylistToIdleOrCancelled()
    {
        var job = await _syncService.SyncPlaylistAsync(_playlistId, SyncJobType.Manual);

        // Cancel
        var result = await _syncService.CancelSyncAsync(job.Id.ToString());
        result.Should().BeTrue();

        // Give background task a moment to finish cleanup
        await Task.Delay(200);

        var active = await _syncService.GetActiveSyncJobAsync(_playlistId);
        active.Should().BeNull("cancelled job should no longer be active");

        var storedJob = await _db.SyncJobs.GetByIdAsync(job.Id);
        storedJob.Should().NotBeNull();
        storedJob!.Status.Should().Be(SyncJobStatus.Cancelled);

        var playlist = await _db.Playlists.GetByIdAsync(_playlistId);
        playlist.Should().NotBeNull();
        playlist!.Status.Should().BeOneOf(PlaylistStatus.Idle, PlaylistStatus.Error);
    }

    // ----------------- Lightweight in-memory database fakes -----------------

    private class FakeDatabaseService : IDatabaseService
    {
        public FakeRepo<User> Users { get; } = new();
        public FakeRepo<Playlist> Playlists { get; } = new();
        public FakeRepo<Track> Tracks { get; } = new();
        public FakeRepo<SyncJob> SyncJobs { get; } = new();
        public FakeRepo<PkceSession> PkceSessions { get; } = new();

        IRepository<User> IDatabaseService.Users => Users;
        IRepository<Playlist> IDatabaseService.Playlists => Playlists;
        IRepository<Track> IDatabaseService.Tracks => Tracks;
        IRepository<SyncJob> IDatabaseService.SyncJobs => SyncJobs;
        IRepository<PkceSession> IDatabaseService.PkceSessions => PkceSessions;

        public void Initialize() { }
    }

    private class FakeRepo<T> : IRepository<T> where T : class
    {
        private readonly List<T> _items = new();

        private LiteDB.ObjectId EnsureId(T entity)
        {
            var idProp = typeof(T).GetProperty("Id");
            if (idProp == null) return LiteDB.ObjectId.Empty;
            var current = idProp.GetValue(entity);
            if (current is LiteDB.ObjectId oid && oid != LiteDB.ObjectId.Empty) return oid;
            var newId = LiteDB.ObjectId.NewObjectId();
            idProp.SetValue(entity, newId);
            return newId;
        }

        public Task<LiteDB.ObjectId> InsertAsync(T entity)
        {
            var id = EnsureId(entity);
            _items.Add(entity);
            return Task.FromResult(id);
        }

        public Task<LiteDB.ObjectId> CreateAsync(T entity) => InsertAsync(entity);

        public Task<bool> UpdateAsync(T entity) => Task.FromResult(true);

        public Task<bool> DeleteAsync(LiteDB.ObjectId id)
        {
            var idProp = typeof(T).GetProperty("Id");
            var removed = _items.RemoveAll(x => idProp?.GetValue(x)?.ToString() == id.ToString()) > 0;
            return Task.FromResult(removed);
        }

        public Task<bool> DeleteAsync(string id)
        {
            if (!LiteDB.ObjectId.TryParse(id, out var oid)) return Task.FromResult(false);
            return DeleteAsync(oid);
        }

        public Task<T?> GetByIdAsync(LiteDB.ObjectId id)
        {
            var idProp = typeof(T).GetProperty("Id");
            var entity = _items.FirstOrDefault(x => idProp?.GetValue(x)?.ToString() == id.ToString());
            return Task.FromResult(entity);
        }

        public Task<T?> GetByIdAsync(string id)
        {
            var idProp = typeof(T).GetProperty("Id");
            var entity = _items.FirstOrDefault(x => idProp?.GetValue(x)?.ToString() == id);
            return Task.FromResult(entity);
        }

        public Task<IEnumerable<T>> GetAllAsync() => Task.FromResult<IEnumerable<T>>(_items);

        public Task<List<T>> FindAllAsync(System.Linq.Expressions.Expression<Func<T, bool>> predicate)
        {
            var compiled = predicate.Compile();
            return Task.FromResult(_items.Where(compiled).ToList());
        }

        public Task<IEnumerable<T>> FindAsync(System.Linq.Expressions.Expression<Func<T, bool>> predicate)
        {
            var compiled = predicate.Compile();
            return Task.FromResult(_items.Where(compiled).AsEnumerable());
        }

        public Task<T?> FindOneAsync(System.Linq.Expressions.Expression<Func<T, bool>> predicate)
        {
            var compiled = predicate.Compile();
            return Task.FromResult(_items.FirstOrDefault(compiled));
        }
    }
}