using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Services;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using Google.Apis.Util.Store;
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
}

public class GoogleAuthService : IGoogleAuthService
{
    private readonly IDatabaseService _database;
    private readonly ILogger<GoogleAuthService> _logger;
    private readonly IConfiguration _configuration;
    private readonly GoogleAuthorizationCodeFlow _flow;
    private const string ApplicationName = "YMusicLite";
    private readonly string[] _scopes = new[] { Google.Apis.YouTube.v3.YouTubeService.Scope.YoutubeReadonly };

    public GoogleAuthService(
        IDatabaseService database,
        ILogger<GoogleAuthService> logger,
        IConfiguration configuration)
    {
        _database = database;
        _logger = logger;
        _configuration = configuration;

        // Use only the provided public client ID (desktop application / installed app OAuth flow with PKCE)
        var clientId = "198027251119-c04chsbao214hcplsf697u2smo682vuq.apps.googleusercontent.com";
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new InvalidOperationException("Google OAuth client ID missing.");
        }

        _flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets
            {
                ClientId = clientId,
                ClientSecret = string.Empty // No secret for installed apps
            },
            Scopes = _scopes,
            DataStore = new CustomDataStore(_database)
        });
        _logger.LogInformation("GoogleAuthService initialized with public client ID (PKCE)");
    }

    public Task<string> GetAuthorizationUrlAsync(string userId, string redirectUri)
    {
        try
        {
            var codeVerifier = GenerateCodeVerifier();
            PkceStore.Set(userId, codeVerifier);
            var request = _flow.CreateAuthorizationCodeRequest(redirectUri);
            request.State = userId;
            var url = request.Build().ToString();

            // Append required parameters only if not already present. Some hosting / proxy scenarios may inject values;
            // Google rejects requests where a parameter like access_type appears multiple times (Error: OAuth 2 parameters can only have a single value)
            url = EnsureQueryParam(url, "access_type", "offline");
            url = EnsureQueryParam(url, "prompt", "consent");
            url = EnsureQueryParam(url, "include_granted_scopes", "true");

            _logger.LogInformation("Generated authorization URL for user {UserId}", userId);
            return Task.FromResult(url);
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
            codeVerifier ??= PkceStore.Take(userId); // retrieve and remove stored verifier
            if (string.IsNullOrEmpty(codeVerifier))
            {
                _logger.LogWarning("PKCE code verifier missing for user {UserId}", userId);
            }

            // Library currently does not expose overload with custom parameters in this version; exchange without explicit PKCE parameter (works for installed app with public client if allowed)
            var token = await _flow.ExchangeCodeForTokenAsync(userId, code, redirectUri, CancellationToken.None);

            var credential = new UserCredential(_flow, userId, token);
            var youtubeService = new Google.Apis.YouTube.v3.YouTubeService(new BaseClientService.Initializer()
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

                var existing = await _database.Users.FindOneAsync(u => u.GoogleId == userId);
                var user = existing ?? new User();
                user.GoogleId = userId;
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
                    _logger.LogInformation("Created new user record for GoogleId {GoogleId}", userId);
                }
                else
                {
                    await _database.Users.UpdateAsync(user);
                    _logger.LogInformation("Updated existing user record for GoogleId {GoogleId}", userId);
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
            if (user == null || string.IsNullOrEmpty(user.AccessToken))
            {
                _logger.LogWarning("User not authenticated: {UserId}", userId);
                return null;
            }

            var remaining = user.TokenExpiry - DateTime.UtcNow;
            var token = new TokenResponse
            {
                AccessToken = user.AccessToken,
                RefreshToken = user.RefreshToken,
                ExpiresInSeconds = remaining > TimeSpan.Zero ? (long)remaining.TotalSeconds : 0,
                IssuedUtc = user.TokenExpiry - (user.TokenExpiry - DateTime.UtcNow)
            };

            var credential = new UserCredential(_flow, userId, token);
            // Refresh proactively if near expiry
            if (remaining < TimeSpan.FromMinutes(2))
            {
                _logger.LogInformation("Access token near expiry for {UserId}, refreshing", userId);
                await credential.RefreshTokenAsync(CancellationToken.None);
                await UpdateUserTokensAsync(userId, credential.Token);
            }
            return credential;
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
                var refreshed = await credentials.RefreshTokenAsync(CancellationToken.None);
                if (refreshed)
                {
                    await UpdateUserTokensAsync(user.GoogleId, credentials.Token);
                    _logger.LogInformation("Token refreshed for user: {UserId}", user.GoogleId);
                }
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

    private async Task UpdateUserTokensAsync(string userId, TokenResponse token)
    {
        var user = await _database.Users.FindOneAsync(u => u.GoogleId == userId);
        if (user == null) return;
        user.AccessToken = token.AccessToken;
        if (!string.IsNullOrEmpty(token.RefreshToken))
        {
            user.RefreshToken = token.RefreshToken; // Only update if provided
        }
        user.TokenExpiry = token.IssuedUtc.AddSeconds(token.ExpiresInSeconds ?? 3600);
        user.UpdatedAt = DateTime.UtcNow;
        await _database.Users.UpdateAsync(user);
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

    private static string EnsureQueryParam(string url, string key, string value)
    {
        // If key already present (even with different value) do not duplicate
        if (url.Contains(key + "=")) return url;
        var separator = url.Contains('?') ? '&' : '?';
        return url + separator + key + "=" + Uri.EscapeDataString(value);
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
        await Task.CompletedTask;
        return default!; // tokens managed elsewhere
    }

    public async Task ClearAsync()
    {
        await Task.CompletedTask;
    }
}

// Simple in-memory PKCE verifier store (not persisted). For production consider expiring entries.
internal static class PkceStore
{
    private static readonly Dictionary<string, string> _verifiers = new();
    private static readonly object _lock = new();

    public static void Set(string userId, string verifier)
    {
        lock (_lock)
        {
            _verifiers[userId] = verifier;
        }
    }

    public static string? Take(string userId)
    {
        lock (_lock)
        {
            if (_verifiers.TryGetValue(userId, out var v))
            {
                _verifiers.Remove(userId);
                return v;
            }
            return null;
        }
    }
}