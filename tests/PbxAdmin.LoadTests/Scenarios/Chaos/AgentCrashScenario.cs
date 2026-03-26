using Microsoft.Extensions.Logging;
using PbxAdmin.LoadTests.Validation;
using PbxAdmin.LoadTests.Validation.Layer3;

namespace PbxAdmin.LoadTests.Scenarios.Chaos;

/// <summary>
/// Randomly kills agent SIP registrations during active calls to test SDK recovery.
/// Verifies that the SDK detects the hangup, sessions close cleanly, and agents
/// return to Idle after re-registration.
/// </summary>
public sealed class AgentCrashScenario : ITestScenario
{
    private const double LoadFraction = 0.5;
    private const int WarmUpSecs = 120;           // 2 minutes normal load before chaos
    private const int CrashIntervalSecs = 30;
    private const int AgentsToCrashPerCycle = 3;  // 1-3 agents per crash cycle
    private const int ReregisterDelaySecs = 5;
    private const int DbFlushDelaySecs = 5;
    private const int SampleLimit = 50;
    private static readonly Random Rng = new();

    public string Name => "agent-crash";
    public string Description => "Randomly kills agent SIP registrations during active calls to test SDK recovery";

    public async Task ExecuteAsync(TestContext context, CancellationToken ct)
    {
        var logger = context.LoggerFactory.CreateLogger<AgentCrashScenario>();
        context.TestStartTime = DateTime.UtcNow;

        int targetConcurrent = Math.Max(1, (int)(context.CallPattern.MaxConcurrentCalls * LoadFraction));

        logger.LogInformation(
            "[{Scenario}] Starting: target={Target}, warmUp={WarmUp}s, crashInterval={Interval}s",
            Name, targetConcurrent, WarmUpSecs, CrashIntervalSecs);

        await context.AgentPool.StartAsync(context.AgentBehavior.AgentCount, ct);
        await context.Scheduler.StartAsync(targetConcurrent, ct);

        // Phase 1: run normally during warm-up period
        logger.LogInformation("[{Scenario}] Phase 1: warm-up for {Secs}s", Name, WarmUpSecs);
        await Task.Delay(TimeSpan.FromSeconds(WarmUpSecs), ct);

        // Phase 2: chaos — crash agents on a timer for remaining duration
        var testDuration = TimeSpan.FromMinutes(context.CallPattern.TestDurationMinutes);
        var chaosEnd = context.TestStartTime + testDuration;

        logger.LogInformation("[{Scenario}] Phase 2: chaos begins", Name);

        while (DateTime.UtcNow < chaosEnd && !ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(CrashIntervalSecs), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // Pick 1-3 in-call agents to crash
            var busyAgents = context.AgentPool.GetBusyAgents().ToList();
            int crashCount = Math.Min(AgentsToCrashPerCycle, busyAgents.Count);
            if (crashCount == 0)
            {
                logger.LogDebug("[{Scenario}] No in-call agents to crash this cycle", Name);
                continue;
            }

            // Pick a random subset
            var targets = busyAgents
                .OrderBy(_ => Rng.Next())
                .Take(crashCount)
                .ToList();

            logger.LogInformation(
                "[{Scenario}] Crashing {Count} agent(s): {Exts}",
                Name, targets.Count, string.Join(", ", targets.Select(a => a.ExtensionId)));

            var crashedAgents = new List<string>();

            foreach (var agent in targets)
            {
                try
                {
                    await agent.UnregisterAsync();
                    crashedAgents.Add(agent.ExtensionId);
                    logger.LogInformation("[{Scenario}] Agent {Ext} unregistered (crash simulated)", Name, agent.ExtensionId);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[{Scenario}] Failed to crash agent {Ext}", Name, agent.ExtensionId);
                }
            }

            // Allow Asterisk to detect the hangup
            await Task.Delay(TimeSpan.FromSeconds(ReregisterDelaySecs), ct);

            // Re-register the crashed agents
            foreach (var extensionId in crashedAgents)
            {
                var agent = context.AgentPool.GetAgent(extensionId);
                if (agent is null) continue;

                try
                {
                    await agent.RegisterAsync(ct);
                    logger.LogInformation("[{Scenario}] Agent {Ext} re-registered", Name, extensionId);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[{Scenario}] Failed to re-register agent {Ext}", Name, extensionId);
                }
            }
        }

        await context.Scheduler.StopAsync();
        context.TestEndTime = DateTime.UtcNow;

        logger.LogInformation(
            "[{Scenario}] Execution complete: TotalGenerated={Generated}",
            Name, context.Scheduler.TotalCallsGenerated);
    }

