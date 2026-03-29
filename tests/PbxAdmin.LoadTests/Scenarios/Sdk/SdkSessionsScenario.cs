using Microsoft.Extensions.Logging;
using PbxAdmin.LoadTests.Sdk;
using PbxAdmin.LoadTests.Validation;

namespace PbxAdmin.LoadTests.Scenarios.Sdk;

/// <summary>
/// Level 2 functional scenario. Generates 30 calls across 5 sequential phases,
/// then validates CallSession lifecycle accuracy against CDR data.
///
/// Phase 1: 15 answered calls to ext 105 (agents available)
/// Phase 2:  5 timeout calls to ext 105 (agents paused → ring timeout)
/// Phase 3:  5 failed calls to ext 999 (Congestion)
/// Phase 4:  3 hold calls to ext 105 (answered then held)
/// Phase 5:  2 transfer calls to ext 105 (answered then transferred)
///
/// Validation checks:
///   - MinSessionCount     >= 15 (at least the answered calls)
///   - CdrCoverage         >= 98% (each session matched to a CDR row)
///   - DurationMatch       |SDK duration - CDR billsec| <= 2 s per matched pair
///   - LeakDetection       no Asterisk resource or agent-pool leaks
/// </summary>
public sealed class SdkSessionsScenario : SdkScenarioBase
{
    public override string Name        => "sdk-sessions";
    public override string Description => "30 calls across 5 phases (answered/timeout/failed/hold/transfer) — validates CallSession lifecycle vs CDR";
    public override string Level       => "functional";

    public override async Task ExecuteAsync(TestContext context, CancellationToken ct)
    {
        var logger = context.LoggerFactory.CreateLogger<SdkSessionsScenario>();

        if (context.SdkRuntime is null)
        {
            logger.LogError("[{Scenario}] SDK runtime not available — cannot run", Name);
            throw new InvalidOperationException("SdkRuntime is required for this scenario");
        }

        if (context.SessionCapture is null)
        {
            logger.LogError("[{Scenario}] SessionCapture not available — cannot run", Name);
            throw new InvalidOperationException("SessionCapture is required for this scenario");
        }

        context.TestStartTime = DateTime.UtcNow;
        var connection = context.SdkRuntime.Connection;

        // ── Phase 1: 15 answered calls ───────────────────────────────────────
        logger.LogInformation("[{Scenario}] Phase 1: 15 answered calls to ext 105", Name);
        await GenerateCallsAsync(context, "105", 15, logger, ct);

        logger.LogInformation("[{Scenario}] Waiting 45s for Phase 1 calls to complete", Name);
        await Task.Delay(TimeSpan.FromSeconds(45), ct);

        logger.LogInformation("[{Scenario}] Draining channels after Phase 1", Name);
        await WaitForDrainAsync(connection, logger, ct);

        // ── Phase 2: 5 timeout calls (agents paused) ─────────────────────────
        logger.LogInformation("[{Scenario}] Phase 2: pausing agents, 5 timeout calls to ext 105", Name);
        await SetAgentsPausedAsync(context, paused: true, logger, ct);
        await Task.Delay(TimeSpan.FromSeconds(2), ct);

        await GenerateCallsAsync(context, "105", 5, logger, ct);

        logger.LogInformation("[{Scenario}] Waiting 50s for Phase 2 calls to time out", Name);
        await Task.Delay(TimeSpan.FromSeconds(50), ct);

        await SetAgentsPausedAsync(context, paused: false, logger, ct);

        logger.LogInformation("[{Scenario}] Draining channels after Phase 2", Name);
        await WaitForDrainAsync(connection, logger, ct);

        // ── Phase 3: 5 failed calls (ext 999 → Congestion) ───────────────────
        logger.LogInformation("[{Scenario}] Phase 3: 5 failed calls to ext 999", Name);
        await GenerateCallsAsync(context, "999", 5, logger, ct);

        logger.LogInformation("[{Scenario}] Waiting 15s for Phase 3 calls to resolve", Name);
        await Task.Delay(TimeSpan.FromSeconds(15), ct);

        logger.LogInformation("[{Scenario}] Draining channels after Phase 3", Name);
        await WaitForDrainAsync(connection, logger, ct);

        // ── Phase 4: 3 hold calls ────────────────────────────────────────────
        logger.LogInformation("[{Scenario}] Phase 4: 3 hold calls to ext 105", Name);
        await GenerateCallsAsync(context, "105", 3, logger, ct);

        logger.LogInformation("[{Scenario}] Waiting 20s for Phase 4 calls to complete", Name);
        await Task.Delay(TimeSpan.FromSeconds(20), ct);

        logger.LogInformation("[{Scenario}] Draining channels after Phase 4", Name);
        await WaitForDrainAsync(connection, logger, ct);

        // ── Phase 5: 2 transfer calls ────────────────────────────────────────
        logger.LogInformation("[{Scenario}] Phase 5: 2 transfer calls to ext 105", Name);
        await GenerateCallsAsync(context, "105", 2, logger, ct);

        logger.LogInformation("[{Scenario}] Waiting 30s for Phase 5 calls to complete", Name);
        await Task.Delay(TimeSpan.FromSeconds(30), ct);

        logger.LogInformation("[{Scenario}] Draining channels after Phase 5", Name);
        await WaitForDrainAsync(connection, logger, ct);

        // ── Stop SessionCapture ──────────────────────────────────────────────
        await context.SessionCapture.StopAsync();
        logger.LogInformation("[{Scenario}] SessionCapture stopped. Captured={Count}",
            Name, context.SessionCapture.CompletedSessionCount);

        context.TestEndTime = DateTime.UtcNow;
    }

