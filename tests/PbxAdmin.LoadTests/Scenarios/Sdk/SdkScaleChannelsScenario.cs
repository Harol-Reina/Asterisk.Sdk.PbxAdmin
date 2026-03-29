using Microsoft.Extensions.Logging;
using PbxAdmin.LoadTests.Validation;

namespace PbxAdmin.LoadTests.Scenarios.Sdk;

/// <summary>
/// Level 3 scale test. Runs 200 agents, ramps to 300 concurrent calls in 1 minute,
/// sustains for 4 minutes, then validates that SDK channel tracking drift stays
/// below 2% relative to AMI ground truth at scale.
/// </summary>
public sealed class SdkScaleChannelsScenario : SdkScenarioBase
{
    private const string QueueName = "loadtest";
    private const int DefaultMaxConcurrent = 300;
    private const int DefaultDurationMinutes = 5;

    public override string Name => "sdk-scale-channels";
    public override string Level => "scale";
    public override string Description => "200 agents, 300 concurrent — channel tracking drift < 2% at scale";

    public override async Task ExecuteAsync(TestContext context, CancellationToken ct)
    {
        var logger = context.LoggerFactory.CreateLogger<SdkScaleChannelsScenario>();
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

        // ── Start scheduler (ramp-up driven by CallPatternOptions.RampUpMinutes) ─
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
                ValidatorName = nameof(SdkScaleChannelsScenario),
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

        // ── Check 1: DriftRateAvg < 2.0% ────────────────────────────────────
        bool driftRateOk = summary.DriftRate < 2.0;
        results.Add(new ValidationResult
        {
            CallId = "aggregate",
            ValidatorName = nameof(SdkScaleChannelsScenario),
            Passed = driftRateOk,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "DriftRateAvg",
                    Passed = driftRateOk,
                    Expected = "< 2.0%",
                    Actual = $"{summary.DriftRate:F2}%",
                    Message = driftRateOk
                        ? null
                        : $"Channel drift rate {summary.DriftRate:F2}% exceeds the 2% threshold at scale"
                }
            ]
        });

        // ── Check 2: DriftMaxAbsolute <= 6 ───────────────────────────────────
        bool maxDriftOk = summary.MaxDrift <= 6;
        results.Add(new ValidationResult
        {
            CallId = "aggregate",
            ValidatorName = nameof(SdkScaleChannelsScenario),
            Passed = maxDriftOk,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "DriftMaxAbsolute",
                    Passed = maxDriftOk,
                    Expected = "<= 6",
                    Actual = summary.MaxDrift.ToString(),
                    Message = maxDriftOk
                        ? null
                        : $"Max channel drift of {summary.MaxDrift} exceeds the absolute threshold of 6 at scale"
                }
            ]
        });

        // ── Check 3: NoPhantomChannels (SDK > Asterisk + 2) ──────────────────
        int phantomCount = samples.Count(s => s.SdkChannelCount > s.AsteriskChannelCount + 2);
        bool noPhantoms = phantomCount == 0;
        results.Add(new ValidationResult
        {
            CallId = "aggregate",
            ValidatorName = nameof(SdkScaleChannelsScenario),
            Passed = noPhantoms,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "NoPhantomChannels",
                    Passed = noPhantoms,
                    Expected = "0 samples with SDK > Asterisk + 2",
                    Actual = phantomCount.ToString(),
                    Message = noPhantoms
                        ? null
                        : $"{phantomCount} sample(s) where SDK reported more channels than Asterisk by > 2 (phantom channels)"
                }
            ]
        });

        // ── Check 4: NoInvisibleChannels (Asterisk > SDK + 2) ────────────────
        int invisibleCount = samples.Count(s => s.AsteriskChannelCount > s.SdkChannelCount + 2);
        bool noInvisible = invisibleCount == 0;
        results.Add(new ValidationResult
        {
            CallId = "aggregate",
            ValidatorName = nameof(SdkScaleChannelsScenario),
            Passed = noInvisible,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "NoInvisibleChannels",
                    Passed = noInvisible,
                    Expected = "0 samples with Asterisk > SDK + 2",
                    Actual = invisibleCount.ToString(),
                    Message = noInvisible
                        ? null
                        : $"{invisibleCount} sample(s) where Asterisk reported more channels than SDK by > 2 (invisible channels)"
                }
            ]
        });

        // ── Check 5: PeakConcurrent >= 250 ───────────────────────────────────
        int peak = context.Metrics.PeakConcurrentCalls;
        bool peakOk = peak >= 250;
        results.Add(new ValidationResult
        {
            CallId = "aggregate",
            ValidatorName = nameof(SdkScaleChannelsScenario),
            Passed = peakOk,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "PeakConcurrent",
                    Passed = peakOk,
                    Expected = ">= 250",
                    Actual = peak.ToString(),
                    Message = peakOk
                        ? null
                        : $"Peak concurrent calls {peak} did not reach the 250 minimum — scale target not achieved"
                }
            ]
        });

        // ── Check 6: Leak detection ───────────────────────────────────────────
        var leakResults = await DetectLeaksAsync(context, ct);
        results.AddRange(leakResults);

        return BuildReport(context, results);
    }
}
