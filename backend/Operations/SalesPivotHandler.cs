using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ReportingPlatform.ExcelProvider.Database;
using ReportingPlatform.Provider.V1;

namespace ReportingPlatform.ExcelProvider.Operations;

/// <summary>
/// Handler for <c>report.sales.pivot</c>.
/// Returns a <c>pivot_table</c> payload — region × channel with revenue + units measures.
/// </summary>
public sealed class SalesPivotHandler : IOperationHandler
{
    private readonly ExcelProviderDb _db;
    private readonly ILogger<SalesPivotHandler> _logger;

    public string OperationPattern => "report.sales.pivot";

    public SalesPivotHandler(ExcelProviderDb db, ILogger<SalesPivotHandler> logger)
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

        _logger.LogInformation("SalesPivot from={From} to={To}", fromDate, today);

        await reportProgress(30, "Querying sales…");
        var rows = await _db.GetSalesByDateRangeAsync(fromDate, today, ct);

        await reportProgress(70, "Building pivot table…");

        // Aggregate by (region, channel)
        var agg = rows
            .GroupBy(r => (r.Region, r.Channel))
            .Select(g => new
            {
                Region  = g.Key.Region,
                Channel = g.Key.Channel,
                Revenue = g.Sum(r => r.Revenue),
                Units   = g.Sum(r => r.Units),
            })
            .ToList();

        var allRegions  = agg.Select(a => a.Region).Distinct().OrderBy(r => r).ToList();
        var allChannels = agg.Select(a => a.Channel).Distinct().OrderBy(c => c).ToList();

        // Build cells
        var cells = new JsonArray();
        foreach (var region in allRegions)
        {
            foreach (var channel in allChannels)
            {
                var match = agg.FirstOrDefault(a => a.Region == region && a.Channel == channel);
                if (match is null) continue;

                var rowKeyArr = new JsonArray(); rowKeyArr.Add(region);
                var colKeyArr = new JsonArray(); colKeyArr.Add(channel);

                cells.Add(new JsonObject
                {
                    ["rowKey"]    = rowKeyArr,
                    ["columnKey"] = colKeyArr,
                    ["values"]    = new JsonObject
                    {
                        ["revenue"] = (double)Math.Round(match.Revenue, 2),
                        ["units"]   = match.Units,
                    },
                });
            }
        }

        // Row totals (per region, across all channels)
        var rowTotals = new JsonArray();
        foreach (var region in allRegions)
        {
            var regionRows = agg.Where(a => a.Region == region).ToList();
            var rowKeyArr = new JsonArray(); rowKeyArr.Add(region);
            rowTotals.Add(new JsonObject
            {
                ["rowKey"] = rowKeyArr,
                ["values"] = new JsonObject
                {
                    ["revenue"] = (double)Math.Round(regionRows.Sum(a => a.Revenue), 2),
                    ["units"]   = regionRows.Sum(a => a.Units),
                },
            });
        }

        // Column totals (per channel, across all regions)
        var colTotals = new JsonArray();
        foreach (var channel in allChannels)
        {
            var channelRows = agg.Where(a => a.Channel == channel).ToList();
            var colKeyArr = new JsonArray(); colKeyArr.Add(channel);
            colTotals.Add(new JsonObject
            {
                ["columnKey"] = colKeyArr,
                ["values"]    = new JsonObject
                {
                    ["revenue"] = (double)Math.Round(channelRows.Sum(a => a.Revenue), 2),
                    ["units"]   = channelRows.Sum(a => a.Units),
                },
            });
        }

        var result = new JsonObject
        {
            ["rowDimensions"]    = new JsonArray { new JsonObject { ["key"] = "region",  ["label"] = "Khu vực" } },
            ["columnDimensions"] = new JsonArray { new JsonObject { ["key"] = "channel", ["label"] = "Kênh"    } },
            ["measures"]         = new JsonArray
            {
                new JsonObject { ["key"] = "revenue", ["label"] = "Doanh thu", ["aggregate"] = "sum",   ["format"] = "currency:VND" },
                new JsonObject { ["key"] = "units",   ["label"] = "Số lượng",  ["aggregate"] = "sum",   ["format"] = "number"       },
            },
            ["cells"]        = cells,
            ["rowTotals"]    = rowTotals,
            ["columnTotals"] = colTotals,
            ["grandTotal"]   = new JsonObject
            {
                ["revenue"] = (double)Math.Round(agg.Sum(a => a.Revenue), 2),
                ["units"]   = agg.Sum(a => a.Units),
            },
        };

        return result.ToJsonString();
    }
}
