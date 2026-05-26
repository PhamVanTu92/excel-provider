using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using ReportingPlatform.ExcelProvider.Config;
using ReportingPlatform.ExcelProvider.Database;
using ReportingPlatform.ExcelProvider.Grpc;
using ReportingPlatform.ExcelProvider.Management;
using ReportingPlatform.ExcelProvider.Operations;
using ReportingPlatform.ExcelProvider.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Logging ───────────────────────────────────────────────────────────────────
builder.Logging.ClearProviders();
builder.Logging.AddConsole(opts => opts.FormatterName = "simple");
builder.Logging.SetMinimumLevel(LogLevel.Information);

var config = builder.Configuration;

// ── Options ───────────────────────────────────────────────────────────────────
builder.Services.Configure<ProviderOptions>(config.GetSection(ProviderOptions.SectionName));
builder.Services.Configure<IngestionOptions>(config.GetSection(IngestionOptions.Section));

// ── Database — Source (postgres excel_provider, read+write) ──────────────────
var excelDbConnStr = config.GetConnectionString("ExcelDb")
    ?? "Host=localhost;Port=5434;Database=excel_provider;Username=excel;Password=excel";

builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(excelDbConnStr));
builder.Services.AddSingleton<ExcelProviderDb>();

// ── HttpClients ───────────────────────────────────────────────────────────────
builder.Services.AddHttpClient<TokenService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

// Named client used by NotificationService to post events to Ingestion API
builder.Services.AddHttpClient("ingestion", client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});

// ── Operation handlers ────────────────────────────────────────────────────────
builder.Services.AddSingleton<IOperationHandler, DashboardSummaryHandler>();
builder.Services.AddSingleton<IOperationHandler, SalesTrendHandler>();
builder.Services.AddSingleton<IOperationHandler, InventoryStatusHandler>();
builder.Services.AddSingleton<IOperationHandler, RegionalPerformanceHandler>();
builder.Services.AddSingleton<IOperationHandler, ChannelComparisonHandler>();
builder.Services.AddSingleton<IOperationHandler, ProductDetailHandler>();
builder.Services.AddSingleton<IOperationHandler, TopPerformersHandler>();

// ── Dispatcher ────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<OperationDispatcher>();

// ── gRPC bridge client (BackgroundService) ────────────────────────────────────
builder.Services.AddSingleton<ProviderBridgeClient>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ProviderBridgeClient>());

// ── HTTP Management API ───────────────────────────────────────────────────────
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddSingleton<DataManagementService>();
builder.Services.AddControllers();

// ── Build app ─────────────────────────────────────────────────────────────────
var app = builder.Build();

// ── Startup tasks ─────────────────────────────────────────────────────────────

var logger       = app.Services.GetRequiredService<ILogger<Program>>();
var providerOpts = app.Services.GetRequiredService<IOptions<ProviderOptions>>().Value;

// ── Bootstrap: fetch ClientSecret from HDOS if not hardcoded ─────────────────
if (string.IsNullOrWhiteSpace(providerOpts.ClientSecret))
{
    if (string.IsNullOrWhiteSpace(providerOpts.BootstrapToken) ||
        string.IsNullOrWhiteSpace(providerOpts.BootstrapUrl))
    {
        logger.LogError(
            "ClientSecret is empty and BootstrapToken/BootstrapUrl are not configured. " +
            "Either set Provider__ClientSecret or both Provider__BootstrapUrl + Provider__BootstrapToken.");
        throw new InvalidOperationException("Cannot start without credentials.");
    }

    var bootstrapEndpoint = $"{providerOpts.BootstrapUrl.TrimEnd('/')}/api/v1/providers/bootstrap";
    logger.LogInformation("ClientSecret not set — fetching from HDOS bootstrap API at {Url}", bootstrapEndpoint);

    using var bootstrapHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    try
    {
        var resp = await bootstrapHttp.PostAsJsonAsync(
            bootstrapEndpoint,
            new BootstrapRequest(providerOpts.ClientId, providerOpts.BootstrapToken));

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync();
            logger.LogError("Bootstrap API returned {Status}: {Body}", (int)resp.StatusCode, body);
            throw new InvalidOperationException($"Bootstrap failed ({resp.StatusCode}): {body}");
        }

        var result = await resp.Content.ReadFromJsonAsync<BootstrapResponse>(
            BootstrapJsonContext.Default.BootstrapResponse)
            ?? throw new InvalidOperationException("Bootstrap API returned empty body.");

        if (string.IsNullOrWhiteSpace(result.ClientSecret))
            throw new InvalidOperationException("Bootstrap API returned empty clientSecret.");

        providerOpts.ClientSecret = result.ClientSecret;
        logger.LogInformation("ClientSecret fetched from HDOS bootstrap — provider ready to authenticate.");
    }
    catch (Exception ex) when (ex is not InvalidOperationException)
    {
        logger.LogError(ex, "Failed to reach HDOS bootstrap API at {Url}. Cannot start without credentials.", bootstrapEndpoint);
        throw;
    }
}

// Initialize source DB (create tables + seed if empty)
var db = app.Services.GetRequiredService<ExcelProviderDb>();
try
{
    await db.InitializeAsync();
}
catch (Exception ex)
{
    logger.LogWarning(ex, "Source DB initialization failed — continuing; service will retry on next query");
}

logger.LogInformation(
    "Excel Provider starting — providerId={ProviderId}, bridge={Bridge}, httpPort=5600",
    providerOpts.ProviderId, providerOpts.BridgeGrpcUrl);

// ── HTTP middleware ────────────────────────────────────────────────────────────
app.MapControllers();

// Simple health-check endpoint
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "excel-provider" }));

await app.RunAsync();

// ── Bootstrap DTOs ────────────────────────────────────────────────────────────

internal sealed record BootstrapRequest(
    [property: JsonPropertyName("clientId")]      string ClientId,
    [property: JsonPropertyName("bootstrapToken")] string BootstrapToken);

internal sealed record BootstrapResponse(
    [property: JsonPropertyName("clientSecret")] string ClientSecret);

[JsonSerializable(typeof(BootstrapRequest))]
[JsonSerializable(typeof(BootstrapResponse))]
internal sealed partial class BootstrapJsonContext : JsonSerializerContext { }
