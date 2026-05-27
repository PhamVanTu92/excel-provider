using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ReportingPlatform.ExcelProvider.Database;
using ReportingPlatform.Provider.V1;

namespace ReportingPlatform.ExcelProvider.Operations;

/// <summary>
/// Handler for <c>report.product.detail</c>.
/// Returns a <c>simple_table</c> payload with product-level sales summary for the last 30 days.
/// Optional parameter: productName — filter to a single product's regional breakdown.
/// </summary>
public sealed class ProductDetailHandler : IOperationHandler
{
    private readonly ExcelProviderDb _db;
    private readonly ILogger<ProductDetailHandler> _logger;

    public string OperationPattern => "report.product.detail";

    public ProductDetailHandler(ExcelProviderDb db, ILogger<ProductDetailHandler> logger)
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

        string? productName = null;
        var today    = DateOnly.FromDateTime(DateTime.Today);
        var fromDate = today.AddDays(-29);
        var toDate   = today;

        if (!string.IsNullOrWhiteSpace(request.ParamsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(request.ParamsJson);
                var root = doc.RootElement;

                if (root.TryGetProperty("productName", out var pn)
                    && !string.IsNullOrWhiteSpace(pn.GetString()))
                    productName = pn.GetString();

                if (root.TryGetProperty("fromDate", out var fp)
                    && DateOnly.TryParse(fp.GetString(), out var pfd)) fromDate = pfd;

                if (root.TryGetProperty("toDate", out var tp)
                    && DateOnly.TryParse(tp.GetString(), out var ptd)) toDate = ptd;
            }
            catch { /* keep defaults */ }
        }

        _logger.LogInformation("ProductDetail product={Name} [{From},{To}]", productName ?? "all", fromDate, toDate);

        await reportProgress(30, "Querying sales data…");
        var allRows = await _db.GetSalesByDateRangeAsync(fromDate, toDate, ct);

        await reportProgress(60, "Querying inventory…");
        var products = await _db.GetProductsAsync(ct);
        var stockMap = products.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

        await reportProgress(80, "Building simple_table…");

        // ── Regional breakdown when a product is specified ────────────────────
        if (!string.IsNullOrWhiteSpace(productName))
        {
            var productRows = allRows
                .Where(r => string.Equals(r.Product, productName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            int dayCount = toDate.DayNumber - fromDate.DayNumber + 1;

            var byRegion = productRows
                .GroupBy(r => r.Region)
                .Select(g =>
                {
                    decimal rev   = Math.Round(g.Sum(r => r.Revenue), 2);
                    int     units = g.Sum(r => r.Units);
                    decimal avg   = units > 0 ? Math.Round(rev / units, 2) : 0m;
                    return (Region: g.Key, Revenue: rev, Units: units, AvgPrice: avg);
                })
                .OrderByDescending(x => x.Revenue)
                .ToList();

            var dataRows = new JsonArray();
            foreach (var row in byRegion)
            {
                dataRows.Add(new JsonObject
                {
                    ["region"]   = row.Region,
                    ["revenue"]  = (double)row.Revenue,
                    ["units"]    = row.Units,
                    ["avgPrice"] = (double)row.AvgPrice,
                });
            }

            var result = new JsonObject
            {
                ["columns"] = new JsonArray
                {
                    Col("region",   "Khu vực",      "string",   null,             align: "left"),
                    Col("revenue",  "Doanh thu",     "number",   "currency:VND",   align: "right"),
                    Col("units",    "Số lượng",      "number",   null,             align: "right"),
                    Col("avgPrice", "Giá trung bình","number",   "currency:VND",   align: "right"),
                },
                ["rows"]       = dataRows,
                ["pagination"] = new JsonObject
                {
                    ["mode"]      = "client",
                    ["totalRows"] = byRegion.Count,
                },
            };
            return result.ToJsonString();
        }

        // ── Product summary table (default — no filter) ────────────────────────

        var productSummaries = allRows
            .GroupBy(r => r.Product)
            .Select(g =>
            {
                string cat   = g.First().Category;
                decimal rev  = Math.Round(g.Sum(r => r.Revenue), 2);
                int units    = g.Sum(r => r.Units);
                int stock    = stockMap.TryGetValue(g.Key, out var p) ? p.CurrentStock : -1;
                return (Product: g.Key, Category: cat, Revenue: rev, Units: units, Stock: stock);
            })
            .OrderByDescending(x => x.Revenue)
            .ToList();

        var summaryRows = new JsonArray();
        foreach (var row in productSummaries)
        {
            summaryRows.Add(new JsonObject
            {
                ["product"]  = row.Product,
                ["category"] = row.Category,
                ["revenue"]  = (double)row.Revenue,
                ["units"]    = row.Units,
                ["stock"]    = row.Stock >= 0 ? (JsonNode)row.Stock : (JsonNode?)null,
            });
        }

        var summary = new JsonObject
        {
            ["columns"] = new JsonArray
            {
                Col("product",  "Sản phẩm",  "string", null,           align: "left"),
                Col("category", "Danh mục",  "string", null,           align: "left"),
                Col("revenue",  "Doanh thu", "number", "currency:VND", align: "right"),
                Col("units",    "Số lượng",  "number", null,           align: "right"),
                Col("stock",    "Tồn kho",   "number", null,           align: "right"),
            },
            ["rows"]       = summaryRows,
            ["pagination"] = new JsonObject
            {
                ["mode"]      = "client",
                ["totalRows"] = productSummaries.Count,
            },
        };

        return summary.ToJsonString();
    }

    // ── Helper ─────────────────────────────────────────────────────────────────

    private static JsonObject Col(string key, string label, string type,
        string? format, string align = "left") => new()
    {
        ["key"]        = key,
        ["label"]      = label,
        ["type"]       = type,
        ["sortable"]   = true,
        ["filterable"] = false,
        ["format"]     = format,
        ["visible"]    = true,
        ["align"]      = align,
    };
}
