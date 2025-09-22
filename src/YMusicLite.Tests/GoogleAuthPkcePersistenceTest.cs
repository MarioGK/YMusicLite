using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using YMusicLite.Models;
using YMusicLite.Services;

namespace YMusicLite.Tests;

public class GoogleAuthPkcePersistenceTest
{
    private class InMemoryRepository<T> : IRepository<T> where T : class
    {
        private readonly System.Collections.Generic.List<T> _items = new();
        public Task<LiteDB.ObjectId> InsertAsync(T entity) { _items.Add(entity); return Task.FromResult(LiteDB.ObjectId.NewObjectId()); }
        public Task<LiteDB.ObjectId> CreateAsync(T entity) => InsertAsync(entity);
        public Task<bool> UpdateAsync(T entity) => Task.FromResult(true);
        public Task<bool> DeleteAsync(LiteDB.ObjectId id) => Task.FromResult(true);
        public Task<bool> DeleteAsync(string id) => Task.FromResult(true);
        public Task<T?> GetByIdAsync(LiteDB.ObjectId id) => Task.FromResult(_items.FirstOrDefault());
        public Task<T?> GetByIdAsync(string id) => Task.FromResult(_items.FirstOrDefault());
        public Task<System.Collections.Generic.IEnumerable<T>> GetAllAsync() => Task.FromResult((System.Collections.Generic.IEnumerable<T>)_items);
        public Task<System.Collections.Generic.List<T>> FindAllAsync(System.Linq.Expressions.Expression<Func<T, bool>> predicate)
        {
            var compiled = predicate.Compile();
            return Task.FromResult(_items.Where(compiled).ToList());
        }
        public Task<System.Collections.Generic.IEnumerable<T>> FindAsync(System.Linq.Expressions.Expression<Func<T, bool>> predicate)
        {
            var compiled = predicate.Compile();
            return Task.FromResult(_items.Where(compiled));
        }
        public Task<T?> FindOneAsync(System.Linq.Expressions.Expression<Func<T, bool>> predicate)
        {
            var compiled = predicate.Compile();
            return Task.FromResult(_items.FirstOrDefault(compiled));
        }
    }

    private class InMemoryDb : IDatabaseService
    {
        public InMemoryDb()
        {
            Users = new InMemoryRepository<User>();
            Playlists = new InMemoryRepository<Playlist>();
            Tracks = new InMemoryRepository<Track>();
            SyncJobs = new InMemoryRepository<SyncJob>();
            PkceSessions = new InMemoryRepository<PkceSession>();
        }
        public IRepository<User> Users { get; }
        public IRepository<Playlist> Playlists { get; }
        public IRepository<Track> Tracks { get; }
        public IRepository<SyncJob> SyncJobs { get; }
        public IRepository<PkceSession> PkceSessions { get; }
        public void Initialize() { }
        public void Dispose() { }
    }

    private static GoogleAuthService CreateService(InMemoryDb db, IConfiguration config, IHttpClientFactory httpFactory)
    {
        var opts = Options.Create(new GoogleOAuthOptions { ClientId = "TEST_DESKTOP_CLIENT_ID" });
        return new GoogleAuthService(db, new NullLogger<GoogleAuthService>(), config, httpFactory, opts);
    }

    [Fact]
    public async Task PkceSession_Persists_Across_Service_Recreation()
    {
        var db = new InMemoryDb();
        var config = new ConfigurationBuilder().Build();
        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(new System.Net.Http.HttpClient(new FakeHandler()));

        var redirect = "http://127.0.0.1:43111/callback";

        var service1 = CreateService(db, config, httpFactory.Object);
        var state = Guid.NewGuid().ToString();
        var url = await service1.GetAuthorizationUrlAsync(state, redirect);
        Assert.Contains("code_challenge=", url);

        // Simulate new scope instance (like page refresh) using same db
        var service2 = CreateService(db, config, httpFactory.Object);
        // Provide fake auth code causing token endpoint to return error; ensures verifier retrieval path executed
        var user = await service2.AuthorizeAsync(state, "fake_code", redirect);
        Assert.Null(user);
        // PKCE session should remain (retry safety) after failed token exchange
        var remaining = await db.PkceSessions.FindAllAsync(p => p.State == state);
        Assert.NotEmpty(remaining);
    }

    private class FakeHandler : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
        {
            var response = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
            {
                Content = new System.Net.Http.StringContent("{\"error\":\"invalid_request\",\"error_description\":\"invalid code.\"}")
            };
            return Task.FromResult(response);
        }
    }
}
