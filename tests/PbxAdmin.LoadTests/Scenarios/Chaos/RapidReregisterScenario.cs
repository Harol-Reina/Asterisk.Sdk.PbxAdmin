using Microsoft.Extensions.Logging;
using PbxAdmin.LoadTests.AgentEmulation;
using PbxAdmin.LoadTests.Validation;
using PbxAdmin.LoadTests.Validation.Layer3;

namespace PbxAdmin.LoadTests.Scenarios.Chaos;

/// <summary>
/// Rapidly registers and unregisters agents to test the SDK's endpoint tracking.
/// Every 5 seconds, 3 random agents are unregistered and 3 others are registered,
/// while call generation continues concurrently.
/// </summary>
public sealed class RapidReregisterScenario : ITestScenario
{
    private const int AgentCount = 10;
    private const int ChurnAgentsPerCycle = 3;
    private const int ChurnIntervalSecs = 5;
    private const int TestDurationSecs = 180; // 3 minutes
    private const double LoadFraction = 0.3;
    private const int DbFlushDelaySecs = 5;
    private const int SampleLimit = 50;
    private static readonly Random Rng = new();

    public string Name => "rapid-reregister";
    public string Description => "Rapidly registers/unregisters agents to test SDK's endpoint tracking";

    public async Task ExecuteAsync(TestContext context, CancellationToken ct)
    {
        var logger = context.LoggerFactory.CreateLogger<RapidReregisterScenario>();
        context.TestStartTime = DateTime.UtcNow;

        int effectiveAgentCount = Math.Min(AgentCount, context.AgentBehavior.MaxAgents);
        // Clamp to MinAgents if pool requires it
        effectiveAgentCount = Math.Max(effectiveAgentCount, context.AgentBehavior.MinAgents);

        int targetConcurrent = Math.Max(1, (int)(context.CallPattern.MaxConcurrentCalls * LoadFraction));

        logger.LogInformation(
            "[{Scenario}] Starting: agents={Agents}, churnEvery={Interval}s, duration={Duration}s, target={Target}",
            Name, effectiveAgentCount, ChurnIntervalSecs, TestDurationSecs, targetConcurrent);

        logger.LogInformation(
            "[{Scenario}] Using pre-registered agent pool: {Idle} idle, {Total} total",
            Name, context.AgentPool.IdleAgents, context.AgentPool.TotalAgents);
        await context.Scheduler.StartAsync(targetConcurrent, ct);

        var churnTimer = DateTime.UtcNow;
        var deadline = context.TestStartTime.AddSeconds(TestDurationSecs);

        try
        {
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);

                if ((DateTime.UtcNow - churnTimer).TotalSeconds < ChurnIntervalSecs)
                    continue;

                churnTimer = DateTime.UtcNow;

                var allAgents = context.AgentPool.Agents.ToList();
                if (allAgents.Count == 0) continue;

                // Pick agents in Idle state to unregister (avoid disrupting in-call agents)
                var idleAgents = allAgents
                    .Where(a => a.State == AgentState.Idle)
                    .OrderBy(_ => Rng.Next())
                    .Take(ChurnAgentsPerCycle)
                    .ToList();

                var unregisteredIds = new List<string>();

                foreach (var agent in idleAgents)
                {
                    try
                    {
                        await agent.UnregisterAsync();
                        unregisteredIds.Add(agent.ExtensionId);
                        logger.LogDebug("[{Scenario}] Unregistered agent {Ext}", Name, agent.ExtensionId);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "[{Scenario}] Failed to unregister agent {Ext}", Name, agent.ExtensionId);
                    }
                }

                // Pick a different set of offline agents to re-register
                var offlineAgents = allAgents
                    .Where(a => a.State == AgentState.Offline && !unregisteredIds.Contains(a.ExtensionId))
                    .OrderBy(_ => Rng.Next())
                    .Take(ChurnAgentsPerCycle)
                    .ToList();

                foreach (var agent in offlineAgents)
                {
                    try
                    {
                        await agent.RegisterAsync(ct);
                        logger.LogDebug("[{Scenario}] Re-registered agent {Ext}", Name, agent.ExtensionId);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "[{Scenario}] Failed to re-register agent {Ext}", Name, agent.ExtensionId);
                    }
                }

                // Also re-register the agents we just unregistered
                foreach (var extensionId in unregisteredIds)
                {
                    var agent = context.AgentPool.GetAgent(extensionId);
                    if (agent is null) continue;

                    try
                    {
                        await agent.RegisterAsync(ct);
                        logger.LogDebug("[{Scenario}] Re-registered churned agent {Ext}", Name, extensionId);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "[{Scenario}] Failed to re-register churned agent {Ext}", Name, extensionId);
                    }
                }

                var stats = context.AgentPool.GetStats();
                logger.LogInformation(
                    "[{Scenario}] Churn cycle: Idle={Idle}, InCall={InCall}, Offline={Offline}, Error={Error}",
                    Name, stats.Idle, stats.InCall,
                    context.AgentPool.Agents.Count(a => a.State == AgentState.Offline),
                    stats.Error);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("[{Scenario}] Test cancelled", Name);
        }

        await context.Scheduler.StopAsync();
        context.TestEndTime = DateTime.UtcNow;

        logger.LogInformation(
            "[{Scenario}] Execution complete: TotalGenerated={Generated}",
            Name, context.Scheduler.TotalCallsGenerated);
    }

    public async Task<ValidationReport> ValidateAsync(TestContext context, CancellationToken ct)
    {
        var logger = context.LoggerFactory.CreateLogger<RapidReregisterScenario>();
        logger.LogInformation("[{Scenario}] Waiting {Delay}s for DB flush before validation", Name, DbFlushDelaySecs);
        await Task.Delay(TimeSpan.FromSeconds(DbFlushDelaySecs), ct);

        var results = new List<ValidationResult>();
        var sdkBugs = new List<string>();

        var allCdrs = await context.CdrReader.GetCallsForTestAsync(
            context.TestStartTime, context.TestEndTime, ct);

        int totalCalls = allCdrs.Count;
        logger.LogInformation("[{Scenario}] CDRs found: {Total}", Name, totalCalls);

        // CDRs should exist for connected calls despite the churn
        results.Add(new ValidationResult
        {
            CallId = "cdr-present",
            ValidatorName = nameof(RapidReregisterScenario),
            Passed = totalCalls > 0,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "CdrsPresentDuringChurn",
                    Passed = totalCalls > 0,
                    Expected = "CDRs written for connected calls during rapid re-register churn",
                    Actual = $"{totalCalls} CDR(s) found",
                    Message = totalCalls == 0 ? "No CDRs found — no calls connected during rapid re-register test" : null
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
                    ValidatorName = nameof(RapidReregisterScenario),
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

        // All agents should be in Idle or Offline state at end (no orphaned calls)
        var leakResult = LeakDetector.DetectAgentLeaks(context.AgentPool);
        results.Add(leakResult);

        if (!leakResult.Passed)
            sdkBugs.Add("Agent leak after rapid-reregister — agents stuck in unexpected state after churn");

        var poolStats = context.AgentPool.GetStats();
        logger.LogInformation(
            "[{Scenario}] Final pool: Total={Total}, Idle={Idle}, InCall={InCall}, Offline={Offline}, Error={Error}",
            Name,
            poolStats.Total,
            poolStats.Idle,
            poolStats.InCall,
            context.AgentPool.Agents.Count(a => a.State == AgentState.Offline),
            poolStats.Error);

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
