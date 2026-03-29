using Microsoft.Extensions.Logging;
using PbxAdmin.LoadTests.Validation;

namespace PbxAdmin.LoadTests.Scenarios.Sdk;

/// <summary>
/// Level 3 scale test. Runs 200 agents, ramps to 300 concurrent calls in 1 minute,
/// sustains for 4 minutes, then validates that SDK queue member and caller drift
/// stays below 2% relative to AMI ground truth at scale.
/// </summary>
public sealed class SdkScaleQueuesScenario : SdkScenarioBase
{
    private const string QueueName = "loadtest";
    private const int DefaultMaxConcurrent = 300;
    private const int DefaultDurationMinutes = 5;

    public override string Name => "sdk-scale-queues";
    public override string Level => "scale";
    public override string Description => "200 agents, 300 concurrent — queue member/caller drift < 2% at scale";

    public override async Task ExecuteAsync(TestContext context, CancellationToken ct)
    {
        var logger = context.LoggerFactory.CreateLogger<SdkScaleQueuesScenario>();
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

        if (context.LiveStateValidator is null)
        {
            results.Add(new ValidationResult
            {
                CallId = "system",
                ValidatorName = nameof(SdkScaleQueuesScenario),
                Passed = false,
                Checks =
                [
                    new ValidationCheck
                    {
                        CheckName = "InfrastructureAvailable",
                        Passed = false,
                        Message = "LiveStateValidator is required for this scenario"
                    }
                ]
            });
            return BuildReport(context, results);
        }

        var summary = context.LiveStateValidator.GetSummary();

        // ── Check 1: MemberDriftRate < 2.0% ─────────────────────────────────
        bool memberDriftOk = summary.QueueMemberDriftRate < 2.0;
        results.Add(new ValidationResult
        {
            CallId = "aggregate",
            ValidatorName = nameof(SdkScaleQueuesScenario),
            Passed = memberDriftOk,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "MemberDriftRate",
                    Passed = memberDriftOk,
                    Expected = "< 2.0%",
                    Actual = $"{summary.QueueMemberDriftRate:F2}%",
                    Message = memberDriftOk
                        ? null
                        : $"Queue member drift rate {summary.QueueMemberDriftRate:F2}% exceeds the 2% threshold at scale"
                }
            ]
        });

        // ── Check 2: CallerDriftRate < 2.0% ─────────────────────────────────
        bool callerDriftOk = summary.QueueCallerDriftRate < 2.0;
        results.Add(new ValidationResult
        {
            CallId = "aggregate",
            ValidatorName = nameof(SdkScaleQueuesScenario),
            Passed = callerDriftOk,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "CallerDriftRate",
                    Passed = callerDriftOk,
                    Expected = "< 2.0%",
                    Actual = $"{summary.QueueCallerDriftRate:F2}%",
                    Message = callerDriftOk
                        ? null
                        : $"Queue caller drift rate {summary.QueueCallerDriftRate:F2}% exceeds the 2% threshold at scale"
                }
            ]
        });

        // ── Check 3: MaxMemberDrift <= 6 ─────────────────────────────────────
        bool maxMemberDriftOk = summary.MaxQueueMemberDrift <= 6;
        results.Add(new ValidationResult
        {
            CallId = "aggregate",
            ValidatorName = nameof(SdkScaleQueuesScenario),
            Passed = maxMemberDriftOk,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "MaxMemberDrift",
                    Passed = maxMemberDriftOk,
                    Expected = "<= 6",
                    Actual = summary.MaxQueueMemberDrift.ToString(),
                    Message = maxMemberDriftOk
                        ? null
                        : $"Max queue member drift of {summary.MaxQueueMemberDrift} exceeds the absolute threshold of 6 at scale"
                }
            ]
        });

        // ── Check 4: SLA_80_30s — informational, always passes ───────────────
        results.Add(new ValidationResult
        {
            CallId = "aggregate",
            ValidatorName = nameof(SdkScaleQueuesScenario),
            Passed = true,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "SLA_80_30s",
                    Passed = true,
                    Expected = "informational",
                    Actual = "see audit",
                    Message = null
                }
            ]
        });

        // ── Check 5: Leak detection ───────────────────────────────────────────
        var leakResults = await DetectLeaksAsync(context, ct);
        results.AddRange(leakResults);

        return BuildReport(context, results);
    }
}
