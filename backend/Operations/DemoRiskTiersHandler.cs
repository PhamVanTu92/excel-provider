using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ReportingPlatform.ExcelProvider.Database;
using ReportingPlatform.Provider.V1;

namespace ReportingPlatform.ExcelProvider.Operations;

/// <summary>
/// Handler for <c>report.demo.risk.tiers</c>.
/// Returns a <c>risk_tiers</c> payload — 4-tier risk stratification, seeded by current hour.
/// Total ~375; level1=12±2, level2=45±5, level3=124±10, level4=rest.
/// </summary>
public sealed class DemoRiskTiersHandler : IOperationHandler
{
    private readonly ExcelProviderDb _db;
    private readonly ILogger<DemoRiskTiersHandler> _logger;

    public string OperationPattern => "report.demo.risk.tiers";

    public DemoRiskTiersHandler(ExcelProviderDb db, ILogger<DemoRiskTiersHandler> logger)
    {
        _db     = db;
        _logger = logger;
    }

    public async Task<string> ExecuteAsync(
        OperationRequest request,
        Func<int, string, Task> reportProgress,
        CancellationToken ct)
    {
        await reportProgress(20, "Generating risk tier data…");
        await Task.CompletedTask;

        var rng = new Random(DateTime.Now.Hour);

        int l1 = 12 + rng.Next(-2, 3);   // 10–14
        int l2 = 45 + rng.Next(-5, 6);   // 40–50
        int l3 = 124 + rng.Next(-10, 11); // 114–134
        int l4 = 375 - l1 - l2 - l3;

        int[] counts  = [l1, l2, l3, l4];
        int total     = counts.Sum();

        int Change() => rng.Next(-2, 3); // -2 to +2

        var tierDefs = new[]
        {
            (Level: 1, Label: "Nguy cơ rất cao", Color: "danger",  Action: "Can thiệp ngay"),
            (Level: 2, Label: "Nguy cơ cao",     Color: "warning", Action: "Theo dõi chặt"),
            (Level: 3, Label: "Nguy cơ TB",      Color: "info",    Action: "Tái khám định kỳ"),
            (Level: 4, Label: "Nguy cơ thấp",    Color: "success", Action: "Duy trì điều trị"),
        };

        var tiers = new JsonArray();
        for (int i = 0; i < tierDefs.Length; i++)
        {
            var (level, label, color, action) = tierDefs[i];
            int count = counts[i];
            double pct = total > 0 ? Math.Round((double)count / total * 100, 1) : 0;

            tiers.Add(new JsonObject
            {
                ["level"]          = level,
                ["label"]          = label,
                ["count"]          = count,
                ["percent"]        = pct,
                ["color"]          = color,
                ["action"]         = action,
                ["changeFromPrev"] = Change(),
            });
        }

        var result = new JsonObject
        {
            ["tiers"]          = tiers,
            ["total"]          = total,
            ["calculatedAt"]   = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["modelVersion"]   = "sepsis-risk-v2.1",
        };

        return result.ToJsonString();
    }
}
