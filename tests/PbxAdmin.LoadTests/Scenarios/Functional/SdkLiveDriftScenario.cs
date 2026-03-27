using Microsoft.Extensions.Logging;
using PbxAdmin.LoadTests.Sdk;
using PbxAdmin.LoadTests.Validation;

namespace PbxAdmin.LoadTests.Scenarios.Functional;

/// <summary>
/// 2-minute sustained burst that validates AsteriskServer.Channels drift is less than
/// 5% compared to AMI ground truth ("core show channels count").
/// </summary>
public sealed class SdkLiveDriftScenario : ITestScenario
{
    public string Name => "sdk-live-drift";
    public string Description => "2-minute sustained burst validates AsteriskServer.Channels drift < 5% vs AMI ground truth";

    private const int BurstSize = 5;
    private const int BurstIntervalSeconds = 10;
    private const int ActiveMinutes = 2;
    private const int DrainSeconds = 90;
    private const int SamplingIntervalSeconds = 3;

    public async Task ExecuteAsync(TestContext context, CancellationToken ct)
    {
        var logger = context.LoggerFactory.CreateLogger<SdkLiveDriftScenario>();
        context.TestStartTime = DateTime.UtcNow;

        if (context.SdkRuntime is null)
        {
            logger.LogError("[{Scenario}] SDK runtime not available - cannot run", Name);
            throw new InvalidOperationException("SdkRuntime is required for this scenario");
        }

        if (context.LiveStateValidator is null)
        {
            logger.LogError("[{Scenario}] LiveStateValidator not available - cannot run", Name);
            throw new InvalidOperationException("LiveStateValidator is required for this scenario");
        }

        // Start live-state sampling at 3-second intervals
        await context.LiveStateValidator.StartAsync(
            context.SdkRuntime.Server,
            context.SdkRuntime.Connection,
            SamplingIntervalSeconds,
            ct);

        logger.LogInformation(
            "[{Scenario}] LiveStateValidator started (interval={Interval}s)", Name, SamplingIntervalSeconds);

        // Generate sustained burst: BurstSize calls every BurstIntervalSeconds for ActiveMinutes
        int totalBursts = ActiveMinutes * 60 / BurstIntervalSeconds;
        logger.LogInformation(
            "[{Scenario}] Generating {Bursts} bursts of {Size} calls over {Minutes} minutes",
            Name, totalBursts, BurstSize, ActiveMinutes);

        for (int burst = 0; burst < totalBursts; burst++)
        {
            ct.ThrowIfCancellationRequested();

            for (int i = 0; i < BurstSize; i++)
            {
                try
                {
                    var result = await context.CallGenerator.GenerateCallAsync("105", cancellationToken: ct);
                    context.EventCapture.RegisterCall(result.CallId, result.Caller.Number, result.Destination, result.Timestamp);
                    context.Metrics.RecordCallOriginated();

                    if (!result.Accepted)
                        logger.LogWarning("[{Scenario}] Call rejected in burst {Burst}: {Error}", Name, burst + 1, result.ErrorMessage);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "[{Scenario}] Call generation failed in burst {Burst}", Name, burst + 1);
                }
            }

            logger.LogDebug("[{Scenario}] Burst {N}/{Total} complete ({Samples} samples so far)",
                Name, burst + 1, totalBursts, context.LiveStateValidator.GetSamples().Count);

            if (burst < totalBursts - 1)
                await Task.Delay(TimeSpan.FromSeconds(BurstIntervalSeconds), ct);
        }

        // Drain period: wait for all calls to complete
        logger.LogInformation("[{Scenario}] Waiting {Drain}s for calls to drain", Name, DrainSeconds);
        await Task.Delay(TimeSpan.FromSeconds(DrainSeconds), ct);

        // Stop sampling
        await context.LiveStateValidator.StopAsync();
        logger.LogInformation(
            "[{Scenario}] LiveStateValidator stopped. Collected {Count} samples",
            Name, context.LiveStateValidator.GetSamples().Count);

