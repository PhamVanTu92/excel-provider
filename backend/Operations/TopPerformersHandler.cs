using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ReportingPlatform.ExcelProvider.Database;
using ReportingPlatform.Provider.V1;

namespace ReportingPlatform.ExcelProvider.Operations;

/// <summary>
/// Handler for <c>report.top.performers</c>.
/// Returns a <c>simple_table</c> payload with top-5 products by revenue
/// and their growth rate versus the equivalent prior period.
/// Parameter: period = week | month | quarter (default: month).
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
        await reportProgress(10, "Parsing parameters…");

        var period = "month";
        int limit  = 10;

        if (!string.IsNullOrWhiteSpace(request.ParamsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(request.ParamsJson);
                var root = doc.RootElement;

                if (root.TryGetProperty("period", out var pp)
                    && !string.IsNullOrWhiteSpace(pp.GetString()))
                    period = pp.GetString()!;

                if (root.TryGetProperty("limit", out var lp)
                    && lp.TryGetInt32(out var lv) && lv > 0)
                    limit = Math.Min(lv, 50);
            }
            catch { /* keep defaults */ }
        }

        int days = period switch
        {
            "quarter" => 90,
            "week"    => 7,
            _         => 30,    // "month"
        };

        var today       = DateOnly.FromDateTime(DateTime.Today);
        var currentFrom = today.AddDays(-(days - 1));
        var prevTo      = currentFrom.AddDays(-1);
        var prevFrom    = prevTo.AddDays(-(days - 1));

        _logger.LogInformation(
            "TopPerformers period={Period} current=[{CF},{CT}] prev=[{PF},{PT}]",
            period, currentFrom, today, prevFrom, prevTo);

        await reportProgress(30, "Querying current period…");
        var currentRows = await _db.GetSalesByDateRangeAsync(currentFrom, today, ct);

        await reportProgress(55, "Querying previous period…");
        var prevRows = await _db.GetSalesByDateRangeAsync(prevFrom, prevTo, ct);

        await reportProgress(80, "Ranking top products…");

        var prevByProduct = prevRows
            .GroupBy(r => r.Product)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Revenue));

        var top = currentRows
            .GroupBy(r => r.Product)
            .Select(g =>
            {
                decimal rev  = g.Sum(r => r.Revenue);
                int     units= g.Sum(r => r.Units);
                prevByProduct.TryGetValue(g.Key, out var prev);
                double growth = prev == 0m ? 0.0
                    : Math.Round((double)((rev - prev) / prev * 100), 1);

                return (Product: g.Key, Revenue: rev, Units: units, Growth: growth);
            })
            .OrderByDescending(x => x.Revenue)
            .Take(limit)
            .ToList();

        var dataRows = new JsonArray();
        for (int i = 0; i < top.Count; i++)
        {
            var row = top[i];
            dataRows.Add(new JsonObject
            {
                ["rank"]    = i + 1,
                ["product"] = row.Product,
                ["revenue"] = (double)Math.Round(row.Revenue, 2),
                ["units"]   = row.Units,
                ["growth"]  = row.Growth,
            });
        }

        var result = new JsonObject
        {
            ["columns"] = new JsonArray
            {
                Col("rank",    "#",          "number", null,           align: "center", sortable: false),
                Col("product", "Sản phẩm",   "string", null,           align: "left"),
                Col("revenue", "Doanh thu",  "number", "currency:VND", align: "right"),
                Col("units",   "Số lượng",   "number", null,           align: "right"),
                Col("growth",  "Tăng trưởng","number", "percent:1",    align: "right"),
            },
            ["rows"]       = dataRows,
            ["pagination"] = new JsonObject
            {
                ["mode"]      = "client",
                ["totalRows"] = top.Count,
            },
        };

        return result.ToJsonString();
    }

    // ── Helper ─────────────────────────────────────────────────────────────────

    private static JsonObject Col(string key, string label, string type,
        string? format, string align = "left", bool sortable = true) => new()
    {
        ["key"]        = key,
        ["label"]      = label,
        ["type"]       = type,
        ["sortable"]   = sortable,
        ["filterable"] = false,
        ["format"]     = format,
        ["visible"]    = true,
        ["align"]      = align,
    };
}
