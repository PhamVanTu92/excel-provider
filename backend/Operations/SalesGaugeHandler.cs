using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ReportingPlatform.ExcelProvider.Database;
using ReportingPlatform.Provider.V1;

namespace ReportingPlatform.ExcelProvider.Operations;

/// <summary>
/// Handler for <c>report.sales.gauge</c>.
/// Returns a <c>gauge</c> payload showing current-month revenue achievement vs regional monthly targets.
/// </summary>
public sealed class SalesGaugeHandler : IOperationHandler
{
    private readonly ExcelProviderDb _db;
    private readonly ILogger<SalesGaugeHandler> _logger;

    public string OperationPattern => "report.sales.gauge";

    public SalesGaugeHandler(ExcelProviderDb db, ILogger<SalesGaugeHandler> logger)
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
        var firstDay = new DateOnly(today.Year, today.Month, 1);

        _logger.LogInformation("SalesGauge from={From} to={To}", firstDay, today);

        await reportProgress(30, "Querying current-month sales…");
        var salesRows = await _db.GetSalesByDateRangeAsync(firstDay, today, ct);

        await reportProgress(60, "Querying regional targets…");
        var regions = await _db.GetRegionsAsync(ct);

        await reportProgress(80, "Computing achievement…");

        decimal actualRevenue = salesRows.Sum(r => r.Revenue);
        decimal totalTarget   = regions.Sum(r => r.MonthlyTarget);

        double achievement = totalTarget > 0
            ? Math.Round((double)(actualRevenue / totalTarget * 100), 1)
            : 0.0;

        // Display value is capped at 130 for rendering, but we store the real value
        double displayValue = Math.Min(achievement, 130.0);

        var thresholds = new JsonArray
        {
            new JsonObject { ["from"] = 0,   ["to"] = 60,  ["color"] = "danger",  ["label"] = "Cần cải thiện"  },
            new JsonObject { ["from"] = 60,  ["to"] = 80,  ["color"] = "warning", ["label"] = "Đang tiến triển" },
            new JsonObject { ["from"] = 80,  ["to"] = 100, ["color"] = "success", ["label"] = "Đạt mục tiêu"   },
            new JsonObject { ["from"] = 100, ["to"] = 130, ["color"] = "info",    ["label"] = "Vượt mục tiêu"  },
        };

        var result = new JsonObject
        {
            ["value"]      = displayValue,
            ["min"]        = 0,
            ["max"]        = 130,
            ["unit"]       = "%",
            ["thresholds"] = thresholds,
            ["target"]     = 100,
        };

        return result.ToJsonString();
    }
}
