using ReportingPlatform.Provider.V1;

namespace ReportingPlatform.ExcelProvider.Operations;

/// <summary>
/// Contract that all operation handlers must implement.
/// </summary>
public interface IOperationHandler
{
    /// <summary>The operation pattern this handler serves, e.g. "report.dashboard.summary".</summary>
    string OperationPattern { get; }

    /// <summary>
    /// Execute the operation.
    /// Implementations should honour <paramref name="ct"/> and report intermediate progress
    /// via <paramref name="reportProgress"/> when the request has <c>WantsProgress = true</c>.
    /// </summary>
    Task<string> ExecuteAsync(
        OperationRequest request,
        Func<int, string, Task> reportProgress,
        CancellationToken ct);
}