        context.TestEndTime = DateTime.UtcNow;
    }

    public Task<ValidationReport> ValidateAsync(TestContext context, CancellationToken ct)
    {
        var results = new List<ValidationResult>();

        if (context.LiveStateValidator is null)
        {
            results.Add(new ValidationResult
            {
                CallId = "sdk-live-drift",
                ValidatorName = nameof(SdkLiveDriftScenario),
                Passed = false,
                Checks =
                [
                    new ValidationCheck
                    {
                        CheckName = "LiveStateValidatorAvailable",
                        Passed = false,
                        Message = "LiveStateValidator was not available - SDK runtime is required for this scenario"
                    }
                ]
            });

            return Task.FromResult(BuildReport(context, results));
        }

        var samples = context.LiveStateValidator.GetSamples();
        var summary = context.LiveStateValidator.GetSummary();

        // Check 1: Sufficient samples collected
        const int minSamples = 30;
        bool sufficientSamples = samples.Count >= minSamples;
        results.Add(new ValidationResult
        {
            CallId = "drift-aggregate",
            ValidatorName = nameof(SdkLiveDriftScenario),
            Passed = sufficientSamples,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "SufficientSamples",
                    Passed = sufficientSamples,
                    Expected = $">= {minSamples}",
                    Actual = samples.Count.ToString(),
                    Message = sufficientSamples ? null : $"Expected at least {minSamples} samples but collected {samples.Count}"
                }
            ]
        });

        // Check 2: Channel drift rate below threshold
        bool driftRateOk = summary.DriftRate < 5.0;
        results.Add(new ValidationResult
        {
            CallId = "drift-aggregate",
            ValidatorName = nameof(SdkLiveDriftScenario),
            Passed = driftRateOk,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "ChannelDriftRate",
                    Passed = driftRateOk,
                    Expected = "< 5%",
                    Actual = $"{summary.DriftRate:F1}%",
                    Message = driftRateOk ? null : $"Drift rate {summary.DriftRate:F1}% exceeds 5% threshold ({summary.SamplesWithinTolerance}/{summary.TotalSamples} within tolerance)"
                }
            ]
        });

        // Check 3: Max channel drift within bounds
        const int maxAllowedDrift = 4;
        bool maxDriftOk = summary.MaxDrift <= maxAllowedDrift;
        results.Add(new ValidationResult
        {
            CallId = "drift-aggregate",
            ValidatorName = nameof(SdkLiveDriftScenario),
            Passed = maxDriftOk,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "MaxChannelDrift",
                    Passed = maxDriftOk,
                    Expected = $"<= {maxAllowedDrift}",
                    Actual = summary.MaxDrift.ToString(),
                    Message = maxDriftOk ? null : $"Max drift of {summary.MaxDrift} exceeds allowed maximum of {maxAllowedDrift}"
                }
            ]
        });

        // Check 4: Peak channel count was non-zero at some point (calls were actually running)
        int peakSdkChannels = samples.Count > 0 ? samples.Max(s => s.SdkChannelCount) : 0;
        bool hadActivity = peakSdkChannels > 0;
        results.Add(new ValidationResult
        {
            CallId = "drift-aggregate",
            ValidatorName = nameof(SdkLiveDriftScenario),
            Passed = hadActivity,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "PeakChannelCount",
                    Passed = hadActivity,
                    Expected = "> 0",
                    Actual = peakSdkChannels.ToString(),
                    Message = hadActivity ? null : "SDK never reported any active channels during the test - calls may not have been tracked"
                }
            ]
        });

        // Check 5: Drain to zero - last 3 samples should show 0 SDK channels
        bool drainedToZero;
        if (samples.Count >= 3)
        {
            var lastThree = samples
                .OrderBy(s => s.Timestamp)
                .TakeLast(3)
                .ToList();
            drainedToZero = lastThree.All(s => s.SdkChannelCount == 0 && s.AsteriskChannelCount == 0);
        }
        else
        {
            drainedToZero = false;
        }

        results.Add(new ValidationResult
        {
            CallId = "drift-aggregate",
            ValidatorName = nameof(SdkLiveDriftScenario),
            Passed = drainedToZero,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "DrainToZero",
                    Passed = drainedToZero,
                    Expected = "Last 3 samples show 0 channels",
                    Actual = samples.Count >= 3
                        ? string.Join(", ", samples.OrderBy(s => s.Timestamp).TakeLast(3).Select(s => $"SDK={s.SdkChannelCount}/AST={s.AsteriskChannelCount}"))
                        : $"Only {samples.Count} samples available",
                    Message = drainedToZero ? null : "SDK did not drain to 0 channels after test - possible channel leak or insufficient drain time"
                }
            ]
        });

        return Task.FromResult(BuildReport(context, results));
    }

    private static ValidationReport BuildReport(TestContext context, List<ValidationResult> results) => new()
    {
        TestStart = context.TestStartTime,
        TestEnd = context.TestEndTime,
        Duration = context.TestEndTime - context.TestStartTime,
        TotalCalls = 0, // Drift scenario does not track individual call validation
        TotalChecks = results.SelectMany(r => r.Checks).Count(),
        PassedChecks = results.SelectMany(r => r.Checks).Count(c => c.Passed),
        FailedChecks = results.SelectMany(r => r.Checks).Count(c => !c.Passed),
        Results = results
    };
}
