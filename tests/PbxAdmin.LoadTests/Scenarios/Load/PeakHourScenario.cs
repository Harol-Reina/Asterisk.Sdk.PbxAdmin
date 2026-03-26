using Microsoft.Extensions.Logging;
using PbxAdmin.LoadTests.Validation;
using PbxAdmin.LoadTests.Validation.Layer3;

namespace PbxAdmin.LoadTests.Scenarios.Load;

/// <summary>
/// Simulates Colombian office peak hours with realistic call patterns and scenario mix.
/// Uses the ScenarioMix distribution from CallPatternOptions (60% normal, 10% short, etc.)
/// and validates that short vs long call durations are reflected in CDR data.
/// </summary>
public sealed class PeakHourScenario : ITestScenario
{
    private const string DefaultQueueName = "ventas";
    private const int SlaSThreshold = 30;
    private const int SampleLimit = 50;
    private const int DbFlushDelaySecs = 5;
    private const int ProgressIntervalSecs = 30;
    private const int ShortCallMaxSecs = 60;

    public string Name => "peak-hour";
    public string Description => "Simulates Colombian office peak hours with realistic call patterns and scenario mix";

    public async Task ExecuteAsync(TestContext context, CancellationToken ct)
    {
        var logger = context.LoggerFactory.CreateLogger<PeakHourScenario>();
        context.TestStartTime = DateTime.UtcNow;

        logger.LogInformation(
            "[{Scenario}] Starting: target={Target}, rampUp={RampUp}m, duration={Duration}m",
            Name,
            context.CallPattern.MaxConcurrentCalls,
            context.CallPattern.RampUpMinutes,
            context.CallPattern.TestDurationMinutes);

        logger.LogInformation(
            "[{Scenario}] Scenario mix: {Mix}",
            Name,
            string.Join(", ", context.CallPattern.ScenarioMix.Select(kv => $"{kv.Key}={kv.Value}%")));

        await context.AgentPool.StartAsync(context.AgentBehavior.AgentCount, ct);

        context.Scheduler.StatsUpdated += stats =>
        {
            var scenarioCounts = string.Join(", ", stats.ScenarioCounts
                .Where(kv => kv.Value > 0)
                .Select(kv => $"{kv.Key}={kv.Value}"));

            logger.LogInformation(
                "[{Scenario}] Stats: Active={Active}/{Target}, Generated={Generated}, Elapsed={Elapsed:mm\\:ss} | {Scenarios}",
                Name,
                stats.ActiveCalls,
                stats.TargetConcurrent,
                stats.TotalGenerated,
                stats.Elapsed,
                scenarioCounts);
        };

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
                        "[{Scenario}] Pool: Idle={Idle}, InCall={InCall}, Wrapup={Wrapup}, TotalHandled={Handled}",
                        Name,
                        poolStats.Idle,
                        poolStats.InCall,
                        poolStats.Wrapup,
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
        var logger = context.LoggerFactory.CreateLogger<PeakHourScenario>();
        logger.LogInformation("[{Scenario}] Waiting {Delay}s for DB flush before validation", Name, DbFlushDelaySecs);
        await Task.Delay(TimeSpan.FromSeconds(DbFlushDelaySecs), ct);

        var results = new List<ValidationResult>();
        var sdkBugs = new List<string>();

        var allCdrs = await context.CdrReader.GetCallsForTestAsync(
            context.TestStartTime, context.TestEndTime, ct);

        int totalCalls = allCdrs.Count;
        int answeredCalls = allCdrs.Count(c =>
            string.Equals(c.Disposition, "ANSWERED", StringComparison.OrdinalIgnoreCase));
        int shortCalls = allCdrs.Count(c => c.BillSec > 0 && c.BillSec <= ShortCallMaxSecs);
        int longCalls = allCdrs.Count(c => c.BillSec > ShortCallMaxSecs);

        logger.LogInformation(
            "[{Scenario}] CDRs: total={Total}, answered={Answered}, short={Short}, long={Long}",
            Name, totalCalls, answeredCalls, shortCalls, longCalls);

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
                    ValidatorName = nameof(PeakHourScenario),
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

            if (sla.Offered > 0 && sla.SlaPercent < 80)
                sdkBugs.Add($"Queue SLA below threshold during peak hours: {sla.SlaPercent:F1}% (expected ≥80%)");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[{Scenario}] Queue SLA check failed", Name);
        }

        // Verify ScenarioMix distribution: short vs long calls should appear in CDR data
        if (totalCalls >= 10)
        {
            bool hasShortCalls = shortCalls > 0;
            bool hasLongCalls = longCalls > 0;

            results.Add(new ValidationResult
            {
                CallId = "scenario-mix",
                ValidatorName = nameof(PeakHourScenario),
                Passed = hasShortCalls && hasLongCalls,
                Checks =
                [
                    new ValidationCheck
                    {
                        CheckName = "ShortCallsPresent",
                        Passed = hasShortCalls,
                        Expected = "Short calls (≤60s) present in CDRs",
                        Actual = $"{shortCalls} short calls found",
                        Message = hasShortCalls ? null : "No short calls found — ScenarioMix ShortCall weight may not be working"
                    },
                    new ValidationCheck
                    {
                        CheckName = "LongCallsPresent",
                        Passed = hasLongCalls,
                        Expected = "Long calls (>60s) present in CDRs",
                        Actual = $"{longCalls} long calls found",
                        Message = hasLongCalls ? null : "No long calls found — ScenarioMix LongCall weight may not be working"
                    }
                ]
            });
        }

        // Answer rate check
        if (totalCalls > 0)
        {
            double answerRate = (double)answeredCalls / totalCalls * 100;
            logger.LogInformation("[{Scenario}] Answer rate: {Rate:F1}%", Name, answerRate);

            if (answerRate < 80)
                sdkBugs.Add($"Low answer rate during peak hours: {answerRate:F1}% (expected ≥80%)");
        }

        // Agent leak detection
        var leakResult = LeakDetector.DetectAgentLeaks(context.AgentPool);
        results.Add(leakResult);

        if (!leakResult.Passed)
            sdkBugs.Add("Agent leak detected after peak-hour test");

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
