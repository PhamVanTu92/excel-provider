using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ReportingPlatform.ExcelProvider.Database;
using ReportingPlatform.Provider.V1;

namespace ReportingPlatform.ExcelProvider.Operations;

/// <summary>
/// Handler for <c>report.sales.heatmap</c>.
/// Returns a <c>heatmap</c> payload — region × day-of-week average daily revenue.
/// Parameters: period (week|month, default month).
/// </summary>
public sealed class SalesHeatmapHandler : IOperationHandler
{
    private readonly ExcelProviderDb _db;
    private readonly ILogger<SalesHeatmapHandler> _logger;

    public string OperationPattern => "report.sales.heatmap";

    // DayOfWeek → Vietnamese label (Mon-first order)
    private static readonly string[] DayLabels = ["T2", "T3", "T4", "T5", "T6", "T7", "CN"];

    // Maps System.DayOfWeek (Sun=0…Sat=6) → index 0-6 in DayLabels
    private static int DayIndex(DayOfWeek dow) => dow switch
    {
        DayOfWeek.Monday    => 0,
        DayOfWeek.Tuesday   => 1,
        DayOfWeek.Wednesday => 2,
        DayOfWeek.Thursday  => 3,
        DayOfWeek.Friday    => 4,
        DayOfWeek.Saturday  => 5,
        _                   => 6,  // Sunday
    };

    public SalesHeatmapHandler(ExcelProviderDb db, ILogger<SalesHeatmapHandler> logger)
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

        var today  = DateOnly.FromDateTime(DateTime.Today);
        int days   = 30;   // default: month

        if (!string.IsNullOrWhiteSpace(request.ParamsJson))
        {
            try
            {
                using var doc  = JsonDocument.Parse(request.ParamsJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("period", out var pp))
                {
                    var period = pp.GetString();
                    if (period == "week") days = 7;
                }
            }
            catch { /* keep defaults */ }
        }

        var fromDate = today.AddDays(-(days - 1));
        _logger.LogInformation("SalesHeatmap from={From} to={To}", fromDate, today);

        await reportProgress(30, "Querying sales…");
        var rows = await _db.GetSalesByDateRangeAsync(fromDate, today, ct);

        await reportProgress(60, "Querying regions…");
        var regions = await _db.GetRegionsAsync(ct);

        await reportProgress(80, "Building heatmap…");

        var regionNames = regions.Select(r => r.Name).OrderBy(n => n).ToList();

        // Group: region → dayIndex → list of daily totals
        // First aggregate revenue by (date, region), then group by dayOfWeek
        var byDateRegion = rows
            .GroupBy(r => (r.Date, r.Region))
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Revenue));

        // Build cell averages
        // key = (regionName, dayIndex) → (totalRevenue, dayCount)
        var sums   = new Dictionary<(string, int), (decimal total, int count)>();

        foreach (var ((date, region), revenue) in byDateRegion)
        {
            int idx = DayIndex(date.ToDateTime(TimeOnly.MinValue).DayOfWeek);
            var key = (region, idx);
            sums.TryGetValue(key, out var existing);
            sums[key] = (existing.total + revenue, existing.count + 1);
        }

        var cells  = new JsonArray();
        double min = double.MaxValue;
        double max = double.MinValue;
        var cellList = new List<(string x, string y, double value)>();

        foreach (var regionName in regionNames)
        {
            for (int di = 0; di < DayLabels.Length; di++)
            {
                var key = (regionName, di);
                if (sums.TryGetValue(key, out var s) && s.count > 0)
                {
                    double avg = Math.Round((double)(s.total / s.count), 2);
                    cellList.Add((DayLabels[di], regionName, avg));
                    if (avg < min) min = avg;
                    if (avg > max) max = avg;
                }
            }
        }

        if (cellList.Count == 0) { min = 0; max = 0; }

        foreach (var (x, y, value) in cellList)
        {
            cells.Add(new JsonObject
            {
                ["x"]       = x,
                ["y"]       = y,
                ["value"]   = value,
                ["tooltip"] = $"{value:N0} VND trung bình",
            });
        }

        var xLabelsArr = new JsonArray();
        foreach (var l in DayLabels) xLabelsArr.Add(l);

        var yLabelsArr = new JsonArray();
        foreach (var n in regionNames) yLabelsArr.Add(n);

        var result = new JsonObject
        {
            ["xLabels"]    = xLabelsArr,
            ["yLabels"]    = yLabelsArr,
            ["cells"]      = cells,
            ["valueRange"] = new JsonObject { ["min"] = min == double.MaxValue ? 0 : min, ["max"] = max == double.MinValue ? 0 : max },
            ["colorScale"] = "sequential",
        };

        return result.ToJsonString();
    }
}
