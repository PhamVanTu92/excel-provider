using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ReportingPlatform.ExcelProvider.Database;
using ReportingPlatform.Provider.V1;

namespace ReportingPlatform.ExcelProvider.Operations;

/// <summary>
/// Handler for <c>report.demo.patient.flow</c>.
/// Returns a <c>patient_flow_stages</c> payload with realistic demo data seeded by current hour.
/// </summary>
public sealed class DemoPatientFlowHandler : IOperationHandler
{
    private readonly ExcelProviderDb _db;
    private readonly ILogger<DemoPatientFlowHandler> _logger;

    public string OperationPattern => "report.demo.patient.flow";

    public DemoPatientFlowHandler(ExcelProviderDb db, ILogger<DemoPatientFlowHandler> logger)
    {
        _db     = db;
        _logger = logger;
    }

    public async Task<string> ExecuteAsync(
        OperationRequest request,
        Func<int, string, Task> reportProgress,
        CancellationToken ct)
    {
        await reportProgress(20, "Generating patient flow data…");

        var rng  = new Random(DateTime.Now.Hour);

        // Business hours multiplier: more patients during 8-17
        int hour         = DateTime.Now.Hour;
        double busyFactor = (hour >= 8 && hour <= 17) ? 1.0 : 0.4;

        int waiting   = (int)Math.Round(rng.Next(35, 60)  * busyFactor);
        int triaged   = (int)Math.Round(rng.Next(8,  18)  * busyFactor);
        int inConsult = (int)Math.Round(rng.Next(15, 30)  * busyFactor);
        int lab       = (int)Math.Round(rng.Next(10, 25)  * busyFactor);
        int discharge = (int)Math.Round(rng.Next(5,  15)  * busyFactor);

        int waitMin    = rng.Next(20, 55);
        int labWaitMin = rng.Next(30, 65);

        await reportProgress(70, "Building payload…");

        var stages = new JsonArray
        {
            Stage("waiting",    "Chờ khám",       waiting,   waitMin,    waitMin    >= 30 ? "warning" : "ok", waitMin    >= 30 ? "clock" : null),
            Stage("triaged",    "Đã phân loại",   triaged,   rng.Next(5,  15), "ok",      null),
            Stage("in_consult", "Đang khám",      inConsult, rng.Next(10, 25), "ok",      null),
            Stage("lab",        "Xét nghiệm",     lab,       labWaitMin, labWaitMin >= 30 ? "warning" : "ok", labWaitMin >= 30 ? "clock" : null),
            Stage("discharge",  "Chờ xuất viện",  discharge, rng.Next(10, 30), "ok",      null),
        };

        int total = waiting + triaged + inConsult + lab + discharge;

        var result = new JsonObject
        {
            ["stages"]        = stages,
            ["totalPatients"] = total,
            ["thresholds"]    = new JsonObject
            {
                ["waitWarningMin"] = 30,
                ["waitDangerMin"]  = 60,
            },
        };

        return result.ToJsonString();
    }

    private static JsonObject Stage(string id, string label, int count, int avgWaitMin, string status, string? icon) =>
        new()
        {
            ["id"]         = id,
            ["label"]      = label,
            ["count"]      = count,
            ["avgWaitMin"] = avgWaitMin,
            ["status"]     = status,
            ["icon"]       = icon is null ? (JsonNode?)null : icon,
        };
}
