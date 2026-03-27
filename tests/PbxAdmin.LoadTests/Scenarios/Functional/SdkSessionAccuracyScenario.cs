using Asterisk.Sdk;
using Asterisk.Sdk.Ami.Actions;
using Microsoft.Extensions.Logging;
using PbxAdmin.LoadTests.Sdk;
using PbxAdmin.LoadTests.Validation;
using PbxAdmin.LoadTests.Validation.Layer3;

namespace PbxAdmin.LoadTests.Scenarios.Functional;

/// <summary>
/// Generates calls across 3 dispositions (answered, no-answer, failed) and validates
/// that the SDK CallSession data matches CDR records in the database.
/// Phase 1: 5 answered calls to ext 105 (queue → agent answers → ANSWERED)
/// Phase 2: 3 timeout calls to ext 105 (agents paused → ring timeout → NO ANSWER)
/// Phase 3: 2 failed calls to ext 999 (Congestion → FAILED/BUSY)
/// </summary>
public sealed class SdkSessionAccuracyScenario : ITestScenario
{
    public string Name => "sdk-session-accuracy";
    public string Description => "10 calls across 3 dispositions — validates SDK CallSession state/duration/caller vs CDR";

    public async Task ExecuteAsync(TestContext context, CancellationToken ct)
    {
        var logger = context.LoggerFactory.CreateLogger<SdkSessionAccuracyScenario>();
        context.TestStartTime = DateTime.UtcNow;

        if (context.SdkRuntime is null || context.SessionCapture is null)
        {
            logger.LogError("[{Scenario}] SDK infrastructure not available — cannot run", Name);
            throw new InvalidOperationException("SdkRuntime and SessionCapture are required for this scenario");
        }

        // ── Phase 1: 5 answered calls (ext 105, agents available) ───────────
        logger.LogInformation("[{Scenario}] Phase 1: 5 answered calls to ext 105", Name);
        await GenerateCallsAsync(context, "105", 5, logger, ct);

        logger.LogInformation("[{Scenario}] Waiting 45s for Phase 1 calls to complete", Name);
        await Task.Delay(TimeSpan.FromSeconds(45), ct);

        // ── Phase 2: 3 timeout calls (ext 105, agents paused → NO ANSWER) ──
        logger.LogInformation("[{Scenario}] Phase 2: pausing agents, 3 timeout calls to ext 105", Name);
        await SetAgentsPausedAsync(context, paused: true, logger, ct);
        await Task.Delay(TimeSpan.FromSeconds(2), ct);

        await GenerateCallsAsync(context, "105", 3, logger, ct);

        // Queue ring timeout is 30-45s, wait for it to expire
        logger.LogInformation("[{Scenario}] Waiting 50s for Phase 2 calls to time out", Name);
        await Task.Delay(TimeSpan.FromSeconds(50), ct);

        await SetAgentsPausedAsync(context, paused: false, logger, ct);

        // ── Phase 3: 2 failed calls (ext 999 → Congestion) ─────────────────
        logger.LogInformation("[{Scenario}] Phase 3: 2 failed calls to ext 999", Name);
        await GenerateCallsAsync(context, "999", 2, logger, ct);

        logger.LogInformation("[{Scenario}] Waiting 15s for Phase 3 calls to resolve", Name);
        await Task.Delay(TimeSpan.FromSeconds(15), ct);

        // ── Stop capture ────────────────────────────────────────────────────
        await context.SessionCapture.StopAsync();
        logger.LogInformation("[{Scenario}] SessionCapture stopped. Captured={Count}",
            Name, context.SessionCapture.CompletedSessionCount);

        context.TestEndTime = DateTime.UtcNow;
    }

