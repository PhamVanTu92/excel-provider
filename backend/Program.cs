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

// ── Database (postgres-excel) ─────────────────────────────────────────────────
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

// Initialize postgres-excel database (create tables + seed if empty)
var db = app.Services.GetRequiredService<ExcelProviderDb>();
try
{
    await db.InitializeAsync();
}
catch (Exception ex)
{
    logger.LogWarning(ex, "DB initialization failed — continuing; service will retry on next query");
}

logger.LogInformation(
    "Excel Provider starting — providerId={ProviderId}, bridge={Bridge}, httpPort=5600",
    providerOpts.ProviderId, providerOpts.BridgeGrpcUrl);

// ── HTTP middleware ────────────────────────────────────────────────────────────
app.MapControllers();

// Simple health-check endpoint
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "excel-provider" }));

await app.RunAsync();
