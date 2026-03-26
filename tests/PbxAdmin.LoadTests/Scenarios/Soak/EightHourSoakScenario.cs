using Microsoft.Extensions.Logging;
using PbxAdmin.LoadTests.Validation;
using PbxAdmin.LoadTests.Validation.Layer3;

namespace PbxAdmin.LoadTests.Scenarios.Soak;

/// <summary>
/// Sustained moderate load (50% of MaxConcurrentCalls) for 8 hours to detect memory leaks
/// and resource exhaustion. Logs memory snapshots every 30 minutes.
/// </summary>
public sealed class EightHourSoakScenario : ITestScenario
{
    private const int SoakDurationMinutes = 480; // 8 hours
    private const double LoadFraction = 0.5;     // 50% of max concurrent calls
    private const string DefaultQueueName = "ventas";
    private const int SlaSThreshold = 30;
    private const int SampleLimit = 50;
    private const int DbFlushDelaySecs = 5;
    private const int SnapshotIntervalMinutes = 30;
    private const double MemoryLeakMultiplier = 2.0;

    // Populated during ExecuteAsync, consumed during ValidateAsync.
    private readonly List<(DateTime Time, long Bytes)> _memorySnapshots = [];

    public string Name => "eight-hour-soak";
    public string Description => "Sustained moderate load for 8 hours to detect memory leaks and resource exhaustion";

    public async Task ExecuteAsync(TestContext context, CancellationToken ct)
    {
        var logger = context.LoggerFactory.CreateLogger<EightHourSoakScenario>();
        context.TestStartTime = DateTime.UtcNow;

        // Use override duration if already long enough; otherwise force 480 minutes.
        int durationMinutes = context.CallPattern.TestDurationMinutes >= SoakDurationMinutes
            ? context.CallPattern.TestDurationMinutes
            : SoakDurationMinutes;

        int targetConcurrent = Math.Max(1, (int)(context.CallPattern.MaxConcurrentCalls * LoadFraction));

        logger.LogInformation(
            "[{Scenario}] Starting: target={Target} (50% of {Max}), duration={Duration}m",
            Name, targetConcurrent, context.CallPattern.MaxConcurrentCalls, durationMinutes);

        await context.AgentPool.StartAsync(context.AgentBehavior.AgentCount, ct);

        context.Scheduler.StatsUpdated += stats =>
            logger.LogInformation(
                "[{Scenario}] Stats: Active={Active}/{Target}, Generated={Generated}, Elapsed={Elapsed:hh\\:mm\\:ss}",
                Name, stats.ActiveCalls, stats.TargetConcurrent, stats.TotalGenerated, stats.Elapsed);

        await context.Scheduler.StartAsync(targetConcurrent, ct);

        long initialMemoryBytes = GC.GetTotalMemory(false);
        _memorySnapshots.Clear();
        _memorySnapshots.Add((DateTime.UtcNow, initialMemoryBytes));

        logger.LogInformation(
            "[{Scenario}] Initial memory: {MemMb:F1} MB", Name, initialMemoryBytes / 1_048_576.0);

        var snapshotTimer = DateTime.UtcNow;

        try
        {
            var testDuration = TimeSpan.FromMinutes(durationMinutes);
            var deadline = context.TestStartTime + testDuration;

            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(60), ct);

                if ((DateTime.UtcNow - snapshotTimer).TotalMinutes >= SnapshotIntervalMinutes)
                {
                    long currentMemory = GC.GetTotalMemory(false);
                    _memorySnapshots.Add((DateTime.UtcNow, currentMemory));

                    var poolStats = context.AgentPool.GetStats();
                    var elapsed = DateTime.UtcNow - context.TestStartTime;
                    var remaining = testDuration - elapsed;
                    if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;

                    logger.LogInformation(
                        "[{Scenario}] Snapshot at {Elapsed:hh\\:mm}: Memory={MemMb:F1}MB (+{DeltaMb:F1}MB), " +
                        "Active={Active}/{Target}, Pool: Idle={Idle}, InCall={InCall}, TotalHandled={Handled}, Remaining={Remaining:hh\\:mm}",
                        Name,
                        elapsed,
                        currentMemory / 1_048_576.0,
                        (currentMemory - initialMemoryBytes) / 1_048_576.0,
                        context.Scheduler.ActiveCalls,
                        context.Scheduler.TargetConcurrent,
                        poolStats.Idle,
                        poolStats.InCall,
                        poolStats.TotalCallsHandled,
                        remaining);

                    snapshotTimer = DateTime.UtcNow;
                }
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("[{Scenario}] Test cancelled", Name);
        }

