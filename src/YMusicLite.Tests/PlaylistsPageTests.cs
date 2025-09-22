using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using YMusicLite.Models;
using YMusicLite.Services;
using MudBlazor.Services;

namespace YMusicLite.Tests;

public class PlaylistsPageTests : IDisposable
{
    private readonly TestContext _ctx = new();

    public PlaylistsPageTests()
    {
        _ctx.Services.AddMudServices();

        // Fakes / Mocks
        _fakeDb = new FakeDatabaseService();

        _mockYouTube = new Mock<IYouTubeService>();
        _mockYouTube
            .Setup(s => s.GetPlaylistInfoAsync(It.IsAny<string>()))
            .ReturnsAsync((string url) => new PlaylistInfo
            {
                Id = "TESTID",
                Title = "Fetched Title",
                Description = "Fetched Description",
                Url = url
            });

        _mockSync = new Mock<ISyncService>();
        _mockSync
            .Setup(s => s.SyncPlaylistAsync(It.IsAny<string>(), It.IsAny<SyncJobType>(), default))
            .ReturnsAsync((string pid, SyncJobType t, System.Threading.CancellationToken _) =>
                new SyncJob
                {
                    PlaylistId = pid,
                    Type = t,
                    Status = SyncJobStatus.Running,
                    StartedAt = DateTime.UtcNow
                });

        _mockScheduling = new Mock<ISchedulingService>();
        _mockScheduling
            .Setup(s => s.SchedulePlaylistSyncAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        _mockScheduling
            .Setup(s => s.UnschedulePlaylistSyncAsync(It.IsAny<string>()))
            .ReturnsAsync(true);

        _ctx.Services.AddSingleton<IDatabaseService>(_fakeDb);
        _ctx.Services.AddSingleton(_mockYouTube.Object);
        _ctx.Services.AddSingleton(_mockSync.Object);
        _ctx.Services.AddSingleton(_mockScheduling.Object);
        // Unused dependencies in page (Snackbar, Logger) are auto-provided by bUnit default service registrations.
    }

    private readonly FakeDatabaseService _fakeDb;
    private readonly Mock<IYouTubeService> _mockYouTube;
    private readonly Mock<ISyncService> _mockSync;
    private readonly Mock<ISchedulingService> _mockScheduling;

    [Fact]
    public async Task RendersExistingPlaylistCard()
    {
        // Arrange
        var pl = new Playlist
        {
            Name = "Test Playlist",
            YouTubeId = "pl123",
            YouTubeUrl = "https://www.youtube.com/playlist?list=pl123",
            Description = "Demo",
            Status = PlaylistStatus.Idle,
            CreatedAt = DateTime.UtcNow.AddMinutes(-5)
        };
        await _fakeDb.Playlists.CreateAsync(pl);

        // Act
        var cut = _ctx.RenderComponent<YMusicLite.Components.Pages.Playlists>();

        // Assert
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Test Playlist"));
        cut.Markup.Should().Contain("Demo");
        cut.Markup.Should().Contain("Sync");
    }

    [Fact]
    public async Task ClickingSyncInvokesSyncService()
    {
        var pl = new Playlist
        {
            Name = "Sync Playlist",
            YouTubeId = "pl456",
            YouTubeUrl = "https://www.youtube.com/playlist?list=pl456",
            Description = "Sync Desc",
            Status = PlaylistStatus.Idle
        };
        await _fakeDb.Playlists.CreateAsync(pl);

        var cut = _ctx.RenderComponent<YMusicLite.Components.Pages.Playlists>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Sync Playlist"));

        var syncButton = cut.FindAll("button").First(b => b.TextContent.Contains("Sync", StringComparison.OrdinalIgnoreCase));
        syncButton.Click();

        // Allow async event to run
        cut.WaitForAssertion(() => _mockSync.Invocations.Count.Should().BeGreaterThan(0));
        _mockSync.Invocations.First().Arguments[0].Should().Be(pl.Id.ToString());
    }

    [Fact]
    public async Task PlaylistInSyncShowsCancelButton()
    {
        var pl = new Playlist
        {
            Name = "Busy Playlist",
            YouTubeId = "plBusy",
            YouTubeUrl = "https://www.youtube.com/playlist?list=plBusy",
            Description = "Busy",
            Status = PlaylistStatus.Syncing
        };
        await _fakeDb.Playlists.CreateAsync(pl);

        var cut = _ctx.RenderComponent<YMusicLite.Components.Pages.Playlists>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Busy Playlist"));

        cut.Markup.Should().Contain("Cancel");
        cut.Markup.Should().NotContain("> Sync <", because: "Sync button replaced by Cancel when syncing");
    }

    [Fact]
    public void CreatingNewPlaylistValidatesUrl()
    {
        var cut = _ctx.RenderComponent<YMusicLite.Components.Pages.Playlists>();

        // Open dialog
        var addButtons = cut.FindAll("button").Where(b => b.TextContent.Contains("Add", StringComparison.OrdinalIgnoreCase)).ToList();
        addButtons.First().Click();

        // Fill name only, invalid URL
        var inputs = cut.FindAll("input");
        var nameInput = inputs.FirstOrDefault();
        nameInput!.Change("Invalid Playlist");
        // URL field is second input (approx) - leave blank or invalid
        var saveBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Create"));
        saveBtn.HasAttribute("disabled").Should().BeTrue("form invalid without required URL");
    }

    public void Dispose()
    {
        _ctx.Dispose();
    }

    // ----------------- In-memory fakes -----------------

    private class FakeDatabaseService : IDatabaseService
    {
        public FakeRepository<User> Users { get; } = new();
        public FakeRepository<Playlist> Playlists { get; } = new();
        public FakeRepository<Track> Tracks { get; } = new();
        public FakeRepository<SyncJob> SyncJobs { get; } = new();
        public FakeRepository<PkceSession> PkceSessions { get; } = new();
        IRepository<User> IDatabaseService.Users => Users;
        IRepository<Playlist> IDatabaseService.Playlists => Playlists;
        IRepository<Track> IDatabaseService.Tracks => Tracks;
        IRepository<SyncJob> IDatabaseService.SyncJobs => SyncJobs;
        IRepository<PkceSession> IDatabaseService.PkceSessions => PkceSessions;
        public void Initialize() { }
    }

    private class FakeRepository<T> : IRepository<T> where T : class
    {
        private readonly List<T> _items = new();
        private LiteDB.ObjectId GetOrAssignId(T entity)
        {
            var prop = typeof(T).GetProperty("Id");
            if (prop == null) return LiteDB.ObjectId.Empty;
            var val = prop.GetValue(entity);
            if (val is LiteDB.ObjectId oid && oid != LiteDB.ObjectId.Empty)
                return oid;
            var newId = LiteDB.ObjectId.NewObjectId();
            prop.SetValue(entity, newId);
            return newId;
        }

        public Task<LiteDB.ObjectId> InsertAsync(T entity)
        {
            var id = GetOrAssignId(entity);
            _items.Add(entity);
            return Task.FromResult(id);
        }

        public Task<LiteDB.ObjectId> CreateAsync(T entity) => InsertAsync(entity);
        public Task<bool> UpdateAsync(T entity) => Task.FromResult(true);

        public Task<bool> DeleteAsync(LiteDB.ObjectId id)
        {
            var prop = typeof(T).GetProperty("Id");
            var removed = _items.RemoveAll(x => prop?.GetValue(x)?.ToString() == id.ToString()) > 0;
            return Task.FromResult(removed);
        }

        public Task<bool> DeleteAsync(string id)
        {
            if (!LiteDB.ObjectId.TryParse(id, out var oid)) return Task.FromResult(false);
            return DeleteAsync(oid);
        }

        public Task<T?> GetByIdAsync(LiteDB.ObjectId id)
        {
            var prop = typeof(T).GetProperty("Id");
            var entity = _items.FirstOrDefault(x => prop?.GetValue(x)?.ToString() == id.ToString());
            return Task.FromResult(entity);
        }

        public Task<T?> GetByIdAsync(string id)
        {
            var prop = typeof(T).GetProperty("Id");
            var entity = _items.FirstOrDefault(x => prop?.GetValue(x)?.ToString() == id);
            return Task.FromResult(entity);
        }

        public Task<IEnumerable<T>> GetAllAsync() => Task.FromResult(_items.AsEnumerable());

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