using Microsoft.Extensions.Logging;
using PbxAdmin.LoadTests.Sdk;
using PbxAdmin.LoadTests.Validation;

namespace PbxAdmin.LoadTests.Scenarios.Sdk;

/// <summary>
/// Level 1 smoke test. Generates 5 calls across 3 dispositions and quickly validates
/// SDK channel tracking, queue state, agent state, session capture, and CDR existence.
/// Phase 1: 3 answered calls to ext 105 (agents available)
/// Phase 2: 1 timeout call to ext 105 (agents paused → ring timeout)
/// Phase 3: 1 failed call to ext 999 (Congestion)
/// </summary>
public sealed class SdkSmokeScenario : SdkScenarioBase
{
    public override string Name => "sdk-smoke";
    public override string Description => "5 calls (3 answered, 1 timeout, 1 failed) — quick validation of channels, queues, agents, sessions";
    public override string Level => "smoke";

    public override async Task ExecuteAsync(TestContext context, CancellationToken ct)
    {
        var logger = context.LoggerFactory.CreateLogger<SdkSmokeScenario>();
        context.TestStartTime = DateTime.UtcNow;

        if (context.SdkRuntime is null)
        {
            logger.LogError("[{Scenario}] SDK runtime not available — cannot run", Name);
            throw new InvalidOperationException("SdkRuntime is required for this scenario");
        }

        if (context.LiveStateValidator is null)
        {
            logger.LogError("[{Scenario}] LiveStateValidator not available — cannot run", Name);
            throw new InvalidOperationException("LiveStateValidator is required for this scenario");
        }

        const string queueName = "loadtest";

        // Start live-state sampling at 3-second intervals
        await context.LiveStateValidator.StartAsync(
            context.SdkRuntime.Server,
            context.SdkRuntime.Connection,
            intervalSeconds: 3,
            ct,
            queueName: queueName);

        logger.LogInformation("[{Scenario}] LiveStateValidator started (interval=3s, queue={Queue})", Name, queueName);

        // ── Phase 1: 3 answered calls ────────────────────────────────────────
        logger.LogInformation("[{Scenario}] Phase 1: 3 answered calls to ext 105", Name);
        await GenerateCallsAsync(context, "105", 3, logger, ct);

        logger.LogInformation("[{Scenario}] Waiting 15s for Phase 1 calls to complete", Name);
        await Task.Delay(TimeSpan.FromSeconds(15), ct);

        // ── Phase 2: 1 timeout call (agents paused) ──────────────────────────
        logger.LogInformation("[{Scenario}] Phase 2: pausing agents, 1 timeout call to ext 105", Name);
        await SetAgentsPausedAsync(context, paused: true, logger, ct);
        await Task.Delay(TimeSpan.FromSeconds(2), ct);

        await GenerateCallsAsync(context, "105", 1, logger, ct);

        logger.LogInformation("[{Scenario}] Waiting 35s for Phase 2 call to time out", Name);
        await Task.Delay(TimeSpan.FromSeconds(35), ct);

        await SetAgentsPausedAsync(context, paused: false, logger, ct);

        // ── Phase 3: 1 failed call (ext 999 → Congestion) ───────────────────
        logger.LogInformation("[{Scenario}] Phase 3: 1 failed call to ext 999", Name);
        await GenerateCallsAsync(context, "999", 1, logger, ct);

        logger.LogInformation("[{Scenario}] Waiting 10s for Phase 3 call to resolve", Name);
        await Task.Delay(TimeSpan.FromSeconds(10), ct);

        // ── Drain ────────────────────────────────────────────────────────────
        logger.LogInformation("[{Scenario}] Waiting for channels to drain (30s timeout)", Name);
        await WaitForDrainAsync(context.SdkRuntime.Connection, logger, ct, timeoutMs: 30_000);

        // ── Stop capture ─────────────────────────────────────────────────────
        await context.LiveStateValidator.StopAsync();
        logger.LogInformation("[{Scenario}] LiveStateValidator stopped. Collected={Count} samples",
            Name, context.LiveStateValidator.GetSamples().Count);

        if (context.SessionCapture is not null)
        {
            await context.SessionCapture.StopAsync();
            logger.LogInformation("[{Scenario}] SessionCapture stopped. Captured={Count}",
                Name, context.SessionCapture.CompletedSessionCount);
        }

        context.TestEndTime = DateTime.UtcNow;
    }