        await context.Scheduler.StopAsync();
        context.TestEndTime = DateTime.UtcNow;

        long finalMemory = GC.GetTotalMemory(false);
        _memorySnapshots.Add((DateTime.UtcNow, finalMemory));

        logger.LogInformation(
            "[{Scenario}] Soak complete: TotalGenerated={Generated}, FinalMemory={MemMb:F1}MB, InitialMemory={InitMb:F1}MB",
            Name,
            context.Scheduler.TotalCallsGenerated,
            finalMemory / 1_048_576.0,
            initialMemoryBytes / 1_048_576.0);
    }

    public async Task<ValidationReport> ValidateAsync(TestContext context, CancellationToken ct)
    {
        var logger = context.LoggerFactory.CreateLogger<EightHourSoakScenario>();
        logger.LogInformation("[{Scenario}] Waiting {Delay}s for DB flush before validation", Name, DbFlushDelaySecs);
        await Task.Delay(TimeSpan.FromSeconds(DbFlushDelaySecs), ct);

        var results = new List<ValidationResult>();
        var sdkBugs = new List<string>();

        var allCdrs = await context.CdrReader.GetCallsForTestAsync(
            context.TestStartTime, context.TestEndTime, ct);

        int totalCalls = allCdrs.Count;
        int answeredCalls = allCdrs.Count(c =>
            string.Equals(c.Disposition, "ANSWERED", StringComparison.OrdinalIgnoreCase));

        logger.LogInformation(
            "[{Scenario}] CDRs: total={Total}, answered={Answered}", Name, totalCalls, answeredCalls);

        // Sample up to SampleLimit CDRs for 3-layer validation
        var snapshots = context.EventCapture.GetAllSnapshots();
        var sample = snapshots.Take(SampleLimit).ToList();

        logger.LogInformation(
            "[{Scenario}] Validating {Sample} of {Total} call snapshots", Name, sample.Count, snapshots.Count);

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
                    ValidatorName = nameof(EightHourSoakScenario),
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

        // Memory leak check: flag if final memory > 2x initial
        if (_memorySnapshots.Count >= 2)
        {
            long initialMem = _memorySnapshots[0].Bytes;
            long finalMem = _memorySnapshots[^1].Bytes;
            bool memoryLeakSuspect = initialMem > 0 && finalMem > initialMem * MemoryLeakMultiplier;

            results.Add(new ValidationResult
            {
                CallId = "memory-leak",
                ValidatorName = nameof(EightHourSoakScenario),
                Passed = !memoryLeakSuspect,
                Checks =
                [
                    new ValidationCheck
                    {
                        CheckName = "MemoryGrowth",
                        Passed = !memoryLeakSuspect,
                        Expected = $"Final memory < {MemoryLeakMultiplier}x initial ({initialMem / 1_048_576.0:F1} MB × {MemoryLeakMultiplier})",
                        Actual = $"Final={finalMem / 1_048_576.0:F1} MB, Initial={initialMem / 1_048_576.0:F1} MB",
                        Message = memoryLeakSuspect
                            ? $"Potential memory leak: memory grew from {initialMem / 1_048_576.0:F1}MB to {finalMem / 1_048_576.0:F1}MB (>{MemoryLeakMultiplier}x over soak duration)"
                            : null
                    }
                ]
            });

            if (memoryLeakSuspect)
                sdkBugs.Add($"Potential memory leak: {initialMem / 1_048_576.0:F1}MB → {finalMem / 1_048_576.0:F1}MB (>{MemoryLeakMultiplier}x growth over soak)");
        }

        // Agent leak detection
        var leakResult = LeakDetector.DetectAgentLeaks(context.AgentPool);
        results.Add(leakResult);

        if (!leakResult.Passed)
            sdkBugs.Add("Agent leak detected after 8-hour soak");

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
