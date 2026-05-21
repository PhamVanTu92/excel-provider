using System.Collections.Concurrent;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReportingPlatform.ExcelProvider.Config;
using ReportingPlatform.ExcelProvider.Operations;
using ReportingPlatform.Provider.V1;

namespace ReportingPlatform.ExcelProvider.Grpc;

/// <summary>
/// BackgroundService that maintains a persistent bidirectional gRPC stream with the provider-bridge.
/// Implements the full protocol: Hello → Welcome → request dispatch → heartbeat → reconnect.
/// </summary>
public sealed class ProviderBridgeClient : BackgroundService
{
    private static readonly string[] SupportedOperations =
    [
        "report.dashboard.summary",
        "report.sales.trend",
        "report.inventory.status",
        "report.regional.performance",
        "report.channel.comparison",
        "report.product.detail",
        "report.top.performers",
    ];

    // Exponential backoff steps in milliseconds (5s → 15s → 30s → 60s → 120s)
    private static readonly int[] BackoffStepsMs = [5_000, 15_000, 30_000, 60_000, 120_000];

    private readonly TokenService _tokenService;
    private readonly OperationDispatcher _dispatcher;
    private readonly ProviderOptions _opts;
    private readonly ILogger<ProviderBridgeClient> _logger;

    public ProviderBridgeClient(
        TokenService tokenService,
        OperationDispatcher dispatcher,
        IOptions<ProviderOptions> opts,
        ILogger<ProviderBridgeClient> logger)
    {
        _tokenService = tokenService;
        _dispatcher   = dispatcher;
        _opts         = opts.Value;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int attempt = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var token = await _tokenService.GetTokenAsync(stoppingToken);
                bool handshakeOk = await ConnectAndServeAsync(token, stoppingToken);
                if (handshakeOk) attempt = 0; // reset backoff on successful session
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                var backoff = GetBackoff(attempt++);
                _logger.LogWarning(ex,
                    "Connection attempt {Attempt} failed; reconnecting in {Delay}ms",
                    attempt, backoff.TotalMilliseconds);

                try { await Task.Delay(backoff, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        _logger.LogInformation("ProviderBridgeClient stopped");
    }

    // ─── Connect + Serve ──────────────────────────────────────────────────────

    private async Task<bool> ConnectAndServeAsync(string token, CancellationToken ct)
    {
        _logger.LogInformation("Connecting to bridge at {Url}", _opts.BridgeGrpcUrl);

        using var channel = GrpcChannel.ForAddress(_opts.BridgeGrpcUrl, new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true,
                KeepAlivePingDelay             = TimeSpan.FromSeconds(30),
                KeepAlivePingTimeout           = TimeSpan.FromSeconds(10),
            }
        });

        var client  = new OperationProvider.OperationProviderClient(channel);
        var headers = new Metadata { { "authorization", $"Bearer {token}" } };

        using var stream = client.Connect(headers, cancellationToken: ct);

        // ── Send Hello ────────────────────────────────────────────────────────
        var hello = new Hello
        {
            ProviderId = _opts.ProviderId,
            Version    = _opts.Version,
        };
        hello.SupportedOperations.AddRange(SupportedOperations);
        hello.Metadata["instanceId"] = Environment.MachineName;
        hello.Metadata["language"]   = "dotnet9";

        await stream.RequestStream.WriteAsync(new FromProvider { Hello = hello }, ct);
        _logger.LogInformation("Hello sent (providerId={ProviderId}, version={Version})",
            _opts.ProviderId, _opts.Version);

        // ── Await Welcome (5s deadline) ───────────────────────────────────────
        using var welcomeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        welcomeCts.CancelAfter(TimeSpan.FromSeconds(5));

        Welcome? welcome = null;
        try
        {
            await foreach (var msg in stream.ResponseStream.ReadAllAsync(welcomeCts.Token))
            {
                if (msg.MessageCase == ToProvider.MessageOneofCase.Welcome)
                {
                    welcome = msg.Welcome;
                    break;
                }
                if (msg.MessageCase == ToProvider.MessageOneofCase.Disconnect)
                {
                    _logger.LogWarning("Disconnect received before Welcome: {Reason}", msg.Disconnect.Reason);
                    return false;
                }
            }
        }
        catch (OperationCanceledException) when (welcomeCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException("Timed out waiting for Welcome from Bridge (5s)");
        }

        if (welcome is null)
        {
            _logger.LogWarning("Bridge stream ended before sending Welcome");
            return false;
        }

        _logger.LogInformation(
            "Welcome received — sessionId={SessionId}, maxConcurrent={Max}, heartbeatInterval={HbInterval}s",
            welcome.SessionId, welcome.MaxConcurrentRequests, welcome.HeartbeatIntervalSeconds);

        // ── Active phase ──────────────────────────────────────────────────────
        await ServeAsync(stream, welcome, ct);

        await channel.ShutdownAsync();
        return true;
    }

