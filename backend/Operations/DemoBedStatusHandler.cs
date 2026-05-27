using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ReportingPlatform.ExcelProvider.Database;
using ReportingPlatform.Provider.V1;

namespace ReportingPlatform.ExcelProvider.Operations;

/// <summary>
/// Handler for <c>report.demo.bed.status</c>.
/// Returns a <c>bed_grid</c> payload with 4 departments, each 5–8 beds, seeded by current hour.
/// </summary>
public sealed class DemoBedStatusHandler : IOperationHandler
{
    private readonly ExcelProviderDb _db;
    private readonly ILogger<DemoBedStatusHandler> _logger;

    public string OperationPattern => "report.demo.bed.status";

    private static readonly string[] VietnameseNames =
    [
        "Nguyễn Văn An", "Trần Thị Bình", "Lê Văn Cường", "Phạm Thị Dung",
        "Hoàng Văn Em", "Ngô Thị Phương", "Đặng Văn Giang", "Bùi Thị Hoa",
        "Đỗ Văn Inh", "Vũ Thị Kim", "Trịnh Văn Long", "Phan Thị Mai",
        "Dương Văn Nam", "Hà Thị Oanh", "Lý Văn Phú", "Tô Thị Quyên",
        "Chu Văn Rồng", "Đinh Thị Sen", "Lưu Văn Tài", "Cao Thị Uyên",
    ];

    public DemoBedStatusHandler(ExcelProviderDb db, ILogger<DemoBedStatusHandler> logger)
    {
        _db     = db;
        _logger = logger;
    }

    public async Task<string> ExecuteAsync(
        OperationRequest request,
        Func<int, string, Task> reportProgress,
        CancellationToken ct)
    {
        await reportProgress(20, "Generating bed status data…");
        await Task.CompletedTask;

        var rng = new Random(DateTime.Now.Hour);

        var departmentDefs = new[]
        {
            (Id: "icu",     Name: "ICU",       Floor: "2F", BedCount: 6),
            (Id: "noi",     Name: "Nội",       Floor: "3F", BedCount: 8),
            (Id: "ngoai",   Name: "Ngoại",     Floor: "4F", BedCount: 7),
            (Id: "capCuu",  Name: "Cấp cứu",   Floor: "1F", BedCount: 5),
        };

        // Status weights: occupied=50%, available=25%, cleaning=15%, reserved=10%
        string PickStatus() => rng.NextDouble() switch
        {
            < 0.50 => "occupied",
            < 0.75 => "available",
            < 0.90 => "cleaning",
            _      => "reserved",
        };

        int nameIdx = 0;
        var departments = new JsonArray();

        foreach (var dept in departmentDefs)
        {
            var beds     = new JsonArray();
            var summary  = new Dictionary<string, int>
            {
                ["occupied"]  = 0,
                ["available"] = 0,
                ["cleaning"]  = 0,
                ["reserved"]  = 0,
                ["total"]     = dept.BedCount,
            };

            for (int b = 1; b <= dept.BedCount; b++)
            {
                string status = PickStatus();
                summary[status]++;

                string bedId      = $"{dept.Id}-{b:D2}";
                string bedLabel   = b.ToString("D2");
                JsonNode? patientId   = (JsonNode?)null;
                JsonNode? patientName = (JsonNode?)null;
                JsonNode? admittedAt  = (JsonNode?)null;

                if (status == "occupied" && nameIdx < VietnameseNames.Length)
                {
                    string name = VietnameseNames[nameIdx++];
                    patientId   = $"P-{10000 + nameIdx}";
                    patientName = name;
                    admittedAt  = DateTime.UtcNow.AddHours(-rng.Next(2, 72)).ToString("yyyy-MM-ddTHH:mm:ssZ");
                }

                beds.Add(new JsonObject
                {
                    ["id"]          = bedId,
                    ["label"]       = bedLabel,
                    ["status"]      = status,
                    ["patientId"]   = patientId,
                    ["patientName"] = patientName,
                    ["admittedAt"]  = admittedAt,
                });
            }

            var summaryNode = new JsonObject();
            foreach (var (k, v) in summary) summaryNode[k] = v;

            departments.Add(new JsonObject
            {
                ["id"]      = dept.Id,
                ["name"]    = dept.Name,
                ["floor"]   = dept.Floor,
                ["beds"]    = beds,
                ["summary"] = summaryNode,
            });
        }

        var legend = new JsonArray
        {
            new JsonObject { ["status"] = "occupied",  ["label"] = "Đang dùng",  ["color"] = "danger"  },
            new JsonObject { ["status"] = "available", ["label"] = "Trống",       ["color"] = "success" },
            new JsonObject { ["status"] = "cleaning",  ["label"] = "Đang dọn",   ["color"] = "warning" },
            new JsonObject { ["status"] = "reserved",  ["label"] = "Đã đặt",     ["color"] = "info"    },
        };

        var result = new JsonObject
        {
            ["departments"] = departments,
            ["legend"]      = legend,
        };

        return result.ToJsonString();
    }
}