    public async Task<ValidationReport> ValidateAsync(TestContext context, CancellationToken ct)
    {
        var logger = context.LoggerFactory.CreateLogger<AgentCrashScenario>();
        logger.LogInformation("[{Scenario}] Waiting {Delay}s for DB flush before validation", Name, DbFlushDelaySecs);
        await Task.Delay(TimeSpan.FromSeconds(DbFlushDelaySecs), ct);

        var results = new List<ValidationResult>();
        var sdkBugs = new List<string>();

        var allCdrs = await context.CdrReader.GetCallsForTestAsync(
            context.TestStartTime, context.TestEndTime, ct);

        int totalCalls = allCdrs.Count;
        logger.LogInformation("[{Scenario}] CDRs found: {Total}", Name, totalCalls);

        // CDRs should exist even for crashed/cut-short calls
        results.Add(new ValidationResult
        {
            CallId = "cdr-present",
            ValidatorName = nameof(AgentCrashScenario),
            Passed = totalCalls > 0,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "CdrsPresentAfterCrash",
                    Passed = totalCalls > 0,
                    Expected = "CDRs written even for crashed calls",
                    Actual = $"{totalCalls} CDR(s) found",
                    Message = totalCalls == 0 ? "No CDRs found after agent-crash test — Asterisk may not be writing CDRs" : null
                }
            ]
        });

        // Sample up to SampleLimit for 3-layer validation
        var snapshots = context.EventCapture.GetAllSnapshots();
        var sample = snapshots.Take(SampleLimit).ToList();

        foreach (var snapshot in sample)
        {
            try
            {
                var cdr = await context.CdrReader.GetCallBySrcAsync(snapshot.CallerNumber, context.TestStartTime, ct);
                var celEvents = snapshot.LinkedId is not null
                    ? await context.CelReader.GetEventSequenceAsync(snapshot.LinkedId, ct)
                    : [];
                var queueEvents = snapshot.QueueName is not null
                    ? await context.QueueLogReader.GetQueueEventsForCallAsync(snapshot.CallId, ct)
                    : [];

                results.Add(SessionValidator.ValidateCall(snapshot, cdr));
                results.Add(EventSequenceValidator.ValidateEventSequence(snapshot, celEvents));
                results.Add(QueueValidator.ValidateQueueCall(snapshot, queueEvents));
            }
            catch (Exception ex)
            {
                results.Add(new ValidationResult
                {
                    CallId = snapshot.CallId,
                    ValidatorName = nameof(AgentCrashScenario),
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

        // No orphaned sessions — agent leak check is the proxy for this
        var leakResult = LeakDetector.DetectAgentLeaks(context.AgentPool);
        results.Add(leakResult);

        if (!leakResult.Passed)
            sdkBugs.Add("Agent leak after crash scenario — agents stuck in InCall/Ringing state after crash+recovery");

        // All agents should have recovered to Idle or Offline
        var poolStats = context.AgentPool.GetStats();
        int notIdle = poolStats.Total - poolStats.Idle - (poolStats.Error);
        logger.LogInformation(
            "[{Scenario}] Final pool: Idle={Idle}, InCall={InCall}, Error={Error}",
            Name, poolStats.Idle, poolStats.InCall, poolStats.Error);

        return new ValidationReport
        {
            TestStart = context.TestStartTime,
            TestEnd = context.TestEndTime,
            Duration = context.TestEndTime - context.TestStartTime,
            TotalCalls = results.Count(r => r.ValidatorName == nameof(SessionValidator)),
            TotalChecks = results.SelectMany(r => r.Checks).Count(),
            PassedChecks = results.SelectMany(r => r.Checks).Count(c => c.Passed),
            FailedChecks = results.SelectMany(r => r.Checks).Count(c => !c.Passed),
            Results = results,
            SdkBugsFound = sdkBugs
        };
    }
}
