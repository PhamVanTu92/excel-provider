using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ReportingPlatform.ExcelProvider.Database;
using ReportingPlatform.Provider.V1;

namespace ReportingPlatform.ExcelProvider.Operations;

/// <summary>
/// Handler for <c>report.top.performers</c>.
/// Returns top-5 products and regions by revenue for a given period,
/// with growth% compared to the equivalent prior period.
/// </summary>
public sealed class TopPerformersHandler : IOperationHandler
{
    private readonly ExcelProviderDb _db;
    private readonly ILogger<TopPerformersHandler> _logger;

    public string OperationPattern => "report.top.performers";

    public TopPerformersHandler(ExcelProviderDb db, ILogger<TopPerformersHandler> logger)
    {
        _db     = db;
        _logger = logger;
    }

    public async Task<string> ExecuteAsync(
        OperationRequest request,
        Func<int, string, Task> reportProgress,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogInformation("TopPerformers starting — requestId={RequestId}", request.RequestId);

        await reportProgress(10, "Parsing parameters…");

        using var doc = JsonDocument.Parse(request.ParamsJson ?? "{}");
        var root      = doc.RootElement;
        var period    = root.TryGetProperty("period", out var periodProp)
                        ? (periodProp.GetString() ?? "week")
                        : "week";

        // ── Resolve date ranges ────────────────────────────────────────────────

        int days = period switch
        {
            "month"   => 30,
            "quarter" => 90,
            _         => 7,    // "week" is default
        };

        var today       = DateOnly.FromDateTime(DateTime.Today);
        var currentFrom = today.AddDays(-(days - 1));
        var currentTo   = today;
        var prevTo      = currentFrom.AddDays(-1);
        var prevFrom    = prevTo.AddDays(-(days - 1));

        _logger.LogInformation(
            "TopPerformers period={Period} current=[{CFrom},{CTo}] previous=[{PFrom},{PTo}]",
            period, currentFrom, currentTo, prevFrom, prevTo);

        await reportProgress(30, "Querying current period sales…");
        var currentRows  = await _db.GetSalesByDateRangeAsync(currentFrom, currentTo, ct);

        await reportProgress(50, "Querying previous period sales…");
        var previousRows = await _db.GetSalesByDateRangeAsync(prevFrom, prevTo, ct);

        await reportProgress(70, "Ranking products and regions…");

        // ── Top products ───────────────────────────────────────────────────────

        var currentByProduct  = currentRows
            .GroupBy(r => r.Product)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Revenue));

        var previousByProduct = previousRows
            .GroupBy(r => r.Product)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Revenue));

        var topProducts = currentByProduct
            .OrderByDescending(kv => kv.Value)
            .Take(5)
            .Select((kv, idx) =>
            {
                previousByProduct.TryGetValue(kv.Key, out var prev);
                var growth = ComputeGrowth(kv.Value, prev);
                return new
                {
                    rank    = idx + 1,
                    name    = kv.Key,
                    revenue = Math.Round(kv.Value, 2),
                    growth,
                };
            })
            .ToList();

        // ── Top regions ────────────────────────────────────────────────────────

        var currentByRegion  = currentRows
            .GroupBy(r => r.Region)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Revenue));

        var previousByRegion = previousRows
            .GroupBy(r => r.Region)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Revenue));

        var topRegions = currentByRegion
            .OrderByDescending(kv => kv.Value)
            .Take(5)
            .Select((kv, idx) =>
            {
                previousByRegion.TryGetValue(kv.Key, out var prev);
                var growth = ComputeGrowth(kv.Value, prev);
                return new
                {
                    rank    = idx + 1,
                    name    = kv.Key,
                    revenue = Math.Round(kv.Value, 2),
                    growth,
                };
            })
            .ToList();

        await reportProgress(90, "Building response…");

        var result = new
        {
            topProducts,
            topRegions,
            period,
        };

        sw.Stop();
        _logger.LogInformation(
            "TopPerformers complete — elapsed={Elapsed}ms, currentRows={Rows}",
            sw.ElapsedMilliseconds, currentRows.Count);

        return JsonSerializer.Serialize(result);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Growth = (current - previous) / previous * 100, rounded to 1 decimal place.
    /// Returns 0.0 when previous is zero (no prior baseline).
    /// </summary>
    private static double ComputeGrowth(decimal current, decimal previous)
    {
        if (previous == 0m) return 0.0;
        return Math.Round((double)((current - previous) / previous * 100), 1);
    }
}
