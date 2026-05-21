using Microsoft.Extensions.Logging;
using ReportingPlatform.ExcelProvider.Database;

namespace ReportingPlatform.ExcelProvider.Management;

// ─── DTOs ─────────────────────────────────────────────────────────────────────

/// <summary>
/// A sales row exposed via the HTTP management API.
/// Uses the database primary key <see cref="Id"/> to identify rows instead of
/// the old Excel row-index approach.
/// </summary>
public sealed record SaleRecord(
    long    Id,
    string  Date,
    string  Region,
    string  Product,
    string  Category,
    decimal Revenue,
    int     Units,
    string  Channel);

public sealed record ProductRecord(
    string  ProductId,
    string  Name,
    string  Category,
    decimal Price,
    int     CurrentStock,
    int     MinStock,
    string  Status);

public sealed record CreateSaleRequest(
    string  Date,
    string  Region,
    string  Product,
    string  Category,
    decimal Revenue,
    int     Units,
    string  Channel);

public sealed record UpdateSaleRequest(
    string?  Region,
    string?  Product,
    string?  Category,
    decimal? Revenue,
    int?     Units,
    string?  Channel);

// ─── Service ──────────────────────────────────────────────────────────────────

/// <summary>
/// Reads and writes the postgres-excel database via <see cref="ExcelProviderDb"/>.
/// NpgsqlDataSource is internally thread-safe; no additional locking is needed.
/// </summary>
public sealed class DataManagementService
{
    private readonly ExcelProviderDb _db;
    private readonly ILogger<DataManagementService> _logger;

    // Operations that should trigger WidgetStale after a data change.
    public static readonly string[] SalesOperations =
    [
        "report.dashboard.summary",
        "report.sales.trend",
        "report.regional.performance",
        "report.channel.comparison",
        "report.top.performers",
        "report.product.detail",
    ];

    public static readonly string[] InventoryOperations =
    [
        "report.dashboard.summary",
        "report.inventory.status",
    ];

    public DataManagementService(
        ExcelProviderDb                  db,
        ILogger<DataManagementService>   logger)
    {
        _db     = db;
        _logger = logger;
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static string StockStatus(int current, int min) =>
        current == 0 ? "Out of Stock"
        : current < min ? "Low Stock"
        : "In Stock";

    private static SaleRecord ToSaleRecord(SaleRow r) =>
        new(r.Id,
            r.Date.ToString("yyyy-MM-dd"),
            r.Region, r.Product, r.Category, r.Revenue, r.Units, r.Channel);

    private static ProductRecord ToProductRecord(ProductRow p) =>
        new(p.ProductId, p.Name, p.Category, p.Price,
            p.CurrentStock, p.MinStock, StockStatus(p.CurrentStock, p.MinStock));

    // ─── Sales: GET ───────────────────────────────────────────────────────────

    public async Task<List<SaleRecord>> GetSalesAsync(
        string?           date,
        string?           region,
        CancellationToken ct = default)
    {
        DateOnly? parsedDate = null;
        if (!string.IsNullOrEmpty(date) && DateOnly.TryParse(date, out var d))
            parsedDate = d;

        var rows = await _db.GetSalesAsync(parsedDate, region, ct);
        _logger.LogInformation(
            "GetSales — date={Date}, region={Region}, returned {Count} rows",
            date, region, rows.Count);
        return rows.Select(ToSaleRecord).ToList();
    }

    // ─── Sales: ADD ───────────────────────────────────────────────────────────

    public async Task<SaleRecord> AddSaleAsync(
        CreateSaleRequest req,
        CancellationToken ct = default)
    {
        ValidateSaleRequest(req.Revenue, req.Units);

        if (!DateOnly.TryParse(req.Date, out var date))
            throw new ArgumentException($"Invalid date format: '{req.Date}'.", nameof(req));

        var newRow = new SaleRow(0, date, req.Region, req.Product, req.Category,
                                 req.Revenue, req.Units, req.Channel);
        var inserted = await _db.AddSaleAsync(newRow, ct);

        _logger.LogInformation("Sale added — id={Id}", inserted.Id);
        return ToSaleRecord(inserted);
    }

    // ─── Sales: UPDATE ────────────────────────────────────────────────────────

    public async Task<SaleRecord> UpdateSaleAsync(
        long              id,
        UpdateSaleRequest req,
        CancellationToken ct = default)
    {
        if (req.Revenue.HasValue) ValidateSaleRequest(req.Revenue.Value, req.Units ?? 0);

        // Fetch existing row first so we can apply partial update
        var existing = await _db.GetSaleByIdAsync(id, ct)
            ?? throw new KeyNotFoundException($"Sale id={id} not found.");

        var updated = new SaleRow(
            id,
            existing.Date,
            req.Region   ?? existing.Region,
            req.Product  ?? existing.Product,
            req.Category ?? existing.Category,
            req.Revenue  ?? existing.Revenue,
            req.Units    ?? existing.Units,
            req.Channel  ?? existing.Channel);

        var result = await _db.UpdateSaleAsync(id, updated, ct)
            ?? throw new KeyNotFoundException($"Sale id={id} not found.");

        _logger.LogInformation("Sale updated — id={Id}", id);
        return ToSaleRecord(result);
    }

    // ─── Sales: DELETE ────────────────────────────────────────────────────────

    public async Task DeleteSaleAsync(long id, CancellationToken ct = default)
    {
        bool deleted = await _db.DeleteSaleAsync(id, ct);
        if (!deleted)
            throw new KeyNotFoundException($"Sale id={id} not found.");
        _logger.LogInformation("Sale deleted — id={Id}", id);
    }

    // ─── Products: GET ────────────────────────────────────────────────────────

    public async Task<List<ProductRecord>> GetProductsAsync(CancellationToken ct = default)
    {
        var rows = await _db.GetProductsAsync(ct);
        _logger.LogInformation("GetProducts — returned {Count} rows", rows.Count);
        return rows.Select(ToProductRecord).ToList();
    }

    // ─── Products: UPDATE STOCK ───────────────────────────────────────────────

    public async Task<ProductRecord> UpdateProductStockAsync(
        string            productId,
        int               newStock,
        CancellationToken ct = default)
    {
        if (newStock < 0)
            throw new ArgumentOutOfRangeException(nameof(newStock), "Stock cannot be negative.");

        var result = await _db.UpdateProductStockAsync(productId, newStock, ct)
            ?? throw new KeyNotFoundException($"Product '{productId}' not found.");

        _logger.LogInformation(
            "Product stock updated — productId={ProductId}, newStock={Stock}",
            productId, newStock);
        return ToProductRecord(result);
    }

    // ─── Validation ───────────────────────────────────────────────────────────

    private static void ValidateSaleRequest(decimal revenue, int units)
    {
        if (revenue <= 0)
            throw new ArgumentOutOfRangeException(nameof(revenue), "Revenue must be greater than 0.");
        if (units < 0)
            throw new ArgumentOutOfRangeException(nameof(units), "Units cannot be negative.");
    }
}
