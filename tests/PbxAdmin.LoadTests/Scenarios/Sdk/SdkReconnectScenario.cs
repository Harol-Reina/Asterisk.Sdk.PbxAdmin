using Asterisk.Sdk.Ami.Actions;
using Asterisk.Sdk.Ami.Responses;
using Microsoft.Extensions.Logging;
using PbxAdmin.LoadTests.Sdk;
using PbxAdmin.LoadTests.Validation;

namespace PbxAdmin.LoadTests.Scenarios.Sdk;

/// <summary>
/// Level 2 functional scenario. Fires 3 AMI disconnects at t=30s, t=90s, and t=150s
/// while the scheduler runs sustained load throughout. Validates that the SDK reconnects
/// within 10 seconds after each forced disconnect and that sessions continue to accumulate
/// post-reconnect.
/// </summary>
public sealed class SdkReconnectScenario : SdkScenarioBase
{
    private const string QueueName = "loadtest";
    private const int DefaultMaxConcurrent = 15;

    public override string Name => "sdk-reconnect";
    public override string Description => "3 AMI disconnects under active load — validates reconnection < 10s and state recovery";
    public override string Level => "functional";

    // Stored between Execute and Validate
    private readonly List<(int DisconnectNumber, bool Reconnected, int ElapsedMs)> _reconnections = [];
    private int _preDisconnectSessionCount;

