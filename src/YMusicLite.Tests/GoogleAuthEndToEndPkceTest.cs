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

public class GoogleAuthEndToEndPkceTest
{
    // In-memory generic repository
    private class InMemoryRepository<T> : IRepository<T> where T : class
    {
        private readonly List<T> _items = new();
        public Task<LiteDB.ObjectId> InsertAsync(T entity) { _items.Add(entity); return Task.FromResult(LiteDB.ObjectId.NewObjectId()); }
        public Task<LiteDB.ObjectId> CreateAsync(T entity) => InsertAsync(entity);
        public Task<bool> UpdateAsync(T entity) => Task.FromResult(true);
        public Task<bool> DeleteAsync(LiteDB.ObjectId id) => Task.FromResult(true);
        public Task<bool> DeleteAsync(string id) => Task.FromResult(true);
        public Task<T?> GetByIdAsync(LiteDB.ObjectId id) => Task.FromResult(_items.FirstOrDefault());
        public Task<T?> GetByIdAsync(string id) => Task.FromResult(_items.FirstOrDefault());
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
    }

    private class TokenMockHandler : HttpMessageHandler
    {
        private readonly List<CapturedRequest> _requests;
        private int _authorizationCalls = 0;

        public TokenMockHandler(List<CapturedRequest> requests)
        {
            _requests = requests;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content != null ? await request.Content.ReadAsStringAsync(cancellationToken) : string.Empty;
            if (request.RequestUri != null && request.RequestUri.AbsoluteUri.Contains("oauth2.googleapis.com/token"))
            {
                _requests.Add(new CapturedRequest { Uri = request.RequestUri, Body = body });

                // First call = authorization_code exchange
                if (body.Contains("grant_type=authorization_code"))
                {
                    _authorizationCalls++;
                    var json = "{\"access_token\":\"ACCESS_TOKEN_E2E\",\"refresh_token\":\"REFRESH_TOKEN_E2E\",\"expires_in\":3600,\"token_type\":\"Bearer\"}";
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };
                }

                // Refresh token grant
                if (body.Contains("grant_type=refresh_token"))
                {
                    var json = "{\"access_token\":\"ACCESS_TOKEN_REFRESHED\",\"expires_in\":3600,\"token_type\":\"Bearer\"}";
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"error\":\"unexpected_request\"}", Encoding.UTF8, "application/json")
                };
            }

            // Any other call (not expected)
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task DesktopPkce_EndToEnd_Succeeds_With_Code_Exchange_And_Optional_Refresh()
    {
        // Arrange
        var db = new InMemoryDb();
        var config = new ConfigurationBuilder().Build();

        var captured = new List<CapturedRequest>();
        var handler = new TokenMockHandler(captured);
        var httpClient = new HttpClient(handler);

        var httpFactoryMock = new Mock<IHttpClientFactory>();
        httpFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(httpClient);

        // Mock channel fetcher to avoid hitting Google APIs
        var channelFetcherMock = new Mock<IGoogleChannelFetcher>();
        channelFetcherMock.Setup(cf => cf.GetChannelAsync(It.IsAny<Google.Apis.Auth.OAuth2.UserCredential>()))
            .ReturnsAsync(("CHANNEL_E2E", "E2E Test Channel"));

        var opts = Options.Create(new GoogleOAuthOptions
        {
            ClientId = "TEST_DESKTOP_CLIENT_ID"
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

        // Act 1: Generate authorization URL
        var authUrl = await service.GetAuthorizationUrlAsync(userId, redirect);

        // Assert URL content
        Assert.Contains("code_challenge=", authUrl);
        Assert.Contains("code_challenge_method=S256", authUrl);
        Assert.Contains(Uri.EscapeDataString(userId), authUrl);
        Assert.Contains("client_id=TEST_DESKTOP_CLIENT_ID", authUrl);

        // Act 2: Simulate redirect from Google with code
        var user = await service.AuthorizeAsync(userId, "CODE123", redirect);

        // Assert user and tokens
        Assert.NotNull(user);
        Assert.Equal("ACCESS_TOKEN_E2E", user!.AccessToken);
        Assert.Equal("REFRESH_TOKEN_E2E", user.RefreshToken);
        Assert.Equal("CHANNEL_E2E", user.GoogleId);
        Assert.Equal("E2E Test Channel", user.DisplayName);

        // Assert PKCE session removed
        var remainingSessions = await db.PkceSessions.FindAllAsync(p => p.State == userId);
        Assert.Empty(remainingSessions);

        // Assert captured authorization_code request correctness
        var authRequest = captured.FirstOrDefault(r => r.Body.Contains("grant_type=authorization_code"));
        Assert.NotNull(authRequest);
        Assert.Contains("code=CODE123", authRequest!.Body);
        Assert.Contains("code_verifier=", authRequest.Body);
        Assert.Contains("client_id=TEST_DESKTOP_CLIENT_ID", authRequest.Body);
        Assert.Contains("redirect_uri=", authRequest.Body);
        Assert.DoesNotContain("client_secret", authRequest.Body, StringComparison.OrdinalIgnoreCase);

        // Optional refresh scenario
        // Force token near expiry to trigger refresh path in GetCredentialsAsync
        user.TokenExpiry = DateTime.UtcNow.AddSeconds(30); // < 2 minutes -> triggers refresh
        await db.Users.UpdateAsync(user);

        var credential = await service.GetCredentialsAsync(user.GoogleId);
        Assert.NotNull(credential);

        // After refresh, user should have refreshed access token
        var updated = await db.Users.FindOneAsync(u => u.GoogleId == user.GoogleId);
        Assert.NotNull(updated);
        Assert.Equal("ACCESS_TOKEN_REFRESHED", updated!.AccessToken);

        var refreshRequest = captured.FirstOrDefault(r => r.Body.Contains("grant_type=refresh_token"));
        Assert.NotNull(refreshRequest);
        Assert.Contains("refresh_token=REFRESH_TOKEN_E2E", refreshRequest!.Body);
        Assert.DoesNotContain("code_verifier", refreshRequest.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("client_secret", refreshRequest.Body, StringComparison.OrdinalIgnoreCase);
    }
}