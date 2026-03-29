using Microsoft.Extensions.Logging;
using PbxAdmin.LoadTests.Validation;

namespace PbxAdmin.LoadTests.Scenarios.Sdk;

/// <summary>
/// Level 3 scale test. Runs 200 agents, ramps to 300 concurrent calls in 1 minute,
/// sustains for 4 minutes, then validates that session capture accuracy is >= 98%
/// versus CDR records at scale, with duration accuracy >= 95% for matched pairs.
/// </summary>
public sealed class SdkScaleSessionsScenario : SdkScenarioBase
{
    private const string QueueName = "loadtest";
    private const int DefaultMaxConcurrent = 300;
    private const int DefaultDurationMinutes = 5;
    private const int MaxSessionSample = 100;

    public override string Name => "sdk-scale-sessions";
    public override string Level => "scale";
    public override string Description => "200 agents, 300 concurrent — session accuracy >= 98% vs CDR at scale";

    public override async Task ExecuteAsync(TestContext context, CancellationToken ct)
    {
        var logger = context.LoggerFactory.CreateLogger<SdkScaleSessionsScenario>();
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

        // ── Start LiveStateValidator ─────────────────────────────────────────
        await context.LiveStateValidator.StartAsync(
            context.SdkRuntime.Server,
            context.SdkRuntime.Connection,
            intervalSeconds: 3,
            ct,
            queueName: QueueName);

        logger.LogInformation(
            "[{Scenario}] LiveStateValidator started (interval=3s, queue={Queue})",
            Name, QueueName);

        // ── Start scheduler ──────────────────────────────────────────────────
        int maxConcurrent = context.CallPattern.MaxConcurrentCalls > 0
            ? context.CallPattern.MaxConcurrentCalls
            : DefaultMaxConcurrent;

        logger.LogInformation(
            "[{Scenario}] Starting scheduler: maxConcurrent={MaxConcurrent}",
            Name, maxConcurrent);

        await context.Scheduler.StartAsync(maxConcurrent, ct);

        // ── Enter sustain phase ──────────────────────────────────────────────
        context.Metrics.EnterSustainPhase();

        logger.LogInformation("[{Scenario}] Entered sustain phase", Name);

        // ── Wait for test duration ───────────────────────────────────────────
        int durationMinutes = context.CallPattern.TestDurationMinutes >= DefaultDurationMinutes
            ? context.CallPattern.TestDurationMinutes
            : DefaultDurationMinutes;

        logger.LogInformation(
            "[{Scenario}] Running for {Duration}m with {Concurrent} concurrent calls",
            Name, durationMinutes, maxConcurrent);

        await Task.Delay(TimeSpan.FromMinutes(durationMinutes), ct);

        // ── Drain phase ──────────────────────────────────────────────────────
        context.Metrics.EnterDrainPhase();
        await context.Scheduler.StopAsync();

        logger.LogInformation("[{Scenario}] Entered drain phase, scheduler stopped", Name);

        await WaitForDrainAsync(context.SdkRuntime.Connection, logger, ct, timeoutMs: 120_000);

        // ── Stop validator and session capture ───────────────────────────────
        await context.LiveStateValidator.StopAsync();

        logger.LogInformation(
            "[{Scenario}] LiveStateValidator stopped. Collected={Count} samples",
            Name, context.LiveStateValidator.GetSamples().Count);

        if (context.SessionCapture is not null)
        {
            await context.SessionCapture.StopAsync();
            logger.LogInformation(
                "[{Scenario}] SessionCapture stopped. Captured={Count}",
                Name, context.SessionCapture.CompletedSessionCount);
        }

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
                ValidatorName = nameof(SdkScaleSessionsScenario),
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

        var allSessions = context.SessionCapture.GetCompletedSessions();

        // ── Check 1: TotalSessionsCaptured >= 50 ─────────────────────────────
        bool capturedOk = allSessions.Count >= 50;
        results.Add(new ValidationResult
        {
            CallId = "aggregate",
            ValidatorName = nameof(SdkScaleSessionsScenario),
            Passed = capturedOk,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "TotalSessionsCaptured",
                    Passed = capturedOk,
                    Expected = ">= 50",
                    Actual = allSessions.Count.ToString(),
                    Message = capturedOk
                        ? null
                        : $"Expected at least 50 captured sessions but captured {allSessions.Count} — scale target not reached"
                }
            ]
        });

        // ── Sample up to MaxSessionSample sessions for CDR validation ─────────
        var sample = allSessions
            .Where(s => s.CallerNumber is not null)
            .Take(MaxSessionSample)
            .ToList();

        int cdrMatched = 0;
        var matchedPairs = new List<(double SdkSeconds, int BillSec)>();

        foreach (var session in sample)
        {
            try
            {
                var cdr = await context.CdrReader.GetCallBySrcAsync(
                    session.CallerNumber!, context.TestStartTime, ct);

                if (cdr is not null)
                {
                    cdrMatched++;
                    if (session.Duration is not null && cdr.BillSec > 0)
                        matchedPairs.Add((session.Duration.Value.TotalSeconds, cdr.BillSec));
                }
            }
            catch (Exception ex)
            {
                results.Add(new ValidationResult
                {
                    CallId = session.SessionId,
                    ValidatorName = nameof(SdkScaleSessionsScenario),
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

        // ── Check 2: CdrCoverage >= 98% ──────────────────────────────────────
        double coveragePct = sample.Count > 0
            ? (double)cdrMatched / sample.Count * 100.0
            : 100.0;
        bool coverageOk = coveragePct >= 98.0;
        results.Add(new ValidationResult
        {
            CallId = "aggregate",
            ValidatorName = nameof(SdkScaleSessionsScenario),
            Passed = coverageOk,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "CdrCoverage",
                    Passed = coverageOk,
                    Expected = ">= 98%",
                    Actual = $"{coveragePct:F1}% ({cdrMatched}/{sample.Count})",
                    Message = coverageOk
                        ? null
                        : $"CDR coverage {coveragePct:F1}% ({cdrMatched}/{sample.Count}) is below the 98% threshold at scale"
                }
            ]
        });

        // ── Check 3: DurationAccuracy >= 95% of matched pairs within 2s ──────
        int accurateCount = matchedPairs.Count(p => Math.Abs(p.SdkSeconds - p.BillSec) <= 2.0);
        double durationAccuracyPct = matchedPairs.Count > 0
            ? (double)accurateCount / matchedPairs.Count * 100.0
            : 100.0;
        bool durationAccuracyOk = durationAccuracyPct >= 95.0;
        results.Add(new ValidationResult
        {
            CallId = "aggregate",
            ValidatorName = nameof(SdkScaleSessionsScenario),
            Passed = durationAccuracyOk,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "DurationAccuracy",
                    Passed = durationAccuracyOk,
                    Expected = ">= 95% of matched pairs within 2s",
                    Actual = $"{durationAccuracyPct:F1}% ({accurateCount}/{matchedPairs.Count})",
                    Message = durationAccuracyOk
                        ? null
                        : $"Duration accuracy {durationAccuracyPct:F1}% ({accurateCount}/{matchedPairs.Count}) is below the 95% threshold at scale"
                }
            ]
        });

        // ── Check 4: Leak detection ───────────────────────────────────────────
        var leakResults = await DetectLeaksAsync(context, ct);
        results.AddRange(leakResults);

        return BuildReport(context, results);
    }
}