    public async Task<ValidationReport> ValidateAsync(TestContext context, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), ct);

        var results = new List<ValidationResult>();

        if (context.SessionCapture is null)
        {
            results.Add(new ValidationResult
            {
                CallId = "sdk-session-accuracy",
                ValidatorName = nameof(SdkSessionAccuracyScenario),
                Passed = false,
                Checks =
                [
                    new ValidationCheck
                    {
                        CheckName = "SessionCaptureAvailable",
                        Passed = false,
                        Message = "SessionCapture was not available — SDK runtime is required"
                    }
                ]
            });
            return BuildReport(context, results);
        }

        var sessions = context.SessionCapture.GetCompletedSessions();

        // Validate each captured session against its CDR
        foreach (var session in sessions)
        {
            try
            {
                var cdr = session.CallerNumber is not null
                    ? await context.CdrReader.GetCallBySrcAsync(session.CallerNumber, context.TestStartTime, ct)
                    : null;

                results.Add(SessionValidator.ValidateSession(session, cdr));
            }
            catch (Exception ex)
            {
                results.Add(new ValidationResult
                {
                    CallId = session.SessionId,
                    ValidatorName = nameof(SdkSessionAccuracyScenario),
                    Passed = false,
                    Checks =
                    [
                        new ValidationCheck
                        {
                            CheckName = "ValidationException",
                            Passed = false,
                            Message = ex.Message
                        }
                    ]
                });
            }
        }

        // Aggregate check: at least 5 sessions (answered calls are guaranteed;
        // timeout/failed calls may or may not generate sessions depending on
        // whether Asterisk creates a CDR before the call enters a tracked context)
        const int expectedMinSessions = 5;
        bool allTracked = sessions.Count >= expectedMinSessions;
        results.Add(new ValidationResult
        {
            CallId = "aggregate",
            ValidatorName = nameof(SdkSessionAccuracyScenario),
            Passed = allTracked,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "AllSessionsTracked",
                    Passed = allTracked,
                    Expected = $">= {expectedMinSessions}",
                    Actual = sessions.Count.ToString(),
                    Message = allTracked ? null : $"Expected at least {expectedMinSessions} sessions but captured {sessions.Count}"
                }
            ]
        });

        return BuildReport(context, results);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task GenerateCallsAsync(
        TestContext context, string destination, int count,
        ILogger logger, CancellationToken ct)
    {
        try
        {
            for (int i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var result = await context.CallGenerator.GenerateCallAsync(destination, cancellationToken: ct);
                context.EventCapture.RegisterCall(result.CallId, result.Caller.Number, result.Destination, result.Timestamp);
                context.Metrics.RecordCallOriginated();

                if (result.Accepted)
                    logger.LogDebug("Call {N}/{Total} to {Dest} accepted: {CallId}", i + 1, count, destination, result.CallId);
                else
                    logger.LogWarning("Call {N}/{Total} to {Dest} rejected: {Error}", i + 1, count, destination, result.ErrorMessage);

                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Call generation to {Dest} failed", destination);
        }
    }

    /// <summary>
    /// Pauses or unpauses all registered agents in the loadtest queue via AMI QueuePause.
    /// Uses the SDK connection to the target PBX (not the PSTN emulator).
    /// </summary>
    private static async Task SetAgentsPausedAsync(
        TestContext context, bool paused, ILogger logger, CancellationToken ct)
    {
        var connection = context.SdkRuntime!.Connection;
        int agentCount = context.AgentPool.TotalAgents;
        string targetServer = context.Options.TargetServer;
        int baseExt = targetServer.Equals("file", StringComparison.OrdinalIgnoreCase) ? 4100 : 2100;

        logger.LogDebug("{Action} {Count} agents in loadtest queue",
            paused ? "Pausing" : "Unpausing", agentCount);

        for (int i = 0; i < agentCount; i++)
        {
            string iface = $"PJSIP/{baseExt + i}";
            try
            {
                await connection.SendActionAsync(new QueuePauseAction
                {
                    Queue = "loadtest",
                    Interface = iface,
                    Paused = paused
                }, ct);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to {Action} {Interface}", paused ? "pause" : "unpause", iface);
            }
        }
    }

    private static ValidationReport BuildReport(TestContext context, List<ValidationResult> results) => new()
    {
        TestStart = context.TestStartTime,
        TestEnd = context.TestEndTime,
        Duration = context.TestEndTime - context.TestStartTime,
        TotalCalls = results.Count(r => r.ValidatorName == nameof(SessionValidator)),
        TotalChecks = results.SelectMany(r => r.Checks).Count(),
        PassedChecks = results.SelectMany(r => r.Checks).Count(c => c.Passed),
        FailedChecks = results.SelectMany(r => r.Checks).Count(c => !c.Passed),
        Results = results
    };
}
