using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReportingPlatform.ExcelProvider.Config;

namespace ReportingPlatform.ExcelProvider.Grpc;

/// <summary>
/// Fetches and caches a platform JWT from the provider token endpoint.
/// Automatically refreshes when the cached token has less than 80 seconds remaining.
/// Thread-safe via <see cref="SemaphoreSlim"/>.
/// </summary>
public sealed class TokenService
{
    private readonly HttpClient _http;
    private readonly ProviderOptions _opts;
    private readonly ILogger<TokenService> _logger;

    private readonly SemaphoreSlim _lock = new(1, 1);

    private string?        _cachedToken;
    private DateTimeOffset _issuedAt;
    private int            _expiresIn; // seconds

    // Refresh 80 seconds before expiry (proto comment: "refresh at jwt.exp - 80s")
    private const int RefreshLeadSeconds = 80;

    public TokenService(
        HttpClient http,
        IOptions<ProviderOptions> opts,
        ILogger<TokenService> logger)
    {
        _http   = http;
        _opts   = opts.Value;
        _logger = logger;
    }

    /// <summary>Returns a fresh (or cached) access token.</summary>
    public async Task<string> GetTokenAsync(CancellationToken ct = default)
    {
        // Fast path — no lock needed if still valid
        if (_cachedToken is not null
            && DateTimeOffset.UtcNow < _issuedAt.AddSeconds(_expiresIn - RefreshLeadSeconds))
        {
            return _cachedToken;
        }

        await _lock.WaitAsync(ct);
        try
        {
            // Re-check inside lock
            if (_cachedToken is not null
                && DateTimeOffset.UtcNow < _issuedAt.AddSeconds(_expiresIn - RefreshLeadSeconds))
            {
                return _cachedToken;
            }

            return await FetchTokenAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Forces a fresh token acquisition, bypassing the cache.</summary>
    public async Task<string> AcquireFreshTokenAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try { return await FetchTokenAsync(ct); }
        finally { _lock.Release(); }
    }

    private async Task<string> FetchTokenAsync(CancellationToken ct)
    {
        _logger.LogInformation("Fetching platform token from {Endpoint}", _opts.TokenEndpoint);

        var request = new TokenRequest
        {
            ClientId     = _opts.ClientId,
            ClientSecret = _opts.ClientSecret,
            GrantType    = "client_credentials",
        };

        var response = await _http.PostAsJsonAsync(
            _opts.TokenEndpoint, request, TokenJsonContext.Default.TokenRequest, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("Token endpoint returned {Status}: {Body}", response.StatusCode, body);
            throw new HttpRequestException(
                $"Token endpoint {_opts.TokenEndpoint} returned {(int)response.StatusCode}: {body}");
        }

        var tokenResponse = await response.Content.ReadFromJsonAsync(
            TokenJsonContext.Default.TokenResponse, ct)
            ?? throw new InvalidOperationException("Token endpoint returned null body");

        _cachedToken = tokenResponse.AccessToken;
        _issuedAt    = DateTimeOffset.UtcNow;
        _expiresIn   = tokenResponse.ExpiresIn;

        _logger.LogInformation("Token acquired, expires in {ExpiresIn}s", _expiresIn);
        return _cachedToken;
    }
}

// ─── DTOs ─────────────────────────────────────────────────────────────────────

internal sealed class TokenRequest
{
    [JsonPropertyName("clientId")]
    public string ClientId { get; set; } = string.Empty;

    [JsonPropertyName("clientSecret")]
    public string ClientSecret { get; set; } = string.Empty;

    [JsonPropertyName("grantType")]
    public string GrantType { get; set; } = "client_credentials";
}

internal sealed class TokenResponse
{
    [JsonPropertyName("accessToken")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("expiresIn")]
    public int ExpiresIn { get; set; } = 900;
}

[JsonSerializable(typeof(TokenRequest))]
[JsonSerializable(typeof(TokenResponse))]
internal sealed partial class TokenJsonContext : JsonSerializerContext { }
