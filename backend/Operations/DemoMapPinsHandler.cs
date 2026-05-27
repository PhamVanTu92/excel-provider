using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ReportingPlatform.ExcelProvider.Database;
using ReportingPlatform.Provider.V1;

namespace ReportingPlatform.ExcelProvider.Operations;

/// <summary>
/// Handler for <c>report.demo.map.pins</c>.
/// Returns a <c>map_pins</c> payload — hospital floor plan with 3 ambulances + 5 departments, seeded by hour.
/// </summary>
public sealed class DemoMapPinsHandler : IOperationHandler
{
    private readonly ExcelProviderDb _db;
    private readonly ILogger<DemoMapPinsHandler> _logger;

    public string OperationPattern => "report.demo.map.pins";

    private static readonly string[] AmbulanceStatuses = ["active", "warning", "idle"];

    public DemoMapPinsHandler(ExcelProviderDb db, ILogger<DemoMapPinsHandler> logger)
    {
        _db     = db;
        _logger = logger;
    }

    public async Task<string> ExecuteAsync(
        OperationRequest request,
        Func<int, string, Task> reportProgress,
        CancellationToken ct)
    {
        await reportProgress(20, "Generating map pin data…");
        await Task.CompletedTask;

        var rng = new Random(DateTime.Now.Hour);

        // Ambulance positions vary slightly by hour (simulating movement)
        double AmbX(double baseX) => Math.Round(baseX + rng.NextDouble() * 4 - 2, 1);
        double AmbY(double baseY) => Math.Round(baseY + rng.NextDouble() * 4 - 2, 1);

        string AmbStatus(int i) => AmbulanceStatuses[rng.Next(AmbulanceStatuses.Length)];
        int EtaMin() => rng.Next(3, 20);

        var pins = new JsonArray
        {
            // ── Ambulances ─────────────────────────────────────────────────────
            Ambulance("amb-01", "Xe cấp cứu 01", AmbX(15), AmbY(70), AmbStatus(0), EtaMin(), "Nguyễn Văn A", "120 Lê Lợi"),
            Ambulance("amb-02", "Xe cấp cứu 02", AmbX(45), AmbY(85), AmbStatus(1), EtaMin(), "Trần Thị B",   "45 Nguyễn Huệ"),
            Ambulance("amb-03", "Xe cấp cứu 03", AmbX(80), AmbY(60), AmbStatus(2), EtaMin(), null,            null),

            // ── Departments ────────────────────────────────────────────────────
            Department("dept-icu",    "ICU",       25.0, 30.0, "warning", 18 + rng.Next(-2, 3), 20),
            Department("dept-noi",    "Nội",       55.0, 25.0, "ok",      32 + rng.Next(-3, 4), 45),
            Department("dept-ngoai",  "Ngoại",     75.0, 40.0, "ok",      28 + rng.Next(-2, 3), 40),
            Department("dept-capCuu", "Cấp cứu",  40.0, 55.0, "ok",      15 + rng.Next(-1, 3), 25),
            Department("dept-phauThuat", "Phẫu thuật", 60.0, 65.0, "ok", 3 + rng.Next(0, 3),   5),
        };

        var result = new JsonObject
        {
            ["backgroundUrl"]  = "/assets/hospital-map.svg",
            ["backgroundType"] = "floor_plan",
            ["width"]          = 1000,
            ["height"]         = 600,
            ["pins"]           = pins,
        };

        return result.ToJsonString();
    }

    private static JsonObject Ambulance(
        string id, string label,
        double x, double y,
        string status, int etaMin,
        string? patientName, string? origin)
    {
        var meta = new JsonObject { ["eta"] = $"{etaMin} phút" };
        if (patientName is not null) meta["patientName"] = patientName;
        if (origin is not null)     meta["origin"]      = origin;

        string sublabel = status switch
        {
            "active"  => "Trên đường đến",
            "warning" => "Chờ lệnh",
            _         => "Sẵn sàng",
        };

        return new JsonObject
        {
            ["id"]       = id,
            ["x"]        = x,
            ["y"]        = y,
            ["label"]    = label,
            ["sublabel"] = sublabel,
            ["status"]   = status,
            ["type"]     = "ambulance",
            ["metadata"] = meta,
        };
    }

    private static JsonObject Department(
        string id, string name,
        double x, double y,
        string status, int occupied, int total)
    {
        return new JsonObject
        {
            ["id"]       = id,
            ["x"]        = x,
            ["y"]        = y,
            ["label"]    = name,
            ["sublabel"] = $"{occupied}/{total} giường",
            ["status"]   = status,
            ["type"]     = "department",
            ["metadata"] = new JsonObject { ["occupied"] = occupied, ["total"] = total },
        };
    }
}
