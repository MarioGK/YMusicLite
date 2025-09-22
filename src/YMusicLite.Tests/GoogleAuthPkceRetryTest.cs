using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using YMusicLite.Models;
using YMusicLite.Services;

namespace YMusicLite.Tests;

public class GoogleAuthPkceRetryTest
{
    // In-memory generic repository with real delete support
    private class InMemoryRepository<T> : IRepository<T> where T : class
    {
        private readonly List<T> _items = new();
        private readonly Func<T, string?> _idAccessor;

        public InMemoryRepository()
        {
            // Try to locate Id or State properties to help with deletions (best-effort)
            var idProp = typeof(T).GetProperty("Id");
            if (idProp != null)
            {
                _idAccessor = (obj) => idProp.GetValue(obj)?.ToString();
            }
            else
            {
                _idAccessor = _ => null;
            }
        }

        public Task<LiteDB.ObjectId> InsertAsync(T entity)
        {
            _items.Add(entity);
            return Task.FromResult(LiteDB.ObjectId.NewObjectId());
        }

        public Task<LiteDB.ObjectId> CreateAsync(T entity) => InsertAsync(entity);

        public Task<bool> UpdateAsync(T entity) => Task.FromResult(true);

        public Task<bool> DeleteAsync(LiteDB.ObjectId id)
        {
            var str = id.ToString();
            var removed = _items.RemoveAll(i => _idAccessor(i) == str) > 0;
            return Task.FromResult(removed);
        }

        public Task<bool> DeleteAsync(string id)
        {
            var removed = _items.RemoveAll(i => _idAccessor(i) == id) > 0;
            return Task.FromResult(removed);
        }

        public Task<T?> GetByIdAsync(LiteDB.ObjectId id)
            => Task.FromResult(_items.FirstOrDefault(i => _idAccessor(i) == id.ToString()));

        public Task<T?> GetByIdAsync(string id)
            => Task.FromResult(_items.FirstOrDefault(i => _idAccessor(i) == id));

        public Task<IEnumerable<T>> GetAllAsync() => Task.FromResult<IEnumerable<T>>(_items);

        public Task<List<T>> FindAllAsync(System.Linq.Expressions.Expression<Func<T, bool>> predicate)
        {
            var compiled = predicate.Compile();
            return Task.FromResult(_items.Where(compiled).ToList());
        }

        public Task<IEnumerable<T>> FindAsync(System.Linq.Expressions.Expression<Func<T, bool>> predicate)
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

    // In-memory DB implementing IDatabaseService
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

    private class CapturedRequest
    {
        public Uri Uri { get; set; } = default!;
        public string Body { get; set; } = string.Empty;
        public HttpStatusCode StatusCode { get; set; }
    }

    private class RetryTokenMockHandler : HttpMessageHandler
    {
        private readonly List<CapturedRequest> _requests;
        private int _attempt = 0;

        public RetryTokenMockHandler(List<CapturedRequest> requests)
        {
            _requests = requests;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content != null
                ? await request.Content.ReadAsStringAsync(cancellationToken)
                : string.Empty;

            if (request.RequestUri != null && request.RequestUri.AbsoluteUri.Contains("oauth2.googleapis.com/token"))
            {
                _attempt++;
                if (body.Contains("grant_type=authorization_code"))
                {
                    if (_attempt == 1)
                    {
                        _requests.Add(new CapturedRequest { Uri = request.RequestUri, Body = body, StatusCode = HttpStatusCode.InternalServerError });
                        return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                        {
                            Content = new StringContent("{\"error\":\"internal_error\"}", Encoding.UTF8, "application/json")
                        };
                    }
                    // Success on retry
                    _requests.Add(new CapturedRequest { Uri = request.RequestUri, Body = body, StatusCode = HttpStatusCode.OK });
                    var json = "{\"access_token\":\"RETRY_ACCESS\",\"refresh_token\":\"RETRY_REFRESH\",\"expires_in\":3600,\"token_type\":\"Bearer\"}";
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };
                }

                if (body.Contains("grant_type=refresh_token"))
                {
                    _requests.Add(new CapturedRequest { Uri = request.RequestUri, Body = body, StatusCode = HttpStatusCode.OK });
                    var json = "{\"access_token\":\"REFRESH_AFTER_RETRY\",\"expires_in\":3600,\"token_type\":\"Bearer\"}";
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };
                }
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task Token_Exchange_Failure_Retains_Session_And_Succeeds_On_Retry()
    {
        // Arrange
        var db = new InMemoryDb();
        var config = new ConfigurationBuilder().Build();
        var captured = new List<CapturedRequest>();
        var handler = new RetryTokenMockHandler(captured);
        var httpClient = new HttpClient(handler);

        var httpFactoryMock = new Mock<IHttpClientFactory>();
        httpFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(httpClient);

        // Channel fetcher returns deterministic channel
        var channelFetcherMock = new Mock<IGoogleChannelFetcher>();
        channelFetcherMock.Setup(cf => cf.GetChannelAsync(It.IsAny<Google.Apis.Auth.OAuth2.UserCredential>()))
            .ReturnsAsync(("CHANNEL_RETRY", "Retry Test Channel"));

        var opts = Options.Create(new GoogleOAuthOptions
        {
            ClientId = "TEST_CLIENT_ID_RETRY"
        });

        var service = new GoogleAuthService(
            db,
            new NullLogger<GoogleAuthService>(),
            config,
            httpFactoryMock.Object,
            opts,
            channelFetcherMock.Object);

        var userId = Guid.NewGuid().ToString();
        var redirect = "http://127.0.0.1:43111/callback";

        // Create PKCE session
        var authUrl = await service.GetAuthorizationUrlAsync(userId, redirect);
        Assert.Contains("code_challenge_method=S256", authUrl);

        // Act 1: First attempt (forced failure 500)
        var firstResult = await service.AuthorizeAsync(userId, "CODE_RETRY", redirect);
        Assert.Null(firstResult);

        // Session should still exist (retry safety)
        var sessionsAfterFail = await db.PkceSessions.FindAllAsync(p => p.State == userId);
        Assert.Single(sessionsAfterFail);

        // Act 2: Second attempt (success)
        var secondResult = await service.AuthorizeAsync(userId, "CODE_RETRY", redirect);
        Assert.NotNull(secondResult);
        Assert.Equal("CHANNEL_RETRY", secondResult!.GoogleId);
        Assert.Equal("Retry Test Channel", secondResult.DisplayName);
        Assert.Equal("RETRY_ACCESS", secondResult.AccessToken);

        // After success session deleted
        var sessionsAfterSuccess = await db.PkceSessions.FindAllAsync(p => p.State == userId);
        Assert.Empty(sessionsAfterSuccess);

        // Validate two authorization_code requests
        var authRequests = captured.Where(r => r.Body.Contains("grant_type=authorization_code")).ToList();
        Assert.Equal(2, authRequests.Count);

        foreach (var req in authRequests)
        {
            Assert.Contains("code=CODE_RETRY", req.Body);
            Assert.Contains("code_verifier=", req.Body);
            Assert.Contains("client_id=TEST_CLIENT_ID_RETRY", req.Body);
            Assert.DoesNotContain("client_secret", req.Body, StringComparison.OrdinalIgnoreCase);
        }
    }
}