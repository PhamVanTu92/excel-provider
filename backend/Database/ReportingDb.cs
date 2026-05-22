using Microsoft.Extensions.Logging;
using Npgsql;

namespace ReportingPlatform.ExcelProvider.Database;

/// <summary>
/// Read-only data access for the <c>excel_reporting</c> database.
/// All report handlers query here instead of <see cref="ExcelProviderDb"/>
/// so that report workloads are fully isolated from operational writes.
///
/// Data arrives via PostgreSQL Logical Replication from <c>excel_provider</c>.
/// <see cref="Services.ReplicationListenerService"/> listens for pg_notify events
/// on this database and pushes datasource.updated to HDOS.
/// </summary>
public sealed class ReportingDb
{
    private readonly NpgsqlDataSource _db;
    private readonly ILogger<ReportingDb> _logger;

    // Exposed so ReplicationListenerService can open a dedicated LISTEN connection
    // on the same database without needing a second DI registration.
    internal string ConnectionString { get; }

    public ReportingDb(
        NpgsqlDataSource       db,
        string                 connectionString,
        ILogger<ReportingDb>   logger)
    {
        _db              = db;
        ConnectionString = connectionString;
        _logger          = logger;
    }

    // ─── Schema init ──────────────────────────────────────────────────────────

    /// <summary>
    /// Creates tables and pg_notify triggers on the reporting DB (idempotent).
    /// Safe to call on every restart — uses IF NOT EXISTS / CREATE OR REPLACE.
    /// Does NOT create or manage the logical replication subscription;
    /// run <c>db/reporting/03_create_subscription.sql</c> once for that.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await using var conn = await _db.OpenConnectionAsync(ct);

        // ── Tables ────────────────────────────────────────────────────────────
        // id is BIGINT (not BIGSERIAL) — values are replicated from the source.

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS sales (
                    id          BIGINT         PRIMARY KEY,
                    sale_date   DATE           NOT NULL,
                    region      TEXT           NOT NULL,
                    product     TEXT           NOT NULL,
                    category    TEXT           NOT NULL,
                    revenue     DECIMAL(12,2)  NOT NULL,
                    units       INT            NOT NULL,
                    channel     TEXT           NOT NULL,
                    created_at  TIMESTAMPTZ    DEFAULT NOW(),
                    updated_at  TIMESTAMPTZ    DEFAULT NOW()
                );
                CREATE INDEX IF NOT EXISTS idx_rep_sales_date    ON sales(sale_date);
                CREATE INDEX IF NOT EXISTS idx_rep_sales_region  ON sales(region);
                CREATE INDEX IF NOT EXISTS idx_rep_sales_product ON sales(product);

                CREATE TABLE IF NOT EXISTS products (
                    product_id    TEXT           PRIMARY KEY,
                    name          TEXT           NOT NULL,
                    category      TEXT           NOT NULL,
                    price         DECIMAL(10,2)  NOT NULL,
                    current_stock INT            NOT NULL DEFAULT 0,
                    min_stock     INT            NOT NULL DEFAULT 10,
                    updated_at    TIMESTAMPTZ    DEFAULT NOW()
                );

                CREATE TABLE IF NOT EXISTS regions (
                    region_id       TEXT           PRIMARY KEY,
                    name            TEXT           NOT NULL,
                    manager         TEXT           NOT NULL,
                    monthly_target  DECIMAL(12,2)  NOT NULL,
                    yearly_target   DECIMAL(12,2)  NOT NULL
                );
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // ── pg_notify trigger function ─────────────────────────────────────────
        // Tách riêng vì Npgsql bị nhầm khi parse $$ dollar-quoting
        // trong batch nhiều statement.

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE OR REPLACE FUNCTION notify_reporting_change()
                RETURNS TRIGGER LANGUAGE plpgsql AS $$
                BEGIN
                    PERFORM pg_notify('reporting_data_changed', TG_TABLE_NAME);
                    RETURN NULL;
                END;
                $$
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // ── Triggers (DROP + CREATE — idempotent) ─────────────────────────────

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                DROP TRIGGER IF EXISTS trg_sales_notify    ON sales;
                DROP TRIGGER IF EXISTS trg_products_notify ON products;
                DROP TRIGGER IF EXISTS trg_regions_notify  ON regions;

                CREATE TRIGGER trg_sales_notify
                    AFTER INSERT OR UPDATE OR DELETE ON sales
                    FOR EACH STATEMENT EXECUTE FUNCTION notify_reporting_change();

                CREATE TRIGGER trg_products_notify
                    AFTER INSERT OR UPDATE OR DELETE ON products
                    FOR EACH STATEMENT EXECUTE FUNCTION notify_reporting_change();

                CREATE TRIGGER trg_regions_notify
                    AFTER INSERT OR UPDATE OR DELETE ON regions
                    FOR EACH STATEMENT EXECUTE FUNCTION notify_reporting_change();

                -- Rows arrive here via logical replication APPLY, not local writes.
                -- Default-mode triggers fire only on origin sessions, so they must be
                -- ENABLE ALWAYS to fire on replication apply — otherwise pg_notify never
                -- runs and downstream WidgetStale notifications are never sent.
                ALTER TABLE sales    ENABLE ALWAYS TRIGGER trg_sales_notify;
                ALTER TABLE products ENABLE ALWAYS TRIGGER trg_products_notify;
                ALTER TABLE regions  ENABLE ALWAYS TRIGGER trg_regions_notify;
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // ── Row counts ────────────────────────────────────────────────────────

        long salesCount = 0;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM sales";
            salesCount = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }

        sw.Stop();
        _logger.LogInformation(
            "ReportingDb initialized — salesRows={Count}, elapsed={Elapsed}ms " +
            "(subscription lag is normal during initial replication copy)",
            salesCount, sw.ElapsedMilliseconds);
    }

    // ─── Sales queries (read-only) ────────────────────────────────────────────

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

        var where = conditions.Count > 0
            ? "WHERE " + string.Join(" AND ", conditions)
            : "";

        cmd.CommandText =
            $"SELECT id, sale_date, region, product, category, revenue, units, channel " +
            $"FROM sales {where} ORDER BY sale_date DESC, id DESC";

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<SaleRow>();
        while (await reader.ReadAsync(ct))
            rows.Add(MapSaleRow(reader));
        return rows;
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

    public async Task<List<SaleRow>> GetSalesByDateRangeAsync(
        DateOnly          from,
        DateOnly          to,
        CancellationToken ct)
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

    // ─── Product queries (read-only) ──────────────────────────────────────────

    public async Task<List<ProductRow>> GetProductsAsync(CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText =
            "SELECT product_id, name, category, price, current_stock, min_stock " +
            "FROM products ORDER BY product_id";

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<ProductRow>();
        while (await reader.ReadAsync(ct))
            rows.Add(MapProductRow(reader));
        return rows;
    }

    // ─── Region queries (read-only) ───────────────────────────────────────────

    public async Task<List<RegionRow>> GetRegionsAsync(CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText =
            "SELECT region_id, name, manager, monthly_target, yearly_target " +
            "FROM regions ORDER BY region_id";

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
}
