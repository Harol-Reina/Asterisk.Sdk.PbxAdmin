using Microsoft.Extensions.Logging;
using PbxAdmin.LoadTests.Validation;

namespace PbxAdmin.LoadTests.Scenarios.Sdk;

/// <summary>
/// Level 3 scale test. Runs 200 agents, ramps to 300 concurrent calls in 1 minute,
/// sustains for 4 minutes, then validates that SDK agent state drift stays below
/// 2% relative to AMI ground truth at scale, and that all agents are idle at drain.
/// </summary>
public sealed class SdkScaleAgentsScenario : SdkScenarioBase
{
    private const string QueueName = "loadtest";
    private const int DefaultMaxConcurrent = 300;
    private const int DefaultDurationMinutes = 5;

    public override string Name => "sdk-scale-agents";
    public override string Level => "scale";
    public override string Description => "200 agents, 300 concurrent — agent state drift < 2% at scale";

    public override async Task ExecuteAsync(TestContext context, CancellationToken ct)
    {
        var logger = context.LoggerFactory.CreateLogger<SdkScaleAgentsScenario>();
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
                ValidatorName = nameof(SdkScaleAgentsScenario),
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

        // ── Check 1: AgentDriftRate < 2.0% ──────────────────────────────────
        bool agentDriftOk = summary.AgentDriftRate < 2.0;
        results.Add(new ValidationResult
        {
            CallId = "aggregate",
            ValidatorName = nameof(SdkScaleAgentsScenario),
            Passed = agentDriftOk,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "AgentDriftRate",
                    Passed = agentDriftOk,
                    Expected = "< 2.0%",
                    Actual = $"{summary.AgentDriftRate:F2}%",
                    Message = agentDriftOk
                        ? null
                        : $"Agent drift rate {summary.AgentDriftRate:F2}% exceeds the 2% threshold at scale"
                }
            ]
        });

        // ── Check 2: MaxAgentDrift <= 6 ──────────────────────────────────────
        bool maxAgentDriftOk = summary.MaxAgentDrift <= 6;
        results.Add(new ValidationResult
        {
            CallId = "aggregate",
            ValidatorName = nameof(SdkScaleAgentsScenario),
            Passed = maxAgentDriftOk,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "MaxAgentDrift",
                    Passed = maxAgentDriftOk,
                    Expected = "<= 6",
                    Actual = summary.MaxAgentDrift.ToString(),
                    Message = maxAgentDriftOk
                        ? null
                        : $"Max agent drift of {summary.MaxAgentDrift} exceeds the absolute threshold of 6 at scale"
                }
            ]
        });

        // ── Check 3: FinalState_NoRinging ─────────────────────────────────────
        int ringingAgents = context.AgentPool.RingingAgents;
        bool noRinging = ringingAgents == 0;
        results.Add(new ValidationResult
        {
            CallId = "aggregate",
            ValidatorName = nameof(SdkScaleAgentsScenario),
            Passed = noRinging,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "FinalState_NoRinging",
                    Passed = noRinging,
                    Expected = "0",
                    Actual = ringingAgents.ToString(),
                    Message = noRinging
                        ? null
                        : $"{ringingAgents} agent(s) still in Ringing state after drain — expected 0"
                }
            ]
        });

        // ── Check 4: FinalState_NoInCall ──────────────────────────────────────
        int inCallAgents = context.AgentPool.InCallAgents;
        bool noInCall = inCallAgents == 0;
        results.Add(new ValidationResult
        {
            CallId = "aggregate",
            ValidatorName = nameof(SdkScaleAgentsScenario),
            Passed = noInCall,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "FinalState_NoInCall",
                    Passed = noInCall,
                    Expected = "0",
                    Actual = inCallAgents.ToString(),
                    Message = noInCall
                        ? null
                        : $"{inCallAgents} agent(s) still in InCall/OnHold state after drain — expected 0"
                }
            ]
        });

        // ── Check 5: Leak detection ───────────────────────────────────────────
        var leakResults = await DetectLeaksAsync(context, ct);
        results.AddRange(leakResults);

        return BuildReport(context, results);
    }
}
