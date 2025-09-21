using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using YMusicLite.Models;

namespace YMusicLite.Services;

public interface IGoogleAuthService
{
    Task<string> GetAuthorizationUrlAsync(string userId, string redirectUri);
    Task<User?> AuthorizeAsync(string userId, string code, string redirectUri, string? codeVerifier = null);
    Task<UserCredential?> GetCredentialsAsync(string userId);
    Task RefreshTokenAsync(User user);
    Task RevokeTokenAsync(string userId);
    Task<IReadOnlyList<Google.Apis.YouTube.v3.Data.Playlist>> GetPrivatePlaylistsAsync(string userId, int maxResults = 25);
    Task<IReadOnlyList<User>> ListAuthenticatedUsersAsync();
}

public class GoogleAuthService : IGoogleAuthService
{
    private readonly IDatabaseService _database;
    private readonly ILogger<GoogleAuthService> _logger;
    private readonly IConfiguration _configuration;
    private readonly GoogleAuthorizationCodeFlow _flow;
    private readonly HttpClient _httpClient;
    private readonly string _clientId;
    private const string ApplicationName = "YMusicLite";
    private readonly string[] _scopes = new[] { Google.Apis.YouTube.v3.YouTubeService.Scope.YoutubeReadonly };

    // OAuth endpoints (centralized)
    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";

    public GoogleAuthService(
        IDatabaseService database,
        ILogger<GoogleAuthService> logger,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        IOptions<GoogleOAuthOptions> oauthOptions)
    {
        _database = database;
        _logger = logger;
        _configuration = configuration;
        _httpClient = httpClientFactory.CreateClient();
        _clientId = oauthOptions.Value.ClientId?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(_clientId))
        {
            throw new InvalidOperationException("Google OAuth client ID missing (GoogleOAuth:ClientId).");
        }

