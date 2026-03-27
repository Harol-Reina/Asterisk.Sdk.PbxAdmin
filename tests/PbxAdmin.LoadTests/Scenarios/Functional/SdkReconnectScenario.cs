using Asterisk.Sdk;
using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ami.Responses;
using Microsoft.Extensions.Logging;
using PbxAdmin.LoadTests.Sdk;
using PbxAdmin.LoadTests.Validation;

namespace PbxAdmin.LoadTests.Scenarios.Functional;

/// <summary>
/// Verifies Asterisk.Sdk.Hosting auto-reconnect behavior:
/// 1. Generate 2 calls to verify connection works, wait for completion
/// 2. Send AMI "manager reload" to force-disconnect
/// 3. Wait 3s for disconnect detection, then poll up to 10s for reconnect
/// 4. If reconnected, generate 2 more calls
/// 5. Validate: ConnectionAlive, PreDisconnectSessionsExist, PostReconnectSessionsExist
/// </summary>
public sealed class SdkReconnectScenario : ITestScenario
{
    public string Name => "sdk-reconnect";
    public string Description => "AMI disconnect + auto-reconnect — validates SDK recovers and continues tracking sessions";

    private const int PreDisconnectCalls = 2;
    private const int PostReconnectCalls = 2;
    private const int ReconnectTimeoutMs = 10_000;
    private const int ReconnectPollMs = 500;

    public async Task ExecuteAsync(TestContext context, CancellationToken ct)
    {
        var logger = context.LoggerFactory.CreateLogger<SdkReconnectScenario>();
        context.TestStartTime = DateTime.UtcNow;

        if (context.SdkRuntime is null || context.SessionCapture is null)
        {
            logger.LogError("[{Scenario}] SDK infrastructure not available — cannot run", Name);
            throw new InvalidOperationException("SdkRuntime and SessionCapture are required for this scenario");
        }

        // Phase 0: Wait for any lingering channels from previous runs to drain
        logger.LogInformation("[{Scenario}] Phase 0: Waiting for Asterisk channels to drain before starting", Name);
        await WaitForChannelsDrainedAsync(context.SdkRuntime.Connection, logger, ct);

        // Phase 1: Pre-disconnect calls to verify connection works
        logger.LogInformation(
            "[{Scenario}] Phase 1: Generating {Count} pre-disconnect calls to extension 105",
            Name, PreDisconnectCalls);

        try
        {
            for (int i = 0; i < PreDisconnectCalls; i++)
            {
                ct.ThrowIfCancellationRequested();
                var result = await context.CallGenerator.GenerateCallAsync("105", cancellationToken: ct);
                context.EventCapture.RegisterCall(result.CallId, result.Caller.Number, result.Destination, result.Timestamp);
                context.Metrics.RecordCallOriginated();

                if (result.Accepted)
                    logger.LogDebug("[{Scenario}] Phase 1 call {N}/{Total} accepted: {CallId}", Name, i + 1, PreDisconnectCalls, result.CallId);
                else
                    logger.LogWarning("[{Scenario}] Phase 1 call {N}/{Total} rejected: {Error}", Name, i + 1, PreDisconnectCalls, result.ErrorMessage);

                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "[{Scenario}] Phase 1 call generation failed", Name);
        }

        logger.LogInformation("[{Scenario}] Waiting 40s for Phase 1 calls to complete", Name);
        await Task.Delay(TimeSpan.FromSeconds(40), ct);

        // Phase 2: Send "manager reload" to force-disconnect AMI
        logger.LogInformation("[{Scenario}] Phase 2: Sending 'manager reload' to force AMI disconnect", Name);
        try
        {
            await context.SdkRuntime.Connection.SendActionAsync(
                new CommandAction { Command = "manager reload" }, ct);
            logger.LogInformation("[{Scenario}] 'manager reload' sent successfully", Name);
        }
        catch (Exception ex)
        {
            // Connection may drop immediately — this is expected
            logger.LogWarning(ex, "[{Scenario}] 'manager reload' threw (connection may have dropped immediately)", Name);
        }

        // Phase 3: Wait for disconnect detection, then poll for reconnect
        logger.LogInformation("[{Scenario}] Phase 3: Waiting 3s for disconnect detection", Name);
        await Task.Delay(TimeSpan.FromSeconds(3), ct);

        bool reconnected = false;
        int elapsed = 0;

        logger.LogInformation("[{Scenario}] Polling for reconnect (timeout={Timeout}ms)", Name, ReconnectTimeoutMs);
        while (elapsed < ReconnectTimeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await context.SdkRuntime.Connection.SendActionAsync<CommandResponse>(
                    new CommandAction { Command = "core show version" }, ct);
                reconnected = true;
                logger.LogInformation("[{Scenario}] Reconnected after {Elapsed}ms", Name, elapsed);
                break;
            }
            catch
            {
                await Task.Delay(ReconnectPollMs, ct);
                elapsed += ReconnectPollMs;
            }
        }

