using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ReportingPlatform.ExcelProvider.Database;
using ReportingPlatform.Provider.V1;

namespace ReportingPlatform.ExcelProvider.Operations;

/// <summary>
/// Handler for <c>report.sales.timeline</c>.
/// Returns a <c>timeline_vertical</c> payload — top sales events from the last 7 days.
/// Parameters: limit (default 10, max 20).
/// </summary>
public sealed class SalesTimelineHandler : IOperationHandler
{
    private readonly ExcelProviderDb _db;
    private readonly ILogger<SalesTimelineHandler> _logger;

    public string OperationPattern => "report.sales.timeline";

    public SalesTimelineHandler(ExcelProviderDb db, ILogger<SalesTimelineHandler> logger)
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

        var today    = DateOnly.FromDateTime(DateTime.Today);
        var fromDate = today.AddDays(-6);
        int limit    = 10;

        if (!string.IsNullOrWhiteSpace(request.ParamsJson))
        {
            try
            {
                using var doc  = JsonDocument.Parse(request.ParamsJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("limit", out var lp) && lp.TryGetInt32(out var lv))
                    limit = Math.Clamp(lv, 1, 20);
            }
            catch { /* keep defaults */ }
        }

        _logger.LogInformation("SalesTimeline from={From} to={To} limit={Limit}", fromDate, today, limit);

        await reportProgress(30, "Querying sales…");
        var rows = await _db.GetSalesByDateRangeAsync(fromDate, today, ct);

        await reportProgress(70, "Building timeline…");

        var topRows = rows
            .OrderByDescending(r => r.Revenue)
            .Take(limit)
            .ToList();

        // Sort chronologically for display (most recent first for timeline feel)
        topRows = topRows.OrderByDescending(r => r.Date).ThenByDescending(r => r.Id).ToList();

        bool firstTodayAssigned = false;
        var items = new JsonArray();

        for (int i = 0; i < topRows.Count; i++)
        {
            var row = topRows[i];

            // Derive approximate time from row Id (seconds within day)
            long secondsInDay = row.Id % 86400;
            var time = TimeSpan.FromSeconds(secondsInDay);
            string timeLabel = $"{time.Hours:D2}:{time.Minutes:D2}";

            string isoTime = row.Date.ToDateTime(new TimeOnly(time.Hours, time.Minutes)).ToString("yyyy-MM-ddTHH:mm:ssZ");

            string status;
            if (row.Date == today && !firstTodayAssigned)
            {
                status = "current";
                firstTodayAssigned = true;
            }
            else if (row.Date < today)
            {
                status = "done";
            }
            else
            {
                status = "done";
            }

            items.Add(new JsonObject
            {
                ["id"]        = row.Id.ToString(),
                ["timeLabel"] = timeLabel,
                ["isoTime"]   = isoTime,
                ["title"]     = $"Đơn hàng: {row.Product} × {row.Units}",
                ["subtitle"]  = $"{row.Region} • {row.Channel} • {row.Revenue:N0} VND",
                ["status"]    = status,
                ["actor"]     = (JsonNode?)null,
                ["note"]      = (JsonNode?)null,
            });
        }

        var result = new JsonObject
        {
            ["items"]    = items,
            ["showTime"] = true,
        };

        return result.ToJsonString();
    }
}