    public override async Task ExecuteAsync(TestContext context, CancellationToken ct)
    {
        var logger = context.LoggerFactory.CreateLogger<SdkReconnectScenario>();

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

        context.TestStartTime = DateTime.UtcNow;
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

        logger.LogInformation("[{Scenario}] Scheduler running — waiting 30s for load to stabilise", Name);

        // ── Wait 30s for load to stabilise ───────────────────────────────────
        await Task.Delay(TimeSpan.FromSeconds(30), ct);

        // Snapshot session count before any disconnect
        _preDisconnectSessionCount = context.SessionCapture?.CompletedSessionCount ?? 0;

        logger.LogInformation(
            "[{Scenario}] Stabilisation complete. PreDisconnectSessionCount={Count}",
            Name, _preDisconnectSessionCount);

        // ── Disconnect 1 at t≈30s ────────────────────────────────────────────
        logger.LogInformation("[{Scenario}] Disconnect 1: forcing AMI disconnect", Name);
        await ForceAmiDisconnectAsync(connection, logger, ct);
        var (r1, e1) = await WaitForReconnectAsync(connection, logger, ct);
        _reconnections.Add((1, r1, e1));
        logger.LogInformation(
            "[{Scenario}] Disconnect 1 result: Reconnected={R}, ElapsedMs={E}",
            Name, r1, e1);

        // ── Wait until t≈90s, then Disconnect 2 ─────────────────────────────
        var elapsed = (DateTime.UtcNow - context.TestStartTime).TotalMilliseconds;
        var waitTo90 = TimeSpan.FromSeconds(90) - TimeSpan.FromMilliseconds(elapsed);
        if (waitTo90 > TimeSpan.Zero)
        {
            logger.LogInformation(
                "[{Scenario}] Waiting {Wait:F0}ms to reach t=90s mark",
                Name, waitTo90.TotalMilliseconds);
            await Task.Delay(waitTo90, ct);
        }

        logger.LogInformation("[{Scenario}] Disconnect 2: forcing AMI disconnect", Name);
        await ForceAmiDisconnectAsync(connection, logger, ct);
        var (r2, e2) = await WaitForReconnectAsync(connection, logger, ct);
        _reconnections.Add((2, r2, e2));
        logger.LogInformation(
            "[{Scenario}] Disconnect 2 result: Reconnected={R}, ElapsedMs={E}",
            Name, r2, e2);

        // ── Wait until t≈150s, then Disconnect 3 ────────────────────────────
        elapsed = (DateTime.UtcNow - context.TestStartTime).TotalMilliseconds;
        var waitTo150 = TimeSpan.FromSeconds(150) - TimeSpan.FromMilliseconds(elapsed);
        if (waitTo150 > TimeSpan.Zero)
        {
            logger.LogInformation(
                "[{Scenario}] Waiting {Wait:F0}ms to reach t=150s mark",
                Name, waitTo150.TotalMilliseconds);
            await Task.Delay(waitTo150, ct);
        }

        logger.LogInformation("[{Scenario}] Disconnect 3: forcing AMI disconnect", Name);
        await ForceAmiDisconnectAsync(connection, logger, ct);
        var (r3, e3) = await WaitForReconnectAsync(connection, logger, ct);
        _reconnections.Add((3, r3, e3));
        logger.LogInformation(
            "[{Scenario}] Disconnect 3 result: Reconnected={R}, ElapsedMs={E}",
            Name, r3, e3);

        // ── Drain phase ──────────────────────────────────────────────────────
        context.Metrics.EnterDrainPhase();
        await context.Scheduler.StopAsync();

        logger.LogInformation("[{Scenario}] Scheduler stopped — draining active channels", Name);

        await WaitForDrainAsync(connection, logger, ct, timeoutMs: 60_000);

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

        if (context.SdkRuntime is null)
        {
            results.Add(new ValidationResult
            {
                CallId = "reconnect-aggregate",
                ValidatorName = nameof(SdkReconnectScenario),
                Passed = false,
                Checks =
                [
                    new ValidationCheck
                    {
                        CheckName = "InfrastructureAvailable",
                        Passed = false,
                        Message = "SdkRuntime is required for this scenario"
                    }
                ]
            });
            return BuildReport(context, results);
        }

        var sessions = context.SessionCapture is not null
            ? context.SessionCapture.GetCompletedSessions()
            : (IReadOnlyList<CallSessionSnapshot>)[];

        // ── Checks 1–6: per-disconnect Success + Under10s ────────────────────
        foreach (var (disconnectNumber, reconnected, elapsedMs) in _reconnections)
        {
            string successKey = $"Reconnect{disconnectNumber}_Success";
            string speedKey   = $"Reconnect{disconnectNumber}_Under10s";
            bool speedOk      = elapsedMs < 10_000;

            results.Add(new ValidationResult
            {
                CallId = "reconnect-aggregate",
                ValidatorName = nameof(SdkReconnectScenario),
                Passed = reconnected,
                Checks =
                [
                    new ValidationCheck
                    {
                        CheckName = successKey,
                        Passed = reconnected,
                        Expected = "true",
                        Actual = reconnected.ToString(),
                        Message = reconnected
                            ? null
                            : $"SDK did not reconnect after disconnect {disconnectNumber} within the polling window"
                    }
                ]
            });

            results.Add(new ValidationResult
            {
                CallId = "reconnect-aggregate",
                ValidatorName = nameof(SdkReconnectScenario),
                Passed = speedOk,
                Checks =
                [
                    new ValidationCheck
                    {
                        CheckName = speedKey,
                        Passed = speedOk,
                        Expected = "< 10000ms",
                        Actual = $"{elapsedMs}ms",
                        Message = speedOk
                            ? null
                            : $"Reconnect {disconnectNumber} took {elapsedMs}ms, exceeding the 10s threshold"
                    }
                ]
            });
        }

        // ── Check 7: PostReconnectSessionsExist ──────────────────────────────
        int postReconnectNew = sessions.Count - _preDisconnectSessionCount;
        bool postReconnectOk = postReconnectNew > 0;
        results.Add(new ValidationResult
        {
            CallId = "reconnect-aggregate",
            ValidatorName = nameof(SdkReconnectScenario),
            Passed = postReconnectOk,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "PostReconnectSessionsExist",
                    Passed = postReconnectOk,
                    Expected = "> 0 new sessions after disconnect 1",
                    Actual = postReconnectNew.ToString(),
                    Message = postReconnectOk
                        ? null
                        : $"No new sessions captured after reconnect (pre={_preDisconnectSessionCount}, total={sessions.Count})"
                }
            ]
        });

        // ── Check 8: TotalSessionCount >= 5 ──────────────────────────────────
        bool totalSessionOk = sessions.Count >= 5;
        results.Add(new ValidationResult
        {
            CallId = "reconnect-aggregate",
            ValidatorName = nameof(SdkReconnectScenario),
            Passed = totalSessionOk,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "TotalSessionCount",
                    Passed = totalSessionOk,
                    Expected = ">= 5",
                    Actual = sessions.Count.ToString(),
                    Message = totalSessionOk
                        ? null
                        : $"Expected at least 5 total sessions under 3-disconnect load but captured {sessions.Count}"
                }
            ]
        });

        // ── Check 9: ConnectionAlive ──────────────────────────────────────────
        bool connectionAlive;
        try
        {
            await context.SdkRuntime.Connection.SendActionAsync<CommandResponse>(
                new CommandAction { Command = "core show version" }, ct);
            connectionAlive = true;
        }
        catch
        {
            connectionAlive = false;
        }

        results.Add(new ValidationResult
        {
            CallId = "reconnect-aggregate",
            ValidatorName = nameof(SdkReconnectScenario),
            Passed = connectionAlive,
            Checks =
            [
                new ValidationCheck
                {
                    CheckName = "ConnectionAlive",
                    Passed = connectionAlive,
                    Expected = "true",
                    Actual = connectionAlive.ToString(),
                    Message = connectionAlive
                        ? null
                        : "AMI connection is not alive after completing all 3 disconnect cycles"
                }
            ]
        });

        // ── Check 10: Leak detection ──────────────────────────────────────────
        var leakResults = await DetectLeaksAsync(context, ct);
        results.AddRange(leakResults);

        return BuildReport(context, results);
    }
}
