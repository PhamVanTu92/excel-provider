using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ReportingPlatform.ExcelProvider.Database;
using ReportingPlatform.Provider.V1;

namespace ReportingPlatform.ExcelProvider.Operations;

/// <summary>
/// Handler for <c>report.channel.comparison</c>.
/// Returns a <c>pie_chart</c> payload comparing Online vs Cửa hàng revenue.
/// Parameter: period = today | week | month (default: month).
/// </summary>
public sealed class ChannelComparisonHandler : IOperationHandler
{
    private readonly ExcelProviderDb _db;
    private readonly ILogger<ChannelComparisonHandler> _logger;

    public string OperationPattern => "report.channel.comparison";

    public ChannelComparisonHandler(ExcelProviderDb db, ILogger<ChannelComparisonHandler> logger)
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

        var period = "month";
        if (!string.IsNullOrWhiteSpace(request.ParamsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(request.ParamsJson);
                var root = doc.RootElement;

                // Accept period (today/week/month) or explicit date range
                if (root.TryGetProperty("period", out var pp)
                    && !string.IsNullOrWhiteSpace(pp.GetString()))
                    period = pp.GetString()!;
            }
            catch { /* keep default */ }
        }

        var today = DateOnly.FromDateTime(DateTime.Today);
        (DateOnly from, DateOnly to) = period switch
        {
            "today" => (today, today),
            "week"  => (today.AddDays(-6), today),
            _       => (today.AddDays(-29), today),  // "month" = last 30 days
        };

        _logger.LogInformation("ChannelComparison period={Period} [{From},{To}]", period, from, to);

        await reportProgress(35, "Querying sales…");
        var rows = await _db.GetSalesByDateRangeAsync(from, to, ct);

        await reportProgress(75, "Building pie_chart…");

        decimal online = Math.Round(rows.Where(r => r.Channel == "Online").Sum(r => r.Revenue), 2);
        decimal store  = Math.Round(rows.Where(r => r.Channel == "Store").Sum(r => r.Revenue), 2);
        decimal total  = online + store;

        var result = new JsonObject
        {
            ["slices"] = new JsonArray
            {
                new JsonObject { ["label"] = "Online",     ["value"] = (double)online, ["color"] = (JsonNode?)null },
                new JsonObject { ["label"] = "Cửa hàng",  ["value"] = (double)store,  ["color"] = (JsonNode?)null },
            },
            ["total"]       = (double)total,
            ["valueFormat"] = "currency:VND",
        };

        return result.ToJsonString();
    }
}