    // ─── Main serving loop ────────────────────────────────────────────────────

    private async Task ServeAsync(
        AsyncDuplexStreamingCall<FromProvider, ToProvider> stream,
        Welcome welcome,
        CancellationToken ct)
    {
        // Shared write semaphore for heartbeat + response writers
        var writeLock   = new SemaphoreSlim(1, 1);
        int heartbeatInterval = welcome.HeartbeatIntervalSeconds > 0
            ? welcome.HeartbeatIntervalSeconds : 30;

        // ── Start heartbeat task ──────────────────────────────────────────────
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeatTask = RunHeartbeatAsync(
            stream.RequestStream, writeLock, heartbeatInterval, heartbeatCts.Token);

        bool refreshRequested = false;

        try
        {
            await foreach (var msg in stream.ResponseStream.ReadAllAsync(ct))
            {
                switch (msg.MessageCase)
                {
                    case ToProvider.MessageOneofCase.Request:
                        _logger.LogInformation(
                            "OperationRequest received — requestId={RequestId}, operation={Op}",
                            msg.Request.RequestId, msg.Request.Operation);

                        _dispatcher.DispatchFireAndForget(
                            msg.Request, stream.RequestStream, writeLock, ct);
                        break;

                    case ToProvider.MessageOneofCase.Cancel:
                        _logger.LogInformation("Cancel received for requestId={RequestId}",
                            msg.Cancel.RequestId);
                        _dispatcher.RequestCancel(msg.Cancel.RequestId);
                        break;

                    case ToProvider.MessageOneofCase.RefreshAuth:
                        _logger.LogInformation(
                            "RefreshAuthRequired received — reason={Reason}, expiresAt={Exp}",
                            msg.RefreshAuth.Reason,
                            DateTimeOffset.FromUnixTimeMilliseconds(msg.RefreshAuth.CurrentTokenExpiresAtUnixMs));
                        refreshRequested = true;
                        goto doneReading;

                    case ToProvider.MessageOneofCase.Disconnect:
                        _logger.LogWarning("Disconnect received from bridge: {Reason}",
                            msg.Disconnect.Reason);
                        goto doneReading;
                }
            }
            doneReading:;
        }
        finally
        {
            heartbeatCts.Cancel();
            try { await heartbeatTask; } catch { /* swallow cancellation */ }
        }

        // ── Refresh auth: drain in-flight then reconnect ───────────────────────
        if (refreshRequested)
        {
            _logger.LogInformation("Waiting for in-flight requests to drain before reconnecting…");
            using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            drainCts.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await _dispatcher.WaitForDrainAsync(drainCts.Token);
            }
            catch (OperationCanceledException) when (drainCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                _logger.LogWarning("Drain timeout; cancelling remaining in-flight requests");
                _dispatcher.CancelAll();
                await Task.Delay(2_000, ct); // brief window for handlers to write Terminal(CANCELLED)
            }

            // Acquire fresh token for next reconnect attempt
            try
            {
                await _tokenService.AcquireFreshTokenAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to pre-fetch fresh token; will retry in next reconnect cycle");
            }

            try { await stream.RequestStream.CompleteAsync(); } catch { }
        }
    }

    // ─── Heartbeat ────────────────────────────────────────────────────────────

    private async Task RunHeartbeatAsync(
        IClientStreamWriter<FromProvider> writer,
        SemaphoreSlim writeLock,
        int intervalSeconds,
        CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct);

                var hb = new FromProvider
                {
                    Heartbeat = new Heartbeat
                    {
                        TsUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    }
                };

                await writeLock.WaitAsync(ct);
                try { await writer.WriteAsync(hb, ct); }
                finally { writeLock.Release(); }

                _logger.LogDebug("Heartbeat sent");
            }
        }
        catch (OperationCanceledException) { /* expected */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Heartbeat loop terminated unexpectedly");
        }
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static TimeSpan GetBackoff(int attempt)
    {
        var ms = BackoffStepsMs[Math.Min(attempt, BackoffStepsMs.Length - 1)];
        return TimeSpan.FromMilliseconds(ms);
    }
}
