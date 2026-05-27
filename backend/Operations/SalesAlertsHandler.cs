using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ReportingPlatform.ExcelProvider.Database;
using ReportingPlatform.Provider.V1;

namespace ReportingPlatform.ExcelProvider.Operations;

/// <summary>
/// Handler for <c>report.sales.alerts</c>.
/// Returns an <c>alert_list</c> payload derived dynamically from DB state (no alert table).
/// Alert sources: out-of-stock, low stock, region underperformance, no sales today, channel imbalance.
/// </summary>
public sealed class SalesAlertsHandler : IOperationHandler
{
    private readonly ExcelProviderDb _db;
    private readonly ILogger<SalesAlertsHandler> _logger;

    public string OperationPattern => "report.sales.alerts";

    public SalesAlertsHandler(ExcelProviderDb db, ILogger<SalesAlertsHandler> logger)
    {
        _db     = db;
        _logger = logger;
    }

    public async Task<string> ExecuteAsync(
        OperationRequest request,
        Func<int, string, Task> reportProgress,
        CancellationToken ct)
    {
        await reportProgress(10, "Loading data…");

        var today    = DateOnly.FromDateTime(DateTime.Today);
        var week7ago = today.AddDays(-6);

        var products = await _db.GetProductsAsync(ct);
        await reportProgress(25, "Querying regional targets…");
        var regions  = await _db.GetRegionsAsync(ct);

        await reportProgress(40, "Querying current-month sales…");
        var firstDay       = new DateOnly(today.Year, today.Month, 1);
        var monthSales     = await _db.GetSalesByDateRangeAsync(firstDay, today, ct);

        await reportProgress(55, "Querying today's sales…");
        var todaySales     = await _db.GetSalesByDateAsync(today, ct);

        await reportProgress(65, "Querying 7-day sales…");
        var week7Sales     = await _db.GetSalesByDateRangeAsync(week7ago, today, ct);

        await reportProgress(80, "Building alerts…");

        var alerts = new List<(int level, string id, string title, string subtitle)>();
        int seq = 1;

        // Rule 1: Out-of-stock products → level 1 (critical)
        foreach (var p in products.Where(p => p.CurrentStock == 0))
        {
            alerts.Add((1, $"a-{seq++:D3}",
                $"Hết hàng: {p.Name}",
                $"{p.Category} • Cần nhập hàng ngay"));
        }

        // Rule 2: Low stock (0 < current < min) → level 2
        foreach (var p in products.Where(p => p.CurrentStock > 0 && p.CurrentStock < p.MinStock))
        {
            alerts.Add((2, $"a-{seq++:D3}",
                $"Tồn kho thấp: {p.Name}",
                $"{p.CurrentStock}/{p.MinStock} • dưới mức tối thiểu"));
        }

        // Rule 3: Regions where month revenue < 50% of pro-rated target → level 2
        int daysInMonth   = DateTime.DaysInMonth(today.Year, today.Month);
        int daysPassed    = today.Day;
        double dayRatio   = (double)daysPassed / daysInMonth;

        var regionRevenue = monthSales
            .GroupBy(r => r.Region)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Revenue));

        foreach (var region in regions)
        {
            decimal actual   = regionRevenue.GetValueOrDefault(region.Name, 0m);
            decimal expected = region.MonthlyTarget * (decimal)dayRatio;
            if (expected > 0 && actual < expected * 0.5m)
            {
                double pct = Math.Round((double)(actual / expected * 100), 0);
                alerts.Add((2, $"a-{seq++:D3}",
                    $"Dưới mục tiêu: {region.Name}",
                    $"Thực tế {actual:N0} / Kỳ vọng {expected:N0} ({pct:N0}%)"));
            }
        }

        // Rule 4: No sales today → level 2
        if (!todaySales.Any())
        {
            alerts.Add((2, $"a-{seq++:D3}",
                "Chưa có đơn hàng hôm nay",
                $"Ngày {today:dd/MM/yyyy}"));
        }

        // Rule 5: Channel imbalance — any channel < 20% of 7-day total → level 3
        if (week7Sales.Any())
        {
            decimal total7 = week7Sales.Sum(r => r.Revenue);
            var byChannel = week7Sales
                .GroupBy(r => r.Channel)
                .ToDictionary(g => g.Key, g => g.Sum(r => r.Revenue));

            foreach (var (channel, rev) in byChannel)
            {
                double pct = Math.Round((double)(rev / total7 * 100), 1);
                if (pct < 20.0)
                {
                    alerts.Add((3, $"a-{seq++:D3}",
                        $"Kênh {channel} suy giảm",
                        $"{pct:N0}% tổng doanh thu 7 ngày qua"));
                }
            }
        }

        // Sort: level ascending (critical first), then by insertion order
        var sorted = alerts.OrderBy(a => a.level).ToList();

        var now      = DateTimeOffset.UtcNow;
        var alertArr = new JsonArray();

        for (int i = 0; i < sorted.Count && i < 20; i++)
        {
            var (level, id, title, subtitle) = sorted[i];
            var alertTime  = now.AddMinutes(-i * 5);
            string timeStr = alertTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
            string timeLabel = alertTime.ToString("HH:mm");

            alertArr.Add(new JsonObject
            {
                ["id"]             = id,
                ["level"]          = level,
                ["title"]          = title,
                ["subtitle"]       = subtitle,
                ["time"]           = timeStr,
                ["timeLabel"]      = timeLabel,
                ["runbookId"]      = (JsonNode?)null,
                ["acknowledged"]   = false,
                ["acknowledgedBy"] = (JsonNode?)null,
            });
        }

        int totalUnack = Math.Min(sorted.Count, 20);

        var result = new JsonObject
        {
            ["alerts"]             = alertArr,
            ["totalUnacknowledged"] = totalUnack,
            ["maxDisplay"]         = 20,
        };

        return result.ToJsonString();
    }
}