        // Flow is still used to build UserCredential instances (no secret required for PKCE desktop apps)
        _flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets
            {
                ClientId = _clientId
            },
            Scopes = _scopes,
            DataStore = new CustomDataStore(_database)
        });

        _logger.LogInformation("GoogleAuthService initialized (PKCE desktop) with client id length {Length}", _clientId.Length);
    }

    public async Task<string> GetAuthorizationUrlAsync(string userId, string redirectUri)
    {
        try
        {
            // Persist PKCE code verifier
            var codeVerifier = GenerateCodeVerifier();
            var existing = (await _database.PkceSessions.FindAllAsync(p => p.State == userId)).FirstOrDefault();
            if (existing != null)
            {
                existing.CodeVerifier = codeVerifier;
                existing.CreatedAt = DateTime.UtcNow;
                existing.ExpiresAt = DateTime.UtcNow.AddMinutes(15);
                await _database.PkceSessions.UpdateAsync(existing);
            }
            else
            {
                await _database.PkceSessions.CreateAsync(new PkceSession
                {
                    State = userId,
                    CodeVerifier = codeVerifier,
                    CreatedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddMinutes(15)
                });
            }

            var scope = Uri.EscapeDataString(string.Join(' ', _scopes));
            var codeChallenge = GenerateCodeChallenge(codeVerifier);
            var url = $"{AuthEndpoint}?response_type=code" +
                      $"&client_id={_clientId}" +
                      $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                      $"&scope={scope}" +
                      $"&state={Uri.EscapeDataString(userId)}" +
                      "&access_type=offline&prompt=consent&include_granted_scopes=true" +
                      $"&code_challenge={codeChallenge}&code_challenge_method=S256";
            _logger.LogInformation("Generated authorization URL for user {UserId}", userId);
            return url;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create authorization URL for user: {UserId}", userId);
            throw;
        }
    }

    public async Task<User?> AuthorizeAsync(string userId, string code, string redirectUri, string? codeVerifier = null)
    {
        try
        {
            if (string.IsNullOrEmpty(codeVerifier))
            {
                var session = (await _database.PkceSessions.FindAllAsync(p => p.State == userId)).FirstOrDefault();
                if (session != null && session.ExpiresAt > DateTime.UtcNow)
                {
                    codeVerifier = session.CodeVerifier;
                    await _database.PkceSessions.DeleteAsync(session.Id);
                }
            }

            if (string.IsNullOrWhiteSpace(codeVerifier))
            {
                _logger.LogWarning("Missing PKCE code_verifier for user {UserId}", userId);
                throw new InvalidOperationException("PKCE code_verifier missing");
            }

            var form = new Dictionary<string, string>
            {
                {"client_id", _clientId},
                {"code", code},
                {"code_verifier", codeVerifier},
                {"grant_type", "authorization_code"},
                {"redirect_uri", redirectUri}
            };

            var response = await _httpClient.PostAsync(TokenEndpoint, new FormUrlEncodedContent(form));
            var content = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                // Log minimal error information, NOT the whole response body if it contains tokens (here it doesn't on failure)
                _logger.LogWarning("Token exchange failed Status:{Status} BodyLength:{Len}", response.StatusCode, content.Length);
                return null;
            }

            var json = System.Text.Json.JsonDocument.Parse(content).RootElement;
            var token = new TokenResponse
            {
                AccessToken = json.GetProperty("access_token").GetString()!,
                RefreshToken = json.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : string.Empty,
                ExpiresInSeconds = json.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600,
                IssuedUtc = DateTime.UtcNow
            };
            _logger.LogInformation("Token exchange succeeded for user {UserId}", userId);

            var credential = new UserCredential(_flow, userId, token);
            var youtubeService = new Google.Apis.YouTube.v3.YouTubeService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = ApplicationName
            });

            var channelsRequest = youtubeService.Channels.List("snippet");
            channelsRequest.Mine = true;
            var channelsResponse = await channelsRequest.ExecuteAsync();

            if (channelsResponse.Items?.Count > 0)
            {
                var channel = channelsResponse.Items.First();
                var channelId = channel.Id ?? userId;
                var existing = await _database.Users.FindOneAsync(u => u.GoogleId == channelId);
                var user = existing ?? new User();
                user.GoogleId = channelId;
                user.DisplayName = channel.Snippet.Title ?? "Unknown";
                user.AccessToken = token.AccessToken;
                user.RefreshToken = token.RefreshToken ?? existing?.RefreshToken ?? string.Empty;
                user.TokenExpiry = token.IssuedUtc.AddSeconds(token.ExpiresInSeconds ?? 3600);
                user.Scopes = string.Join(' ', _scopes);
                user.UpdatedAt = DateTime.UtcNow;

                if (existing == null)
                {
                    user.CreatedAt = DateTime.UtcNow;
                    await _database.Users.CreateAsync(user);
                    _logger.LogInformation("Created new user record {GoogleId}", user.GoogleId);
                }
                else
                {
                    await _database.Users.UpdateAsync(user);
                    _logger.LogInformation("Updated user record {GoogleId}", user.GoogleId);
                }

                _logger.LogInformation("User authenticated: {DisplayName}", user.DisplayName);
                return user;
            }

            _logger.LogWarning("No channel found for user {UserId}", userId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to authorize user: {UserId}", userId);
            if (ex is InvalidOperationException) throw;
            return null;
        }
    }

    public async Task<UserCredential?> GetCredentialsAsync(string userId)
    {
        try
        {
            var user = await _database.Users.FindOneAsync(u => u.GoogleId == userId);
            if (user == null || string.IsNullOrEmpty(user.AccessToken))
            {
                _logger.LogWarning("User not authenticated: {UserId}", userId);
                return null;
            }

            var remaining = user.TokenExpiry - DateTime.UtcNow;
            if (remaining < TimeSpan.FromMinutes(2))
            {
                _logger.LogInformation("Access token near expiry for {UserId}, performing manual refresh", userId);
                await ManualRefreshAsync(user);
                user = await _database.Users.FindOneAsync(u => u.GoogleId == userId) ?? user;
                remaining = user.TokenExpiry - DateTime.UtcNow;
            }

            var approxIssued = DateTime.UtcNow - (user.TokenExpiry - DateTime.UtcNow);
            var token = new TokenResponse
            {
                AccessToken = user.AccessToken,
                RefreshToken = user.RefreshToken,
                ExpiresInSeconds = remaining > TimeSpan.Zero ? (long)remaining.TotalSeconds : 0,
                IssuedUtc = approxIssued
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
            await ManualRefreshAsync(user);
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
                    user.Scopes = string.Empty;
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

    public async Task<IReadOnlyList<Google.Apis.YouTube.v3.Data.Playlist>> GetPrivatePlaylistsAsync(string userId, int maxResults = 25)
    {
        var credentials = await GetCredentialsAsync(userId);
        if (credentials == null)
        {
            _logger.LogWarning("Cannot get private playlists: user {UserId} not authenticated", userId);
            return Array.Empty<Google.Apis.YouTube.v3.Data.Playlist>();
        }

        var service = new Google.Apis.YouTube.v3.YouTubeService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credentials,
            ApplicationName = ApplicationName
        });

        var request = service.Playlists.List("snippet,contentDetails");
        request.Mine = true;
        request.MaxResults = maxResults;
        var response = await request.ExecuteAsync();
        _logger.LogInformation("Retrieved {Count} private playlists for user {UserId}", response.Items?.Count ?? 0, userId);
        var list = response.Items ?? new List<Google.Apis.YouTube.v3.Data.Playlist>();
        return (IReadOnlyList<Google.Apis.YouTube.v3.Data.Playlist>)list.ToList();
    }

    private async Task ManualRefreshAsync(User user)
    {
        if (string.IsNullOrEmpty(user.RefreshToken))
        {
            _logger.LogWarning("Cannot refresh token: no refresh token stored for {UserId}", user.GoogleId);
            return;
        }

        var form = new Dictionary<string, string>
        {
            {"client_id", _clientId},
            {"grant_type", "refresh_token"},
            {"refresh_token", user.RefreshToken}
        };

        var response = await _httpClient.PostAsync(TokenEndpoint, new FormUrlEncodedContent(form));
        var content = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Manual refresh failed for {UserId} Status:{Status} BodyLength:{Len}", user.GoogleId, response.StatusCode, content.Length);
            return;
        }

        try
        {
            var json = System.Text.Json.JsonDocument.Parse(content).RootElement;
            var accessToken = json.GetProperty("access_token").GetString();
            if (string.IsNullOrEmpty(accessToken))
            {
                _logger.LogError("Manual refresh response missing access_token for {UserId}", user.GoogleId);
                return;
            }
            var expires = json.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;

            user.AccessToken = accessToken;
            user.TokenExpiry = DateTime.UtcNow.AddSeconds(expires);
            user.UpdatedAt = DateTime.UtcNow;
            await _database.Users.UpdateAsync(user);
            _logger.LogInformation("Access token refreshed for user {UserId}", user.GoogleId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed parsing refresh response for {UserId}", user.GoogleId);
        }
    }

    private static string GenerateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(64);
        return Base64UrlEncode(bytes);
    }

    private static string GenerateCodeChallenge(string codeVerifier)
    {
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.ASCII.GetBytes(codeVerifier));
        return Base64UrlEncode(bytes);
    }

    private static string Base64UrlEncode(byte[] input)
    {
        return Convert.ToBase64String(input)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public async Task<IReadOnlyList<User>> ListAuthenticatedUsersAsync()
    {
        var users = await _database.Users.GetAllAsync();
        return users.Where(u => !string.IsNullOrEmpty(u.AccessToken) && u.TokenExpiry > DateTime.UtcNow.AddMinutes(-5))
                    .OrderByDescending(u => u.UpdatedAt)
                    .ToList();
    }

    private async Task CleanupExpiredPkceAsync()
    {
        var expired = await _database.PkceSessions.FindAllAsync(s => s.ExpiresAt < DateTime.UtcNow.AddMinutes(-1));
        foreach (var s in expired)
        {
            await _database.PkceSessions.DeleteAsync(s.Id);
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
        await Task.CompletedTask;
    }

    public async Task DeleteAsync<T>(string key)
    {
        await Task.CompletedTask;
    }

    public async Task<T> GetAsync<T>(string key)
    {
        await Task.CompletedTask;
        return default!;
    }

    public async Task ClearAsync()
    {
        await Task.CompletedTask;
    }
}
// End GoogleAuthService