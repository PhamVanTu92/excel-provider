using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ReportingPlatform.ExcelProvider.Database;
using ReportingPlatform.Provider.V1;

namespace ReportingPlatform.ExcelProvider.Operations;

/// <summary>
/// Handler for <c>report.sales.trend</c>.
/// Returns a <c>line_chart</c> payload with daily revenue data.
/// Parameters: fromDate (default: 30 days ago), toDate (default: today), groupBy (day/week/month).
/// </summary>
public sealed class SalesTrendHandler : IOperationHandler
{
    private readonly ExcelProviderDb _db;
    private readonly ILogger<SalesTrendHandler> _logger;

    public string OperationPattern => "report.sales.trend";

    public SalesTrendHandler(ExcelProviderDb db, ILogger<SalesTrendHandler> logger)
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

        var today   = DateOnly.FromDateTime(DateTime.Today);
        var toDate  = today;
        var fromDate= today.AddDays(-29);   // default: last 30 days
        var groupBy = "day";

        if (!string.IsNullOrWhiteSpace(request.ParamsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(request.ParamsJson);
                var root = doc.RootElement;

                if (root.TryGetProperty("fromDate", out var fp)
                    && DateOnly.TryParse(fp.GetString(), out var pf)) fromDate = pf;

                if (root.TryGetProperty("toDate", out var tp)
                    && DateOnly.TryParse(tp.GetString(), out var pt)) toDate = pt;

                if (root.TryGetProperty("groupBy", out var gp)
                    && !string.IsNullOrWhiteSpace(gp.GetString()))
                    groupBy = gp.GetString()!;
            }
            catch { /* keep defaults */ }
        }

        _logger.LogInformation("SalesTrend from={From} to={To} groupBy={Group}", fromDate, toDate, groupBy);

        await reportProgress(30, "Querying sales…");
        var rows = await _db.GetSalesByDateRangeAsync(fromDate, toDate, ct);

        await reportProgress(70, "Grouping data…");

        IEnumerable<(string x, decimal revenue)> points = groupBy switch
        {
            "week"  => GroupByWeek(rows),
            "month" => GroupByMonth(rows),
            _       => GroupByDay(rows, fromDate, toDate),
        };

        await reportProgress(90, "Building line_chart…");

        var seriesData = new JsonArray();
        foreach (var (x, rev) in points)
        {
            seriesData.Add(new JsonObject
            {
                ["x"] = x,
                ["y"] = (double)Math.Round(rev, 2),
            });
        }

        var result = new JsonObject
        {
            ["series"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "Doanh thu",
                    ["data"] = seriesData,
                }
            },
            ["axes"] = new JsonObject
            {
                ["x"] = new JsonObject
                {
                    ["type"]  = groupBy == "day" ? "time" : "category",
                    ["label"] = groupBy switch
                    {
                        "week"  => "Tuần",
                        "month" => "Tháng",
                        _       => "Ngày",
                    },
                    ["format"] = groupBy == "day" ? "yyyy-MM-dd" : (JsonNode?)null,
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

    // ── Grouping helpers ───────────────────────────────────────────────────────

    private static IEnumerable<(string x, decimal revenue)> GroupByDay(
        List<SaleRow> rows, DateOnly from, DateOnly to)
    {
        var dict = new SortedDictionary<string, decimal>();
        for (var d = from; d <= to; d = d.AddDays(1))
            dict[d.ToString("yyyy-MM-dd")] = 0m;

        foreach (var r in rows)
        {
            var key = r.Date.ToString("yyyy-MM-dd");
            if (dict.ContainsKey(key)) dict[key] += r.Revenue;
        }

        return dict.Select(kv => (kv.Key, kv.Value));
    }

    private static IEnumerable<(string x, decimal revenue)> GroupByWeek(List<SaleRow> rows)
    {
        var dict = new SortedDictionary<string, decimal>();
        foreach (var r in rows)
        {
            int wk = ISOWeek.GetWeekOfYear(r.Date.ToDateTime(TimeOnly.MinValue));
            var key = $"{r.Date.Year}-W{wk:D2}";
            dict[key] = dict.GetValueOrDefault(key) + r.Revenue;
        }
        return dict.Select(kv => (kv.Key, kv.Value));
    }

    private static IEnumerable<(string x, decimal revenue)> GroupByMonth(List<SaleRow> rows)
    {
        var dict = new SortedDictionary<string, decimal>();
        foreach (var r in rows)
        {
            var key = $"{r.Date.Year}-{r.Date.Month:D2}";
            dict[key] = dict.GetValueOrDefault(key) + r.Revenue;
        }
        return dict.Select(kv => (kv.Key, kv.Value));
    }
}
