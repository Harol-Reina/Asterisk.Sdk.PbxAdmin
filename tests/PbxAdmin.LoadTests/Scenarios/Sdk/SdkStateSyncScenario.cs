using Microsoft.Extensions.Logging;
using PbxAdmin.LoadTests.Validation;

namespace PbxAdmin.LoadTests.Scenarios.Sdk;

/// <summary>
/// Level 2 functional test. Runs 20 agents at 15 concurrent calls for 3 minutes and
/// validates that SDK in-memory state stays in sync with Asterisk's AMI ground truth.
/// Drift thresholds: channels &lt; 2%, queue members &gt;= 98% match, agent states &gt;= 98% match.
/// </summary>
public sealed class SdkStateSyncScenario : SdkScenarioBase
{
    private const string QueueName = "loadtest";
    private const int DefaultDurationMinutes = 3;
    private const int DefaultMaxConcurrent = 15;

    public override string Name => "sdk-state-sync";
    public override string Description => "3-min sustained load — validates Channels + Queues + Agents drift < 2% vs AMI ground truth";
    public override string Level => "functional";

    public override async Task ExecuteAsync(TestContext context, CancellationToken ct)
    {
        var logger = context.LoggerFactory.CreateLogger<SdkStateSyncScenario>();
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

        // ── Start scheduler (immediate full load, no ramp-up) ────────────────
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

        await WaitForDrainAsync(context.SdkRuntime.Connection, logger, ct, timeoutMs: 60_000);

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
                ValidatorName = nameof(SdkStateSyncScenario),
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

        var samples = context.LiveStateValidator.GetSamples();
        var summary = context.LiveStateValidator.GetSummary();

        // ── Check 1: ChannelDriftRate < 2.0% ────────────────────────────────
        bool channelDriftRateOk = summary.DriftRate < 2.0;
        results.Add(new ValidationResult
        {
            CallId = "aggregate",
            ValidatorName = nameof(SdkStateSyncScenario),
            Passed = channelDriftRateOk,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "ChannelDriftRate",
                    Passed = channelDriftRateOk,
                    Expected = "< 2.0%",
                    Actual = $"{summary.DriftRate:F2}%",
                    Message = channelDriftRateOk
                        ? null
                        : $"Channel drift rate {summary.DriftRate:F2}% exceeds the 2% threshold under sustained load"
                }
            ]
        });

        // ── Check 2: ChannelMaxDrift <= 4 ────────────────────────────────────
        bool channelMaxDriftOk = summary.MaxDrift <= 4;
        results.Add(new ValidationResult
        {
            CallId = "aggregate",
            ValidatorName = nameof(SdkStateSyncScenario),
            Passed = channelMaxDriftOk,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "ChannelMaxDrift",
                    Passed = channelMaxDriftOk,
                    Expected = "<= 4",
                    Actual = summary.MaxDrift.ToString(),
                    Message = channelMaxDriftOk
                        ? null
                        : $"Max channel drift of {summary.MaxDrift} exceeds the absolute threshold of 4"
                }
            ]
        });

        // ── Check 3: SufficientSamples >= 30 ─────────────────────────────────
        bool sufficientSamples = summary.TotalSamples >= 30;
        results.Add(new ValidationResult
        {
            CallId = "aggregate",
            ValidatorName = nameof(SdkStateSyncScenario),
            Passed = sufficientSamples,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "SufficientSamples",
                    Passed = sufficientSamples,
                    Expected = ">= 30",
                    Actual = summary.TotalSamples.ToString(),
                    Message = sufficientSamples
                        ? null
                        : $"Expected at least 30 LiveStateValidator samples but collected {summary.TotalSamples}"
                }
            ]
        });

        // ── Check 4: QueueMemberMatchRate >= 98% ─────────────────────────────
        // Match rate = percentage of samples where QueueMemberDrift <= 2
        int queueMemberMatchCount = samples.Count(s => s.QueueMemberDrift <= 2);
        double queueMemberMatchRate = samples.Count > 0
            ? (double)queueMemberMatchCount / samples.Count * 100.0
            : 100.0;
        bool queueMemberMatchOk = queueMemberMatchRate >= 98.0;
        results.Add(new ValidationResult
        {
            CallId = "aggregate",
            ValidatorName = nameof(SdkStateSyncScenario),
            Passed = queueMemberMatchOk,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "QueueMemberMatchRate",
                    Passed = queueMemberMatchOk,
                    Expected = ">= 98%",
                    Actual = $"{queueMemberMatchRate:F2}%",
                    Message = queueMemberMatchOk
                        ? null
                        : $"Queue member match rate {queueMemberMatchRate:F2}% is below the 98% threshold"
                }
            ]
        });

        // ── Check 5: QueueCallerDriftRate < 5.0% ─────────────────────────────
        bool queueCallerDriftRateOk = summary.QueueCallerDriftRate < 5.0;
        results.Add(new ValidationResult
        {
            CallId = "aggregate",
            ValidatorName = nameof(SdkStateSyncScenario),
            Passed = queueCallerDriftRateOk,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "QueueCallerDriftRate",
                    Passed = queueCallerDriftRateOk,
                    Expected = "< 5.0%",
                    Actual = $"{summary.QueueCallerDriftRate:F2}%",
                    Message = queueCallerDriftRateOk
                        ? null
                        : $"Queue caller drift rate {summary.QueueCallerDriftRate:F2}% exceeds the 5% threshold"
                }
            ]
        });

        // ── Check 6: AgentStateMatchRate >= 98% ──────────────────────────────
        // Match rate = percentage of samples where AgentDrift <= 2
        int agentMatchCount = samples.Count(s => s.AgentDrift <= 2);
        double agentStateMatchRate = samples.Count > 0
            ? (double)agentMatchCount / samples.Count * 100.0
            : 100.0;
        bool agentStateMatchOk = agentStateMatchRate >= 98.0;
        results.Add(new ValidationResult
        {
            CallId = "aggregate",
            ValidatorName = nameof(SdkStateSyncScenario),
            Passed = agentStateMatchOk,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "AgentStateMatchRate",
                    Passed = agentStateMatchOk,
                    Expected = ">= 98%",
                    Actual = $"{agentStateMatchRate:F2}%",
                    Message = agentStateMatchOk
                        ? null
                        : $"Agent state match rate {agentStateMatchRate:F2}% is below the 98% threshold"
                }
            ]
        });

        // ── Check 7: AgentMaxDrift <= 4 ───────────────────────────────────────
        bool agentMaxDriftOk = summary.MaxAgentDrift <= 4;
        results.Add(new ValidationResult
        {
            CallId = "aggregate",
            ValidatorName = nameof(SdkStateSyncScenario),
            Passed = agentMaxDriftOk,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "AgentMaxDrift",
                    Passed = agentMaxDriftOk,
                    Expected = "<= 4",
                    Actual = summary.MaxAgentDrift.ToString(),
                    Message = agentMaxDriftOk
                        ? null
                        : $"Max agent drift of {summary.MaxAgentDrift} exceeds the absolute threshold of 4"
                }
            ]
        });

        // ── Check 8: Leak detection ───────────────────────────────────────────
        var leakResults = await DetectLeaksAsync(context, ct);
        results.AddRange(leakResults);

        return BuildReport(context, results);
    }
}
