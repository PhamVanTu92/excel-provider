using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ReportingPlatform.ExcelProvider.Database;
using ReportingPlatform.Provider.V1;

namespace ReportingPlatform.ExcelProvider.Operations;

/// <summary>
/// Handler for <c>report.sales.funnel</c>.
/// Returns a <c>funnel</c> payload built from last-30-day unit sales with conversion-rate simulation.
/// </summary>
public sealed class SalesFunnelHandler : IOperationHandler
{
    private readonly ExcelProviderDb _db;
    private readonly ILogger<SalesFunnelHandler> _logger;

    public string OperationPattern => "report.sales.funnel";

    public SalesFunnelHandler(ExcelProviderDb db, ILogger<SalesFunnelHandler> logger)
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
        var fromDate = today.AddDays(-29);

        if (!string.IsNullOrWhiteSpace(request.ParamsJson))
        {
            try
            {
                using var doc  = JsonDocument.Parse(request.ParamsJson);
                var root = doc.RootElement;
                if (root.TryGetProperty("fromDate", out var fp)
                    && DateOnly.TryParse(fp.GetString(), out var pf)) fromDate = pf;
                if (root.TryGetProperty("toDate", out var tp)
                    && DateOnly.TryParse(tp.GetString(), out var pt)) today = pt;
            }
            catch { /* keep defaults */ }
        }

        _logger.LogInformation("SalesFunnel from={From} to={To}", fromDate, today);

        await reportProgress(30, "Querying sales…");
        var rows = await _db.GetSalesByDateRangeAsync(fromDate, today, ct);

        await reportProgress(70, "Building funnel…");

        double baseUnits = rows.Sum(r => r.Units);

        // Define funnel stages with multipliers relative to actual sold units
        var stages = new[]
        {
            ("Khách tiếp cận",       baseUnits * 4.5),
            ("Quan tâm sản phẩm",    baseUnits * 2.8),
            ("Thêm vào giỏ",         baseUnits * 1.4),
            ("Đặt hàng",             baseUnits * 1.0),
            ("Giao hàng thành công", baseUnits * 0.96),
        };

        double startValue = stages[0].Item2;
        if (startValue == 0) startValue = 1; // guard zero division

        var stepsArr = new JsonArray();
        for (int i = 0; i < stages.Length; i++)
        {
            var (label, value) = stages[i];
            double roundedValue    = Math.Round(value);
            double percentOfStart  = Math.Round(roundedValue / startValue * 100, 1);
            JsonNode? dropRate;

            if (i == 0)
            {
                dropRate = (JsonNode?)null;
            }
            else
            {
                double prevValue = Math.Round(stages[i - 1].Item2);
                double drop      = prevValue > 0
                    ? Math.Round((prevValue - roundedValue) / prevValue * 100, 1)
                    : 0.0;
                dropRate = drop;
            }

            stepsArr.Add(new JsonObject
            {
                ["label"]          = label,
                ["value"]          = roundedValue,
                ["percentOfStart"] = percentOfStart,
                ["dropRate"]       = dropRate,
            });
        }

        var result = new JsonObject { ["steps"] = stepsArr };
        return result.ToJsonString();
    }
}
