using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ReportingPlatform.ExcelProvider.Database;
using ReportingPlatform.Provider.V1;

namespace ReportingPlatform.ExcelProvider.Operations;

/// <summary>
/// Handler for <c>report.sales.scatter</c>.
/// Returns a <c>scatter</c> payload — each product as a bubble (units sold, revenue, avg-price size).
/// </summary>
public sealed class SalesScatterHandler : IOperationHandler
{
    private readonly ExcelProviderDb _db;
    private readonly ILogger<SalesScatterHandler> _logger;

    public string OperationPattern => "report.sales.scatter";

    public SalesScatterHandler(ExcelProviderDb db, ILogger<SalesScatterHandler> logger)
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

        var today    = DateOnly.FromDateTime(DateTime.Today);
        var fromDate = today.AddDays(-29);

        if (!string.IsNullOrWhiteSpace(request.ParamsJson))
        {
            try
            {
                using var doc  = JsonDocument.Parse(request.ParamsJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("fromDate", out var fp)
                    && DateOnly.TryParse(fp.GetString(), out var pf)) fromDate = pf;
                if (root.TryGetProperty("toDate", out var tp)
                    && DateOnly.TryParse(tp.GetString(), out var pt)) today = pt;
            }
            catch { /* keep defaults */ }
        }

        _logger.LogInformation("SalesScatter from={From} to={To}", fromDate, today);

        await reportProgress(30, "Querying sales…");
        var rows = await _db.GetSalesByDateRangeAsync(fromDate, today, ct);

        await reportProgress(70, "Building scatter points…");

        // Group by product
        var productGroups = rows
            .GroupBy(r => r.Product)
            .Select(g => new
            {
                Name         = g.Key,
                TotalRevenue = g.Sum(r => r.Revenue),
                TotalUnits   = g.Sum(r => r.Units),
                AvgPrice     = g.Any() ? g.Sum(r => r.Revenue) / g.Sum(r => r.Units) : 0m,
            })
            .ToList();

        double maxAvgPrice = productGroups.Count > 0
            ? (double)productGroups.Max(p => p.AvgPrice)
            : 1.0;

        if (maxAvgPrice == 0) maxAvgPrice = 1.0;

        var points = new JsonArray();
        foreach (var p in productGroups.OrderByDescending(p => p.TotalRevenue))
        {
            double size = (double)p.AvgPrice / maxAvgPrice * 15.0 + 5.0;
            points.Add(new JsonObject
            {
                ["x"]     = p.TotalUnits,
                ["y"]     = Math.Round((double)p.TotalRevenue, 2),
                ["size"]  = Math.Round(size, 1),
                ["label"] = p.Name,
                ["color"] = (JsonNode?)null,
            });
        }

        var result = new JsonObject
        {
            ["series"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"]   = "Sản phẩm",
                    ["points"] = points,
                }
            },
            ["axes"] = new JsonObject
            {
                ["x"] = new JsonObject { ["label"] = "Số lượng bán",      ["format"] = "number"        },
                ["y"] = new JsonObject { ["label"] = "Doanh thu (VND)", ["format"] = "currency:VND" },
            },
        };

        return result.ToJsonString();
    }
}
