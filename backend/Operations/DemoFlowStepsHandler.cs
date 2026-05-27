using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ReportingPlatform.ExcelProvider.Database;
using ReportingPlatform.Provider.V1;

namespace ReportingPlatform.ExcelProvider.Operations;

/// <summary>
/// Handler for <c>report.demo.flow.steps</c>.
/// Returns a <c>flow_steps</c> payload — 6-step order fulfillment process, seeded by current hour.
/// </summary>
public sealed class DemoFlowStepsHandler : IOperationHandler
{
    private readonly ExcelProviderDb _db;
    private readonly ILogger<DemoFlowStepsHandler> _logger;

    public string OperationPattern => "report.demo.flow.steps";

    public DemoFlowStepsHandler(ExcelProviderDb db, ILogger<DemoFlowStepsHandler> logger)
    {
        _db     = db;
        _logger = logger;
    }

    public async Task<string> ExecuteAsync(
        OperationRequest request,
        Func<int, string, Task> reportProgress,
        CancellationToken ct)
    {
        await reportProgress(20, "Generating flow steps data…");
        await Task.CompletedTask;

        var rng        = new Random(DateTime.Now.Hour);
        int packingCount = rng.Next(3, 9); // 3–8 orders being packed

        var steps = new JsonArray
        {
            Step("step-01", "Tiếp nhận đơn",        "done",    null,          null),
            Step("step-02", "Kiểm tra kho",          "done",    null,          null),
            Step("step-03", "Đóng gói",              "current", packingCount,  $"{packingCount} đơn đang xử lý"),
            Step("step-04", "Bàn giao vận chuyển",   "pending", null,          null),
            Step("step-05", "Đang giao hàng",         "pending", null,          null),
            Step("step-06", "Xác nhận nhận hàng",    "pending", null,          null),
        };

        var result = new JsonObject
        {
            ["direction"] = "horizontal",
            ["steps"]     = steps,
        };

        return result.ToJsonString();
    }

    private static JsonObject Step(string id, string label, string status, int? count, string? sublabel) =>
        new()
        {
            ["id"]       = id,
            ["label"]    = label,
            ["sublabel"] = sublabel is null ? (JsonNode?)null : sublabel,
            ["status"]   = status,
            ["count"]    = count.HasValue ? (JsonNode)count.Value : (JsonNode?)null,
        };
}