        if (!reconnected)
        {
            logger.LogWarning("[{Scenario}] Did not reconnect within {Timeout}ms — skipping Phase 4", Name, ReconnectTimeoutMs);
        }

        // Phase 4: Post-reconnect calls (only if reconnected)
        if (reconnected)
        {
            logger.LogInformation(
                "[{Scenario}] Phase 4: Generating {Count} post-reconnect calls to extension 105",
                Name, PostReconnectCalls);

            try
            {
                for (int i = 0; i < PostReconnectCalls; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var result = await context.CallGenerator.GenerateCallAsync("105", cancellationToken: ct);
                    context.EventCapture.RegisterCall(result.CallId, result.Caller.Number, result.Destination, result.Timestamp);
                    context.Metrics.RecordCallOriginated();

                    if (result.Accepted)
                        logger.LogDebug("[{Scenario}] Phase 4 call {N}/{Total} accepted: {CallId}", Name, i + 1, PostReconnectCalls, result.CallId);
                    else
                        logger.LogWarning("[{Scenario}] Phase 4 call {N}/{Total} rejected: {Error}", Name, i + 1, PostReconnectCalls, result.ErrorMessage);

                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "[{Scenario}] Phase 4 call generation failed", Name);
            }

            logger.LogInformation("[{Scenario}] Waiting 40s for Phase 4 calls to complete", Name);
            await Task.Delay(TimeSpan.FromSeconds(40), ct);
        }

        // Stop session capture
        if (context.SessionCapture is not null)
        {
            await context.SessionCapture.StopAsync();
            logger.LogInformation(
                "[{Scenario}] SessionCapture stopped. Captured={Count}",
                Name, context.SessionCapture.CompletedSessionCount);
        }

