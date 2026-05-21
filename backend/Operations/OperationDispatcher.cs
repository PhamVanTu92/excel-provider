using System.Collections.Concurrent;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using ReportingPlatform.Provider.V1;

namespace ReportingPlatform.ExcelProvider.Operations;

/// <summary>
/// Dispatches incoming <see cref="OperationRequest"/> messages to the correct handler,
/// manages in-flight request cancellation, and serialises gRPC stream writes.
/// </summary>
public sealed class OperationDispatcher
{
    private readonly IReadOnlyDictionary<string, IOperationHandler> _handlers;
    private readonly ILogger<OperationDispatcher> _logger;

    // requestId → CancellationTokenSource for in-flight requests
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inflight = new();

    // Tracks in-flight task count for drain support
    private volatile int _inflightCount;
    private readonly SemaphoreSlim _drainSignal = new(0, int.MaxValue);

    public OperationDispatcher(
        IEnumerable<IOperationHandler> handlers,
        ILogger<OperationDispatcher> logger)
    {
        _handlers = handlers.ToDictionary(h => h.OperationPattern, StringComparer.Ordinal);
        _logger   = logger;
    }

    /// <summary>Fire-and-forget dispatch. Writes Terminal (and optional Progress) to the gRPC stream.</summary>
    public void DispatchFireAndForget(
        OperationRequest request,
        IClientStreamWriter<FromProvider> writer,
        SemaphoreSlim writeLock,
        CancellationToken streamCt)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(streamCt);
        _inflight[request.RequestId] = cts;
        Interlocked.Increment(ref _inflightCount);

        _ = Task.Run(async () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Terminal terminal;

            try
            {
                if (!_handlers.TryGetValue(request.Operation, out var handler))
                {
                    _logger.LogWarning("No handler registered for operation={Op}", request.Operation);
                    terminal = MakeFailedTerminal(
                        "VALIDATION_ERROR",
                        $"Operation '{request.Operation}' is not supported by this provider",
                        sw.ElapsedMilliseconds);
                }
                else
                {
                    // Honour hard deadline
                    if (request.TimeoutAtUnixMs > 0)
                    {
                        var deadlineMs = request.TimeoutAtUnixMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        if (deadlineMs > 0)
                            cts.CancelAfter(TimeSpan.FromMilliseconds(deadlineMs));
                    }

                    Func<int, string, Task> reportProgress = async (pct, msg) =>
                    {
                        if (!request.WantsProgress) return;

                        var chunk = new FromProvider
                        {
                            ResponseChunk = new OperationResponseChunk
                            {
                                RequestId = request.RequestId,
                                Progress  = new Progress
                                {
                                    Percent  = pct,
                                    Message  = msg,
                                    TsUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                                }
                            }
                        };

                        await writeLock.WaitAsync(cts.Token);
                        try { await writer.WriteAsync(chunk, cts.Token); }
                        finally { writeLock.Release(); }

                        _logger.LogDebug(
                            "Progress {Pct}% sent for requestId={RequestId}: {Message}",
                            pct, request.RequestId, msg);
                    };

                    var payloadJson = await handler.ExecuteAsync(request, reportProgress, cts.Token);
                    terminal = new Terminal
                    {
                        Status      = ReportingPlatform.Provider.V1.Status.Done,
                        PayloadJson = payloadJson,
                        ElapsedMs   = sw.ElapsedMilliseconds,
                    };
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested && !streamCt.IsCancellationRequested)
            {
                _logger.LogInformation("Request {RequestId} was cancelled", request.RequestId);
                terminal = new Terminal
                {
                    Status    = ReportingPlatform.Provider.V1.Status.Cancelled,
                    ElapsedMs = sw.ElapsedMilliseconds,
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Handler threw for requestId={RequestId}", request.RequestId);
                terminal = MakeFailedTerminal("INTERNAL_ERROR", ex.Message, sw.ElapsedMilliseconds);
            }
            finally
            {
                _inflight.TryRemove(request.RequestId, out _);
                cts.Dispose();
            }

            // Write terminal (best-effort — stream may already be closed)
            try
            {
                var chunk = new FromProvider
                {
                    ResponseChunk = new OperationResponseChunk
                    {
                        RequestId = request.RequestId,
                        Terminal  = terminal
                    }
                };

                await writeLock.WaitAsync(streamCt);
                try { await writer.WriteAsync(chunk, streamCt); }
                finally { writeLock.Release(); }

                _logger.LogInformation(
                    "Terminal sent for requestId={RequestId} status={Status} elapsed={Elapsed}ms",
                    request.RequestId, terminal.Status, terminal.ElapsedMs);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write Terminal for requestId={RequestId}", request.RequestId);
            }
            finally
            {
                int remaining = Interlocked.Decrement(ref _inflightCount);
                if (remaining == 0)
                    _drainSignal.Release();
            }
        }, streamCt);
    }

    /// <summary>Signals cancellation for a specific in-flight request.</summary>
    public void RequestCancel(string requestId)
    {
        if (_inflight.TryGetValue(requestId, out var cts))
        {
            _logger.LogInformation("Cancelling request {RequestId}", requestId);
            cts.Cancel();
        }
    }

    /// <summary>Cancels all in-flight requests (used during RefreshAuth drain).</summary>
    public void CancelAll()
    {
        foreach (var (id, cts) in _inflight)
        {
            _logger.LogDebug("Force-cancelling in-flight request {RequestId}", id);
            cts.Cancel();
        }
    }

    /// <summary>
    /// Waits until all in-flight requests have completed (written their Terminal).
    /// Returns immediately if there are no in-flight requests.
    /// </summary>
    public async Task WaitForDrainAsync(CancellationToken ct)
    {
        if (_inflightCount == 0) return;

        // Wait for the drain signal (released when count reaches 0)
        await _drainSignal.WaitAsync(ct);

        // Replenish so subsequent calls work correctly
        // Note: _drainSignal is only released when count hits exactly 0
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static Terminal MakeFailedTerminal(string code, string message, long elapsedMs) =>
        new()
        {
            Status    = ReportingPlatform.Provider.V1.Status.Failed,
            ElapsedMs = elapsedMs,
            Error     = new Error { Code = code, Message = message },
        };
}
