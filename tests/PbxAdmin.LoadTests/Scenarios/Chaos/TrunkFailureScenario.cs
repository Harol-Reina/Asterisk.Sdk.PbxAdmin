using Microsoft.Extensions.Logging;
using PbxAdmin.LoadTests.Validation;
using PbxAdmin.LoadTests.Validation.Layer3;

namespace PbxAdmin.LoadTests.Scenarios.Chaos;

/// <summary>
/// Stops the call generator mid-test to simulate a trunk failure, then restarts it.
/// Verifies that the SDK handles the traffic gap gracefully and CDRs are correct
/// both before and after the failure.
/// </summary>
public sealed class TrunkFailureScenario : ITestScenario
{
    private const int PreFailureSecs = 60;     // 1 minute of normal calls before failure
    private const int DrainSecs = 30;          // 30s for active calls to drain
    private const int PostRestartSecs = 60;    // 1 minute of calls after restart
    private const int DbFlushDelaySecs = 5;
    private const int SampleLimit = 50;

    public string Name => "trunk-failure";
    public string Description => "Stops PSTN emulator mid-test to verify SDK handles trunk failure gracefully";

    public async Task ExecuteAsync(TestContext context, CancellationToken ct)
    {
        var logger = context.LoggerFactory.CreateLogger<TrunkFailureScenario>();
        context.TestStartTime = DateTime.UtcNow;

        logger.LogInformation(
            "[{Scenario}] Starting: {PreSecs}s normal → trunk failure → {Drain}s drain → restart → {PostSecs}s normal",
            Name, PreFailureSecs, DrainSecs, PostRestartSecs);

        await context.AgentPool.StartAsync(context.AgentBehavior.AgentCount, ct);
        await context.Scheduler.StartAsync(context.CallPattern.MaxConcurrentCalls, ct);

        // Phase 1: normal operation
        logger.LogInformation("[{Scenario}] Phase 1: generating calls for {Secs}s", Name, PreFailureSecs);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(PreFailureSecs), ct);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("[{Scenario}] Cancelled during pre-failure phase", Name);
            goto Cleanup;
        }

        // Phase 2: trunk failure — stop the scheduler (no new calls)
        logger.LogInformation("[{Scenario}] Phase 2: trunk failure — stopping call generator", Name);
        await context.Scheduler.StopAsync();

        logger.LogInformation("[{Scenario}] Waiting {Secs}s for active calls to drain", Name, DrainSecs);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(DrainSecs), ct);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("[{Scenario}] Cancelled during drain phase", Name);
            goto Cleanup;
        }

        // Phase 3: restart the call generator
        logger.LogInformation("[{Scenario}] Phase 3: trunk restored — restarting call generator", Name);

        try
        {
            await context.Scheduler.StartAsync(context.CallPattern.MaxConcurrentCalls, ct);

            await Task.Delay(TimeSpan.FromSeconds(PostRestartSecs), ct);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("[{Scenario}] Cancelled during post-restart phase", Name);
        }

        await context.Scheduler.StopAsync();

        Cleanup:
        context.TestEndTime = DateTime.UtcNow;

        logger.LogInformation(
            "[{Scenario}] Execution complete: TotalGenerated={Generated}",
            Name, context.Scheduler.TotalCallsGenerated);
    }

    public async Task<ValidationReport> ValidateAsync(TestContext context, CancellationToken ct)
    {
        var logger = context.LoggerFactory.CreateLogger<TrunkFailureScenario>();
        logger.LogInformation("[{Scenario}] Waiting {Delay}s for DB flush before validation", Name, DbFlushDelaySecs);
        await Task.Delay(TimeSpan.FromSeconds(DbFlushDelaySecs), ct);

        var results = new List<ValidationResult>();
        var sdkBugs = new List<string>();

        var allCdrs = await context.CdrReader.GetCallsForTestAsync(
            context.TestStartTime, context.TestEndTime, ct);

        int totalCalls = allCdrs.Count;
        logger.LogInformation("[{Scenario}] CDRs found: {Total}", Name, totalCalls);

        // Split CDRs into pre-failure and post-restart windows for independent checks
        DateTime failureTime = context.TestStartTime.AddSeconds(PreFailureSecs);
        DateTime restartTime = failureTime.AddSeconds(DrainSecs);

        var preFailureCdrs = allCdrs.Where(c => c.CallDate < failureTime).ToList();
        var postRestartCdrs = allCdrs.Where(c => c.CallDate > restartTime).ToList();

        logger.LogInformation(
            "[{Scenario}] Pre-failure CDRs: {Pre}, Post-restart CDRs: {Post}",
            Name, preFailureCdrs.Count, postRestartCdrs.Count);

        // Pre-failure calls should have normal dispositions
        if (preFailureCdrs.Count > 0)
        {
            int preAnswered = preFailureCdrs.Count(c =>
                string.Equals(c.Disposition, "ANSWERED", StringComparison.OrdinalIgnoreCase));

            results.Add(new ValidationResult
            {
                CallId = "pre-failure",
                ValidatorName = nameof(TrunkFailureScenario),
                Passed = preAnswered > 0,
                Checks =
                [
                    new ValidationCheck
                    {
                        CheckName = "PreFailureCallsAnswered",
                        Passed = preAnswered > 0,
                        Expected = "Some answered calls before trunk failure",
                        Actual = $"{preAnswered}/{preFailureCdrs.Count} answered",
                        Message = preAnswered == 0 ? "No calls answered before trunk failure — pre-failure phase may not have worked" : null
                    }
                ]
            });
        }

        // Post-restart calls should also work normally
        if (postRestartCdrs.Count > 0)
        {
            int postAnswered = postRestartCdrs.Count(c =>
                string.Equals(c.Disposition, "ANSWERED", StringComparison.OrdinalIgnoreCase));

            results.Add(new ValidationResult
            {
                CallId = "post-restart",
                ValidatorName = nameof(TrunkFailureScenario),
                Passed = postAnswered > 0,
                Checks =
                [
                    new ValidationCheck
                    {
                        CheckName = "PostRestartCallsAnswered",
                        Passed = postAnswered > 0,
                        Expected = "Some answered calls after trunk restart",
                        Actual = $"{postAnswered}/{postRestartCdrs.Count} answered",
                        Message = postAnswered == 0
                            ? "No calls answered after trunk restart — SDK may not have recovered from trunk failure"
                            : null
                    }
                ]
            });

            if (postAnswered == 0)
                sdkBugs.Add("SDK did not recover after trunk failure: no answered calls post-restart");
        }

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
                    ValidatorName = nameof(TrunkFailureScenario),
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

        // No orphaned sessions
        var leakResult = LeakDetector.DetectAgentLeaks(context.AgentPool);
        results.Add(leakResult);

        if (!leakResult.Passed)
            sdkBugs.Add("Agent leak after trunk-failure scenario — channels not cleaned up during outage");

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
