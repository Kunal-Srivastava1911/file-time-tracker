using System.Text.Json.Serialization;

namespace FileTimeTracker.Core.Storage;

/// <summary>
/// Dropbox OAuth material for a scoped app-folder app (design.md §4.3). Only the long-lived
/// refresh token is stored; short-lived access tokens are obtained on demand by the SDK.
/// </summary>
/// <remarks>
/// <see cref="AppSecret"/> is optional on purpose. A PKCE ("public") Dropbox app needs no secret,
/// which removes the embedded-credential half of the §4.3 security caveat — prefer it. A
/// confidential app still works for the aggregator, which runs on one admin machine.
/// </remarks>
public sealed class DropboxCredentials
{
    [JsonPropertyName("app_key")]
    public required string AppKey { get; init; }

    [JsonPropertyName("app_secret")]
    public string? AppSecret { get; init; }

    [JsonPropertyName("refresh_token")]
    public required string RefreshToken { get; init; }

    public bool IsPkce => string.IsNullOrWhiteSpace(AppSecret);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(AppKey))
        {
            throw new InvalidOperationException("Dropbox credentials are missing app_key.");
        }

        if (string.IsNullOrWhiteSpace(RefreshToken))
        {
            throw new InvalidOperationException("Dropbox credentials are missing refresh_token.");
        }
    }
}
