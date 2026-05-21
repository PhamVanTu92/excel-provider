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

// ── Database — Reporting (postgres excel_reporting, read-only replica) ────────
var reportingConnStr = config.GetConnectionString("ReportingDb")
    ?? "Host=localhost;Port=5434;Database=excel_reporting;Username=excel;Password=excel";

// Keyed so it doesn't conflict with the source NpgsqlDataSource registration.
builder.Services.AddKeyedSingleton<NpgsqlDataSource>(
    "reporting", (_, _) => NpgsqlDataSource.Create(reportingConnStr));

builder.Services.AddSingleton<ReportingDb>(sp => new ReportingDb(
    sp.GetRequiredKeyedService<NpgsqlDataSource>("reporting"),
    reportingConnStr,
    sp.GetRequiredService<ILogger<ReportingDb>>()));

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

// ── Replication listener (BackgroundService) ──────────────────────────────────
// Listens for pg_notify on excel_reporting and pushes datasource.updated to HDOS.
builder.Services.AddHostedService<ReplicationListenerService>();

// ── HTTP Management API ───────────────────────────────────────────────────────
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddSingleton<DataManagementService>();
builder.Services.AddControllers();

// ── Build app ─────────────────────────────────────────────────────────────────
var app = builder.Build();

// ── Startup tasks ─────────────────────────────────────────────────────────────

var logger       = app.Services.GetRequiredService<ILogger<Program>>();
var providerOpts = app.Services.GetRequiredService<IOptions<ProviderOptions>>().Value;

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

// Initialize reporting DB (create tables + pg_notify triggers — idempotent)
// NOTE: Logical replication subscription must be created separately via
//       db/reporting/03_create_subscription.sql (requires superuser).
var reportingDb = app.Services.GetRequiredService<ReportingDb>();
try
{
    await reportingDb.InitializeAsync();
}
catch (Exception ex)
{
    logger.LogWarning(ex,
        "Reporting DB initialization failed — reports will fall back to source DB; " +
        "ensure excel_reporting database exists and run db/reporting/README.md setup steps");
}

logger.LogInformation(
    "Excel Provider starting — providerId={ProviderId}, bridge={Bridge}, httpPort=5600",
    providerOpts.ProviderId, providerOpts.BridgeGrpcUrl);

// ── HTTP middleware ────────────────────────────────────────────────────────────
app.MapControllers();

// Simple health-check endpoint
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "excel-provider" }));

await app.RunAsync();
