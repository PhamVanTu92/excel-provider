using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReportingPlatform.ExcelProvider.Config;
using ReportingPlatform.ExcelProvider.Grpc;

namespace ReportingPlatform.ExcelProvider.Services;

/// <summary>
/// Posts a datasource.updated event to the Ingestion API after every Excel write so that
/// the platform can push WidgetStale notifications to connected frontends.
///
/// Auth note: The Ingestion API requires a JWT with scope "ingestion".  The platform token
/// obtained via <see cref="TokenService"/> does NOT carry that scope, so the call will
/// typically return 403.  In that case the service logs a warning and continues gracefully
/// rather than failing the write operation.  Configure a dedicated Keycloak ingestion
/// client to enable this feature.
/// </summary>
public sealed class NotificationService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly TokenService       _tokenService;
    private readonly IngestionOptions   _opts;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(
        IHttpClientFactory               httpFactory,
        TokenService                     tokenService,
        IOptions<IngestionOptions>       opts,
        ILogger<NotificationService>     logger)
    {
        _httpFactory   = httpFactory;
        _tokenService  = tokenService;
        _opts          = opts.Value;
        _logger        = logger;
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
        string token;
        try
        {
            token = await _tokenService.GetTokenAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not obtain bearer token — widget stale notification skipped");
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

[JsonSerializable(typeof(IngestionEventRequest))]
[JsonSerializable(typeof(IngestionEventPayload))]
internal sealed partial class IngestionJsonContext : JsonSerializerContext { }
