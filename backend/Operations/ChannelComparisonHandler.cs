using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ReportingPlatform.ExcelProvider.Database;
using ReportingPlatform.Provider.V1;

namespace ReportingPlatform.ExcelProvider.Operations;

/// <summary>
/// Handler for <c>report.channel.comparison</c>.
/// Compares Online vs Store channel performance for a given date range.
/// </summary>
public sealed class ChannelComparisonHandler : IOperationHandler
{
    private readonly ReportingDb _db;
    private readonly ILogger<ChannelComparisonHandler> _logger;

    public string OperationPattern => "report.channel.comparison";

    public ChannelComparisonHandler(ReportingDb db, ILogger<ChannelComparisonHandler> logger)
    {
        _db     = db;
        _logger = logger;
    }

    public async Task<string> ExecuteAsync(
        OperationRequest request,
        Func<int, string, Task> reportProgress,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogInformation("ChannelComparison starting — requestId={RequestId}", request.RequestId);

        await reportProgress(10, "Parsing parameters…");

        using var doc = JsonDocument.Parse(request.ParamsJson ?? "{}");
        var root      = doc.RootElement;
        var fromDate  = DateOnly.Parse(root.GetProperty("fromDate").GetString()!);
        var toDate    = DateOnly.Parse(root.GetProperty("toDate").GetString()!);

        _logger.LogInformation("ChannelComparison from={From} to={To}", fromDate, toDate);

        await reportProgress(30, $"Querying sales from {fromDate} to {toDate}…");
        var rows = await _db.GetSalesByDateRangeAsync(fromDate, toDate, ct);

        await reportProgress(50, "Aggregating channel totals…");

        // ── Aggregation ────────────────────────────────────────────────────────

        decimal onlineRevenue = rows.Where(r => r.Channel == "Online").Sum(r => r.Revenue);
        int     onlineUnits   = rows.Where(r => r.Channel == "Online").Sum(r => r.Units);
        decimal storeRevenue  = rows.Where(r => r.Channel == "Store").Sum(r => r.Revenue);
        int     storeUnits    = rows.Where(r => r.Channel == "Store").Sum(r => r.Units);

        decimal totalRevenue = onlineRevenue + storeRevenue;
        double  onlinePct    = totalRevenue == 0 ? 0 : Math.Round((double)(onlineRevenue / totalRevenue * 100), 1);
        double  storePct     = totalRevenue == 0 ? 0 : Math.Round((double)(storeRevenue  / totalRevenue * 100), 1);

        await reportProgress(65, "Building daily trend series…");

        // ── Daily trend ────────────────────────────────────────────────────────

        var labels       = new List<string>();
        var onlineSeries = new List<decimal>();
        var storeSeries  = new List<decimal>();

        // Pre-index revenue by (date, channel) for O(n) lookup
        var byDateChannel = rows
            .GroupBy(r => (r.Date, r.Channel))
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Revenue));

        for (var d = fromDate; d <= toDate; d = d.AddDays(1))
        {
            labels.Add(d.ToString("yyyy-MM-dd"));

            byDateChannel.TryGetValue((d, "Online"), out var oRev);
            byDateChannel.TryGetValue((d, "Store"),  out var sRev);
            onlineSeries.Add(Math.Round(oRev, 2));
            storeSeries.Add(Math.Round(sRev, 2));
        }

        await reportProgress(90, "Building response…");

        var result = new
        {
            online = new
            {
                revenue    = Math.Round(onlineRevenue, 2),
                units      = onlineUnits,
                percentage = onlinePct,
            },
            store = new
            {
                revenue    = Math.Round(storeRevenue, 2),
                units      = storeUnits,
                percentage = storePct,
            },
            trend = new
            {
                labels,
                online = onlineSeries,
                store  = storeSeries,
            },
        };

        sw.Stop();
        _logger.LogInformation(
            "ChannelComparison complete — elapsed={Elapsed}ms, rows={Rows}",
            sw.ElapsedMilliseconds, rows.Count);

        return JsonSerializer.Serialize(result);
    }
}