    public override async Task<ValidationReport> ValidateAsync(TestContext context, CancellationToken ct)
    {
        var results = new List<ValidationResult>();

        if (context.LiveStateValidator is null || context.SessionCapture is null)
        {
            results.Add(new ValidationResult
            {
                CallId = "system",
                ValidatorName = nameof(SdkSmokeScenario),
                Passed = false,
                Checks =
                [
                    new ValidationCheck
                    {
                        CheckName = "InfrastructureAvailable",
                        Passed = false,
                        Message = "LiveStateValidator and SessionCapture are required for this scenario"
                    }
                ]
            });
            return BuildReport(context, results);
        }

        var samples = context.LiveStateValidator.GetSamples();
        var summary = context.LiveStateValidator.GetSummary();
        var sessions = context.SessionCapture.GetCompletedSessions();

        // ── Check 1: ChannelDriftMax <= 1 (absolute) ─────────────────────────
        // Only 5 channels total — strict tolerance is appropriate at this scale.
        bool driftOk = summary.MaxDrift <= 1;
        results.Add(new ValidationResult
        {
            CallId = "aggregate",
            ValidatorName = nameof(SdkSmokeScenario),
            Passed = driftOk,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "ChannelDriftMax",
                    Passed = driftOk,
                    Expected = "<= 1",
                    Actual = summary.MaxDrift.ToString(),
                    Message = driftOk ? null : $"Max channel drift of {summary.MaxDrift} exceeds threshold of 1 for a 5-call smoke test"
                }
            ]
        });

        // ── Check 2: SufficientSamples >= 3 ─────────────────────────────────
        bool sufficientSamples = samples.Count >= 3;
        results.Add(new ValidationResult
        {
            CallId = "aggregate",
            ValidatorName = nameof(SdkSmokeScenario),
            Passed = sufficientSamples,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "SufficientSamples",
                    Passed = sufficientSamples,
                    Expected = ">= 3",
                    Actual = samples.Count.ToString(),
                    Message = sufficientSamples ? null : $"Expected at least 3 LiveStateValidator samples but collected {samples.Count}"
                }
            ]
        });

        // ── Check 3: SessionCount >= 3 (at least the answered calls) ─────────
        bool sessionCountOk = sessions.Count >= 3;
        results.Add(new ValidationResult
        {
            CallId = "aggregate",
            ValidatorName = nameof(SdkSmokeScenario),
            Passed = sessionCountOk,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "SessionCount",
                    Passed = sessionCountOk,
                    Expected = ">= 3",
                    Actual = sessions.Count.ToString(),
                    Message = sessionCountOk ? null : $"Expected at least 3 captured sessions but captured {sessions.Count}"
                }
            ]
        });

        // ── Check 4: CdrExists for each answered session ──────────────────────
        var answeredSessions = sessions
            .Where(s => string.Equals(s.FinalState, "Answered", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var session in answeredSessions)
        {
            bool cdrFound = false;
            string? failReason = null;

            try
            {
                if (session.CallerNumber is not null)
                {
                    var cdr = await context.CdrReader.GetCallBySrcAsync(
                        session.CallerNumber, context.TestStartTime, ct);
                    cdrFound = cdr is not null;
                    if (!cdrFound)
                        failReason = $"No CDR found for caller {session.CallerNumber} after {context.TestStartTime:u}";
                }
                else
                {
                    failReason = "Session has no CallerNumber — cannot look up CDR";
                }
            }
            catch (Exception ex)
            {
                failReason = ex.Message;
            }

            results.Add(new ValidationResult
            {
                CallId = session.SessionId,
                ValidatorName = nameof(SdkSmokeScenario),
                Passed = cdrFound,
                Checks =
                [
                    new ValidationCheck
                    {
                        CheckName = "CdrExists",
                        Passed = cdrFound,
                        Expected = "CDR row present in database",
                        Actual = cdrFound ? "found" : "not found",
                        Message = failReason
                    }
                ]
            });
        }

        // ── Check 5: LeakDetection ────────────────────────────────────────────
        var leakResults = await DetectLeaksAsync(context, ct);
        results.AddRange(leakResults);

        // ── Check 6: CompletesUnder90s ────────────────────────────────────────
        double totalSeconds = (context.TestEndTime - context.TestStartTime).TotalSeconds;
        bool completedInTime = totalSeconds <= 90.0;
        results.Add(new ValidationResult
        {
            CallId = "system",
            ValidatorName = nameof(SdkSmokeScenario),
            Passed = completedInTime,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "CompletesUnder90s",
                    Passed = completedInTime,
                    Expected = "<= 90s",
                    Actual = $"{totalSeconds:F1}s",
                    Message = completedInTime ? null : $"Smoke test took {totalSeconds:F1}s which exceeds the 90s limit"
                }
            ]
        });

        return BuildReport(context, results);
    }
}
