using Microsoft.Extensions.Logging;
using Npgsql;

namespace ReportingPlatform.ExcelProvider.Database;

// ─── Domain records ───────────────────────────────────────────────────────────

public sealed record SaleRow(
    long    Id,
    DateOnly Date,
    string  Region,
    string  Product,
    string  Category,
    decimal Revenue,
    int     Units,
    string  Channel);

public sealed record ProductRow(
    string  ProductId,
    string  Name,
    string  Category,
    decimal Price,
    int     CurrentStock,
    int     MinStock);

public sealed record RegionRow(
    string  RegionId,
    string  Name,
    string  Manager,
    decimal MonthlyTarget,
    decimal YearlyTarget);

// ─── Database access class ────────────────────────────────────────────────────

/// <summary>
/// Wraps NpgsqlDataSource and provides all SQL query methods used by the
/// Excel.Provider service. NpgsqlDataSource is thread-safe; no additional locking needed.
/// </summary>
public sealed class ExcelProviderDb
{
    private readonly NpgsqlDataSource _db;
    private readonly ILogger<ExcelProviderDb> _logger;

    private static readonly string[] Regions    = ["North", "South", "East", "West", "Central"];
    private static readonly string[] Products   = ["Laptop Pro", "Wireless Mouse", "USB Hub", "Monitor 24\"", "Keyboard MX", "Webcam HD", "SSD 1TB", "RAM 16GB", "Headset Pro", "Desk Lamp"];
    private static readonly string[] Categories = ["Electronics", "Peripherals", "Peripherals", "Electronics", "Peripherals", "Peripherals", "Storage", "Storage", "Peripherals", "Electronics"];
    private static readonly decimal[] BasePrices = [1200m, 45m, 35m, 350m, 120m, 80m, 95m, 75m, 150m, 25m];

    public ExcelProviderDb(NpgsqlDataSource db, ILogger<ExcelProviderDb> logger)
    {
        _db     = db;
        _logger = logger;
    }

    // ─── Schema init + seed ───────────────────────────────────────────────────

    /// <summary>
    /// Creates tables (if not exist) and seeds sample data if all tables are empty.
    /// Safe to call on every restart.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await using var conn = await _db.OpenConnectionAsync(ct);

        // ── Create tables ─────────────────────────────────────────────────────
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS sales (
                    id          BIGSERIAL PRIMARY KEY,
                    sale_date   DATE           NOT NULL,
                    region      TEXT           NOT NULL,
                    product     TEXT           NOT NULL,
                    category    TEXT           NOT NULL,
                    revenue     DECIMAL(12,2)  NOT NULL CHECK (revenue > 0),
                    units       INT            NOT NULL CHECK (units >= 0),
                    channel     TEXT           NOT NULL CHECK (channel IN ('Online','Store')),
                    created_at  TIMESTAMPTZ    DEFAULT NOW(),
                    updated_at  TIMESTAMPTZ    DEFAULT NOW()
                );
                CREATE INDEX IF NOT EXISTS idx_sales_date   ON sales(sale_date);
                CREATE INDEX IF NOT EXISTS idx_sales_region ON sales(region);

                CREATE TABLE IF NOT EXISTS products (
                    product_id    TEXT PRIMARY KEY,
                    name          TEXT           NOT NULL,
                    category      TEXT           NOT NULL,
                    price         DECIMAL(10,2)  NOT NULL,
                    current_stock INT            NOT NULL DEFAULT 0,
                    min_stock     INT            NOT NULL DEFAULT 10,
                    updated_at    TIMESTAMPTZ    DEFAULT NOW()
                );

