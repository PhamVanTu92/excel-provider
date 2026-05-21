using System.Text.Json;
using Microsoft.Extensions.Logging;
using ReportingPlatform.ExcelProvider.Database;
using ReportingPlatform.Provider.V1;

namespace ReportingPlatform.ExcelProvider.Operations;

/// <summary>
/// Handler for <c>report.regional.performance</c>.
/// Aggregates sales by region for a given period (today / week / month) and compares against targets.
/// </summary>
public sealed class RegionalPerformanceHandler : IOperationHandler
{
    private readonly ReportingDb _db;
    private readonly ILogger<RegionalPerformanceHandler> _logger;

    public string OperationPattern => "report.regional.performance";

    public RegionalPerformanceHandler(ReportingDb db, ILogger<RegionalPerformanceHandler> logger)
    {
        _db     = db;
        _logger = logger;
    }

    public async Task<string> ExecuteAsync(
        OperationRequest request,
        Func<int, string, Task> reportProgress,
        CancellationToken ct)
    {
        await reportProgress(10, "Parsing parameters…");

        using var doc = JsonDocument.Parse(request.ParamsJson ?? """{"period":"today"}""");
        var period    = doc.RootElement.GetProperty("period").GetString() ?? "today";

        _logger.LogInformation("RegionalPerformance period={Period}", period);

        var today = DateOnly.FromDateTime(DateTime.Today);
        (DateOnly From, DateOnly To) range = period switch
        {
            "week"  => (today.AddDays(-(int)today.DayOfWeek + 1), today),   // Mon–today (ISO)
            "month" => (new DateOnly(today.Year, today.Month, 1), today),
            _       => (today, today), // "today"
        };

        await reportProgress(30, $"Querying sales for period {range.From} – {range.To}…");
        var sales   = await _db.GetSalesByDateRangeAsync(range.From, range.To, ct);

        await reportProgress(50, "Querying region targets…");
        var regions = await _db.GetRegionsAsync(ct);

        await reportProgress(70, "Aggregating by region…");

        // Calculate target for the period (scale MonthlyTarget)
        int periodDays   = (range.To.ToDateTime(TimeOnly.MinValue) - range.From.ToDateTime(TimeOnly.MinValue)).Days + 1;
        double monthFrac = periodDays / 30.0;

        var regionMap = regions.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);

        var regionGroups = sales
            .GroupBy(r => r.Region)
            .Select(g =>
            {
                decimal revenue = g.Sum(r => r.Revenue);
                int     units   = g.Sum(r => r.Units);

                decimal target = regionMap.TryGetValue(g.Key, out var ri)
                    ? Math.Round(ri.MonthlyTarget * (decimal)monthFrac, 2)
                    : 50_000m;

                double achievementPct = target > 0
                    ? Math.Round((double)revenue / (double)target * 100, 1)
                    : 0;

                return new
                {
                    name           = g.Key,
                    revenue        = Math.Round(revenue, 2),
                    units,
                    target,
                    achievementPct,
                };
            })
            .OrderByDescending(r => r.revenue)
            .ToList();

        // Ensure all regions appear even if they have no sales in the period
        var presentRegions = regionGroups.Select(r => r.name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var zeroRegions = regions
            .Where(r => !presentRegions.Contains(r.Name))
            .Select(r =>
            {
                decimal target = Math.Round(r.MonthlyTarget * (decimal)monthFrac, 2);
                return new
                {
                    name           = r.Name,
                    revenue        = 0m,
                    units          = 0,
                    target,
                    achievementPct = 0.0,
                };
            })
            .ToList();

        await reportProgress(90, "Building response…");

        var result = new
        {
            regions = regionGroups
                .Cast<object>()
                .Concat(zeroRegions)
                .ToList()
        };

        return JsonSerializer.Serialize(result);
    }
}
