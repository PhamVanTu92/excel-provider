using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Npgsql;
using ReportingPlatform.ExcelProvider.Database;

namespace ReportingPlatform.ExcelProvider.Services;

/// <summary>
/// BackgroundService that maintains a dedicated pg_notify LISTEN connection to
/// <c>excel_reporting</c> and pushes <c>datasource.updated</c> events to HDOS
/// whenever logical replication delivers changes to the reporting DB.
///
/// Flow:
///   excel_provider write → WAL → logical replication → excel_reporting tables
///   → pg_notify trigger fires → this service catches it
///   → NotificationService.NotifyDataChangedAsync → HDOS Ingestion API → frontend
///
/// Batching: notifications within a 300 ms window are merged before posting to
/// HDOS to avoid stampedes when many rows arrive during the initial copy phase.
/// </summary>
public sealed class ReplicationListenerService : BackgroundService
{
    // Maps replicated table name → report operation patterns that depend on it.
    private static readonly Dictionary<string, string[]> TableToOperations =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["sales"] =
            [
                "report.dashboard.summary",
                "report.sales.trend",
                "report.channel.comparison",
                "report.top.performers",
                "report.regional.performance",
                "report.product.detail",
            ],
            ["products"] =
            [
                "report.inventory.status",
                "report.dashboard.summary",
            ],
            ["regions"] = ["report.regional.performance"],
        };

    // Exponential backoff steps (ms) for reconnection after connection loss.
    private static readonly int[] BackoffMs = [3_000, 10_000, 30_000, 60_000];

    // How long to accumulate pg_notify events before flushing to HDOS.
    private const int FlushIntervalMs = 300;

    private readonly ReportingDb         _reportingDb;
    private readonly NotificationService _notificationService;
    private readonly ILogger<ReplicationListenerService> _logger;

    public ReplicationListenerService(
        ReportingDb                          reportingDb,
        NotificationService                  notificationService,
        ILogger<ReplicationListenerService>  logger)
    {
        _reportingDb         = reportingDb;
        _notificationService = notificationService;
        _logger              = logger;
    }

    // ─── BackgroundService entry point ────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int attempt = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ListenLoopAsync(stoppingToken);
                attempt = 0; // reset backoff on clean exit
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                var delay = TimeSpan.FromMilliseconds(
                    BackoffMs[Math.Min(attempt, BackoffMs.Length - 1)]);

                _logger.LogWarning(ex,
                    "Replication listener lost connection — reconnecting in {Delay}s (attempt {Attempt})",
                    delay.TotalSeconds, ++attempt);

                try { await Task.Delay(delay, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        _logger.LogInformation("ReplicationListenerService stopped");
    }

    // ─── LISTEN loop ──────────────────────────────────────────────────────────

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        // Use a dedicated raw connection (not from pool) — pool connections
        // cannot be reliably reused for LISTEN because they may be returned
        // to the pool while callbacks are still registered.
        await using var conn = new NpgsqlConnection(_reportingDb.ConnectionString);
        await conn.OpenAsync(ct);

        // Thread-safe bag to collect table names from the Notification callback.
        var pending = new ConcurrentBag<string>();

        conn.Notification += (_, e) =>
        {
            var table = e.Payload ?? "";
            if (!string.IsNullOrEmpty(table))
            {
                pending.Add(table);
                _logger.LogDebug(
                    "pg_notify received: channel={Channel}, table={Table}",
                    e.Channel, table);
            }
        };

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "LISTEN reporting_data_changed";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        _logger.LogInformation(
            "ReplicationListenerService connected — listening for pg_notify on excel_reporting");

        // ── Flush loop ────────────────────────────────────────────────────────
        // WaitAsync blocks until a notification arrives OR the linked CTS fires.
        // We cancel the inner CTS every FlushIntervalMs to batch notifications.

        while (!ct.IsCancellationRequested)
        {
            using var flushCts =
                CancellationTokenSource.CreateLinkedTokenSource(ct);
            flushCts.CancelAfter(FlushIntervalMs);

            try
            {
                // Waits for the next pg_notify or until flushCts fires.
                await conn.WaitAsync(flushCts.Token);
            }
            catch (OperationCanceledException)
                when (!ct.IsCancellationRequested)
            {
                // Flush interval elapsed — not an error, just time to flush.
            }

            if (pending.IsEmpty) continue;

            // Drain and deduplicate table names.
            var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (pending.TryTake(out var t))
                tables.Add(t);

            if (tables.Count == 0) continue;

            // Map tables → distinct affected operation patterns.
            var ops = tables
                .SelectMany(t =>
                    TableToOperations.TryGetValue(t, out var o)
                        ? o
                        : Array.Empty<string>())
                .Distinct()
                .ToArray();

            if (ops.Length == 0) continue;

            _logger.LogInformation(
                "Replication flush: tables=[{Tables}] → pushing {Count} operations to HDOS",
                string.Join(", ", tables), ops.Length);

            // Non-blocking fire-and-forget so the listen loop is not stalled
            // if the HDOS Ingestion API is slow.
            _ = _notificationService.NotifyDataChangedAsync(ops, CancellationToken.None);
        }
    }
}
