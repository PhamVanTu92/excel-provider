using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ReportingPlatform.ExcelProvider.Database;
using ReportingPlatform.Provider.V1;

namespace ReportingPlatform.ExcelProvider.Operations;

/// <summary>
/// Handler for <c>report.dashboard.summary</c>.
/// Returns a <c>kpi_grid</c> payload with 6 KPI cards comparing today vs yesterday.
/// </summary>
public sealed class DashboardSummaryHandler : IOperationHandler
{
    private readonly ExcelProviderDb _db;
    private readonly ILogger<DashboardSummaryHandler> _logger;

    public string OperationPattern => "report.dashboard.summary";

    public DashboardSummaryHandler(ExcelProviderDb db, ILogger<DashboardSummaryHandler> logger)
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

        var today = DateOnly.FromDateTime(DateTime.Today);
        if (!string.IsNullOrWhiteSpace(request.ParamsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(request.ParamsJson);
                if (doc.RootElement.TryGetProperty("date", out var dp)
                    && DateOnly.TryParse(dp.GetString(), out var parsed))
                    today = parsed;
            }
            catch { /* fall back to today */ }
        }

        var yesterday = today.AddDays(-1);
        _logger.LogInformation("DashboardSummary date={Date}", today);

        await reportProgress(25, "Querying today + yesterday sales…");
        var todayRows     = await _db.GetSalesByDateAsync(today, ct);
        var yestRows      = await _db.GetSalesByDateAsync(yesterday, ct);

        await reportProgress(50, "Querying 7-day sparkline…");
        var week = await _db.GetSalesByDateRangeAsync(today.AddDays(-6), today, ct);

        await reportProgress(70, "Querying inventory…");
        var products = await _db.GetProductsAsync(ct);

        await reportProgress(85, "Building kpi_grid…");

        // ── Aggregates ────────────────────────────────────────────────────────

        decimal todayRev   = todayRows.Sum(r => r.Revenue);
        int     todayUnits = todayRows.Sum(r => r.Units);
        decimal todayOnline= todayRows.Where(r => r.Channel == "Online").Sum(r => r.Revenue);
        decimal todayStore = todayRows.Where(r => r.Channel == "Store").Sum(r => r.Revenue);

        decimal yestRev    = yestRows.Sum(r => r.Revenue);
        int     yestUnits  = yestRows.Sum(r => r.Units);
        decimal yestOnline = yestRows.Where(r => r.Channel == "Online").Sum(r => r.Revenue);
        decimal yestStore  = yestRows.Where(r => r.Channel == "Store").Sum(r => r.Revenue);

        string topRegion = todayRows
            .GroupBy(r => r.Region)
            .OrderByDescending(g => g.Sum(r => r.Revenue))
            .Select(g => g.Key)
            .FirstOrDefault() ?? "—";

        int alertCount = products.Count(p => p.CurrentStock == 0 || p.CurrentStock < p.MinStock);

        // 7-day revenue sparkline (oldest → newest)
        var sparkArr = new JsonArray();
        for (int i = 6; i >= 0; i--)
        {
            var d = today.AddDays(-i);
            var rev = (double)Math.Round(week.Where(r => r.Date == d).Sum(r => r.Revenue), 2);
            sparkArr.Add(rev);
        }

        // ── Build items ───────────────────────────────────────────────────────

        var items = new JsonArray
        {
            Item("total_revenue",  "Doanh thu hôm nay",   (double)Math.Round(todayRev, 2),   "currency:VND",
                 Cmp(todayRev,   yestRev,   true),  sparkArr, "TrendingUp", "default"),

            Item("total_units",    "Sản phẩm bán ra",     (double)todayUnits,                "number",
                 Cmp(todayUnits, yestUnits, true),   null,     "BarChart2",  "default"),

            Item("online_revenue", "Doanh thu Online",    (double)Math.Round(todayOnline, 2),"currency:VND",
                 Cmp(todayOnline,yestOnline,true),   null,     "Zap",        "default"),

            Item("store_revenue",  "Doanh thu Cửa hàng", (double)Math.Round(todayStore, 2), "currency:VND",
                 Cmp(todayStore, yestStore, true),   null,     "Building2",  "default"),

            Item("top_region",     "Khu vực dẫn đầu",    topRegion,                         "text",
                 null,                                 null,     "MapPin",     "info"),

            Item("stock_alerts",   "Cảnh báo tồn kho",   (double)alertCount,                "number",
                 null,                                 null,     "ShieldAlert",
                 alertCount == 0 ? "success" : alertCount <= 2 ? "warning" : "danger"),
        };

        var result = new JsonObject
        {
            ["columns"] = 3,
            ["items"]   = items,
        };

        return result.ToJsonString();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static JsonObject Item(
        string id, string label, JsonNode value, string format,
        JsonNode? comparison, JsonNode? sparkline, string? icon, string variant) => new()
    {
        ["id"]         = id,
        ["label"]      = label,
        ["value"]      = value,
        ["format"]     = format,
        ["comparison"] = comparison,
        ["sparkline"]  = sparkline,
        ["icon"]       = icon,
        ["variant"]    = variant,
    };

    private static JsonNode? Cmp(decimal today, decimal yesterday, bool upIsGood)
    {
        if (yesterday == 0) return null;
        double delta = Math.Round((double)((today - yesterday) / yesterday * 100), 1);
        bool isUp = delta >= 0;
        return new JsonObject
        {
            ["deltaPercent"] = delta,
            ["direction"]    = isUp ? "up" : "down",
            ["isGood"]       = isUp == upIsGood,
            ["periodLabel"]  = "vs hôm qua",
        };
    }

    private static JsonNode? Cmp(int today, int yesterday, bool upIsGood)
        => Cmp((decimal)today, yesterday, upIsGood);
}
