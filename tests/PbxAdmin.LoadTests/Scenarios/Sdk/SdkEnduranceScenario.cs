using Microsoft.Extensions.Logging;
using PbxAdmin.LoadTests.Validation;

namespace PbxAdmin.LoadTests.Scenarios.Sdk;

/// <summary>
/// Level 4 endurance test. Sustains 300 concurrent calls for 30 minutes with a single
/// forced AMI disconnect at the midpoint (~15 min). Validates channel/queue/agent drift,
/// session accuracy, reconnect speed, answer rate, and the absence of resource leaks
/// across the full run.
/// </summary>
public sealed class SdkEnduranceScenario : SdkScenarioBase
{
    private const string QueueName = "loadtest";
    private const int DefaultMaxConcurrent = 300;
    private const int DefaultDurationMinutes = 30;
    private const int DrainReserveSeconds = 180;
    private const int MaxSessionSample = 100;

    public override string Name => "sdk-endurance";
    public override string Level => "endurance";
    public override string Description => "30-min combined: 300 concurrent, drift + sessions + reconnect + memory/CPU validation";

    // Stored between Execute and Validate
    private bool _reconnected;
    private int _reconnectElapsedMs;

    public override async Task ExecuteAsync(TestContext context, CancellationToken ct)
    {
        var logger = context.LoggerFactory.CreateLogger<SdkEnduranceScenario>();
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

        var connection = context.SdkRuntime.Connection;

        // ── Start LiveStateValidator ─────────────────────────────────────────
        await context.LiveStateValidator.StartAsync(
            context.SdkRuntime.Server,
            connection,
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
        context.Metrics.EnterSustainPhase();

        logger.LogInformation("[{Scenario}] Entered sustain phase", Name);

        // ── Determine total duration and midpoint ────────────────────────────
        int durationMinutes = context.CallPattern.TestDurationMinutes >= DefaultDurationMinutes
            ? context.CallPattern.TestDurationMinutes
            : DefaultDurationMinutes;

        var totalDuration = TimeSpan.FromMinutes(durationMinutes);
        var midpoint = totalDuration / 2;

        logger.LogInformation(
            "[{Scenario}] Running for {Duration}m, midpoint disconnect at {Midpoint}m",
            Name, durationMinutes, midpoint.TotalMinutes);

        // ── Sustain until midpoint ───────────────────────────────────────────
        await Task.Delay(midpoint, ct);

        logger.LogInformation("[{Scenario}] Midpoint reached — forcing AMI disconnect", Name);

        // ── Force AMI disconnect at midpoint ─────────────────────────────────
        await ForceAmiDisconnectAsync(connection, logger, ct);
        (_reconnected, _reconnectElapsedMs) = await WaitForReconnectAsync(connection, logger, ct);

        logger.LogInformation(
            "[{Scenario}] Midpoint disconnect result: Reconnected={Reconnected}, ElapsedMs={ElapsedMs}",
            Name, _reconnected, _reconnectElapsedMs);

        // ── Sustain remaining time minus drain reserve ───────────────────────
        var elapsed = DateTime.UtcNow - context.TestStartTime;
        var remaining = totalDuration - elapsed - TimeSpan.FromSeconds(DrainReserveSeconds);

        if (remaining > TimeSpan.Zero)
        {
            logger.LogInformation(
                "[{Scenario}] Sustaining remaining {Remaining:mm\\:ss} before drain reserve",
                Name, remaining);
            await Task.Delay(remaining, ct);
        }
        else
        {
            logger.LogWarning(
                "[{Scenario}] Remaining time after reconnect is non-positive ({Remaining:F0}ms) — skipping sustain tail",
                Name, remaining.TotalMilliseconds);
        }

        // ── Drain phase ──────────────────────────────────────────────────────
        context.Metrics.EnterDrainPhase();
        await context.Scheduler.StopAsync();

        logger.LogInformation("[{Scenario}] Scheduler stopped — draining active channels (timeout={Timeout}s)", Name, DrainReserveSeconds);

        await WaitForDrainAsync(connection, logger, ct, timeoutMs: DrainReserveSeconds * 1_000);

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
                ValidatorName = nameof(SdkEnduranceScenario),
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

        // ── Check 1: ChannelDriftRate < 2.0% ────────────────────────────────
        bool channelDriftOk = summary.DriftRate < 2.0;
        results.Add(new ValidationResult
        {
            CallId = "aggregate",
            ValidatorName = nameof(SdkEnduranceScenario),
            Passed = channelDriftOk,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "ChannelDriftRate",
                    Passed = channelDriftOk,
                    Expected = "< 2.0%",
                    Actual = $"{summary.DriftRate:F2}%",
                    Message = channelDriftOk
                        ? null
                        : $"Channel drift rate {summary.DriftRate:F2}% exceeds the 2% threshold over the 30-min endurance run"
                }
            ]
        });

