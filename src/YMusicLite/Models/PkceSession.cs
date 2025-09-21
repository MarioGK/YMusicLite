using LiteDB;

namespace YMusicLite.Models;

/// <summary>
/// Stores a short-lived PKCE code verifier associated with a temporary user/state identifier.
/// Persisting this allows the app to survive restarts between authorization redirect and token exchange.
/// </summary>
public class PkceSession
{
    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.NewObjectId();

    /// <summary>
    /// Correlates to the state parameter (we reuse userId passed to GetAuthorizationUrlAsync).
    /// </summary>
    public string State { get; set; } = string.Empty;

    /// <summary>
    /// The raw PKCE code verifier (not hashed). Stored briefly until token exchange then deleted.
    /// </summary>
    public string CodeVerifier { get; set; } = string.Empty;

    /// <summary>
    /// When the session was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Expiration time (default 15 minutes) after which the verifier is discarded.
    /// </summary>
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddMinutes(15);
}
