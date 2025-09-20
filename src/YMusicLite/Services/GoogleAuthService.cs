using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Services;
using Google.Apis.YouTube.v3;
using Google.Apis.Util.Store;
using YMusicLite.Models;

namespace YMusicLite.Services;

public interface IGoogleAuthService
{
    Task<string> GetAuthorizationUrlAsync(string userId, string redirectUri);
    Task<User?> AuthorizeAsync(string userId, string code, string redirectUri);
    Task<UserCredential?> GetCredentialsAsync(string userId);
    Task RefreshTokenAsync(User user);
    Task RevokeTokenAsync(string userId);
}

public class GoogleAuthService : IGoogleAuthService
{
    private readonly IDatabaseService _database;
    private readonly ILogger<GoogleAuthService> _logger;
    private readonly IConfiguration _configuration;
    private readonly GoogleAuthorizationCodeFlow _flow;

    public GoogleAuthService(
        IDatabaseService database,
        ILogger<GoogleAuthService> logger,
        IConfiguration configuration)
    {
        _database = database;
        _logger = logger;
        _configuration = configuration;

        var clientId = _configuration["Google:ClientId"];
        var clientSecret = _configuration["Google:ClientSecret"];

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
        {
            throw new InvalidOperationException("Google OAuth credentials not configured. Please set Google:ClientId and Google:ClientSecret in configuration.");
        }

        _flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets
            {
                ClientId = clientId,
                ClientSecret = clientSecret
            },
            Scopes = new[] { Google.Apis.YouTube.v3.YouTubeService.Scope.YoutubeReadonly },
            DataStore = new CustomDataStore(_database)
        });
    }

    public async Task<string> GetAuthorizationUrlAsync(string userId, string redirectUri)
    {
        try
        {
            var request = _flow.CreateAuthorizationCodeRequest(redirectUri);
            request.State = userId; // Use userId as state parameter
            return request.Build().ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create authorization URL for user: {UserId}", userId);
            throw;
        }
    }

    public async Task<User?> AuthorizeAsync(string userId, string code, string redirectUri)
    {
        try
        {
            var token = await _flow.ExchangeCodeForTokenAsync(userId, code, redirectUri, CancellationToken.None);
            
            // Get user info from Google
            var credential = new UserCredential(_flow, userId, token);
            var youtubeService = new Google.Apis.YouTube.v3.YouTubeService(new BaseClientService.Initializer()
            {
                HttpClientInitializer = credential,
                ApplicationName = "YMusicLite"
            });

            // Get channel info to extract user details
            var channelsRequest = youtubeService.Channels.List("snippet");
            channelsRequest.Mine = true;
            var channelsResponse = await channelsRequest.ExecuteAsync();
            
            if (channelsResponse.Items?.Count > 0)
            {
                var channel = channelsResponse.Items.First();
                
                var user = await _database.Users.GetByIdAsync(userId) ?? new User();
                user.GoogleId = userId;
                user.DisplayName = channel.Snippet.Title;
                user.AccessToken = token.AccessToken;
                user.RefreshToken = token.RefreshToken;
                user.TokenExpiry = token.IssuedUtc.AddSeconds(token.ExpiresInSeconds ?? 3600);
                user.UpdatedAt = DateTime.UtcNow;
                
                if (string.IsNullOrEmpty(user.Id.ToString()) || user.Id == LiteDB.ObjectId.Empty)
                {
                    user.CreatedAt = DateTime.UtcNow;
                    await _database.Users.CreateAsync(user);
                }
                else
                {
                    await _database.Users.UpdateAsync(user);
                }

                _logger.LogInformation("User authenticated successfully: {DisplayName}", user.DisplayName);
                return user;
            }

            _logger.LogWarning("No channel found for user: {UserId}", userId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to authorize user: {UserId}", userId);
            return null;
        }
    }

    public async Task<UserCredential?> GetCredentialsAsync(string userId)
    {
        try
        {
            var user = await _database.Users.FindOneAsync(u => u.GoogleId == userId);
            if (user == null)
            {
                _logger.LogWarning("User not found: {UserId}", userId);
                return null;
            }

            var token = new TokenResponse
            {
                AccessToken = user.AccessToken,
                RefreshToken = user.RefreshToken,
                ExpiresInSeconds = (long)(user.TokenExpiry - DateTime.UtcNow).TotalSeconds,
                IssuedUtc = user.TokenExpiry.AddSeconds(-(user.TokenExpiry - DateTime.UtcNow).TotalSeconds)
            };

            return new UserCredential(_flow, userId, token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get credentials for user: {UserId}", userId);
            return null;
        }
    }

    public async Task RefreshTokenAsync(User user)
    {
        try
        {
            var credentials = await GetCredentialsAsync(user.GoogleId);
            if (credentials?.Token != null)
            {
                await credentials.RefreshTokenAsync(CancellationToken.None);
                
                user.AccessToken = credentials.Token.AccessToken;
                user.RefreshToken = credentials.Token.RefreshToken;
                user.TokenExpiry = credentials.Token.IssuedUtc.AddSeconds(credentials.Token.ExpiresInSeconds ?? 3600);
                user.UpdatedAt = DateTime.UtcNow;
                
                await _database.Users.UpdateAsync(user);
                
                _logger.LogInformation("Token refreshed for user: {UserId}", user.GoogleId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh token for user: {UserId}", user.GoogleId);
            throw;
        }
    }

    public async Task RevokeTokenAsync(string userId)
    {
        try
        {
            var credentials = await GetCredentialsAsync(userId);
            if (credentials != null)
            {
                await credentials.RevokeTokenAsync(CancellationToken.None);
                
                var user = await _database.Users.FindOneAsync(u => u.GoogleId == userId);
                if (user != null)
                {
                    user.AccessToken = string.Empty;
                    user.RefreshToken = string.Empty;
                    user.TokenExpiry = DateTime.MinValue;
                    user.UpdatedAt = DateTime.UtcNow;
                    
                    await _database.Users.UpdateAsync(user);
                }
                
                _logger.LogInformation("Token revoked for user: {UserId}", userId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to revoke token for user: {UserId}", userId);
            throw;
        }
    }
}

// Custom data store implementation for LiteDB
public class CustomDataStore : IDataStore
{
    private readonly IDatabaseService _database;

    public CustomDataStore(IDatabaseService database)
    {
        _database = database;
    }

    public async Task StoreAsync<T>(string key, T value)
    {
        // Store OAuth tokens in user records instead of separate token store
        await Task.CompletedTask;
    }

    public async Task DeleteAsync<T>(string key)
    {
        // Handle token deletion
        await Task.CompletedTask;
    }

    public async Task<T> GetAsync<T>(string key)
    {
        // Return default for now - tokens are managed through User entities
        await Task.CompletedTask;
        return default(T);
    }

    public async Task ClearAsync()
    {
        await Task.CompletedTask;
    }
}