        // ── Check 2: QueueDriftRate (QueueMemberDriftRate) < 2.0% ───────────
        bool queueDriftOk = summary.QueueMemberDriftRate < 2.0;
        results.Add(new ValidationResult
        {
            CallId = "aggregate",
            ValidatorName = nameof(SdkEnduranceScenario),
            Passed = queueDriftOk,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "QueueDriftRate",
                    Passed = queueDriftOk,
                    Expected = "< 2.0%",
                    Actual = $"{summary.QueueMemberDriftRate:F2}%",
                    Message = queueDriftOk
                        ? null
                        : $"Queue member drift rate {summary.QueueMemberDriftRate:F2}% exceeds the 2% threshold over the 30-min endurance run"
                }
            ]
        });

        // ── Check 3: AgentDriftRate < 2.0% ──────────────────────────────────
        bool agentDriftOk = summary.AgentDriftRate < 2.0;
        results.Add(new ValidationResult
        {
            CallId = "aggregate",
            ValidatorName = nameof(SdkEnduranceScenario),
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
                        : $"Agent drift rate {summary.AgentDriftRate:F2}% exceeds the 2% threshold over the 30-min endurance run"
                }
            ]
        });

        // ── Check 4: SessionCoverage >= 98% vs CDR (sample 100) ─────────────
        if (context.SessionCapture is not null)
        {
            var allSessions = context.SessionCapture.GetCompletedSessions();

            var sample = allSessions
                .Where(s => s.CallerNumber is not null)
                .Take(MaxSessionSample)
                .ToList();

            int cdrMatched = 0;
            foreach (var session in sample)
            {
                try
                {
                    var cdr = await context.CdrReader.GetCallBySrcAsync(
                        session.CallerNumber!, context.TestStartTime, ct);
                    if (cdr is not null)
                        cdrMatched++;
                }
                catch (Exception ex)
                {
                    results.Add(new ValidationResult
                    {
                        CallId = session.SessionId,
                        ValidatorName = nameof(SdkEnduranceScenario),
                        Passed = false,
                        Checks =
                        [
                            new ValidationCheck
                            {
                                CheckName = "CdrLookup",
                                Passed = false,
                                Message = $"CDR lookup threw for caller {session.CallerNumber}: {ex.Message}"
                            }
                        ]
                    });
                }
            }

            double coveragePct = sample.Count > 0
                ? (double)cdrMatched / sample.Count * 100.0
                : 100.0;
            bool coverageOk = coveragePct >= 98.0;
            results.Add(new ValidationResult
            {
                CallId = "aggregate",
                ValidatorName = nameof(SdkEnduranceScenario),
                Passed = coverageOk,
                Checks =
                [
                    new ValidationCheck
                    {
                        CheckName = "SessionCoverage",
                        Passed = coverageOk,
                        Expected = ">= 98%",
                        Actual = $"{coveragePct:F1}% ({cdrMatched}/{sample.Count})",
                        Message = coverageOk
                            ? null
                            : $"Session CDR coverage {coveragePct:F1}% ({cdrMatched}/{sample.Count}) is below the 98% threshold over the endurance run"
                    }
                ]
            });

            // ── Check 5: TotalSessions >= 100 ───────────────────────────────
            bool totalSessionsOk = allSessions.Count >= 100;
            results.Add(new ValidationResult
            {
                CallId = "aggregate",
                ValidatorName = nameof(SdkEnduranceScenario),
                Passed = totalSessionsOk,
                Checks =
                [
                    new ValidationCheck
                    {
                        CheckName = "TotalSessions",
                        Passed = totalSessionsOk,
                        Expected = ">= 100",
                        Actual = allSessions.Count.ToString(),
                        Message = totalSessionsOk
                            ? null
                            : $"Expected at least 100 captured sessions over the 30-min run but captured {allSessions.Count}"
                    }
                ]
            });
        }

        // ── Check 6: Reconnected == true ─────────────────────────────────────
        results.Add(new ValidationResult
        {
            CallId = "aggregate",
            ValidatorName = nameof(SdkEnduranceScenario),
            Passed = _reconnected,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "Reconnected",
                    Passed = _reconnected,
                    Expected = "true",
                    Actual = _reconnected.ToString(),
                    Message = _reconnected
                        ? null
                        : "SDK did not reconnect after the midpoint AMI disconnect within the polling window"
                }
            ]
        });

        // ── Check 7: ReconnectTime < 10000ms ────────────────────────────────
        bool reconnectSpeedOk = _reconnectElapsedMs < 10_000;
        results.Add(new ValidationResult
        {
            CallId = "aggregate",
            ValidatorName = nameof(SdkEnduranceScenario),
            Passed = reconnectSpeedOk,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "ReconnectTime",
                    Passed = reconnectSpeedOk,
                    Expected = "< 10000ms",
                    Actual = $"{_reconnectElapsedMs}ms",
                    Message = reconnectSpeedOk
                        ? null
                        : $"Midpoint reconnect took {_reconnectElapsedMs}ms, exceeding the 10s threshold"
                }
            ]
        });

        // ── Check 8: AnswerRate >= 95% ───────────────────────────────────────
        int originated = context.Metrics.CallsOriginated;
        int answered = context.Metrics.CallsAnswered;
        double answerRate = originated > 0
            ? (double)answered / originated * 100.0
            : 100.0;
        bool answerRateOk = answerRate >= 95.0;
        results.Add(new ValidationResult
        {
            CallId = "aggregate",
            ValidatorName = nameof(SdkEnduranceScenario),
            Passed = answerRateOk,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "AnswerRate",
                    Passed = answerRateOk,
                    Expected = ">= 95%",
                    Actual = $"{answerRate:F1}% ({answered}/{originated})",
                    Message = answerRateOk
                        ? null
                        : $"Answer rate {answerRate:F1}% ({answered}/{originated}) is below the 95% threshold over the 30-min endurance run"
                }
            ]
        });

        // ── Check 9: Leak detection ───────────────────────────────────────────
        var leakResults = await DetectLeaksAsync(context, ct);
        results.AddRange(leakResults);

        return BuildReport(context, results);
    }
}