    public override async Task<ValidationReport> ValidateAsync(TestContext context, CancellationToken ct)
    {
        var results = new List<ValidationResult>();

        if (context.SessionCapture is null)
        {
            results.Add(new ValidationResult
            {
                CallId = "system",
                ValidatorName = nameof(SdkSessionsScenario),
                Passed = false,
                Checks =
                [
                    new ValidationCheck
                    {
                        CheckName = "InfrastructureAvailable",
                        Passed = false,
                        Message = "SessionCapture is required for this scenario"
                    }
                ]
            });
            return BuildReport(context, results);
        }

        var sessions = context.SessionCapture.GetCompletedSessions();

        // ── Check 1: MinSessionCount >= 15 ───────────────────────────────────
        bool sessionCountOk = sessions.Count >= 15;
        results.Add(new ValidationResult
        {
            CallId = "aggregate",
            ValidatorName = nameof(SdkSessionsScenario),
            Passed = sessionCountOk,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "MinSessionCount",
                    Passed = sessionCountOk,
                    Expected = ">= 15",
                    Actual = sessions.Count.ToString(),
                    Message = sessionCountOk
                        ? null
                        : $"Expected at least 15 captured sessions (answered calls) but captured {sessions.Count}"
                }
            ]
        });

        // ── Check 2: CdrCoverage >= 98% ──────────────────────────────────────
        // For each session that has a caller number, attempt a CDR lookup.
        // Sessions without a caller number are excluded from the coverage denominator.
        var lookupableSessions = sessions
            .Where(s => s.CallerNumber is not null)
            .ToList();

        int cdrMatched = 0;
        var matchedPairs = new List<(CallSessionSnapshot Session, int BillSec)>();

        foreach (var session in lookupableSessions)
        {
            try
            {
                var cdr = await context.CdrReader.GetCallBySrcAsync(
                    session.CallerNumber!, context.TestStartTime, ct);

                if (cdr is not null)
                {
                    cdrMatched++;
                    matchedPairs.Add((session, cdr.BillSec));
                }
            }
            catch (Exception ex)
            {
                results.Add(new ValidationResult
                {
                    CallId = session.SessionId,
                    ValidatorName = nameof(SdkSessionsScenario),
                    Passed = false,
                    Checks =
                    [
                        new ValidationCheck
                        {
                            CheckName = "CdrLookup",
                            Passed = false,
                            Message = $"CDR lookup threw for caller {session.CallerNumber}: {ex.Message}"
                        }
                    ]
                });
            }
        }

        double coveragePct = lookupableSessions.Count > 0
            ? (double)cdrMatched / lookupableSessions.Count * 100.0
            : 100.0;

        bool coverageOk = coveragePct >= 98.0;
        results.Add(new ValidationResult
        {
            CallId = "aggregate",
            ValidatorName = nameof(SdkSessionsScenario),
            Passed = coverageOk,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "CdrCoverage",
                    Passed = coverageOk,
                    Expected = ">= 98%",
                    Actual = $"{coveragePct:F1}% ({cdrMatched}/{lookupableSessions.Count})",
                    Message = coverageOk
                        ? null
                        : $"CDR coverage {coveragePct:F1}% ({cdrMatched}/{lookupableSessions.Count}) is below the 98% threshold"
                }
            ]
        });

        // ── Check 3: DurationMatch — |SDK duration - CDR billsec| <= 2s ──────
        foreach (var (session, billSec) in matchedPairs)
        {
            // Only compare sessions that have a measured Duration and were answered
            // (billsec is 0 for unanswered calls, which would create false failures).
            if (session.Duration is null || billSec == 0)
                continue;

            double sdkSeconds  = session.Duration.Value.TotalSeconds;
            double diffSeconds = Math.Abs(sdkSeconds - billSec);
            bool durationOk    = diffSeconds <= 2.0;

            results.Add(new ValidationResult
            {
                CallId = session.SessionId,
                ValidatorName = nameof(SdkSessionsScenario),
                Passed = durationOk,
                Checks =
                [
                    new ValidationCheck
                    {
                        CheckName = "DurationMatch",
                        Passed = durationOk,
                        Expected = "|SDK - CDR billsec| <= 2s",
                        Actual = $"{diffSeconds:F1}s diff (SDK={sdkSeconds:F1}s, CDR={billSec}s)",
                        Message = durationOk
                            ? null
                            : $"Duration mismatch for session {session.SessionId}: SDK={sdkSeconds:F1}s, CDR billsec={billSec}s, diff={diffSeconds:F1}s"
                    }
                ]
            });
        }

        // ── Check 4: LeakDetection ────────────────────────────────────────────
        var leakResults = await DetectLeaksAsync(context, ct);
        results.AddRange(leakResults);

        return BuildReport(context, results);
    }
}
