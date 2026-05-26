namespace ReportingPlatform.ExcelProvider.Config;

/// <summary>
/// Configuration options for the Ingestion API notification endpoint.
/// </summary>
public sealed class IngestionOptions
{
    public const string Section = "Ingestion";

    /// <summary>Base URL of the Ingestion API, e.g. http://ingestion-api:5100</summary>
    public string BaseUrl { get; set; } = "http://localhost:5100";

    /// <summary>Client ID for obtaining an ingestion-scoped token (future use).</summary>
    public string ClientId { get; set; } = "";

    /// <summary>Client secret for obtaining an ingestion-scoped token (future use).</summary>
    public string ClientSecret { get; set; } = "";

    /// <summary>Platform token endpoint reused to obtain a bearer token for Ingestion API calls.</summary>
    public string TokenEndpoint { get; set; } = "http://localhost:5000/api/v1/providers/token";
}

/// <summary>
/// Configuration options bound from the "Provider" section of appsettings.json.
/// </summary>
public sealed class ProviderOptions
{
    public const string SectionName = "Provider";

    /// <summary>Client ID used for platform token endpoint (client_credentials grant).</summary>
    public string ClientId { get; set; } = "excel-provider";

    /// <summary>
    /// Client secret (plain-text). Can be left empty when BootstrapToken is set —
    /// in that case the secret is fetched from HDOS at startup via the bootstrap API.
    /// </summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>URL of the platform token endpoint, e.g. http://request-api:5000/api/v1/providers/token</summary>
    public string TokenEndpoint { get; set; } = "http://localhost:5000/api/v1/providers/token";

    /// <summary>URL of the provider-bridge gRPC endpoint, e.g. http://provider-bridge:5400</summary>
    public string BridgeGrpcUrl { get; set; } = "http://localhost:5400";

    /// <summary>Provider ID string sent in the Hello message.</summary>
    public string ProviderId { get; set; } = "excel-provider";

    /// <summary>Semver of this provider.</summary>
    public string Version { get; set; } = "1.0.0";

    // ── Bootstrap (alternative to hardcoding ClientSecret) ────────────────────

    /// <summary>
    /// Base URL of HDOS (e.g. http://192.168.100.62:5000).
    /// Used with BootstrapToken to fetch ClientSecret on startup.
    /// </summary>
    public string BootstrapUrl { get; set; } = string.Empty;

    /// <summary>
    /// One-time bootstrap token issued by HDOS Admin → Credentials tab.
    /// If set (and ClientSecret is empty), the provider calls
    /// POST {BootstrapUrl}/api/v1/providers/bootstrap to receive its ClientSecret.
    /// </summary>
    public string BootstrapToken { get; set; } = string.Empty;
}
