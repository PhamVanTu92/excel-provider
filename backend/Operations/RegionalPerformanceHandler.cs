using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ReportingPlatform.ExcelProvider.Database;
using ReportingPlatform.Provider.V1;

namespace ReportingPlatform.ExcelProvider.Operations;

/// <summary>
/// Handler for <c>report.regional.performance</c>.
/// Returns a <c>bar_chart</c> payload with two series: Thực tế (actual) vs Mục tiêu (target).
/// Parameter: period = today | week | month (default: month).
/// </summary>
public sealed class RegionalPerformanceHandler : IOperationHandler
{
    private readonly ExcelProviderDb _db;
    private readonly ILogger<RegionalPerformanceHandler> _logger;

    public string OperationPattern => "report.regional.performance";

    public RegionalPerformanceHandler(ExcelProviderDb db, ILogger<RegionalPerformanceHandler> logger)
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

        var period = "month";
        if (!string.IsNullOrWhiteSpace(request.ParamsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(request.ParamsJson);
                if (doc.RootElement.TryGetProperty("period", out var pp)
                    && !string.IsNullOrWhiteSpace(pp.GetString()))
                    period = pp.GetString()!;
            }
            catch { /* keep default */ }
        }

        var today = DateOnly.FromDateTime(DateTime.Today);
        (DateOnly from, DateOnly to) = period switch
        {
            "today" => (today, today),
            "week"  => (today.AddDays(-(int)today.DayOfWeek + 1), today),
            _       => (today.AddDays(-29), today),   // "month" = last 30 days
        };

        _logger.LogInformation("RegionalPerformance period={Period} [{From},{To}]", period, from, to);

        await reportProgress(30, "Querying sales…");
        var sales = await _db.GetSalesByDateRangeAsync(from, to, ct);

        await reportProgress(55, "Querying region targets…");
        var regions = await _db.GetRegionsAsync(ct);

        await reportProgress(75, "Building bar_chart…");

        int    periodDays  = to.DayNumber - from.DayNumber + 1;
        double monthFrac   = periodDays / 30.0;

        // Revenue by region (actual)
        var revenueByRegion = sales
            .GroupBy(r => r.Region)
            .ToDictionary(g => g.Key, g => Math.Round(g.Sum(r => r.Revenue), 2));

        // Ordered by region name for a stable axis
        var regionNames = regions
            .Select(r => r.Name)
            .OrderBy(n => n)
            .ToList();

        // Add any sales-only regions not in the regions table
        foreach (var extra in revenueByRegion.Keys.Where(k => !regionNames.Contains(k)))
            regionNames.Add(extra);

        var regionLookup = regions.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);

        var actualData = new JsonArray();
        var targetData = new JsonArray();

        foreach (var name in regionNames)
        {
            decimal actual = revenueByRegion.TryGetValue(name, out var rev) ? rev : 0m;

            decimal target = regionLookup.TryGetValue(name, out var ri)
                ? Math.Round(ri.MonthlyTarget * (decimal)monthFrac, 2)
                : Math.Round(50_000m * (decimal)monthFrac, 2);

            actualData.Add(new JsonObject { ["x"] = name, ["y"] = (double)actual });
            targetData.Add(new JsonObject { ["x"] = name, ["y"] = (double)target });
        }

        var result = new JsonObject
        {
            ["series"] = new JsonArray
            {
                new JsonObject { ["name"] = "Thực tế", ["data"] = actualData },
                new JsonObject { ["name"] = "Mục tiêu", ["data"] = targetData },
            },
            ["axes"] = new JsonObject
            {
                ["x"] = new JsonObject
                {
                    ["type"]  = "category",
                    ["label"] = "Khu vực",
                },
                ["y"] = new JsonObject
                {
                    ["type"]   = "number",
                    ["label"]  = "Doanh thu (VND)",
                    ["format"] = "currency:VND",
                },
                ["y2"] = (JsonNode?)null,
            },
            ["annotations"] = new JsonArray(),
        };

        return result.ToJsonString();
    }
}