                CREATE TABLE IF NOT EXISTS regions (
                    region_id       TEXT PRIMARY KEY,
                    name            TEXT           NOT NULL,
                    manager         TEXT           NOT NULL,
                    monthly_target  DECIMAL(12,2)  NOT NULL,
                    yearly_target   DECIMAL(12,2)  NOT NULL
                );
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // ── Seed if empty ─────────────────────────────────────────────────────
        long salesCount;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM sales";
            salesCount = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }

        if (salesCount == 0)
        {
            _logger.LogInformation("Tables empty — seeding sample data…");
            await SeedDataAsync(conn, ct);
        }

        long finalCount;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM sales";
            finalCount = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }

        sw.Stop();
        _logger.LogInformation(
            "ExcelProvider DB initialized with {Count} sales records — elapsed={Elapsed}ms",
            finalCount, sw.ElapsedMilliseconds);
    }

    // ─── Sales queries ────────────────────────────────────────────────────────

    public async Task<List<SaleRow>> GetSalesAsync(
        DateOnly?         date,
        string?           region,
        CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();

        var conditions = new List<string>();
        if (date.HasValue)
        {
            conditions.Add("sale_date = @date");
            cmd.Parameters.AddWithValue("@date", date.Value);
        }
        if (!string.IsNullOrEmpty(region))
        {
            conditions.Add("region ILIKE @region");
            cmd.Parameters.AddWithValue("@region", region);
        }

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        cmd.CommandText = $"SELECT id, sale_date, region, product, category, revenue, units, channel FROM sales {where} ORDER BY sale_date DESC, id DESC";

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<SaleRow>();
        while (await reader.ReadAsync(ct))
            rows.Add(MapSaleRow(reader));
        return rows;
    }

    public async Task<SaleRow> AddSaleAsync(SaleRow row, CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();

        cmd.CommandText = """
            INSERT INTO sales (sale_date, region, product, category, revenue, units, channel)
            VALUES (@date, @region, @product, @category, @revenue, @units, @channel)
            RETURNING id, sale_date, region, product, category, revenue, units, channel
            """;
        cmd.Parameters.AddWithValue("@date",     row.Date);
        cmd.Parameters.AddWithValue("@region",   row.Region);
        cmd.Parameters.AddWithValue("@product",  row.Product);
        cmd.Parameters.AddWithValue("@category", row.Category);
        cmd.Parameters.AddWithValue("@revenue",  row.Revenue);
        cmd.Parameters.AddWithValue("@units",    row.Units);
        cmd.Parameters.AddWithValue("@channel",  row.Channel);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return MapSaleRow(reader);
    }

    public async Task<SaleRow?> UpdateSaleAsync(long id, SaleRow row, CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();

        cmd.CommandText = """
            UPDATE sales
            SET region   = @region,
                product  = @product,
                category = @category,
                revenue  = @revenue,
                units    = @units,
                channel  = @channel,
                updated_at = NOW()
            WHERE id = @id
            RETURNING id, sale_date, region, product, category, revenue, units, channel
            """;
        cmd.Parameters.AddWithValue("@id",       id);
        cmd.Parameters.AddWithValue("@region",   row.Region);
        cmd.Parameters.AddWithValue("@product",  row.Product);
        cmd.Parameters.AddWithValue("@category", row.Category);
        cmd.Parameters.AddWithValue("@revenue",  row.Revenue);
        cmd.Parameters.AddWithValue("@units",    row.Units);
        cmd.Parameters.AddWithValue("@channel",  row.Channel);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return MapSaleRow(reader);
    }

    public async Task<bool> DeleteSaleAsync(long id, CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM sales WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    // ─── Product queries ──────────────────────────────────────────────────────

    public async Task<List<ProductRow>> GetProductsAsync(CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT product_id, name, category, price, current_stock, min_stock FROM products ORDER BY product_id";

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<ProductRow>();
        while (await reader.ReadAsync(ct))
            rows.Add(MapProductRow(reader));
        return rows;
    }

    public async Task<ProductRow?> UpdateProductStockAsync(string productId, int newStock, CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();

        cmd.CommandText = """
            UPDATE products
            SET current_stock = @stock, updated_at = NOW()
            WHERE product_id = @id
            RETURNING product_id, name, category, price, current_stock, min_stock
            """;
        cmd.Parameters.AddWithValue("@id",    productId);
        cmd.Parameters.AddWithValue("@stock", newStock);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return MapProductRow(reader);
    }

    // ─── Region queries ───────────────────────────────────────────────────────

    public async Task<List<RegionRow>> GetRegionsAsync(CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT region_id, name, manager, monthly_target, yearly_target FROM regions ORDER BY region_id";

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<RegionRow>();
        while (await reader.ReadAsync(ct))
            rows.Add(new RegionRow(
                RegionId:      reader.GetString(0),
                Name:          reader.GetString(1),
                Manager:       reader.GetString(2),
                MonthlyTarget: reader.GetDecimal(3),
                YearlyTarget:  reader.GetDecimal(4)));
        return rows;
    }

    // ─── Report queries ───────────────────────────────────────────────────────

    public async Task<List<SaleRow>> GetSalesByDateRangeAsync(
        DateOnly from, DateOnly to, CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();

        cmd.CommandText = """
            SELECT id, sale_date, region, product, category, revenue, units, channel
            FROM sales
            WHERE sale_date >= @from AND sale_date <= @to
            ORDER BY sale_date, id
            """;
        cmd.Parameters.AddWithValue("@from", from);
        cmd.Parameters.AddWithValue("@to",   to);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<SaleRow>();
        while (await reader.ReadAsync(ct))
            rows.Add(MapSaleRow(reader));
        return rows;
    }

    public async Task<SaleRow?> GetSaleByIdAsync(long id, CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT id, sale_date, region, product, category, revenue, units, channel FROM sales WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return MapSaleRow(reader);
    }

    public async Task<List<SaleRow>> GetSalesByDateAsync(DateOnly date, CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();

        cmd.CommandText = """
            SELECT id, sale_date, region, product, category, revenue, units, channel
            FROM sales
            WHERE sale_date = @date
            ORDER BY id
            """;
        cmd.Parameters.AddWithValue("@date", date);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<SaleRow>();
        while (await reader.ReadAsync(ct))
            rows.Add(MapSaleRow(reader));
        return rows;
    }

    // ─── Mapping helpers ──────────────────────────────────────────────────────

    private static SaleRow MapSaleRow(NpgsqlDataReader r) =>
        new(
            Id:       r.GetInt64(0),
            Date:     r.GetFieldValue<DateOnly>(1),
            Region:   r.GetString(2),
            Product:  r.GetString(3),
            Category: r.GetString(4),
            Revenue:  r.GetDecimal(5),
            Units:    r.GetInt32(6),
            Channel:  r.GetString(7));

    private static ProductRow MapProductRow(NpgsqlDataReader r) =>
        new(
            ProductId:    r.GetString(0),
            Name:         r.GetString(1),
            Category:     r.GetString(2),
            Price:        r.GetDecimal(3),
            CurrentStock: r.GetInt32(4),
            MinStock:     r.GetInt32(5));

    // ─── Seed helpers ─────────────────────────────────────────────────────────

    private async Task SeedDataAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        // Regions
        string[] managers       = ["Alice Nguyen", "Bob Tran", "Carol Le", "David Pham", "Eve Vo"];
        decimal[] monthlyTarget = [80_000m, 60_000m, 70_000m, 55_000m, 65_000m];

        for (int i = 0; i < Regions.Length; i++)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO regions (region_id, name, manager, monthly_target, yearly_target)
                VALUES (@rid, @name, @mgr, @mt, @yt)
                ON CONFLICT (region_id) DO NOTHING
                """;
            cmd.Parameters.AddWithValue("@rid",  $"R{i + 1:D2}");
            cmd.Parameters.AddWithValue("@name", Regions[i]);
            cmd.Parameters.AddWithValue("@mgr",  managers[i]);
            cmd.Parameters.AddWithValue("@mt",   monthlyTarget[i]);
            cmd.Parameters.AddWithValue("@yt",   monthlyTarget[i] * 12);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Products — stock levels: some intentionally low/out
        int[] currentStock = [45, 3, 0, 22, 8, 0, 60, 12, 2, 100];
        int[] minStock      = [20, 10, 5, 10, 10, 5, 30, 20, 10, 50];

        for (int i = 0; i < Products.Length; i++)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO products (product_id, name, category, price, current_stock, min_stock)
                VALUES (@pid, @name, @cat, @price, @stock, @min)
                ON CONFLICT (product_id) DO NOTHING
                """;
            cmd.Parameters.AddWithValue("@pid",   $"P{i + 1:D3}");
            cmd.Parameters.AddWithValue("@name",  Products[i]);
            cmd.Parameters.AddWithValue("@cat",   Categories[i]);
            cmd.Parameters.AddWithValue("@price", BasePrices[i]);
            cmd.Parameters.AddWithValue("@stock", currentStock[i]);
            cmd.Parameters.AddWithValue("@min",   minStock[i]);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Sales — 180 days, ~6 rows/day, trending upward, weekdays slightly higher
        var rng   = new Random(42);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var channels = new[] { "Online", "Store" };

        for (int dayOffset = 179; dayOffset >= 0; dayOffset--)
        {
            var date = today.AddDays(-dayOffset);
            bool isWeekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            int rowsToday  = isWeekend ? rng.Next(3, 6) : rng.Next(5, 9);

            for (int r = 0; r < rowsToday; r++)
            {
                int     prodIdx   = rng.Next(Products.Length);
                int     regionIdx = rng.Next(Regions.Length);
                int     channel   = rng.Next(channels.Length);
                int     units     = rng.Next(1, 20);
                double  trendMult = 0.7 + 0.3 * (1.0 - dayOffset / 180.0);
                decimal revenue   = Math.Round(
                    BasePrices[prodIdx] * units * (decimal)(trendMult + rng.NextDouble() * 0.4 - 0.2), 2);
                if (revenue <= 0) revenue = BasePrices[prodIdx];

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO sales (sale_date, region, product, category, revenue, units, channel)
                    VALUES (@date, @region, @product, @category, @revenue, @units, @channel)
                    """;
                cmd.Parameters.AddWithValue("@date",     date);
                cmd.Parameters.AddWithValue("@region",   Regions[regionIdx]);
                cmd.Parameters.AddWithValue("@product",  Products[prodIdx]);
                cmd.Parameters.AddWithValue("@category", Categories[prodIdx]);
                cmd.Parameters.AddWithValue("@revenue",  revenue);
                cmd.Parameters.AddWithValue("@units",    units);
                cmd.Parameters.AddWithValue("@channel",  channels[channel]);
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }

        _logger.LogInformation("Sample data seeded — {Regions} regions, {Products} products, {Days} days of sales",
            Regions.Length, Products.Length, 180);
    }
}
