using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using YMusicLite.Models;
using YMusicLite.Services;

namespace YMusicLite.Tests;

public class GoogleAuthServiceTest
{
    private static GoogleAuthService CreateService(string dataPath, IHttpClientFactory? httpFactory = null)
    {
        var inMemory = new Dictionary<string, string?>
        {
            {"DataPath", dataPath}
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemory).Build();
        var dbLogger = LoggerFactory.Create(b => { }).CreateLogger<DatabaseService>();
        var db = new DatabaseService(config, dbLogger);
        var logger = LoggerFactory.Create(b => { }).CreateLogger<GoogleAuthService>();
        IHttpClientFactory factory;
        if (httpFactory != null)
        {
            factory = httpFactory;
        }
        else
        {
            var m = new Mock<IHttpClientFactory>();
            m.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(new HttpClient());
            factory = m.Object;
        }
        var opts = Options.Create(new GoogleOAuthOptions { ClientId = "TEST_DESKTOP_CLIENT_ID" });
        return new GoogleAuthService(db, logger, config, factory, opts);
    }

    [Fact]
    public async Task AuthorizationUrl_ContainsClientIdAndOfflineAccess()
    {
        var service = CreateService("/tmp/ymusic-test-auth");
        var userId = Guid.NewGuid().ToString();
        var redirect = "http://127.0.0.1:43111/callback";
        var url = await service.GetAuthorizationUrlAsync(userId, redirect);
        Assert.Contains("client_id=TEST_DESKTOP_CLIENT_ID", url);
        Assert.Contains("access_type=offline", url);
    }

    [Fact]
    public async Task AuthorizationUrl_DoesNotDuplicateAccessType()
    {
        var service = CreateService("/tmp/ymusic-test-auth2");
        var userId = Guid.NewGuid().ToString();
        var redirect = "http://127.0.0.1:43111/callback";
        var url = await service.GetAuthorizationUrlAsync(userId, redirect);
        var occurrences = url.Split("access_type=").Length - 1;
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public async Task AuthorizationUrl_IncludesCodeChallenge()
    {
        var service = CreateService("/tmp/ymusic-test-auth3");
        var userId = Guid.NewGuid().ToString();
        var redirect = "http://127.0.0.1:43111/callback";
        var url = await service.GetAuthorizationUrlAsync(userId, redirect);
        Assert.Contains("code_challenge=", url);
        Assert.Contains("code_challenge_method=S256", url);
    }

    [Fact]
    public async Task AuthorizeAsync_WithoutPriorVerifier_Throws()
    {
        var service = CreateService("/tmp/ymusic-test-auth4");
        var userId = Guid.NewGuid().ToString();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AuthorizeAsync(userId, "dummy_code", "http://127.0.0.1:43111/callback"));
    }

    [Fact]
    public async Task ManualRefresh_UpdatesToken()
    {
        // Arrange DB + existing expired user
        var dataPath = "/tmp/ymusic-test-auth5";
        var inMemory = new Dictionary<string, string?> { { "DataPath", dataPath } };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemory).Build();
        var dbLogger = LoggerFactory.Create(b => { }).CreateLogger<DatabaseService>();
        var db = new DatabaseService(config, dbLogger);
        var logger = LoggerFactory.Create(b => { }).CreateLogger<GoogleAuthService>();

        var handler = new StubHandler();
        var httpClient = new HttpClient(handler);
        var httpFactoryMock = new Mock<IHttpClientFactory>();
        httpFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var opts = Options.Create(new GoogleOAuthOptions { ClientId = "TEST_DESKTOP_CLIENT_ID" });
        var service = new GoogleAuthService(db, logger, config, httpFactoryMock.Object, opts);

        var user = new User
        {
            GoogleId = "user-refresh",
            AccessToken = "OLD_TOKEN",
            RefreshToken = "REFRESH_TOKEN",
            TokenExpiry = DateTime.UtcNow.AddMinutes(-5),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        await db.Users.CreateAsync(user);

        await service.RefreshTokenAsync(user);

        Assert.Equal("NEW_TOKEN", user.AccessToken);
        Assert.True(user.TokenExpiry > DateTime.UtcNow);
    }

    private class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsoluteUri.Contains("oauth2.googleapis.com/token"))
            {
                var json = "{\"access_token\":\"NEW_TOKEN\",\"expires_in\":3600,\"token_type\":\"Bearer\"}";
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest));
        }
    }
}
