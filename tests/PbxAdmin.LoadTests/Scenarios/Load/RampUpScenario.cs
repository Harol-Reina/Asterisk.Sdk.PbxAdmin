using Microsoft.Extensions.Logging;
using PbxAdmin.LoadTests.Validation;
using PbxAdmin.LoadTests.Validation.Layer3;

namespace PbxAdmin.LoadTests.Scenarios.Load;

/// <summary>
/// Linearly increases concurrent calls from 0 to target over the configured ramp-up period,
/// measuring SDK degradation under increasing load.
/// </summary>
public sealed class RampUpScenario : ITestScenario
{
    private const string DefaultQueueName = "ventas";
    private const int SlaSThreshold = 30;
    private const int SampleLimit = 50;
    private const int DbFlushDelaySecs = 5;
    private const int ProgressIntervalSecs = 30;

    public string Name => "ramp-up";
    public string Description => "Linearly increases concurrent calls from 0 to target over ramp-up period, measuring SDK degradation";

    public async Task ExecuteAsync(TestContext context, CancellationToken ct)
    {
        var logger = context.LoggerFactory.CreateLogger<RampUpScenario>();
        context.TestStartTime = DateTime.UtcNow;

        logger.LogInformation(
            "[{Scenario}] Starting: target={Target}, rampUp={RampUp}m, duration={Duration}m",
            Name,
            context.CallPattern.MaxConcurrentCalls,
            context.CallPattern.RampUpMinutes,
            context.CallPattern.TestDurationMinutes);

        await context.AgentPool.StartAsync(context.AgentBehavior.AgentCount, ct);

        context.Scheduler.StatsUpdated += stats =>
            logger.LogInformation(
                "[{Scenario}] Stats: Active={Active}/{Target}, Generated={Generated}, Elapsed={Elapsed:mm\\:ss}, Remaining={Remaining:mm\\:ss}",
                Name,
                stats.ActiveCalls,
                stats.TargetConcurrent,
                stats.TotalGenerated,
                stats.Elapsed,
                stats.Remaining);

        await context.Scheduler.StartAsync(context.CallPattern.MaxConcurrentCalls, ct);

        var progressTimer = DateTime.UtcNow;

        try
        {
            var testDuration = TimeSpan.FromMinutes(context.CallPattern.TestDurationMinutes);
            var deadline = context.TestStartTime + testDuration;

            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);

                if ((DateTime.UtcNow - progressTimer).TotalSeconds >= ProgressIntervalSecs)
                {
                    var poolStats = context.AgentPool.GetStats();
                    logger.LogInformation(
                        "[{Scenario}] Progress: Idle={Idle}, InCall={InCall}, Calls={Calls}",
                        Name,
                        poolStats.Idle,
                        poolStats.InCall,
                        poolStats.TotalCallsHandled);
                    progressTimer = DateTime.UtcNow;
                }
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("[{Scenario}] Test cancelled", Name);
        }

        await context.Scheduler.StopAsync();

        // Drain: wait for all active calls to finish naturally
        int drainMaxSecs = context.AgentBehavior.TalkTimeSecs + context.AgentBehavior.WrapupTimeSecs + 10;
        logger.LogInformation("[{Scenario}] Draining active calls (max {Secs}s)...", Name, drainMaxSecs);

        var drainDeadline = DateTime.UtcNow.AddSeconds(drainMaxSecs);
        while (DateTime.UtcNow < drainDeadline && !ct.IsCancellationRequested)
        {
            int active = context.AgentPool.InCallAgents + context.AgentPool.RingingAgents;
            if (active == 0) break;
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        context.TestEndTime = DateTime.UtcNow;

        logger.LogInformation(
            "[{Scenario}] Execution complete: TotalGenerated={Generated}",
            Name,
            context.Scheduler.TotalCallsGenerated);
    }

    public async Task<ValidationReport> ValidateAsync(TestContext context, CancellationToken ct)
    {
        var logger = context.LoggerFactory.CreateLogger<RampUpScenario>();
        logger.LogInformation("[{Scenario}] Waiting {Delay}s for DB flush before validation", Name, DbFlushDelaySecs);
        await Task.Delay(TimeSpan.FromSeconds(DbFlushDelaySecs), ct);

        var results = new List<ValidationResult>();
        var sdkBugs = new List<string>();

        // Read all CDRs in test time range
        var allCdrs = await context.CdrReader.GetCallsForTestAsync(
            context.TestStartTime, context.TestEndTime, ct);

        int totalCalls = allCdrs.Count;
        int answeredCalls = allCdrs.Count(c =>
            string.Equals(c.Disposition, "ANSWERED", StringComparison.OrdinalIgnoreCase));

        logger.LogInformation(
            "[{Scenario}] CDRs: total={Total}, answered={Answered}",
            Name, totalCalls, answeredCalls);

        // Sample up to SampleLimit CDRs for 3-layer validation
        var snapshots = context.EventCapture.GetAllSnapshots();
        var sample = snapshots.Take(SampleLimit).ToList();

        logger.LogInformation(
            "[{Scenario}] Validating {Sample} of {Total} call snapshots",
            Name, sample.Count, snapshots.Count);

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
                    ValidatorName = nameof(RampUpScenario),
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

        // Queue SLA check
        try
        {
            var sla = await context.QueueLogReader.GetQueueSlaAsync(
                DefaultQueueName, context.TestStartTime, context.TestEndTime, SlaSThreshold, ct);

            logger.LogInformation(
                "[{Scenario}] Queue SLA: queue={Queue}, offered={Offered}, answered={Answered}, SLA%={Sla:F1}",
                Name, sla.QueueName, sla.Offered, sla.Answered, sla.SlaPercent);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[{Scenario}] Queue SLA check failed", Name);
        }

        // Agent leak detection
        var leakResult = LeakDetector.DetectAgentLeaks(context.AgentPool);
        results.Add(leakResult);

        if (!leakResult.Passed)
            sdkBugs.Add("Agent leak detected after ramp-up test");

        // Answer rate check
        if (totalCalls > 0)
        {
            double answerRate = (double)answeredCalls / totalCalls * 100;
            logger.LogInformation("[{Scenario}] Answer rate: {Rate:F1}%", Name, answerRate);

            if (answerRate < 80)
                sdkBugs.Add($"Low answer rate under load: {answerRate:F1}% (expected ≥80%)");
        }

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
