using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ReportingPlatform.ExcelProvider.Database;
using ReportingPlatform.Provider.V1;

namespace ReportingPlatform.ExcelProvider.Operations;

/// <summary>
/// Handler for <c>report.demo.room.status</c>.
/// Returns a <c>room_status_grid</c> payload — 5 OR rooms with seeded status mix.
/// </summary>
public sealed class DemoRoomStatusHandler : IOperationHandler
{
    private readonly ExcelProviderDb _db;
    private readonly ILogger<DemoRoomStatusHandler> _logger;

    public string OperationPattern => "report.demo.room.status";

    public DemoRoomStatusHandler(ExcelProviderDb db, ILogger<DemoRoomStatusHandler> logger)
    {
        _db     = db;
        _logger = logger;
    }

    public async Task<string> ExecuteAsync(
        OperationRequest request,
        Func<int, string, Task> reportProgress,
        CancellationToken ct)
    {
        await reportProgress(20, "Generating room status data…");
        await Task.CompletedTask;

        var rng  = new Random(DateTime.Now.Hour);
        var now  = DateTimeOffset.UtcNow;

        // Seeded surgery start times vary by hour
        var surgeryStartOffset1 = now.AddHours(-rng.Next(1, 4));
        var surgeryStartOffset2 = now.AddHours(-rng.Next(1, 3));

        int prog1 = rng.Next(40, 85);
        int prog2 = rng.Next(20, 60);

        var surgeons = new[] { "BS. Nguyễn Minh Tuấn", "BS. Trần Văn Hùng", "BS. Lê Thị Hương" };

        // Fixed 5-room layout: 2 occupied, 1 cleaning, 2 available
        var rooms = new JsonArray
        {
            new JsonObject
            {
                ["id"]              = "or-01",
                ["label"]           = "Phòng mổ 01",
                ["status"]          = "occupied",
                ["primaryText"]     = "Phẫu thuật nội soi bụng",
                ["secondaryText"]   = $"{surgeons[rng.Next(surgeons.Length)]} • Còn ~{100 - prog1} phút",
                ["progressPercent"] = prog1,
                ["startTime"]       = surgeryStartOffset1.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["estimatedEnd"]    = surgeryStartOffset1.AddHours(3).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["badgeLabel"]      = $"{prog1}%",
                ["badgeVariant"]    = prog1 >= 80 ? "danger" : "warning",
            },
            new JsonObject
            {
                ["id"]              = "or-02",
                ["label"]           = "Phòng mổ 02",
                ["status"]          = "occupied",
                ["primaryText"]     = "Phẫu thuật chỉnh hình",
                ["secondaryText"]   = $"{surgeons[rng.Next(surgeons.Length)]} • Còn ~{100 - prog2} phút",
                ["progressPercent"] = prog2,
                ["startTime"]       = surgeryStartOffset2.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["estimatedEnd"]    = surgeryStartOffset2.AddHours(2).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["badgeLabel"]      = $"{prog2}%",
                ["badgeVariant"]    = prog2 >= 80 ? "danger" : "warning",
            },
            new JsonObject
            {
                ["id"]              = "or-03",
                ["label"]           = "Phòng mổ 03",
                ["status"]          = "cleaning",
                ["primaryText"]     = "Đang vệ sinh",
                ["secondaryText"]   = $"Hoàn thành lúc {now.AddMinutes(rng.Next(10, 40)):HH:mm}",
                ["progressPercent"] = rng.Next(30, 70),
                ["startTime"]       = (JsonNode?)null,
                ["estimatedEnd"]    = (JsonNode?)null,
                ["badgeLabel"]      = (JsonNode?)null,
                ["badgeVariant"]    = (JsonNode?)null,
            },
            new JsonObject
            {
                ["id"]              = "or-04",
                ["label"]           = "Phòng mổ 04",
                ["status"]          = "available",
                ["primaryText"]     = "Sẵn sàng",
                ["secondaryText"]   = (JsonNode?)null,
                ["progressPercent"] = (JsonNode?)null,
                ["startTime"]       = (JsonNode?)null,
                ["estimatedEnd"]    = (JsonNode?)null,
                ["badgeLabel"]      = (JsonNode?)null,
                ["badgeVariant"]    = (JsonNode?)null,
            },
            new JsonObject
            {
                ["id"]              = "or-05",
                ["label"]           = "Phòng mổ 05",
                ["status"]          = "available",
                ["primaryText"]     = "Sẵn sàng",
                ["secondaryText"]   = (JsonNode?)null,
                ["progressPercent"] = (JsonNode?)null,
                ["startTime"]       = (JsonNode?)null,
                ["estimatedEnd"]    = (JsonNode?)null,
                ["badgeLabel"]      = (JsonNode?)null,
                ["badgeVariant"]    = (JsonNode?)null,
            },
        };

        var result = new JsonObject
        {
            ["rooms"]        = rooms,
            ["statusValues"] = new JsonArray { "available", "occupied", "cleaning", "reserved", "blocked", "emergency" },
        };

        return result.ToJsonString();
    }
}
