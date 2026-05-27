using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ReportingPlatform.ExcelProvider.Database;
using ReportingPlatform.Provider.V1;

namespace ReportingPlatform.ExcelProvider.Operations;

/// <summary>
/// Handler for <c>report.demo.news2</c>.
/// Returns a <c>news2_bars</c> payload — 12 demo patients with varied NEWS2 scores, seeded by current hour.
/// Mix: 5 L1 (score 0-4), 4 L2 (score 5-6), 3 L3 (score 7+).
/// </summary>
public sealed class DemoNews2BarsHandler : IOperationHandler
{
    private readonly ExcelProviderDb _db;
    private readonly ILogger<DemoNews2BarsHandler> _logger;

    public string OperationPattern => "report.demo.news2";

    private static readonly string[] PatientNames =
    [
        "Nguyễn Văn An",   "Trần Thị Bình",  "Lê Văn Cường",   "Phạm Thị Dung",
        "Hoàng Văn Em",    "Ngô Thị Phương", "Đặng Văn Giang", "Bùi Thị Hoa",
        "Đỗ Văn Inh",      "Vũ Thị Kim",     "Trịnh Văn Long", "Phan Thị Mai",
    ];

    private static readonly string[] Wards = ["ICU", "Nội", "Ngoại", "Cấp cứu", "Tim mạch"];
    private static readonly string[] Trends = ["up", "down", "stable"];

    public DemoNews2BarsHandler(ExcelProviderDb db, ILogger<DemoNews2BarsHandler> logger)
    {
        _db     = db;
        _logger = logger;
    }

    public async Task<string> ExecuteAsync(
        OperationRequest request,
        Func<int, string, Task> reportProgress,
        CancellationToken ct)
    {
        await reportProgress(20, "Generating NEWS2 data…");
        await Task.CompletedTask;

        var rng = new Random(DateTime.Now.Hour);

        // Define score ranges: 5 L1, 4 L2, 3 L3
        var scoreRanges = new[]
        {
            // L1: score 0-4
            (Min: 0, Max: 4), (Min: 0, Max: 4), (Min: 0, Max: 4), (Min: 0, Max: 4), (Min: 0, Max: 4),
            // L2: score 5-6
            (Min: 5, Max: 6), (Min: 5, Max: 6), (Min: 5, Max: 6), (Min: 5, Max: 6),
            // L3: score 7+
            (Min: 7, Max: 12), (Min: 7, Max: 14), (Min: 7, Max: 16),
        };

        // Shuffle scores
        var shuffledRanges = scoreRanges.OrderBy(_ => rng.Next()).ToArray();

        var patients = new JsonArray();
        var now      = DateTimeOffset.UtcNow;

        for (int i = 0; i < 12; i++)
        {
            var (minScore, maxScore) = shuffledRanges[i];
            int score = rng.Next(minScore, maxScore + 1);

            string level = score <= 4 ? "L1" : score <= 6 ? "L2" : "L3";
            bool alertSent = level == "L3";

            // Generate 7 component scores that sum to total score
            var components = GenerateComponents(rng, score);
            var compObj    = new JsonObject
            {
                ["respRate"]      = components[0],
                ["spO2"]          = components[1],
                ["airO2"]         = components[2],
                ["bp"]            = components[3],
                ["heartRate"]     = components[4],
                ["consciousness"] = components[5],
                ["temp"]          = components[6],
            };

            string ward = Wards[rng.Next(Wards.Length)];
            int bedNum  = rng.Next(1, 30);
            string bed  = $"{bedNum:D2}{(char)('A' + rng.Next(0, 3))}";

            patients.Add(new JsonObject
            {
                ["id"]           = $"P-{10000 + i + 1}",
                ["name"]         = PatientNames[i],
                ["ward"]         = ward,
                ["bed"]          = bed,
                ["score"]        = score,
                ["level"]        = level,
                ["trend"]        = Trends[rng.Next(Trends.Length)],
                ["components"]   = compObj,
                ["lastAssessed"] = now.AddHours(-rng.Next(0, 6)).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["alertSent"]    = alertSent,
            });
        }

        var thresholds = new JsonArray
        {
            new JsonObject { ["label"] = "L1 Routine",  ["scoreFrom"] = 0, ["scoreTo"] = 4,  ["color"] = "success" },
            new JsonObject { ["label"] = "L2 Tăng TĐ", ["scoreFrom"] = 5, ["scoreTo"] = 6,  ["color"] = "warning" },
            new JsonObject { ["label"] = "L3 Khẩn",    ["scoreFrom"] = 7, ["scoreTo"] = 20, ["color"] = "danger"  },
        };

        var result = new JsonObject
        {
            ["patients"]   = patients,
            ["thresholds"] = thresholds,
            ["maxScore"]   = 20,
        };

        return result.ToJsonString();
    }

    /// <summary>
    /// Generate 7 component scores (each 0-3) that sum approximately to <paramref name="targetScore"/>.
    /// </summary>
    private static int[] GenerateComponents(Random rng, int targetScore)
    {
        int[] comps = new int[7];
        int remaining = targetScore;

        for (int i = 0; i < 7; i++)
        {
            int maxAlloc = Math.Min(3, remaining);
            if (i == 6)
            {
                // Last component gets whatever remains (clamped)
                comps[i] = Math.Min(3, Math.Max(0, remaining));
            }
            else
            {
                int spotsLeft = 6 - i;
                int minAlloc  = Math.Max(0, remaining - spotsLeft * 3);
                comps[i] = rng.Next(minAlloc, maxAlloc + 1);
                remaining -= comps[i];
            }
        }

        return comps;
    }
}
