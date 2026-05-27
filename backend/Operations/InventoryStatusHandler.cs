using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ReportingPlatform.ExcelProvider.Database;
using ReportingPlatform.Provider.V1;

namespace ReportingPlatform.ExcelProvider.Operations;

/// <summary>
/// Handler for <c>report.inventory.status</c>.
/// Returns a <c>progress_rows</c> payload showing each product's stock level as a progress bar.
/// </summary>
public sealed class InventoryStatusHandler : IOperationHandler
{
    private readonly ExcelProviderDb _db;
    private readonly ILogger<InventoryStatusHandler> _logger;

    public string OperationPattern => "report.inventory.status";

    public InventoryStatusHandler(ExcelProviderDb db, ILogger<InventoryStatusHandler> logger)
    {
        _db     = db;
        _logger = logger;
    }

    public async Task<string> ExecuteAsync(
        OperationRequest request,
        Func<int, string, Task> reportProgress,
        CancellationToken ct)
    {
        await reportProgress(20, "Querying product data…");
        var products = await _db.GetProductsAsync(ct);

        _logger.LogInformation("InventoryStatus for {Count} products", products.Count);
        await reportProgress(70, "Building progress_rows…");

        // Standard thresholds (same for every product)
        static JsonArray DefaultThresholds() => new()
        {
            new JsonObject { ["from"] = 0,  ["to"] = 30,  ["color"] = "danger"  },
            new JsonObject { ["from"] = 30, ["to"] = 70,  ["color"] = "warning" },
            new JsonObject { ["from"] = 70, ["to"] = 101, ["color"] = "success" },
        };

        var rows = new JsonArray();
        foreach (var p in products)
        {
            // Capacity: 5× the minimum restock threshold, or current if higher
            int max     = Math.Max(p.CurrentStock, p.MinStock * 5);
            double pct  = max > 0 ? Math.Round(p.CurrentStock / (double)max * 100, 1) : 0;

            (string badge, string badgeVariant) = p.CurrentStock == 0      ? ("Hết hàng",       "danger")
                                                : pct < 30                 ? ("Rất thấp",        "danger")
                                                : p.CurrentStock < p.MinStock ? ("Tồn kho thấp", "warning")
                                                : pct < 70                 ? ("Bình thường",     "warning")
                                                :                             ("Đủ hàng",        "success");

            rows.Add(new JsonObject
            {
                ["id"]              = p.ProductId,
                ["label"]           = p.Name,
                ["sublabel"]        = $"{p.Category} • {p.CurrentStock}/{max} sản phẩm",
                ["current"]         = p.CurrentStock,
                ["max"]             = max,
                ["percent"]         = pct,
                ["colorThresholds"] = DefaultThresholds(),
                ["badge"]           = badge,
                ["badgeVariant"]    = badgeVariant,
            });
        }

        var result = new JsonObject
        {
            ["rows"]        = rows,
            ["showPercent"] = true,
            ["showValues"]  = true,
        };

        return result.ToJsonString();
    }
}
