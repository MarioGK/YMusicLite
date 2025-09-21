using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using YMusicLite.Services;
using YMusicLite.Models;
using LiteDB;

namespace YMusicLite.Tests;

public class GoogleAuthServiceTest
{
    [Fact]
    public async Task AuthorizationUrl_ContainsClientIdAndOfflineAccess()
    {
        var inMemory = new Dictionary<string,string?>
        {
            // No client secret needed now
            {"DataPath", "/tmp/ymusic-test-auth"}
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemory).Build();
        var dbLogger = LoggerFactory.Create(b=>b.AddConsole()).CreateLogger<DatabaseService>();
        var db = new DatabaseService(config, dbLogger);
        var logger = LoggerFactory.Create(b=>b.AddConsole()).CreateLogger<GoogleAuthService>();
        var service = new GoogleAuthService(db, logger, config);
        var userId = Guid.NewGuid().ToString();
        var redirect = "http://localhost/auth";
        var url = await service.GetAuthorizationUrlAsync(userId, redirect);
        Assert.Contains("client_id=198027251119-c04chsbao214hcplsf697u2smo682vuq.apps.googleusercontent.com", url);
        Assert.Contains("access_type=offline", url);
    }

    [Fact]
    public async Task AuthorizationUrl_DoesNotDuplicateAccessType()
    {
        var inMemory = new Dictionary<string,string?>
        {
            {"DataPath", "/tmp/ymusic-test-auth2"}
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(inMemory).Build();
        var dbLogger = LoggerFactory.Create(b=>b.AddConsole()).CreateLogger<DatabaseService>();
        var db = new DatabaseService(config, dbLogger);
        var logger = LoggerFactory.Create(b=>b.AddConsole()).CreateLogger<GoogleAuthService>();
        var service = new GoogleAuthService(db, logger, config);
        var userId = Guid.NewGuid().ToString();
        var redirect = "http://localhost/auth";
        var url = await service.GetAuthorizationUrlAsync(userId, redirect);
        var occurrences = url.Split("access_type=").Length - 1; // naive count
        Assert.Equal(1, occurrences);
    }
}