        context.TestEndTime = DateTime.UtcNow;
    }

    public async Task<ValidationReport> ValidateAsync(TestContext context, CancellationToken ct)
    {
        // Wait for Asterisk to flush CDR to PostgreSQL
        await Task.Delay(TimeSpan.FromSeconds(3), ct);

        var results = new List<ValidationResult>();

        if (context.SdkRuntime is null || context.SessionCapture is null)
        {
            results.Add(new ValidationResult
            {
                CallId = "sdk-reconnect",
                ValidatorName = nameof(SdkReconnectScenario),
                Passed = false,
                Checks =
                [
                    new ValidationCheck
                    {
                        CheckName = "SdkInfrastructureAvailable",
                        Passed = false,
                        Message = "SdkRuntime and SessionCapture are required for this scenario"
                    }
                ]
            });

            return BuildReport(context, results);
        }

        var sessions = context.SessionCapture.GetCompletedSessions();

        // Check 1: ConnectionAlive — verify the AMI connection is responsive
        bool connectionAlive;
        try
        {
            await context.SdkRuntime.Connection.SendActionAsync<CommandResponse>(
                new CommandAction { Command = "core show version" }, ct);
            connectionAlive = true;
        }
        catch
        {
            connectionAlive = false;
        }

        results.Add(new ValidationResult
        {
            CallId = "reconnect-aggregate",
            ValidatorName = nameof(SdkReconnectScenario),
            Passed = connectionAlive,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "ConnectionAlive",
                    Passed = connectionAlive,
                    Expected = "true",
                    Actual = connectionAlive.ToString(),
                    Message = connectionAlive ? null : "AMI connection is not alive after reconnect test"
                }
            ]
        });

        // Check 2: PreDisconnectSessionsExist — at least PreDisconnectCalls sessions
        bool preDisconnectOk = sessions.Count >= PreDisconnectCalls;
        results.Add(new ValidationResult
        {
            CallId = "reconnect-aggregate",
            ValidatorName = nameof(SdkReconnectScenario),
            Passed = preDisconnectOk,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "PreDisconnectSessionsExist",
                    Passed = preDisconnectOk,
                    Expected = $">= {PreDisconnectCalls}",
                    Actual = sessions.Count.ToString(),
                    Message = preDisconnectOk ? null : $"Expected at least {PreDisconnectCalls} pre-disconnect sessions but captured {sessions.Count}"
                }
            ]
        });

        // Check 3: PostReconnectSessionsExist — total sessions >= pre + post
        int expectedTotal = PreDisconnectCalls + PostReconnectCalls;
        bool postReconnectOk = sessions.Count >= expectedTotal;
        results.Add(new ValidationResult
        {
            CallId = "reconnect-aggregate",
            ValidatorName = nameof(SdkReconnectScenario),
            Passed = postReconnectOk,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "PostReconnectSessionsExist",
                    Passed = postReconnectOk,
                    Expected = $">= {expectedTotal}",
                    Actual = sessions.Count.ToString(),
                    Message = postReconnectOk ? null : $"Expected at least {expectedTotal} total sessions (pre + post reconnect) but captured {sessions.Count}"
                }
            ]
        });

        return BuildReport(context, results);
    }

    /// <summary>
    /// Polls AMI "core show channels count" until active channels reach 0, or times out after 60s.
    /// Prevents interference from channels left by previous scenario runs.
    /// </summary>
    private static async Task WaitForChannelsDrainedAsync(
        IAmiConnection connection, ILogger logger, CancellationToken ct)
    {
        const int timeoutMs = 60_000;
        const int pollMs = 3_000;
        int elapsed = 0;

        while (elapsed < timeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var response = await connection.SendActionAsync<CommandResponse>(
                    new CommandAction { Command = "core show channels count" }, ct);
                int channels = ParseFirstInteger(response.Output ?? "");

                if (channels == 0)
                {
                    logger.LogDebug("Channels drained after {Elapsed}ms", elapsed);
                    return;
                }

                logger.LogDebug("Waiting for {Channels} active channels to drain...", channels);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Channel drain query failed, retrying...");
            }

            await Task.Delay(pollMs, ct);
            elapsed += pollMs;
        }

        logger.LogWarning("Channel drain timed out after {Timeout}ms — proceeding anyway", timeoutMs);
    }

    private static int ParseFirstInteger(string text)
    {
        int start = -1;
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsDigit(text[i])) { start = i; break; }
        }
        if (start < 0) return 0;
        int end = start;
        while (end < text.Length && char.IsDigit(text[end])) end++;
        return int.TryParse(text[start..end], out int value) ? value : 0;
    }

    private static ValidationReport BuildReport(TestContext context, List<ValidationResult> results) => new()
    {
        TestStart = context.TestStartTime,
        TestEnd = context.TestEndTime,
        Duration = context.TestEndTime - context.TestStartTime,
        TotalCalls = 0, // Reconnect scenario validates infrastructure, not individual calls
        TotalChecks = results.SelectMany(r => r.Checks).Count(),
        PassedChecks = results.SelectMany(r => r.Checks).Count(c => c.Passed),
        FailedChecks = results.SelectMany(r => r.Checks).Count(c => !c.Passed),
        Results = results
    };
}
