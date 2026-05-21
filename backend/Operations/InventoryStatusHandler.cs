using System.Text.Json;
using Microsoft.Extensions.Logging;
using ReportingPlatform.ExcelProvider.Database;
using ReportingPlatform.Provider.V1;

namespace ReportingPlatform.ExcelProvider.Operations;

/// <summary>
/// Handler for <c>report.inventory.status</c>.
/// Reads the products table and classifies each product as ok / low / out.
/// </summary>
public sealed class InventoryStatusHandler : IOperationHandler
{
    private readonly ReportingDb _db;
    private readonly ILogger<InventoryStatusHandler> _logger;

    public string OperationPattern => "report.inventory.status";

    public InventoryStatusHandler(ReportingDb db, ILogger<InventoryStatusHandler> logger)
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

        await reportProgress(70, "Classifying stock levels…");

        var productList = products.Select(p =>
        {
            string status = p.CurrentStock == 0        ? "out"
                          : p.CurrentStock < p.MinStock ? "low"
                          :                               "ok";
            return new
            {
                name     = p.Name,
                category = p.Category,
                stock    = p.CurrentStock,
                status,
            };
        }).ToList();

        int okCount  = productList.Count(p => p.status == "ok");
        int lowCount = productList.Count(p => p.status == "low");
        int outCount = productList.Count(p => p.status == "out");

        await reportProgress(90, "Building response…");

        var result = new
        {
            products = productList,
            summary  = new { ok = okCount, low = lowCount, @out = outCount },
        };

        return JsonSerializer.Serialize(result);
    }
}
