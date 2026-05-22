using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReportingPlatform.ExcelProvider.Config;

namespace ReportingPlatform.ExcelProvider.Services;

/// <summary>
/// Posts a datasource.updated event to the Ingestion API after every Excel write so that
/// the platform can push WidgetStale notifications to connected frontends.
///
/// Auth: The Ingestion API validates Keycloak-issued JWTs and requires the claim
/// scope="ingestion". This service therefore obtains its own token via the Keycloak
/// client_credentials grant (configured in IngestionOptions) — NOT the platform provider
/// token used for the gRPC bridge, which has the wrong issuer/audience and is rejected 401.
/// </summary>
public sealed class NotificationService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IngestionOptions   _opts;
    private readonly ILogger<NotificationService> _logger;

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string?        _cachedToken;
    private DateTimeOffset _tokenExpiresAt;

    public NotificationService(
        IHttpClientFactory               httpFactory,
        IOptions<IngestionOptions>       opts,
        ILogger<NotificationService>     logger)
    {
        _httpFactory   = httpFactory;
        _opts          = opts.Value;
        _logger        = logger;
    }

    // ── Keycloak client_credentials token (cached, refreshed 60s before expiry) ──
    private async Task<string?> GetIngestionTokenAsync(CancellationToken ct)
    {
        if (_cachedToken is not null && DateTimeOffset.UtcNow < _tokenExpiresAt)
            return _cachedToken;

        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_cachedToken is not null && DateTimeOffset.UtcNow < _tokenExpiresAt)
                return _cachedToken;

            using var http = _httpFactory.CreateClient("ingestion");
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"]    = "client_credentials",
                ["client_id"]     = _opts.ClientId,
                ["client_secret"] = _opts.ClientSecret,
                ["scope"]         = "ingestion",
            });

            using var resp = await http.PostAsync(_opts.TokenEndpoint, form, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning(
                    "Keycloak token endpoint returned {Status}: {Body}", (int)resp.StatusCode, body);
                return null;
            }

            var tok = await resp.Content.ReadFromJsonAsync(
                IngestionJsonContext.Default.KeycloakTokenResponse, ct);
            if (tok?.AccessToken is null) return null;

            _cachedToken    = tok.AccessToken;
            _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(tok.ExpiresIn - 60, 30));
            return _cachedToken;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to obtain Keycloak ingestion token");
            return null;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    /// <summary>
    /// Sends a <c>datasource.updated</c> event to the Ingestion API.
    /// 403 responses are swallowed with a warning; all other errors are also swallowed
    /// (non-fatal) so that a notification failure never blocks a successful write.
    /// </summary>
    public async Task NotifyDataChangedAsync(
        string[]          affectedOperations,
        CancellationToken ct = default)
    {
        var token = await GetIngestionTokenAsync(ct);
        if (token is null)
        {
            _logger.LogWarning("No ingestion token — widget stale notification skipped");
            return;
        }

        var payload = new IngestionEventRequest
        {
            EventType  = "datasource.updated",
            OccurredAt = DateTimeOffset.UtcNow,
            Payload    = new IngestionEventPayload
            {
                Source             = "excel",
                AffectedOperations = affectedOperations,
            }
        };

        var endpoint = $"{_opts.BaseUrl.TrimEnd('/')}/api/v1/events";

        try
        {
            using var http = _httpFactory.CreateClient("ingestion");
            using var req  = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(
                    payload,
                    IngestionJsonContext.Default.IngestionEventRequest)
            };
            req.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

            if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                _logger.LogWarning(
                    "Ingestion auth not configured, widget stale notification skipped " +
                    "(Ingestion API returned 403 — configure an ingestion-scoped Keycloak client)");
                return;
            }

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning(
                    "Ingestion API returned {StatusCode} for event notification: {Body}",
                    (int)resp.StatusCode, body);
                return;
            }

            _logger.LogInformation(
                "WidgetStale notification sent — affectedOperations=[{Ops}]",
                string.Join(", ", affectedOperations));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to notify Ingestion API — widget stale notification skipped");
        }
    }
}

// ─── DTOs ─────────────────────────────────────────────────────────────────────

internal sealed class IngestionEventRequest
{
    [JsonPropertyName("eventType")]
    public string EventType { get; set; } = string.Empty;

    [JsonPropertyName("occurredAt")]
    public DateTimeOffset OccurredAt { get; set; }

    [JsonPropertyName("payload")]
    public IngestionEventPayload Payload { get; set; } = new();
}

internal sealed class IngestionEventPayload
{
    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("affectedOperations")]
    public string[] AffectedOperations { get; set; } = [];
}

internal sealed class KeycloakTokenResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; } = 300;
}

[JsonSerializable(typeof(IngestionEventRequest))]
[JsonSerializable(typeof(IngestionEventPayload))]
[JsonSerializable(typeof(KeycloakTokenResponse))]
internal sealed partial class IngestionJsonContext : JsonSerializerContext { }
