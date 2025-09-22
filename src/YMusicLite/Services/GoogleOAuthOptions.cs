namespace YMusicLite.Services
{
    /// <summary>
    /// Google OAuth configuration.
    /// For pure desktop/installed PKCE apps Google does NOT require a client secret.
    /// However if you have created a "Web application" OAuth client, Google will reject
    /// the authorization_code and refresh_token grants without the client_secret.
    /// Provide ClientSecret ONLY if your OAuth client type requires it.
    /// </summary>
    public sealed class GoogleOAuthOptions
    {
        public string ClientId { get; set; } = string.Empty;

        /// <summary>
        /// Optional client secret (only for Web application OAuth client types). Leave empty for Installed/Desktop client.
        /// </summary>
        public string? ClientSecret { get; set; } = string.Empty;
    }
}